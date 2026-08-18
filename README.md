# C~

C~ is a small, statically typed systems language with C#-style syntax. The compiler accepts `.ct` source files and emits one self-contained GNU C23 translation unit. GCC-compatible extensions are enabled by default for hosted and embedded toolchains.

The current language is draft 0.4. It includes single class inheritance, virtual dispatch, `System.Object`, boxing, checked casts, and all earlier language features. It does not require a CLR or C# runtime.

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
ctilde <input.ct>... -o <program.c> [--check] [--trace]
ctilde --compile-directory <directory> [--trace]
```

- `-o` selects the generated C file.
- `--check` parses and checks the program without writing C.
- `--trace` reports compiler phase progress to standard error.
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

See [examples/Features.ct](examples/Features.ct) for classes, properties, overloads, structures, enums, loops, strings, and unsafe pointers.

## Public compiler API

```csharp
var tree = SyntaxTree.Parse(SourceText.From(source, "program.ct"));
var compilation = Compilation.Create(new[] { tree });

using var output = new StringWriter();
EmitResult result = compilation.EmitC(output);
```

`Compilation.GetDiagnostics()` returns structured diagnostics without requiring emission. Each diagnostic has a stable code, severity, message, file, line, column, and optional related location.

The full-fidelity syntax API intentionally breaks the prototype node API. Tokens expose trivia, missing-token state, `Span`, and `FullSpan`. Nodes expose `ChildNodesAndTokens()` and exact `ToFullString()` output.

## Projects

| Project | Purpose |
| --- | --- |
| `CTilde` | Lexer, parser, semantic analysis, lowering, and GNU C23 emission |
| `CTilde.Cli` | The `ctilde` command-line compiler |
| `Test` | Compiler and native C conformance runner |
| `examples` | Checked draft 0.4 programs |

## Validation

```powershell
dotnet build .\CTilde.sln
dotnet run --project .\Test\Test.csproj --no-build
```

The native tests discover Visual Studio C tools on Windows. Set `CTILDE_CC` to test another compiler:

```powershell
$env:CTILDE_CC = "clang"
dotnet run --project .\Test\Test.csproj
```

Use `wsl:gcc` or `wsl:clang` to run the GNU compiler through WSL. Set `CTILDE_C_STANDARD` to force a dialect.

The driver uses `gnu23` first and retries with `gnu2x` only when the compiler rejects the option. MSVC uses `/std:clatest /W4 /WX` as a compatibility check.

## Documentation

- [LANGUAGE.md](LANGUAGE.md) is the normative draft 0.4 language specification.
- [STDLIB.md](STDLIB.md) specifies the bundled standard-library API and runtime behavior.
- [ARCHITECTURE.md](ARCHITECTURE.md) describes the compiler phases and ownership boundaries.
- [C_ABI.md](C_ABI.md) defines generated C layouts, names, initialization, and interop.
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) records the measured feature and validation status.
- [TODO.md](TODO.md) defines planned work and acceptance criteria, including ESP-IDF target support.

## License

See [LICENSE](LICENSE).
