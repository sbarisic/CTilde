# C~

C~ is a small systems language with C#-style syntax. It compiles `.ct` files to deterministic GNU C23 and native programs. Generated programs use the C~ runtime. They do not require the CLR.

Draft 0.46 adds `System.Storage`, a T-CAN485 SDSPI/FatFs/VFS backend, removable-media generations, and Runtime ABI 19 filesystem services for ESP-IDF managed applications. Managed Module ABI 1, the firmware-owned process host, deterministic dependency preflight, and per-process mutable state remain unchanged. Debug metadata remains version 3.

C~ is experimental. [LANGUAGE.md](LANGUAGE.md) is the normative specification.

## Language examples

### Hello world

```csharp
using System;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        Console.WriteLine("Hello from C~");
    }
}
```

Save the program as `hello.ct`, then build and run it:

```powershell
dotnet run --project .\CTilde.Cli -- .\hello.ct --build
.\build\program.exe
```

The program prints `Hello from C~`. Linux and macOS use `./build/program` for the last command.

### ARC, exceptions, and deterministic cleanup

```csharp
using System;

public sealed class Counter
{
    public int Value;
    public Counter(int value) { Value = value; }
}

public static class Program
{
    private static int Read(Counter counter)
    {
        defer Console.WriteLine("leaving Read");
        if (counter == null)
        {
            throw new ArgumentException();
        }
        return counter.Value;
    }

    [EntryPoint]
    public static void Main()
    {
        Counter counter = new Counter(42);
        Console.WriteLine(Read(counter));
    }
}
```

Managed values use non-moving ARC. Acyclic values are reclaimed when their last owned reference is released. `defer` runs in last-in, first-out order on ordinary control-flow and exception paths.

### Lambdas, `double`, and `rune`

```csharp
using System;

public delegate double Transform(double value);

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        double offset = 0.5d;
        Transform adjust = [offset] value => Math.Sqrt(value) + offset;

        rune marker = r'λ';
        Console.Write(marker);
        Console.Write(": ");
        Console.WriteLine(adjust(9.0d));
    }
}
```

Lambda captures are explicit. Captured managed values live in an ARC-managed closure. Binary64 operations use native IEEE-754 `double` semantics. A `rune` stores one Unicode scalar.

### Fixed-width SIMD

```csharp
using System;
using System.Simd;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        F32x4 values = F32x4.Create(1.0f, 2.0f, 3.0f, 4.0f);
        F32x4 result = values * F32x4.Splat(2.0f);
        Console.WriteLine(result.GetLane<3>());
    }
}
```

SIMD lane values always use 16-byte storage. Scalar lowering is the portable default. Set `cpuFeatures: ["simd128"]` for explicit supported-target intrinsic lowering, or set top-level `simdOptimizations: true` to optimize scalar geometry in hosted x64 applications and implicitly select SIMD128. `Vec3x4` stores three `F32x4` components in exactly 48 bytes.

The [example catalog](examples/README.md) groups 20 editor projects by language/hosted, systems-target, managed-module, and ESP-IDF responsibilities. The focused [language tour](examples/LanguageTour/README.md) covers embedded assets, runes, lambdas, operators, abstract dispatch, and native data layouts. The [collections and geometry tour](examples/CollectionsAndGeometry/README.md) exercises the generic containers and scalar math surface, while the [hosted path tracer](examples/HostedIo/README.md) remains the larger multi-file program.

## Build the compiler

Install the .NET 10 SDK and one supported native toolchain:

- MSVC, GCC, or Clang for hosted programs.
- ESP-IDF 6 for ESP32 projects.
- GNU-compatible ELF tools for freestanding images.
- Cosmopolitan `cosmocc` for x86-64 APEs.

Build the compiler and command-line tooling:

```powershell
dotnet build .\CTilde.sln --nologo
```

The compiler can stop after C emission:

```powershell
dotnet run --project .\CTilde.Cli -- --project .\examples\Hello\ctilde.json --build
.\examples\Hello\build\Hello.exe
```

GCC releases that lack the final C23 option can use `-std=gnu2x`.

### Format C~ source

The compiler includes a deterministic syntax-aware formatter. Write formatted source recursively with:

```powershell
ctilde format .\src
```

Use check mode in validation without changing files:

```powershell
ctilde format --check .\src
```

From the repository root, format or verify the complete physical C~ source set with:

```powershell
ctilde format CTilde CTilde.Cli editors examples
ctilde format --check CTilde CTilde.Cli editors examples
```

Repository C~ source uses UTF-8 without a byte-order mark, LF endings, four-space indentation, Allman braces, one attribute and statement per line, and a 120-column target. The formatter preserves comments, documentation, literals, template placeholders, and raw assembly text. A file containing intentionally malformed grammar-test text can opt out of syntax rewriting with `// ctilde-format: preserve`; line endings and trailing whitespace are still normalized.

Directory inputs are recursive and deterministic. The command does not follow reparse points or enter Git metadata, build outputs, dependency directories, VS Code test state, or C~ module caches. Every non-preserved input is parsed before write mode replaces any changed file, so a syntax error leaves the complete input set unchanged.

## Projects

A `ctilde.json` file defines a source set, target, build outputs, and run command:

```json
{
  "target": "hosted",
  "sources": ["src/**/*.ct"],
  "exclude": ["src/generated/**"],
  "hosted": {
    "runtimeFiles": [
      {
        "os": "windows",
        "architecture": "x64",
        "source": "native/example.dll",
        "output": "example.dll"
      }
    ]
  },
  "build": {
    "cLayout": "modules",
    "configuration": "release",
    "lto": true,
    "optimization": "speed",
    "cpuTarget": "baseline",
    "floatingPoint": "precise",
    "stackReport": "build/stack-usage.json",
    "pgo": { "mode": "off", "directory": "build/pgo" }
  },
  "run": {
    "executor": "host",
    "args": ["--verbose"]
  }
}
```

Use the same manifest for checks, builds, and runs:

```powershell
ctilde --project .\ctilde.json --check
ctilde --project .\ctilde.json --build
ctilde --project .\ctilde.json --run
```

`--run` rebuilds first and starts the configured command only after a successful build. The runner uses argument arrays without shell evaluation. It supports host and WSL executors plus `${projectRoot}` and `${buildOutput}` placeholders.

Project Build and Run commands use concise `normal` output by default: manifest, source count, target, architecture, configuration, toolchain, C layout, optimization profile, phases, artifact, elapsed time, and diagnostic totals. Select `--verbosity quiet|minimal|normal|detailed`; detailed mode also prints source files, generated-output decisions, native commands, and native tool output. `--trace` remains an independent compiler-internal diagnostic mode. Project Check and direct compilation default to `minimal` unless verbosity is selected explicitly.

Builds that share an output directory wait for its owner for up to 30 seconds. The lock records the owner process, operation, manifest, and start time; a timeout is reported as `CT6002`. Each project Check, Build, or Run atomically refreshes `.ctilde/build-diagnostics.json`. Successful compilation clears its diagnostics, failed compilation replaces them, and cancellation preserves the previous valid receipt.

Release builds can select `speed` or `aggressive` optimization, `baseline` or x64-only `avx2`, and `precise` or `fast` floating-point behavior. The matching CLI overrides are `--optimization`, `--cpu-target`, and `--floating-point`. Hosted project builds can run explicit `--pgo generate` training and `--pgo use` phases; PGO also requires Release and LTO. Omit these settings to preserve the target's historical toolchain behavior.

`build.stackReport` or `--stack-report <path>` explicitly enables schema-v1 native stack analysis for a GCC-family native build. `[StackUsage(n)]` verifies a body-bearing method's complete transitive byte bound and supplies a trusted terminal bound for extern, native-import, and assembly-only methods. Incomplete recursion, dynamic frames, indirect calls, and unannotated native boundaries remain visible as unknown rather than being guessed. MSVC and Clang stack-report requests fail before native compilation.

Hosted projects can list checked-in `.c` files in `hosted.nativeSources`; those files compile and link with generated C and Clean never deletes them. `hosted.runtimeFiles` selects explicit files by resolved OS and architecture, copies them beside a successfully linked executable, and records their hashes for safe Clean behavior. Sources are manifest-relative explicit files; destinations are filenames, not paths. Linux binaries with staged runtime files receive an `$ORIGIN` runtime search path. Clean removes only unchanged staged copies and preserves files modified after staging. A manifest with `"kind": "standard-library"` accepts only `kind`, `sources`, and `exclude`. Check and Build validate its physical declarations across the supported target matrix without producing a binary; Clean is a no-op and Run is unavailable.

An ESP-IDF managed application selects `espIdf.artifact: "managed-module"`, modular C output, and a `managedModule` identity. Optional exact `managedModule.nativeSources` compile checked-in `.c` files from the ESP-IDF `main` component into the `.ctm`; project-local quoted headers are included in the build identity but do not change the managed API hash. Missing, duplicate, external, generated, and undeclared component C files are rejected. Build emits a deterministic `.ctmeta.json` reference and an ELF `.ctm` containing the Module ABI 1 preflight manifest. The firmware-side process host searches direct-child module files in `/sd/modules` before `/storage/modules`; only absence selects the LittleFS fallback. ManagedShell invokes applications by their explicit lowercase `.ctm` names and includes standalone memory, task-manager, and SD-management applications. Their small example-owned host protocols are not part of either managed ABI. Managed module code is trusted and has accounting but no memory protection. Managed Module ABI 1 is not a hosted artifact format.

Repository modules use exact lock-file revisions. Ordinary builds do not access the network. Use explicit module commands when content is missing or must change:

```powershell
ctilde restore --project .\ctilde.json
ctilde update --project .\ctilde.json
ctilde vendor --project .\ctilde.json
```

Commit `ctilde.lock.json`. Keep the machine-local `ctilde.local.json` file untracked.

## Targets

| Target | Purpose | Details |
| --- | --- | --- |
| `hosted` | Windows, Linux, and macOS programs | [LANGUAGE.md](LANGUAGE.md) |
| `esp-idf` | ESP32-family firmware, managed modules, and generated bindings | [T-CAN485 guide](examples/TCan485/README.md), [ManagedShell](examples/ManagedShell/README.md) |
| `esp32_qemu` | Classic ESP32 firmware built for ESP-IDF QEMU | [T-CAN485 guide](examples/TCan485/README.md) |
| `esp32c3_qemu` | ESP32-C3 firmware built for ESP-IDF QEMU | [T-CAN485 guide](examples/TCan485/README.md) |
| `freestanding` | Explicit-runtime ELF images | [Freestanding guide](examples/Freestanding/README.md) |
| `cosmopolitan` | x86-64 Actually Portable Executables | [COSMOPOLITAN.md](COSMOPOLITAN.md) |

The [QEMU example](examples/QemuFreestanding/README.md) builds a 32-bit Multiboot kernel and runs it through WSL. The ESP QEMU aliases retain the ESP-IDF compilation profile while selecting an emulated execution environment and fixed chip architecture. ESP-IDF projects can generate checked C~ declarations and private C adapters from allowlisted public headers.

## Native interop and ownership

C~ supports `[Extern]`, hosted `[NativeImport]`, `[Export]`, pointers, scoped native buffers, synchronous callbacks, typed GNU assembly, assembly functions, fixed sections, linker addresses, MMIO, and explicit ownership annotations. Native imports use extensionless logical names: `foo` maps to `foo.dll` on Windows and `libfoo.so` on Linux, using the operating-system loader search path. The [hosted native-import example](examples/HostedNativeImport/README.md) builds and executes one stateful plug-in under MSVC, WSL GCC, and WSL Clang; it deliberately does not present that C ABI as managed-module loading.

The generated header exposes exported methods and Runtime ABI 19 lifecycle functions. Managed `.ctm` files use the separate Managed Module ABI 1 descriptor and bind to the firmware-owned `ct_runtime_api_v19` table. [C_ABI.md](C_ABI.md) defines the native layouts and compatibility rules.

Null access, bounds errors, invalid casts, integer division by zero, checked size overflow, and managed allocation failure are catchable exceptions on exception-capable targets. Freestanding routes these faults to its panic provider. ABI, runtime lifecycle, thread attachment, ARC corruption, and native-boundary failures are always panics.

## Visual Studio Code

The extension provides compiler diagnostics, semantic highlighting, completion, navigation, project tasks, and C~-aware debugging. It also adds **C~: Run Project** for manifest-driven rebuild-and-run workflows.

See [the extension guide](editors/vscode/README.md) for installation and debugger requirements. The extension bundles the framework-dependent compiler and language server. It requires a .NET 10 runtime.

## Visual Studio

The preview Visual Studio extension supplies TextMate and LSP editor support plus manifest-backed `.ctproj` projects. The root solutions are intentionally focused:

- `CTilde.sln` contains the compiler, CLI, language server, debug adapter, and managed tests.
- `Editors.sln` contains the three Visual Studio extension projects.
- `Examples.sln` contains 21 projects grouped as language and hosted programs, systems targets, managed modules, and T-CAN variants. It includes separate editor projects for the ManagedShell firmware, Hello application, memory tool, task manager, and SD tool.
- `CTilde.StandardLibrary.sln` contains the physical standard-library project.

The `.ctproj` entries in the example and standard-library solutions have solution configuration mappings but are excluded from Build Solution. Select one in Solution Explorer to use Check, Build, Clean, Rebuild, or Run with its exact manifest. VS Code remains an independent npm workspace under `editors/vscode`.

See [the Visual Studio extension guide](editors/visualstudio/README.md). Version 0.15.0 supports hosted launch debugging with explicitly configured GCC, Clang, or WSL-GCC plus owned Debug Launch sessions for `esp32_qemu` and `esp32c3_qemu`. Attach and physical ESP debugging remain out of scope.

## Compiler API

```csharp
var tree = SyntaxTree.Parse(SourceText.From(source, "program.ct"));
var compilation = Compilation.Create(
    new[] { tree },
    new CompilationOptions(CompilationTarget.Hosted));

var diagnostics = compilation.GetDiagnostics();
using var output = new StringWriter();
EmitResult result = compilation.EmitC(output);
```

The API also emits modular bundles, public headers, symbol maps, and version-3 debug maps. `LanguageServiceSnapshot` supplies editor-neutral language queries.

## Documentation

- [LANGUAGE.md](LANGUAGE.md): normative Draft 0.46 language and native-build rules.
- [STDLIB.md](STDLIB.md): standard-library APIs and runtime behavior.
- [C_ABI.md](C_ABI.md): generated C, Runtime ABI 19, Managed Module ABI 1, and native interop.
- [ARCHITECTURE.md](ARCHITECTURE.md): compiler phases and ownership boundaries.
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md): measured implementation and validation status.
- [TODO.md](TODO.md): outstanding work only.
- [FUTURE_FEATURES.md](FUTURE_FEATURES.md): historical design record for Drafts 0.26 through 0.34.
- [COSMOPOLITAN.md](COSMOPOLITAN.md): APE target design and acceptance stages.
- [examples/README.md](examples/README.md): categorized runnable examples, prerequisites, and deliberate coverage boundaries.

## Validation

`Test/Test-ExampleCatalog.ps1` compares the complete output of the focused hosted tours, checks the ManagedShell firmware plus its Hello, memory, task-manager, and SD module projects, and executes the native-import example. `-IncludeEspIdfBuild` adds all four `.ctm` files, their metadata, and firmware packaging when ESP-IDF is installed; `CTILDE_EXAMPLE_ESP_IDF_BUILD=1` enables that lane through the Release validation tier. The ordinary portable smoke remains ahead of the larger compiler and SIMD matrices.

The fixed fast gate builds managed projects once, runs every conformance case under MSVC, repeats only toolchain-sensitive cases under WSL GCC and Clang, and reuses those outputs for the managed editor tests:

```powershell
.\Test\Test-Validation.ps1
```

Use `-Tier Release` to add the reduced native-import and HostedIo/SIMD matrices, bundled and minimum-version VS Code extension-host tests, formatting, and diff checks. QEMU, Cosmopolitan, ESP-IDF, connected hardware, full VSIX packaging, and `Test-HostedSimd.ps1` remain explicit target, packaging, or benchmark gates rather than ordinary hosted validation.

See [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) for measured host, WSL, ESP-IDF, QEMU, and Cosmopolitan results.

## License

C~ is released under the [Unlicense](LICENSE).
