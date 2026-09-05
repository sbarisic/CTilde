"""Run non-destructive serial acceptance against an already installed test image.

This runner does not flash, format, configure networking, or write user files.
Install matching firmware and fixtures with a separate storage-preserving procedure.
Requires pyserial. Raw serial evidence and an incremental JSON report stay local.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import statistics
import time

import serial


ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
ALLOCATOR = "/storage/modules/tests.allocator.ctm"
OVERLAY = "/storage/modules/tests.overlay.ctm"
MEMORY = "/storage/modules/memory.ctm"


def clean(data):
    return ANSI.sub("", data.decode("utf-8", errors="replace")).replace("\r", "")


class Shell:
    def __init__(self, port, transcript):
        self.port = serial.Serial(port=None, baudrate=115200, timeout=0.1)
        self.port.dtr = False
        self.port.rts = False
        self.port.port = port
        self.port.open()
        self.transcript = transcript
        self.owned_processes = set()

    def command(self, command, timeout=30):
        self.port.reset_input_buffer()
        self.port.write((command + "\r").encode())
        data = bytearray()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            block = self.port.read(max(1, self.port.in_waiting))
            data.extend(block)
            self.transcript.write(block)
            self.transcript.flush()
            text = clean(data)
            if "Guru Meditation" in text or "abort() was called" in text:
                raise RuntimeError("Device panic; inspect the local serial transcript")
            if re.search(r"\nct> $", text):
                return text
        raise TimeoutError(f"No completed shell prompt after {command!r}")

    def snapshot(self):
        memory = self.command(MEMORY)
        if not re.search(r"Heap integrity\s+ok", memory):
            raise AssertionError("Heap integrity check missing or failed")
        free = self.command("free")
        processes = self.command("ps")
        modules = self.command("modules")
        heap = re.search(r"free heap: (\d+), minimum: (\d+)", free)
        tasks = re.search(r"FreeRTOS tasks\s+count=(\d+)", memory)
        rows = [dict(id=int(p[0]), state=int(p[1]), exit=int(p[2]),
                     heap=int(p[3]), limit=int(p[4]), tasks=int(p[5]), module=p[6])
                for p in re.findall(r"id=(\d+) state=(\d+) exit=(-?\d+) heap=(\d+)/(\d+) tasks=(\d+) module=(\S+)", processes)]
        allocations = [dict(module=m[0], references=int(m[1]), calls=int(m[2]), objects=int(m[3]))
                       for m in re.findall(r"(\S+) \S+ load-refs=(\d+) calls=(\d+) objects=(\d+)", modules)]
        if not heap or not tasks or not rows or not allocations:
            raise AssertionError("Incomplete memory, process, or allocation telemetry")
        if any(p["module"].startswith("tests.") for p in rows):
            raise AssertionError("A test process survived cleanup")
        if any(m["module"].startswith("tests.") for m in allocations):
            raise AssertionError("A test module survived cleanup")
        if any(p["limit"] and p["heap"] > p["limit"] for p in rows):
            raise AssertionError("Managed payload exceeds quota")
        return dict(freeHeap=int(heap[1]), minimumFreeHeap=int(heap[2]),
                    freertosTasksDuringDiagnostics=int(tasks[1]), processes=rows,
                    modules=allocations, heapIntegrity=True)

    def stopped_worker(self, mode):
        started = self.command(f"{ALLOCATOR} {mode} &")
        match = re.search(r"started process (\d+)", started)
        if not match:
            raise AssertionError(f"Could not start {mode} allocator workers")
        pid = int(match[1])
        self.owned_processes.add(pid)
        row = None
        for attempt in range(20):
            state = self.command("ps")
            row = re.search(rf"id={pid} .*?heap=(\d+)/(\d+) tasks=(\d+)", state)
            if row and int(row[3]) >= 3:
                break
            time.sleep(0.05)
        if not row or int(row[3]) < 3 or int(row[1]) > int(row[2]):
            raise AssertionError("Concurrent allocator workers were not observed within quota")
        start = time.monotonic()
        stopped = self.command(f"kill {pid}")
        if "process not found" in stopped:
            raise AssertionError("Worker ended before cancellation")
        waited = self.command(f"wait {pid}")
        # Process slots can be released once the kill command drops its last handle.
        # The missing slot is accepted only with subsequent full cleanup telemetry.
        code = re.search(r"exit code: (-?\d+)", waited)
        self.owned_processes.discard(pid)
        if mode == "force" and code and int(code[1]) != -1:
            raise AssertionError("Forced worker did not report the forced exit code")
        return dict(processId=pid, activeTasks=int(row[3]), peakObservedPayload=int(row[1]),
                    quota=int(row[2]), terminationSeconds=time.monotonic() - start,
                    exitCode=int(code[1]) if code else None,
                    slotReleased="process not found" in waited)

    def quota_failure(self):
        text = self.command(ALLOCATOR + " quota")
        code = re.search(r"exit code: (-?\d+)", text)
        if not code or int(code[1]) >= 0:
            raise AssertionError("Over-quota allocation did not report its negative exit code")
        return int(code[1])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True)
    parser.add_argument("--cycles", type=int, default=100)
    parser.add_argument("--quick-exit-cycles", type=int, default=0,
                        help="Additional quota failures before measurement to stress foreground assignment")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if args.cycles < 1:
        parser.error("--cycles must be positive")
    if args.quick_exit_cycles < 0:
        parser.error("--quick-exit-cycles must be nonnegative")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    report = dict(status="running", port=args.port, baud=115200, requestedCycles=args.cycles,
                  startedUtc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), cycles=[])
    root = Path(__file__).resolve().parents[2]
    report["fixtureHashes"] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                               for p in (root / "examples/ManagedShell/storage/modules").glob("*.ctm")}
    firmware = root / "examples/ManagedShell/build/ctilde_managed_shell.bin"
    report["firmwareSha256"] = hashlib.sha256(firmware.read_bytes()).hexdigest()

    def save():
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    save()
    with args.report.with_suffix(".serial.log").open("wb") as transcript:
        shell = Shell(args.port, transcript)
        try:
            shell.command("")
            report["quickExitQuotaRejections"] = []
            for _ in range(args.quick_exit_cycles):
                report["quickExitQuotaRejections"].append(shell.quota_failure())
                save()
            if args.quick_exit_cycles:
                print(f"PASS {args.quick_exit_cycles} rapid quota exits", flush=True)
            # Fill bounded command history before the measured lifecycle series.
            # Consecutive identical commands are deduplicated by the shell.
            # Alternate harmless padding to populate all 32 retained strings.
            for index in range(34):
                shell.command("free".ljust(40 + index % 2))
            report["baseline"] = shell.snapshot()
            save()
            for cycle in range(1, args.cycles + 1):
                allocator = shell.command(ALLOCATOR)
                if "ALLOCATOR_OK" not in allocator or "exit code:" in allocator:
                    raise AssertionError("Normal concurrent allocation failed")
                overlay = shell.command(OVERLAY)
                if "OVERLAY_CALLABLES_OK" not in overlay or "exit code:" in overlay:
                    raise AssertionError("Overlay delegate/nested/exception fixture failed")
                entry = dict(cycle=cycle, allocator=True, overlay=True)
                if cycle % 10 == 0 or cycle == args.cycles:
                    entry["quotaExitCode"] = shell.quota_failure()
                    entry["cancellation"] = shell.stopped_worker("cancel")
                    entry["forcedCleanup"] = shell.stopped_worker("force")
                entry["idle"] = shell.snapshot()
                report["cycles"].append(entry)
                save()
                print(f"PASS cycle {cycle}/{args.cycles}: heap={entry['idle']['freeHeap']}", flush=True)
            report["status"] = "passed"
            report["completedCycles"] = len(report["cycles"])
            heaps = [entry["idle"]["freeHeap"] for entry in report["cycles"]]
            report["observedIdleHeap"] = dict(first=heaps[0], last=heaps[-1], minimum=min(heaps), maximum=max(heaps))
            if len(heaps) >= 30:
                early = statistics.median(heaps[10:20])
                late = statistics.median(heaps[-10:])
                report["heapStability"] = dict(earlyMedian=early, lateMedian=late,
                                              lossBytes=early - late, allowedLossBytes=512)
                if early - late > 512:
                    raise AssertionError("Idle heap declined beyond the lifecycle allowance")
        except BaseException as error:
            report["status"] = "failed"
            report["error"] = str(error)
            raise
        finally:
            for pid in shell.owned_processes:
                try:
                    shell.command(f"kill {pid}")
                except Exception:
                    report["cleanupCommandFailed"] = True
            report["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            save()
            shell.port.close()


if __name__ == "__main__":
    main()
