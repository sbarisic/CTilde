# ManagedShell

ManagedShell is the Draft 0.50 ESP-IDF example for Runtime ABI 22 and Managed Module ABI 3. `Examples.sln` exposes the firmware, its applications, libraries, and overlay acceptance fixtures as separate projects under **Managed Modules**. The firmware mounts LittleFS at `/storage` and the isolated SFTP partition at `/sftp`, owns the T-CAN485 removable-SD monitor for `/sd`, initializes the shared process/runtime-service hosts, and supervises `/storage/modules/shell.ctm --uart`. The same loadable shell application supplies UART, redirected SSH, and one-command exec sessions.

Bare `ls` lists the mounted `/sd` root followed by the LittleFS module directory, so files created directly on the card are visible. Use `ls <path>` to inspect a specific directory, for example `ls /sd/modules` or `ls /storage/modules`.

After storage initialization, ESP-IDF diagnostic logs append to `/sd/run.log`. Ordinary command output stays on the console. The existing SD control task drains a 2 KiB queue, normally within 50 ms. It closes the file after each append and excludes unmount during a write.

If the SD log cannot be written, queued text goes to the console. Oversized log records, a full queue, and errors from the log writer also use the console. This fallback preserves diagnostics without blocking driver tasks on SD access. Early boot logs precede this routing.

`mkdir <path> [path ...]` recursively creates one or more directories. `cat <path> [path ...]` writes one or more strict UTF-8 text files to the console without inserting separators. If the combined nonempty output has no final LF, `cat` writes one display newline so the next shell prompt starts on a fresh line. These extensionless commands are thin shell aliases: the filesystem behavior lives in the LittleFS-shipped `commands.fs.ctm` application and is loaded only when needed.

Applications must be invoked with their exact lowercase `.ctm` extension. A bare application name is not inferred and the old `exec` command no longer exists. Applications run in the foreground by default; a final unquoted `&` starts one in the background and prints its process identifier. Double quotes preserve whitespace and may form all or part of an argument. The parser supports `\"`, `\\`, `\t`, `\r`, and `\n`; malformed quotes or escapes execute nothing. A quoted `"&"` remains an ordinary argument. There is no expansion, globbing, piping, redirection, comment syntax, or single-quote syntax.

Application names search `/sd/modules` first and `/storage/modules` second. Only an absent SD entry selects the LittleFS fallback; a present but corrupt or ABI-incompatible SD module reports its real error. Explicit paths must be direct children of one of those two roots. Nested paths, traversal, backslashes, and absolute paths outside the roots are rejected. A `send` payload preserves every parsed argument after the process identifier, joined with spaces.

The resident platform owns the card monitor and a versioned, serialized storage-control bridge. Storage command parsing, validation, policy, and reporting live in the LittleFS-shipped `sd.ctm`, not in the shell. The monitor uses the supported T-CAN485 SDSPI wiring on SPI2: MISO 2, MOSI 15, SCLK 14, CS 13, at 20 MHz. No card at boot is nonfatal and remains eligible for automatic retry. Unsafe physical removal can lose unflushed FAT data.

`shell.ctm --uart` and `shell.ctm --ssh` use the same managed ANSI editor. It supports UTF-8 input, Backspace/Delete, cursor movement, Home/End, a volatile 32-command Up/Down history, quoting, bracketed paste, redraw, EOF, and cancellation. Each shell process owns its history. `shell.ctm --exec <command>` parses and runs exactly one foreground command and rejects background syntax; invalid invocation forms return 2. UART input still drives the resident white LED activity pulse, while redirected SSH input does not impersonate local terminal activity.

The onboard GPIO4 WS2812 is a low-brightness status display. Orange means that firmware startup is still initializing, blue is an idle prompt after a successful process batch, green means that at least one process is starting or running, and red means that a module failed to start or at least one process in the completed overlapping batch returned a nonzero exit code. Red remains latched across ordinary shell commands and clears when a new idle process batch starts. Every received terminal byte overlays white for 100 ms; rapid input extends the pulse, and the controller then restores the current base color. LED setup or update failure disables further LED work and reports an ESP error without stopping the shell.

`Modules/Hello` builds `examples.hello.ctm` plus deterministic `examples.hello.ctmeta.json`. It demonstrates a managed application entry point, its `Process.Current` identity, copied arguments, process-local mutable statics, shared-runtime console output, heap accounting, cancellation inspection, automatic exit, reverse finalization, unloading, and reload. With no arguments it exits immediately for lifecycle loops. The `listen` argument enters a timed mailbox loop, decodes copied UTF-8 messages, exits normally after an `exit` message, and returns `-1` after observing cooperative cancellation. The `burn` argument performs bounded arithmetic bursts and a timed mailbox receive, which loads one core without starving both ESP32 idle tasks; it accepts the same `exit` message and cancellation behavior. `Process.Current` is `null` in ordinary firmware code.

`kill` submits termination to a firmware control task. The runtime waits for allocator, registry, call-frame, mailbox, and console bookkeeping to reach a stable boundary before deletion; a separate reaper performs blocking cleanup. Source-created FreeRTOS workers inherit the creating process, contribute to its task count, and are joined on normal completion or deleted before forced reclamation. Redirected endpoints and resident-accessor sockets and cryptographic contexts are process-owned resources. An uncaught application-main exception writes a bounded stderr diagnostic and exits with `-2` instead of panicking firmware. Unsafe state which escapes the resident resource ledger remains outside the forced-reclamation guarantee.

## Diagnostics

Module builds now emit `<module>.memory.json` beside each `.ctm`. The report separates linked code, mutable data, constants, padding, stack, and overlay requirements. Dynamic costs remain unknown until measured. Optional `managedModule.memoryLimits` fields are `residentRamBytes`, `overlayRamBytes`, and `processStackBytes`. They limit known build requirements, not total runtime peaks.

The [lower-RAM progress report](DRAFT051_PROGRESS.md) describes current loader and SSH changes. Draft 0.51, flash mapping, and safe spans remain incomplete. Existing ABI versions and partition offsets remain unchanged.

`free` prints the current free heap and lifetime minimum free heap, followed by an aligned RAM table. Each row shows free, used, and total bytes plus the free percentage to one decimal place. Rows include default, 8-bit, 32-bit, internal, DMA, executable, and SPIRAM pools. These pools overlap and must not be summed. Unavailable pools show `not configured`.

Run `memory.ctm` for the comprehensive one-shot report. The application accepts no arguments. Its sections have these meanings:

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

Each task-manager row also shows `mem`, the process's managed allocation payload divided by total byte-addressable RAM, to one decimal place. The heading identifies this basis. Shared module code, task stacks, and native allocations are excluded because the process payload counter does not attribute them. This percentage is independent of CPU sampling and the process heap quota.

See the [logging and SSH memory review](SHELL_LOGGING_REVIEW.md) for the current changes, linked-memory comparison, and pending device checks.

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

Nano uses two working-set-oriented code overlays. The `editor` overlay keeps the main loop, input parser, and file operations together; the `buffer` overlay keeps editing, cursor calculations, and terminal rendering together. This avoids reloading overlays for every helper call during input and painting. The earlier Draft 0.49 size baseline recorded about 26.9 KiB of `.text`, its reusable overlay window is 26.9 KiB, and neither requires the previous contiguous 36.9 KiB text allocation. That 96,772-byte package was below the 96 KiB artifact limit. Rebuilt package measurements are recorded by the size-check runner; this historical measurement is not a current size guarantee.

## Network and SSH development slice

`net.ctm` administers the resident Wi-Fi station owner. Use `net.ctm status`, `net.ctm wifi scan`, `net.ctm wifi connect-profile <name>`, `net.ctm wifi disconnect`, or `net.ctm wait [timeout-ms]`. Profiles are read first from `/sd/wifi_profile/<name>.conf`, so credentials survive firmware and LittleFS image updates. An absent SD profile falls back to `/storage/net/profiles/<name>.conf`; a present SD profile always wins. Only `ssid`, `password`, and optional `hostname` keys are accepted. Profile names use a restricted filename alphabet, so credentials never appear in command arguments or shell history. Network state stays resident after `net.ctm` unloads. The command implementation uses a bounded linear UTF-8 profile parser. The pre-correctness size baseline recorded a 12.5 KiB overlay and about 25.5 KiB resident `.text`, replacing a contiguous 48.4 KiB allocation. Use the latest build report for current sizes.

`system.ssh.ctm` is a metadata-linked Managed Module ABI 3 library. `sshd.ctm` compiles against its deterministic `.ctmeta.json` declarations without provider source, obtains the fixed administration defaults from `SshDaemon.ManagedShellDefaults()`, and enters the library service loop. The library implements one nonblocking connection, strict Curve25519 key exchange, P-256 host signatures and public-key authentication, AES-128-GCM packets, rekey requests and one-hour/1-GiB limits, two session channels, redirected shell/exec, window changes, channel flow control, and SFTP v3 rooted at `/sftp`. Service, transport, channel, configuration, key-exchange, authentication, and three SFTP groups are separately placed; stable calls and the active connection state remain resident. The firmware exports only a versioned opaque-token socket/crypto accessor whose resources are charged to the `sshd` process.

Start the service as `sshd.ctm &`. Its mailbox accepts `status`, `reload-keys`, `disconnect`, and `stop`. The configured shell path is fixed to `/storage/modules/shell.ctm`, bypassing SD module precedence for the administration shell. Reload constructs a complete replacement key set; an active connection keeps its captured keys until it disconnects. This development implementation has passed only focused compilation and deterministic source/fixture checks. Connected-board, OpenSSH/libssh interoperability, malformed-packet fuzzing, endurance, and independent security review remain deferred. Do not treat it as production-ready or expose it to an untrusted network.

`tests.overlay.library.ctm` and `tests.overlay.ctm` are acceptance fixtures rather than shell utilities. The library exports resident-stubbed overlay methods, including a method which throws after registering cleanup. The application exercises nested `first -> second -> first` transitions, same-overlay direct calls, a delegate call, cross-module state, exception propagation through the provider stub, and a successful call after the exception. They are built and copied by `Test-ManagedShell.ps1`; run `tests.overlay.ctm` in the foreground on an ESP32/Xtensa board to validate the runtime path.

`Modules/AllocatorFixture` builds the separate non-overlay `tests.allocator.ctm` fixture. Two source-created workers allocate and release managed cells concurrently under a 64 KiB payload quota. Normal completion prints `ALLOCATOR_OK`; `quota` requests an allocation above that limit, `cancel` runs until cooperative cancellation, and `force` keeps both workers active for forced termination. Overlay-enabled processes reject source-created threads, so allocator concurrency and overlay transitions are separate acceptance lanes. This fixture is built by `Test-ManagedShell.ps1` and is not an additional `Examples.sln` editor project.

The overlay fixture also checks a private delegate target held in a static initializer and a resident unmanaged-address target called by overlay code. Compiler placement retains their callable entries and ordinary resident call closure. The local elf_loader changes are documented separately in [ELF_LOADER_CHANGES.md](ELF_LOADER_CHANGES.md).

The partition table keeps the 2 MiB factory image, uses `0x0f0000` bytes for `/storage`, and dedicates `0x100000` bytes to `/sftp`. An optional gitignored `provisioning.local` tree is merged into the storage image at configure time. Keep Wi-Fi profiles, the unencrypted PKCS#8 P-256 host key, and P-256 `authorized_keys` there; ordinary builds do not require them. The `ctilde_ssh_package` CMake target requires `ssh/sshd.conf`, `ssh/ssh_host_ecdsa_key.pem`, `ssh/authorized_keys`, and at least one local Wi-Fi profile before producing an SSH-provisioned image.

Runtime statistics, 64-bit counters, and task tracing add firmware RAM, flash, and scheduling overhead. Both reports are live snapshots: a task, process, allocation, or filesystem value can change immediately after collection.

All three administration modules explicitly select Release, LTO, precise floating point, and the Draft 0.50 `size` profile. The focused build reads the final packaged ELF program headers and enforces these executable-memory budgets: `shell.ctm` has at most 28 KiB resident code and a 20 KiB window, `system.ssh.ctm` has at most 36 KiB resident code and a 28 KiB window, and `sshd.ctm` has at most 10 KiB resident code. The combined UART shell plus SSH daemon graph must remain at or below 122 KiB, including both process overlay windows. The historical Draft 0.50 optimization baseline before this correctness pass recorded 71,144 bytes/13,453 resident executable/6,304 overlay for `shell.ctm`, 204,388/35,774/15,936 for `system.ssh.ctm`, and 14,604/10,184/0 for `sshd.ctm`. Their computed concurrent executable working set is 81,651 bytes. The ignored report is `artifacts/managed-shell/managed-module-sizes.json`.

Before relocation, the firmware now logs each module's total resident executable bytes, largest contiguous executable segment, maximum overlay window, executable-capable free memory, and largest executable-capable free block. A failed resident or overlay allocation repeats the relevant values without printing credentials or provisioning paths.

Build both images with:

```powershell
.\examples\ManagedShell\Test-ManagedShell.ps1 -BuildOnly
```

Use `-ValidateOnly` for the bounded source-contract, copied-header, and transcript checks without rebuilding every module.

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

For initial installation on a device whose storage can be replaced, flash and monitor with an active ESP-IDF 6 environment. This target also writes the generated `storage` and `sftp` images. For an existing device, preserve its flash backup and merge only the required module updates into its storage image before writing the application and storage partitions; do not use the full flash target to preserve user data.

For the existing 4 MiB ESP32 on Windows, close the serial monitor and run this command from the repository root:

```powershell
.\examples\ManagedShell\Rebuild-Flash.ps1 -Port COM4
```

The script rebuilds all modules and firmware, checks size budgets, and flashes the full firmware and built storage images. It does not download flash or calculate differences by default. Files stored only on the device's storage partition are replaced by the build image. NVS, SFTP, the partition table, and the bootloader are not written.

The script uses the ROM bootloader (`--no-stub`) and opens the serial monitor after flashing. Press Ctrl+] to close the monitor. Use `-NoMonitor` to stop after flashing. Override `-IdfProfile` and `-EspPython` if the installed tool paths differ. Use `-UseStub` only on a connection where the faster stub works reliably.

Use `-PreserveStorage` to enable the slower backup-and-merge workflow. This mode downloads a fresh 4 MiB backup, replaces only changed shipped modules, and verifies that other storage files remain unchanged. It enables differential flashing with esptool verification. Add `-FullFlash` to this mode to write both merged images in full. The ROM backup can take tens of minutes.

Preservation mode stores backups and reports in ignored `artifacts/managed-shell/flash-*` directories and installs `littlefs-python` 0.19.0 locally. Backups can contain credentials. This mode retries a failed backup once and requires a complete backup, matching partition table, and mountable filesystem before writing.

```powershell
idf.py -C examples/ManagedShell -p COM4 flash monitor
```

After installing matching firmware and test modules, run the serial acceptance from the repository root with a Python environment that has `pyserial`:

```powershell
python examples/ManagedShell/Test-ManagedShellHardware.py --port COM4 --cycles 100 --quick-exit-cycles 100 --report artifacts/correctness-review/device/lifecycle.json
```

The runner uses 115200 baud and never flashes, formats, changes credentials, or writes user files. It records allocator and overlay completion for each cycle, exercises quota rejection and both termination paths every ten cycles, and checks heap integrity, managed payload quotas, task counts, and module cleanup. The optional quick-exit prelude repeats quota failures to check foreground assignment while process cleanup is still in progress. It fills the bounded shell history with harmless commands before measurement. Runs of at least 30 cycles compare the median idle heap in cycles 11–20 with the final ten cycles and allow at most 512 bytes of loss. The report also retains every individual sample and the initial baseline. JSON progress survives a failed run. Raw serial evidence stays beside the report.

The 2026-09-05 correctness follow-up completed 100 automated allocator/overlay cycles, 100 rapid quota exits, ten cooperative cancellations, and ten forced terminations. Heap integrity stayed valid and all test processes and modules cleared. Diagnostic task count stayed at 17; early and late idle-heap medians were 112,640 and 112,634 bytes. The separately linked unity overlay package passed three additional physical runs. See [the correctness report](../../CORRECTNESS_REVIEW.md) for artifacts and test boundaries.

A manual serial-terminal acceptance on the connected ESP32-D0WDQ6-V3 on 2026-09-02 completed a 16-cycle warm-up followed by 100 load/run/exit/unload cycles. Every application returned its argument count, every reload observed independent static state, the module registry returned to zero, task counts returned to zero, and free heap remained exactly 167,000 bytes across the measured 100-cycle interval. This is historical pre-diagnostics evidence; the runtime-statistics build has different memory overhead.

The native diagnostics implementation was accepted on the same board on 2026-09-02 while it was temporarily exposed as shell built-ins. Idle, listening, CPU-active, normal-exit, and termination cases passed; the burn process measured 93.2% of one core, cancellation returned `-1`, normal exit returned `0`, attributed managed payload returned to zero, the module registry returned to zero, and heap integrity remained `ok`. After warming all 32 history entries, 100 consecutive reports left current free heap unchanged at exactly 129,632 bytes. The lifetime minimum fell from 125,404 to 124,372 bytes because of transient reporter activity. These figures validate the reporter internals but are not acceptance evidence for the final standalone `memory.ctm` and `taskmgr.ctm` packaging.

Managed modules are trusted native code. There is no MMU boundary: unsafe pointers, ESP-IDF calls, MMIO, callbacks, interrupts, or native resources that escape runtime accounting can corrupt the firmware and make forced reclamation unsafe.
