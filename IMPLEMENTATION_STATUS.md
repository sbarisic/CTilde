# Implementation status

Last reviewed: 2026-08-18

## Current state

C~ draft 0.3 has one compiler path:

```text
.ct source -> full-fidelity syntax -> binding -> flow -> typed IR -> target validation -> GNU C23
```

The compiler library, CLI, and conformance runner target .NET 10. The previous prototype AST, direct assembly backend, mutable backend state, and demonstration harness have been removed.

The compiler emits one self-contained C file. It does not invoke a native compiler.

## Measured baseline

The current workspace passes:

```powershell
dotnet build .\CTilde.sln --nologo
dotnet run --project .\Test\Test.csproj --no-build
```

The .NET build completes with zero warnings and zero errors. The conformance runner contains 36 managed and native checks.

Native checks discover Visual Studio 2022 C tools and compile generated files with:

```text
cl /std:clatest /W4 /WX
```

The checked feature example prints:

```text
14
4
12
6
east
2
A
10
```

The independent compiler check uses GCC 13.3.0 from Ubuntu 24.04 under WSL. That compiler uses the draft compatibility spelling for the C23 dialect:

```text
gcc -std=gnu2x -Wall -Wextra -Werror
```

It exits successfully and produces the same checked output. The runner tries `gnu23` and retries with `gnu2x` only after an unsupported-option error. `CTILDE_CC` accepts compiler paths, `wsl:gcc`, and `wsl:clang`. `CTILDE_C_STANDARD` forces one dialect.

Ubuntu Clang 18.1.3 under WSL also passes the complete suite with `-std=gnu23 -Wall -Wextra -Werror`.

## Language support

| Area | Status | Evidence |
| --- | --- | --- |
| UTF-8 files and Unicode identifiers | Implemented | Strict UTF-8 decoding and rune-based identifier categories |
| Full-fidelity tokens and trivia | Implemented | Valid and invalid exact round-trip tests |
| Comments, escapes, and numeric forms | Implemented | Lexer diagnostics and literal tests |
| File and block namespaces | Implemented | Parser and multi-file test |
| Namespace imports | Implemented | Multi-file test with imported type |
| Classes and static classes | Implemented | Native object test and feature example |
| Structures | Implemented | Native feature example |
| Enumerations and fixed underlying types | Implemented | Native enum and switch example |
| Fields and static initialization | Implemented | Native ordered-evaluation and feature tests |
| Constructors and `new` | Implemented | Class and structure native tests |
| Custom and automatic properties | Implemented | Native property tests |
| Access modifiers | Implemented | Private member and setter diagnostics |
| Method overloads | Implemented | Pairwise best-candidate and cross-argument ambiguity tests |
| `const` and delayed `readonly` | Implemented | Constant switch and branch-flow tests |
| Definite assignment and reachability | Implemented | `do`, switch, read-only, constructor, and reachability tests |
| Fixed-width numeric types | Implemented | Typed lowering and C static assertions |
| Checked arrays and `foreach` | Implemented | Native iteration and failure tests |
| Immutable UTF-8 strings | Implemented | Native concatenation, output, indexing, and length tests |
| Expression precedence | Implemented | Pratt parser and deterministic emission test |
| Calls as expressions | Implemented | Nested call and overload tests |
| Ordered evaluation | Implemented | Native `Pack(Next(), Next()) == 12` test |
| Arithmetic, logical, bitwise, shift, and comparison operators | Implemented | Integral-only remainder and typed constant folding tests |
| Assignment and compound assignment | Implemented | Native state and iteration tests |
| `if`, loops, `switch`, `break`, and `continue` | Implemented | Label lowering and native tests |
| Numeric, enum, null, and pointer conversions | Implemented | Positive and negative conversion tests |
| Unsafe address, dereference, indexing, pointer arrays, and pointer arithmetic | Implemented | Recursive unsafe checks and native example |
| `[EntryPoint]` | Implemented | Validation and native wrapper tests |
| `[Extern]` | Implemented | Reserved-name, collision, alias, ABI, and prototype tests |
| Bundled `System.Console` and `System.Environment` sources | Implemented | Embedded-source and native output tests |
| Scalar `ToString()` | Implemented | Boundary formatting, identity, diagnostic, and null-failure tests |
| Structured diagnostics | Implemented | Stable phase ranges and source locations |

## Conformance coverage

The executable test project checks:

- Byte-identical repeated C emission.
- Trivia, comments, missing tokens, skipped tokens, spans, and exact syntax round-tripping.
- Definite assignment.
- Multi-file declarations and imports.
- Accessor access control.
- Unsafe pointer exposure through recursively pointer-containing types.
- Unrelated reference-cast and integral-only operator diagnostics.
- Pairwise overload ambiguity.
- `do` and switch return-flow analysis.
- Converted duplicate and out-of-range case labels.
- Compilation-wide external ABI validation.
- Atomic and stale-safe directory output.
- Entry point and extern validation.
- Readonly branch merging and duplicate assignment.
- Left-to-right receiver and argument evaluation.
- Constant folding into C case labels.
- Classes, structures, enums, constructors, methods, and properties.
- Arrays, loops, strings, and the bundled standard library.
- Managed null, bounds, negative-length, division-by-zero, and allocation-overflow paths.
- Native C compilation with warnings treated as errors.
- Checked standard output and runtime error output.

The full example in [examples/Features.ct](examples/Features.ct) is part of the native suite.

## Runtime status

Managed objects currently use program-lifetime allocation. This is conforming draft 0.3 behavior.

The runtime provides deterministic failures for null access, bad array lengths, allocation-size overflow, bounds access, allocation failure, integer division by zero, and string-length overflow.

The C ABI uses native target-width pointers. The reviewed native run used a 64-bit MSVC target.

## Deliberately deferred

These features are outside draft 0.3:

- Class inheritance and interfaces.
- Generics.
- Exceptions.
- Delegates, lambdas, and function types.
- Iterators and yield statements.
- Pattern matching.
- Nullable reference analysis.
- Reflection and dynamic binding.
- Async methods and tasks.
- Named, optional, `ref`, `in`, `out`, and parameter-array arguments.
- Multidimensional and jagged arrays.
- String interpolation and raw or verbatim strings.
- Finalizers and automatic disposal.
- Garbage collection before process exit.
- Exact-source compilation of the current C# compiler.

## Release gate

A draft 0.3 release requires:

- A zero-warning .NET build.
- All managed and native conformance checks.
- Byte-identical repeated output.
- GNU C23 compilation with warnings as errors.
- MSVC latest-C compatibility compilation with warnings as errors.
- Documentation synchronized with measured behavior.
- No C output for invalid programs, including stale generated directory output.

Draft 0.3 uses GCC or Clang in GNU C23 mode as the canonical native release gate. MSVC latest-C mode is retained as an independent compatibility check.
