# C~

C~ is a small, statically typed systems language with C#-style syntax. The compiler accepts `.ct` source files and emits one self-contained, portable C11 translation unit.

The current language is draft 0.3. It includes namespaces, classes, structures, enumerations, properties, overloads, arrays, immutable UTF-8 strings, structured control flow, checked managed access, and explicit unsafe pointers. It does not require a CLR or a C# runtime.

## Quick start

Build the .NET 10 solution:

```powershell
dotnet build .\CTilde.sln
```

Compile the example to C:

```powershell
dotnet run --project .\CTilde.Cli -- .\examples\Hello.ct -o .\bin\hello.c
```

Compile the generated file with a C11 compiler. For MSVC:

```powershell
cl /std:c11 /W4 /WX /Fe:.\bin\hello.exe .\bin\hello.c
.\bin\hello.exe
```

The program prints:

```text
5
```

The CLI accepts multiple input files as one compilation:

```text
ctilde <input.ct>... -o <program.c> [--check] [--trace]
```

- `-o` selects the generated C file.
- `--check` parses and checks the program without writing C.
- `--trace` reports compiler phase progress to standard error.

The compiler writes no output file when an error is present.

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

## Projects

| Project | Purpose |
| --- | --- |
| `CTilde` | Lexer, parser, semantic analysis, lowering, and C11 emission |
| `CTilde.Cli` | The `ctilde` command-line compiler |
| `Test` | Compiler and native C conformance runner |
| `examples` | Checked draft 0.3 programs |

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

GCC and Clang are invoked with `-std=c11 -Wall -Wextra -Werror -pedantic`. MSVC is invoked with `/std:c11 /W4 /WX`.

## Documentation

- [LANGUAGE.md](LANGUAGE.md) is the normative draft 0.3 language specification.
- [ARCHITECTURE.md](ARCHITECTURE.md) describes the compiler phases and ownership boundaries.
- [C_ABI.md](C_ABI.md) defines generated C layouts, names, initialization, and interop.
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) records the measured feature and validation status.

## License

See [LICENSE](LICENSE).
