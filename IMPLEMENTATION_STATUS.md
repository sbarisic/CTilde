# Implementation status and roadmap

## Status

Last reviewed: 2026-08-18

The standalone solution builds successfully for .NET Framework 4.8. The default demonstration also completes the parser and FishAsm code-generation steps.

This result does not validate the generated program. The console harness does not run the assembler or virtual machine.

The current maturity level is an experimental prototype.

## Support matrix

The terms in this table have these meanings:

- **Yes** means the basic path exists.
- **Partial** means the feature has important limits or known defects.
- **No** means the component rejects the feature or has no implementation.

| Feature | Parser | FishAsm | C backend | Status notes |
| --- | --- | --- | --- | --- |
| Modules | Yes | Yes | Yes | One source file only |
| Integer literals | Yes | Yes | Yes | FishAsm parses values as `uint` |
| Decimal literals | Yes | No | No | FishAsm throws during `uint.Parse` |
| Character literals | Yes | Yes | No | Escape set is limited |
| String literals | Yes | Yes | No | Generated labels appear at module end |
| Boolean literals | Yes | Partial | No | `while (true)` works, but `if (true)` fails |
| Scalar declarations | Yes | Partial | Yes | Byte-sized local storage is incorrect |
| Initialized globals | Yes | Partial | Yes | Integers and static strings have paths |
| Uninitialized globals | Yes | No | Yes | FishAsm emits no storage or label |
| Static string arrays | Yes | Partial | No | This is the only allocation form |
| General arrays | Partial | No | Partial | Syntax differs from C |
| Pointers | Partial | Incorrect | Partial | Pointer and pointee loads are confused |
| Address-of | Partial | Partial | No | FishAsm supports labels only |
| Dereference | Yes | No | No | No FishAsm dispatch case |
| Addition and subtraction | Yes | Yes | Yes | No formal precedence rules |
| Multiplication and division | No | No | No | Enum values exist only |
| Comparisons | Yes | Partial | No | Flags work in some paths, values do not |
| Simple assignment | Yes | Partial | No | FishAsm handles identifiers |
| Indexed assignment | Yes | Partial | No | Identifier bases only |
| Function definitions | Yes | Partial | Partial | No signature validation |
| Function declarations | Yes | Partial | No | C backend compiles a null body |
| Function calls as statements | Yes | Incorrect | Yes | Multi-argument order is reversed |
| Function calls as expressions | No | No | No | Return values cannot be consumed normally |
| Return statements | Yes | Partial | No | No type or control-flow checks |
| `if` and `else` | Partial | Incorrect | No | Comparison branches are inconsistent |
| `while` | Partial | Partial | No | Limited conditions and control-state defects |
| `break` | Yes | Incorrect | No | Nested `if` statements can capture it |
| `continue` | Yes | Incorrect | No | Jumps can bypass stack restoration |
| Increment and decrement | Partial | Partial | No | Identifier statements only |
| Classes | Partial | No | Partial | No usable object model |
| Naked functions | Yes | Partial | No | Explicit return emits an invalid epilogue |
| Inline FishAsm | Yes | Yes | No | String literals only |
| Two-argument syscall | Yes | Partial | No | First argument must be a number literal |

## Confirmed high-priority defects

### P0: Function arguments use the wrong order

The caller pushes arguments from first to last. The callee assigns the first parameter to `EBP+8`.

The last pushed argument occupies that slot. All calls with two or more arguments receive reversed values.

### P0: Byte-sized local variables use invalid stack offsets

The first local always receives offset `EBP-4`. A one-byte local reserves only one byte from `ESP`.

Initialized locals also use a four-byte store. This can overwrite nearby stack data.

### P0: Pointer loads use pointee sizes

The identifier backend chooses a pointer element size before it loads the variable. A string parameter therefore loads one byte from its stack slot.

The backend must first load the four-byte pointer value. It can then load the pointee when an expression requests dereference or indexing.

### P0: Conditional branches do not preserve source meaning

The `if` backend jumps to the false branch with the equality jump. The `while` backend uses the opposite jump for the same comparison.

Relational conditions also swap operands in one code path. The backend needs one tested branch-emission routine.

### P0: Loop exits can leave the stack unbalanced

The loop body saves `EAX` and `EBX`. A `break` or `continue` can jump past the matching pop instructions.

`PopLoopLabel()` also returns the top label without removing it. Later loops can observe stale state.

### P1: Uninitialized globals have no definition

The backend emits `.globl name` but does not emit `name:` or reserve bytes.

### P1: Comparisons do not produce Boolean values

Comparison code sets flags only. An assignment from a comparison stores an unrelated register value.

### P1: Parser terminator ownership is inconsistent

Some expression parsers consume their closing token. Others return before it. Callers then make different assumptions.

This causes valid-looking forms such as `if (true)` and function calls in expressions to fail with unrelated token errors.

### P1: Normal compilation prints parser look-ahead

Each statement or expression parse prints five tokens. The compiler needs an optional trace interface instead of unconditional console output.

## Validation baseline

The current baseline uses these checks:

```powershell
dotnet build .\CTilde.sln

Set-Location .\bin
.\Test.exe .\tests\FishAsm.c
```

The build completed with zero warnings and zero errors during the last review.

The compiler generated FishAsm for the included demonstration. The harness did not assemble or execute that output.

Focused review probes had these results:

| Probe | Result |
| --- | --- |
| Integer declaration and addition | Passed |
| Multiplication | `NotImplementedException` |
| Function call in an initializer | Parse failure |
| Decimal literal | `FormatException` |
| `if (true)` | Parse failure |
| Pointer dereference | FishAsm `NotImplementedException` |
| C-style array declaration | Parse failure |
| Empty statement | Parse failure |
| Included sample through C backend | `NotImplementedException` |

## Test gaps

The solution has no automated unit, parser, code-generation, or conformance tests.

The `Test` project is a compiler harness. It has no expected-output comparison and does not return a failure for invalid FishAsm.

The repository needs these test layers:

1. Lexer tests for every token and escape sequence.
2. Parser tests for valid trees and precise invalid syntax.
3. Binder and type-checker tests after those phases exist.
4. FishAsm snapshot tests for small language constructs.
5. Assembler tests for every emitted instruction and directive.
6. End-to-end Fishmachine tests that check visible program output.
7. Regression tests for every P0 and P1 defect in this document.

## Roadmap

### Phase 1: Select the canonical compiler

Choose this repository or the embedded Fishmachine compiler as the source of truth.

Move shared compiler code into one project. Make Fishmachine reference that project instead of keeping a copy.

Record the supported FishAsm and bytecode versions.

### Phase 2: Add correctness foundations

Replace fixed look-ahead parsing with a grammar that has explicit precedence and terminator ownership.

Add a binder with nested scopes and symbol tables. Add a type checker with explicit expression result types.

Define and test one calling convention. Separate pointer values, array storage, and pointee loads.

Build control-flow blocks before instruction emission. Track stack depth at every branch target.

### Phase 3: Repair the current language subset

Fix parameter order, local offsets, uninitialized globals, condition branches, loop labels, and Boolean results.

Implement function calls as expressions. Complete dereference and address-of behavior for local and global values.

Make arrays use one documented syntax and storage model.

### Phase 4: Add diagnostics and tests

Replace general exceptions with diagnostics that contain a code, message, file, line, and column.

Remove unconditional token output. Add an optional compiler trace.

Create automated tests for the frontend, backend, assembler, and virtual machine path.

### Phase 5: Complete language features

Implement multiplication, division, modulo, unary operators, logical operators, and bitwise operators.

Add `for`, `switch`, structures, enums, member access, casts, and function pointers only after the core is stable.

Define whether classes remain part of C~. If they remain, specify layout, construction, destruction, allocation, and method calls.

### Phase 6: Add user tooling

Create a compiler command that accepts input and output paths. Add options for the backend, diagnostics, and debug traces.

Document the Fishmachine toolchain. Add versioned examples and a small standard library.

## Release criteria

A first usable release should meet all these conditions:

- The standalone and Fishmachine projects use one compiler source.
- All P0 and P1 defects in this document have regression tests.
- The compiler has deterministic diagnostics and no debug output by default.
- The supported language subset has a conformance suite.
- Generated FishAsm assembles and runs through Fishmachine in automated tests.
- The README example produces checked program output.
