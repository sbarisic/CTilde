# C~

C~ is a small, statically typed systems language with familiar C#-style syntax. It compiles `.ct` source to deterministic GNU C23, then optionally invokes GCC, Clang, MSVC, or ESP-IDF to produce native programs. Generated programs use a compact C runtime; they do not require the CLR or a C# runtime.

Draft 0.18 adds compile-time target queries, statement-level `static if`, file/type `static assert`, `[Used]` object retention, typed extern data, ordered and relaxed MMIO, and ESP-IDF `[TaskEntry]` exports. It retains Draft 0.17 section placement, aggregate layouts, interfaces, monomorphized generics, deterministic ARC, exceptions, concurrency, native interop, modular C output, and hosted and ESP-IDF profiles. The language is experimental and intentionally smaller than C#; [the specification](LANGUAGE.md) lists the exact supported and deferred features.

## A taste of C~

This complete program uses standard-library vectors, operator overloads, a managed array, `foreach`, string construction, and deterministic deferred cleanup:

```csharp
using System;

namespace Examples;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        defer Console.WriteLine("done");

        Vec3 direction = (Vec3.UnitX + Vec3.UnitY).Normalize();
        int[] samples = new int[3];
        samples[0] = 2;
        samples[1] = 3;
        samples[2] = 4;

        int total = 0;
        foreach (int sample in samples)
        {
            total += sample;
        }

        Console.WriteLine("samples: " + total.ToString());
        Console.WriteLine("direction: " + direction.X.ToString() + ", " + direction.Y.ToString());
    }
}
```

C~ evaluates calls and operands from left to right, automatically owns managed values with non-moving ARC, and runs the deferred call on every ordinary exit from its block. The bundled `Vec3` operators and methods are allocation-free.

For a broad executable language tour, see [examples/Features.ct](examples/Features.ct). The [hosted path tracer](examples/HostedIo/README.md) exercises the object model, virtual dispatch, vector operators, exceptions, deterministic random sampling, and owned file I/O in a larger program.

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build the compiler and an installed native toolchain when using `--build`:

- MSVC, GCC, or Clang for hosted programs.
- ESP-IDF 6 for ESP32-family projects.

Build the solution and compile the checked hello-world example:

```powershell
dotnet build .\CTilde.sln
dotnet run --project .\CTilde.Cli -- .\examples\Hello.ct --build
.\build\program.exe
```

On Linux or macOS, run the generated executable as `./build/program`. The example prints `5`.

The compiler can also stop after emitting portable C:

```powershell
dotnet run --project .\CTilde.Cli -- .\examples\Hello.ct -o .\build\hello.c
gcc -std=gnu23 -Wall -Wextra -Werror -o .\build\hello .\build\hello.c
```

GCC versions that have not adopted the final C23 option spelling can use `-std=gnu2x`; the CLI retries that spelling automatically when its discovered GNU compiler rejects `gnu23`.

### Windows x64 inline assembly

The checked [Windows x64 inline-assembly example](examples/InlineAssemblyWindows.ct) implements separate add, subtract, multiply, increment, negate, and rotate-left functions with typed GNU assembly operands. It requires a native Windows x64 GCC or Clang installation on `PATH`; MSVC does not support C~ `asm` programs.

Build and run it with MinGW-w64 GCC:

```powershell
dotnet run --project .\CTilde.Cli -c Release --no-launch-profile -- .\examples\InlineAssemblyWindows.ct --build --compiler gcc --configuration release --native-output .\build\inline-assembly-windows.exe
.\build\inline-assembly-windows.exe
```

Pass `--compiler clang` instead to use Clang. The example uses GNU AT&T x86 assembly, an early-clobber output constraint, and an explicit condition-code clobber. It is not portable to Windows on ARM64 or to the MSVC backend.

## What the language provides

- C#-style namespaces, classes, structures, enums, constructors, properties, overloads, and single inheritance.
- Fixed-width and native-width integers, checked arrays, immutable UTF-8 strings, pointers, native buffers, and stack allocation.
- Virtual dispatch, boxing, named single-cast delegates, unmanaged function pointers, and user-defined `+`, `-`, `*`, and `/` operators.
- Typed exceptions, `try`/`catch`/`finally`, deterministic `defer`, and catchable allocation-free runtime faults.
- Non-moving atomic reference counting with deterministic destruction of acyclic managed values. Reference cycles intentionally leak.
- Explicit native contracts through attributes such as `[Extern]`, `[Export]`, `[Section]`, `[NoAlloc]`, and ownership annotations.
- Raw GNU inline assembly with typed operands for GCC and Clang builds. Programs containing `asm` are rejected by the MSVC native-build path.

The bundled standard library supplies objects and exceptions, console I/O, single-precision math and vectors, runtime memory operations, hosted binary file I/O, and a small target-specific ESP-IDF surface. See [STDLIB.md](STDLIB.md) for signatures and runtime behavior.

## Ownership, failures, and native boundaries

Managed objects, arrays, strings, boxes, and reference-bearing structures use automatic reference counting. Parameters are borrowed; managed results are owned; fields, array slots, locals, and temporaries retain or transfer values as required. `defer` also provides deterministic cleanup for move-only native resources.

Null access, bounds errors, invalid casts, integer division by zero, checked size overflow, invalid arguments, and managed allocation failure are ordinary catchable exceptions backed by allocation-free runtime objects. ABI, lifecycle, attachment, ARC-corruption, and native-boundary violations are panics. Each attached native thread has independent exception, cleanup, and release state.

The generated native header exposes `[Export]` methods plus the ABI 16 runtime lifecycle, thread attachment, retain, and release operations. [C_ABI.md](C_ABI.md) defines the generated layouts and interop contract.

## Files, projects, and native builds

Compile one or more files directly, or use a `ctilde.json` manifest when several files form one program:

```json
{
  "target": "hosted",
  "sources": ["src/**/*.ct"],
  "exclude": ["src/generated/**"],
  "build": {
    "cLayout": "modules",
    "configuration": "release",
    "lto": true
  }
}
```

```powershell
dotnet run --project .\CTilde.Cli -- --project .\ctilde.json --check
dotnet run --project .\CTilde.Cli -- --project .\ctilde.json --build
```

Project globs and generated paths are deterministic and confined to the manifest directory. Unity output is one self-contained C file. Modular output contains shared headers, a runtime source, reachable namespace sources, an entry source, a versioned symbol map, and a CMake source fragment.

Useful CLI workflows include:

```text
ctilde <input.ct>... --check
ctilde <input.ct>... -o <program.c> [--header <exports.h>]
ctilde <input.ct>... --build [--compiler auto|msvc|gcc|clang]
ctilde --project <ctilde.json> [--check|--build]
ctilde --project <ctilde.json> --generate-bindings [--esp-clang <path>]
ctilde --project <ctilde.json> --verify-bindings [--esp-clang <path>]
ctilde --project <ctilde.json> --prepare-debug launch --debug-target <target.json> --debug-memory objects
ctilde --project <ctilde.json> --prepare-debug attach --debug-target <target.json>
```

Run `ctilde --help` for modular-layout, reproducible-path, toolchain, LTO, ESP-IDF, and directory-compilation options. Native builds write generated files atomically only when their bytes change, lock their build directory, and never invoke the native toolchain after a C~ error. `--trace` reports changed outputs, binding-cache decisions, and compiler/native phase timings.

Publish a self-contained command-line compiler that does not require an installed .NET runtime:

```powershell
.\CTilde.Cli\Publish.ps1 -Runtime win-x64
```

The published compiler still requires an external hosted C toolchain or ESP-IDF to build native output.

## ESP-IDF

The ESP-IDF target emits `app_main` and uses the same language and GNU C23 backend as hosted builds. ESP-IDF remains responsible for chip selection, components, linking, flashing, and monitoring; C~ does not have separate per-chip backends.

ESP-IDF projects can list explicit binding manifests under `espIdf.bindings`. On a cold or invalidated build, the CLI reconfigures the selected IDF project, derives its target, macros, and include paths from the exported compile database, validates allowlisted public declarations with Espressif Clang AST JSON, and emits tracked C~ declarations plus project-private C adapters. A versioned cache under `build/.ctilde/bindings` skips reconfiguration, AST parsing, and adapter validation when manifests, public headers, `sdkconfig`, target, CMake inputs, ESP-IDF, Clang, and tracked outputs still match. Structured adapters support validated native initializers, nested fields, bounded fixed UTF-8 arrays, output structures, ordinary native parameters, and explicit opaque-return ownership. Check, Build, and debug preparation use the cache automatically. Explicit `--generate-bindings` always validates and regenerates; `--verify-bindings` accepts a cache hit only when all inputs and outputs match. Generic host Clang is never substituted for Espressif Clang.

The checked T-CAN485 project builds modular firmware for Xtensa and RISC-V targets:

```powershell
cd .\examples\TCan485
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
.\Build.ps1 -Target esp32 -Clean
```

The project covers generated timer, hardware-random, GPIO, Wi-Fi, network-interface, and HTTPS bindings alongside its existing handwritten APIs, an RMT-driven WS2812, FreeRTOS delays and counters, source-created threads, recursive locks, atomics, generic interface dispatch, attached native tasks, exports, synchronous callbacks, opaque resources, runtime failures, and ARC recovery. Its default worker-thread firmware fetches `https://example.com/` when local credentials are configured and uses an offline fallback when the SSID is empty. The [hardware guide](examples/TCan485/README.md) records configuration and physical-board validation.

## Compiler API

The compiler is also a .NET library:

```csharp
var tree = SyntaxTree.Parse(SourceText.From(source, "program.ct"));
var compilation = Compilation.Create(
    new[] { tree },
    new CompilationOptions(CompilationTarget.Hosted));

var diagnostics = compilation.GetDiagnostics();

using var output = new StringWriter();
EmitResult result = compilation.EmitC(output);
```

`GetDiagnostics()` performs analysis without initializing C emission. `EmitC()` lazily lowers the validated program and produces deterministic unity output; `EmitCBundle()`, `EmitCHeader()`, `EmitSymbolMap()`, and `EmitDebugMap()` expose modular artifacts, exported declarations, compact-name mappings, and C~-aware debug metadata. `CompilationOptions.DebugInformation` selects no debugging, source mappings, or full version-3 instrumentation. Instrumented images add logical probes and optional ARC diagnostics privately; ordinary and Release emission remains unchanged. `LanguageServiceSnapshot` provides editor-neutral completion, documentation, hover, signature, definition, symbol, diagnostic, and semantic-token queries.

## Editor support

The extension in [editors/vscode](editors/vscode/README.md) provides compiler-aware semantic highlighting, completion, diagnostics, XML-documentation hover and signature help, definitions, document and workspace symbols, project checks and native builds, C~-aware GDB debugging, and navigation into the embedded standard library. Its GDB adapter uses compiler-emitted logical probes for source, function, log, and exception breakpoints; it also provides C~-level stepping, Run to Cursor, lexical locals, hardware data watchpoints, and optional ARC object/guard inspection. It bundles the framework-dependent compiler, language server, and Node debug adapter and therefore requires an installed .NET 10 runtime.

Rename, references, formatting, code actions, auto-import edits, and semantic-token deltas are not yet implemented. MSVC uses the Microsoft C/C++ debugger as a native-variable fallback; GCC, Clang, WSL, and ESP-IDF use the C~-aware GDB adapter. Type-body completion includes arithmetic-operator declarations, while operator hover, definition, symbols, usage classification, and ordinary member filtering share the same language-service regression coverage.

## Project status

C~ is an experimental Draft 0.18 implementation, not a stable production language. Draft 0.18 retains runtime ABI 16 and debug metadata v3. Draft 0.18 compile-time and native-system facilities pass hosted conformance; ESP-IDF cross-build and connected-board evidence is recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md).

On 2026-08-23, the connected classic ESP32 completed the ABI 15 Release workload, allocation-failure and fatal-runtime images, guarded debugger-v3 matrix, detach continuation, no-debugger startup timeout, exact USB-to-UART console check, and visible LED confirmation. Both ESP32 and ESP32-C3 also pass the ABI 15 cross-build gate. See [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) for measured results and [TODO.md](TODO.md) for outstanding work.

## Repository guide

| Path | Purpose |
| --- | --- |
| `CTilde` | Syntax, semantic analysis, lowering, standard library, and GNU C23 emission |
| `CTilde.Cli` | Command-line emission and native-build driver |
| `CTilde.LanguageServer` | LSP 3.17 language server |
| `Test` | Managed, native, ABI, artifact, and language-service conformance checks |
| `examples` | Focused language programs and hosted/ESP-IDF projects |
| `editors/vscode` | VS Code client, grammar, project schema, and editor tests |

The documentation is split by purpose:

- [LANGUAGE.md](LANGUAGE.md) — normative Draft 0.18 language specification.
- [STDLIB.md](STDLIB.md) — standard-library API and runtime behavior.
- [C_ABI.md](C_ABI.md) — generated C layouts, lifecycle, symbols, and native interop.
- [ARCHITECTURE.md](ARCHITECTURE.md) — compiler phases and ownership boundaries.
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) — measured feature and validation status.
- [TODO.md](TODO.md) — concise outstanding roadmap and release blockers.

## Validation

```powershell
dotnet build .\CTilde.sln --nologo
dotnet run --project .\Test\Test.csproj --no-build
Push-Location .\editors\vscode
npm ci
npm test
npm run test:extension
Pop-Location
.\Test\Test-EspIdf.ps1
```

Set `CTILDE_CC` to a compiler name or path to exercise another hosted C compiler. `wsl:gcc` and `wsl:clang` run GNU toolchains through WSL, and `CTILDE_C_STANDARD` overrides the selected GNU dialect.

## License

C~ is released under the [Unlicense](LICENSE).
