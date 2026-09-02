# ManagedShell

ManagedShell is the Draft 0.45 ESP-IDF example for Runtime ABI 18 and Managed Module ABI 1. `Examples.sln` exposes the firmware and managed Hello application as separate projects under **Managed Modules**. The firmware mounts LittleFS at `/storage`, initializes the shared process/runtime-service host, and accepts `ls`, `modules`, `ps`, `exec`, `kill`, `wait`, `send`, `free`, and `help` commands. Modules use the current loader's flat namespace and must be direct children of `/storage/modules`; nested paths, absolute paths outside that root, and traversal segments are rejected. A `send` payload preserves every space-separated word after the process identifier.

The serial shell passes `ct> ` to `Console.ReadLine(string)` so ESP-IDF `linenoise` owns the prompt and keeps cursor redraws aligned. It provides visible input, Backspace/Delete, cursor editing, and a volatile 32-command Up/Down history. The history is intentionally RAM-only and resets with the firmware.

The onboard GPIO4 WS2812 is a low-brightness status display. Orange means that firmware startup is still initializing, blue is an idle prompt after a successful process batch, green means that at least one process is starting or running, and red means that a module failed to start or at least one process in the completed overlapping batch returned a nonzero exit code. Red remains latched across ordinary shell commands and clears when a new idle process batch starts. Every received terminal byte overlays white for 100 ms; rapid input extends the pulse, and the controller then restores the current base color. LED setup or update failure disables further LED work and reports an ESP error without stopping the shell.

`Modules/Hello` builds `examples.hello.ctm` plus deterministic `examples.hello.ctmeta.json`. It demonstrates a managed application entry point, its `Process.Current` identity, copied arguments, process-local mutable statics, shared-runtime console output, heap accounting, cancellation inspection, automatic exit, reverse finalization, unloading, and reload. With no arguments it exits immediately for lifecycle loops. The `listen` argument enters a timed mailbox loop, decodes copied UTF-8 messages, exits normally after an `exit` message, and returns `-1` after observing cooperative cancellation. `Process.Current` is `null` in ordinary firmware code.

`kill` submits termination to a firmware control task. The runtime waits for allocator, registry, call-frame, mailbox, and console-write bookkeeping to reach a stable boundary before deleting the managed main task; a separate reaper performs blocking cleanup. Blocking console input, finalization already in progress, module-created child tasks, and untracked native resources remain current limitations rather than protected-process guarantees.

Build both images with:

```powershell
.\examples\ManagedShell\Test-ManagedShell.ps1 -BuildOnly
```

After flashing, this interaction demonstrates copied IPC and cooperative cancellation. Replace `<id>` with the identifier printed by `exec`:

```text
exec examples.hello.ctm listen
send <id> hello from the shell
kill <id>
wait <id>
```

The module prints `message: hello from the shell`, then `cancellation observed`; `wait` reports exit code `-1`. Send the exact message `exit` instead of `kill` to exercise graceful completion.

For an LED acceptance run, start `examples.hello.ctm listen` and observe green. Sending `exit` makes the final idle state blue. Starting it again and using `kill` produces exit code `-1` and makes the final idle state red. While either color is displayed, typing temporarily overlays white and restores the underlying state.

Flash and monitor with an active ESP-IDF 6 environment:

```powershell
idf.py -C examples/ManagedShell -p COM4 flash monitor
```

A manual serial-terminal acceptance on the connected ESP32-D0WDQ6-V3 on 2026-09-02 completed a 16-cycle warm-up followed by 100 load/run/exit/unload cycles. Every application returned its argument count, every reload observed independent static state, the module registry returned to zero, task counts returned to zero, and free heap remained exactly 167,000 bytes across the measured 100-cycle interval. The current runner builds and copies the artifacts but does not persist a machine-readable hardware report, so these values are a dated operator observation rather than a replayable automated result.

Managed modules are trusted native code. There is no MMU boundary: unsafe pointers, ESP-IDF calls, MMIO, callbacks, interrupts, or native resources that escape runtime accounting can corrupt the firmware and make forced reclamation unsafe.
