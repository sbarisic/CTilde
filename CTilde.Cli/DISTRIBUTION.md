# C~ command-line compiler

`ctilde` contains the C~ compiler and standard library. It does not require an installed .NET runtime.

Build a hosted project with an installed MSVC, GCC, or Clang toolchain:

```text
ctilde --project path/to/ctilde.json --build
```

Build an ESP-IDF project from an activated ESP-IDF terminal:

```text
ctilde --project path/to/ctilde.json --build
```

Alternatively, pass `--idf-path`. On Windows, the compiler detects a matching Espressif Installation Manager PowerShell profile before trying the stock `export.ps1`, so it reuses the installation's Python environment and toolchain.

Use `ctilde --help` for generated-C, project, toolchain, and ESP-IDF options. ESP-IDF and hosted C toolchains are external dependencies and are not included in this archive.

Prepare a hosted executable and machine-local debugger descriptor:

```text
ctilde --project path/to/ctilde.json --prepare-debug launch --debug-target build/ctilde-debug-target.json
```

This forces Debug compilation, emits C~ source mappings and `ctilde_debug.json`, disables LTO, and records the selected GDB or MSVC backend. Reuse the artifacts only when sources still match:

```text
ctilde --project path/to/ctilde.json --prepare-debug attach --debug-target build/ctilde-debug-target.json
```

ESP-IDF Launch also requires `--serial-port`; it validates runtime GDB-stub configuration, builds, and flashes before writing the descriptor. Attach does not rebuild or flash. The native debugger and ESP-IDF Python/serial environment remain external dependencies.
