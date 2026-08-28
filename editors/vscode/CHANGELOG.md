# Changelog

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
