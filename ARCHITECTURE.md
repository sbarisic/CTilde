# Compiler architecture

## Overview

The C~ compiler is a .NET 10 library with one output format: deterministic GNU C23.

```text
UTF-8 source files
    -> SourceText and locations
    -> Lexer
    -> Parser and immutable syntax trees
    -> Declaration binding
    -> Combined body binding, flow analysis, and C-fragment lowering
    -> Exception cleanup and handler lowering
    -> Transitional typed-line IR adapter
    -> Target validation
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
- `MethodLowerer` currently combines name binding, access and conversion checks, overload resolution, flow analysis, and ordered C-fragment lowering.
- The same pass tracks reachability, returns, loop and switch exits, definite assignment, and delayed read-only assignment.
- Exception lowering owns lexical handler frames, durable local slots, catch dispatch, rethrow, and pending finally actions.
- `TypedIrLowerer` currently classifies the rendered function lines into typed instruction categories. This is a transition adapter, not the final three-address IR design.
- `TargetValidator` rejects ABI and generated-symbol conflicts before output starts.
- `CEmitter` consumes the transitional IR and owns the runtime, layouts, declarations, initialization, definitions, and entry wrapper.

Internal compiler phases share one `DiagnosticBag`. Public callers receive immutable `Diagnostic` values.

### CTilde.Cli

The CLI is a thin file-system adapter. It reads `.ct` files, creates syntax trees, and prints diagnostics. It writes C only after successful analysis. It atomically replaces the destination through a temporary file.

Directory mode checks the first line before it removes stale output. It removes only files with the C~ generated-file banner. It preserves handwritten C files.

The CLI does not invoke a C compiler. This keeps C emission deterministic and leaves native toolchain selection to the caller.

### Test

The conformance runner exercises the public API and compiles generated C. On Windows it finds Visual Studio with `vswhere`. `CTILDE_CC` selects MSVC, GCC, Clang, `wsl:gcc`, or `wsl:clang`.

The GNU adapter tries `gnu23` first. It retries with `gnu2x` only after an unsupported-option error. `CTILDE_C_STANDARD` overrides the dialect.

Native tests use temporary directories and check process output, error text, and exit codes.

## Public API lifecycle

```csharp
SourceText text = SourceText.From(source, path);
SyntaxTree tree = SyntaxTree.Parse(text);
Compilation compilation = Compilation.Create(new[] { tree });

ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();
EmitResult result = compilation.EmitC(writer);
```

`SyntaxTree` contains parser diagnostics immediately. `Compilation` lazily adds cached internal standard-library trees. Its public `SyntaxTrees` collection exposes only caller-supplied trees.

`GetDiagnostics()` runs declarations, the combined body pass, transitional IR construction, and target validation. It does not assemble the C translation unit. `EmitC()` consumes the cached result after successful analysis. Repeated emission is byte-identical.

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
- Class, structure, enum, field, property, constructor, method, parameter, and local symbols.
- Single-base class hierarchies, virtual slots, exact overrides, sealed slots, and constructor initializer targets.
- Static and instance membership.
- Accessibility.
- Fixed-width built-in types, arrays, pointers, and target-width references.
- Automatically imported declarations from the bundled C~ standard-library sources.

Overload resolution filters by name, context, argument count, and implicit conversions. It compares candidates per argument. A winner must be no worse for every argument. It must be better for at least one.

Inherited accessible methods join the overload set. An override replaces its base implementation in the same slot. A different signature remains an overload.

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

A bound expression contains:

- Its resolved `CType`.
- Ordered prerequisite statements.
- A C expression for its value.
- Optional lvalue store and address operations.
- Optional constant value information.

Generated temporaries hold receivers and operands with side effects. Calls evaluate the receiver first. Arguments evaluate from left to right. Compound assignments evaluate their target once. Short-circuit operators lower the right operand into a conditional block.

The combined lowering pass spills receivers, arguments, operands, indices, and compound targets in source order. The C emitter does not repeat name lookup, overload selection, type conversion, or flow analysis.

The target architecture replaces the combined pass with immutable bound declarations and bodies, then lowers those bodies to typed operands, locals, blocks, calls, conversions, loads, stores, and branches. That replacement remains implementation work.

## C emission

The emitter assembles one translation unit in this order:

1. Standard headers and runtime support.
2. String literal data.
3. User-type forward declarations.
4. Enum, object, class, and structure layouts.
5. Array and box layouts and allocators.
6. Static fields.
7. Function, constructor-initializer, and accessor prototypes.
8. Runtime descriptors, vtables, and dispatch thunks.
9. Method, constructor, and accessor definitions.
10. Deterministic static initialization.
11. Symbol-retention routine.
12. C `main` wrapper.

Emission first lowers all bodies and initializers into memory. This discovers every array specialization and string literal before section ordering begins.

Generated identifiers use deterministic UTF-8 byte encoding. User text is never copied directly into a C identifier.

## Runtime ownership

The generated translation unit embeds a small runtime:

- Zero-initialized program-lifetime allocation.
- A common managed-object header, deterministic type descriptors, identity hashes, and typed virtual dispatch.
- Checked reference casts, safe casts, type tests, boxing, and exact unboxing.
- Array allocation and bounds checks.
- Null checks.
- Checked division failure.
- Two's-complement wrapping helpers for signed arithmetic.
- Immutable UTF-8 strings and concatenation.
- Console output and process exit.
- A single-thread `setjmp` and `longjmp` handler stack for C~ exceptions.
- Heap-backed parameters and locals in methods with try statements, so modified C automatic storage is not read after `longjmp`.

Managed storage is not reclaimed before process exit. C~ source has no `delete` operation.

Runtime faults remain fatal and bypass the exception stack. `Environment.Exit` also bypasses cleanup. C~ exceptions use managed `System.Exception` objects and descriptor-chain catch matching.

A class layout starts with its complete base-class structure. `System.Object` starts with `ct_object`. Strings, arrays, and boxes use the same header. Class allocation installs the most-derived descriptor before any initializer runs. Non-allocating constructor initializer functions then execute the base or same-type chain on that allocation.

Unsafe pointers lower to native C pointers. Unsafe operations bypass managed null and bounds checks but remain statically typed.

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

ESP-IDF is a planned target profile, not a separate language backend. It must reuse typed IR and replace only platform runtime and packaging behavior.

ESP-IDF selects each ESP32 chip toolchain. The C~ compiler must not duplicate chip selection or create one emitter for each ESP32 chip.

See [TODO.md](TODO.md#esp-idf-target-support) for the implementation order and acceptance criteria.
