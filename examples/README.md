# C~ examples

`Examples.sln` is the editor/discovery solution for 29 manifest-backed C~ projects. Projects are grouped by responsibility and have `ActiveCfg` mappings only, so opening the solution never builds every native target or requires every optional toolchain at once. Select one project and use its supported manifest-backed command; examples that need a companion native library, module packaging, optional toolchain, or hardware use the runner named in their README.

## Language and hosted programs

| Project | What it demonstrates | Requirements |
| --- | --- | --- |
| [Hello](Hello/Program.ct) | Smallest hosted entry point and console output | MSVC, GCC, or Clang |
| [Exceptions](Exceptions/Program.ct) | ARC, exceptions, `finally`, and deterministic `defer` cleanup | MSVC, GCC, or Clang |
| [Features](Features/Program.ct) | Classes, structs, arrays, pointers, delegates, exports, extern ownership, callbacks, and native buffers | MSVC, GCC, or Clang |
| [Object model](ObjectModel/Program.ct) | Inheritance, virtual/interface dispatch, casts, `as`, and generics | MSVC, GCC, or Clang |
| [Language tour](LanguageTour/README.md) | Embedded data, runes, lambdas, custom operators, abstract dispatch, newtypes, unions, explicit layout, compile-time selection, and `do` loops | x64 MSVC, GCC, or Clang |
| [Standard library](StandardLibrary/README.md) | Strings, formatting, parsing, UTF-8, I/O, time, random generation, and threading | x64 MSVC, GCC, or Clang |
| [Collections and geometry](CollectionsAndGeometry/README.md) | Generic result values, array algorithms, all mutable collections, enumerator versioning, vectors, matrices, and quaternions | x64 MSVC, GCC, or Clang |
| [Hosted native import](HostedNativeImport/README.md) | A real stateful C DLL/`.so` loaded through typed `[NativeImport]` slots | MSVC and/or WSL GCC/Clang |
| [Hosted I/O](HostedIo/README.md) | Multi-file path tracer, Raylib native imports, SIMD packets, threads, files, and benchmarking | x64 plus Raylib |

## Systems targets

| Project | What it demonstrates | Requirements |
| --- | --- | --- |
| [Inline Assembly](InlineAssemblyWindows/Program.ct) | Typed GNU extended assembly from Windows through WSL GCC | x64 WSL GCC |
| [Cosmopolitan](Cosmopolitan/README.md) | One x64 Actually Portable Executable running on Windows and Linux | Cosmopolitan 4.x toolchain |
| [Freestanding](Freestanding/README.md) | Explicit allocate/free/panic providers, startup assembly, linker script, and native export | x64 WSL GCC |
| [QEMU Freestanding](QemuFreestanding/README.md) | C~-owned Multiboot header, constant image data, assembly functions, console provider, and naked start | WSL GCC multilib and QEMU |

## ESP-IDF and managed modules

| Project | What it demonstrates | Requirements |
| --- | --- | --- |
| [Managed Shell Firmware](ManagedShell/README.md) | Firmware supervision plus size-gated `shell.ctm` UART/SSH/exec sessions, LittleFS and removable SD/FAT VFS, redirected processes, inferred overlays, status LED, and unload | ESP-IDF 6 and ESP32 |
| [Managed Shell Application](ManagedShell/Modules/Shell/Program.ct) | One managed ANSI editor, parser, command router, foreground ownership policy, and cold command overlays shared by UART and SSH | ESP-IDF 6 Xtensa toolchain |
| [Managed Hello Module](ManagedShell/Modules/Hello/Program.ct) | A `.ctm` application with arguments, process-local mutable statics, copied-message receive, cooperative cancellation, and safe CPU-load mode | ESP-IDF 6 Xtensa toolchain |
| [Managed Memory Tool](ManagedShell/Modules/Memory/Program.ct) | A separate `memory.ctm` application with module-local native reporting for RAM, allocator, process, module, task, and LittleFS diagnostics | ESP-IDF 6 Xtensa toolchain |
| [Managed Task Manager](ManagedShell/Modules/TaskManager/Program.ct) | A separate `taskmgr.ctm` application with module-local native sampling for process CPU, heap, threads, stack headroom, and termination | ESP-IDF 6 Xtensa toolchain |
| [Managed SD Tool](ManagedShell/Modules/Sd/Program.ct) | A separate `sd.ctm` application for card status, identity, mount selection, explicit formatting, and validated four-entry MBR management through a versioned firmware bridge | ESP-IDF 6 Xtensa toolchain |
| [Managed Nano Editor](ManagedShell/Modules/Nano/Program.ct) | A standalone `nano.ctm` full-screen ANSI editor with strict UTF-8, a 32 KiB gap buffer, bracketed paste, and recovery-safe file replacement | ESP-IDF 6 Xtensa toolchain |
| [Managed Filesystem Commands](ManagedShell/Modules/FsCommands/Program.ct) | The load-on-demand `commands.fs.ctm` implementation behind the shell's extensionless recursive `mkdir` and strict UTF-8 `cat` aliases | ESP-IDF 6 Xtensa toolchain |
| [Managed Network Tool](ManagedShell/Modules/Net/Program.ct) | `net.ctm` Wi-Fi station status, scan, profile-based connection, disconnect, and readiness through a resident state owner | ESP-IDF 6 Xtensa toolchain |
| [Managed SSH Library](ManagedShell/Modules/SystemSsh/Protocol.ct) | Managed SSH framing, Curve25519/P-256/AES-GCM transport, public-key authentication, session channels, shared-shell launch, and rooted SFTP v3 with cold overlays | ESP-IDF 6 Xtensa toolchain |
| [Managed SSH Service](ManagedShell/Modules/Sshd/Program.ct) | Size-profiled `sshd.ctm` consuming `system.ssh.ctmeta.json` without provider source; protocol, mailbox control, and SFTP remain in the partitioned unloadable library | ESP-IDF 6 Xtensa toolchain |
| [Managed Overlay Library](ManagedShell/Modules/OverlayLibrary/Library.ct) | A separately packaged schema-3 library with resident stubs, overlay bodies, process-local state, and a throwing managed export | ESP-IDF 6 Xtensa toolchain |
| [Managed Overlay Acceptance](ManagedShell/Modules/OverlayFixture/Program.ct) | Nested local and cross-library overlay transitions, same-overlay direct calls, delegates, cleanup, exception propagation, and subsequent-call recovery | ESP-IDF 6 Xtensa toolchain |
| [T-CAN Hardware](TCan485/README.md) | Physical ESP32 peripherals, generated bindings, runtime services, debugger acceptance, and Wi-Fi opt-in | ESP-IDF 6 and T-CAN485 |
| [T-CAN QEMU ESP32](TCan485/README.md) | Xtensa ESP-IDF runtime and language fixture under QEMU | ESP-IDF 6 QEMU |
| [T-CAN QEMU ESP32-C3](TCan485/README.md) | RISC-V ESP-IDF runtime and language fixture under QEMU | ESP-IDF 6 QEMU |

Managed Module ABI 3 is ESP-IDF-only. Named overlays are supported only by Xtensa managed applications and libraries. The hosted target loads ordinary native C libraries through `[NativeImport]`; it does not host managed C~ `.ctm` files or share ARC objects across desktop modules.

Repository source modules are covered by conformance tests instead of a normal solution project. Their exact Git revisions and cache/vendor lifecycle are intentionally explicit, and a clean `Examples.sln` checkout must not require network access. A full-service freestanding backend example for file, directory, clock, math, thread, mutex, and TLS roles remains future work; the two current freestanding examples demonstrate the minimum provider subsets they actually use.

Run the hosted/MSVC catalog smoke from the repository root with `./Test/Test-ExampleCatalog.ps1`. It compares the complete deterministic output of both focused hosted tours, executes the MSVC native-import plug-in, and checks the ManagedShell firmware and managed module projects. Add `-IncludeEspIdfBuild` (and, if needed, `-IdfPath <path>`) to build the catalogued `.ctm` files, deterministic metadata, and firmware. Setting `CTILDE_EXAMPLE_ESP_IDF_BUILD=1` enables the same lane inside the Release validation runner; it uses `IDF_PATH` or the checked default installation path. SSH hardware and interoperability acceptance and destructive SD-card tests remain separate.
