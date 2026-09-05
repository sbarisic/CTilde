# Shell logging, process memory, and SSH RAM

The firmware routes ESP-IDF diagnostics through the existing SD control task to `/sd/run.log`. Shell commands keep their normal output. Logging adds a fixed 2,048-byte queue and no task stack.

Unavailable or failed storage uses console output. Queue overflow and records longer than 511 bytes also use the console instead of losing the record. The SD monitor waits for an active append before invalidating the volume.

Task manager shows `mem`, the managed process payload as a percentage of total 8-bit RAM. This is not a complete resident-memory measurement. Shared code, native storage, and task stacks are excluded. CPU measurements remain unchanged.

SSH now reports network and listener state changes, for example `sshd: network=True listening=True port=22 error=0`. The old startup-only waiting message did not describe later Wi-Fi state.

## Linked SSH memory comparison

The baseline is commit `5ba7f44`. The same ESP32 release configuration and toolchain produced these measurements. Both versions retain the 8,192-byte daemon stack and 35,000-byte packet limit.

| `system.ssh` cost | Before | After | Change |
| --- | ---: | ---: | ---: |
| Shared linked RAM | 50,276 | 51,360 | +1,084 |
| Per-process overlay window | 15,952 | 10,112 | -5,840 |
| Combined known cost for one process | 66,228 | 61,472 | -4,756 |

Separate channel-request, channel-I/O, and key-negotiation groups reduce the largest overlay. Extra entry points and metadata increase resident memory. The net linked saving is 4,756 bytes. Resident code remains below the existing 36 KiB limit. More group transitions can increase SD/LittleFS reads during a session. The throughput effect has not been measured.

These figures exclude dynamic loader costs, cryptographic allocations, sockets, and the new firmware log queue. They are not measurements of current device free heap. SSH still has a large resident footprint; flash-mapped modules and further compiler reductions remain outstanding.

## Validation

All 13 modules and the complete firmware build passed, including the existing executable and overlay size budgets. The diagnostic and SSH fixture suites passed all 12 checks.

The production log queue passed host sanitizer checks for concurrent producers, append order, overflow, recursive logging, unavailable storage, and remount. The production append and unmount functions passed a deterministic concurrent test, including short writes and full-storage errors. Diagnostic transcript checks cover memory percentages alongside CPU and partial task snapshots.

Run the logging checks from the repository root in Linux or WSL with a C compiler:

```sh
python3 Test/Test-ManagedLog.py
python3 Test/Test-ManagedLogStorage.py
```

On-device log routing, SD removal, task-manager output, SSH connections, and session throughput require a new firmware and module flash. The currently connected board was not interrupted for these changes.
