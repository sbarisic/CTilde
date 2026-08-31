# Changelog

- Added Draft 0.39 `[NativeImport]` diagnostics, semantic metadata, and schema support for platform-selected `hosted.runtimeFiles` while retaining hosted x64 SIMD, `Vec3x4` debug presentation, and extension version 0.15.0.

## 0.15.0 - Preview

- Added solution-wide semantic Find All References and native Visual Studio reference-count CodeLens indicators with lazy details, navigation, live refresh, and an opt-out setting.
- Added Draft 0.37 SIMD128, matrix, quaternion, and scalar-vector helper completion, hover, signatures, semantic tokens, and embedded-source navigation.
- Aligned the VSIX, bundled compiler, language server, and debug adapter.

## 0.13.0 - Preview

- Aligned the VSIX, bundled compiler, language server, and debug adapter for C~ Draft 0.35.
- Added editor support for generic containers, array algorithms, callback delegates, and `System.Text.Utf8`.

## 0.12.0 - Preview

- Added Visual Studio F5 launch debugging for `esp32_qemu` and `esp32c3_qemu` through the existing .NET 10 DAP adapter.
- Added ESP-IDF and Espressif Clang options with environment and compiler-discovery fallback.
- Added manifest-specific debug descriptors and launch leases for same-directory target variants.
- Added strict ESP QEMU descriptor validation and direct target cross-GDB connections to `127.0.0.1:3333`.
- Added adapter-owned QEMU launch, Debug Console output, ready/trap synchronization, restart, stop, port-conflict reporting, and Windows Job Object process-tree cleanup.
- Added fake-process lifecycle coverage and real T-CAN485 DAP coverage for classic ESP32 and ESP32-C3.
- Hardened command routing against Visual Studio placeholder projects so an unavailable DTE `FullName` cannot terminate the IDE.
- Added and inventory-checked the private `System.Text.Json` dependency closure required by target-aware preparation inside `devenv.exe`.

Physical ESP debugging, Attach, generic CLI `--run`, and peripheral emulation remain intentionally outside this release.

## 0.11.0 - Preview

- Added TextMate and C~ language-server editor support.
- Added the `.ctproj` CPS project type backed by `ctilde.json`.
- Added Check, Build, Clean, Rebuild, cancellation, and external-console Run commands.
- Added a hosted-console template and manifest-wrapper command.
- Added versioned read-only standard-library navigation.
- Added Visual Studio options for tool paths and protocol tracing.
- Added a framework-dependent .NET 10 C~ Debug Adapter Protocol executable and registered it with Visual Studio's Debug Adapter Host.
- Added hosted GCC, Clang, and WSL-GCC F5 launch debugging with version-3 metadata validation, source hashes, C~-mapped breakpoints, stepping, stacks, variables, watches, data breakpoints, memory reads, restart, and runtime exception filters.
- Routed Visual Studio breakpoints, stepping, Run to Cursor, conditions, logpoints, and runtime events through the version-3 logical-probe control block so stopped frames and variable values match the exact next C~ statement.
- Added lexical local lifetime and shadowing filtering, per-thread frame handles, C~-named object fields, ARC object state, and an explicit logical probe description in the Runtime scope.
- Kept `Ctrl+F5` on the non-debug external-console workflow and added a shared per-project Run/Debug lease.
- Fixed the CPS launch target registration so Visual Studio enables both `F5` and `Ctrl+F5` for runnable C~ startup projects.
- Collapsed method overloads into one completion row without removing overloads from signature help.
- Fixed incomplete member-access completion and multi-file project context in non-main source files.
- Added a Visual Studio-specific TextMate classification map so methods, types, locals, literals, comments, operators, and punctuation follow the active theme's C#-style colors.

Attach plus MSVC, ESP-IDF, QEMU, freestanding, and Cosmopolitan Visual Studio debugging remain intentionally deferred.
