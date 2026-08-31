# Changelog

- Added Draft 0.39 `[NativeImport]` diagnostics, navigation, semantic metadata, and schema support for platform-selected `hosted.runtimeFiles` while retaining the Draft 0.38 hosted x64 SIMD and `Vec3x4` support.

## 0.15.0 - 2026-08-30

- Added Draft 0.37 SIMD128, matrix, quaternion, and scalar-vector helper completion, hover, signatures, semantic tokens, and embedded-source navigation.
- Aligned the compiler, language server, debug adapter, and extension package.

## 0.13.0 - 2026-08-30

- Aligned the compiler, language server, debug adapter, and extension package for C~ Draft 0.35.
- Added editor coverage for generic standard-library types, array algorithms, callback delegates, and `System.Text.Utf8`.

## 0.11.0 - 2026-08-28

- Added **C~: Run Project** and manifest-driven rebuild-and-run tasks.
- Added hosted debug defaults from `run.args`, `run.workingDirectory`, and `run.environment`.
- Added QEMU/WSL run support for freestanding projects; freestanding debugging remains unsupported.
- Updated the extension guide for Draft 0.34 and the 0.11.0 VSIX.

## 0.10.1 - 2026-08-28

- Added the official C~ website to the Marketplace Details and Resources sections.

## 0.10.0 - 2026-08-28

First Visual Studio Marketplace preview.

- Added Draft 0.25 syntax and semantic support.
- Bundled the C~ compiler and language server.
- Added project checking, native builds, and ESP-IDF binding generation.
- Added C~-aware GDB debugging for hosted, WSL, and ESP-IDF targets.
- Added source, function, log, exception, and data breakpoints.
- Added C~-level stepping, lexical locals, and ARC runtime inspection.
- Added JSON schemas for C~ projects and ESP-IDF binding manifests.
- Added explicit Workspace Trust, remote-host, and virtual-workspace policy.

## Known limitations

- References, rename, formatting, code actions, and auto-import edits are not implemented.
- The bundled compiler and language server require the .NET 10 runtime.
- MSVC debugging requires Microsoft's C/C++ extension and uses `cppvsdbg`.
- OpenOCD, JTAG, reverse execution, panic-only postmortem debugging, and ISR entry are outside the current debugger profile.
