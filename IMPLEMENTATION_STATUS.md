# Implementation status

Last reviewed: 2026-08-19

## Current state

C~ draft 0.10 has one compiler path:

```text
.ct source -> full-fidelity syntax -> declarations -> immutable bound bodies and semantic maps -> flow/effect/target validation -> structured typed IR -> hosted or ESP-IDF GNU C23
```

The compiler library, CLI, and conformance runner target .NET 10. The previous prototype AST, direct assembly backend, mutable backend state, and demonstration harness have been removed.

The compiler emits one C file and can independently emit a deterministic public header for `[Export]` methods and the native ARC/thread attachment ABI. Hosted output is self-contained. ESP-IDF output includes the checked `ctilde_esp_shim.h` boundary. The CLI can stop after emission or invoke an installed MSVC/GCC/Clang or ESP-IDF toolchain. Self-contained single-file CLI distributions bundle .NET but not native toolchains.

## Measured baseline

The current workspace passes:

```powershell
dotnet build .\CTilde.sln --nologo
```

The .NET 10 build uses SDK `10.0.400-preview.0.26322.102` and completes with zero warnings and zero errors. The conformance runner contains 92 managed and native checks, plus end-to-end LSP protocol and VS Code Extension Host checks. Complete conformance runs pass under MSVC, WSL GCC, and WSL Clang, including `System.Math` behavior and the HostedIo ray tracer's deterministic PPM output.

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
Text.Length < 10!
10
42
-9223372036854775808
18446744073709551615
42
42
42
42
6
42
42
42
Before deferred, i hope?
deferred
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
| Exact-width integers through `long`/`ulong` | Implemented | Suffix, boundary, promotion, wrapping, formatting, enum, boxing, and C ABI tests |
| Native-sized `nint` and `nuint` | Implemented | Portable constants, promotions, wrapping, target-width shifts, overloads, formatting, boxing, and ABI tests |
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
| `void*` data-pointer conversions | Implemented | Explicit typed conversion and operation-rejection tests |
| `ref`, `in`, and `out` parameters | Implemented | Methods, constructors, delegates, function pointers, externs, flow, readonly, ARC, mangling, and pointer ABI tests |
| Scoped native buffers and `stackalloc` | Implemented | Construction, conversion, flattening, bounds, count checks, escape diagnostics, and native fixtures |
| Scoped `NativeUtf8String` | Implemented | Owner retention, zero allocation, NUL diagnostics, nullable input, ABI flattening, and escape checks |
| Nominal opaque handles and native ownership | Implemented | Native typedef headers, moves, created/consumed/retained contracts, defer reservations, and leak diagnostics |
| Named single-cast delegates | Implemented | Static, instance, virtual, inherited/base, ARC receiver, identity, and null-invocation tests |
| Unsafe unmanaged function pointers | Implemented | Structural signatures, trampolines, native round trip, unsafe checks, and fatal callback-exception test |
| `[EntryPoint]` | Implemented | Validation and native wrapper tests |
| `[Extern]` | Implemented | Reserved-name, collision, alias, ABI, and prototype tests |
| Native ownership attributes | Implemented | Borrowed, consumed, retained, created, nullable, owned-return, and borrowed-return tests |
| `[Export]` and C headers | Implemented | Signature validation, wrappers, exception barriers, deterministic C/C++ declarations, and conflict tests |
| Synchronous delegate/context callbacks | Implemented | ARC lifetime, virtual dispatch, ABI placement, attachment guards, and exception barriers |
| `[NoAlloc]` | Implemented | Direct, recursive, transitive, extern, virtual, property, and defer-effect tests |
| Bundled `System.Object`, `System.Console`, `System.Environment`, `System.Math`, and `System.Runtime.Memory` sources | Implemented | Embedded-source, documentation, native math, and output tests |
| Hosted console input and `System.IO` | Implemented | UTF-8 line/EOF behavior, Unicode paths, opaque ownership, binary round trip, exceptions, target filtering, and editor documentation |
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
- 64-bit suffixes, boundaries, conversions, promotions, wrapping arithmetic, shifts, boxing, formatting, enums, and switches.
- Native-sized conversions, portable constant rules, target-dependent shifts, wrapping, boxing, formatting, overloads, and pointer differences.
- By-reference modifier matching, addressability, readonly and definite-assignment flow, ARC slot replacement, delegate/function-pointer calls, and exact extern ABI mappings.
- Native-buffer construction, writable/read-only views, checked indexing, stack-count failures, lexical loop rejection, scoped-storage restrictions, and flattened pointer-plus-length calls.
- Delegate method-group selection, static and instance capture, virtual and base dispatch, ARC receiver lifetime, identity, and null invocation.
- Unmanaged function-pointer parsing, exact signatures, native synchronous callback invocation, unsafe enforcement, and the `CTE0003` exception barrier.
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

Managed objects use atomic, non-moving automatic reference counting. Classes, arrays, dynamic strings, boxes, and nested reference-bearing structures release deterministically on the thread that atomically drops the last owned reference. Static strings are immortal; static fields live until termination; cycles intentionally leak. Generated class, array, string, box, and structure drop helpers use a per-thread allocation-free LIFO worklist, so cascading destruction does not recurse on the C stack.

The runtime provides deterministic failures for null access, casts, unboxing, arrays, allocation, integer division, string overflow, unhandled exceptions, and null throws. Existing runtime faults remain fatal and are not catchable.

Each attached thread has independent `setjmp`/`longjmp` handlers, current-exception ownership, automatic cleanup records, and iterative release state. Methods with `try` or `defer` keep values that survive `longjmp` in one volatile automatic aggregate. Handler frames record cleanup boundaries so throwing releases all exited owning slots on the current thread before `longjmp`.

The entrypoint installs an automatic primary `ct_thread_state` before static initialization and publishes a ready phase afterward. Native-created threads use `ct_thread_attach` and `ct_thread_detach`; exports, callback trampolines, retain, and release reject unattached use. Hosted builds use C thread-local storage, while ESP-IDF uses a configured FreeRTOS task-local-storage slot with deletion checking. ARC atomics protect lifetime only; sharing ordinary object state still requires synchronization.

The C ABI uses native target-width pointers and `nint`/`nuint`. Scoped native buffers use checked pointer-plus-length values, and scoped UTF-8 input retains its managed owner without allocating before flattening to `const char*`. Nominal opaque native handles carry lexical move-only ownership obligations. Stack allocation does not use the managed heap.

Hosted compilations add `Console.Read`, UTF-8 `Console.ReadLine`, and synchronous binary `System.IO` only when source uses that surface. Windows file paths use validated UTF-8-to-UTF-16 conversion and `_wfopen_s`; POSIX uses validated UTF-8 `fopen` paths. File handles are move-only opaque values, reads use checked native buffers, complete writes accept buffers or managed UTF-8 strings, and close consumes native storage even when it reports a catchable `IOException`. Unrelated hosted programs and all ESP-IDF output retain their earlier generated C.

## Compiler pipeline status

Binding now produces immutable bound bodies and per-document semantic maps. Bound expressions carry resolved types, symbols, constants, value categories, and ARC ownership; bound statements preserve lexical scopes, control flow, exception regions, and defer/finally cleanup boundaries. Allocation effects and extern uses are analysis results rather than emitter state.

Typed IR contains typed values, basic blocks, loads, stores, calls, allocations, conversions, checks, ownership and cleanup actions, and structured terminators. The rendered-line classifier and `MethodLowerer` have been removed. `GetDiagnostics()` is analysis-only and a conformance check verifies that it constructs no `CEmitter`, `CWriter`, typed IR, or generated C. Emission remains lazy and deterministic; draft 0.10 deliberately updates the managed-header and runtime snapshots.

## Language server and VS Code

The repository includes an LSP 3.17 server and VS Code client. The server supports incremental document synchronization, cancellable diagnostic publication, full-document semantic tokens, semantic completion with lazy documentation resolution, documented hover and signature help, go-to-definition, document symbols, workspace symbols, and read-only embedded standard-library navigation. Semantic tokens classify resolved identifiers with declaration, static, readonly, and default-library modifiers; TextMate remains responsible for lexical and unresolved syntax, including scoped `///` XML comments.

Documentation analysis accepts summaries, parameters, returns, remarks, exception and inline references, parameter references, and explicit inheritance. Malformed, unsupported, duplicate, unresolved, invalid-inheritance, and orphan documentation reports warning codes `CT5000` through `CT5006` without blocking checking or C emission. Embedded `System`, compiler-intrinsic, and ESP-IDF descriptions live in XML sidecars, so standard-library source locations and existing generated C remain unchanged.

`ctilde.json` defines deterministic source globs, exclusions, and a hosted or ESP-IDF target. The CLI and language server share the loader. Files without a manifest are analyzed as standalone hosted programs; files outside a manifest source set retain that manifest's target but do not join its compilation.

The 0.3.1 extension bundles its JavaScript client and framework-dependent .NET 10 server. The user supplies the .NET 10 runtime. Protocol and Extension Host checks exercise initialization, incremental edits, diagnostics, semantic-token encoding and refresh, lazy completion documentation, documented hover and active parameters, definitions, symbols, target filtering, embedded sources, shutdown, and exit. Hosted snapshots include documented console-input and `System.IO` symbols; ESP-IDF snapshots omit them.

The language-service query snapshot owns the same immutable bound program used by compilation. Its per-document indexes reuse bound expression types and symbols without calling `EmitC` or initializing backend state.

## ESP-IDF target

The hardware MVP compiler and project support are implemented. `CompilationOptions` and `--target esp-idf` select one chip-independent profile. It emits `app_main`, compact source locations, unbuffered console startup, four-byte pointer assertions, abort-based fatal failures, and no `ct_keep_symbols` retention routine.

`Esp.Idf` provides FreeRTOS delay and counters, restart and heap counters, a signed 64-bit monotonic microsecond timer, typed `EspError` results for GPIO and one RMT-backed WS2812 strip, and exact error-name copying through the handwritten shim. `System.Environment.Exit` is rejected with `CT4105`.

Draft 0.9 intentionally changes the GPIO configuration/write and WS2812 operation results from `bool` to `EspError`. Boolean sensor data such as `Gpio.Read` remains unchanged.

ESP-IDF 6.0.2 complete firmware builds pass with `-Wall -Wextra -Werror` for both `esp32` using Xtensa GCC 15.2.0 and `esp32c3` using RISC-V GCC 15.2.0. Fresh Hello and Exceptions output also passes both cross-compilers in GNU C23 syntax checks.

Measured self-test firmware sizes are:

| Target | Image | Flash code | Flash data | IRAM/DRAM |
| --- | ---: | ---: | ---: | ---: |
| `esp32` | 153,280-byte binary; 153,165-byte image | 64,022 bytes | 32,560 bytes | 45,003 bytes IRAM; 14,020 bytes DRAM |
| `esp32c3` | 156,744-byte image | 79,600 bytes | 29,900 bytes | 51,316 bytes DRAM, including 40,012 bytes executable text |

The Draft 0.9 self-test ran on an ESP32-D0WDQ6-V3 revision 3.1 T-CAN485 at `COM4`. In addition to every Draft 0.8 marker, it printed `native utf8: ok`, `opaque defer: ok`, `esp error: ESP_OK`, `delegate context: 42`, and `export: 42`. After the strip was configured and cleared and the managed self-tests returned, the board reported 297,700 bytes of free heap, a 295,112-byte minimum, and 6,552 bytes of main-task stack high-water headroom with the configured 8 KiB stack.

The RMT-backed GPIO4 WS2812 commands completed more than ten 500 ms on/off cycles without a watchdog reset. The same path was previously confirmed by a person to blink the onboard LED green. The separate Draft 0.9 failure image printed `C~ runtime error CTN0001 at RuntimeFailure.ct:23`, entered ESP-IDF `abort()`, and rebooted with `rst:0xc (SW_CPU_RESET)`. The Draft 0.9 self-test image was then rebuilt, reflashed, and verified through every marker and more than ten additional LED cycles as the final board state. The earlier GPIO2 run is retained only as command-level GPIO validation: GPIO2 is the T-CAN485 microSD MISO signal and did not provide a visible blink.

The Draft 0.9 ESP acceptance source repeats mixed acyclic managed allocations for 50 rounds and requires free heap to return within 512 bytes of its baseline. It also checks a scoped UTF-8 call, deferred opaque release, exact ESP error naming, same-task delegate/context entry, a generated export, the timer, virtual delegate, unmanaged function pointer, and native buffer. Both Xtensa and RISC-V ESP cross-compilers accepted it with warnings as errors, complete firmware links passed with the sizes above, and its physical-board acceptance sequence is complete.

The Draft 0.10 firmware adds two attached FreeRTOS workers, cross-task delegate and function-pointer callbacks, per-task exception/defer cleanup, and concurrent ARC lifetime operations. Complete Xtensa and RISC-V links pass. The final 155,360-byte Xtensa image was flashed to the connected dual-core ESP32 on 2026-08-19 and printed `threading: ok`, `exception: caught on ESP32`, `arc heap recovery: True`, and `CTILDE_ESP_OK`. It reported 297,620 bytes free, a 286,624-byte minimum, and 6,520 bytes of stack high-water headroom before continuing for more than ten GPIO4 WS2812 cycles without a watchdog reset.

## Deliberately deferred

These features are outside draft 0.10:

- Interfaces and abstract types.
- General user-defined generics; only intrinsic native-buffer forms exist.
- Exception filters, inner exceptions, stack traces, and specialized exception subclasses.
- General exceptions across native boundaries.
- Lambdas, closures, multicast delegates, retained callbacks, and callback registration lifetime management.
- Long-lived owned native-resource fields, source-level task and lock APIs, and exported delegates as ordinary ABI values.
- Header-driven ESP-IDF bindings for configuration structures, constants, macros, and static-inline functions.
- Source-level `volatile` or atomic access and compiler-checked ISR or IRAM execution profiles.
- Iterators and yield statements.
- Pattern matching.
- Nullable reference analysis.
- Reflection and dynamic binding.
- Async methods and tasks.
- Named, optional, implicit by-reference, reference-return, reference-local, and parameter-array arguments.
- Multidimensional and jagged arrays.
- String interpolation and raw or verbatim strings.
- Weak references, cycle collection, finalizers, and automatic disposal.
- Exact-source compilation of the current C# compiler.

## Release gate

A draft 0.10 release requires:

- A zero-warning .NET build.
- All managed and native conformance checks.
- Byte-identical repeated output.
- GNU C23 compilation with warnings as errors.
- MSVC latest-C compatibility compilation with warnings as errors.
- Documentation synchronized with measured behavior.
- No C output for invalid programs, including stale generated directory output.

Draft 0.10 uses GCC or Clang in GNU C23 mode as the canonical native release gate. MSVC latest-C mode remains an independent compatibility check. Hosted MSVC, GCC 13, and Clang 18 conformance pass for the atomic ARC and attached-thread runtime, including the Clang ThreadSanitizer fixture. Both ESP syntax cross-compilers and complete firmware links pass, and the connected dual-core Xtensa ESP32 passes the Draft 0.10 threading and heap-recovery acceptance sequence.
