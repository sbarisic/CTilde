# Compiler architecture

## Scope

This document describes the standalone CTilde solution. The solution contains a compiler library and a console demonstration.

The repository does not contain the FishAsm assembler or the Fishmachine virtual machine.

## Pipeline

```text
C~ source
    |
    v
Lexer -> Tokenizer -> Parser and AST -> Language backend -> Text output
                                                    |
                                      +-------------+-------------+
                                      |                           |
                                      v                           v
                                  FishAsm text                C source text
```

The parser builds the abstract syntax tree directly. Most abstract syntax tree nodes contain their own parsing method.

The compiler has no separate binding, semantic analysis, typed intermediate representation, or optimization phase.

## Projects

### CTilde

`CTilde/CTilde.csproj` builds the compiler library for .NET Framework 4.8.

The public entry points are:

- `Tokenizer` reads a file or a `TextReader`.
- `Parser` creates an `Expr_Module` tree.
- `LangProvider` defines the text backend interface.
- `FishAsmProvider` emits FishAsm.
- `CLangProvider` emits a small C source subset.

### Test

`Test/Test.csproj` builds a console harness for .NET Framework 4.8.

The harness accepts one source path. It uses `Test/tests/FishAsm.c` when no argument is present in the build output directory.

The harness always uses `FishAsmProvider`. It writes `out.asm` in the current directory.

The project name is historical. It is not an automated test project and contains no assertions.

## Frontend

### Lexer

`Lexer.cs` is a generic configurable lexer. It tracks source positions and produces whitespace, comment, identifier, keyword, symbol, number, and quoted-string tokens.

The file is much larger than the C~-specific lexical configuration. Most C~ rules come from `Tokenizer` settings.

### Tokenizer

`Tokenizer.cs` configures keywords, symbols, comments, and quote characters. It reads the complete token stream into an array.

The tokenizer removes comments and whitespace. It supports `NextToken()` and fixed look-ahead through `Peek()`.

The tokenizer has no rewind, checkpoint, or error-recovery feature.

### Parser and abstract syntax tree

`Parser.Parse()` delegates to `Expr_Module.Parse()`. The module repeatedly calls `Expression.ParseStatement()` until the token stream ends.

`Expression.ParseStatement()` selects a statement type through token look-ahead. `Expression.ParseExpression()` builds expression nodes.

This design has three important limits:

1. Statement parsing depends on fixed token patterns.
2. Expression parsing has no operator-precedence table.
3. Parser methods consume terminators inconsistently.

The parser does not create a symbol table. It also does not resolve names or check types.

## Abstract syntax tree

Files under `CTilde/Expr` define the abstract syntax tree.

The main node groups are:

| Group | Nodes |
| --- | --- |
| Structure | Module, block, class, function, parameters |
| Declarations | Type, variable, initialized variable, static value |
| Values | Identifier, number, character, string |
| Operations | Math, comparison, index, address, dereference, increment |
| Statements | Assignment, call, return, if, while, break, continue |

The tree stores type names as strings. It does not carry resolved type identities, conversions, or expression result types.

Several nodes implement `ToSourceStr()`, but the base method is optional and throws by default. Backends use these methods mainly for comments.

## Backend interface

`LangProvider` owns a `StringBuilder` and indentation state. A backend implements one method:

```csharp
public abstract void Compile(Expression expression);
```

The backend appends output as it walks the tree. `CompileToSource()` returns the complete string.

The interface has no diagnostic sink, output sections, source map, relocation model, or capability query.

## FishAsm backend

`FishAsmProvider` uses a large type switch over abstract syntax tree nodes. It emits text instructions and data directives immediately.

`FishCompileState` stores mutable compilation state:

- Global and local variable records
- Stack and parameter offsets
- Generated labels
- Loop and break label stacks
- Flags for the current compilation context

The state is global to one backend instance. It does not model nested lexical scopes.

### Current calling convention

The backend intends to use this stack frame:

```text
EBP + 8   first parameter
EBP + 12  second parameter
...
EBP + 4   return address
EBP + 0   saved EBP
EBP - N   local storage
```

Scalar return values use `EAX`. The caller removes argument slots after a call.

Each argument uses a four-byte stack slot. The current caller pushes arguments in source order, which reverses the parameter mapping.

### Function emission

A normal function uses this prologue:

```text
PUSH_REG %ebp
MOVE_REG_REG %esp, %ebp
```

A normal return uses this epilogue:

```text
LEAVE
RET
```

The backend also emits an implicit epilogue after every function body. An explicit return can therefore make a second unreachable epilogue.

### Data emission

Initialized globals emit a global label followed by data. Integer values use `.long`.

Static string arrays use `.Raw`. String literals receive generated `.L_` labels and `.String` directives at module end.

Uninitialized globals do not receive storage. General static arrays are not implemented.

### Interrupt wrappers

The backend treats each function name that starts with `handler_` as an interrupt handler. It emits an implementation function and a register-saving wrapper.

This behavior belongs in an explicit function attribute. A name prefix is easy to trigger by accident and is difficult to validate.

## C source backend

`CLangProvider` walks the same abstract syntax tree and writes C-like source.

It supports modules, blocks, classes, functions, parameters, types, declarations, identifiers, integer constants, simple math, and calls.

It does not support most statements or expressions accepted by the parser. It cannot translate the included FishAsm demonstration.

The C backend also mixes statement formatting with expression formatting. For example, every function-call node emits a semicolon and newline.

Treat this backend as an unfinished experiment.

## External Fishmachine integration

The [Fishmachine](https://github.com/sbarisic/Fishmachine) project contains the assembler, linker, bytecode format, virtual machine, and graphical host.

Fishmachine also contains a newer embedded C~ compiler. That copy has more syntax and a larger type system than this repository.

The two compiler copies have changed independently. This standalone repository cannot serve as the compiler dependency until the projects select a canonical source.

## Main architectural gaps

The next compiler design needs these explicit phases:

```text
Source
  -> Lexer
  -> Parser
  -> Syntax tree
  -> Binder and scope resolver
  -> Type checker
  -> Typed intermediate representation
  -> FishAsm code generator
  -> Assembler and linker
  -> Fishmachine bytecode
```

The binder must own names, scopes, function signatures, and class or structure members.

The type checker must own conversions, pointer rules, expression types, return checks, and lvalue checks.

The intermediate representation must make control flow and stack lifetimes explicit. This step removes many current backend state errors.

The compiler should also return structured diagnostics instead of throwing general exceptions.
