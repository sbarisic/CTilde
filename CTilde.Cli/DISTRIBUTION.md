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

Use `ctilde --help` for generated-C, project, toolchain, and ESP-IDF options. ESP-IDF and hosted C toolchains are external dependencies and are not included in this archive.
