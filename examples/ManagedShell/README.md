# ManagedShell

ManagedShell is the Draft 0.49 ESP-IDF example for Runtime ABI 22 and Managed Module ABI 3. `Examples.sln` exposes the firmware, its applications, libraries, and overlay acceptance fixtures as separate projects under **Managed Modules**. The firmware mounts LittleFS at `/storage` and the isolated SFTP partition at `/sftp`, owns the T-CAN485 removable-SD monitor for `/sd`, initializes the shared process/runtime-service host, and accepts the built-ins `ls`, `modules`, `ps`, `kill`, `wait`, `send`, `free`, and `help`.

Bare `ls` lists the mounted `/sd` root followed by the LittleFS module directory, so files created directly on the card are visible. Use `ls <path>` to inspect a specific directory, for example `ls /sd/modules` or `ls /storage/modules`.

Applications must be invoked with their exact lowercase `.ctm` extension. A bare application name is not inferred and the old `exec` command no longer exists. Applications run in the foreground by default; a final unquoted `&` starts one in the background and prints its process identifier. Double quotes preserve whitespace and may form all or part of an argument. The parser supports `\"`, `\\`, `\t`, `\r`, and `\n`; malformed quotes or escapes execute nothing. A quoted `"&"` remains an ordinary argument. There is no expansion, globbing, piping, redirection, comment syntax, or single-quote syntax.

Application names search `/sd/modules` first and `/storage/modules` second. Only an absent SD entry selects the LittleFS fallback; a present but corrupt or ABI-incompatible SD module reports its real error. Explicit paths must be direct children of one of those two roots. Nested paths, traversal, backslashes, and absolute paths outside the roots are rejected. A `send` payload preserves every parsed argument after the process identifier, joined with spaces.

The resident platform owns the card monitor and a versioned, serialized storage-control bridge. Storage command parsing, validation, policy, and reporting live in the LittleFS-shipped `sd.ctm`, not in the shell. The monitor uses the supported T-CAN485 SDSPI wiring on SPI2: MISO 2, MOSI 15, SCLK 14, CS 13, at 20 MHz. No card at boot is nonfatal and remains eligible for automatic retry. Unsafe physical removal can lose unflushed FAT data.

The serial shell passes `ct> ` to `Console.ReadLine(string)` so ESP-IDF `linenoise` owns the prompt and keeps cursor redraws aligned. It provides visible input, Backspace/Delete, cursor editing, and a volatile 32-command Up/Down history. The history is intentionally RAM-only and resets with the firmware.

The onboard GPIO4 WS2812 is a low-brightness status display. Orange means that firmware startup is still initializing, blue is an idle prompt after a successful process batch, green means that at least one process is starting or running, and red means that a module failed to start or at least one process in the completed overlapping batch returned a nonzero exit code. Red remains latched across ordinary shell commands and clears when a new idle process batch starts. Every received terminal byte overlays white for 100 ms; rapid input extends the pulse, and the controller then restores the current base color. LED setup or update failure disables further LED work and reports an ESP error without stopping the shell.

`Modules/Hello` builds `examples.hello.ctm` plus deterministic `examples.hello.ctmeta.json`. It demonstrates a managed application entry point, its `Process.Current` identity, copied arguments, process-local mutable statics, shared-runtime console output, heap accounting, cancellation inspection, automatic exit, reverse finalization, unloading, and reload. With no arguments it exits immediately for lifecycle loops. The `listen` argument enters a timed mailbox loop, decodes copied UTF-8 messages, exits normally after an `exit` message, and returns `-1` after observing cooperative cancellation. The `burn` argument performs bounded arithmetic bursts and a timed mailbox receive, which loads one core without starving both ESP32 idle tasks; it accepts the same `exit` message and cancellation behavior. `Process.Current` is `null` in ordinary firmware code.

`kill` submits termination to a firmware control task. The runtime waits for allocator, registry, call-frame, mailbox, and console-write bookkeeping to reach a stable boundary before deleting the managed main task; a separate reaper performs blocking cleanup. Blocking console input, finalization already in progress, module-created child tasks, and untracked native resources remain current limitations rather than protected-process guarantees.

## Diagnostics

`free` keeps its compact existing shell output. Run `memory.ctm` for the comprehensive one-shot report. The application accepts no arguments. Its sections have these meanings:

- `RAM summary` reports exact total, used, currently available, attributed allocation payload, allocator overhead, lifetime minimum free, peak used, largest currently possible one-block allocation, fragmentation, and block counts.
- `Capability pools` reports default, 8-bit, 32-bit, internal, DMA, executable, and SPIRAM heaps. These rows overlap and must not be summed.
- `Managed processes` includes retained process handles, states, root modules, attributed managed payload and limit, and task counts. The running `memory` application therefore appears in its own snapshot. An exited process can remain until the shell releases its handle.
- `Managed modules` includes load references, active calls, live managed allocations, and unload state.
- `FreeRTOS tasks` is ordered by stable task number and includes state, priority, affinity, and lifetime minimum unused stack bytes.
- `LittleFS` reports exact capacity, use, availability, and integer percentage. `Heap integrity` reports `ok` or `corrupt`.

Facilities which are unavailable are printed as `not configured` or an explicit error. The current ESP32 has no configured SPIRAM. Reporter scratch allocations use the firmware's native allocator and are captured after the RAM snapshot; they are freed before the command returns and never enter managed-process accounting.

The full reporters, their formatting strings, and their private C entry points live in `memory.ctm` and `taskmgr.ctm`, not in the always-resident shell firmware. Each module declares its implementation through `managedModule.nativeSources`; the generated component source list links that C directly into the corresponding ELF. Consequently, the implementation occupies LittleFS and temporary loader memory only while its module is present or loaded. Firmware retains a small versioned host accessor table and the enabled FreeRTOS trace/runtime-statistics support because a loaded module cannot directly access private runtime state or unregistered ESP-IDF functions. Diagnostics protocol version 2 exposes fixed-width, example-owned heap and task records plus wrapper operations instead of leaking ESP-IDF or FreeRTOS structures into either module's native source. The accessor stores no module callback and is safe across unload. It is an example-local contract, not Runtime ABI 22 or Managed Module ABI 3.

Run `taskmgr.ctm` to sample for 250 ms between two raw FreeRTOS task snapshots and list only starting, running, or cancelling managed processes. CPU uses a per-core scale: one saturated core is 100%, so this dual-core target can report up to 200% system load. Each row contains PID, state, root module, thread count, attributed heap use and limit, interval CPU, and lifetime minimum task-stack headroom. Tasks are matched by both task number and native handle. If every runtime-published process task cannot be mapped in both samples, that process prints `cpu=n/a stack-min=n/a` instead of a misleading partial measurement. The report includes the short-lived `taskmgr` process itself because it is a normal managed application.

`taskmgr.ctm kill <pid>` resolves the published runtime process identifier and uses the same policy as the shell's `kill <pid>`: cooperative cancellation followed by forced termination after one second. Other application arguments print `usage: taskmgr [kill <pid>]`; arguments to `memory.ctm` print `usage: memory`.

## SD management

`sd.ctm` is the sole SD administration command. Read-only inspection is available with `sd.ctm status`, `sd.ctm info`, and `sd.ctm mbr show`. Lifecycle commands are `sd.ctm mount [whole|p0|p1|p2|p3]`, `sd.ctm unmount`, and `sd.ctm remount`. A deliberate unmount invalidates open `/sd` handles and resets affected process current directories to `/`.

Destructive operations require an unmounted card and the literal `--yes` acknowledgement:

```text
sd.ctm format --yes <whole|p0|p1|p2|p3> [auto|fat12|fat16|fat32] [allocationUnitBytes] [1|2]
sd.ctm mbr write --yes <entry0> <entry1> <entry2> <entry3>
```

An MBR entry is `empty` or `[boot:]<type>:<firstLba>:<sectorCount>`. Type can be a supported FAT name or `0xNN`. MBR writing preserves bootstrap and disk-signature bytes, replaces exactly four partition entries plus the signature, flushes, and verifies the result. It neither formats nor mounts a partition. Formatting does not remount automatically. These commands erase or replace storage structures; use them only on a disposable or backed-up card.

## Text editor

Run `nano.ctm <path>` in the foreground to edit one strict UTF-8 file. New files begin empty; existing CRLF or lone-CR line endings are normalized to LF and marked modified. The editor preserves an existing UTF-8 BOM, limits editable text to 32 KiB, and uses sibling `.nano.tmp` and `.nano.bak` files for recovery-safe replacement. If the target is missing on the next launch while its backup exists, Nano restores that backup before loading it.

Arrow keys, Home, End, Page Up, and Page Down move the cursor. Enter, Tab, Backspace, Delete, typed Unicode scalars, and bracketed paste edit the buffer. `Ctrl+O` saves, `Ctrl+X` exits, and `Ctrl+L` repeats terminal-size negotiation and repaints. Exiting a modified buffer offers save, discard, and cancel choices. Terminal-size negotiation waits at most 200 ms, falls back to 80 by 24, caps the display at 160 by 50, and rejects terminals smaller than 20 by 6. Tabs use four-column stops; Unicode scalars occupy one display cell in this initial editor.

Do not launch Nano with a trailing `&`. ManagedShell does not arbitrate terminal input or job control for background interactive applications. Nano restores the normal screen, cursor visibility, and bracketed-paste mode on normal exit, cancellation, and I/O failure.

## Network and SSH development slice

`net.ctm` administers the resident Wi-Fi station owner. Use `net.ctm status`, `net.ctm wifi scan`, `net.ctm wifi connect-profile <name>`, `net.ctm wifi disconnect`, or `net.ctm wait [timeout-ms]`. Profiles are read from `/storage/net/profiles/<name>.conf`; only `ssid`, `password`, and optional `hostname` keys are accepted. Profile names use a restricted filename alphabet, so credentials never appear in command arguments or shell history. Network state stays resident after `net.ctm` unloads.

`system.ssh.ctm` is a metadata-linked Managed Module ABI 3 library. `sshd.ctm` compiles against its deterministic `.ctmeta.json` declarations without provider source, and the loader checks and patches the two imports used by the service. The current native transport slice waits for an address, listens on port 22, accepts one client at a time, and exchanges the SSH identification line. The public concrete SSH types, framing helpers, algorithm allowlist, P-256 authorized-key line validation, and SFTP root-normalization helpers are present, but key exchange, authentication, encrypted packets, channels, remote shell/exec, and SFTP request execution are not yet implemented. Do not expose this development server to an untrusted network.

`tests.overlay.library.ctm` and `tests.overlay.ctm` are acceptance fixtures rather than shell utilities. The library exports resident-stubbed overlay methods, including a method which throws after registering cleanup. The application exercises nested `first -> second -> first` transitions, same-overlay direct calls, a delegate call, cross-module state, exception propagation through the provider stub, and a successful call after the exception. They are built and copied by `Test-ManagedShell.ps1`; run `tests.overlay.ctm` in the foreground on an ESP32/Xtensa board to validate the runtime path.

The partition table keeps the 2 MiB factory image, uses `0x0f0000` bytes for `/storage`, and dedicates `0x100000` bytes to `/sftp`. An optional gitignored `provisioning.local` tree is merged into the storage image at configure time. Keep Wi-Fi profiles, the PKCS#8 host key, and `authorized_keys` there; ordinary builds do not require them. A future SSH packaging target will make those files mandatory before flashing an SSH-enabled image.

Runtime statistics, 64-bit counters, and task tracing add firmware RAM, flash, and scheduling overhead. Both reports are live snapshots: a task, process, allocation, or filesystem value can change immediately after collection.

Build both images with:

```powershell
.\examples\ManagedShell\Test-ManagedShell.ps1 -BuildOnly
```

After flashing, this interaction demonstrates copied IPC and cooperative cancellation. Replace `<id>` with the identifier printed by the background launch:

```text
examples.hello.ctm listen &
send <id> hello from the shell
kill <id>
wait <id>
```

The module prints `message: hello from the shell`, then `cancellation observed`; `wait` reports exit code `-1`. Send the exact message `exit` instead of `kill` to exercise graceful completion.

The standalone applications produce output in this form. Exact identifiers, byte counts, task counts, stack headroom, and percentages depend on the build and live activity. Each diagnostics application appears in its own report while it is running.

```text
ct> taskmgr.ctm
Task manager
  sample-ms=250 cpu-scale=per-core cores=2 maximum=200.0%
  system-cpu=<sample>% freertos-tasks=<count> active-processes=1
  PID STATE MODULE THREADS HEAP LIMIT CPU STACK-MIN
  pid=<id> state=running module=taskmgr threads=1 heap=<bytes> limit=32768 cpu=<sample>% stack-min=<bytes>

ct> examples.hello.ctm listen &
hello from managed process <id> invocation #1
arguments: 1
listening for copied messages
started process <id>
ct> taskmgr.ctm
  system-cpu=<sample>% freertos-tasks=<count> active-processes=2
  pid=<id> state=running module=examples.hello threads=1 heap=<bytes> limit=65536 cpu=<sample>% stack-min=<bytes>
  pid=<id> state=running module=taskmgr threads=1 heap=<bytes> limit=32768 cpu=<sample>% stack-min=<bytes>

ct> examples.hello.ctm burn &
started process <id>
burning CPU in watchdog-safe bursts
ct> taskmgr.ctm
  system-cpu=<sample>% freertos-tasks=<count> active-processes=2
  pid=<id> state=running module=examples.hello threads=1 heap=<bytes> limit=65536 cpu=<nonzero-sample>% stack-min=<bytes>
  pid=<id> state=running module=taskmgr threads=1 heap=<bytes> limit=32768 cpu=<sample>% stack-min=<bytes>
ct> taskmgr.ctm kill <id>
cancellation observed
termination requested for process <id>
ct> wait <id>
exit code: -1
```

For an LED acceptance run, start `examples.hello.ctm listen` and observe green. Sending `exit` makes the final idle state blue. Starting it again and using `kill` produces exit code `-1` and makes the final idle state red. While either color is displayed, typing temporarily overlays white and restores the underlying state.

Flash and monitor with an active ESP-IDF 6 environment:

```powershell
idf.py -C examples/ManagedShell -p COM4 flash monitor
```

A manual serial-terminal acceptance on the connected ESP32-D0WDQ6-V3 on 2026-09-02 completed a 16-cycle warm-up followed by 100 load/run/exit/unload cycles. Every application returned its argument count, every reload observed independent static state, the module registry returned to zero, task counts returned to zero, and free heap remained exactly 167,000 bytes across the measured 100-cycle interval. This is historical pre-diagnostics evidence; the runtime-statistics build has different memory overhead.

The native diagnostics implementation was accepted on the same board on 2026-09-02 while it was temporarily exposed as shell built-ins. Idle, listening, CPU-active, normal-exit, and termination cases passed; the burn process measured 93.2% of one core, cancellation returned `-1`, normal exit returned `0`, attributed managed payload returned to zero, the module registry returned to zero, and heap integrity remained `ok`. After warming all 32 history entries, 100 consecutive reports left current free heap unchanged at exactly 129,632 bytes. The lifetime minimum fell from 125,404 to 124,372 bytes because of transient reporter activity. These figures validate the reporter internals but are not acceptance evidence for the final standalone `memory.ctm` and `taskmgr.ctm` packaging.

Managed modules are trusted native code. There is no MMU boundary: unsafe pointers, ESP-IDF calls, MMIO, callbacks, interrupts, or native resources that escape runtime accounting can corrupt the firmware and make forced reclamation unsafe.
