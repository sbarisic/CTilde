# C~

C~ is an experimental C#-inspired systems language for the Fishmachine virtual machine.

The current compiler reads prototype C~ source, builds an abstract syntax tree, and emits FishAsm text. The repository also contains an early C source backend.

> [!WARNING]
> The compiler predates the draft C#-style specification. The included demonstration compiles, but many features are incomplete or generate incorrect FishAsm.

## Project status

The standalone compiler currently supports a small demonstration path. It is not ready for general programs or production use.

Known correctness problems include pointer handling, argument order, byte-sized local variables, conditional branches, and loop control. See [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) for the full support matrix and repair plan.

The newer Fishmachine repository contains a separate C~ copy with later language work. The two copies are not synchronized. A future revision must select one canonical compiler source.

## Documentation

- [LANGUAGE.md](LANGUAGE.md) defines the proposed C#-style C~ language.
- [ARCHITECTURE.md](ARCHITECTURE.md) describes the compiler pipeline and FishAsm backend.
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) lists supported features, defects, and roadmap priorities.
- [CTilde/todo.md](CTilde/todo.md) tracks the original single-assignment idea and its draft `readonly` design.

## Requirements

- Windows
- A .NET SDK or Visual Studio installation that can build .NET Framework 4.8 projects
- The .NET Framework 4.8 targeting pack

The repository uses classic MSBuild project files. It does not use NuGet packages.

## Build

Run this command from the repository root:

```powershell
dotnet build .\CTilde.sln
```

The build creates these main files in `bin`:

- `CTilde.dll` contains the compiler library.
- `Test.exe` contains the command-line demonstration harness.
- `tests/FishAsm.c` contains the default C~ demonstration program.

## Compile the demonstration

Run these commands after the build:

```powershell
Set-Location .\bin
.\Test.exe .\tests\FishAsm.c
```

The harness writes `out.asm` in the current directory. It also prints parser debug tokens to standard output.

The harness does not assemble or run the generated FishAsm. Use the separate [Fishmachine](https://github.com/sbarisic/Fishmachine) project for the assembler and virtual machine.

## Design target example

```csharp
using Fishmachine.Runtime;

namespace Examples;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        uint result = 2 + 3;
        FishVm.Syscall(2, result);
        FishVm.Stop();
    }
}
```

This example follows the draft specification. The current compiler does not accept the complete example yet.

## Compiler library example

```csharp
using CTilde;
using CTilde.FishAsm;
using CTilde.Langs;

var tokenizer = new Tokenizer("program.ct");
var parser = new Parser(tokenizer);
var state = new FishCompileState();
var backend = new FishAsmProvider(state);

backend.Compile(parser.Parse());
string assembly = backend.CompileToSource();
```

`Tokenizer(string)` treats the string as a file path. Use the `TextReader` constructor to compile source held in memory.

## Repository layout

| Path | Purpose |
| --- | --- |
| `CTilde/` | Compiler library |
| `CTilde/Expr/` | Abstract syntax tree nodes and parser routines |
| `CTilde/FishAsm/` | FishAsm instruction names and compiler state |
| `CTilde/Langs/` | FishAsm and C source backends |
| `Test/` | Console harness and sample programs |
| `Test/vm_out.txt` | Historical Fishmachine execution trace |

## License

The project uses the Unlicense. See [LICENSE](LICENSE).
