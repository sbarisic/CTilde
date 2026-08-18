# Compiler architecture

## Overview

The C~ compiler is a .NET 10 library with one output format: deterministic GNU C23.

```text
UTF-8 source files
    -> SourceText and locations
    -> Lexer
    -> Parser and immutable syntax trees
    -> Declaration and type model
    -> Semantic and control-flow analysis
    -> Typed expression and statement lowering
    -> Deterministic GNU C23 emission
    -> External C compiler
    -> Native executable
```

The compiler emits no C when any error diagnostic is present. It does not contain an assembler, virtual machine, native linker, or second code generator.

## Project boundaries

### CTilde

The `CTilde.Compiler` assembly owns the complete language implementation.

- `SourceText` owns UTF-8-decoded text, line starts, spans, and one-based source locations.
- `Lexer` owns tokenization, comments, literals, escapes, Unicode identifiers, and lexical diagnostics.
- `Parser` owns declarations, statements, Pratt expression precedence, recovery, and immutable syntax nodes.
- `CompilationModel` owns namespaces, imports, declared symbols, types, overload signatures, attributes, and the bundled standard-library surface.
- `MethodLowerer` performs method-body binding, definite assignment, access checks, conversions, ordered evaluation, control-flow lowering, and typed C expression construction.
- `CEmitter` owns the C runtime, layouts, symbol names, declarations, initialization, definitions, and entry wrapper.

Internal compiler phases share one `DiagnosticBag`. Public callers receive immutable `Diagnostic` values.

### CTilde.Cli

The CLI is a thin file-system adapter. It reads one or more `.ct` files, creates syntax trees, creates one `Compilation`, prints diagnostics, and writes the generated translation unit only after successful analysis.

The CLI does not invoke a C compiler. This keeps C emission deterministic and leaves native toolchain selection to the caller.

### Test

The conformance runner exercises the public API and compiles generated C with a real C compiler. On Windows it discovers Visual Studio with `vswhere`. The `CTILDE_CC` environment variable selects another compiler.

Native tests use temporary directories and check process output, error text, and exit codes.

## Public API lifecycle

```csharp
SourceText text = SourceText.From(source, path);
SyntaxTree tree = SyntaxTree.Parse(text);
Compilation compilation = Compilation.Create(new[] { tree });

ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();
EmitResult result = compilation.EmitC(writer);
```

`SyntaxTree` contains parser diagnostics immediately. `Compilation` lazily adds cached syntax trees from the embedded standard library, then builds the program model and generated C once. Its public `SyntaxTrees` collection continues to expose only caller-supplied trees. Subsequent diagnostics and emission requests reuse the immutable result, so repeated emission is byte-identical.

`EmitC` writes nothing when `EmitResult.Success` is false.

## Syntax and recovery

The lexer removes whitespace and comments while preserving token spans. Invalid input produces a token or diagnostic and continues whenever possible.

The parser uses:

- Recursive descent for files, namespaces, types, members, and statements.
- A Pratt parser for unary and binary expressions.
- Right-recursive parsing for assignment.
- Token synchronization at type boundaries.
- Missing zero-width tokens after recoverable expectation failures.

The syntax tree contains source syntax only. It does not contain resolved types or generated C names.

## Symbols and types

The declaration pass creates all user types before it declares members. This permits references to types declared later or in another input file.

The semantic model owns:

- Fully qualified namespace and type names.
- Class, structure, enum, field, property, constructor, method, parameter, and local symbols.
- Static and instance membership.
- Accessibility.
- Fixed-width built-in types, arrays, pointers, and target-width references.
- Automatically imported declarations from the bundled C~ standard-library sources.

Overload resolution filters by name, static or instance context, argument count, and implicit conversions. Identity conversions score before widening conversions. Equal best scores produce an ambiguity diagnostic.

## Flow and lowering

Method lowering carries explicit lexical scopes and assignment state.

- Branches merge local and required-field assignment state by intersection.
- Loops do not make body-only assignments definite after the loop.
- `readonly` assignment counts are merged across branches.
- Return coverage and unreachable statements are checked before successful emission.
- Loop and switch exits lower to unique labels, so nested control flow cannot capture the wrong target.

A lowered expression contains:

- Its resolved `CType`.
- Ordered prerequisite statements.
- A C expression for its value.
- Optional lvalue store and address operations.
- Optional constant value information.

Receivers and operands with side effects are placed in generated temporaries. Calls evaluate the receiver first and arguments from left to right. Compound assignments evaluate their target once. Short-circuit operators lower their right operand into a conditional block.

This ordered prerequisite list is the compiler's typed intermediate representation between binding and text emission.

## C emission

The emitter assembles one translation unit in this order:

1. Standard headers and runtime support.
2. String literal data.
3. User-type forward declarations.
4. Enum and aggregate layouts.
5. Array layouts and allocators.
6. Static fields.
7. Function and accessor prototypes.
8. Method, constructor, and accessor definitions.
9. Deterministic static initialization.
10. Symbol-retention routine.
11. C `main` wrapper.

Emission first lowers all bodies and initializers into memory. This discovers every array specialization and string literal before section ordering begins.

Generated identifiers use deterministic UTF-8 byte encoding. User text is never copied directly into a C identifier.

## Runtime ownership

The generated translation unit embeds a small runtime:

- Zero-initialized program-lifetime allocation.
- Array allocation and bounds checks.
- Null checks.
- Checked division failure.
- Two's-complement wrapping helpers for signed arithmetic.
- Immutable UTF-8 strings and concatenation.
- Console output and process exit.

Managed storage is not reclaimed before process exit. C~ source has no `delete` operation.

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

Runtime failures use separate short codes such as `CTN0001` for null access and `CTA0003` for array bounds.

## Extension rules

A new language feature must define syntax, binding, conversions, ordered lowering, diagnostics, generated C, and positive and negative tests together.

Do not add backend-specific decisions to syntax nodes. New output targets must consume resolved or lowered forms and must not recreate name or type resolution inside an emitter.
