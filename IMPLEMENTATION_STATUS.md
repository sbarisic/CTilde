# Implementation status

Last reviewed: 2026-08-24

## Current state

C~ draft 0.17 has one compiler path:

```text
.ct source -> full-fidelity syntax -> declarations -> immutable bound bodies and semantic maps -> flow/effect/target validation -> structured typed IR -> reachability/optimization -> unity or modular hosted/ESP-IDF GNU C23
```

The compiler library, CLI, and conformance runner target .NET 10. The previous prototype AST, direct assembly backend, mutable backend state, and demonstration harness have been removed.

The compiler emits one C file by default or an immutable modular bundle containing shared headers, one runtime source, one source per reachable namespace, an entry/lifecycle source, a versioned symbol map, and an ESP-IDF CMake fragment. It can independently emit a deterministic public header for `[Export]` methods and runtime ABI 16. Source-debug emission adds C~ mappings and stable hooks. Debug Launch emits deterministic logical probes and version-3 metadata, including aggregate layout kind, packing, explicit offsets, and generated storage paths. Ordinary and Release output remain unchanged. Hosted output is self-contained. ESP-IDF output includes the checked `ctilde_esp_shim.h` boundary. The CLI can stop after emission, invoke an installed MSVC/GCC/Clang or ESP-IDF toolchain, or prepare verified Launch/Attach descriptors. Hosted modular objects use a content-addressed cache; hosted Release builds can enable LTO.

## Measured baseline

The current workspace builds with:

```powershell
dotnet build .\CTilde.sln --nologo
```

The .NET 10 build uses SDK `10.0.400-preview.0.26322.102` and completes with zero warnings and zero errors. Draft 0.17 coverage adds controlled method/static-field section placement, strict target and name diagnostics, generic propagation, deterministic unity/modular/header rendering, and MSVC/GCC/Clang object-table probes. Draft 0.16 coverage added unions, natural and packed aggregate layouts, overlapping explicit field offsets, and symbolic layout operators. Earlier deterministic debug, runtime-fault, generic, concurrency, modular-emission, immutable-metadata, renderer, and exact reduced-image gates continue to pass.

Draft 0.15 completed its hosted, ESP32/ESP32-C3 cross-build, and connected T-CAN485 acceptance on 2026-08-23. The physical ESP32-D0WDQ6-V3 run used ESP-IDF 6.0.2, Xtensa GCC 15.2.0, ESP-GDB 17.1, COM4 at 460800 baud, and the onboard USB-to-UART bridge. The 171,136-byte pre-network Release binary reported 297,036 bytes free heap, a 284,304-byte minimum, and 6,736 bytes of main-task stack headroom while completing every ABI 15 marker and 25 alternating WS2812 transitions. The operator confirmed visible LED activity. The default firmware now invokes its Wi-Fi/HTTPS worker whenever an SSID is configured and uses an empty tracked SSID as the clean-checkout offline fallback. Linking Wi-Fi, TLS, the HTTP client, and the full certificate bundle increased the accepted ESP32 cross-build to 1,009,888 binary bytes and 1,009,764 image bytes, with 696,614 bytes flash code, 204,380 bytes flash data, 90,779 bytes IRAM, and 38,871 bytes static DRAM. The corresponding ESP32-C3 cross-build is 1,072,512 binary bytes and 1,072,142 image bytes, with 776,986 bytes flash code, 206,596 bytes flash data, and 109,488 bytes static DRAM. These larger values are an explicit ESP-IDF 6.0.2/GCC 15.2.0 memory-baseline update, not an ABI change.

The same run validated exact UTF-8 console bytes, catchable allocation failure for classes, arrays, boxes, and dynamic strings, zero residual ARC ownership, the `CTN0001` fatal boundary, and the exact 32-bit ABI layouts. Guarded debugger-v3 validation used six logical breakpoints, C~-level stepping, five FreeRTOS tasks, exception translation, lexical locals, ARC/canary inspection, and a reference-count watchpoint. It observed 3,364 allocations and 3,364 final releases, detached without reset, continued the LED loop, and passed the no-debugger startup timeout. The machine-readable report is `artifacts/esp32-hardware/20260823-030442.json`; the runner restored the ordinary Release firmware.

The network-disabled Wi-Fi/HTTPS image received a fresh automated connected-board pass on 2026-08-23. It printed `wifi: not configured`, retained every ABI 15 marker, completed 25 alternating WS2812 transitions, and reported 291,696 bytes free heap, a 278,964-byte minimum, and 6,708 bytes of main-task stack headroom. Allocation-failure recovery, exact UTF-8 output, fatal reset, debugger-v3 inspection, clean detach, post-detach LED execution, and the startup timeout all passed. The ignored report is `artifacts/esp32-hardware/20260823-211534.json`. This was an automated-only run, so it does not add a new visual LED confirmation or claim a live HTTPS result.

The configured live-network gate subsequently passed on the same board. The generated NVS, netif, event-loop, Wi-Fi, certificate-bundle, and HTTP bindings associated through WPA2, obtained an IPv4 address, validated the server certificate, and fetched `https://example.com/` with HTTP 200. It read 559 bytes, produced FNV-1a hash `1710764169`, bounded ESP-IDF's retained first-use Wi-Fi/TLS state to 7,280 bytes, completed ARC cleanup, reached `CTILDE_ESP_OK`, and continued the WS2812 loop. This run also exposed and fixed two ESP concurrency defects: C~ now reserves FreeRTOS TLS slot 1 instead of colliding with pthread slot 0, and passes source stack sizes to ESP-IDF `xTaskCreate` as bytes rather than dividing them into vanilla-FreeRTOS words.

The modular MSVC Release+LTO production renderer completed the full 1200x675, 500-sample, 50-bounce BVH profile in 4,420.348 seconds (1:13:40.348) on the reviewed machine. Its P3 PPM SHA-256 is `4084366E15EACF65F73758C22C0A12589B30EC09362B9749DA690A7D71B1D5A4`. The reduced image remains the automated deterministic gate; the production elapsed time is a recorded machine-specific measurement.

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

Ubuntu Clang 18.1.3 under WSL passed the previously reviewed complete suite with `-std=gnu23 -O2 -Wall -Wextra -Werror`.

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
| Interfaces and abstract classes | Implemented | Multiple contracts, inherited implementations, abstract completeness, class views, structure boxing, casts, properties, per-concrete dispatch tables, native dispatch tests, and physical ESP32 dispatch |
| Closed generics | Implemented | Generic types, methods, interfaces, delegates, inference, constraints, closed statics/layouts, ARC helpers, recursion limits, deterministic monomorphization tests, and physical ESP32 ARC cleanup |
| Virtual methods and properties | Implemented | Multi-level dispatch and sealed-override tests |
| Base and same-type constructor chains | Implemented | Constructor order and cycle tests |
| `System.Object` and `object` | Implemented | Instance, static, null, and override tests |
| `System.Exception` | Implemented | Constructors, message, inherited runtime name, and unhandled output tests |
| Built-in runtime-fault exceptions | Implemented | Allocation-free immortal null, bounds, divide, cast, overflow, argument, and OOM objects with origin metadata |
| `throw` and rethrow | Implemented | Cross-call throw, null throw, rethrow identity, and replacement tests |
| Typed and catch-all handlers | Implemented | Source-order matching, reachability diagnostics, and native dispatch tests |
| `finally` cleanup | Implemented | Normal, return, break, continue, and exception cleanup tests |
| `defer` cleanup | Implemented | Immediate capture, receiver capture, LIFO, block, loop, transfer, and cleanup-exception tests |
| Boxing and exact unboxing | Implemented | Scalar, enum, structure, and unsafe pointer tests |
| Checked casts, `is`, and `as` | Implemented | Positive, null, mismatch, and runtime-failure tests |
| Structures | Implemented | Native feature example |
| Unions and controlled aggregate layout | Implemented | Natural and packed layouts, explicit overlapping offsets, generic unmanaged revalidation, deterministic unity/modular/header C rendering, and native probes |
| `sizeof`, `alignof`, and `offsetof` | Implemented | Symbolic `nuint` constants, unsafe validation, arithmetic/comparison use, reachability, and native probes |
| Enumerations and fixed underlying types | Implemented | Native enum and switch example |
| Fields and static initialization | Implemented | Native ordered-evaluation and feature tests |
| Constructors and `new` | Implemented | Class and structure native tests |
| Custom and automatic properties | Implemented | Native property tests |
| Access modifiers | Implemented | Private member and setter diagnostics |
| Method overloads | Implemented | Pairwise best-candidate and cross-argument ambiguity tests |
| User-defined arithmetic operators | Implemented | Unary/binary declarations, type-body completion, scalar order, base lookup, ambiguity, ARC, evaluation order, compound targets, deterministic `ct_op_*` emission, and editor navigation tests |
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
| `ref`, `in`, and constructive `out` parameters | Implemented | First-write construction, repeated replacement, methods, constructors, delegates, function pointers, externs, flow, readonly, ARC, mangling, and pointer ABI tests |
| Runtime ABI 16 lifecycle | Implemented | Process initialization/shutdown, module descriptor, reverse static finalization, native attachment, source-created workers, thread gates, panic callback tests, and hosted acceptance; ABI 15 remains the latest connected-board baseline |
| Unity and modular C artifacts | Implemented | Deterministic shuffled-input bundles, reachability partitioning, strict native builds, object cache, symbol maps, and LTO flag mapping |
| C~-aware native debugging | Implemented | Source-only and instrumented modes, version-3 logical probes and scopes with closed-generic, interface, atomic, Thread, and Mutex presentation, cached C~ stepping and inspection, Run to Cursor, logical exception/log/function breakpoints, GDB watchpoints, ARC runtime inspection, ESP target-output forwarding and detach-and-continue, validated descriptors, WSL and ESP UART-stub resolution, MSVC fallback, and guarded ESP32 acceptance |
| Scalar atomics and `volatile` | Implemented | Typed atomic operations and fences, operation-specific order validation, non-copyable storage, acquire/release volatile fields, MSVC/GNU/FreeRTOS lowering, concurrent native tests, and physical ESP32 publication checks |
| `Thread`, `Mutex`, and `lock` | Implemented | Windows/POSIX/FreeRTOS workers, recursive mutexes, start/join publication, lifecycle faults, every structured lock exit, ARC ownership, native tests, and physical dual-worker ESP32 validation |
| Scoped native buffers and `stackalloc` | Implemented | Construction, conversion, flattening, bounds, count checks, escape diagnostics, and native fixtures |
| Scoped `NativeUtf8String` | Implemented | Owner retention, zero allocation, NUL diagnostics, nullable input, ABI flattening, and escape checks |
| Nominal opaque handles and native ownership | Implemented | Native typedef headers, moves, created/consumed/retained contracts, defer reservations, and leak diagnostics |
| Named single-cast delegates | Implemented | Static, instance, virtual, inherited/base, ARC receiver, identity, and null-invocation tests |
| Unsafe unmanaged function pointers | Implemented | Structural signatures, trampolines, native round trip, unsafe checks, and fatal callback-exception test |
| `[EntryPoint]` | Implemented | Validation and native wrapper tests |
| `[Extern]` | Implemented | Reserved-name, collision, alias, ABI, and prototype tests |
| Native ownership attributes | Implemented | Borrowed, consumed, retained, created, nullable, owned-return, and borrowed-return tests |
| `[Export]` and C headers | Implemented | Signature validation, wrappers, exception barriers, deterministic C/C++ declarations, and conflict tests |
| `[Section]` native placement | Implemented | Controlled names and targets, code/data conflict diagnostics, generic propagation, unity/modular/header annotations, reachability preservation, runtime execution, and MSVC/GCC/Clang object inspection |
| Synchronous delegate/context callbacks | Implemented | ARC lifetime, virtual dispatch, ABI placement, attachment guards, and exception barriers |
| `[NoAlloc]` | Implemented | Direct, recursive, transitive, extern, virtual, property, and defer-effect tests |
| Bundled `System.Object`, `System.Console`, `System.Environment`, `System.Math`, and `System.Runtime.Memory` sources | Implemented | Embedded-source, documentation, native math, and output tests |
| Hosted console input and `System.IO` | Implemented | UTF-8 line/EOF behavior, Unicode paths, opaque ownership, binary round trip, exceptions, target filtering, and editor documentation |
| Scalar `ToString()` | Implemented | Boundary formatting, identity, diagnostic, and null-failure tests |
| Structured diagnostics | Implemented | Stable phase ranges and source locations |

## Conformance coverage

The executable test project registers 132 checks, and all 132 pass. Coverage includes:

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

Managed objects use atomic, non-moving automatic reference counting. Classes, contiguous arrays, contiguous dynamic strings, boxes, and nested reference-bearing structures release deterministically on the thread that atomically drops the last owned reference. Type descriptors, vtables, the empty string, and literal string objects use portable `const` storage; ESP-IDF retains them in flash-backed read-only sections. Runtime-fault objects and mutable statics remain writable. Static strings and runtime-fault objects are immortal; static fields live until module finalization; cycles intentionally leak. Generated class, array, string, box, and structure drop helpers use a per-thread allocation-free LIFO worklist, so cascading destruction does not recurse on the C stack.

Recoverable runtime checks throw preinitialized `NullReferenceException`, `IndexOutOfRangeException`, `DivideByZeroException`, `InvalidCastException`, `OverflowException`, `ArgumentException`, or `OutOfMemoryException` objects. Raising them allocates nothing and preserves diagnostic code and source origin per thread. Runtime phase, attachment, ABI, reference-count, cleanup, and native-boundary violations remain panics; a configured panic callback runs before the default platform termination.

Each attached thread has independent `setjmp`/`longjmp` handlers, current-exception ownership, automatic cleanup records, and iterative release state. Only real `try` regions create exception frames. CFG liveness moves values into C-volatile durable storage only when they are modified after `setjmp` and remain live after a possible `longjmp`; this internal representation is separate from C~ acquire/release `volatile` fields. Ordinary `defer` uses direct automatic cleanup records and does not manufacture an exception frame.

The entrypoint calls `ct_runtime_initialize`, which attaches the primary thread, initializes fault singletons and the ABI-versioned module descriptor, and publishes ready. `ct_runtime_shutdown` requires secondary threads detached, finalizes static fields in reverse order, drains ARC work, and detaches the primary thread. Native-created threads use `ct_thread_attach` and `ct_thread_detach`; exports, callback trampolines, retain, and release reject unattached use. Source-created `Thread` workers use the same isolated runtime state and attach/detach lifecycle. Hosted builds use C thread-local storage, while ESP-IDF uses a configured FreeRTOS task-local-storage slot with deletion checking. ARC atomics protect lifetime only; sharing ordinary object state requires scalar `Atomic<T>`, acquire/release `volatile`, `Mutex`/`lock`, or another valid synchronization edge.

Draft 0.15 generics are whole-program monomorphized. The compiler interns each closed type and method by its canonical definition and substituted arguments, validates constraints before typed IR, and emits only reachable closed instances. Each closed reference-bearing layout receives its own ARC helpers and static storage. Interface references retain ordinary managed ownership; class conversion is allocation-free, structure conversion boxes, and concrete descriptors expose deterministic interface tables and dispatch slots. Open generics and runtime-backed interface, atomic, Thread, and Mutex values are rejected at native ABI boundaries.

The C ABI uses native target-width pointers and `nint`/`nuint`. Scoped native buffers use checked pointer-plus-length values, and scoped UTF-8 input retains its managed owner without allocating before flattening to `const char*`. Nominal opaque native handles carry lexical move-only ownership obligations. Stack allocation does not use the managed heap.

Hosted compilations add `Console.Read`, UTF-8 `Console.ReadLine`, and synchronous binary `System.IO` only when source uses that surface. Windows file paths use validated UTF-8-to-UTF-16 conversion and `_wfopen_s`; POSIX uses validated UTF-8 `fopen` paths. File handles are move-only opaque values, reads use checked native buffers, complete writes accept buffers or managed UTF-8 strings, and close consumes native storage even when it reports a catchable `IOException`. Unrelated hosted programs and all ESP-IDF output retain their earlier generated C.

## Compiler pipeline status

Binding now produces immutable bound bodies and per-document semantic maps. Bound expressions carry resolved types, symbols, constants, value categories, and ARC ownership; bound statements preserve lexical scopes, control flow, exception regions, and defer/finally cleanup boundaries. Allocation effects and extern uses are analysis results rather than emitter state.

Typed IR contains typed values, basic blocks, loads, stores, calls, allocations, conversions, checks, ownership and cleanup actions, and structured terminators. Reachability, cleanup-record liveness, constant and fixed-range facts, conservative non-null propagation, owned-result moves, direct-defer cleanup facts, durable-state liveness, fused scalar string builds, and user metadata pruning run before artifact layout. The first size tranche removes cleanup boundaries that cannot acquire records, disarms moved fresh results instead of retaining and releasing them, treats immortal strings as ownership-neutral, omits proven redundant null and fixed-index bounds checks, and simplifies constant loops and valid constant `stackalloc` sizes. `ref`/`out` aliases, unsafe effects, fields, nullable native results, and uncertain control-flow joins invalidate proofs. `TypedIrEmissionLowerer` then creates immutable function and initializer plans only for retained IR, and `CEmitter` composes those plans without reopening method syntax. The former rendered-line classifier, `MethodLowerer`, `BodyPipeline`, `CBodyLowerer`, and `LoweredExpression` transition layers are gone. `GetDiagnostics()` is analysis-only and constructs no `CEmitter`, `CWriter`, typed IR, or generated C.

On the unchanged full TCan485 Wi-Fi/TLS workload, the typed-IR size tranche reduced generated modular C from 285,651 to 271,437 bytes and from 5,281 to 4,692 lines. With ESP-IDF 6.0.2 and GCC 15.2.0, ESP32 C~-owned executable text fell from 21,710 to 19,727 bytes, the firmware binary from 1,009,888 to 1,008,496 bytes, flash code from 696,614 to 695,238 bytes, flash data from 204,380 to 204,092 bytes, and IRAM from 90,779 to 89,751 bytes; static DRAM remained exactly 38,871 bytes. The ESP32-C3 firmware fell from 1,072,512 to 1,070,464 bytes, flash code from 776,986 to 774,946 bytes, and flash data from 206,596 to 206,064 bytes; its combined executable/static-DRAM accounting remained 109,488 bytes. These are identical-input compiler measurements and require no memory-baseline increase.

Draft 0.13 adds full-fidelity raw `asm` blocks. Binding resolves scalar local and parameter operands, applies definite-assignment and `[NoAlloc]` rules, and records a side-effecting typed-IR instruction. C emission converts standalone operand names to GNU symbolic operands, preserves raw target instructions, and emits volatile extended asm with explicit constraints and clobbers. Language services classify and navigate operand references inside the raw body. Hosted GCC and Clang and both ESP-IDF toolchains are supported; the CLI rejects hosted MSVC native builds containing `asm`. Draft 0.14 preserves this surface across unity and modular layouts.

Every dynamic string helper now stores a trailing zero byte. A fused concatenation flattens nested string additions, formats supported built-in scalars into bounded automatic buffers, checks aggregate length, and allocates exactly one managed string object and one byte buffer. User-defined `ToString()` remains an ordinary owned call.

Both targets omit `ct_keep_symbols`. Reachability starts from entrypoints, exports, module initializers, address-taken methods, delegate/callback and virtual targets, then closes over bound and IR calls. Unreachable user functions, layouts, descriptors, and thunks are omitted. A deterministic identifier-based closure now prunes private runtime helper definitions and prototypes from the controlled generated prefix while retaining dependencies reached through public ABI functions, runtime data initializers, vtables, descriptors, drop callbacks, function pointers, generated methods, module lifecycle, and entrypoint code. Unity and modular output consume the same pruned prefix; the modular internal header contains declarations instead of duplicated runtime helper bodies. On the Draft 0.14 TCan485 workload, this reduced `ctilde_runtime.c` from 82,081 to 72,975 bytes (11.1 percent), `ctilde_internal.h` from 71,770 to 34,671 bytes (51.7 percent), and the connected-board ordinary Release binary from 168,480 to 164,816 bytes (2.2 percent).

Hosted callers can set an absolute `CompilationOptions.SourceRoot` or use CLI `--source-root`. Runtime paths are then normalized relative paths with `/` separators; default hosted output preserves full paths, virtual standard-library paths remain stable, and ESP-IDF continues to use compact filenames. Invalid API configuration reports `CT4106`, while invalid CLI combinations return usage exit code 2.

## Language server and VS Code

The repository includes an LSP 3.17 server and VS Code client. The server supports incremental document synchronization, cancellable diagnostic publication, full-document semantic tokens, semantic completion with lazy documentation resolution, documented hover and signature help, go-to-definition, document symbols, workspace symbols, and read-only embedded standard-library navigation. Semantic tokens classify resolved identifiers with declaration, static, readonly, and default-library modifiers; TextMate remains responsible for lexical and unresolved syntax, including scoped `///` XML comments.

Documentation analysis accepts summaries, parameters, returns, remarks, exception and inline references, parameter references, and explicit inheritance. Malformed, unsupported, duplicate, unresolved, invalid-inheritance, and orphan documentation reports warning codes `CT5000` through `CT5006` without blocking checking or C emission. Embedded `System`, compiler-intrinsic, and ESP-IDF descriptions live in XML sidecars, so standard-library source locations and existing generated C remain unchanged.

`ctilde.json` defines deterministic source globs, exclusions, and a hosted or ESP-IDF target. The CLI and language server share the loader. Files without a manifest are analyzed as standalone hosted programs; files outside a manifest source set retain that manifest's target but do not join its compilation.

ESP-IDF projects can declare schema-versioned binding manifests. The CLI resolves the selected IDF target and compilation context through `project_description.json` and `compile_commands.json`, requires Espressif Clang, checks selected functions through AST JSON, compiles strict validation adapters, and atomically refreshes tracked C~ and C outputs. Generated declarations join the project outside ordinary source globs. Check, Build, and debug preparation refresh automatically; `--verify-bindings` provides a clean-tree gate. Structured adapters now preserve mixed native parameter order, validated initializer macros, nested fields, bounded fixed UTF-8 arrays, selected output fields, and owned/borrowed nullable opaque returns. The TCan485 pilot binds timer, hardware RNG, GPIO, NVS, network stack, event loop, Wi-Fi station, HTTPS client, and certificate-bundle APIs without changing Draft 0.15 or runtime ABI 15. Its normal firmware runs a worker-thread HTTPS fetch with deterministic hash and preview reporting when credentials are configured; an empty SSID preserves the clean-checkout offline fallback.

ESP-IDF project builds are incremental by default. All generated text artifacts use one compare-before-replace writer, and a versioned ignored binding cache fingerprints manifests, imported headers, target/compiler context, `sdkconfig`, CMake inputs, tool versions, and tracked outputs. The TCan485 wrapper invokes the compiler once, preserves an initialized target, and reserves `-Clean` for explicit clean builds. On the accepted local ESP32 tree, a warm no-op build took 9.6 seconds end to end: the binding cache check took 69 ms, C~ analysis and emission took 1.1 seconds with zero changed outputs, and the no-native-compilation Ninja phase took 2.5 seconds. A scalar `Program.ct` edit changed one generated namespace module; Ninja compiled that one C object and relinked. The measured source-edit build took 24.8 seconds, including 1.2 seconds in C~ and 17.5 seconds in the native build/link phase. A warm build plus 1,009,888-byte firmware upload took 30.2 seconds at 921600 baud without using the fallback. The flashed image completed its ABI markers, Wi-Fi association, certificate validation, HTTP 200 fetch, ARC recovery, and permanent WS2812 loop without a panic. These timings include PowerShell ESP-IDF environment activation and are measurements, not regression budgets.

The VS Code extension is version 0.4.0 and bundles its JavaScript client, framework-dependent compiler, version 0.3.1 .NET 10 language server, and Node GDB/MI debug adapter. The user supplies the .NET 10 runtime and native debugger. Debug Project creates an instrumented version-3 image; Attach validates its source hashes and metadata and rejects stale v2 images. GCC/Clang, WSL, and ESP-IDF receive logical breakpoints, adapter-owned conditions/hit counts/logpoints, C~-level stepping, lexical locals, hardware data watchpoints, and ARC/runtime presentation including closed generics, interface views, atomics, Thread IDs, and Mutex state. MSVC uses `cppvsdbg`. Protocol and Extension Host suites cover initialization, incremental edits, diagnostics, semantic-token encoding and refresh, lazy completion documentation, documented hover and active parameters, definitions, symbols, target filtering, embedded sources, shutdown, and exit.

The language-service query snapshot owns the same immutable bound program used by compilation. Its per-document indexes reuse bound expression types and symbols without calling `EmitC` or initializing backend state.

## ESP-IDF target

The hardware MVP compiler and project support are implemented. `CompilationOptions` and `--target esp-idf` select one chip-independent profile. It emits `app_main`, compact source locations, unbuffered console startup, four-byte pointer assertions, abort-based fatal failures, and no `ct_keep_symbols` retention routine.

`Esp.Idf` provides FreeRTOS delay and counters, restart and heap counters, a signed 64-bit monotonic microsecond timer, typed `EspError` results for GPIO and one RMT-backed WS2812 strip, and exact error-name copying through the handwritten shim. `System.Environment.Exit` is rejected with `CT4105`.

Draft 0.9 intentionally changes the GPIO configuration/write and WS2812 operation results from `bool` to `EspError`. Boolean sensor data such as `Gpio.Read` remains unchanged.

ESP-IDF 6.0.2 complete modular firmware builds pass for both `esp32` using Xtensa GCC 15.2.0 and `esp32c3` using RISC-V GCC 15.2.0. The ABI 14 T-CAN485 program and the draft 0.13 inline-assembly fixture both link on both targets. Fresh Hello, Exceptions, ARC, Math, operator, vector, and inline-assembly output also passes both cross-compilers in GNU C23 syntax checks with `-Wall -Wextra -Werror`.

Measured self-test firmware sizes are:

| Target | Image | Flash code | Flash data | IRAM/DRAM |
| --- | ---: | ---: | ---: | ---: |
| `esp32` | 154,640-byte binary; 154,525-byte image | 65,222 bytes | 32,704 bytes | 45,003 bytes IRAM; 14,028 bytes DRAM |
| `esp32c3` | 159,728-byte binary; 159,428-byte image | 81,834 bytes | 30,236 bytes | 51,422 bytes DRAM, including 40,102 bytes executable text |

The Draft 0.9 self-test ran on an ESP32-D0WDQ6-V3 revision 3.1 T-CAN485 at `COM4`. In addition to every Draft 0.8 marker, it printed `native utf8: ok`, `opaque defer: ok`, `esp error: ESP_OK`, `delegate context: 42`, and `export: 42`. After the strip was configured and cleared and the managed self-tests returned, the board reported 297,700 bytes of free heap, a 295,112-byte minimum, and 6,552 bytes of main-task stack high-water headroom with the configured 8 KiB stack.

The RMT-backed GPIO4 WS2812 commands completed more than ten 500 ms on/off cycles without a watchdog reset. The same path was previously confirmed by a person to blink the onboard LED green. The separate Draft 0.9 failure image printed `C~ runtime error CTN0001 at RuntimeFailure.ct:23`, entered ESP-IDF `abort()`, and rebooted with `rst:0xc (SW_CPU_RESET)`. The Draft 0.9 self-test image was then rebuilt, reflashed, and verified through every marker and more than ten additional LED cycles as the final board state. The earlier GPIO2 run is retained only as command-level GPIO validation: GPIO2 is the T-CAN485 microSD MISO signal and did not provide a visible blink.

The Draft 0.9 ESP acceptance source repeats mixed acyclic managed allocations for 50 rounds and requires free heap to return within 512 bytes of its baseline. It also checks a scoped UTF-8 call, deferred opaque release, exact ESP error naming, same-task delegate/context entry, a generated export, the timer, virtual delegate, unmanaged function pointer, and native buffer. Both Xtensa and RISC-V ESP cross-compilers accepted it with warnings as errors, complete firmware links passed with the sizes above, and its physical-board acceptance sequence is complete.

The Draft 0.10 firmware adds two attached FreeRTOS workers, cross-task delegate and function-pointer callbacks, per-task exception/defer cleanup, and concurrent ARC lifetime operations. Complete Xtensa and RISC-V links pass. The final 155,360-byte Xtensa image was flashed to the connected dual-core ESP32 on 2026-08-19 and printed `threading: ok`, `exception: caught on ESP32`, `arc heap recovery: True`, and `CTILDE_ESP_OK`. It reported 297,620 bytes free, a 286,624-byte minimum, and 6,520 bytes of stack high-water headroom before continuing for more than ten GPIO4 WS2812 cycles without a watchdog reset.

The optimized Draft 0.12 firmware was built with ESP-IDF 6.0.2 and GCC 15.2.0 for both architectures, then flashed to the same T-CAN485 on 2026-08-20. It passed every current marker, including `threading: ok`, `arc heap recovery: True`, and `CTILDE_ESP_OK`, and reported 297,692 bytes free, a 286,696-byte minimum, and 6,704 bytes of stack high-water headroom. UART showed more than 25 GPIO4 WS2812 transitions without a watchdog reset. The separate failure image produced `CTN0001`, called `abort()`, and rebooted with `SW_CPU_RESET`; the full self-test was reflashed and revalidated as the final board state.

On 2026-08-22, the automated Draft 0.14 ABI 14 hardware runner completed on the connected ESP32-D0WDQ6-V3 revision 3.1 T-CAN485 at COM4 and 460800 baud. The environment used ESP-IDF 6.0.2, Xtensa GCC 15.2.0, and ESP-GDB 17.1. The 168,480-byte ordinary Release binary passed every runtime marker, ARC heap recovery, and 25 alternating WS2812 UART transitions. It reported 295,204 bytes free, a 284,740-byte minimum, and 6,744 bytes of main-task stack headroom. The isolated failure image emitted `CTN0001`, entered `abort()`, and rebooted. The runner restored and flashed the ordinary Release image as its final board state.

The immediate memory follow-up measured the pruned ordinary ESP32 Release image at 164,816 binary bytes and 164,693 image bytes: 73,854 bytes of flash code, 35,184 bytes of flash data, 45,211 bytes of IRAM, and 14,580 bytes of static DRAM. The physical ESP32 reported 297,108 bytes free, a 288,536-byte minimum, and 6,744 bytes of main-task stack headroom. The ESP32-C3 cross-build measured 170,784 binary bytes and 170,484 image bytes: 91,654 bytes of flash code, 32,684 bytes of flash data, and 51,746 bytes of static DRAM. A versioned ESP-IDF 6.0.2/GCC 15.2.0 baseline enforces balanced flash, DRAM, heap, stack, and exact managed-layout limits and requires an explicit `-AcceptMemoryBaseline` update. ELF symbol inspection places retained descriptors, vtables, and literal objects in flash-backed read-only sections.

The connected allocation-failure image caught injected OOM during class, array, box, and dynamic-string allocation, disabled injection, allocated successfully afterward, and returned to zero live objects with allocations and final releases balanced relative to their starting counts. COM4 was verified as the T-CAN485 onboard `VID_1A86&PID_55D4` USB-to-UART bridge. Its raw 460800-baud CRLF wire frame matched the expected ASCII, UTF-8, signed, unsigned, float, and Boolean bytes under strict UTF-8 decoding. The ignored memory-acceptance report is `artifacts/esp32-hardware/20260822-234147.json`; it also records the successful debugger, detach, startup-timeout, and final Release-restore checks. Native USB CDC and ESP32-C3 USB Serial/JTAG remain unvalidated because suitable hardware is not available.

The same acceptance run prepared a guarded instrumented image and drove the bundled debug adapter through DAP. It verified the pre-initialization and first-statement stops, six simultaneous logical source breakpoints, C~ Step Over/Into/Out, five FreeRTOS tasks, caught-exception translation, lexical locals, live ARC-object inspection, intact canaries, a reference-count hardware watchpoint, immediate console forwarding, and complete ARC recovery with 3,356 allocations and 3,356 final releases. Clean Disconnect removed logical and hardware debugger state; four later WS2812 messages arrived without retrapping or rebooting. A separate instrumented boot without a debugger passed the 15-second startup gate after 14.16 seconds. The ignored automated evidence report is `artifacts/esp32-hardware/20260822-155832.json`. After the runner restored the ordinary Release image, the operator confirmed that its onboard GPIO4 WS2812 visibly alternated. This completes the Draft 0.14 ABI 14 physical acceptance.

## Deliberately deferred

These features are outside draft 0.17:

- Generic variance, return-context or partial inference, specialization syntax, and static-abstract generic arithmetic.
- Default or explicit interface implementations and static abstract interface members.
- Independent DLL loading, unloading, and dynamic runtime module registration.
- Parallel renderer row workers; the per-sample RNG contract is ready for them, but the example remains intentionally single-threaded.
- Exception filters, inner exceptions, stack traces, and specialized exception subclasses.
- General exceptions across native boundaries.
- Lambdas, closures, retained callbacks, and callback registration lifetime management.
- Long-lived owned native-resource fields and exported delegates as ordinary ABI values.
- Managed-reference and floating-point atomics, plus compiler-checked ISR or IRAM execution profiles.
- Iterators and yield statements.
- Named, optional, implicit by-reference, and parameter-array arguments.
- User-defined conversions and equality, comparison, bitwise, logical, remainder, increment, or decrement operator declarations.
- Multidimensional and jagged arrays.
- String interpolation and raw or verbatim strings.
- Weak references, cycle collection, finalizers, and automatic disposal.
- Exact-source compilation of the current C# compiler.

## Release gate

A draft 0.17 release requires:

- A zero-warning .NET build.
- All managed and native conformance checks.
- Byte-identical repeated output.
- GNU C23 compilation with warnings as errors.
- MSVC latest-C compatibility compilation with warnings as errors for programs without inline assembly, plus a focused rejection check for programs containing `asm`.
- Documentation synchronized with measured behavior.
- No C output for invalid programs, including stale generated directory output.

The hosted Draft 0.17 software gates and ABI 16 ESP32/ESP32-C3 cross-build gates pass. No Draft 0.17 hardware run was performed; the preserved ABI 15 results remain the latest connected-board evidence.

Draft 0.17 uses GCC or Clang in GNU C23 mode as the canonical native release gate. MSVC latest-C mode remains an independent compatibility check for the portable subset and is not an inline-assembly backend. Unity and modular layouts consume the same optimized typed-IR program and must agree under every supported hosted toolchain. Draft 0.15 ABI 15 is the latest complete measured physical-hardware baseline.
