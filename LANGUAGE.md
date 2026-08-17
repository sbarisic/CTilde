# C~ language specification

Specification version: draft 0.2

## Status

This document defines the proposed C~ language. The design uses C# syntax and a small systems runtime for Fishmachine.

The current compiler does not implement this specification. [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) describes the implemented prototype and its known defects.

The words **must**, **must not**, **should**, and **may** define language requirements in this document.

## Design goals

C~ has these design goals:

- Use familiar C# declaration, type, member, expression, and statement syntax.
- Keep runtime behavior small enough for Fishmachine.
- Separate safe code from pointer and inline assembly operations.
- Define deterministic type sizes and a stable application binary interface.
- Produce clear compile-time diagnostics before code generation.
- Support classes without requiring the full .NET runtime.

C~ does not try to implement the complete C# language or the Common Language Runtime.

The first conforming version does not require generics, exceptions, delegates, reflection, dynamic binding, tasks, or language-integrated query.

## Example program

```csharp
using Fishmachine.Runtime;

namespace Examples;

public static class Program
{
    [EntryPoint]
    public static void Main()
    {
        uint left = 2;
        uint right = 3;
        uint result = left + right;

        FishVm.Syscall(2, result);
        FishVm.Stop();
    }
}
```

This example shows the design target. The current compiler does not accept this complete program.

## Source files

C~ source files should use the `.ct` extension. A source file contains zero or more `using` directives and type declarations.

A type can belong to the global namespace or one declared namespace.

The compiler must support UTF-8 source text. It must not depend on the host culture for identifiers or numeric literals.

### File layout

A file can use a file-scoped namespace:

```csharp
namespace Game.World;

public sealed class Entity
{
}
```

A file can also use a block namespace:

```csharp
namespace Game.World
{
    public sealed class Entity
    {
    }
}
```

A file must not contain both namespace forms. Namespace-level variables and functions are not permitted.

Place functions and mutable state in a class or structure. Use a static class for program-wide functions and state.

### Using directives

A `using` directive imports type names from one namespace.

```csharp
using Fishmachine.Runtime;
using Game.World;
```

The first conforming version does not require aliases or `using static`.

## Lexical structure

### Identifiers

An identifier starts with a Unicode letter or underscore. Later characters can contain Unicode letters, decimal digits, and underscores.

Identifiers are case-sensitive. `Player`, `player`, and `PLAYER` name different symbols.

An `@` prefix permits a keyword as an identifier.

```csharp
int @class = 1;
```

The `@` character is not part of the declared name.

### Keywords

The core language reserves these keywords:

```text
bool      break      byte       case       char       class
const     continue   default    do         else       enum
false     float      for        foreach    if         in
int       internal   namespace  new        null       private
protected public     readonly   return     sbyte      sealed
short     static     string     struct     switch     this
true      uint       unsafe     ushort     using      var
void      while
```

`get` and `set` are contextual keywords inside property declarations.

Future specifications can reserve more keywords. A compiler must not reserve undocumented words.

### Comments

C~ supports line comments and block comments.

```csharp
// A line comment ends at the next line break.

/* A block comment can span lines. */
```

Block comments do not nest.

### Statement terminators

Declarations and simple statements end with `;`. Blocks do not require a trailing semicolon.

### Numeric literals

Integer literals can use decimal, hexadecimal, or binary notation. An underscore can separate digits.

```csharp
42
1_000_000
0xFF00
0b1010_0110
```

The suffix `u` or `U` selects an unsigned integer literal. The suffix `f` or `F` selects a floating-point literal.

```csharp
42u
3.5f
```

The compiler must diagnose a literal that does not fit its target type.

### Character literals

A `char` literal uses single quotes.

```csharp
'A'
'\n'
'\x7F'
```

C~ supports `\0`, `\a`, `\b`, `\t`, `\n`, `\v`, `\f`, `\r`, `\"`, `\'`, and `\\`.

A hexadecimal escape uses exactly two hexadecimal digits. A `char` contains one eight-bit code unit.

### String literals

A string literal uses double quotes.

```csharp
"Hello, world!"
"Line one\nLine two"
```

Strings use UTF-8 storage and end with a zero byte at Fishmachine boundaries. The string length does not include that terminator.

`Length` returns the number of UTF-8 code units. Indexing a string returns one read-only `char` code unit.

The first conforming version does not require verbatim strings, raw strings, or string interpolation.

### Boolean and null literals

`true` and `false` have type `bool`. `null` can convert to a reference type or pointer type.

C~ does not use integer values as Boolean values.

## Type system

C~ is statically typed. Every expression has a compile-time type before code generation starts.

Types belong to one of three groups:

- Value types contain their data directly.
- Reference types refer to runtime-managed objects.
- Pointer types contain unchecked machine addresses.

### Built-in types

| Type | Kind | Size | Description |
| --- | --- | ---: | --- |
| `bool` | Value | 1 byte | `true` or `false` |
| `byte` | Value | 1 byte | Unsigned integer |
| `sbyte` | Value | 1 byte | Signed integer |
| `short` | Value | 2 bytes | Signed integer |
| `ushort` | Value | 2 bytes | Unsigned integer |
| `char` | Value | 1 byte | UTF-8 code unit |
| `int` | Value | 4 bytes | Signed integer |
| `uint` | Value | 4 bytes | Unsigned integer |
| `float` | Value | 4 bytes | IEEE 754 binary32 value |
| `string` | Reference | 4 bytes | Immutable UTF-8 string |
| `void` | Return marker | None | No return value |

The Fishmachine target uses 32-bit addresses. Every reference and pointer therefore occupies four bytes.

Unlike C#, C~ defines `char` as an eight-bit UTF-8 code unit. This choice matches the Fishmachine text interface.

The first conforming version does not require `long`, `ulong`, `double`, `decimal`, `nint`, or `nuint`.

### Value types

Numeric types, `bool`, structures, and enumerations are value types. Assignment copies the complete value.

Each value type has a default value. Numeric types use zero, `bool` uses `false`, and each structure field uses its default value.

### Reference types

Classes, arrays, and strings are reference types. Assignment copies the reference, not the object.

Reference equality compares object identity. String equality compares string contents.

The default value of a reference type is `null`. Nullable reference analysis is not part of draft 0.2.

### Pointer types

`T*` declares a pointer to `T`. Pointer types are valid only in an `unsafe` context.

```csharp
unsafe
{
    int value = 10;
    int* pointer = &value;
    *pointer = 20;
}
```

Pointer arithmetic scales an integer offset by the pointed type size. Pointer operations do not perform bounds or null checks.

### Array types

`T[]` declares a one-dimensional array reference. The array length belongs to the object, not the type.

```csharp
byte[] data = new byte[256];
```

Each array has a read-only `Length` property of type `int`. Array indexing starts at zero and performs a bounds check.

Draft 0.2 does not require multidimensional or jagged arrays.

### Enumerations

An enumeration defines named integral constants.

```csharp
public enum Direction : byte
{
    North = 0,
    East = 1,
    South = 2,
    West = 3
}
```

The underlying type can be `byte`, `sbyte`, `short`, `ushort`, `int`, or `uint`. The default underlying type is `int`.

### Type conversions

The compiler permits an implicit numeric conversion when every source value fits the target type.

Other numeric conversions require an explicit cast.

```csharp
byte small = 10;
int wide = small;
byte narrowed = (byte)wide;
```

An explicit numeric conversion truncates excess high bits. The first conforming version does not require checked overflow contexts.

Reference conversions follow the declared class hierarchy when inheritance becomes available. Draft 0.2 defines no user class inheritance.

## Declarations and scope

### Local variables

A local variable declaration specifies a type or uses `var`.

```csharp
int count = 0;
var total = count + 10;
```

`var` requires an initializer. It does not make a variable dynamically typed.

A local variable must receive a value before its first read. The compiler must report a definite-assignment error otherwise.

### Constants

A `const` variable receives a compile-time constant value at its declaration.

```csharp
const int BufferSize = 256;
```

The initializer must contain constants only. The compiler substitutes the value where the program uses the constant.

### Read-only variables

`readonly` permits one runtime assignment. This rule applies to fields and local variables in C~.

```csharp
readonly int deviceId;
deviceId = ReadDeviceId();
```

A read-only local can receive a value at most once. It must receive that value before its first read.

A constructor can assign a read-only instance field. Each constructor must assign that field before it returns.

This local-variable behavior is a C~ extension to C#.

### Scope

A block creates a lexical scope. A nested block can read symbols from its parent scope.

A declaration cannot hide another local variable from an active parent scope. Fields and local variables can share a name through explicit `this` access.

```csharp
private int count;

public void SetCount(int count)
{
    this.count = count;
}
```

## Classes and structures

### Classes

A class is a reference type with fields, properties, constructors, and methods.

```csharp
public sealed class Counter
{
    private int value;

    public Counter(int initialValue)
    {
        value = initialValue;
    }

    public int Value
    {
        get { return value; }
        private set { this.value = value; }
    }

    public void Increment()
    {
        value++;
    }
}
```

A constructor uses the class name and has no return type. C~ does not use `__ctor` or `__dtor` keywords.

Draft 0.2 permits the `sealed` modifier only as documentation of the default rule. User classes do not support inheritance yet.

The compiler does not generate a finalizer. A type must expose `Dispose()` when it owns an external resource.

### Structures

A structure is a value type with fields, constructors, properties, and methods.

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

Structure assignment copies all fields. A structure cannot inherit from another type.

Each structure constructor must assign every instance field before it returns.

### Static classes

A static class contains only static members and cannot be instantiated.

```csharp
public static class MathHelpers
{
    public static int Square(int value)
    {
        return value * value;
    }
}
```

Use a static class for functions that do not belong to an object.

### Access modifiers

C~ supports `public`, `internal`, `protected`, and `private`.

Top-level types default to `internal`. Class and structure members default to `private`.

Draft 0.2 reserves `protected` for future inheritance support. A compiler can accept it but must report that the target does not support inheritance.

### Fields

A field stores data in a class, structure, or static class.

```csharp
private int count;
public static readonly uint Version = 1u;
```

An instance field belongs to each object. A static field has one value for the program.

### Properties

A property exposes accessors through field-like syntax.

```csharp
public int Count
{
    get { return count; }
    private set { count = value; }
}
```

The implicit `value` parameter contains the assigned value in a setter.

Draft 0.2 also permits auto-properties:

```csharp
public int Count { get; private set; }
```

The compiler creates a hidden backing field for an auto-property.

### Member access

The `.` operator selects an instance or static member.

```csharp
counter.Increment();
int value = counter.Value;
int squared = MathHelpers.Square(value);
```

An instance method uses `this` for the current object. A static method has no `this` value.

## Methods

### Method declarations

A method declaration contains modifiers, a return type, a name, parameters, and a body.

```csharp
public static uint Add(uint left, uint right)
{
    return left + right;
}
```

Each parameter has a type and a name. The argument count and argument types must match the selected method.

Draft 0.2 supports overloads with different parameter lists. It does not require optional, named, `ref`, `in`, `out`, or parameter-array arguments.

### Evaluation order

The runtime evaluates the receiver first. It then evaluates arguments from left to right.

The calling convention must preserve that language order. Stack push order is a backend detail and must not change parameter values.

### Return values

A `void` method returns no value. A non-void method must return a compatible value on every reachable path.

```csharp
public int GetValue()
{
    return value;
}
```

The compiler must report unreachable statements after an unconditional return.

### Entry point

One static method must have the `[EntryPoint]` attribute.

```csharp
[EntryPoint]
public static void Main()
{
}
```

The entry point must return `void` and take no parameters in draft 0.2. The Fishmachine backend maps it to the runtime entry symbol.

## Attributes

An attribute attaches compile-time metadata to a declaration.

```csharp
[EntryPoint]
[Interrupt(2)]
[Naked]
```

An attribute name uses an identifier. Arguments must be compile-time constants.

Draft 0.2 defines these standard attributes:

| Attribute | Target | Meaning |
| --- | --- | --- |
| `EntryPoint` | Static method | Program entry method |
| `Interrupt(number)` | Static method | Fishmachine interrupt handler |
| `Naked` | Static method | No generated prologue or epilogue |
| `Extern(symbol)` | Static method | Function supplied by another module |
| `Intrinsic(name)` | Static method | Compiler or runtime intrinsic |

The compiler must validate attribute targets and argument types.

An interrupt handler name has no special meaning. `[Interrupt]` replaces the current `handler_` name prefix.

## Object and array creation

The `new` operator creates a class instance or array.

```csharp
Counter counter = new Counter(10);
byte[] buffer = new byte[256];
```

The runtime manages class and array storage. Unreachable managed objects are eligible for reclamation.

An early runtime may keep managed allocations until program exit. This limit must not change source semantics.

Structures do not require heap allocation. `new Point(1, 2)` creates a structure value.

## Expressions

### Primary expressions

Primary expressions include literals, names, `this`, member access, calls, indexing, object creation, and parenthesized expressions.

```csharp
42
this.value
counter.Value
MathHelpers.Square(4)
buffer[index]
new Counter(0)
(left + right)
```

A method call is an expression. A non-void result can appear in any compatible expression context.

### Operator precedence

Operators use this precedence from highest to lowest:

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

Parentheses override this precedence.

### Arithmetic operators

Numeric types support `+`, `-`, `*`, `/`, and `%`. Integer division truncates toward zero.

Unary `+` keeps a numeric value. Unary `-` negates a signed numeric value.

The increment and decrement operators work on assignable numeric expressions.

The binary `+` operator concatenates two strings and produces a new string.

### Comparison operators

`==`, `!=`, `<`, `<=`, `>`, and `>=` produce a `bool` value.

Ordered comparisons require numeric operands or an enumeration with the same type.

A reference or pointer can compare with `null` through `==` and `!=`.

### Logical operators

`!`, `&&`, and `||` require Boolean operands. `&&` and `||` use short-circuit evaluation.

### Bitwise and shift operators

Integral types and enumerations support `~`, `&`, `|`, `^`, `<<`, and `>>`.

The right operand of a shift uses its low five bits for 32-bit values. Smaller values promote to `int` or `uint` first.

### Assignment

An assignment requires an assignable left expression and a compatible right expression.

```csharp
count = 10;
buffer[index] = value;
counter.Value = 4;
```

An assignment expression produces the assigned value. Compound assignments evaluate the left expression once.

## Statements

### Block

A block contains zero or more statements and creates a lexical scope.

```csharp
{
    int value = 1;
    value++;
}
```

An empty statement is valid.

```csharp
;
```

### If statement

An `if` condition must have type `bool`.

```csharp
if (value > 0)
{
    value--;
}
else
{
    value = 0;
}
```

Braces are optional for one embedded statement. Project style should use braces.

### Switch statement

A `switch` selects one section from constant case labels.

```csharp
switch (direction)
{
    case Direction.North:
        MoveNorth();
        break;

    default:
        Stop();
        break;
}
```

Draft 0.2 does not require pattern matching or `goto case`.

### While and do statements

Loop conditions must have type `bool`.

```csharp
while (index < count)
{
    index++;
}

do
{
    index--;
}
while (index > 0);
```

### For statement

A `for` statement has an initializer, condition, iterator, and body.

```csharp
for (int index = 0; index < count; index++)
{
    Process(index);
}
```

An omitted condition has the value `true`.

### Foreach statement

`foreach` iterates through an array from index zero to `Length - 1`.

```csharp
foreach (byte value in buffer)
{
    Process(value);
}
```

Draft 0.2 requires `foreach` for arrays only. A later version can add an enumeration protocol.

### Break and continue

`break` exits the nearest loop or switch. `continue` starts the next iteration of the nearest loop.

These statements must restore the stack state required at their target.

### Return

`return;` exits a void method. `return expression;` exits a non-void method with a value.

The returned expression must convert to the declared return type.

## Unsafe code

Pointer declarations, address-of, dereference, pointer arithmetic, and inline assembly require an unsafe context.

Mark a method or block with `unsafe`:

```csharp
public static unsafe void Clear(byte* address, int length)
{
    for (int index = 0; index < length; index++)
    {
        address[index] = 0;
    }
}
```

Safe code must not expose an unchecked pointer through a field, property, return value, or parameter.

The compiler must still type-check unsafe code. `unsafe` disables selected runtime checks, not compile-time type rules.

## Fishmachine runtime interface

Low-level Fishmachine operations use intrinsic methods. They do not use reserved identifiers or function-name conventions.

```csharp
using Fishmachine.Runtime;

FishVm.Syscall(1, character);
FishVm.Syscall(2, number);
FishVm.Stop();
FishVm.Wait();
```

The runtime library declares these methods with `[Intrinsic]`. The backend replaces each call with the matching FishAsm instruction.

### Inline FishAsm

Inline FishAsm requires an unsafe context and a compile-time constant string.

```csharp
unsafe
{
    FishVm.Emit("DBG_BREAK");
}
```

The compiler must not parse register effects from arbitrary inline FishAsm in draft 0.2. The programmer owns register and stack correctness.

### Interrupt handlers

An interrupt handler is a static method with `[Interrupt(number)]`.

```csharp
[Interrupt(2)]
public static void KeyboardCharacter(uint character)
{
    Input.Add((char)character);
}
```

The backend must generate the required wrapper and preserve the documented registers. Handler names do not affect code generation.

### Naked methods

A `[Naked]` method has no generated prologue or epilogue. It must also be static and unsafe.

Every reachable path in a naked method must end with inline FishAsm that transfers control. A C~ `return` is not valid in a naked method.

## Managed object lifetime

C~ source uses managed reference semantics for classes, arrays, and strings. The source language has no `delete` operator.

The runtime may use tracing collection, reference counting, regions, or program-lifetime allocation. The selected method must preserve observable reference behavior.

External resources require explicit release through a `Dispose()` method. Draft 0.2 does not define a `using` statement for disposal.

## Diagnostics

A conforming compiler must report errors before it emits FishAsm for an invalid program.

Each diagnostic must contain:

- A stable diagnostic code
- A severity
- A source file
- A one-based line and column
- A concise message
- A related declaration location when useful

The compiler should continue after a recoverable syntax or type error. It must not print parser traces unless the user enables tracing.

## Conformance

A compiler conforms to draft 0.2 only when it implements every non-deferred rule in this document.

An implementation can expose incomplete features behind an experimental flag. It must not report those features as conforming.

The conformance suite must test parsing, name binding, type checking, FishAsm generation, assembly, and Fishmachine execution.

## Deliberate differences from C#

C~ differs from C# in these core areas:

- C~ targets Fishmachine instead of the Common Language Runtime.
- C~ uses fixed 32-bit references and pointers.
- C~ defines `char` as one UTF-8 code unit.
- C~ exposes unsafe pointers and FishAsm intrinsics as first-class systems features.
- C~ permits read-only local variables with one delayed runtime assignment.
- Draft 0.2 has no class inheritance, interfaces, generics, exceptions, delegates, or asynchronous methods.
- Draft 0.2 has no nullable reference analysis.
- The first runtime can keep managed allocations until program exit.

These limits keep the initial compiler and runtime small. They do not change the C#-style syntax used by supported features.

## Migration from the current prototype

The new specification replaces several prototype forms:

| Prototype form | Draft 0.2 form |
| --- | --- |
| Module-level function | Static class method |
| Module-level variable | Static field |
| `__ctor()` | Constructor named after its type |
| `__dtor()` | Explicit `Dispose()` method |
| `naked void Entry()` | `[Naked] static unsafe void Entry()` |
| Function name starting with `handler_` | Method with `[Interrupt(number)]` |
| `string buffer = static string[50]` | `byte[] buffer = new byte[50]` |
| Mutable `string` buffer | `byte[]` or `char[]` |
| `__asm("WAIT")` | `FishVm.Emit("WAIT")` in unsafe code |
| `syscall_2(2, value)` | `FishVm.Syscall(2, value)` |
| `void kmain()` | `[EntryPoint] static void Main()` |

The compiler should provide focused migration diagnostics for these old forms during the transition.
