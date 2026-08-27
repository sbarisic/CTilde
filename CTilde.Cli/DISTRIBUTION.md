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

The Draft 0.24 Cosmopolitan target uses an external official `cosmocc` toolchain. CTilde does not bundle or download its roughly 421 MiB release archive during compilation, install a systemwide APE loader, or call unsupported physical `*-linux-cosmo-gcc` executables directly. The first target requires the supported `x86_64-unknown-cosmo-cc` wrapper and matching `x86_64-linux-cosmo-objcopy`; see [the repository design](../COSMOPOLITAN.md).

Prepare a hosted executable and machine-local debugger descriptor:

```text
ctilde --project path/to/ctilde.json --prepare-debug launch --debug-target build/ctilde-debug-target.json
```

This forces an instrumented Debug compilation, emits C~ source mappings and version-2 `ctilde_debug.json`, disables LTO, and records the selected GDB or MSVC backend. ARC object tracking is enabled by default. Select another launch-only mode with `--debug-memory off|objects|guarded`; guarded mode adds canaries, released-memory poisoning, and a bounded quarantine. Reuse the artifacts only when sources still match:

```text
ctilde --project path/to/ctilde.json --prepare-debug attach --debug-target build/ctilde-debug-target.json
```

ESP-IDF Launch also requires `--serial-port`; it validates runtime GDB-stub configuration, builds, and flashes before writing the descriptor. The instrumented application waits for the debugger for up to 15 seconds before runtime and module initialization, then starts normally if no debugger connects. Attach does not rebuild or flash and rejects non-instrumented or version-1 metadata. Ordinary Debug builds and `--debug-info` use source mappings without logical-probe or memory-diagnostic overhead. The native debugger and ESP-IDF Python/serial environment remain external dependencies.
