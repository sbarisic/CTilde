# C~ language specification

Specification version: draft 0.18

## Status

This document is the normative specification for C~ draft 0.18.

C~ is a statically typed language with C#-style syntax and a small managed runtime. A conforming draft 0.18 compiler emits deterministic GNU C23 unity or modular artifacts and diagnoses invalid programs before it writes C.

Draft 0.18 adds compile-time target selection and assertions, native object retention and data declarations, exact-width MMIO intrinsics, and ESP-IDF task-entry exports. It retains Draft 0.17 controlled section placement and all earlier language and runtime facilities. Runtime ABI 16 and debug metadata version 3 are unchanged.

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

Draft 0.18 has no aliases and no `using static`.

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
finally   float      for        foreach    get        if
in        int        interface  internal   is         lock       long
namespace
new       nint       nuint      null       object     out
override  private    protected  public     readonly   ref
offsetof  return     sbyte      sealed     set        short      sizeof
stackalloc static    string     struct     switch     this       throw
true      try        uint       ulong      union      unmanaged  unsafe
ushort    using      var        virtual    void       volatile   where
while
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

Integer suffixes are case-insensitive. With no suffix, the literal takes the first type that can contain it from `int`, `uint`, `long`, and `ulong`. `U` selects the first fit from `uint` and `ulong`; `L` selects the first fit from `long` and `ulong`; `UL` and `LU` select `ulong`. `F` selects `float`.

```csharp
42u
4_294_967_296L
18_446_744_073_709_551_615UL
3.5f
```

A literal must fit its selected type. Malformed, duplicated, mixed floating/integer, and overflowing suffixes are errors. The unary forms `-2147483648` and `-9223372036854775808L` are valid signed-minimum constants. An explicit cast applies normal truncation rules after literal typing.

### Character literals

`char` is one eight-bit UTF-8 code unit. A character literal must encode to exactly one byte.

Supported escapes are `\0`, `\a`, `\b`, `\t`, `\n`, `\v`, `\f`, `\r`, `\"`, `\'`, `\\`, and `\xHH`. A hexadecimal escape contains exactly two hexadecimal digits.

### String literals

Strings use double quotes and the character escape set. Draft 0.18 has no verbatim, raw, or interpolated strings.

String storage is UTF-8. `Length` counts UTF-8 code units, not Unicode scalar values. Indexing returns one read-only `char` code unit.

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
| `string` | Managed reference | Target pointer width |
| `object` | Managed root reference | Target pointer width |
| `void` | Return marker | None |

Class, array, string, delegate, unsafe-pointer, and unmanaged function-pointer values use the native pointer width of the selected C target.

Draft 0.18 has no `double` or `decimal`. Integer literals never infer `nint` or `nuint`; context can convert constants in the portable `int` or `uint` range, while larger `long` or `ulong` values require an explicit cast.

### Value and reference types

Numeric types, `bool`, structures, and enums are value types. Assignment copies the complete value.

Classes, arrays, strings, delegates, and `object` are managed reference types. Assignment copies object identity. Their default value is `null`.

Class, array, and `object` equality compares identity. String equality compares contents, with two null strings equal.

### Arrays

`T[]` declares a one-dimensional managed array reference.

```csharp
byte[] data = new byte[256];
```

Every array has a read-only `Length` property of type `int`. Indexing starts at zero and checks the receiver, index, and length.

Draft 0.18 has no multidimensional or jagged arrays.

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

Delegate creation allocates a target-and-thunk object. Parameters are borrowed and managed or reference-containing results are owned, like ordinary C~ calls. Invocation propagates C~ exceptions normally, and null invocation throws `NullReferenceException` with origin code `CTN0001`. Equality compares delegate object identity, not target and method structure. Delegates can be generic and can be stored in fields, arrays, structures, parameters, returns, and boxes. Draft 0.18 delegates are single-cast: there are no lambdas, closures, multicast operations, open-instance delegates, generic `Action`/`Func`, or variance.

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

An interface is a managed contract type. It can inherit several interfaces and contains public instance method and property declarations without bodies. Classes and structures can implement several interfaces implicitly by providing identical public members. One implementation can satisfy the same inherited contract from several interfaces.

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

A class retains one class base and lists interfaces after it. An abstract method or property has no body and is valid only in an abstract class. Abstract classes cannot be constructed. Every non-abstract class and every structure must implement all inherited abstract and interface contracts. Interface implementations are public and implicit; explicit implementations, default interface bodies, static abstract members, and variance are not supported.

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

### Generic declarations

Classes, structures, interfaces, delegates, and methods can declare type parameters. Closed types and explicit method arguments use angle brackets; method arguments can also be inferred from value arguments.

```csharp
public class Box<T> { public T Value; }
public static T Identity<T>(T value) { return value; }
Box<int> box = new Box<int>();
int value = Identity(box.Value);
```

A `where` clause can require `class`, `struct`, `unmanaged`, one base class, any number of interfaces, and `new()`. Constraint members are available in the generic body, and `new T()` requires `new()`. The compiler validates constraints before binding a closed instance.

Generics use whole-program monomorphization. Each reachable closed type and method has separate code, layout, ARC helpers, allocation analysis, and static storage. Open generic values cannot reach native ABI boundaries. An expanding instantiation chain is rejected with `CT1272`. Variance, partial or return-context inference, reflection metadata, specialization syntax, and static-abstract generic arithmetic are not supported.

## Methods

A method declares a return type, name, parameters, and body.

Methods can be overloaded by parameter types. Resolution includes accessible inherited methods with the correct argument count.

The compiler compares candidates for each argument. Identity is better than widening. One widening target is better when it converts implicitly to the other target only. If two integral widening targets remain, a signed target is better than an unsigned target.

A candidate must be no worse for every argument. It must also be better for at least one argument. Otherwise, the call reports `CT2123`.

Parameters and call arguments can be marked `ref`, `in`, or `out`. The call site must use the same modifier, and passing kind is part of overload and delegate/function-pointer signature matching. `ref` requires an assigned writable variable, `in` requires an assigned addressable variable and is read-only in the callee, and `out` is unreadable until assigned and must be assigned on every normal return. Properties, constants, literals, and temporaries are not by-reference arguments. Readonly storage can be passed only with `in`.

An `out` call treats the caller slot as an uninitialized destination. The caller drops an initialized managed/reference-bearing value, clears the slot to a safe empty state, and marks it uninitialized before entry. The callee's first assignment constructs or moves directly into the destination without reading, retaining, or dropping an old value. Later assignments use ordinary strong-slot replacement. The rule is identical for methods, constructors, delegates, unmanaged function pointers, externs, and exported C declarations. Normal return assigns the caller slot; exceptional control leaves its safe empty value but does not make it definitely assigned. Native `ref` and `out` parameters map to `T*`; native `in` maps to `const T*`. Buffer parameters cannot themselves be by-reference.

Draft 0.18 has no optional, named, implicit by-reference, reference-return, reference-local, or parameter-array arguments. An overload cannot differ only between `ref` and `out`.

### User-defined arithmetic operators

A non-static class or structure can declare public static unary and binary arithmetic operators:

```csharp
public static Vector3 operator +(Vector3 left, Vector3 right) { ... }
public static Vector3 operator -(Vector3 value) { ... }
public static Vector3 operator *(Vector3 value, float scale) { ... }
```

The supported declaration tokens are binary `+`, `-`, `*`, and `/`, plus unary `+` and `-`. Unary declarations have one value parameter. Binary declarations have two value parameters. A unary parameter must be the exact containing type; at least one binary parameter must be the exact containing type. The result can be any non-`void` type. Operators must have bodies and cannot use `ref`, `in`, or `out`. They can use `unsafe` and `[NoAlloc]`, but cannot be virtual, overrides, externs, exports, entry points, or native-ownership contracts. Invalid declarations report `CT1269`.

Operator lookup examines the operand class or structure types and their base chains, then deduplicates shared declarations. Existing implicit conversions and better-conversion rules select the overload. No match reports `CT2167`; no unique best match reports `CT2168`. Built-in arithmetic remains preferred when both operands have built-in types. The compiler does not infer scalar symmetry: `vector * scale` and `scale * vector` require separate declarations.

Operator declarations are statically dispatched and are not ordinary named members. They do not appear in member access or member completion and cannot be called through names such as `op_Addition`. Class operands are borrowed and can be null; the operator body is responsible for null handling.

### Evaluation order

The receiver is evaluated first. Arguments are evaluated from left to right. Binary operands are evaluated from left to right.

`&&` and `||` evaluate the right operand only when required. Compound assignment evaluates its left expression once.

These rules apply even when the target C compiler leaves an equivalent C expression order unspecified.

### Returns

A `void` method returns no value. A non-void method must return a compatible value on every reachable path.

Statements after an unconditional return, break, continue, or throw are unreachable.

### EntryPoint

Exactly one method has `[EntryPoint]`. It must be a body-bearing `static void` method with no parameters.

The C backend generates `int main(void)`, initializes static storage, calls the entry method, and returns `EXIT_SUCCESS`.

### Extern

`[Extern("symbol")]` marks a static bodyless method supplied by native code.

The symbol must be a portable C identifier. Its native signature must use the C~ mappings in [C_ABI.md](C_ABI.md).

The compiler rejects `main`, runtime names, and generated symbol names. Repeated external names require identical complete ABI signatures. Matching declarations produce one C prototype. An incompatible declaration reports `CT4102`. The diagnostic includes the earlier location.

Unknown attributes, invalid targets, duplicate attributes, and non-constant arguments are errors.

### Section placement

`[Section("name")]` controls the native object section of an eligible definition. The name is one ASCII string from 1 through 128 characters. Letters, digits, `.`, `_`, `$`, and `-` are permitted; the first character must be a letter, `.`, `_`, or `$`. Malformed arguments and names report `CT1286`.

The attribute is valid on a body-bearing static method of any accessibility and on a non-const static field whose declared type is complete and unmanaged. A sectioned field may be `readonly` or `volatile`; its native section remains writable because module initialization can assign it. Constructors, operators, properties, instance members, abstract or extern methods, const fields, and managed or incomplete field types report `CT1287`.

Several code definitions may share one section, and several data definitions may share one section. One name cannot be used for both code and data in the same compilation; that conflict reports `CT4107` with the earlier declaration location. Closed generic methods and closed generic-type static fields inherit the annotation from their source declaration.

For `[Export]`, the internal C~ implementation and external wrapper use the requested code section, and the exported native-header prototype carries the matching declaration annotation. For `[EntryPoint]`, only the C~ implementation is placed; generated `main` and `app_main` wrappers remain in their default sections. `[Section]` does not make a method or field reachable, does not change linkage or initialization order, and does not specify ordering, alignment, retention, linker-script mapping, memory regions, or final addresses.

### Native calls and callbacks

Extern methods and unmanaged function pointers cover direct calls from C~ to exact C symbols and signatures, including native-sized scalars, unmanaged `ref`/`in`/`out`, flattened native buffers, opaque handles, and scoped UTF-8 input. `[Borrowed]`, `[Consumes]`, `[Retained]`, `[Creates]`, and `[Nullable]` describe opaque or explicitly annotated pointer parameters. `[ReturnsOwned]`, `[ReturnsBorrowed]`, and `[ReturnsNullable]` describe results. A borrowed input is the default. `[Creates]` applies to `out`; ownership transfers only on normal return. `[Retained]` transfers an owned opaque value to native code.

`[Export("symbol")]` marks a public static body-bearing C~ method with a unique portable C name. Its signature is limited to ABI-safe scalars, enums, unmanaged structures, pointers, opaque handles, `EspError`, native buffers, by-reference parameters, and input `NativeUtf8String`. The generated wrapper requires an attached runtime thread, translates flattened arguments, and converts an escaping exception to fatal `CTE0003`. `EmitCHeader` and CLI `--header` produce its deterministic C/C++ declaration, reachable unmanaged layouts, and native runtime ownership and attachment declarations.

`[SynchronousCallback]` on an extern delegate parameter flattens the delegate to a C function pointer followed by `void*` context. The adapter retains the delegate until the extern call returns, preserves instance and virtual dispatch, and releases it afterward. Native code may invoke it on any explicitly attached thread, but every invocation must finish and all worker threads must join before the extern call returns. An unattached invocation panics with `CTT0001`. Callback exceptions run that thread's C~ cleanup and panic with `CTE0003`.

Ordinary parameters are borrowed for the duration of a call. `[Retained]` accepts no arguments and is valid only on a direct class, array, or string parameter of an extern method. The compiler retains that argument immediately before the call and transfers the additional ownership count to native code. Invalid uses report `CT1234`.

Managed-reference results are owned. `[ReturnsBorrowed]` accepts no arguments and is valid only on an extern method returning a direct class, array, or string reference. The compiler retains that native result immediately, converting it to the normal owned-result convention. Invalid uses report `CT1235`.

Delegates and unmanaged function pointers never convert implicitly to one another. Draft 0.18 supports static-method function-pointer trampolines and synchronous delegate/context adapters on any attached native thread. An exception escaping a callback runs that thread's C~ cleanup and panics with `CTE0003`; it never unwinds through native frames. Retained callbacks and ISR callbacks remain unsupported.

### NoAlloc

`[NoAlloc]` accepts no arguments. It can annotate a method, extern method, or property. A property contract applies to every accessor it declares. A virtual contract is inherited by overrides. Arguments report `CT1233`; a contract that may allocate reports `CT2155`.

The compiler rejects a contracted member when any reachable generated code can call `ct_alloc`. It infers body-bearing, statically dispatched helper effects to a fixed point, including recursive calls. An annotated extern is a trusted native assertion. An extern or virtual dispatch boundary without an effective `[NoAlloc]` contract is rejected from contracted code.

Allocating operations are class and array construction, boxing, delegate creation, nonconstant string concatenation, scalar, Boolean, and character `ToString()`, and calls whose inferred effects allocate. An unconstrained delegate invocation, unmanaged function-pointer call, extern call, or virtual dispatch is an unknown boundary unless an applicable contract proves otherwise. String literals, folded constant concatenation, `string.ToString()`, unboxing, casts, allocation-free structure construction, and exception/defer control state do not allocate. Diagnostics include a deterministic call-chain witness.

## Core library

The automatically imported `System` namespace provides `Object`, `Exception`, `Console`, `Environment`, single-precision `Math`, and the mutable `Vec2`, `Vec3`, and `Vec4` value types. Vector arithmetic is allocation-free: multiplication and division between vectors are componentwise, dot products are named methods, and normalizing a zero vector follows native IEEE division behavior. The exact API and runtime behavior are in [STDLIB.md](STDLIB.md).

Draft 0.18 does not provide `System.Type`, reflection, or `System.Convert`.

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

Draft 0.18 has no pattern cases and no `goto case`.

### Loops

`while`, `do while`, and `for` use `bool` conditions. An omitted `for` condition is true.

`foreach` iterates a one-dimensional array from index zero through `Length - 1` and copies each element into the iteration local.

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

Exception filters, inner exceptions, stack traces, user-defined runtime-fault policies, and automatic disposal are not part of draft 0.18. An exception that escapes a supported synchronous native boundary becomes a panic with `CTE0003`; general exception propagation across native boundaries is unsupported.

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

Inline assembly is an unknown allocation boundary. `[NoAlloc]` on the statement is a trusted assertion that the block cannot reach the managed allocator, equivalent to the trusted contract on an extern. An unasserted block makes an enclosing `[NoAlloc]` call graph invalid.

The feature is supported by GCC and Clang for hosted and ESP-IDF builds. The CLI rejects a hosted build that selects MSVC when the program contains `asm`. Emit-only output remains GNU C23.

## XML documentation

Three-slash comments immediately before a type, delegate, opaque type, field, property, constructor, method, or enum value attach XML documentation to that declaration. Attributes and modifiers can appear between the comment and the declaration. A blank line or ordinary comment breaks attachment. Unattached documentation reports warning `CT5006`; documentation warnings never prevent checking or C emission.

The supported elements are `summary`, `param`, `returns`, `remarks`, `exception` with `cref`, inline `see` with `cref`, inline `paramref` with `name`, and `inheritdoc`. References use the current namespace and imports and can select a member overload with a C~-style parameter list. XML DTDs and external entities are prohibited. Raw Markdown, raw HTML, block documentation comments, and documentation-file emission are not supported.

`inheritdoc` must be the only documentation element. It explicitly copies documentation from an overridden method or property, or from a base type. Override parameter descriptions are matched by ordinal so parameter names can differ. Documentation is never inherited automatically.

Malformed XML (`CT5000`), unsupported structure (`CT5001`), duplicate sections (`CT5002`), unknown parameters (`CT5003`), unresolved references (`CT5004`), and invalid or cyclic inheritance (`CT5005`) are warnings. The compiler does not warn when a declaration has no documentation.

## Managed concurrency

`System.Threading.Atomic<T>` provides load, store, exchange, compare-exchange, fetch-add, fetch-subtract, fetch-and, fetch-or, and fetch-xor. `T` can be Boolean, integral, native-integral, enum, or unsafe pointer. Pointer atomics support only load, store, exchange, and compare-exchange. Arithmetic fetch operations require an integral type; bitwise fetch operations require Boolean or integral storage. Operations return the value observed before modification.

`MemoryOrder` values are `Relaxed`, `Acquire`, `Release`, `AcquireRelease`, and `SequentiallyConsistent`. Loads reject release and acquire-release. Stores reject acquire and acquire-release. Compare-exchange failure order is relaxed, acquire, or sequentially consistent and cannot be stronger than its success order. Invalid dynamic orders throw the allocation-free `ArgumentException` singleton. `Atomic.Fence` emits a fence with the selected order.

An `Atomic<T>` value is non-copyable. It can be directly constructed as a local, static, or field. It cannot be assigned, returned by value, boxed, stored in an array or property, passed by value, or contained by a structure whose copies would duplicate it.

`System.Threading.Thread` starts one managed `ThreadStart` delegate on a new OS thread or FreeRTOS task. `Start` publishes captured state before invocation; `Join` supplies an acquire edge after completion. `Sleep` and `Yield` delegate to the target scheduler. `Id` is a stable C~ runtime ID. Explicit stack size and priority requests are validated by the target; a non-default priority that cannot be applied throws `ThreadStateException` instead of degrading silently.

A thread starts at most once. Joining before start, starting twice, or joining itself throws `ThreadStateException`. A worker retains its delegate and control object until completion, so releasing the source `Thread` reference does not cancel it. An exception escaping the delegate is fatal through the existing unhandled-exception path. Workers attach to independent C~ exception, cleanup, ARC-release, and debug state before invoking source code and detach after cleanup.

`System.Threading.Mutex` is process-local and recursive. `Enter`, `TryEnter`, and `Exit` map to `CRITICAL_SECTION`, recursive POSIX mutexes, or recursive FreeRTOS mutexes. Cancellation, interruption, affinity, naming, timed joins, source thread-local declarations, pools, futures, `Task`, and `async` are not part of Draft 0.18.

## Managed lifetime and failures

C~ source has no `delete` operator, destructors, user finalizers, or weak references. Draft 0.18 uses thread-safe, non-moving automatic reference counting for classes, arrays, strings, boxes, interface views, and references nested in naturally laid-out structures. Heap objects begin with one atomic owned reference and are reclaimed on the thread that releases the last owned reference. Dynamic strings and arrays each occupy one checked contiguous allocation. Static and empty strings are immortal. Static managed fields own their values until runtime shutdown, when they are dropped in exact reverse initialization order and cleared.

Parameters and `this` are borrowed. Managed-reference and reference-containing structure results are owned. Owning locals, fields, properties, array elements, temporaries, boxes, and structure copies retain or transfer their contents as required. Cleanup runs on normal block exit, return, break, continue, and C~ exception propagation. Reference cycles intentionally leak in draft 0.18.

Draft 0.18 uses a data-race-free memory model. Concurrent reads are allowed. Conflicting accesses to an ordinary location, when at least one access writes and no synchronization orders them, have undefined behavior. ARC atomics protect lifetime only: they neither publish object contents nor make reference slots, fields, array elements, or static fields atomic. A reference transferred between threads requires an owned count plus synchronization. Thread start, join, mutex operations, volatile fields, and atomics establish the documented happens-before edges. Correctly synchronized programs behave sequentially consistently; relaxed atomics provide atomicity without publication, and sequentially consistent atomics participate in one total order.

One C~ runtime exists per process. `ct_runtime_initialize(config)` attaches the calling primary thread, initializes immortal runtime-fault objects and the ABI-versioned module descriptor, then publishes the ready phase. `ct_runtime_shutdown()` requires all secondary threads detached, finalizes modules, drains ARC work, and detaches the primary thread. `main` and `app_main` perform this lifecycle automatically. A native-created thread uses `ct_thread_attach()` and `ct_thread_detach()` between those calls and must detach with no active C~ calls, cleanup records, exception frames, pending exception, or release drain. Export wrappers, callback trampolines, `ct_retain`, and `ct_release` require attachment. Runtime-phase misuse and unattached entry are panics. Modules cannot unload while their descriptors, vtables, delegates, objects, or generated function pointers remain live. Independent DLL loading and runtime registration are deferred.

`System.Runtime.Memory.Retain` and `Release` manipulate an additional untracked ownership count. `null` is a no-op. They are unsafe APIs: unbalanced use can leak, dangle, or double-release a value. Calling any unsafe method requires an unsafe method or block and otherwise reports `CT2139`.

External resources require explicit release. `defer Release(handle);` reserves an owned opaque handle's cleanup immediately, forbids reassignment or a second transfer, and still permits borrowed use until the block exits. Cleanup runs before ordinary lexical ownership teardown. There is no language `using` statement or automatic `Dispose` convention.

Managed null access, null unboxing, and `throw null` raise `NullReferenceException`; array and native-buffer bounds raise `IndexOutOfRangeException`; integer division or remainder by zero raises `DivideByZeroException`; invalid casts and mismatched unboxing raise `InvalidCastException`; negative or overflowing array, stack, and string sizes raise `OverflowException`; embedded NUL at a native UTF-8 boundary raises `ArgumentException`; and managed allocation failure after attachment raises `OutOfMemoryException`. These objects are immortal and preinitialized, so raising them allocates nothing and remains valid inside `[NoAlloc]`. The original runtime code and source location are per-thread exception-origin metadata and survive calls, cleanup, and rethrow.

Runtime-phase misuse, unattached entry, reference-count corruption, cleanup corruption, ABI mismatch, pre-attachment allocation failure, and exceptions escaping callbacks or exports are panics. A configured native panic callback runs first; returning invokes the platform's default fatal termination.

An unhandled exception prints `CTE0001`, its fully qualified runtime type, and its non-empty message. It then exits with `EXIT_FAILURE`.

## Compile-time and native-system facilities

`System.Runtime.Target.Profile`, `Architecture`, and `PointerSize` are compiler constants and have no runtime storage. The architecture is selected through `CompilationOptions.Architecture`, top-level `architecture` in `ctilde.json`, or CLI `--architecture`; CLI input has precedence. The supported values are `auto`, `x86`, `x64`, `arm32`, `arm64`, `xtensa`, `riscv32`, and `riscv64`. An architecture-dependent query with no resolved architecture reports `CT4108`.

`static if (condition) statement else statement` is statement-level compile-time selection. Its condition must be a compile-time Boolean. Only the selected branch is bound, analyzed, lowered, and made reachable. The inactive branch must parse but can name APIs unavailable on the selected target.

`static assert(condition);` and `static assert(condition, "message");` are valid at file/namespace and type-member scope. A known false condition reports `CT2201`; a non-Boolean or non-constant condition reports `CT2200`. Layout-dependent assertions over `sizeof`, `alignof`, and `offsetof` emit deterministic C `static_assert` declarations after their required layouts. A closed generic type evaluates its assertions for each emitted specialization.

`[Used]` accepts no arguments and applies to body-bearing static methods and owned, non-const, complete unmanaged static fields. It is an object-emission reachability root. It propagates to otherwise-created closed generic specializations and is recorded in the symbol map. It does not guarantee retention by the final linker. Invalid targets report `CT1288`.

`[Extern("symbol")]` can declare a static data field with no initializer and a complete unmanaged non-generic type. Every access, reference, or address operation requires unsafe context. `readonly` emits native `const`; `[NativeVolatile]` emits native `volatile`; C~ `volatile`, `[Section]`, and `[Used]` are forbidden on extern data. Invalid declarations report `CT1289`; invalid native volatility reports `CT1290`.

`System.Runtime.Mmio` provides `Read<T>`, `Write<T>`, `ReadRelaxed<T>`, `WriteRelaxed<T>`, and `Barrier`. `T` must be a fixed-width integer, `char`, or an enum with a fixed-width integer underlying type. Each read or write emits exactly one naturally aligned volatile native access. Ordered accesses execute a full target I/O barrier before and after; relaxed accesses emit only the access. Invalid element types and known misalignment report `CT2203`; an unsupported ordered target reports `CT4109`.

On ESP-IDF, `[TaskEntry(StackSize = N)]` combines with `[Export("symbol")]` on a public static non-generic `void(void*)` method body. `N` is a positive `uint` divisible by four. The exported wrapper attaches fresh C~ task state, installs the export exception barrier, calls the implementation, detaches on normal completion, calls `vTaskDelete(NULL)`, and never returns. The public header defines `CTILDE_TASK_STACK_<EXPORT>` with the configured value. Invalid metadata reports `CT1291`; invalid targets or signatures report `CT1292`.

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

A compiler conforms to draft 0.18 when:

1. It implements every non-deferred rule in this document.
2. Invalid programs produce structured diagnostics and no C.
3. Repeated compilation produces byte-identical C.
4. Generated C compiles as GNU C23 without warnings.
5. Native execution passes the language and runtime conformance suite.

The canonical backend is GNU C23. Draft 0.18 has no second backend. Unity and modular layouts consume the same optimized whole-program IR and must have equivalent behavior.

## Deliberate differences from C#

- `char` is one UTF-8 code unit, not UTF-16.
- References, pointers, `nint`, and `nuint` use the target C ABI width.
- Signed integer overflow is always wrapping.
- `readonly` locals permit one delayed assignment.
- Managed ownership uses deterministic ARC; cycles leak, and `[NoAlloc]` is the compile-time allocation boundary.
- The core library is intentionally small.

Draft 0.18 defers declaration-level conditional compilation, final-image retention, weak symbols, linker declarations, register and bitfield abstractions, atomic MMIO read-modify-write, default and explicit interface implementations, generic variance, user-defined conversions, managed-reference and floating-point atomics, weak references, cycle collection, lambdas and closures, retained callbacks, ISR entry, owned resource fields, iterators, multidimensional arrays, string interpolation, general native-boundary unwinding, dynamic DLL loading, and runtime module registration.
