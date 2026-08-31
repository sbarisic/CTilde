# Compiler architecture

## Overview

The C~ compiler is a .NET 10 library with one backend, deterministic GNU C23, two artifact layouts, and four target profiles: hosted, ESP-IDF, GNU/ELF freestanding, and x86-64 Cosmopolitan.

Draft 0.40 retains the x86-64 Cosmopolitan profile and the exact repository-module pipeline. Source-owner identities, binary64 and Unicode scalars, fixed-width SIMD, immutable embedded resources, lambdas, ARC closures, native imports, and reachability-pruned monotonic timing all reach the existing typed IR and C backend. `TimeSpan`, deterministic `Random`, and spin primitives are ordinary standard-library source; only the clock and scalar-math native leaves require emitter support. The target-specific Cosmopolitan design is in [COSMOPOLITAN.md](COSMOPOLITAN.md).

```text
UTF-8 source files
    -> SourceText and locations
    -> Lexer
    -> Parser and immutable syntax trees
    -> Declaration binding
    -> Immutable bound bodies and per-document semantic maps
    -> Flow, general-effect, and ARC ownership validation
    -> Target validation
    -> Structured typed three-address IR and cleanup actions
    -> Reachability and semantics-preserving IR optimization
    -> Deterministic unity or modular GNU C23 artifacts
    -> External C compiler
    -> Native executable
```

The compiler emits no C when any error diagnostic is present. It does not contain an assembler, virtual machine, native linker, or second code generator.

## Project boundaries

### CTilde

The `CTilde.Compiler` assembly owns the complete language implementation.

- `SourceText` owns UTF-8-decoded text, line starts, spans, and one-based source locations.
- `Lexer` owns tokenization, trivia, literals, escapes, Unicode identifiers, and lexical diagnostics.
- `Parser` owns declarations, statements, Pratt expression precedence, recovery, missing tokens, and skipped-token trivia.
- `CompilationModel` owns namespaces, imports, declared symbols, types, overload signatures, attributes, and the bundled standard-library surface.
- `BoundBodyBinder` creates immutable structured statements and expressions with resolved types, symbols, constants, value categories, ownership, flow summaries, extern uses, and direct effect operations.
- Binding tracks reachability, returns, loop and switch exits, definite assignment, delayed read-only assignment, and exception/defer cleanup boundaries.
- Ownership-aware lowering classifies values as non-owning, borrowed, owned, or immortal; emits strong-slot replacement, owned-result transfer, nested structure retain/drop helpers, and automatic cleanup records.
- Exception lowering owns automatic lexical handler frames, volatile durable method state, catch dispatch, rethrow, pending finally/defer actions, and cleanup-stack boundaries.
- `EffectRegistry` records allocation, exception, blocking, runtime-use, and call operations during analysis-only lowering. `EffectAnalyzer` computes recursive effects to a deterministic fixed point, applies trusted and inherited contracts, and reports full witnesses for `[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]`. `InterruptValidator` reuses that graph for the implicit ISR profile, closes native boundaries, and marks IRAM/DRAM residency before typed-IR emission. Freestanding heap inference and runtime-hook bootstrap validation query the same result.
- `TypedIrLowerer` consumes bound bodies and produces typed values, basic blocks, loads, stores, calls, conversions, allocations, checks, ownership operations, branches, throws, returns, and cleanup actions. It never classifies rendered C lines.
- `TargetValidator` rejects ABI, generated-symbol, unavailable-platform API, and target-profile conflicts before output starts.
- `TypedIrOptimizer` computes reachable methods from entrypoints, exports, module initializers, address-taken and virtual targets, bound calls, and implicit runtime roots. The emitter receives only that closed program and derives the user layouts and metadata it must retain.
- `CEmitter` owns reusable runtime, public-header, internal-header, source-owned, entry/lifecycle, symbol-map, and CMake fragments plus explicit hosted, ESP-IDF, or freestanding policy. Unity and modular layouts consume the same reachable optimized `TypedIrProgram`; partitioning never changes semantic lowering. `[Used]` declarations receive target-specific final-image retention directives, while ordinary translation-local definitions remain eligible for removal. Freestanding runtime roles and naked exports are immutable method metadata preserved through cloning and reachability.
- `NameMangler` owns canonical identities and the 96-bit SHA-256 compact symbol scheme. `EmitSymbolMap` serializes the deterministic identity ledger used to diagnose collisions and debug generated output.
- Debug emission has `None`, `Source`, and `Instrumented` modes. Source mode adds coalesced project-relative `#line` mappings, resets generated runtime regions to `<ctilde-generated>`, and retains stable exception hooks. Instrumented mode also assigns dense logical probe IDs, tracks method depth and activation through cleanup records, preserves user storage for inspection, and writes deterministic version-3 site, scope, variable, constructed-generic, interface-view, atomic, managed-thread, and runtime-control metadata without embedding toolchain paths.

Internal compiler phases share one `DiagnosticBag`. Public callers receive immutable `Diagnostic` values.

### CTilde.Cli

The CLI is the file-system and native-build adapter. It reads `.ct` files, creates syntax trees, prints diagnostics, and writes C only after successful analysis. One shared atomic writer compares encoded bytes first, so unchanged C, headers, maps, descriptors, binding outputs, and CMake fragments retain their timestamps.

Directory mode checks the first line before it removes stale output. It removes only files with the C~ generated-file banner. It preserves handwritten C files.

Emit-only mode stops at deterministic C artifacts. Native build mode locks the output directory and then delegates to an installed MSVC, GCC, Clang, ESP-IDF, or GNU/ELF cross toolchain. Hosted modular builds compile source files separately and cache objects by generated content, both shared-header hashes, compiler identity, configuration, and flags; linking begins only after every object succeeds. ESP-IDF receives the generated CMake source fragment and owns incremental compilation. Freestanding uses a dedicated driver that validates target macros, compiles generated and declared `.c`/`.S`/`.s` sources without builtins or startup support, and links declared ELF objects and archives through a contained linker script and explicit entry symbol. Object-cache identities use `CompilerContract.DraftVersion`. Hosted compiler discovery, freestanding linker construction, and ESP-IDF activation are CLI concerns; they do not enter syntax, binding, typed IR, C emission, or the generated ABI. Run mode completes and unlocks a native build before it starts the configured host or WSL process. It launches an argument array without shell evaluation, forwards standard streams, applies validated environment overrides, and maps configured success exit codes. Debug preparation selects Instrumented mode, disables LTO, applies variable-preserving debug flags, and writes a machine-local version-3 descriptor with artifact paths, debugger selection, memory-diagnostic mode, and source hashes. Attach performs no build and accepts only matching instrumented version-3 artifacts.

`CTildeProjectFile` is shared with editor tooling. A `ctilde.json` manifest supplies source and exclusion globs, one compilation target, `cLayout`, generated paths, optional symbol map, native build settings including hosted Release LTO, platform-selected hosted runtime files, and an optional immutable run configuration. Source globs remain confined to the manifest directory. Runtime-file inputs are explicit manifest-relative files and may point at a checked-in repository-level dependency; their output names cannot contain paths. The hosted build driver stages selected assets after linking through atomic replacement, records hashes in an output receipt, gives Linux executables an `$ORIGIN` search path, and removes only unchanged compiler-staged copies during Clean. Generated-bundle replacement prunes only compiler-marked files and refuses to overwrite handwritten files. ESP-IDF builds always delegate their graph to CMake and `idf.py`.

### CTilde.LanguageServer

The .NET 10 language server runs out of process over LSP 3.17 and header-delimited UTF-8 JSON-RPC. Standard output contains protocol frames only; logs use standard error.

`LanguageServiceSnapshot` is an immutable editor-facing view over syntax, bound-program semantic maps, compiler diagnostics, structured XML documentation, target-specific standard-library sources, and source positions. A per-document index shares syntax hierarchy, resolved expression types, parent, declaration, and scope information across completion, hover, definition, and semantic-token queries. Documentation is keyed by deterministic symbol IDs that include containing type, member kind, parameter passing kind, and parameter type. Source `///` comments and embedded standard-library XML sidecars feed the same index; documentation analysis never constructs typed IR or backend state. Full-document semantic tokens classify only resolved identifiers; the TextMate grammar retains lexical and unresolved highlighting.

The server applies versioned incremental document changes to in-memory text. Open buffers override disk sources. A 150 ms cancellable debounce publishes diagnostics and requests semantic-token refresh from the newest project snapshot. File and manifest changes invalidate cached snapshots. Completion items carry a documentation ID and snapshot revision; `completionItem/resolve` fetches Markdown only when that revision is still current. Hover and signature help include structured documentation immediately. Semantic tokens use LSP UTF-16 delta encoding and full-document responses; range and delta-result protocols are deferred. One process owns all workspace folders and manifest-defined projects.

The VS Code client starts `dotnet CTilde.LanguageServer.dll`, synchronizes `.ct` and `ctilde.json` changes, maps embedded declarations to the read-only `ctilde-stdlib:` scheme, and contributes the project schema. It also discovers manifests and supplies Check, Build, Run, Debug, and Attach workflows that launch the short-lived CLI. A standalone Node DAP process drives GDB through token-correlated MI records. It installs logical source and function breakpoints by writing the probe bitmap, owns condition, hit-count, logpoint, and stepping state, maps lexical storage through version-3 metadata, and reserves native hardware watchpoints for data breakpoints. Version-3 metadata adds closed-generic names, interface views, atomic storage, runtime thread IDs, and managed Thread/Mutex presentation to the packed-control and stop-cache model. Its runtime scope traverses the optional intrusive ARC registry without calling target methods. ESP UART sessions use the ESP-IDF Python environment as a persistent serial-to-GDB pipe, so interrupt, target stdout, and remote-protocol bytes remain on one open port. Instrumented ESP output uses the same probe lock to serialize GDB target-output packets and falls back to the ROM UART when the session is inactive, independently of the selected ESP-IDF C library. Instrumented ESP Launch waits up to 15 seconds before initialization, allowing the adapter to install probes before it releases startup. Clean ESP termination clears debugger state and watchpoints, advances an active logical trap once, and uses the GDB-stub kill command to continue firmware execution. MSVC delegates to `cppvsdbg`. The packaged extension includes framework-dependent server and compiler assemblies plus the debug adapter; the .NET 10 runtime remains an external requirement for the bundled copies. Window-scoped overrides can select external built server or compiler artifacts. Only the long-running development server needs shadow copying and restart coordination.

### Test

The conformance runner exercises the public API and compiles generated C. On Windows it finds Visual Studio with `vswhere`. `CTILDE_CC` selects MSVC, GCC, Clang, `wsl:gcc`, or `wsl:clang`.

The GNU adapter tries `gnu23` first. It retries with `gnu2x` only after an unsupported-option error. `CTILDE_C_STANDARD` overrides the dialect.

Native tests use temporary directories and check process output, error text, and exit codes.

HostedIo is also an end-to-end object-model fixture. Its acyclic scene graph stores `Hittable` references in an ARC-owned array and stores one material reference in each sphere. Virtual `[NoAlloc]` hit and scatter calls exercise polymorphic dispatch inside a recursive renderer. Production settings remain in the example entry point, while conformance supplies a separate small entry point over the same source files so native tests do not run the 500-sample render.

## Public API lifecycle

```csharp
SourceText text = SourceText.From(source, path);
SyntaxTree tree = SyntaxTree.Parse(text);
Compilation compilation = Compilation.Create(
    new[] { tree },
    new CompilationOptions(CompilationTarget.Hosted));

ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();
EmitResult result = compilation.EmitC(writer);
```

`SyntaxTree` contains parser diagnostics immediately. `Compilation` lazily adds cached common and target-specific standard-library trees. Its public `SyntaxTrees` collection exposes only caller-supplied trees. `CompilationOptions` is immutable and defaults to `Hosted`; its optional absolute `SourceRoot` validates rooted user inputs and produces normalized relative hosted runtime paths. Invalid or outside-root inputs report `CT4106`. ESP-IDF keeps compact filenames and rejects a source root.

`GetDiagnostics()` runs declarations, immutable body binding, flow/effect analysis, and target validation. It does not construct a C emitter, C writer, typed IR, or translation unit. `EmitC()` lazily lowers and caches the backend result after successful analysis. Repeated emission is byte-identical.

`EmitCHeader()` consumes the validated `BoundProgram` directly. It does not initialize typed IR or the C emitter. This keeps export discovery, native headers, layouts, ownership comments, runtime attachment declarations, and prototypes available to tooling without backend side effects.

`EmitC` writes nothing when `EmitResult.Success` is false.

## Syntax and recovery

The lexer retains whitespace, newlines, comments, and invalid text. Each token has leading trivia, trailing trivia, `Span`, and `FullSpan`. Try, catch, finally, and throw nodes use the same full-fidelity model.

The parser uses:

- Recursive descent for files, namespaces, types, members, and statements.
- A Pratt parser for unary and binary expressions.
- Right-recursive parsing for assignment.
- Token synchronization at type boundaries.
- Missing zero-width tokens after recoverable expectation failures.
- Skipped tokens attached to the next token as recovery trivia.

Valid and invalid trees round-trip exactly through `ToFullString()`. `ChildNodesAndTokens()` returns source-ordered children. Syntax trees do not contain resolved types or generated C names.

## Symbols and types

The declaration pass creates all user types before it declares members. This permits references to types declared later or in another input file.

The semantic model owns:

- Fully qualified namespace and type names.
- Class, structure, enum, delegate, field, property, constructor, method, operator, parameter, and local symbols.
- Single-base class hierarchies, multiple interface contracts, per-concrete immutable interface dispatch tables, abstract and virtual slots, exact overrides, sealed slots, and constructor initializer targets.
- Generic definitions, type parameters and constraints, and canonical interned closed types and methods. Constraint validation and value-argument inference occur before typed-IR binding; emission reaches only closed monomorphized instances.
- Static and instance membership.
- Accessibility.
- Fixed-width built-in types through 64 bits, native-sized integers, arrays, managed delegates, structural unmanaged function pointers, unsafe pointers, scoped native buffers, and target-width references.
- Automatically imported declarations from the bundled C~ standard-library sources.

Overload resolution filters by name, context, argument count, and implicit conversions. It compares candidates per argument. A winner must be no worse for every argument. It must be better for at least one.

Inherited accessible methods join the overload set. An override replaces its base implementation in the same slot. A different signature remains an overload.

Arithmetic operator declarations are tagged static method symbols. Candidate lookup starts from both operand types and their class base chains, deduplicates shared declarations, and reuses the ordinary implicit-conversion and better-candidate rules. Selected operators lower as direct ARC-aware calls with dedicated `ct_op_*` names; they never enter virtual slots or ordinary member lookup.

## Flow and lowering

Control-flow analysis carries explicit lexical scopes and assignment state.

- Branches merge local and required-field assignment state by intersection.
- Loops do not make body-only assignments definite after the loop.
- `readonly` assignment counts are merged across branches.
- Return coverage and unreachable statements are checked before successful emission.
- Loop and switch exits lower to unique labels, so nested control flow cannot capture the wrong target.
- A `do` body contributes assignments to its condition path because it executes once.
- Switch break exits remain separate from return exits.
- Case constants convert to the governing type before range and duplicate checks.
- A throw is a non-fallthrough exit. Catch bodies start with the assignment state from before the try.
- A finally body also starts with the pre-try assignment state because any call can throw. Normal try, catch, and finally assignments merge for subsequent code.
- Return, break, continue, and exception exits that cross finally lower to an explicit pending action and one cleanup label.
- A direct block `defer` captures its receiver and converted arguments immediately into automatic state. Capture ownership records are pushed before its invocation record, so LIFO unwinding invokes the call and then releases its captures. It does not create a synthetic try/finally or `setjmp` region.

A bound expression contains:

- Its resolved `CType`.
- Its resolved declaration or method group.
- Optional constant value information.
- Its value, variable, type, method-group, or error category.
- Its non-owning, borrowed, owned, or immortal ownership classification.
- Immutable bound child expressions.

Generated temporaries hold receivers and operands with side effects. Calls and overloaded operators evaluate inputs from left to right. Compound assignments evaluate their target once, preserve its old value through right-operand evaluation, and use normal strong-slot replacement. Short-circuit operators lower the right operand into a conditional block.

Bound statements preserve lexical scopes, control-flow constructs, catches, finally regions, defers, and cleanup boundaries. Typed-IR lowering assigns typed values in source evaluation order and creates explicit blocks, checks, ownership operations, and terminators. Reachability consumes IR call targets and bound semantic references. After reachability, `TypedIrEmissionLowerer` creates immutable function and static-initializer emission plans only for retained IR. `CEmitter` consumes those plans and never reopens a method syntax body. The former `BodyPipeline`, `CBodyLowerer`, and `LoweredExpression` transition layers have been removed.

## C emission

The emitter first assembles reusable fragments in this order:

1. Standard headers and runtime support.
2. String literal data.
3. User-type forward declarations.
4. Enum, object, class, structure, and delegate layouts.
5. Array, box, delegate, structural function-pointer, by-reference ABI, and native-buffer support.
6. Static fields.
7. Function, constructor-initializer, and accessor prototypes.
8. Runtime descriptors, vtables, delegate thunks, and unmanaged callback trampolines.
9. Method, constructor, and accessor definitions.
10. Deterministic module initialization and reverse finalization.
11. Hosted C `main`, ESP-IDF `app_main`, explicit freestanding lifecycle functions, or a runtime-free naked export.

Emission lazily lowers the already validated bound program to semantic typed IR, computes a reachability closure, and creates immutable emission plans for the retained functions and module initializers. The typed-IR optimizer attaches deterministic facts for cleanup-record liveness, constants, non-null values, and movable owned results; emission dataflow additionally tracks fixed array lengths. Emission consumes those facts to omit empty lexical cleanup boundaries, move fresh ownership into strong slots and return payloads, remove immortal-string ARC traffic, elide only dominated null and fixed-index bounds checks, and simplify constant loops and valid constant `stackalloc` sizes. Facts are invalidated conservatively at `ref`/`out` aliases, unsafe calls, inline assembly, fields, nullable native results, and control-flow joins; dynamic safety checks remain when proof is incomplete. Instrumented output retains its logical source probes and lexical storage even when a neighboring native operation disappears.

The artifact emitter composes only retained plans. Unity concatenates the fragments. Modular output shares runtime and internal headers, writes one runtime source, partitions definitions by canonical source identity, and writes an entry/lifecycle source, symbol map, and CMake source fragment. A body-only edit therefore changes only its owning source artifact; the broad internal header remains shared. Direct `defer` records avoid exception frames, and fused scalar string builds evaluate segments once from left to right. Calling `GetDiagnostics()` never initializes typed IR, a C writer, or emitter artifacts.

Draft 0.12 adds body-bearing vectors. Draft 0.13 adds typed GNU inline assembly. Draft 0.14 replaces the process runtime and storage model. Draft 0.15 adds interfaces, generics, atomics, and managed concurrency. Draft 0.16 adds aggregate-layout metadata and symbolic layout operators. Draft 0.17 adds immutable native-section metadata and deterministic placement. Draft 0.18 adds an analysis-only compile-time evaluator, architecture state, inactive-branch pruning, assertion layout dependencies, native data and retention metadata, MMIO intrinsic lowering, and task-entry export plans. Draft 0.19 extends substitutions and specialization identities with typed constants, adds inline value layouts and nominal aliases, carries alignment metadata, validates closed call graphs, and centralizes ordinary-memory CPU lowering beside MMIO. Draft 0.20 adds endian-domain values, linker-address expressions, final-image retention, scalar bitfields and register lowering, canonical source ownership, and configurable ESP-IDF panic termination. Draft 0.21 adds explicit target predicates, freestanding availability checks, runtime-hook closure analysis, heap-requirement inference, runtime-free naked emission, and a dedicated ELF driver. Draft 0.22 replaces allocation-only analysis with general effect contracts and additive symbol-map facts. Draft 0.23 adds ESP-IDF-native interrupt roots, transitive effect/residency validation, and direct IRAM entry emission. Draft 0.24 adds explicit Cosmopolitan target state, a single-architecture wrapper probe, content-addressed APE object compilation, a retained ELF carrier, and deterministic APE unwrapping. Draft 0.25 adds callable assembly-only methods, complete raw naked assembly bodies, and restricted immutable structured constant-data evaluation before module initialization. Assembly text, section names, effects, and native symbols never become ordinary expression fragments.

Drafts 0.26 through 0.34 add source-owner identities, IEEE-754 `double`, fixed-width SIMD, `rune`, `[Embed]`, captureless lambdas, explicit value captures with ARC-managed closure state, and exact-pinned repository modules with locks, aliases, vendoring, local replacements, and explicit update policies. These features extend binding, ownership, typed IR, deterministic emission, project resolution, and debug metadata without adding another native backend.

Generated global identifiers use kind-specific prefixes and the first 96 bits of SHA-256 over centralized canonical identities. The versioned symbol map preserves full identities and source locations. User text is never copied directly into a global C identifier.

## Native interop and binding generation

The ESP target keeps handwritten fixed-width shims and adds an explicit project binding layer without coupling the core compiler to ESP-IDF:

1. A schema-versioned manifest selects public component headers and declarations.
2. The CLI asks `idf.py reconfigure` for the actual compile database and project description, then validates selected declarations through Espressif Clang AST JSON and a strict adapter translation unit.
3. Deterministic tracked C~ declarations expose functions, constants, opaque types, callbacks, flattened configuration operations, and selected output structures. Companion C adapters include the real headers, preserve native parameter order, apply validated initializer macros, map nested fields, bound fixed UTF-8 arrays, and perform native invocation.
4. An ignored generated CMake fragment adds adapter sources and selected component requirements to the owning component.

This follows ESP-IDF's source-compatibility boundary. Native configuration structures, enum numbers, and typedef implementation details do not become durable compiler metadata. Opaque returns carry explicit owned/borrowed and nullable contracts, so ordinary C~ ownership lowering checks their cleanup without exposing native layouts. Private, `esp_private`, example, preview, and experimental headers require an explicit unstable opt-in. Generated adapter symbols are project-private and do not alter runtime ABI 16 or the public native header.

The language-side ABI has exact fixed-width and native-width scalars, checked `ref`/`in`/`out`, `void*`, scoped pointer-plus-length native buffers, scoped UTF-8 views, nominal opaque handles, and lexical native ownership. The compiler flattens buffers and UTF-8 inputs and renders qualified declarators from structured types. `defer` reserves opaque release obligations without making native resources managed objects. Binding manifests accept identifiers and structured mappings only; arbitrary compiler flags and source fragments remain rejected.

Native-to-C~ calls form a separate layer. Unsafe function pointers represent raw C code addresses. Delegates represent ARC-managed method-and-target callables and are not ABI-compatible with function pointers. Export wrappers and callback trampolines accept any attached native thread. The entrypoint initializes the process runtime and primary thread; native-created threads use the generated-header attachment ABI, while Draft 0.15 `Thread` workers attach and detach internally. Wrappers convert escaping exceptions to panic `CTE0003`; unattached entry panics with `CTT0001`. Open generics, interface views, atomic storage, and managed Thread/Mutex objects do not cross native ABI boundaries. Draft 0.23 interrupt entries bypass this wrapper layer and instead require a statically closed, runtime-free, non-blocking IRAM/DRAM call graph. Retained callback lifetimes and generalized ISR signatures remain later profiles.

Draft 0.39 adds a hosted-only dynamic native edge without changing that ABI. Semantic method symbols retain logical library and symbol metadata; reachability registers only imports used by retained typed IR or module initialization. Emission groups and orders those imports, creates structurally typed private slots, and emits one platform loader lifecycle in the runtime unit. Startup resolves every slot before module initialization. Shutdown finalizes C~ static storage before unloading libraries in reverse order. `[Extern]` remains the independent link-time mechanism. Loader handles never enter C~ symbols, public headers, or managed ownership.

## Runtime ownership

Unity output embeds one small runtime; modular output defines the same runtime once and imports it from every generated module:

- Zero-initialized allocation and target-aware deallocation.
- Atomic, non-moving ARC with immortal static strings and a per-thread allocation-free iterative release worklist.
- Generated drop callbacks for classes, contiguous arrays and strings, boxes, and reference-bearing structures.
- A common managed-object header, deterministic type descriptors, identity hashes, and typed virtual dispatch.
- Checked reference casts, safe casts, type tests, boxing, and exact unboxing.
- Allocation-free throws of immortal built-in runtime-fault exceptions with per-thread diagnostic origins.
- Deterministic 32-bit and 64-bit two's-complement wrapping, division, remainder, negation, and arithmetic-shift helpers.

Freestanding replaces host allocation, panic, TLS, exception, and libc dependencies with selected runtime-role methods, one static execution state, direct terminal faults, and internal byte loops. Runtime code is omitted entirely for a naked-only program. Its header advertises this choice through `CTILDE_HAS_RUNTIME`.
- Immutable UTF-8 strings and concatenation.
- Console output, hosted UTF-8 input, owned hosted binary-file handles, and process exit.
- Per-attached-thread `setjmp` and `longjmp` handler, current-exception, ownership-cleanup, and release-worklist state.
- One volatile automatic method-state aggregate for values that must remain defined across `longjmp`.
- ARC-aware delegate descriptors, receiver ownership, typed invocation thunks, structural C function-pointer types, and attached-thread callback exception barriers.
- Scoped UTF-8 owner views, nominal opaque-handle ownership checks, deterministic export headers, and general per-thread attachment checks.

Managed storage is reclaimed on the thread that atomically releases its reference count to zero. Reference cycles leak, static fields own values until module finalization, and immortal strings and fault objects are never released. Exception/defer control state and ownership cleanup records are stack-backed and linked from the current `ct_thread_state`. C~ source has no `delete`, destructor, or finalizer operation.

`ct_runtime_initialize` attaches the primary thread, creates fault singletons, validates runtime ABI 16, initializes the module descriptor, and publishes ready. `ct_runtime_shutdown` rejects attached secondary threads, finalizes managed static fields in reverse order, drains ARC work, and detaches the primary thread. Hosted output stores the state pointer in C thread-local storage. ESP-IDF stores it in a reserved FreeRTOS task-local-storage slot with a task-deletion callback. Source-created workers use the same attachment contract. ARC atomics protect lifetime only; ordinary managed fields and slots require `Atomic<T>`, volatile publication, or mutex/thread synchronization when shared.

Recoverable runtime faults enter the ordinary exception stack through immortal standard exception singletons and allocate nothing. Runtime-phase, attachment, ABI, ARC, cleanup, and native-boundary violations remain panics. `Environment.Exit` also bypasses cleanup. C~ exceptions use managed `System.Exception` objects and descriptor-chain catch matching.

A class layout starts with its complete base-class structure. `System.Object` starts with `ct_object`, which contains the descriptor, immutable identity hash, four-byte atomic reference count, and intrusive release link. Strings, arrays, and boxes use the same header. Descriptors contain generated drop callbacks. Class allocation installs the most-derived descriptor before any initializer runs. Non-allocating constructor initializer functions then execute the base or same-type chain on that allocation; a throwing initializer releases the partial object through the current thread's cleanup stack.

Unsafe pointers lower to native C pointers. `nint` and `nuint` lower to `intptr_t` and `uintptr_t`. Unmanaged function pointers lower to exact C function-pointer signatures, and `ref`/`out` versus `in` lower to writable versus const pointers. Native-buffer locals are scoped pointer-plus-length structures, while ABI parameters flatten to adjacent pointer and `size_t` values. Stack allocation uses checked compiler alloca support. Raw pointer operations bypass managed checks; native-buffer indexing remains bounds-checked.

## Diagnostics

Diagnostic code ranges are stable by phase:

| Range | Owner |
| --- | --- |
| `CT0xxx` | Lexing and parsing |
| `CT1xxx` | Declarations, names, access, and attributes |
| `CT2xxx` | Types, conversions, and expressions |
| `CT3xxx` | Definite assignment and control flow |
| `CT4xxx` | C layout and emission |

Runtime failures use separate short codes such as `CTN0001` for null access, `CTA0003` for array bounds, and `CTE0001` for an unhandled exception.

## Extension rules

A new language feature must define syntax, binding, conversions, ordered lowering, diagnostics, generated C, and positive and negative tests together.

Do not add backend-specific decisions to syntax nodes. New output targets must consume resolved or lowered forms and must not recreate name or type resolution inside an emitter.

Future fixed-width SIMD follows the same rule. Binding resolves portable lane and mask semantics; typed IR carries a target-neutral SIMD operation, shape, inputs, and constant immediates; C emission selects compiler-owned helpers from a validated architecture and CPU-feature set. GCC vector attributes, MSVC intrinsic names, Neon types, and scalar fallback details must not leak into syntax, symbols, effects, or ordinary call binding. Stable 16-byte source storage remains distinct from temporary native register representation. See [FUTURE_FEATURES.md](FUTURE_FEATURES.md#fixed-width-128-bit-simd).

ESP-IDF is a target profile, not a separate language backend. It reuses the parser, bound program, typed IR, and C emitter. The profile supplies `app_main`, compact runtime locations, abort behavior, ESP-only declarations, and a fixed-width native shim. Native GPIO and the singleton WS2812/RMT handle stay behind that shim. ESP-IDF retains responsibility for chip selection, component resolution, linking, flashing, and monitoring.

Cosmopolitan must likewise remain a target profile rather than a backend fork. Unlike ESP-IDF, it uses hosted process/runtime semantics. Unlike ordinary hosted builds, it produces an APE plus an ELF debug carrier through supported Cosmopolitan wrappers. The initial x64 profile uses one semantic architecture. A true x64/AArch64 image requires two independently bound and lowered programs followed by cross-slice ABI validation and `apelink`; compiling one architecture-pruned C program twice is invalid.

ESP-IDF selects each ESP32 chip toolchain. The C~ compiler must not duplicate chip selection or create one emitter for each ESP32 chip.

See the [ESP-IDF](TODO.md#esp-idf) and [native-interop](TODO.md#native-interop) roadmap sections for the remaining callback, ISR, weak-linkage, custom-linker-script, and hardware-validation work.
