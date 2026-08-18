# C~

C~ is a small, statically typed systems language with C#-style syntax. The compiler accepts `.ct` source files and emits one GNU C23 translation unit for either a hosted process or an ESP-IDF component. GCC-compatible extensions are enabled by default.

The current language is draft 0.5. It adds unchecked exceptions, typed and catch-all handlers, rethrow, and finally cleanup to the draft 0.4 object model. It does not require a CLR or C# runtime.

## Quick start

Build the .NET 10 solution:

```powershell
dotnet build .\CTilde.sln
```

Compile the example to C:

```powershell
dotnet run --project .\CTilde.Cli -- .\examples\Hello.ct -o .\bin\hello.c
```

Compile the generated file with GCC or Clang:

```powershell
gcc -std=gnu23 -Wall -Wextra -Werror -o .\bin\hello.exe .\bin\hello.c
.\bin\hello.exe
```

Older GCC and Clang versions can use `-std=gnu2x`. The conformance driver retries after an unsupported `gnu23` option.

MSVC remains supported as a compatibility toolchain through its latest C mode:

```powershell
cl /std:clatest /W4 /WX /Fe:.\bin\hello.exe .\bin\hello.c
.\bin\hello.exe
```

The program prints:

```text
5
```

The CLI accepts multiple input files as one compilation:

```text
ctilde <input.ct>... -o <program.c> [--target hosted|esp-idf] [--check] [--trace]
ctilde --compile-directory <directory> [--target hosted|esp-idf] [--trace]
```

- `-o` selects the generated C file.
- `--check` parses and checks the program without writing C.
- `--trace` reports compiler phase progress to standard error.
- `--target` selects `hosted` by default or emits an ESP-IDF `app_main` profile.
- `--compile-directory` compiles each top-level `.ct` file independently and writes a same-named `.c` file beside it.

Running `CTilde.Cli` from Visual Studio uses `--compile-directory data/programs --trace`, so every file in `CTilde.Cli/data/programs` is compiled automatically.

The compiler atomically replaces output after successful emission. Directory mode removes stale generated output after an error. It identifies generated files by their banner and preserves handwritten C.

## Language example

```csharp
using System;

namespace Examples;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        int[] values = new int[3];
        values[0] = 2;
        values[1] = 3;
        values[2] = 4;

        int total = 0;
        foreach (int value in values)
        {
            total += value;
        }

        Console.WriteLine(total);
    }
}
```

See [examples/Features.ct](examples/Features.ct) for the general language surface. [examples/ObjectModel.ct](examples/ObjectModel.ct) covers inheritance, virtual dispatch, constructor chaining, casts, and boxing. [examples/Exceptions.ct](examples/Exceptions.ct) covers typed catch and finally cleanup.

## Public compiler API

```csharp
var tree = SyntaxTree.Parse(SourceText.From(source, "program.ct"));
var compilation = Compilation.Create(
    new[] { tree },
    new CompilationOptions(CompilationTarget.EspIdf));

using var output = new StringWriter();
EmitResult result = compilation.EmitC(output);
```

`Compilation.GetDiagnostics()` returns structured diagnostics without requiring emission. Each diagnostic has a stable code, severity, message, file, line, column, and optional related location.

The full-fidelity syntax API intentionally breaks the prototype node API. Tokens expose trivia, missing-token state, `Span`, and `FullSpan`. Nodes expose `ChildNodesAndTokens()` and exact `ToFullString()` output.

Omit `CompilationOptions` to retain hosted output.

## ESP-IDF quick start

ESP-IDF 6 builds the same generated C for Xtensa and RISC-V chips. The compiler does not select a chip, link, flash, or monitor; `idf.py` owns those operations.

```powershell
cd .\examples\TCan485
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

The checked T-CAN485 project includes the fixed-width `Esp.Idf` shim, UART0 configuration, an 8 KiB main-task stack, an RMT-driven WS2812 on GPIO4, heap and stack reporting, and an object/exception self-test. See [the T-CAN485 example](examples/TCan485/README.md) for the failure test and current runtime limits.

## Projects

| Project | Purpose |
| --- | --- |
| `CTilde` | Lexer, parser, semantic analysis, lowering, and GNU C23 emission |
| `CTilde.Cli` | The `ctilde` command-line compiler |
| `Test` | Compiler and native C conformance runner |
| `examples` | Checked draft 0.5 programs |
| `examples/TCan485` | T-CAN485 ESP-IDF hardware project and native API shim |
| [`editors/vscode`](editors/vscode) | Visual Studio Code syntax highlighting and editor configuration |

## Validation

```powershell
dotnet build .\CTilde.sln
dotnet run --project .\Test\Test.csproj --no-build
.\Test\Test-EspIdf.ps1
```

The native tests discover Visual Studio C tools on Windows. Set `CTILDE_CC` to test another compiler:

```powershell
$env:CTILDE_CC = "clang"
dotnet run --project .\Test\Test.csproj
```

Use `wsl:gcc` or `wsl:clang` to run the GNU compiler through WSL. Set `CTILDE_C_STANDARD` to force a dialect.

The driver uses `gnu23` first and retries with `gnu2x` only when the compiler rejects the option. MSVC uses `/std:clatest /W4 /WX` as a compatibility check.

## Documentation

- [LANGUAGE.md](LANGUAGE.md) is the normative draft 0.5 language specification.
- [STDLIB.md](STDLIB.md) specifies the bundled standard-library API and runtime behavior.
- [ARCHITECTURE.md](ARCHITECTURE.md) describes the compiler phases and ownership boundaries.
- [C_ABI.md](C_ABI.md) defines generated C layouts, names, initialization, and interop.
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) records the measured feature and validation status.
- [TODO.md](TODO.md) defines planned work and acceptance criteria, including ESP-IDF target support.

## License

See [LICENSE](LICENSE).
