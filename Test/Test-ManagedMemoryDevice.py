#!/usr/bin/env python3
"""Run non-destructive allocator/overlay lifecycle checks on a provisioned ManagedShell ESP."""
import argparse
import json
from pathlib import Path
import re
import time
import serial

ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", default="COM4")
    parser.add_argument("--cycles", type=int, default=100)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.cycles < 1:
        parser.error("cycles must be positive")
    args.output.mkdir(parents=True, exist_ok=True)
    port = serial.Serial(port=None, baudrate=115200, timeout=0.1, write_timeout=2)
    port.dtr = False
    port.rts = False
    port.port = args.port
    report = {"schemaVersion": 1, "port": args.port, "requestedCycles": args.cycles,
              "completedCycles": 0, "passed": False, "cycles": [],
              "limitations": ["UART elapsed time includes loading, execution, and console delivery.",
                              "Heap samples can miss peaks between commands.",
                              "This run does not establish SSH, flash mapping, or lifetime-check acceptance."]}
    transcript = []

    def run(command, marker, timeout=30):
        started = time.monotonic()
        port.write(command.encode("ascii") + b"\r")
        response = ""
        while time.monotonic() - started < timeout:
            response += port.read(4096).decode("utf-8", errors="replace")
            clean = ANSI.sub("", response).replace("\r", "")
            marker_offset = clean.find(marker)
            prompt_area = clean[marker_offset + len(marker):] if command else clean
            if marker_offset >= 0 and re.search(r"(?:^|\n)ct> ", prompt_area):
                transcript.append({"command": command, "response": clean})
                return clean, round((time.monotonic() - started) * 1000, 2)
        transcript.append({"command": command, "response": ANSI.sub("", response)})
        raise RuntimeError(f"Timed out waiting for {command}: expected {marker!r} and shell prompt")

    def heap():
        text, _ = run("free", "free heap:")
        match = re.search(r"free heap: (\d+), minimum: (\d+)", text)
        if match is None:
            raise RuntimeError("Unrecognized heap report")
        return {"freeBytes": int(match[1]), "bootMinimumFreeBytes": int(match[2])}

    try:
        port.open()
        run("", "ct>")
        report["before"] = heap()
        for index in range(args.cycles):
            _, allocator_ms = run("tests.allocator.ctm", "ALLOCATOR_OK")
            overlay, overlay_ms = run("tests.overlay.ctm", "OVERLAY_CALLABLES_OK")
            if not re.search(r"overlay failure\s+library overlay failure\s+31\s+34\s+9\s+1\s+1", overlay):
                raise RuntimeError("Overlay exception/call results differ from the expected fixture")
            sample = heap()
            report["cycles"].append({"cycle": index + 1, "allocatorPassed": True,
                                     "overlayPassed": True, "allocatorUartElapsedMs": allocator_ms,
                                     "overlayUartElapsedMs": overlay_ms, **sample})
            report["completedCycles"] = index + 1
            if (index + 1) % 10 == 0:
                print(f"Completed {index + 1}/{args.cycles} cycles", flush=True)
        quota, _ = run("tests.allocator.ctm quota", "exit code:")
        quota_exit = re.search(r"exit code: (-?\d+)", quota)
        if quota_exit is None or int(quota_exit[1]) == 0 or re.search(r"\n131072\s", quota):
            raise RuntimeError("Allocator quota fixture did not reject its oversized allocation")
        report["quota"] = {"rejected": True, "exitCode": int(quota_exit[1]), **heap()}
        report["termination"] = []
        for mode in ("cancel", "force"):
            started, _ = run(f"tests.allocator.ctm {mode} &", "started process ")
            match = re.search(r"started process (\d+)", started)
            if match is None:
                raise RuntimeError("Allocator termination fixture did not start")
            stopped, elapsed = run("kill " + match[1], "kill " + match[1])
            if "process not found" in stopped:
                raise RuntimeError("Allocator termination fixture exited before termination")
            if mode == "cancel" and "ALLOCATOR_OK" not in stopped:
                raise RuntimeError("Cooperative allocator cancellation did not join its workers")
            report["termination"].append({"mode": mode, "terminationUartElapsedMs": elapsed, **heap()})
        modules, _ = run("modules", "modules:")
        if not re.search(r"\nmodules: 1\s", modules):
            raise RuntimeError("Expected only the UART shell module after fixture cleanup")
        report["processes"], _ = run("ps", "processes:")
        report["memory"], _ = run("memory.ctm", "Heap integrity")
        if not re.search(r"Heap integrity\s+ok", report["memory"]):
            raise RuntimeError("Heap integrity check failed")
        report["after"] = heap()
        report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        port.close()
        (args.output / "lifecycle.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        (args.output / "transcript.json").write_text(json.dumps(transcript, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
