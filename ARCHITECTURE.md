# Compiler architecture

## Overview

The C~ compiler is a .NET 10 library with one output format, deterministic GNU C23, and two target profiles: hosted and ESP-IDF.

```text
UTF-8 source files
    -> SourceText and locations
    -> Lexer
    -> Parser and immutable syntax trees
    -> Declaration binding
    -> Immutable bound bodies and per-document semantic maps
    -> Flow, allocation-effect, and ARC ownership validation
    -> Target validation
    -> Structured typed three-address IR and cleanup actions
    -> Reachability and semantics-preserving IR optimization
    -> Deterministic GNU C23 emission
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
- `BoundBodyBinder` creates immutable structured statements and expressions with resolved types, symbols, constants, value categories, ownership, flow summaries, extern uses, and allocation effects.
- Binding tracks reachability, returns, loop and switch exits, definite assignment, delayed read-only assignment, and exception/defer cleanup boundaries.
- Ownership-aware lowering classifies values as non-owning, borrowed, owned, or immortal; emits strong-slot replacement, owned-result transfer, nested structure retain/drop helpers, and automatic cleanup records.
- Exception lowering owns automatic lexical handler frames, volatile durable method state, catch dispatch, rethrow, pending finally/defer actions, and cleanup-stack boundaries.
- `AllocationEffectRegistry` records direct allocation reasons and exact or virtual call edges during lowering, computes recursive effects to a fixed point, and verifies `[NoAlloc]` contracts with deterministic witnesses.
- `TypedIrLowerer` consumes bound bodies and produces typed values, basic blocks, loads, stores, calls, conversions, allocations, checks, ownership operations, branches, throws, returns, and cleanup actions. It never classifies rendered C lines.
- `TargetValidator` rejects ABI, generated-symbol, unavailable-platform API, and target-profile conflicts before output starts.
- `TypedIrOptimizer` computes reachable methods from entrypoints, exports, module initializers, address-taken and virtual targets, bound calls, and implicit runtime roots. The emitter receives only that closed program and derives the user layouts and metadata it must retain.
- `CEmitter` owns common runtime emission plus hosted or ESP-IDF entry, failure, console, and source-path policy. Translation-local definitions use portable unused annotations where conservative runtime retention remains necessary; no target emits a symbol-retention routine.

Internal compiler phases share one `DiagnosticBag`. Public callers receive immutable `Diagnostic` values.

### CTilde.Cli

The CLI is the file-system and native-build adapter. It reads `.ct` files, creates syntax trees, prints diagnostics, and writes C only after successful analysis. It atomically replaces generated outputs through temporary files.

Directory mode checks the first line before it removes stale output. It removes only files with the C~ generated-file banner. It preserves handwritten C files.

Emit-only mode stops at deterministic C. Native build mode locks the output directory and then delegates to an installed MSVC, GCC, Clang, or ESP-IDF toolchain. Hosted compiler discovery and ESP-IDF activation are CLI concerns; they do not enter syntax, binding, typed IR, C emission, or the generated ABI.

`CTildeProjectFile` is shared with editor tooling. A `ctilde.json` manifest supplies source and exclusion globs, one compilation target, and optional generated/native build settings. Paths are confined to the manifest directory, deduplicated, and sorted before parsing. Hosted builds directly compile the generated translation unit; advanced native sources and link graphs remain external build-system work. ESP-IDF builds always delegate their graph to CMake and `idf.py`.

### CTilde.LanguageServer

The .NET 10 language server runs out of process over LSP 3.17 and header-delimited UTF-8 JSON-RPC. Standard output contains protocol frames only; logs use standard error.

`LanguageServiceSnapshot` is an immutable editor-facing view over syntax, bound-program semantic maps, compiler diagnostics, structured XML documentation, target-specific standard-library sources, and source positions. A per-document index shares syntax hierarchy, resolved expression types, parent, declaration, and scope information across completion, hover, definition, and semantic-token queries. Documentation is keyed by deterministic symbol IDs that include containing type, member kind, parameter passing kind, and parameter type. Source `///` comments and embedded standard-library XML sidecars feed the same index; documentation analysis never constructs typed IR or backend state. Full-document semantic tokens classify only resolved identifiers; the TextMate grammar retains lexical and unresolved highlighting.

The server applies versioned incremental document changes to in-memory text. Open buffers override disk sources. A 150 ms cancellable debounce publishes diagnostics and requests semantic-token refresh from the newest project snapshot. File and manifest changes invalidate cached snapshots. Completion items carry a documentation ID and snapshot revision; `completionItem/resolve` fetches Markdown only when that revision is still current. Hover and signature help include structured documentation immediately. Semantic tokens use LSP UTF-16 delta encoding and full-document responses; range and delta-result protocols are deferred. One process owns all workspace folders and manifest-defined projects.

The VS Code client starts `dotnet CTilde.LanguageServer.dll`, synchronizes `.ct` and `ctilde.json` changes, maps embedded declarations to the read-only `ctilde-stdlib:` scheme, and contributes the project schema. It also discovers manifests and supplies Check and Build tasks that launch the short-lived CLI. The packaged extension includes framework-dependent server and compiler assemblies; the .NET 10 runtime remains an external requirement for the bundled copies. Window-scoped overrides can select external built server or compiler artifacts. Only the long-running development server needs shadow copying and restart coordination.

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
- Single-base class hierarchies, virtual slots, exact overrides, sealed slots, and constructor initializer targets.
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

Bound statements preserve lexical scopes, control-flow constructs, catches, finally regions, defers, and cleanup boundaries. Typed-IR lowering assigns typed values in source evaluation order and creates explicit blocks, checks, ownership operations, and terminators. Reachability consumes IR call targets and bound semantic references. Function-body rendering still uses the transitional `BodyPipeline` with immutable semantic hints; replacing that final syntax-to-C renderer with instruction-only emission remains the compiler-architecture closure item.

## C emission

The emitter assembles one translation unit in this order:

1. Standard headers and runtime support.
2. String literal data.
3. User-type forward declarations.
4. Enum, object, class, structure, and delegate layouts.
5. Array, box, delegate, structural function-pointer, by-reference ABI, and native-buffer support.
6. Static fields.
7. Function, constructor-initializer, and accessor prototypes.
8. Runtime descriptors, vtables, delegate thunks, and unmanaged callback trampolines.
9. Method, constructor, and accessor definitions.
10. Deterministic static initialization.
11. Hosted C `main` or ESP-IDF `app_main` wrapper.

Emission lazily lowers the already validated bound program to typed IR, computes a reachability closure, and then renders the retained functions. The body optimizer removes unused cleanup boundaries and blanket parameter reads, direct `defer` records avoid exception frames, and fused scalar string builds evaluate segments once from left to right. Calling `GetDiagnostics()` never initializes typed IR or emitter artifacts.

Draft 0.12 adds body-bearing `System.Vec2`, `System.Vec3`, and `System.Vec4` declarations. Compilation detects exact vector identifiers and loads only the corresponding embedded source, while language-service snapshots load all three for discovery and navigation. Draft 0.13 adds a raw inline-assembly syntax node, typed operand bindings, a side-effecting typed-IR instruction, and GNU extended-asm rendering. Assembly text never becomes C identifiers or ordinary expression fragments. Every translation-unit banner identifies draft 0.13, while the managed runtime, ARC header, exception/thread state, export ABI, and generated public-header contract remain draft 0.10 compatible.

Generated identifiers use deterministic UTF-8 byte encoding. User text is never copied directly into a C identifier.

## Planned native interop layer

The current ESP target uses handwritten fixed-width shims. The intended next layer keeps the compiler independent from ESP-IDF while reducing repetitive wrapper code:

1. A binding manifest selects public component headers from the installed ESP-IDF project.
2. A header-aware generator emits editor-visible C~ declarations and companion C adapters.
3. The adapters include the real headers and perform designated initialization, default-macro expansion, static-inline or macro calls, and native error conversion.
4. ESP-IDF compiles and links those adapters as ordinary component sources.

This design follows ESP-IDF's source-compatibility boundary. Native configuration structures, enum numbers, and typedef implementation details do not become durable compiler metadata. Private and example-only headers remain outside generated bindings by default.

The language-side ABI has exact fixed-width and native-width scalars, checked `ref`/`in`/`out`, `void*`, scoped pointer-plus-length native buffers, scoped UTF-8 views, nominal opaque handles, and lexical native ownership. The compiler flattens buffers and UTF-8 inputs and renders qualified declarators from structured types. `defer` reserves opaque release obligations without making native resources managed objects. Broad ESP-IDF coverage still requires header-driven source-compatible binding generation.

Native-to-C~ calls form a separate layer. Unsafe function pointers represent raw C code addresses. Delegates represent ARC-managed method-and-target callables and are not ABI-compatible with function pointers. Draft 0.10 emits body-bearing export wrappers and callback trampolines that accept any attached native thread. The entrypoint attaches an automatic primary state; native-created threads use the generated-header `ct_thread_attach` and `ct_thread_detach` ABI. Wrappers convert escaping exceptions to fatal `CTE0003`; unattached entry fails with `CTT0001`. Retained callback lifetimes and ISR entry remain later profiles because their blocking, allocation, and IRAM-safety rules differ.

## Runtime ownership

The generated translation unit embeds a small runtime:

- Zero-initialized allocation and target-aware deallocation.
- Atomic, non-moving ARC with immortal static strings and a per-thread allocation-free iterative release worklist.
- Generated drop callbacks for classes, arrays, strings, boxes, and reference-bearing structures.
- A common managed-object header, deterministic type descriptors, identity hashes, and typed virtual dispatch.
- Checked reference casts, safe casts, type tests, boxing, and exact unboxing.
- Array allocation and bounds checks.
- Null checks.
- Checked division failure.
- Deterministic 32-bit and 64-bit two's-complement wrapping, division, remainder, negation, and arithmetic-shift helpers.
- Immutable UTF-8 strings and concatenation.
- Console output, hosted UTF-8 input, owned hosted binary-file handles, and process exit.
- Per-attached-thread `setjmp` and `longjmp` handler, current-exception, ownership-cleanup, and release-worklist state.
- One volatile automatic method-state aggregate for values that must remain defined across `longjmp`.
- ARC-aware delegate descriptors, receiver ownership, typed invocation thunks, structural C function-pointer types, and attached-thread callback exception barriers.
- Scoped UTF-8 owner views, nominal opaque-handle ownership checks, deterministic export headers, and conditional entry-task guards.

Managed storage is reclaimed on the thread that atomically releases its reference count to zero. Reference cycles leak, static fields own values until termination, and immortal strings are never released. Exception/defer control state and ownership cleanup records are stack-backed and linked from the current `ct_thread_state`. C~ source has no `delete`, destructor, or finalizer operation.

The runtime phase publishes completed static initialization before public attachment becomes legal. Hosted output stores the state pointer in C thread-local storage. ESP-IDF stores it in a reserved FreeRTOS task-local-storage slot with a task-deletion callback. ARC atomics protect lifetime only; ordinary managed fields and slots still require native synchronization when shared.

Runtime faults remain fatal and bypass the exception stack. `Environment.Exit` also bypasses cleanup. C~ exceptions use managed `System.Exception` objects and descriptor-chain catch matching.

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

ESP-IDF is a target profile, not a separate language backend. It reuses the parser, bound program, typed IR, and C emitter. The profile supplies `app_main`, compact runtime locations, abort behavior, ESP-only declarations, and a fixed-width native shim. Native GPIO and the singleton WS2812/RMT handle stay behind that shim. ESP-IDF retains responsibility for chip selection, component resolution, linking, flashing, and monitoring.

ESP-IDF selects each ESP32 chip toolchain. The C~ compiler must not duplicate chip selection or create one emitter for each ESP32 chip.

See [TODO.md](TODO.md#esp-idf-target-support) for the implementation order and acceptance criteria.
