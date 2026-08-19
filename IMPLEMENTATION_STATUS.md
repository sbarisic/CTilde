# Implementation status

Last reviewed: 2026-08-19

## Current state

C~ draft 0.6 has one compiler path:

```text
.ct source -> full-fidelity syntax -> combined binding, flow, allocation effects, and ARC ownership lowering -> transitional typed-line IR -> target validation -> hosted or ESP-IDF GNU C23
```

The compiler library, CLI, and conformance runner target .NET 10. The previous prototype AST, direct assembly backend, mutable backend state, and demonstration harness have been removed.

The compiler emits one C file. Hosted output is self-contained. ESP-IDF output includes the checked `ctilde_esp_shim.h` boundary. The compiler does not invoke a native compiler.

## Measured baseline

The current workspace passes:

```powershell
dotnet build .\CTilde.sln --nologo
dotnet run --project .\Test\Test.csproj --no-build
```

The .NET 10 build uses SDK `10.0.400-preview.0.26322.102` and completes with zero warnings and zero errors. The conformance runner contains 69 managed and native checks, plus end-to-end LSP protocol and VS Code Extension Host checks.

Native checks discover Visual Studio 2022 C tools. The reviewed run used MSVC `19.44.35225` and compiled generated files with:

```text
cl /std:clatest /O2 /W4 /WX /wd4702
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

The checked exception example prints:

```text
handled
cleanup
5
```

The independent compiler check uses GCC 13.3.0 from Ubuntu 24.04 under WSL. That compiler uses the draft compatibility spelling for the C23 dialect:

```text
gcc -std=gnu2x -O2 -Wall -Wextra -Werror
```

It exits successfully and produces the same checked output. The runner tries `gnu23` and retries with `gnu2x` only after an unsupported-option error. `CTILDE_CC` accepts compiler paths, `wsl:gcc`, and `wsl:clang`. `CTILDE_C_STANDARD` forces one dialect.

Ubuntu Clang 18.1.3 under WSL also passes the complete suite with `-std=gnu23 -O2 -Wall -Wextra -Werror`.

## Language support

| Area | Status | Evidence |
| --- | --- | --- |
| UTF-8 files and Unicode identifiers | Implemented | Strict UTF-8 decoding and rune-based identifier categories |
| Full-fidelity tokens and trivia | Implemented | Valid and invalid exact round-trip tests |
| Comments, escapes, and numeric forms | Implemented | Lexer diagnostics and literal tests |
| File and block namespaces | Implemented | Parser and multi-file test |
| Namespace imports | Implemented | Multi-file test with imported type |
| Classes and static classes | Implemented | Native object test and feature example |
| Single class inheritance and protected access | Implemented | Hierarchy diagnostics and native base-member tests |
| Virtual methods and properties | Implemented | Multi-level dispatch and sealed-override tests |
| Base and same-type constructor chains | Implemented | Constructor order and cycle tests |
| `System.Object` and `object` | Implemented | Instance, static, null, and override tests |
| `System.Exception` | Implemented | Constructors, message, inherited runtime name, and unhandled output tests |
| `throw` and rethrow | Implemented | Cross-call throw, null throw, rethrow identity, and replacement tests |
| Typed and catch-all handlers | Implemented | Source-order matching, reachability diagnostics, and native dispatch tests |
| `finally` cleanup | Implemented | Normal, return, break, continue, and exception cleanup tests |
| `defer` cleanup | Implemented | Immediate capture, receiver capture, LIFO, block, loop, transfer, and cleanup-exception tests |
| Boxing and exact unboxing | Implemented | Scalar, enum, structure, and unsafe pointer tests |
| Checked casts, `is`, and `as` | Implemented | Positive, null, mismatch, and runtime-failure tests |
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
| `[Retained]` and `[ReturnsBorrowed]` | Implemented | Target, argument, native transfer, and borrowed-result tests |
| `[NoAlloc]` | Implemented | Direct, recursive, transitive, extern, virtual, property, and defer-effect tests |
| Bundled `System.Object`, `System.Console`, `System.Environment`, and `System.Runtime.Memory` sources | Implemented | Embedded-source and native output tests |
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
- Object headers, descriptors, inherited layouts, virtual dispatch, constructor chains, casts, and boxing.
- Arrays, loops, strings, and the bundled standard library.
- Managed null, bounds, negative-length, division-by-zero, and allocation-overflow paths.
- Native C compilation with warnings treated as errors.
- Checked standard output and runtime error output.
- Exact exception-syntax round trips and stable exception diagnostics.
- Deterministic handler, `setjmp`, catch-dispatch, pending-action, and finally lowering.
- Durable integer, string, structure, parameter, and return state across `longjmp`.
- Handler cleanup on normal completion, return, loop transfer, catch, rethrow, and finally paths.
- Stack-backed exception and defer state with no control-flow `ct_alloc` calls.
- `[NoAlloc]` direct allocation categories, recursive inference, transitive witnesses, and trusted boundary contracts.
- ARC aliases, self-assignment, strong fields and array slots, nested structures, boxes, owned returns, constructor rollback, exception cleanup, native ownership transfer, balanced unsafe counts, cycle leakage, and a 10,000-object non-recursive destruction chain.

The full examples in [examples/Features.ct](examples/Features.ct), [examples/ObjectModel.ct](examples/ObjectModel.ct), and [examples/Exceptions.ct](examples/Exceptions.ct) are part of the native and ABI checks.

## Runtime status

Managed objects use single-threaded, non-moving automatic reference counting. Classes, arrays, dynamic strings, boxes, and nested reference-bearing structures release deterministically at the last owned reference. Static strings are immortal; static fields live until termination; cycles intentionally leak. Generated class, array, string, box, and structure drop helpers use an allocation-free iterative release queue, so cascading destruction does not recurse on the C stack.

The runtime provides deterministic failures for null access, casts, unboxing, arrays, allocation, integer division, string overflow, unhandled exceptions, and null throws. Existing runtime faults remain fatal and are not catchable.

Exception handlers use one process-global `setjmp` and `longjmp` stack, one owning current-exception pointer, and one automatic cleanup-record stack. The implementation is single-threaded. Methods with `try` or `defer` keep values that survive `longjmp` in one volatile automatic aggregate. Handler frames record cleanup boundaries so throwing releases all exited owning slots before `longjmp`.

The C ABI uses native target-width pointers. The reviewed native run used a 64-bit MSVC target.

## Known architecture debt

The body pipeline does not yet satisfy the final bound-tree and typed-IR design. `MethodLowerer` still combines semantic binding, flow analysis, and C-fragment construction. `TypedIrLowerer` classifies rendered lines into instruction categories.

The draft 0.6 exception and ARC surface and ABI checks pass, but the compiler architecture is not complete until binding produces immutable bound nodes and lowering produces structured three-address IR without C text. `GetDiagnostics()` also still triggers this combined lowering pass.

## Language server and VS Code

The repository includes an LSP 3.17 server and VS Code client. The server supports incremental document synchronization, cancellable diagnostic publication, full-document semantic tokens, semantic completion, hover, signature help, go-to-definition, document symbols, workspace symbols, and read-only embedded standard-library navigation. Semantic tokens classify resolved identifiers with declaration, static, readonly, and default-library modifiers; TextMate remains responsible for lexical and unresolved syntax.

`ctilde.json` defines deterministic source globs, exclusions, and a hosted or ESP-IDF target. The CLI and language server share the loader. Files without a manifest are analyzed as standalone hosted programs; files outside a manifest source set retain that manifest's target but do not join its compilation.

The extension bundles its JavaScript client and framework-dependent .NET 10 server. The user supplies the .NET 10 runtime. Protocol and Extension Host checks exercise initialization, incremental edits, diagnostics, semantic-token encoding and refresh, completion, hover, signature help, definitions, symbols, target filtering, embedded sources, shutdown, and exit.

The language-service query snapshot is immutable and does not call `EmitC`. The broader compiler architecture debt above remains: compiler diagnostics still pass through the transitional combined body lowering path until immutable bound bodies replace it.

## ESP-IDF target

The hardware MVP compiler and project support are implemented. `CompilationOptions` and `--target esp-idf` select one chip-independent profile. It emits `app_main`, compact source locations, unbuffered console startup, four-byte pointer assertions, abort-based fatal failures, and no `ct_keep_symbols` retention routine.

`Esp.Idf` provides FreeRTOS delay and counters, restart and heap counters, basic GPIO, and one RMT-backed WS2812 strip through a fixed-width handwritten shim. `System.Environment.Exit` is rejected with `CT4105`.

ESP-IDF 6.0.2 complete firmware builds pass with `-Wall -Wextra -Werror` for both `esp32` using Xtensa GCC 15.2.0 and `esp32c3` using RISC-V GCC 15.2.0. Fresh Hello and Exceptions output also passes both cross-compilers in GNU C23 syntax checks.

Measured self-test firmware sizes are:

| Target | Image | Flash code | Flash data | IRAM/DRAM |
| --- | ---: | ---: | ---: | ---: |
| `esp32` | 145,417 bytes | 57,666 bytes | 31,904 bytes | 45,003 bytes IRAM; 13,260 bytes DRAM |
| `esp32c3` | 148,070 bytes | 72,462 bytes | 29,228 bytes | 50,428 bytes DRAM, including 39,876 bytes executable text |

The corrected self-test ran on an ESP32-D0WDQ6-V3 revision 3.1 T-CAN485 at `COM4`. It printed `virtual: 42`, `boxed: 7`, `exception: caught on ESP32`, and `CTILDE_ESP_OK`; the RMT-backed GPIO4 WS2812 commands alternated every 500 ms without a watchdog reset, and the onboard LED was confirmed to blink green in step with them. After the strip was configured and cleared, the board reported 298,172 bytes of free and minimum free heap and 7,744 bytes of main-task stack high-water headroom with the configured 8 KiB stack.

The separate failure image printed `C~ runtime error CTN0001 at RuntimeFailure.ct:17`, entered ESP-IDF `abort()`, and rebooted with `SW_CPU_RESET`. The WS2812 self-test image was reflashed and verified by UART as the final board state. The earlier GPIO2 run is retained only as command-level GPIO validation: GPIO2 is the T-CAN485 microSD MISO signal and did not provide a visible blink.

The draft 0.6 ESP acceptance source now repeats mixed acyclic object, reference-bearing structure, array, box, and dynamic-string allocation for 50 rounds and requires free heap to return within 512 bytes of its baseline. Both Xtensa and RISC-V ESP cross-compilers accept it with warnings as errors, and complete `esp32` and `esp32c3` firmware links pass. The revised ARC image has not yet been flashed, so the hardware heap-recovery marker remains to be measured on-device.

## Deliberately deferred

These features are outside draft 0.6:

- Interfaces and abstract types.
- Generics.
- Exception filters, inner exceptions, stack traces, and specialized exception subclasses.
- Exceptions across native boundaries and thread-safe handler state.
- Unsafe unmanaged function pointers.
- Delegates, lambdas, exported methods, callback trampolines, and callback lifetime management.
- Native-sized and 64-bit integers, opaque native handles, native strings and buffers, and `ref`/`in`/`out` ABI parameters.
- Header-driven ESP-IDF bindings for configuration structures, constants, macros, and static-inline functions.
- Typed `esp_err_t` results and native resource ownership diagnostics.
- FreeRTOS task attachment, `volatile`, atomics, and compiler-checked ISR or IRAM execution profiles.
- Iterators and yield statements.
- Pattern matching.
- Nullable reference analysis.
- Reflection and dynamic binding.
- Async methods and tasks.
- Named, optional, `ref`, `in`, `out`, and parameter-array arguments.
- Multidimensional and jagged arrays.
- String interpolation and raw or verbatim strings.
- Weak references, cycle collection, finalizers, and automatic disposal.
- Exact-source compilation of the current C# compiler.

## Release gate

A draft 0.6 release requires:

- A zero-warning .NET build.
- All managed and native conformance checks.
- Byte-identical repeated output.
- GNU C23 compilation with warnings as errors.
- MSVC latest-C compatibility compilation with warnings as errors.
- Documentation synchronized with measured behavior.
- No C output for invalid programs, including stale generated directory output.

Draft 0.6 uses GCC or Clang in GNU C23 mode as the canonical native release gate. MSVC latest-C mode remains an independent compatibility check.
