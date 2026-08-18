# C~ language specification

Specification version: draft 0.4

## Status

This document is the normative specification for C~ draft 0.4.

C~ is a statically typed language with C#-style syntax and a small managed runtime. A conforming draft 0.4 compiler emits deterministic GNU C23 and diagnoses invalid programs before it writes C.

The words **must**, **must not**, **should**, and **may** define language requirements.

## Design goals

- Use familiar C# declaration, type, member, expression, and statement syntax.
- Keep scalar sizes and safe-code behavior deterministic.
- Use GNU C23 as the compilation boundary for hosted and GCC-family embedded targets.
- Separate managed references from explicit unsafe pointers.
- Preserve receiver-first and left-to-right expression evaluation.
- Report structured source diagnostics before emission.
- Support useful classes without a CLR.

C~ is not C#, C++/CLI, or a CLR language.

### C backend dialect

The canonical backend dialect is C23 with GCC-compatible extensions enabled. Generated files are compiled with `-std=gnu23` on current GCC and Clang toolchains. Older toolchains that implemented C23 under its draft name may use `-std=gnu2x` as a compatibility spelling.

A conforming compiler may emit GCC extensions and is not required to provide an ISO-only fallback. Extensions are backend implementation details and do not add syntax or implementation-defined behavior to C~ source programs. Implementations may additionally offer stricter ISO C23 or vendor-specific compatibility modes.

## Example

```csharp
using System;

namespace Examples;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        uint left = 2u;
        uint right = 3u;
        Console.WriteLine(left + right);
    }
}
```

## Source files and compilation

C~ files use the `.ct` extension and UTF-8 text. Invalid UTF-8 is an input error.

One compilation can contain multiple files. All files share declarations and `internal` access. File order must not change name binding or generated symbols.

`SyntaxTree.Parse(SourceText)` returns a full-fidelity immutable tree. Tokens retain leading and trailing trivia. Trivia includes whitespace, newlines, and comments. Missing tokens have zero width. Parser recovery attaches skipped tokens to trivia.

`SyntaxTree.ToFullString()` and `SyntaxNode.ToFullString()` reproduce source text exactly. This rule also applies to invalid input. Each node and token exposes `Span` and `FullSpan`. `ChildNodesAndTokens()` returns source-ordered children.

Bundled standard-library trees are internal trusted inputs. `Compilation.SyntaxTrees` contains only trees supplied by the caller.

A file contains:

1. Zero or more `using` directives.
2. Zero or one namespace declaration.
3. Zero or more type declarations.

Namespace-level variables and functions are not permitted.

### Namespaces

A file can use a file-scoped namespace:

```csharp
namespace Game.World;

public sealed class Entity
{
}
```

It can instead use a block namespace:

```csharp
namespace Game.World
{
    public sealed class Entity
    {
    }
}
```

A file must not mix the two forms or place declarations outside a block namespace.

### Using directives

A `using` directive imports simple type names from one namespace:

```csharp
using System;
using Game.World;
```

Draft 0.4 has no aliases and no `using static`.

The `System` namespace is imported automatically.

## Lexical structure

### Identifiers

An identifier starts with a Unicode letter or underscore. Later characters can contain Unicode letters, decimal digits, and underscores.

Identifiers are case-sensitive. An `@` prefix permits a keyword as an identifier; the prefix is not part of the name.

```csharp
int @class = 1;
```

### Keywords

```text
as        base       bool       break      byte       case
char      class
const     continue   default    do         else       enum
false     float      for        foreach    get        if
in        int        internal   is         namespace  new
null      object     override   private    protected  public
readonly  return     sbyte
sealed    set        short      static     string     struct
switch    this       true       uint       unsafe     ushort
using     var        virtual    void       while
```

`get` and `set` are meaningful only in property declarations. `default` is a switch label.

### Comments and terminators

`//` starts a line comment. `/*` and `*/` delimit a non-nesting block comment.

Declarations and simple statements end with a semicolon. A block does not have a trailing semicolon.

### Numeric literals

Integer literals can use decimal, hexadecimal, or binary notation. Underscores can separate digits.

```csharp
42
1_000_000
0xFF00
0b1010_0110
```

`u` or `U` selects `uint`. `f` or `F` selects `float`.

```csharp
42u
3.5f
```

A literal must fit its selected type. An explicit cast applies normal truncation rules after literal typing.

### Character literals

`char` is one eight-bit UTF-8 code unit. A character literal must encode to exactly one byte.

Supported escapes are `\0`, `\a`, `\b`, `\t`, `\n`, `\v`, `\f`, `\r`, `\"`, `\'`, `\\`, and `\xHH`. A hexadecimal escape contains exactly two hexadecimal digits.

### String literals

Strings use double quotes and the character escape set. Draft 0.4 has no verbatim, raw, or interpolated strings.

String storage is UTF-8. `Length` counts UTF-8 code units, not Unicode scalar values. Indexing returns one read-only `char` code unit.

### Boolean and null literals

`true` and `false` have type `bool`. Integers do not convert to `bool`.

`null` converts to a class, array, string, or unsafe pointer type.

## Type system

Every expression has a compile-time type before C emission.

### Built-in types

| Type | Kind | Size |
| --- | --- | ---: |
| `bool` | Value | 1 byte |
| `byte` | Unsigned value | 1 byte |
| `sbyte` | Signed value | 1 byte |
| `short` | Signed value | 2 bytes |
| `ushort` | Unsigned value | 2 bytes |
| `char` | UTF-8 code-unit value | 1 byte |
| `int` | Signed value | 4 bytes |
| `uint` | Unsigned value | 4 bytes |
| `float` | IEEE-754 binary32 value | 4 bytes |
| `string` | Managed reference | Target pointer width |
| `object` | Managed root reference | Target pointer width |
| `void` | Return marker | None |

Class, array, string, and unsafe pointer values use the native pointer width of the selected C target.

Draft 0.4 has no `long`, `ulong`, `double`, `decimal`, `nint`, or `nuint`.

### Value and reference types

Numeric types, `bool`, structures, and enums are value types. Assignment copies the complete value.

Classes, arrays, strings, and `object` are reference types. Assignment copies object identity. Their default value is `null`.

Class, array, and `object` equality compares identity. String equality compares contents, with two null strings equal.

### Arrays

`T[]` declares a one-dimensional managed array reference.

```csharp
byte[] data = new byte[256];
```

Every array has a read-only `Length` property of type `int`. Indexing starts at zero and checks the receiver, index, and length.

Draft 0.4 has no multidimensional or jagged arrays.

### Unsafe pointers

`T*` is a native pointer to `T`. Pointer syntax and operations require an `unsafe` method or block.

Pointer arithmetic scales by the pointed element size. Dereference and pointer indexing do not perform managed null or bounds checks.

Every type that recursively contains a pointer requires an unsafe context. This rule includes arrays such as `T*[]`.

Safe members must not expose a pointer-containing type through a field, property, parameter, or return type.

### Enumerations

An enum has a fixed underlying type:

```csharp
public enum Direction : byte
{
    North = 0,
    East = 1
}
```

The underlying type can be `byte`, `sbyte`, `short`, `ushort`, `int`, or `uint`. The default is `int`.

Draft 0.4 enum initializers are integral literals. An omitted initializer is one greater than the previous value. Every value must fit the underlying type.

### Conversions

Identity conversions are implicit.

An implicit numeric conversion is valid when the target range contains every source value. Integral values can also convert implicitly to `float` because the range fits, although precision can change.

All other numeric conversions require a cast. Explicit numeric conversions truncate high bits. There is no checked-overflow context.

`null` converts implicitly to reference and pointer types. A derived class converts implicitly to each base class.

Every class, array, and string converts implicitly to `object`. A value type converts implicitly to `object` by boxing.

Pointer boxing requires an unsafe context. Boxing creates a new managed object and copies the value.

An explicit cast can convert between integral and enum types. An unsafe explicit cast can convert one raw pointer type to another.

An explicit cast can downcast a class reference or unbox an exact value type. A failed cast terminates with a stable runtime error.

`value is T` tests the runtime type. `value as T` returns a compatible reference or `null`. The `as` target must be a reference type.

An explicit class cast requires related source and target types. Code can cast through `object` when it needs a runtime type check.

## Declarations and scope

### Locals

```csharp
int count = 0;
var total = count + 10;
```

`var` requires an initializer and adopts its compile-time type. It is not dynamic.

A local must be definitely assigned before its first read. A declaration cannot hide another active local or parameter.

### Constants

`const` requires a compile-time constant initializer. Constant uses substitute the constant value.

```csharp
const int BufferSize = 128 * 2;
```

### Read-only storage

A `readonly` local permits one delayed runtime assignment:

```csharp
readonly int device;
if (condition)
    device = 1;
else
    device = 2;
```

Every reachable read must follow an assignment, and no reachable path can assign the value twice.

A constructor can assign a `readonly` instance field. Each constructor must assign that field exactly once before returning.

## Types and members

### Classes

A class is a managed reference type with fields, properties, constructors, and methods.

```csharp
public sealed class Counter
{
    private int value;

    public Counter(int initial)
    {
        value = initial;
    }

    public void Increment()
    {
        value++;
    }
}
```

Each class has one base class. A class with no base clause derives from `System.Object`.

Classes are open for inheritance by default. The `sealed` modifier prevents inheritance.

Instance methods and properties can use `virtual` and `override`. A `sealed override` prevents another override.

An override must keep the base signature, return type, and accessibility. C~ does not support member hiding.

The `base` expression accesses the direct base implementation. Static classes, structures, and enums cannot have a base clause.

There are no finalizers. External resources require an explicit `Dispose()` method; the name has no compiler magic.

### Structures

A structure is a value type. Assignment, parameters, fields, array elements, and return values copy it.

```csharp
public struct Point
{
    public int X;
    public int Y;

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }
}
```

Every structure constructor must assign every instance field on every reachable path.

### Static classes

A static class contains only static members and cannot be constructed.

### Access

Namespace types can be `public` or `internal` and default to `internal`.

Members can be `public`, `internal`, or `private` and default to `private`. `internal` means accessible anywhere in the same compilation.

`protected` grants access to the declaring class and its derived classes.

An accessor can be less accessible than its property but cannot be more accessible.

### Fields and initialization

Fields can be instance or `static`. A `const` field is implicitly static.

Storage starts at the type's default value. Instance field initializers run before each constructor body. Static field initializers run once before the entry method, in ordinal fully qualified type-name order and source declaration order within each type.

### Properties

A property has a getter, setter, or both:

```csharp
public int Count
{
    get { return count; }
    private set { count = value; }
}
```

`value` is the setter's implicit parameter.

An accessor ending in a semicolon is automatic:

```csharp
public int Count { get; private set; }
```

The compiler creates a private backing field. A missing getter makes the property unreadable; a missing setter makes it unassignable.

## Methods

A method declares a return type, name, parameters, and body.

Methods can be overloaded by parameter types. Resolution includes accessible inherited methods with the correct argument count.

The compiler compares candidates for each argument. Identity is better than widening. One widening target is better when it converts implicitly to the other target only. If two integral widening targets remain, a signed target is better than an unsigned target.

A candidate must be no worse for every argument. It must also be better for at least one argument. Otherwise, the call reports `CT2123`.

Draft 0.4 has no optional, named, `ref`, `in`, `out`, or parameter-array arguments.

### Evaluation order

The receiver is evaluated first. Arguments are evaluated from left to right. Binary operands are evaluated from left to right.

`&&` and `||` evaluate the right operand only when required. Compound assignment evaluates its left expression once.

These rules apply even when the target C compiler leaves an equivalent C expression order unspecified.

### Returns

A `void` method returns no value. A non-void method must return a compatible value on every reachable path.

Statements after an unconditional return, break, or continue are unreachable.

### EntryPoint

Exactly one method has `[EntryPoint]`. It must be a body-bearing `static void` method with no parameters.

The C backend generates `int main(void)`, initializes static storage, calls the entry method, and returns `EXIT_SUCCESS`.

### Extern

`[Extern("symbol")]` marks a static bodyless method supplied by native code.

The symbol must be a portable C identifier. Its native signature must use the C~ mappings in [C_ABI.md](C_ABI.md).

The compiler rejects `main`, runtime names, and generated symbol names. Repeated external names require identical complete ABI signatures. Matching declarations produce one C prototype. An incompatible declaration reports `CT4102`. The diagnostic includes the earlier location.

Unknown attributes, invalid targets, duplicate attributes, and non-constant arguments are errors.

## Core library

The automatically imported `System` namespace provides `Object`, `Console`, and `Environment`. The exact API and runtime behavior are in [STDLIB.md](STDLIB.md).

Draft 0.4 does not provide `System.Type`, reflection, `System.Convert`, or `System.Math`.

## Object and array creation

`new Class(args)` allocates one zero-initialized managed object. It then runs the selected base and same-type constructor chain.

`new Struct(args)` creates and returns a structure value.

`new T[length]` checks the length and allocates a zero-initialized managed array.

If a class or structure declares no constructor, it has an implicit public parameterless constructor. A class constructor calls an accessible base constructor.

A constructor can use `: base(args)` or `: this(args)`. The compiler rejects constructor cycles.

## Expressions

Primary expressions include literals, names, `this`, `base`, member access, calls, indexing, construction, and parentheses.

A non-void call is a value expression and can appear in any compatible context.

### Precedence

| Level | Operators | Association |
| ---: | --- | --- |
| 1 | `x.y`, `f(x)`, `a[x]`, `x++`, `x--` | Left |
| 2 | `+x`, `-x`, `!x`, `~x`, `++x`, `--x`, `(T)x`, `*p`, `&x` | Right |
| 3 | `*`, `/`, `%` | Left |
| 4 | `+`, `-` | Left |
| 5 | `<<`, `>>` | Left |
| 6 | `<`, `<=`, `>`, `>=` | Left |
| 7 | `==`, `!=` | Left |
| 8 | `&` | Left |
| 9 | `^` | Left |
| 10 | `|` | Left |
| 11 | `&&` | Left |
| 12 | `||` | Left |
| 13 | `=`, `+=`, `-=`, `*=`, `/=`, `%=` | Right |

### Arithmetic

Numeric types support `+`, `-`, `*`, and `/`. Integral types also support `%`. Smaller integers promote to `int` or `uint`. Integer division truncates toward zero.

The compiler rejects `float % float`, `float %=`, and `~float` with typing diagnostics.

Signed integer arithmetic wraps in two's-complement form. Division by zero terminates with a runtime failure. `int.MinValue / -1` wraps to `int.MinValue`.

String `+` concatenates two string-compatible operands. A null string operand is empty.

### Comparison and logic

Comparisons produce `bool`. Ordered comparisons require numeric operands or the same enum type.

References and pointers can compare with `null`. String equality compares contents.

`!`, `&&`, and `||` require `bool` operands.

### Bitwise and shifts

Integral types and enums support `~`, `&`, `|`, `^`, `<<`, and `>>`. Binary enum bitwise operands must have the same enum type.

A shift count uses its low five bits after conversion to `int`.

### Assignment

Assignment requires an assignable target and a compatible value. The expression produces the assigned value.

Fields, writable properties, array elements, locals, parameters, and unsafe dereferences can be assignment targets.

## Statements

### Blocks and empty statements

A block creates a lexical scope. A single semicolon is an empty statement.

### Selection

`if` requires a `bool` condition. Braces are optional for one embedded statement.

`switch` accepts an integral or enum value. The compiler converts each case constant to the governing type. It rejects out-of-range and duplicate converted values.

One `default` label is permitted. A section must end with `break`, `continue`, or `return`. Implicit fallthrough is not permitted.

A switch completes a non-void return only when it has `default` and every reachable section returns.

Draft 0.4 has no pattern cases and no `goto case`.

### Loops

`while`, `do while`, and `for` use `bool` conditions. An omitted `for` condition is true.

`foreach` iterates a one-dimensional array from index zero through `Length - 1` and copies each element into the iteration local.

`break` exits the nearest loop or switch. `continue` starts the next iteration of the nearest loop.

A `do` body executes once for definite assignment. The compiler merges normal condition exits with all early `break` exits.

### Return

`return;` exits a void method. `return expression;` exits a non-void method with a converted value.

Constructors do not use a C~ return statement.

## Unsafe code

A method or block can be marked `unsafe`:

```csharp
public static unsafe void Clear(byte* address, int length)
{
    for (int index = 0; index < length; index++)
    {
        address[index] = 0;
    }
}
```

Unsafe code remains statically typed. `unsafe` permits pointer declarations, address-of, dereference, pointer indexing, pointer arithmetic, and pointer casts. It does not disable normal type checking.

Draft 0.4 has no inline assembly.

## Managed lifetime and failures

C~ source has no `delete` operator. Draft 0.4 permits the runtime to retain all managed allocations until process exit.

External resources require explicit release. There is no language `using` statement for disposal.

Managed null access, invalid casts, invalid unboxing, array failures, allocation failures, integer division by zero, and string overflow terminate the program.

## Diagnostics

Every diagnostic contains:

- A stable code.
- Severity.
- Source file.
- One-based line and column.
- Concise message.
- A related declaration location when useful.

The compiler should continue after recoverable lexical, syntax, and semantic errors. It must not emit C when any error remains. Parser traces are disabled unless the CLI receives `--trace`.

## Conformance

A compiler conforms to draft 0.4 when:

1. It implements every non-deferred rule in this document.
2. Invalid programs produce structured diagnostics and no C.
3. Repeated compilation produces byte-identical C.
4. Generated C compiles as GNU C23 without warnings.
5. Native execution passes the language and runtime conformance suite.

The canonical backend is GNU C23. Draft 0.4 has no second backend.

## Deliberate differences from C#

- `char` is one UTF-8 code unit, not UTF-16.
- References and pointers use the target C ABI width.
- Signed integer overflow is always wrapping.
- `readonly` locals permit one delayed assignment.
- Managed allocations can live until process exit.
- The core library is intentionally small.

Draft 0.4 defers interfaces, abstract types, generics, exceptions, delegates, lambdas, iterators, pattern matching, nullable analysis, reflection, dynamic binding, async methods, LINQ, multidimensional arrays, string interpolation, and automatic disposal.
