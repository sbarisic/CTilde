# C~ extension support

## Requirements

- Visual Studio Code 1.85 or newer.
- The .NET 10 runtime available through `dotnet`, or configured with the C~ `dotnetPath` settings.
- A supported native C toolchain for native builds.
- GDB for C~-aware GCC, Clang, WSL, or ESP-IDF debugging.
- The Microsoft C/C++ extension for MSVC debugging.

Install the .NET 10 runtime from <https://dotnet.microsoft.com/download/dotnet/10.0>.

## Diagnostics

Use these commands before reporting a problem:

1. Run **C~: Show Language Server Output**.
2. Run **C~: Restart Language Server**.
3. Check the **C~** task output for compiler and native-toolchain diagnostics.
4. Set `ctilde.trace.server` to `messages` or `verbose` when an LSP request fails.

Do not attach credentials, Wi-Fi configuration, private source code, or complete ESP-IDF environment dumps to a public issue.

## Report a problem

Use <https://github.com/sbarisic/CTilde/issues>. Include:

- extension version;
- Visual Studio Code version and operating system;
- target and native toolchain;
- a minimal C~ source or project manifest when possible;
- the relevant C~ output, with private paths and secrets removed.

Language design and general questions can use the Marketplace Q&A page or the repository issue tracker.
