# C~ language specification

Specification version: draft 0.50

## Status

This document is the normative specification for C~ draft 0.50.

C~ is a statically typed language with C#-style syntax and a small managed runtime. A conforming draft 0.50 compiler emits deterministic GNU C23 unity or modular artifacts and diagnoses invalid programs before it writes C.

Draft 0.47 introduced metadata-linked managed libraries, Draft 0.48 added redirected process streams and reclaimable process identities, and Draft 0.49 introduced Runtime ABI 22, Managed Module ABI 3, provider-owned cleanup-safe call stubs, and ESP32/Xtensa managed code overlays. Draft 0.50 retains both ABIs and schema-3 metadata while adding controlled size optimization, private-helper overlay inference, and separated resident ELF segments. Runtime ABI 19 filesystem services and the Draft 0.46 storage surface remain available. Debug metadata remains version 3.

`CompilationTarget.Cosmopolitan` uses the hosted language and standard-library contract with `TargetProfile.Cosmopolitan`. This target requires the explicit x64 semantic architecture and supported single-architecture Cosmopolitan wrapper. Arm64 and fat x64/Arm64 output are deferred. The staged engineering contract is in [COSMOPOLITAN.md](COSMOPOLITAN.md).

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

The non-normative [example catalog](examples/README.md) maps the wider language surface to focused runnable projects. [LanguageTour](examples/LanguageTour/README.md) demonstrates runes, explicit lambda captures, custom operators, abstract dispatch, embedded resources, and native aggregate layout without changing the contracts in this specification.

## Source files and compilation

C~ files use the `.ct` extension and UTF-8 text. Invalid UTF-8 is an input error.

One compilation can contain multiple files. All files share declarations and `internal` access. File order must not change name binding or generated symbols.

`SyntaxTree.Parse(SourceText)` returns a full-fidelity immutable tree. Tokens retain leading and trailing trivia. Trivia includes whitespace, newlines, and comments. Missing tokens have zero width. Parser recovery attaches skipped tokens to trivia.

`SyntaxTree.ToFullString()` and `SyntaxNode.ToFullString()` reproduce source text exactly. This rule also applies to invalid input. Each node and token exposes `Span` and `FullSpan`. `ChildNodesAndTokens()` returns source-ordered children.

Bundled standard-library trees are internal trusted inputs. `Compilation.SyntaxTrees` contains only trees supplied by the caller.

Every user syntax tree has a source owner. The root application owner has a module identity and content root; every repository module owner additionally has an exact locked revision. Source-owned output identities and `[Embed]` paths use this owner instead of the current process directory. A source file cannot be assigned to two owners in one compilation.

Repository modules use canonical slash-separated paths and exact `ctilde.lock.json` revisions. Selectors can name commits, tags, or branches. `ctilde restore` materializes locked revisions, `ctilde update` resolves selectors again, and `ctilde vendor` copies verified exact content. `updatePolicy: "locked"` advances only during update; `"refresh"` also advances during an explicit restore. Check and build never access the network. Resolution precedence is an ignored `ctilde.local.json` replacement, verified vendor content, then the exact `.ctilde/modules` cache. An alias is a unique project-local module name used for stable vendor placement; it does not change the canonical source-owner identity.

Restore and update drain both Git output streams concurrently and accept Ctrl+C cancellation. Cancellation terminates and awaits the owned Git process tree, returns exit status 130, and preserves the last valid lockfile. The library retains its synchronous restore API as a wrapper around the cancellation-aware asynchronous entry point.

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

Draft 0.40 has no using aliases and no `using static`.

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
abstract  alignof    as         asm        base       bool       break      byte
case      catch      char       class      clobber    const
defer     delegate   do         else       enum       false
finally   float      double     for        foreach    get        if
in        int        interface  internal   is         lock       long
namespace
new       nint       nuint      null       object     out
override  private    protected  public     readonly   ref        rune
offsetof  return     sbyte      sealed     set        short      sizeof
stackalloc static    string     struct     switch     this       throw
true      try        uint       ulong      union      unmanaged  unsafe
ushort    using      var        virtual    void       volatile   where
while
```

`get` and `set` are meaningful only in property declarations. `default` starts either a switch label or the typed `default(T)` expression. `yield` starts `yield return` and `yield break` statements.

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

Integer suffixes are case-insensitive. With no suffix, the literal takes the first type that can contain it from `int`, `uint`, `long`, and `ulong`. `U` selects the first fit from `uint` and `ulong`; `L` selects the first fit from `long` and `ulong`; `UL` and `LU` select `ulong`. `F` selects `float`; `D` selects `double`. A decimal point or exponent without `F` also selects `double`.

```csharp
42u
4_294_967_296L
18_446_744_073_709_551_615UL
3.5f
1.25d
6.022e23
```

A literal must fit its selected type. Malformed, duplicated, mixed floating/integer, and overflowing suffixes are errors. The unary forms `-2147483648` and `-9223372036854775808L` are valid signed-minimum constants. An explicit cast applies normal truncation rules after literal typing.

### Character literals

`char` is one eight-bit UTF-8 code unit. A character literal must encode to exactly one byte.

`rune` is one valid Unicode scalar value stored in 32 bits. A rune literal uses the `r'…'` prefix and must contain exactly one scalar. Surrogates and values above `0x10FFFF` are invalid. Explicit conversion between `rune` and `uint` preserves the scalar value and validates conversion into `rune`.

Supported escapes are `\0`, `\a`, `\b`, `\t`, `\n`, `\v`, `\f`, `\r`, `\"`, `\'`, `\\`, and `\xHH`. A hexadecimal escape contains exactly two hexadecimal digits.

### String literals

Strings use double quotes and the character escape set. Draft 0.42 has no verbatim, raw, or interpolated strings.

String storage is UTF-8. `Length` counts UTF-8 code units, not Unicode scalar values. Indexing returns one read-only `char` code unit.

The built-in `string` type and the embedded or physical `System.String` declaration are the same sealed managed type. That declaration defines methods and implemented interfaces, but it cannot define storage, constructors, a different base class, or another layout. User declarations cannot replace it. Its instance surface provides ordinal, case-sensitive search, prefix and suffix tests, byte-range substring and copying, insertion, removal, replacement, single-byte trimming, splitting, and segment enumeration. Unless stated otherwise, every index, count, length, and segment offset is a UTF-8 byte offset.

The built-in scalar keywords and compiler-owned `System.Boolean`, fixed-width integer, native-integer, `System.Single`, and `System.Double` declarations have one type identity. Their `Parse` and `TryParse` methods use invariant syntax and optional `System.Globalization.NumberStyles`. Leading and trailing ASCII whitespace are permitted by default. Decimal integer parsing checks the destination range; hexadecimal parsing interprets the destination's fixed-width bit pattern. Floating-point parsing is deterministic nearest-even binary32 or binary64 and accepts decimal/exponent forms, signed zero, `NaN`, and signed `Infinity`. `TryParse` returns false and clears its output for null, malformed, or overflowing text. `Parse` distinguishes null, format (`CTP0001`), overflow (`CTP0002`), and invalid-style (`CTP0003`) failures.

`System.Enum.Parse<T>` and `TryParse<T>` require a closed enum type. They accept declared names, decimal values in the enum's underlying range, and comma-separated name combinations. Name matching is ordinal and case-sensitive unless the explicit ASCII ignore-case overload is selected. Only reachable closed enum parsers and their names enter generated output.

`String.Empty` is the immortal empty string. Full-range substrings and no-op replacements or trims return the original reference; zero-length constructed results use the immortal empty string. `StringSegment` retains its source and byte range until materialized. Splitting preserves leading, trailing, and adjacent empty entries unless `RemoveEmptyEntries` is selected. A zero result count returns an empty array; one returns the unsplit remainder.

`String.Format` and `System.Text.StringBuilder.AppendFormat` use invariant composite formatting. They accept escaped braces, indexed arguments, alignment, integral `D`/`d` and `X`/`x`, and floating-point `F`/`f` and `G`/`g` with precision from 0 through 99. The decimal separator is `.`, rounding is nearest-even, null arguments produce empty text, and non-formattable objects use `ToString()`. Built-in scalars and values implementing `System.IFormattable` receive the format specification. Malformed composite or value formats throw `FormatException` and report `CTS0006` at an unhandled boundary.

### Boolean and null literals

`true` and `false` have type `bool`. Integers do not convert to `bool`.

`null` converts to a class, array, string, delegate, unsafe pointer, or unmanaged function-pointer type.

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
| `long` | Signed value | 8 bytes |
| `ulong` | Unsigned value | 8 bytes |
| `nint` | Signed value | Target pointer width |
| `nuint` | Unsigned value | Target pointer width |
| `float` | IEEE-754 binary32 value | 4 bytes |
| `double` | IEEE-754 binary64 value | 8 bytes |
| `rune` | Unicode scalar value | 4 bytes |
| `string` | Managed reference | Target pointer width |
| `object` | Managed root reference | Target pointer width |
| `void` | Return marker | None |

Class, array, string, delegate, unsafe-pointer, and unmanaged function-pointer values use the native pointer width of the selected C target.

Draft 0.40 has no `decimal`. Integer literals never infer `nint` or `nuint`; context can convert constants in the portable `int` or `uint` range, while larger `long` or `ulong` values require an explicit cast.

### Value and reference types

Numeric types, `bool`, structures, and enums are value types. Assignment copies the complete value.

Classes, arrays, strings, delegates, and `object` are managed reference types. Assignment copies object identity. Their default value is `null`.

Class, array, and `object` equality compares identity. String equality compares contents, with two null strings equal.

### Arrays

`T[]` declares a one-dimensional managed array reference. `T[N]` declares an inline value containing exactly `N` elements, where `N` is a positive known integral constant representable by the target.

```csharp
byte[] data = new byte[256];
```

Every array has a read-only `Length` property of type `int`. Indexing starts at zero and checks the receiver, index, and length.

Inline arrays support one dimension, default initialization, checked indexing, by-value copying, parameters, and returns. Constant out-of-range indices are rejected; dynamic failures use `CTA0003`. Elements that contain managed references are retained and dropped element by element. Native ABI exposure requires a recursively complete unmanaged element type. Managed, multidimensional inline, jagged, and nested inline arrays are not part of this draft. Invalid lengths report `CT2204`.

Draft 0.40 has no multidimensional or jagged managed arrays.

### Unsafe pointers and unmanaged function pointers

`T*` is a native pointer to `T`. Pointer syntax and operations require an `unsafe` method or block.

Pointer arithmetic scales by the pointed element size. Dereference and pointer indexing do not perform managed null or bounds checks.

Pointer addition and subtraction accept `int` or `nint` offsets. Subtracting compatible pointers produces `nint`. `void*` represents an untyped data pointer: every data pointer converts to it implicitly, conversion back requires an explicit cast, and it cannot be dereferenced, indexed, or used in pointer arithmetic. Function pointers and managed references do not convert to `void*`.

Every type that recursively contains a pointer requires an unsafe context. This rule includes arrays such as `T*[]`.

Safe members must not expose a pointer-containing type through a field, property, parameter, or return type.

An unmanaged function pointer has an exact C signature:

```csharp
delegate* unmanaged<int, int> callback = &Transform;
delegate* unmanaged<void> notify = &Notify;
```

The final type is the return type; `delegate* unmanaged<void>` has no parameters and returns `void`. Parameters can use `ref`, `in`, or `out`. Signature elements are limited to `void` as a return, Boolean, numeric, enum, unsafe-pointer, and intrinsic native-buffer types. By-reference native elements must be unmanaged ABI-safe. Declarations, address acquisition, storage, casts, comparisons, and calls require an unsafe context. `&StaticMethod` creates a translation-unit-local C ABI trampoline; `&ExternMethod` uses the declared native symbol. Instance methods and managed signature elements are rejected. Function pointers compare only with the same function-pointer type or `null`; they have no arithmetic or dereference operations.

### Native buffers and stack allocation

`System.Runtime.NativeBuffer<T>` and `ReadOnlyNativeBuffer<T>` are compiler-intrinsic scoped value types. `T` must be a complete unmanaged type and cannot be `void`, an array, a managed reference, a delegate, or a reference-bearing structure.

Both views expose `nuint Length` and `T* Pointer`. Writable buffers have checked read/write indexing; read-only buffers have checked read-only indexing. Indexes use `nuint`. Writable-to-read-only conversion is implicit. Constructing a view from `(T* pointer, nuint length)` requires an unsafe context.

```csharp
NativeBuffer<byte> data = stackalloc byte[128];
ReadOnlyNativeBuffer<byte> input = data;
```

`stackalloc T[count]` returns `NativeBuffer<T>`. The count is `int` or `nuint`; negative `int` values fail, zero produces `{ null, 0 }`, and size multiplication is checked before aligned compiler alloca storage is requested. Lexical stack allocation inside a loop is rejected because the storage lasts until method return. It is permitted in `[NoAlloc]` code.

Buffers can be locals and value parameters and can pass through nested synchronous calls. They cannot escape through fields, properties, arrays, boxing, managed structures, static storage, returns, delegates, or by-reference parameters. At every C ABI boundary a buffer parameter expands to adjacent pointer and `size_t` length parameters; read-only data pointers are `const`.

`System.Runtime.NativeUtf8String` is a scoped stack-only view over a managed string. `Borrow(string)` retains its non-null owner without allocating and exposes UTF-8 data and a `nuint` byte length. Embedded NUL is rejected at compile time for literals and fails with `CTS0003` for dynamic values. `Null` is valid only at a `[Nullable]` native boundary. Native UTF-8 views can be locals and input parameters, but cannot be fields, properties, arrays, returns, boxes, static values, or retained arguments. An extern parameter maps to `const char*`.

`System.Text.Utf8.GetString` and `TryGetString` explicitly copy native UTF-8 into one managed string. Pointer input is bounded, requires a NUL terminator within the supplied maximum, and maps a null pointer to a null string. Buffer input consumes its exact length and preserves embedded NUL bytes. Both forms reject non-canonical UTF-8. Throwing conversion reports `CTS0004` for invalid UTF-8 and `CTS0005` for a missing terminator. `TryCopyTo` copies exact managed UTF-8 bytes to a native buffer with an optional trailing NUL. Insufficient output capacity returns false and writes zero bytes. These conversions do not transfer native ownership and do not introduce automatic native string marshalling.

An opaque declaration is nominal and names a native typedef and its public header:

```csharp
[NativeType("esp_timer_handle_t", "esp_timer.h")]
public opaque EspTimerHandle;
```

Opaque values support locals, parameters, returns, `out` creation, equality, and `null`. They cannot be fields, properties, arrays, boxes, managed-structure members, pointer operands, casts, arithmetic, or static storage. Owned handles are move-only. The compiler rejects discarded owned results, use after move, double consumption, owned-slot overwrite, and normal or exceptional exits with unresolved ownership.

### Enumerations

An enum has a fixed underlying type:

```csharp
public enum Direction : byte
{
    North = 0,
    East = 1
}
```

The underlying type can be `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, or `ulong`. The default is `int`.

Enum initializers are integral constants. An omitted initializer is one greater than the previous value. Every value must fit the underlying type, including the complete `ulong` range.

### Newtypes

`public newtype Name : Underlying;` declares a distinct nominal value type with the representation, layout, calling convention, boxing behavior, and native ABI of its complete unmanaged non-void underlying type. Recursive definitions report `CT1295`.

Conversion between a newtype and its immediate underlying type is explicit. Distinct newtypes do not convert directly. Equality and ordering operate only on two values of the same newtype when supported by the underlying type. Arithmetic, bitwise, mixed-type, and user-defined operations are not lifted; invalid conversion or operator use reports `CT2205`. Newtypes have no declaration body in Draft 0.40.

The standard library declares `be16`, `be32`, `le16`, and `le32` as nominal newtypes. Their stored bytes use the named wire order. `System.Endian` converts between native numeric values and these types, folds constant operands, and lowers big-endian conversions to byte swaps on the currently little-endian target set. Ordinary explicit newtype casts remain representation-preserving. Invalid intrinsic calls report `CT2208`.

### Conversions

Identity conversions are implicit.

An implicit numeric conversion is valid when the target range contains every source value. Integral values can also convert implicitly to `float` because the range fits, although precision can change.

Binary numeric promotion first selects `float` when either operand is floating-point. `nint` with signed types through `int` produces `nint`, while `nint` with `long` produces `long`. `nuint` with unsigned types through `uint` produces `nuint`, while `nuint` with `ulong` produces `ulong`. Mixing `nint` with `uint`, `nuint`, or `ulong`, or mixing `nuint` with a signed type, requires an explicit cast. `ulong` otherwise combines only with unsigned integral types. `long` combines with every non-native integral type except `ulong`. `uint` combined with a signed integral type promotes to `long`. Remaining small integers promote to `int` or `uint`.

All other numeric conversions require a cast. Explicit numeric conversions truncate high bits. There is no checked-overflow context.

`null` converts implicitly to managed reference, unsafe-pointer, and unmanaged function-pointer types. A derived class converts implicitly to each base class and implemented interface. An interface converts implicitly to its inherited interfaces.

Every class, interface, array, and string converts implicitly to `object`. A value type converts implicitly to `object` or one of its implemented interfaces by boxing.

Pointer boxing requires an unsafe context. Boxing creates a new managed object and copies the value.

An explicit cast can convert between numeric types and between fixed-width integral and enum types. `nint` and `nuint` cannot underlie an enum. An unsafe explicit cast can convert one raw pointer type to another.

An explicit cast can downcast a class reference or unbox an exact value type. A failed cast throws `InvalidCastException` with stable origin metadata.

`value is T` tests the runtime type. `value as T` returns a compatible reference or `null`. Both the `as` source and target must be reference types.

An explicit class cast requires related source and target types. Interface casts can test any compatible runtime implementation. Code can cast through `object` when it needs a runtime type check.

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

### Delegates

A named delegate declares one sealed managed callable reference type:

```csharp
public delegate int Transformer(int value);
```

A compatible method group converts contextually to that delegate. Overload resolution uses the delegate's exact parameter and return types. Static, instance, inherited, virtual, and `base` method groups are supported. An instance delegate retains its receiver; virtual invocation dispatches against the captured receiver, while `base.Method` remains a direct base call.

Delegate creation allocates a target-and-thunk object. Parameters are borrowed and managed or reference-containing results are owned, like ordinary C~ calls. Invocation propagates C~ exceptions normally, and null invocation throws `NullReferenceException` with origin code `CTN0001`. Equality compares delegate object identity, not target and method structure. Delegates can be generic and can be stored in fields, arrays, structures, parameters, returns, and boxes.

A lambda converts only in a named-delegate context. Parameters can be inferred or explicitly typed, and its body is an expression or block. A captureless lambda has no access to enclosing locals or parameters. An explicit capture list precedes its parameters: `[value, copy = expression] (int item) => item + value + copy`. Each initializer is evaluated once, left to right, when the closure is created. Captures are by value; managed captures are retained in a compiler-generated ARC environment and released with it. Duplicate captures and use of an uncaptured outer value are errors. Delegates remain single-cast; multicast operations, open-instance delegates, generic `Action`/`Func`, and variance are not supported.

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

### Interfaces and abstract classes

An interface is a managed contract type. It can inherit several interfaces and contains public instance method and property declarations without bodies. Classes and structures can implement several interfaces implicitly by providing identical public members. An exact member qualified by its interface name is an explicit implementation; it has no accessibility modifier and is callable only through that interface.

```csharp
public interface IShape
{
    float Area();
    string Name { get; }
}

public abstract class Shape : object, IShape
{
    public abstract float Area();
    public virtual string Name { get { return "shape"; } }
}
```

A class retains one class base and lists interfaces after it. An abstract method or property has no body and is valid only in an abstract class. Abstract classes cannot be constructed. Every non-abstract class and every structure must implement all inherited abstract and interface contracts. Interface implementations can be public and implicit or interface-qualified and explicit. Default interface bodies, static abstract members, and variance are not supported.

Interface references participate in ARC. A class-to-interface conversion retains the same object and allocates nothing. A structure-to-interface conversion boxes the value. Interface calls use the concrete type's generated interface dispatch table. Casts, `is`, `as`, null behavior, and `[NoAlloc]` virtual-boundary rules match class references.

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

### Aggregate layout

Structures use natural sequential layout unless layout attributes select another representation. `[Packed(n)]` is valid on structures and unions, where `n` is exactly `1`, `2`, `4`, `8`, or `16`; it caps each member's alignment and the aggregate alignment. `[FieldOffset(n)]` is valid on instance structure fields and requires a nonnegative integral literal. If any instance field has an offset, every instance field must have exactly one and the structure uses explicit layout. Explicit field ranges may overlap. Packing and explicit offsets may be combined.

```csharp
[Packed(2)]
public struct Header
{
    public byte Kind;
    public int Length;
}

public struct Register
{
    [FieldOffset(0)] public uint Value;
    [FieldOffset(0)] public ushort Low;
    [FieldOffset(2)] public ushort High;
}
```

A union is an unmanaged value aggregate whose instance fields all begin at offset zero. Assignment, default-zero initialization, parameters, and returns copy its object representation. Reading a field reinterprets that representation; there is no active-member state.

```csharp
public union NumberBits
{
    public int Integer;
    public float Float;
}
```

A union permits instance fields, static fields and constants, and static methods. It cannot declare constructors, properties, operators, instance methods, base types, interfaces, or instance field initializers. An empty union has a one-byte representation.

Every instance field of a union, packed aggregate, or explicit-layout structure must be unmanaged and reference-free. A generic field type must have an `unmanaged` constraint and is checked again after substitution. `Atomic<T>`, `volatile` fields, managed references, native buffers, UTF-8 views, and opaque stored handles are invalid in these layouts. Explicit-layout structures cannot contain auto-properties or instance field initializers.

Fields of packed or explicit-layout aggregates cannot be operands of unary address-taking or be passed as `ref`, `in`, or `out`. A whole aggregate value can still be addressed or passed by reference.

### Static classes

A static class contains only static members and cannot be constructed.

### Access

Namespace types can be `public` or `internal` and default to `internal`.

Members can be `public`, `internal`, or `private` and default to `private`. `internal` means accessible anywhere in the same compilation.

`protected` grants access to the declaring class and its derived classes.

An accessor can be less accessible than its property but cannot be more accessible.

### Fields and initialization

Fields can be instance or `static`. A `const` field is implicitly static.

Storage starts at the type's default value. Each class's instance field initializers run once after its base initializer completes and before that class's constructor body. A `this` chain does not run them again. Static field initializers run once before the entry method, in ordinal fully qualified type-name order and source declaration order within each type.

### Volatile fields

`volatile` is valid only on writable instance or static fields whose type is Boolean, integral, native-integral, enum, or unsafe pointer. A volatile read is an acquire operation and a volatile write is a release operation. C~ does not lower this modifier as plain C `volatile`.

Volatile locals, parameters, properties, constants, readonly fields, compound assignments, address-taking, and by-reference aliasing are invalid. Use `System.Threading.Atomic<T>` for exchange, compare-exchange, arithmetic, or bitwise read-modify-write operations.

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

An instance indexer uses `TValue this[TKey key] { get; set; }`. Draft 0.40 permits exactly one index parameter and one indexer per type. Indexers can be declared by classes, structures, interfaces, and abstract types, including virtual and override accessors. They cannot be static or auto-backed. Receiver, key, and assigned value are evaluated exactly once; setters use the same ARC strong-slot replacement rules as properties.

### Generic declarations

Classes, structures, interfaces, delegates, and methods can declare type parameters. Types and methods can also declare typed integral constant parameters with `const Type Name`. Closed types and explicit method arguments use angle brackets; ordinary method type arguments can be inferred from value arguments, but constant method arguments are always explicit.

```csharp
public class Box<T> { public T Value; }
public static T Identity<T>(T value) { return value; }
public struct Buffer<T, const int Capacity> { public T[Capacity] Items; }
Box<int> box = new Box<int>();
int value = Identity(box.Value);
Buffer<byte, 64> buffer;
```

A `where` clause can require `class`, `struct`, `unmanaged`, one base class, any number of interfaces, and `new()`. Constraint members are available in the generic body, and `new T()` requires `new()`. The compiler validates constraints before binding a closed instance.

Constant parameter types are integral types, `char`, native-sized integers, or enums with integral underlyings. Every argument is evaluated and checked before monomorphization. Constant values can appear in inline lengths, attributes, `static if`, `static assert`, switch cases, and other integral constant contexts. Defaults and `where` constraints for constant parameters are not supported. Invalid declarations and arguments report `CT2202`.

Generics use whole-program monomorphization. Each reachable closed type and method has separate code, layout, ARC helpers, allocation analysis, and static storage. Constant declared type and canonical value participate in specialization identity, native names, headers, maps, and debug metadata. Open generic values cannot reach native ABI boundaries. An expanding instantiation chain is rejected with `CT1272`. Variance, partial or return-context inference, reflection metadata, specialization syntax, and static-abstract generic arithmetic are not supported.

Draft 0.35 supplies `Pair<TFirst,TSecond>`, `Option<T>`, `Result<TOk,TErr>`, purpose-specific callback delegates, and `System.Collections.ArrayAlgorithms`. Draft 0.36 adds mutable `List<T>`, `Stack<T>`, `Queue<T>`, `Map<TKey,TValue>`, and `Set<T>` collections with explicit equality and hashing callbacks.

`default(T)` is valid for every complete non-`void` type, including a type parameter. It recursively zero-initializes value storage, produces false, zero, or null as appropriate, and never invokes a constructor. There is no target-typed `default` literal.

`System.Text.Utf8` decodes managed-string bytes or external native buffers and encodes a `rune` into a native destination. Its scalar helpers are `[NoAlloc]`, not `[NoRuntime]`; native-to-managed string conversion allocates one owned string. Invalid offsets, continuation starts, truncation, overlong forms, surrogates, and values above `U+10FFFF` fail with NUL and zero output counts. An encoding destination that is too small remains unchanged. Unicode escape syntax is not part of Draft 0.42.

### SIMD and scalar geometry

`System.Simd.F32x4`, `I32x4`, `U32x4`, and `Mask32x4` are explicit four-lane values with deterministic 16-byte storage. Their operations carry a semantic lane type, width, inputs, and constant immediates through lowering; target instruction spellings are backend choices. Lane and shuffle arguments are compile-time integers in `0..3`, and shift counts are compile-time integers in `0..31`.

SIMD is scalar by default. Selecting `cpuFeatures: ["simd128"]` permits exact SSE or NEON lowering on a supported x86 or Arm architecture. Missing exact instructions use fixed-order scalar lane code. Reductions preserve their documented left-to-right grouping. Division and square root remain exact operations; reciprocal estimates, fast-math reassociation, and contraction of ordinary expressions are forbidden. `F32x4.MultiplyAdd` and recognized matrix or quaternion kernels may use FMA only when target compiler macros prove support. Fused and non-fused configurations can differ by one rounding, but one fixed configuration must be deterministic.

The top-level project-manifest property `simdOptimizations` is `false` by default. `true` is accepted only for a hosted x64 application, implies `CpuFeature.Simd128`, and makes `Target.HasFeature(CpuFeature.Simd128)` true. `simdOptimizations: false` never removes an explicitly selected `cpuFeatures: ["simd128"]`. Draft 0.40 does not extend this automatic optimization contract to x86, Arm, ESP-IDF, freestanding, or Cosmopolitan builds.

### Native build profiles

The optional `build.optimization` property is `size`, `speed`, or `aggressive`. `size` selects `/O1` or `-Os`; `speed` selects `/O2` or `-O2`; `aggressive` adds `/Ob3` or selects `-O3`. The applicable optimization is repeated during native or LTO linking. Cosmopolitan `tiny` accepts an omitted or explicit `size` profile and rejects `speed` and `aggressive`. The optional `build.cpuTarget` property is `baseline` or `avx2`. AVX2 requires resolved x64 and selects `/arch:AVX2` or `-march=x86-64-v3 -mtune=generic`; it does not add runtime dispatch or alter `CpuFeature`. The optional `build.floatingPoint` property is `precise` or `fast`. Precise explicitly disables fast-math and ordinary-expression contraction; fast permits the selected native compiler's fast-math transformations. A fast build must be deterministic for one compiler and complete profile, but it need not match a precise checksum. Omitted properties preserve the target's earlier toolchain behavior.

`build.pgo.mode` is `off`, `generate`, or `use`, and `build.pgo.directory` defaults to `build/pgo` beneath the project root. PGO is hosted-only and requires a project manifest, Release, and LTO. Profile identity includes the Draft version, generated-C hashes, compiler identity, optimization, CPU, floating-point, architecture, and LTO. `use` requires matching training data. MSVC maps to `/GENPROFILE` or `/USEPROFILE`; GCC maps to `-fprofile-generate` or `-fprofile-use -fprofile-correction`; Clang maps to instrumentation profiles merged by a version-matched `llvm-profdata`. Freestanding, ESP-IDF, and Cosmopolitan PGO are rejected. ESP-IDF optimization and floating-point flags apply only to C~-generated modular sources.

SIMD storage never imposes alignment on user arrays, buffers, pointers, vectors, matrices, or quaternions. Runtime-backed profiles provide checked loads and stores that validate all four lanes before mutation. Unsafe pointer loads and stores are available on every target, are unaligned-safe, and are `[NoRuntime]`. SIMD values cannot appear in exports, extern signatures, callbacks, unmanaged function pointers, or public native data.

`Vec2`, `Vec3`, `Vec4`, `Matrix3x2`, `Matrix4x4`, and `Quaternion` are mutable scalar-layout structures. Enabling SIMD cannot alter their fields, offsets, size, alignment, or native ABI. Matrices are row-major and use row-vector composition: `A * B` applies `A` and then `B`. Camera factories are right-handed and map depth to zero through one. Named point, vector, projective, and normal transforms prevent an implicit choice of homogeneous coordinate. Failed matrix inversion writes the zero matrix; failed quaternion try-normalization or try-inversion writes `Identity`. These value APIs allocate no managed storage and remain available on targets without SIMD hardware.

`System.Simd.Vec3x4` is a 48-byte structure-of-arrays value containing three `F32x4` components. It provides splat and constant-lane construction, lane access and replacement, arithmetic, per-lane scaling, dot, cross, length, normalization, and mask selection. Like every other SIMD value, it cannot cross exports, externs, callbacks, unmanaged function pointers, or public native-data boundaries. Debug metadata version 3 may attach optional lane and component shape data without changing the map version.

## Methods

A method declares a return type, name, parameters, and body.

Methods can be overloaded by parameter types. Resolution includes accessible inherited methods with the correct argument count.

The compiler compares candidates for each argument. Identity is better than widening. One widening target is better when it converts implicitly to the other target only. If two integral widening targets remain, a signed target is better than an unsigned target.

A candidate must be no worse for every argument. It must also be better for at least one argument. Otherwise, the call reports `CT2123`.

Parameters and call arguments can be marked `ref`, `in`, or `out`. The call site must use the same modifier, and passing kind is part of overload and delegate/function-pointer signature matching. `ref` requires an assigned writable variable, `in` requires an assigned addressable variable and is read-only in the callee, and `out` is unreadable until assigned and must be assigned on every normal return. Properties, constants, literals, and temporaries are not by-reference arguments. Readonly storage can be passed only with `in`.

An `out` call treats the caller slot as an uninitialized destination. The caller drops an initialized managed/reference-bearing value, clears the slot to a safe empty state, and marks it uninitialized before entry. The callee's first assignment constructs or moves directly into the destination without reading, retaining, or dropping an old value. Later assignments use ordinary strong-slot replacement. The rule is identical for methods, constructors, delegates, unmanaged function pointers, externs, and exported C declarations. Normal return assigns the caller slot; exceptional control leaves its safe empty value but does not make it definitely assigned. Native `ref` and `out` parameters map to `T*`; native `in` maps to `const T*`. Buffer parameters cannot themselves be by-reference.

Draft 0.50 has no optional, named, implicit by-reference, reference-return, reference-local, or parameter-array arguments. An overload cannot differ only between `ref` and `out`.

### User-defined operators

A non-static class or structure can declare public static unary and binary arithmetic operators:

```csharp
public static Vector3 operator +(Vector3 left, Vector3 right) { ... }
public static Vector3 operator -(Vector3 value) { ... }
public static Vector3 operator *(Vector3 value, float scale) { ... }
```

The supported declaration tokens are binary `+`, `-`, `*`, `/`, `==`, `!=`, `<`, `<=`, `>`, and `>=`, plus unary `+` and `-`. Unary declarations have one value parameter. Binary declarations have two value parameters. A unary parameter must be the exact containing type; at least one binary parameter must be the exact containing type. Arithmetic results can be any non-`void` type; equality and ordering operators must return `bool`. Operators must have bodies and cannot use `ref`, `in`, or `out`. They can use `unsafe`, `[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]`, but cannot be virtual, overrides, externs, exports, entry points, or native-ownership contracts. Invalid declarations report `CT1269`.

Operator lookup examines the operand class or structure types and their base chains, then deduplicates shared declarations. Existing implicit conversions and better-conversion rules select the overload. No match reports `CT2167`; no unique best match reports `CT2168`. Built-in arithmetic and comparison remain preferred when both operands have built-in types. The compiler does not infer scalar symmetry: `vector * scale` and `scale * vector` require separate declarations.

Operator declarations are statically dispatched and are not ordinary named members. They do not appear in member access or member completion and cannot be called through names such as `op_Addition`. Class operands are borrowed and can be null; the operator body is responsible for null handling.

### Evaluation order

The receiver is evaluated first. Arguments are evaluated from left to right. Binary operands are evaluated from left to right.

`&&` and `||` evaluate the right operand only when required. Compound assignment evaluates its left expression once.

These rules apply even when the target C compiler leaves an equivalent C expression order unspecified.

### Returns

A `void` method returns no value. A non-void method must return a compatible value on every reachable path.

Statements after an unconditional return, break, continue, or throw are unreachable.

### EntryPoint

An ordinary hosted or ESP-IDF firmware application has exactly one `[EntryPoint]`, which is a body-bearing `static void` method with no parameters. An ESP-IDF managed application instead requires exactly one body-bearing `static int Main(string[] args)` entry point. A managed library has no entry point.

The C backend generates `int main(void)`, initializes static storage, calls the entry method, and returns `EXIT_SUCCESS`.

`[EntryPoint]` is invalid for the freestanding target. Freestanding compilation emits no `main`, `app_main`, automatic runtime initialization, or automatic shutdown. Public `[Export]` methods are native-callable roots and require explicit runtime initialization before invocation unless the program contains only naked exports.

### ESP-IDF managed modules

An ESP-IDF project selects `espIdf.artifact: "managed-module"` and `build.cLayout: "modules"`. Its `managedModule` block contains `kind` (`application` or `library`), a canonical name, an exact version, exact `.ctmeta.json` references, the application task stack size, an optional heap limit, and optional exact `nativeSources`. Canonical names are ASCII and at most 63 bytes; exact versions are ASCII and at most 31 bytes, leaving one NUL byte in each fixed ABI field. The same limits apply to every referenced identity and dependency. A native source must be a declared checked-in `.c` file inside the module ESP-IDF `main` component; missing, duplicate, external, generated-directory, and undeclared component C sources are rejected. The generated CMake fragment adds declared files to the component source list. Their project-local quoted-header closure participates in the module build identity, while the managed API hash remains a function of the public managed surface only.

Build emits `<name>.ctm`, a 32-bit target ELF shared object, and deterministic schema-3 `<name>.ctmeta.json` public metadata. The ELF contains a fixed binary preflight manifest for Runtime ABI 22 and Managed Module ABI 3. Packaged Xtensa modules use distinct read-only loader-metadata, executable, immutable-data, and writable-data load segments. The loader allocates only executable segments from executable-capable memory and reports resident executable requirements, the largest contiguous executable segment, the overlay window, current executable free memory, and its largest free block before relocation. Private native functions can satisfy module-local `[Extern]` declarations without becoming managed exports. The module emits neither `app_main` nor a private copy of the firmware module/process host. Reachable compiler-generated helpers can still be module-local; moving every non-generic standard-library implementation into firmware remains implementation work.

Managed Module ABI 3 is ESP-IDF-only in Draft 0.50. A hosted application can resolve ordinary native C ABI methods through `[NativeImport]`, but the hosted target does not emit `.ctm` files or register managed C~ module types in a shared desktop runtime.

Every public type and member of a managed library is recorded in its managed ABI metadata. Public signatures may use scalars, enums, strings, arrays, concrete classes and structures, interfaces, and delegates. Public generic definitions and constructed generic types are rejected. A consumer parses the provider's emitted declarations without compiling provider source. `internal` is binary-module-local. Callable concrete members use deterministic identities and loader-checked import slots. Each export resolves to a provider-owned stable resident stub, which pins the provider and restores its module and overlay context through the ordinary cleanup stack on both return and exception propagation. Managed references may cross module boundaries only inside one logical process. Process messages remain copied byte arrays and never transfer managed identity.

The firmware owns one versioned `ct_runtime_api_v22` table, process registry, and module registry. A module binds the table through its fixed entry function. Mutable statics are stored in per-process module instances; resident code and immutable metadata are shared. Applications run as logical processes backed by one managed main FreeRTOS task. Arguments are copied before entry. Generated type descriptors carry stable 128-bit fingerprints, casts compare fingerprints rather than only addresses, and the firmware registers compatible descriptors and removes provider registrations before unload. The ABI reserves runtime-sized `ct_type_ops`; complete shared generic-container lowering and authoritative substitution of every embedded local descriptor pointer remain in progress.

`[Overlay("name")]` may place a concrete type, body-bearing method, constructor, or property in a named overlay. Type placement is inherited by executable members; a member can select another overlay or use `[Resident]`. Names are case-sensitive ASCII identifiers of at most 31 bytes, begin with a letter, and contain letters, digits, `_`, or `-`. Entry points, abstract or bodyless methods, interfaces, fields, interrupts, native imports or exports, runtime implementations, naked methods, and explicit sections cannot be overlay bodies. The feature is available only to ESP-IDF Xtensa managed applications and libraries; ESP32-C3/RISC-V and all other targets reject it.

Overlay-enabled dependency closures are single-task. Source `Thread.Start` is rejected, and Runtime ABI 22 rejects secondary thread attachment. Managed delegates and virtual or interface dispatch name stable resident stubs. Taking an unmanaged pointer to an overlay body is invalid; only explicitly synchronous native callbacks may enter its stable stub on the process main task. Calls proven to stay inside one overlay may call the loaded body directly. A non-exported helper used only by one overlay inherits that overlay; helpers reached from resident code or multiple overlay groups remain resident. Entry points, runtime roles, initialization and finalization, unknown virtual targets, exports, address-escaped targets, and externally visible managed identities retain resident entries. Resident, cross-overlay, imported, delegate, virtual, interface, and generic-dispatch calls use stable stubs.

Each process has at most one lazily allocated executable overlay window sized for the largest reachable payload. Entering another overlay replaces that window after validation and relocation; leaving reloads the caller's overlay before returning. Thus nested transitions and recursion are valid, but overlay source storage must remain available for the process lifetime. A deliberate storage operation reports busy while a live process depends on that prefix. Physical removal makes future transitions unavailable and forces affected processes through resident cleanup without user finalizers.

`ProcessStartInfo` selects inherited or redirected stdin, stdout, and stderr. A false redirect flag shares the caller's reference-counted endpoint; a true flag creates an independent bounded 8 KiB queue and exposes its parent side through the corresponding `ProcessPipe` property. Firmware-created roots without redirects use UART. Writers are serialized, and only the foreground process can read a shared input endpoint. `ProcessPipeReader` and `ProcessPipeWriter` provide timed operations and explicit close; writers apply backpressure, and cancellation or closure wakes polling operations. `Process.TryOpen` resolves a currently retained global process identity. Completed slots may be recycled; monotonically increasing IDs prevent a stale handle from naming a later process in the same slot.

Module paths name direct children of `/sd/modules` or `/storage/modules`. A bare filename searches the SD path first and falls back to LittleFS only when the SD entry is absent; an unreadable, corrupt, or ABI-incompatible SD module reports its own error. Empty names, `.`, `..`, nested paths, backslashes, and absolute paths outside those roots are rejected. This flat namespace matches the current Espressif loader's basename-keyed module registry and ensures preflight and relocation select the same file. Dependency metadata names an exact canonical name, version, build identity, and API hash. The loader recursively preflights exact dependencies, rejects cycles and global version/build/API conflicts, creates process-local module instances in dependency order, and releases newly unused modules in reverse order. It pins modules across dependent load references, active managed calls, type registrations, and allocations. Schema-3 `.ctmeta.json` references provide the supported concrete, non-generic managed-library surface and checked provider-owned import stubs. Authoritative canonical descriptors, shared unboxed generics, and the complete shared standard-library extraction remain incomplete.

Current processes have independent module instances, mutable statics, managed allocation lists, heap accounting, cancellation state, a tracked task set, parent identity, console endpoints, copied-message mailboxes, and a resident native-resource ledger. Source-created threads inherit their creating process and are joined on normal completion or deleted during forced cleanup; overlay-enabled dependency closures continue to prohibit them. `Cancel` is cooperative. `Terminate` requests cancellation and may delete remaining tasks after its grace period; `uint.MaxValue` selects an unbounded cooperative wait. A dedicated control task owns deletion and a reaper performs blocking cleanup outside task-deletion callbacks. A generated top-level boundary converts an uncaught application-main exception to a bounded stderr diagnostic and exit code `-2`. Because modules are trusted native code and ESP32 provides no process memory protection, raw pointers, locks, callbacks, interrupts, MMIO, or native resources that escape accounting make forced termination behavior undefined.

### Runtime service implementations

The `freestanding` target requires an explicit architecture. `Target.Profile` is the constant `TargetProfile.Freestanding`; `Target.Architecture` and `Target.PointerSize` retain their ordinary compile-time semantics. Architecture `auto` reports `CT4108`.

`[RuntimeImpl(Runtime.Panic)]` selects the one bootstrap-safe `static unsafe void Panic(RuntimePanicInfo)` implementation. Panic is required whenever reachable ordinary freestanding code exists. `[RuntimeImpl(Runtime.Allocate)]` selects `static unsafe void* Allocate(nuint)`, and `[RuntimeImpl(Runtime.Free)]` selects `static unsafe void Free(void*)`; these roles are required when reachable code uses managed heap storage. Other roles provide exit, console byte transfer and flush, monotonic nanoseconds, path separators, scalar math dispatch, files, metadata, directories, current-directory access, threads, mutexes, and runtime thread-local state. Exact signatures use the public `RuntimeResult`, `RuntimeTransferResult`, `RuntimeStatus`, file metadata, operation enums, native buffers, and `NativeUtf8String` types in `System.Runtime`.

Freestanding requirements are reachability-driven by service group. File/stream use requires the complete file-handle group; filesystem use requires the complete path/directory group; managed `Thread` or `Mutex` use requires its complete group plus `ThreadStateGet`, `ThreadStateSet`, allocation, and free. Console, clock, exit, and math roles remain individually reachable. ESP-IDF supplies platform defaults. Declaring any grouped ESP-IDF override requires the complete matching group, while individual panic, exit, clock, and math roles can be replaced independently.

Every role method is static, non-generic, body-bearing, unique, `[NoAlloc]`, and a compiler root. Its reachable closure must not allocate, throw, or use the managed runtime; providers can add `[NoThrow]` and `[NoRuntime]` contracts to state that requirement explicitly. Blocking is permitted because console, file, thread, and mutex backends may wait. `NativeUtf8String` parameters are borrowed bootstrap values and do not themselves count as managed-runtime use. `[Section]` is permitted and `[Used]` is redundant. Native-entry attributes, `[Naked]`, and `[Extern]` are incompatible. Malformed declarations report `CT1299`; missing, duplicate, or incomplete grouped roles report `CT4114`.

A runtime implementation and its transitive C~ call closure cannot use managed values, ARC, allocation, exceptions, `defer`, runtime lifecycle calls, or initialized managed statics. It may use unmanaged fields, registers, linker symbols, MMIO, CPU intrinsics, trusted `[NoAlloc]` externs, and trusted inline assembly. A violation reports `CT2211`.

The compiler changes a zero-byte request to one byte, calls the selected allocator, panics with `CTM0001` on null, and clears the returned storage before object construction. The allocator must return storage aligned to at least 16 bytes. Generated code does not pass null to the free role. Runtime faults and non-success service results route to panic on freestanding; if panic returns, generated code loops forever. Transfer roles report partial progress through `Count`, `EndOfStream` terminates reads, and `BufferTooSmall` reports the required byte count for variable-length directory and current-directory results.

### Naked startup

`[Naked]` is a freestanding-only startup facility. It requires a public static unsafe non-generic `void()` method with `[Export]` and `[NoAlloc]`. The preferred body is an assembly-function body with no operands or clobbers. The older ordinary method containing exactly one operand-free `[NoAlloc] asm` statement remains source-compatible. Parameters, locals, C~ expressions, compiler-bound operands, clobbers, and generated cleanup are forbidden. Invalid declarations or bodies report `CT1302`; a non-GNU/ELF native toolchain reports `CT4116`.

The GNU backend emits one exported `naked,noreturn` definition rather than an export wrapper and internal implementation. It copies the basic assembly body without operand substitution or percent escaping and emits no prologue, epilogue, return, runtime check, ARC, instrumentation, or exception barrier. `[Section]` remains valid. Naked parameters and compiler-bound naked assembly operands are deferred.

### Extern

`[Extern("symbol")]` marks a static bodyless method supplied by native code.

The symbol must be a portable C identifier. Its native signature must use the C~ mappings in [C_ABI.md](C_ABI.md).

The compiler rejects `main`, runtime names, and generated symbol names. Repeated external names require identical complete ABI signatures. Matching declarations produce one C prototype. An incompatible declaration reports `CT4102`. The diagnostic includes the earlier location.

Unknown attributes, invalid targets, duplicate attributes, and non-constant arguments are errors.

### NativeImport

`[NativeImport("library")]` marks a static bodyless hosted method whose native symbol is the exact C~ method name. `[NativeImport("library", "symbol")]` selects an explicit symbol. The library is an extensionless logical ASCII base name containing only letters, digits, `_`, `+`, or `-`; it cannot contain a path, drive prefix, Unix `lib` prefix, or `.dll`, `.so`, or `.dylib` extension. An explicit symbol follows the same portable C identifier rules as `[Extern]`. Malformed declarations report `CT1312` or `CT1313`; a non-hosted target reports `CT1314`.

The non-normative [HostedNativeImport example](examples/HostedNativeImport/README.md) builds these typed declarations against a small stateful DLL/`.so` and verifies startup resolution and execution under all three supported hosted compiler families.

The mapping is platform-defined: `foo` becomes `foo.dll` on Windows and `libfoo.so` on Linux. The operating-system loader search path is authoritative. A logical name makes source portable, but does not assert that the same third-party library or compatible version is installed on every host. macOS and versioned shared-library mappings are deferred.

Every reachable import is resolved after panic and runtime-fault initialization and before the first C~ static initializer. Imports pruned by `static if` or whole-program reachability create no loader state. Compatible declarations with the same library, symbol, and native signature share one typed resolved slot; incompatible declarations report `CT4102`. Libraries remain private runtime state, load once in ordinal logical-name order, stay loaded through reverse static finalization, and unload in reverse load order afterward. `CTI0001` reports a library-load failure, `CTI0002` reports a missing symbol, and `CTI0003` reports an unload failure. Resolution failure is fatal before `[EntryPoint]` and cannot be caught by C~ code.

Native imports use the platform default C calling convention and all existing scalar, enum, newtype, opaque, pointer, natural-layout aggregate, native-buffer, UTF-8, ownership, and synchronous-callback rules. SIMD, managed, open-generic, interface, atomic, and runtime-backed threading types remain forbidden. `[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]` are trusted contracts for the resolved function; startup resolution adds no call-site effect. `[Extern]`, `[Export]`, `[EntryPoint]`, `[RuntimeImpl]`, `[Naked]`, `[Interrupt]`, `[InterruptSafe]`, `[Section]`, `[Used]`, bodies, instance methods, and open generics are incompatible. Taking an imported method's address returns the resolved unmanaged function pointer without a C~ trampoline.

### Section placement

`[Section("name")]` controls the native object section of an eligible definition. The name is one ASCII string from 1 through 128 characters. Letters, digits, `.`, `_`, `$`, and `-` are permitted; the first character must be a letter, `.`, `_`, or `$`. Malformed arguments and names report `CT1286`.

The attribute is valid on a body-bearing static method of any accessibility and on a non-const static field whose declared type is complete and unmanaged. An ordinary sectioned field may be `readonly` or `volatile`; its native section remains writable because module initialization can assign it. A `[ConstInit]` field instead occupies read-only native storage. Constructors, operators, properties, instance members, abstract or extern methods, const fields, and managed or incomplete field types report `CT1287`.

Several definitions of the same section category may share one name. Code, writable data, and `[ConstInit]` read-only data are distinct categories and cannot share a custom section name; a conflict reports `CT4107` with the earlier declaration location. Closed generic methods and closed generic-type static fields inherit the annotation from their source declaration.

### Constant-initialized immutable data

`[ConstInit]` accepts no arguments and applies to an owned `static readonly` field with an initializer and a complete unmanaged, non-atomic, pointer-free type. It evaluates before module-initializer construction and emits a `const` positional C aggregate directly at file scope. The field creates no zero-then-assign sequence, constructor call, module lifecycle work, runtime requirement, or runtime call-graph edge. `[Used]`, `[Section]`, and `[Align]` retain their ordinary meanings; `[Used]` alone does not require the freestanding runtime.

Scalar constants, enums, newtypes, explicit casts, wrapping arithmetic, constant fields, target/layout constants, zero values, and nested sequential structure construction are supported. A structure constructor is eligible only when its body consists of straight-line assignments of compile-time expressions to every instance field exactly once. Branches, loops, locals, calls, constructor chaining, properties, managed operations, unions, overlapping explicit layout, recursion, and general compile-time execution report `CT2218`. Invalid field shapes report `CT1308`.

The field and every member or indexed location rooted in it are immutable storage. Reads, member reads, value copies, and `in` passing are valid. Assignment, increment, `ref`/`out`, explicit address-taking, and direct instance method calls report `CT2219`; a copied local value may be mutated normally. `[ConstInit]` data is initialized and readable in freestanding bootstrap and interrupt closures. Symbol maps record `constInit`.

`[Embed("relative/path")]` applies to an uninitialized `public static unsafe readonly ReadOnlyNativeBuffer<byte>` field. The path resolves under the declaring source owner's content root and cannot escape it. The compiler reads the exact bytes during compilation and emits immutable native storage plus its length; generated output and symbol metadata do not contain the absolute source path. Missing content, traversal, an invalid target, or mutable storage reports `CT2222`.

For `[Export]`, the internal C~ implementation and external wrapper use the requested code section, and the exported native-header prototype carries the matching declaration annotation. For `[EntryPoint]`, only the C~ implementation is placed; generated `main` and `app_main` wrappers remain in their default sections. `[Section]` does not make a method or field reachable, does not change linkage or initialization order, and does not specify ordering, alignment, retention, linker-script mapping, memory regions, or final addresses.

### Native calls and callbacks

Extern methods, native-import methods, and unmanaged function pointers cover direct calls from C~ to exact C symbols and signatures, including native-sized scalars, unmanaged `ref`/`in`/`out`, flattened native buffers, opaque handles, and scoped UTF-8 input. `[Borrowed]`, `[Consumes]`, `[Retained]`, `[Creates]`, and `[Nullable]` describe opaque or explicitly annotated pointer parameters. `[ReturnsOwned]`, `[ReturnsBorrowed]`, and `[ReturnsNullable]` describe results. A borrowed input is the default. `[Creates]` applies to `out`; ownership transfers only on normal return. `[Retained]` transfers an owned opaque value to native code.

`[Export("symbol")]` marks a public static body-bearing C~ method with a unique portable C name. Its signature is limited to ABI-safe scalars, enums, unmanaged structures, pointers, opaque handles, `EspError`, native buffers, by-reference parameters, and input `NativeUtf8String`. The generated wrapper requires an attached runtime thread, translates flattened arguments, and converts an escaping exception to fatal `CTE0003`. `EmitCHeader` and CLI `--header` produce its deterministic C/C++ declaration, reachable unmanaged layouts, and native runtime ownership and attachment declarations.

`[SynchronousCallback]` on an extern or native-import delegate parameter flattens the delegate to a C function pointer followed by `void*` context. The adapter retains the delegate until the native call returns, preserves instance and virtual dispatch, and releases it afterward. Native code may invoke it on any explicitly attached thread, but every invocation must finish and all worker threads must join before the native call returns. An unattached invocation panics with `CTT0001`. Callback exceptions run that thread's C~ cleanup and panic with `CTE0003`.

Ordinary parameters are borrowed for the duration of a call. `[Retained]` accepts no arguments and is valid only on a direct class, array, or string parameter of an extern or native-import method. The compiler retains that argument immediately before the call and transfers the additional ownership count to native code. Invalid uses report `CT1234`.

Managed-reference results are owned. `[ReturnsBorrowed]` accepts no arguments and is valid only on an extern or native-import method returning a direct class, array, or string reference. The compiler retains that native result immediately, converting it to the normal owned-result convention. Invalid uses report `CT1235`.

Delegates and unmanaged function pointers never convert implicitly to one another. Draft 0.40 supports static-method function-pointer trampolines and synchronous delegate/context adapters on any attached native thread. An exception escaping a callback runs that thread's C~ cleanup and panics with `CTE0003`; it never unwinds through native frames. Retained callbacks remain unsupported. ESP-IDF interrupt entry uses the separate restricted contract below and is not a delegate callback.

### Effect contracts

`[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]` accept no arguments. They apply to methods, constructors, operators, properties and their declared accessors, abstract/interface declarations, extern methods, and native-import methods. Virtual and interface contracts are inherited by every implementation; an implementation may add contracts but cannot remove inherited ones. Generic specializations preserve and validate the contracts independently. Malformed attributes report `CT1233`, `CT1303`, `CT1304`, or `CT1305`. Violations report `CT2155`, `CT2212`, `CT2213`, or `CT2214` with a deterministic complete call-path witness.

`[NoAlloc]` prohibits managed heap allocation. Allocating operations include class and managed-array construction, boxing, delegate creation, nonconstant string construction, formatting conversions, and calls inferred to allocate. `[NoThrow]` prohibits explicit throw, every `try`/`catch`/`finally` region, allocation that can fail, potentially failing runtime checks, and calls not proven `NoThrow`. It therefore implies `NoAlloc`. A constructor contract covers only its body; constructing a class remains an allocation and throw effect at the `new` expression.

`[NoBlock]` prohibits operations that can wait for time, I/O, synchronization, another thread, or external progress. `Thread.Join`, nonzero or dynamically timed `Thread.Sleep`, `Mutex.Enter`, `lock`, console/file I/O, and unknown native calls block. `Mutex.TryEnter`, `Thread.Yield`, CPU hints, finite computation, and busy loops do not. The compiler does not attempt termination proofs.

`[NoRuntime]` is the public bootstrap-safe contract. It prohibits managed parameters, results, locals, ARC operations, allocation, exception machinery, `defer`, runtime lifecycle calls, runtime-backed helpers, and initialized managed statics. Unmanaged stack values, inline arrays, newtypes, fields, registers, linker symbols, MMIO, CPU/endian intrinsics, and trusted native boundaries remain available. `NoRuntime` implies `NoThrow` and `NoAlloc`, but not `NoBlock`. Diagnostics are emitted only for contracts explicitly declared or inherited.

The compiler records immutable direct effect operations after `static if` pruning and closed generic construction, then infers recursive and transitive effects to a fixed point. Constant, non-null, and fixed-range facts suppress a throw effect only when semantic analysis proves the generated check unnecessary. Uncontracted virtual/interface dispatch, delegates, function pointers, externs, native imports, and inline assembly are conservative unknown boundaries. Contracts on externs and native imports are trusted independently.

### ESP-IDF interrupt entry points

`[Interrupt]` accepts no arguments and applies only to a public, static, unsafe, non-generic, body-bearing `void(void*)` method that also has `[Export("symbol")]`. It is valid only for the ESP-IDF target. It cannot be combined with `[EntryPoint]`, `[TaskEntry]`, `[RuntimeImpl]`, `[Naked]`, `[Extern]`, or `[Section]`; `[Used]` is allowed but redundant. Malformed declarations report `CT1306`, and unsupported targets report `CT4117`.

An interrupt entry is a native-only reachability root. The compiler emits its exported name as the only definition: there is no ordinary export wrapper, runtime-ready check, thread attachment, exception barrier, ARC cleanup, or debug instrumentation. C~ calls to the interrupt entry report `CT2215`. The entry and its complete statically closed C~ call graph must satisfy the `NoRuntime` and `NoBlock` profiles. Indirect or open dispatch and transitive violations report `CT2215` with a deterministic call path.

The compiler places every C~ method in the interrupt call closure in ESP-IDF IRAM and places referenced compiler-owned unmanaged static storage in DRAM. A closure cannot override code or data placement with `[Section]`, reference managed static storage, use a non-constant static initializer, or reference a flash-backed string literal. It can use registers, linker symbols, MMIO, CPU and endian intrinsics, and compatible unmanaged constants. Residency violations report `CT2216`.

`[InterruptSafe]` accepts no arguments. It is valid only on an extern method, extern data field, or inline assembly statement reached by interrupt code. Every such native boundary must carry the attribute; otherwise the interrupt closure reports `CT2216`. The attribute is an explicit placement and execution-context trust assertion. It does not imply `[NoRuntime]`, `[NoBlock]`, `[NoThrow]`, or `[NoAlloc]`; the required effect contracts remain independent and are checked by the shared effect engine.

## Core library

Hosted, Cosmopolitan, ESP-IDF, and freestanding compilations import the common `System` standard library, including `Object`, runtime-fault exception types, `Console`, `Environment`, single- and double-precision `Math`, collections, I/O, diagnostics, managed threading, and the mutable geometry value types. `System.Storage` is selected only for a non-freestanding compilation that refers to its surface; selection also brings in its internal native-UTF-8 and common I/O dependencies even when the user source does not name those helper types. Freestanding operational services are reachability-gated through `[RuntimeImpl]` providers and runtime faults are terminal rather than catchable; source exception regions remain unavailable. ESP-IDF APIs remain target-specific. The exact API and runtime behavior are in [STDLIB.md](STDLIB.md).

Hosted Windows startup changes attached console input and output code pages to UTF-8 after runtime fault initialization and before static initialization. It flushes output and restores the prior code pages after static finalization. Redirected handles and pipes are not console-transcoded and receive exact UTF-8 bytes. `Console.InputEncoding` and `OutputEncoding` are read-only and report the strict `System.Text.Encoding.UTF8` singleton on every console-bearing target.

Every target provides synchronous `System.IO`. Hosted Windows/Linux and Cosmopolitan use native adapters, ESP-IDF uses VFS defaults unless overridden, and freestanding requires the matching runtime-provider groups. The move-only `FileHandle` operations include 64-bit seek, position, length, truncation, and flush. Managed `FileStream`, `StreamReader`, and `StreamWriter` own or borrow explicit resources and require idempotent `Dispose`; use after disposal raises `ObjectDisposedException` on exception-capable targets and panics on freestanding. UTF-8 readers strip one leading BOM, reject malformed or truncated UTF-8, recognize LF and CRLF, and preserve embedded NUL bytes. Writers use a 4096-byte buffer, deterministic LF line endings, and emit the UTF-8 BOM once only for `UTF8WithBom` at the beginning of an empty seekable stream.

`File`, `Directory`, and `Path` provide synchronous byte/text helpers, copying, moving, deletion, recursive creation and deletion, current-directory access, platform separators, and deterministic ordinally sorted full-path enumeration. Recursive deletion inspects links and reparse points and never traverses them. `FileMetadata` reports kind, attributes, length, and explicitly available Unix-second/nanosecond timestamps; metadata inspection itself does not follow symbolic links. Missing-path `Exists` calls return false. Native-adapter failures throw `IOException` with a platform error code and operation. Freestanding and explicit ESP-IDF provider status failures instead route status plus native code to `Runtime.Panic`. Async I/O, watchers, globbing, lazy enumeration, and per-stream concurrency are not part of Draft 0.46.

`System.Storage.BlockDevice` is an explicitly disposed, opaque byte device. It reports length, read/write/erase alignments, preferred transfer sizes, and read-only state. Checked raw operations reject disposed, mounted, overflowing, out-of-range, or misaligned access. A slice retains its parent until disposal. `MbrPartitionTable` reads exactly four primary entries and can replace their layout in one sector write while preserving bootstrap and disk-signature bytes; protective GPT, extended partitions, overlap, invalid active flags, and LBAs outside the device are rejected. Partition-formatting APIs never run implicitly during mount.

`FatFileSystem.Format` explicitly formats an unmounted device as FAT12, FAT16, or FAT32. `FatFileSystem.Mount` returns an idempotently disposed `MountPoint` with state, generation, capacity, and free-space information. Mount prefixes are absolute normalized UTF-8 paths no longer than 15 bytes. The ESP-IDF `Esp.Idf.Storage` implementation provides `SdSpiCard`, the T-CAN485 SPI2 preset, and `RemovableSdCardMonitor`. The preset uses MISO 2, MOSI 15, SCLK 14, CS 13, and a 20 MHz clock. Monitoring probes card status every 500 ms, retries absence every second, invalidates handles by mount generation before unmount, and remounts configured whole-card or MBR slices after reinsertion. A configuration, partition, or mount error enters `Faulted` and remains latched until `Remount` requests a new probe. Unsafe physical removal can lose unflushed data.

Runtime ABI 19 gives each managed process an independent current directory initially set to `/` and process-owned opaque file and directory handles. Normal exit, cancellation, forced termination, or storage-generation invalidation closes those handles. Managed applications can access the firmware's complete mounted VFS namespace. Current ESP-IDF FAT and LittleFS adapters expose no symbolic links; a future VFS that does must preserve the no-follow metadata and recursive-deletion contract.

`Vec2`, `Vec3`, and `Vec4` remain scalar geometry structures. `System.Simd` defines fixed 16-byte `F32x4`, `I32x4`, `U32x4`, and `Mask32x4` values with constant lane access, shuffle, comparisons, selection, and arithmetic. Scalar lowering is the default. `CpuFeature.Simd128`, `Target.HasFeature`, manifest `cpuFeatures`, and CLI `--cpu-feature simd128` enable architecture-validated x86 or Arm intrinsic lowering explicitly.

Draft 0.40 does not provide `System.Type`, reflection, or `System.Convert`.

## Object and array creation

`new Class(args)` allocates one zero-initialized managed object. It then runs the selected base and same-type constructor chain.

`new Struct(args)` creates and returns a structure value.

`new T[length]` checks the length and allocates a zero-initialized managed array.

If a class or structure declares no constructor, it has an implicit public parameterless constructor. A class constructor calls an accessible base constructor.

A constructor can use `: base(args)` or `: this(args)`. The compiler rejects constructor cycles.

Construction allocates the most-derived object and installs its runtime type before it invokes the initializer chain. Base construction runs before derived field initializers and constructor bodies. A virtual call during construction dispatches to the most-derived runtime type.

## Expressions

Primary expressions include literals, names, `this`, `base`, member access, calls, indexing, construction, and parentheses.

A non-void call is a value expression and can appear in any compatible context.

`sizeof(T)`, `alignof(T)`, and `offsetof(T, Field)` are type-only operators whose result type is `nuint`. The first two accept complete unmanaged scalars, enums, pointers, unmanaged function pointers, structures, and unions. `offsetof` requires a directly declared accessible instance field of a structure or union; dotted field paths are not supported. Pointer-containing operands require an `unsafe` context. These expressions are symbolic compile-time constants usable wherever a constant expression of compatible type is required, including arithmetic and comparisons. They are not permitted inside `[Packed]` or `[FieldOffset]`, and `sizeof(expression)` is not supported.

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

Numeric types support `+`, `-`, `*`, and `/`. Integral types also support `%`. Operands follow the numeric promotion rules above. Integer division truncates toward zero.

When an arithmetic operand is a class or structure, the compiler resolves a matching user-defined operator and lowers it as an ordinary ARC-aware static call. Operands are evaluated from left to right exactly once and remain alive through the call. Constant folding applies only to built-in operators.

The compiler rejects `float % float`, `float %=`, and `~float` with typing diagnostics.

Signed integer arithmetic wraps in two's-complement form. Division or remainder by zero throws the allocation-free `DivideByZeroException` singleton. `int.MinValue / -1` and `long.MinValue / -1L` wrap to their respective minimum values.

String `+` concatenates two string-compatible operands. A null string operand is empty.

### Comparison and logic

Comparisons produce `bool`. Ordered comparisons require numeric operands or the same enum type.

References, delegates, and pointers can compare with `null`. String equality compares contents. Delegates and unmanaged function pointers use identity/address equality only with their exact type.

`!`, `&&`, and `||` require `bool` operands.

### Bitwise and shifts

Integral types and enums support `~`, `&`, `|`, `^`, `<<`, and `>>`. Binary enum bitwise operands must have the same enum type.

A shift count uses its low six bits for `long` and `ulong`, its low five bits for fixed 32-bit and smaller integral values, and the low five or six bits for `nint` and `nuint` according to the target pointer width, after conversion to `int`.

### Assignment

Assignment requires an assignable target and a compatible value. The expression produces the assigned value.

Fields, writable properties, array elements, locals, parameters, and unsafe dereferences can be assignment targets.

`+=`, `-=`, `*=`, and `/=` use the corresponding user-defined binary operator when applicable. The target location is evaluated once, its old value is preserved while the right operand runs, and the operator result must convert implicitly back to the target type. The final write uses the same ARC strong-slot replacement as ordinary assignment. `%=` remains built-in and integral-only.

## Statements

### Blocks and empty statements

A block creates a lexical scope. A single semicolon is an empty statement.

### Lock

`lock (mutex) { ... }` accepts one non-null `System.Threading.Mutex`. The expression is evaluated once and retained through the complete braced block. The compiler enters the recursive mutex before the body and guarantees `Exit` on fallthrough, return, break, continue, and C~ exception propagation by using the same cleanup-transfer machinery as `defer` and `finally`.

Acquisition is an acquire synchronization operation and release is a release synchronization operation. A null mutex throws `NullReferenceException`. Calling `Mutex.Exit` without owning the mutex throws `SynchronizationLockException`; destroying an entered mutex is an unrecoverable lifecycle failure.

### Selection

`if` requires a `bool` condition. Braces are optional for one embedded statement.

`switch` accepts an integral or enum value. The compiler converts each case constant to the governing type. It rejects out-of-range and duplicate converted values.

One `default` label is permitted. A section must end with `break`, `continue`, `return`, or `throw`. Implicit fallthrough is not permitted.

A switch completes a non-void return only when it has `default` and every reachable section returns.

Draft 0.40 has no pattern cases and no `goto case`.

### Loops

`while`, `do while`, and `for` use `bool` conditions. An omitted `for` condition is true.

`foreach` first uses the optimized one-dimensional array path. Otherwise it prefers an accessible concrete `GetEnumerator()` whose result has `bool MoveNext()`, readable `Current`, and `void Dispose()`, then falls back to `IEnumerable<T>`. Concrete structure enumerators remain unboxed; interface enumeration can box them. The enumerator is disposed on exhaustion and every control-flow exit.

An ordinary method returning `IEnumerable<T>` can contain `yield return expression;` and `yield break;`. Each call produces an enumerable and each enumeration has independent state. Execution begins with the first `MoveNext`; disposal is idempotent and terminal. Iterator creation and enumeration allocate and therefore cannot satisfy `[NoAlloc]`. `yield` is invalid in constructors, accessors, operators, lambdas, `catch`, `finally`, `lock`, or where active `try` or `defer` cleanup would cross suspension.

`break` exits the nearest loop or switch. `continue` starts the next iteration of the nearest loop.

A `do` body executes once for definite assignment. The compiler merges normal condition exits with all early `break` exits.

### Return

`return;` exits a void method. `return expression;` exits a non-void method with a converted value.

Constructors do not use a C~ return statement.

### Defer

`defer Call(args);` schedules one method invocation for the end of the containing braced block. The receiver and converted arguments are evaluated in source order and copied into hidden durable automatic storage when execution reaches the statement. A returned value is discarded. A non-call expression reports `CT2156`.

Deferred calls run in reverse registration order on fallthrough, `return`, `break`, `continue`, and C~ exception propagation. A deferred by-reference call can refer to a local that remains in the enclosing lexical scope; it runs before that local's ownership cleanup. A defer in a loop block registers once for each executed iteration. `defer` must be a direct member of a braced block; an `if`, loop, or switch section must add braces around it. Invalid placement reports `CT3111`.

If cleanup throws, older enclosing defers still run. The final cleanup exception replaces an earlier exception or pending return, matching nested `finally` behavior. Runtime panics, `Environment.Exit`, native `abort`, reset, and power loss do not run deferred calls.

### Exceptions

`throw expression;` throws a reference whose runtime type derives from `System.Exception`. The conversion to `System.Exception` is implicit. Throwing `null` raises the immortal `NullReferenceException` singleton with origin code `CTE0002`.

`throw;` rethrows the current exception. It is valid only inside a catch clause. Rethrow preserves the same managed object.

A try statement has one or more catch clauses, a finally clause, or both:

```csharp
try
{
    Work();
}
catch (SpecificException error)
{
    Console.WriteLine(error.Message);
}
catch (Exception)
{
    throw;
}
catch
{
    Console.WriteLine("fallback");
}
finally
{
    Console.WriteLine("cleanup");
}
```

Catch clauses run in source order. A typed catch accepts the declared type and its derived types. A catch can omit its local name. A catch-all has no parentheses and must be last. A catch is invalid when an earlier compatible catch makes it unreachable.

Exceptions are unchecked. Every call can complete by throwing. A throw from a catch propagates to an enclosing handler and cannot enter a sibling catch.

A finally block runs when its protected statement completes normally, returns, breaks, continues, or throws. A return, break, or continue cannot leave a finally block. A throw from finally replaces the pending action. `Environment.Exit` terminates the process without running finally blocks or defers.

Exception filters, inner exceptions, stack traces, user-defined runtime-fault policies, and automatic disposal are not part of draft 0.25. An exception that escapes a supported synchronous native boundary becomes a panic with `CTE0003`; general exception propagation across native boundaries is unsupported.

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

Unsafe code remains statically typed. `unsafe` permits pointer declarations, address-of, dereference, pointer indexing, pointer arithmetic, pointer casts, unmanaged function-pointer declarations, method-address acquisition, native-buffer construction, stack allocation, comparisons, casts, and invocation. It does not disable normal type checking.

### Inline assembly

An assembly function places `asm` after `static unsafe` and before the result type. It is body-bearing, non-generic, and non-virtual, and may be called like an ordinary C~ function:

```csharp
[NoRuntime]
[NoBlock]
public static unsafe asm uint ReadPort(uint port)
    (in("d") port, out("a") result as value)
{
    inl port, value
}
```

Parameters and results are limited to inline-assembly-compatible scalar, enum, newtype, opaque, pointer, or unmanaged-function-pointer values. Every declared parameter appears exactly once with a compatible `in`, `out`, or `ref` role. A non-void method declares exactly one `out result` operand; a void method cannot declare `result`. The reserved `result` name is local to the declaration. Assembly functions reuse the constraint, alias, clobber, substitution, and validation rules below.

A non-naked assembly function emits an ordinary C ABI definition containing one `__asm__ volatile` block and, for a non-void result, a compiler-generated C return. Its assembly falls through exactly once and cannot contain its own ABI return. `[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]` independently contract the opaque boundary; omitted contracts remain conservatively unknown to constrained callers. `[Export]`, `[Used]`, `[Section]`, and `[InterruptSafe]` follow their ordinary method rules. Raw assembly calls remain opaque to reachability: native calls from a raw body must manually obey the ABI and target a stable exported/native symbol.

Assembly functions select the same GNU assembly toolchain path as inline assembly. GCC and Clang are supported; hosted MSVC native builds reject them. Symbol maps record `assemblyFunction`.

An `asm` statement contains raw GNU assembly text and requires an unsafe method or block. The compiler does not parse instructions, registers, directives, or the target instruction set. The selected GCC or Clang assembler validates them.

```csharp
[NoAlloc]
asm (
    in value as source,
    out("&r") result,
    ref accumulator,
    clobber("eax", "cc", "memory")
) {
    movl source, %eax
    addl %eax, accumulator
    movl %eax, result
}
```

The operand clause is optional. `asm { nop }` is a valid operand-free block. `in` is a read-only input, `out` is a write-only output, and `ref` is a read/write operand. An operand names a local or parameter. `as` supplies the standalone name used in the assembly template; without it, the source variable name is used. Raw registers such as `%eax`, immediates such as `$1`, quoted text, and identifiers prefixed by `.`, `%`, or `$` are never substituted.

The default constraint is `r`. The compiler emits it as `r`, `=r`, or `+r` according to the operand role. A parenthesized string replaces the default; it omits the `=` or `+` direction marker, so `out("&r")` emits `=&r`. Float operands require an explicit target constraint. Constraint spelling and register availability are GNU backend rules.

Operands are limited to Boolean, numeric, enum, opaque-handle, unsafe-pointer, and unmanaged function-pointer locals or parameters. Managed references, arrays, delegates, native-buffer views, and structures are invalid. `in` and `ref` require definite assignment. `out` assigns its variable after the statement. `out` and `ref` require mutable storage. A variable cannot appear in more than one operand; use `ref` for read/write storage.

Every block emits GNU `__asm__ volatile`. The compiler adds no implicit clobbers. Changed registers and the special `cc` and `memory` effects must be listed with `clobber`. The block must fall through once, preserve the stack and ABI-required state, and must not unwind, throw, jump into or out of generated C~, or mutate managed ownership state except through declared scalar operands. Violating this contract has undefined behavior.

Inline assembly is an unknown boundary for all effects. The zero-argument `[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]` statement attributes independently assert the block's behavior. `NoRuntime` implies `NoThrow` and `NoAlloc` but not `NoBlock`. Draft 0.21 runtime implementations and naked startup retain compatibility with their existing trusted `[NoAlloc] asm` form.

The feature is supported by GCC and Clang for hosted and ESP-IDF builds. The CLI rejects a hosted build that selects MSVC when the program contains `asm`. Emit-only output remains GNU C23.

## XML documentation

Three-slash comments immediately before a type, delegate, opaque type, field, property, constructor, method, or enum value attach XML documentation to that declaration. Attributes and modifiers can appear between the comment and the declaration. A blank line or ordinary comment breaks attachment. Unattached documentation reports warning `CT5006`; documentation warnings never prevent checking or C emission.

The supported elements are `summary`, `param`, `returns`, `remarks`, `exception` with `cref`, inline `see` with `cref`, inline `paramref` with `name`, and `inheritdoc`. References use the current namespace and imports and can select a member overload with a C~-style parameter list. XML DTDs and external entities are prohibited. Raw Markdown, raw HTML, block documentation comments, and documentation-file emission are not supported.

`inheritdoc` must be the only documentation element. It explicitly copies documentation from an overridden method or property, or from a base type. Override parameter descriptions are matched by ordinal so parameter names can differ. Documentation is never inherited automatically.

Malformed XML (`CT5000`), unsupported structure (`CT5001`), duplicate sections (`CT5002`), unknown parameters (`CT5003`), unresolved references (`CT5004`), and invalid or cyclic inheritance (`CT5005`) are warnings. The compiler does not warn when a declaration has no documentation.

## Time, random generation, and managed concurrency

`System.TimeSpan` is an exact signed nanosecond value available on every target. It provides zero, exact and truncated unit access, fractional millisecond and second totals, integer factories, wrapping arithmetic, equality, and ordering. Negative values are valid, and its natural layout is exactly one signed 64-bit field.

`System.Diagnostics.Stopwatch` is an allocation-free mutable value available on every target. It reads a monotonic nanosecond clock, accumulates elapsed time across starts and stops, and exposes `StartNew`, `Start`, `Stop`, `Reset`, `Restart`, `IsRunning`, `Elapsed`, `ElapsedNanoseconds`, and truncated `ElapsedMilliseconds`. Repeated `Start` and `Stop` calls are idempotent. Value copies have independent state, and instances are not thread-safe. `GetTimestampNanoseconds()` exposes the same monotonic clock. Freestanding programs supply `Runtime.MonotonicNanoseconds`; other targets use their platform clock unless overridden. A failed clock query terminates with `CTK0001`; unused programs emit no clock support.

`System.Random` is an allocation-free value available on every target. `Random()` uses deterministic seed zero; `Random(ulong)` and `Reseed` select another sequence. `NextUInt()` uses the fixed Draft 0.40 PCG-XSH-RR 64/32 multiplier, increment, direct seed-to-state mapping, xorshift, and rotation algorithm. `NextUInt(maxExclusive)` and `NextInt(minInclusive, maxExclusive)` use rejection sampling and exclude their upper bound. `NextFloat()` uses 24 random bits and returns a value in `[0,1)`. Seeded sequences are a cross-target compatibility contract. Invalid ranges throw the preinitialized `ArgumentOutOfRangeException` singleton with origin code `CTR0001`.

`System.Threading.Atomic<T>` provides load, store, exchange, compare-exchange, fetch-add, fetch-subtract, fetch-and, fetch-or, and fetch-xor. `T` can be Boolean, integral, native-integral, enum, or unsafe pointer. Pointer atomics support only load, store, exchange, and compare-exchange. Arithmetic fetch operations require an integral type; bitwise fetch operations require Boolean or integral storage. Operations return the value observed before modification.

`MemoryOrder` values are `Relaxed`, `Acquire`, `Release`, `AcquireRelease`, and `SequentiallyConsistent`. Loads reject release and acquire-release. Stores reject acquire and acquire-release. Compare-exchange failure order is relaxed, acquire, or sequentially consistent and cannot be stronger than its success order. Invalid dynamic orders throw the allocation-free `ArgumentException` singleton. `Atomic.Fence` emits a fence with the selected order.

An `Atomic<T>` value is non-copyable. It can be directly constructed as a local, static, or field. A structure can contain atomic storage and can itself be directly constructed, but the containing structure becomes non-copyable. Atomic-containing values cannot be assigned from another value, returned by value, boxed, stored in an array or property, or passed by value.

`System.Threading.Thread` starts one managed `ThreadStart` delegate on a new OS thread or FreeRTOS task. `Start` publishes captured state before invocation; `Join` supplies an acquire edge after completion. `Sleep` and `Yield` delegate to the target scheduler. `Id` is a stable C~ runtime ID. Explicit stack size and priority requests are validated by the target; a non-default priority that cannot be applied throws `ThreadStateException` instead of degrading silently.

A thread starts at most once. Joining before start, starting twice, or joining itself throws `ThreadStateException`. A worker retains its delegate and control object until completion, so releasing the source `Thread` reference does not cancel it. An exception escaping the delegate is fatal through the existing unhandled-exception path. Workers attach to independent C~ exception, cleanup, ARC-release, and debug state before invoking source code and detach after cleanup.

`System.Threading.Mutex` is process-local and recursive. `Enter`, `TryEnter`, and `Exit` map to `CRITICAL_SECTION`, recursive POSIX or Cosmopolitan mutexes, or recursive FreeRTOS mutexes. Draft 0.40 does not include cancellation, interruption, affinity, naming, timed joins, source thread-local declarations, pools, futures, `Task`, or `async`.

`System.Threading.SpinWait` performs exponentially increasing `Cpu.Pause` work for its first ten calls and then calls `Thread.Yield`; its count saturates instead of wrapping and `Reset` restores zero. `System.Threading.SpinLock` is non-recursive and unfair, does not track thread ownership, and uses acquire compare-exchange plus release store. It contains `Atomic<int>`, is non-copyable, and requires the caller that successfully enters to call `Exit`.

## Managed lifetime and failures

C~ source has no `delete` operator, destructors, user finalizers, or weak references. Draft 0.42 uses thread-safe, non-moving automatic reference counting for classes, arrays, strings, boxes, interface views, closure state, and references nested in naturally laid-out structures. Heap objects begin with one atomic owned reference and are reclaimed on the thread that releases the last owned reference. Dynamic strings and arrays each occupy one checked contiguous allocation. Static and empty strings are immortal. Static managed fields own their values until runtime shutdown, when they are dropped in exact reverse initialization order and cleared.

Parameters and `this` are borrowed. Managed-reference and reference-containing structure results are owned. Owning locals, fields, properties, array elements, temporaries, boxes, and structure copies retain or transfer their contents as required. Cleanup runs on normal block exit, return, break, continue, and C~ exception propagation. Reference cycles intentionally leak in draft 0.25.

C~ uses a data-race-free memory model for hosted, Cosmopolitan, ESP-IDF, and provider-backed freestanding programs. Concurrent reads are allowed. Conflicting accesses to an ordinary location, when at least one access writes and no synchronization orders them, have undefined behavior. ARC atomics protect lifetime only: they neither publish object contents nor make reference slots, fields, array elements, or static fields atomic. A reference transferred between threads requires an owned count plus synchronization. Thread start, join, mutex operations, volatile fields, atomics, and `SpinLock` establish the documented happens-before edges. Correctly synchronized programs behave sequentially consistently; relaxed atomics provide atomicity without publication, and sequentially consistent atomics participate in one total order. A freestanding backend that does not expose threads can omit the thread and mutex groups.

An ordinary hosted, freestanding, Cosmopolitan, or ESP-IDF program owns one C~ runtime. `ct_runtime_initialize(config)` attaches the calling primary thread, initializes immortal runtime-fault objects and the ABI-versioned module descriptor, then publishes the ready phase. `ct_runtime_shutdown()` requires all secondary threads detached, finalizes modules, drains ARC work, and detaches the primary thread. `main` and `app_main` perform this lifecycle automatically. A native-created thread uses `ct_thread_attach()` and `ct_thread_detach()` between those calls and must detach with no active C~ calls, cleanup records, exception frames, pending exception, or release drain. Export wrappers, callback trampolines, `ct_retain`, and `ct_release` require attachment. Runtime-phase misuse and unattached entry are panics.

An ESP-IDF managed-module host instead owns one firmware process host and runtime-service table shared by all logical processes. Each current application process has independent mutable module instances, allocation accounting, main-task state, cancellation, arguments, and mailboxes. An application or structurally loaded dependency cannot unload while a process, dependent load reference, tracked active call, allocation, or type registration can still refer to its code or metadata; the current loader unregisters its types and releases the dependency graph in reverse order after those references disappear. The complete Managed Module ABI will extend those rules to callable managed exports, descriptors, vtables, delegates, callbacks, function leases, and registered resources.

`System.Runtime.Memory.Retain` and `Release` manipulate an additional untracked ownership count. `null` is a no-op. They are unsafe APIs: unbalanced use can leak, dangle, or double-release a value. Calling any unsafe method requires an unsafe method or block and otherwise reports `CT2139`.

External resources require explicit release. `defer Release(handle);` reserves an owned opaque handle's cleanup immediately, forbids reassignment or a second transfer, and still permits borrowed use until the block exits. Cleanup runs before ordinary lexical ownership teardown. There is no language `using` statement or automatic `Dispose` convention.

Managed null access, null unboxing, and `throw null` raise `NullReferenceException`; required null arguments raise `ArgumentNullException`; array and native-buffer bounds raise `IndexOutOfRangeException`; integer division or remainder by zero raises `DivideByZeroException`; invalid casts and mismatched unboxing raise `InvalidCastException`; negative or overflowing array, stack, and string sizes raise `OverflowException`; embedded NUL or invalid native UTF-8 raises `ArgumentException`; malformed composite or scalar formats raise `FormatException`; and managed allocation failure after attachment raises `OutOfMemoryException`. Compiler-generated runtime-fault objects are immortal and preinitialized, so raising those faults allocates nothing and remains valid inside `[NoAlloc]`; explicitly constructing an exception is an ordinary allocation. The original runtime code and source location are per-thread exception-origin metadata and survive calls, cleanup, and rethrow.

Runtime-phase misuse, unattached entry, reference-count corruption, cleanup corruption, ABI mismatch, pre-attachment allocation failure, and exceptions escaping callbacks or exports are panics. A configured native panic callback runs first; returning invokes the platform's default fatal termination.

An unhandled exception prints `CTE0001`, its fully qualified runtime type, and its non-empty message. It then exits with `EXIT_FAILURE`.

## Compile-time and native-system facilities

`System.Runtime.Target.Profile`, `Architecture`, `Environment`, and `PointerSize` are compiler constants and have no runtime storage. `Target.Environment` is `TargetEnvironment.Native` for ordinary targets and `TargetEnvironment.Qemu` for an emulator alias. The architecture is selected through `CompilationOptions.Architecture`, top-level `architecture` in `ctilde.json`, or CLI `--architecture`; CLI input has precedence. The supported values are `auto`, `x86`, `x64`, `arm32`, `arm64`, `xtensa`, `riscv32`, and `riscv64`. An architecture-dependent query with no resolved architecture reports `CT4108`.

The public targets `esp32_qemu` and `esp32c3_qemu` both retain `TargetProfile.EspIdf`. They set `Target.Environment` to `Qemu` and fix `Target.Architecture` to `Xtensa` and `RiscV32`, respectively. An explicit conflicting architecture is rejected. The ordinary `esp-idf` target remains `Native` and continues to infer its physical chip from ESP-IDF configuration.

The `cosmopolitan` target requires `architecture: "x64"`; automatic or other architecture selection reports `CT4118`. It requires one ordinary `[EntryPoint]`, generates hosted startup and runtime lifecycle, and exposes the hosted object, exception, console, file, math, timing, random, threading, mutex, and TLS facilities. `Target.Profile` is `TargetProfile.Cosmopolitan`, `Target.Architecture` is `TargetArchitecture.X64`, and `Target.PointerSize` is eight. The native build emits an x86-64 APE distribution image and retains its ELF/DWARF carrier as `<image>.dbg`. Arm64 and fat multi-architecture APEs are outside Draft 0.40.

`static if (condition) statement else statement` is statement-level compile-time selection. Its condition must be a compile-time Boolean. Only the selected branch is bound, analyzed, lowered, and made reachable. The inactive branch must parse but can name APIs unavailable on the selected target.

`static assert(condition);` and `static assert(condition, "message");` are valid at file/namespace and type-member scope. A known false condition reports `CT2201`; a non-Boolean or non-constant condition reports `CT2200`. Layout-dependent assertions over `sizeof`, `alignof`, and `offsetof` emit deterministic C `static_assert` declarations after their required layouts. A closed generic type evaluates its assertions for each emitted specialization.

`[Used]` accepts no arguments and applies to body-bearing static methods and owned, non-const, complete unmanaged static fields. It is a reachability root and guarantees final-image retention on supported ELF and COFF toolchains: GNU and Clang use `used, retain`, while MSVC and clang-cl use deterministic `/INCLUDE` directives. It propagates to otherwise-created closed generic specializations; an export retains both its wrapper and implementation, while an entry retains only its implementation. Symbol maps record `used` and `linkerRetained`. Invalid targets report `CT1288`; unsupported object formats report `CT4111`.

`[LinkerSymbol("symbol")]` declares a storage-free static unsafe readonly field whose type is a pointer, `nuint`, or a `nuint`-backed newtype. Reading it evaluates to the linker's symbol address. Assignment, `ref`, `out`, and address-taking are invalid. The compiler emits one sorted `extern unsigned char symbol[]` declaration, permits compatible duplicates, exposes public declarations in the native header, and does not root the declaration. Invalid declarations report `CT1296`; incompatible native declarations use the native-symbol conflict diagnostics.

`[Extern("symbol")]` can declare a static data field with no initializer and a complete unmanaged non-generic type. Every access, reference, or address operation requires unsafe context. `readonly` emits native `const`; `[NativeVolatile]` emits native `volatile`; C~ `volatile`, `[Section]`, and `[Used]` are forbidden on extern data. Invalid declarations report `CT1289`; invalid native volatility reports `CT1290`.

`System.Runtime.Mmio` provides `Read<T>`, `Write<T>`, `ReadRelaxed<T>`, `WriteRelaxed<T>`, and `Barrier`. `T` must be a fixed-width integer, `char`, an enum with a fixed-width integer underlying type, an endian newtype, or a bitfield scalar. Each read or write emits exactly one naturally aligned volatile native access. Ordered accesses execute a full target I/O barrier before and after; relaxed accesses emit only the access. Invalid element types and known misalignment report `CT2203`; an unsupported ordered target reports `CT4109`.

`[BitField(typeof(T))]` declares a scalar-backed structure, where `T` is `byte`, `ushort`, `uint`, or `ulong`. `[Bit(n)] bool` and `[Bits(first, last)]` unsigned integer, unsigned enum, or endian-newtype members are overlapping views numbered from least-significant bit zero. Views have no storage and lower to masks and shifts; readonly views cannot be assigned. Explicit casts between the bitfield and its backing scalar preserve representation. Invalid declarations report `CT1297`; invalid ranges or operations report `CT2209`.

`[Register(address)]` declares a storage-free static unsafe fixed-address field of a fixed-width MMIO scalar, enum, or bitfield type. The address must be a naturally aligned compile-time value that fits the selected pointer width. Whole-field reads and writes perform one ordered volatile access. Direct bit-view reads perform one load; writes perform a non-atomic read-modify-write with one barrier before the load and one after the store. Readonly registers cannot be written, and register fields cannot use initializers, `ref`, `out`, address-taking, volatility, or conflicting native-storage attributes. Invalid declarations report `CT1298`; invalid types, addresses, alignment, or access report `CT2210`.

`[Align(n)]` requests a minimum native alignment on structs, unions, newtypes, owned static fields, value-aggregate instance fields, and non-durable locals. `n` is a compile-time power of two from 1 through 8192. It contributes to aggregate layout, `sizeof`, `alignof`, static and stack storage, generated headers, and native layout assertions. Packed members retain their pack; a field request cannot exceed the active pack, and an explicit offset must be divisible by its requested alignment. It does not control managed heap allocation or final linker placement. Invalid arguments and targets report `CT1293`.

`[NoRecursion]` applies to body-bearing methods, constructors, operators, and property accessors. It validates the complete transitive call closure after `static if` pruning and closed generic construction. The `noRecursion` project property and CLI `--no-recursion` apply the same rule to every method reachable from entry, export, task-entry, and `[Used]` roots. Virtual and interface calls expand to their finite closed target set; delegates, function pointers, and other unprovable dispatch are rejected. A deterministic cycle witness or unknown call reports `CT2206`; invalid attribute targets report `CT1294`.

`[StackUsage(N)]` accepts one positive byte count. On a body-bearing method it is a verified maximum for transitive native stack use; on an extern, native import, or assembly-only method it is a trusted terminal upper bound. Invalid forms and targets report `CT1323`. Static analysis is explicitly enabled by `--stack-report <path>` or project `build.stackReport` and requires a native GCC-family build. MSVC and Clang requests are rejected before compilation. GCC emits frame and callgraph sidecars; LTO analysis consumes final `.ltrans` sidecars. Recursion, unbounded dynamic frames, unresolved indirect calls, and unannotated native boundaries make a path incomplete. An exceeded or unverifiable method contract reports `CT2226`. The schema-v1 report is written atomically even when a contract fails.

`System.Runtime.Cpu` provides allocation-free full ordinary-memory barriers, spin-loop pause hints, 16/32/64-bit byte swaps, 32/64-bit population counts, and 32/64-bit leading-zero counts. Leading-zero count of zero equals the operand width. The compiler lowers target barriers and hints for x86/x64, ARM32/ARM64, Xtensa, and RISC-V, and otherwise uses deterministic baseline-safe C operations. `MemoryBarrier` is distinct from the MMIO I/O barrier. Unsupported target-dependent use reports `CT4110`; malformed intrinsic calls report `CT2207`. Privileged interrupt control, halt, atomics, and target-specific intrinsic namespaces remain deferred.

On ESP-IDF, `[TaskEntry(StackSize = N)]` combines with `[Export("symbol")]` on a public static non-generic `void(void*)` method body. `N` is a positive `uint` divisible by four and is measured in bytes. The exported wrapper attaches fresh C~ task state, installs the export exception barrier, calls the implementation, detaches on normal completion, calls `vTaskDelete(NULL)`, and never returns. The public header defines `CTILDE_TASK_STACK_<EXPORT>` with the configured value. A complete stack report records verified headroom and reports `CT2226` when the bound exceeds `N`; an incomplete graph is reported as unverified. Invalid metadata reports `CT1291`; invalid targets or signatures report `CT1292`.

ESP-IDF projects select `panicPolicy` as `abort`, `restart`, or `halt`; CLI `--panic-policy` overrides the manifest. The default is `abort`. Every policy invokes the native panic callback and prints and flushes the diagnostic first. Restart calls `esp_restart`; halt enters `esp_system_abort` and requires `CONFIG_ESP_SYSTEM_PANIC_PRINT_HALT=y` in the effective `sdkconfig`, which the native driver validates. Hosted restart or halt policy requests report `CT4113`.

Modular output assigns every reachable definition and generated export wrapper to `source_<stable-hash>.c` using its normalized source identity. `CompilationOptions.SourceIdentityRoot` defines path normalization; project builds use the manifest root. Bundled files use stable virtual paths, and pathless trees use content identities. Duplicate or unstable identities report `CT4112`. `ctilde_types.h`, `ctilde_runtime_internal.h`, and one declaration-owning `source_<stable-hash>.h` per source provide narrow dependencies. Implementations include only their direct owner dependencies; `ctilde_internal.h` remains a compatibility umbrella. Native object-cache identities hash each source and the transitive contents of the generated headers it actually includes.

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

A compiler conforms to Draft 0.50 when:

1. It implements every non-deferred rule in this document.
2. Invalid programs produce structured diagnostics and no C.
3. Repeated compilation produces byte-identical C.
4. Generated C compiles as GNU C23 without warnings.
5. Native execution passes the language and runtime conformance suite.

The canonical backend is GNU C23. Draft 0.50 has no second backend. Unity and modular layouts consume the same optimized whole-program IR and must have equivalent behavior.

## Deliberate differences from C#

- `char` is one UTF-8 code unit, not UTF-16.
- References, pointers, `nint`, and `nuint` use the target C ABI width.
- Signed integer overflow is always wrapping.
- `readonly` locals permit one delayed assignment.
- Managed ownership uses deterministic ARC; cycles leak, and `[NoAlloc]` is the compile-time allocation boundary.
- The core library is intentionally small.

Draft 0.50 defers Unicode escape syntax, locale-aware parsing and formatting, Unicode normalization and collation, UTF-16 encodings, asynchronous I/O, file watchers and mapping, Arm64 and fat Cosmopolitan output, project-wide effect switches, cleanup-aware iterator suspension, effect-polymorphic generics, effect-qualified delegates and function pointers, declaration-level conditional compilation, weak imports and definitions, write-only registers, atomic MMIO read-modify-write, general naked functions and generalized interrupt signatures, default interface implementations, generic variance, user-defined conversions, managed-reference and floating-point atomics, weak references, cycle collection, retained callbacks, owned resource fields, multidimensional arrays, string interpolation, general native-boundary unwinding, versioned hosted shared-library mapping, macOS loader support, GPT, extended/logical MBR partitions, shared SPI buses, native SDMMC, card-detect GPIOs, `[NoStackProbe]`, and `[StackAlign(n)]`.
