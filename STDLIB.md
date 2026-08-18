# C~ standard library

## Status

This document is the canonical standard-library reference for C~ draft 0.4. The compiler includes this library in every compilation.

## Object

`object` is an alias for `System.Object`. Every class derives from this type.

```csharp
public class Object
{
    public Object();
    public virtual string ToString();
    public virtual bool Equals(object value);
    public virtual int GetHashCode();
    public static bool Equals(object left, object right);
    public static bool ReferenceEquals(object left, object right);
}
```

The default `ToString` result is the fully qualified runtime type name. A class or structure can override the method.

The default instance `Equals` compares reference identity. Strings and boxed values provide value equality.

Static `Equals` handles null values and then calls the virtual instance method. `ReferenceEquals` only compares managed identity.

`GetHashCode` is stable during one process. C~ does not require the same hash across separate executions.

Boxing creates a new managed object. Unboxing requires the exact boxed type. Pointer boxing and unboxing require an unsafe context.

The `System` namespace is imported automatically. Writing `using System;` is allowed but is not required.

## Console

`System.Console` provides standard-output operations:

```csharp
public static class Console
{
    public static void Write(string value);
    public static void Write(char value);
    public static void Write(int value);
    public static void Write(uint value);
    public static void Write(float value);
    public static void Write(bool value);
    public static void Write(object value);

    public static void WriteLine();
    public static void WriteLine(string value);
    public static void WriteLine(char value);
    public static void WriteLine(int value);
    public static void WriteLine(uint value);
    public static void WriteLine(float value);
    public static void WriteLine(bool value);
    public static void WriteLine(object value);
}
```

Smaller integer types use the language overload rules. A signed widening target is better when other rules do not decide. Strings write their exact UTF-8 bytes. A null string writes no bytes. `char` writes one UTF-8 code unit. Booleans write `True` or `False`. Floats use nine significant digits.

`WriteLine(value)` writes the value followed by one newline byte. Parameterless `WriteLine()` writes only the newline.

## Environment

`System.Environment` provides process control:

```csharp
public static class Environment
{
    public static void Exit(int code);
}
```

`Exit` terminates the process immediately with the supplied native exit code.

## Scalar ToString

The following built-in values provide an intrinsic, zero-argument `ToString()` method:

| Receiver | Result |
| --- | --- |
| `byte`, `ushort`, `uint` | Unsigned decimal text |
| `sbyte`, `short`, `int` | Signed decimal text |
| `float` | Nine-significant-digit binary32 text |
| `bool` | `True` or `False` |
| `char` | A one-code-unit string |
| `string` | The same string reference |

```csharp
int value = 42;
string text = value.ToString();
Console.WriteLine("value: " + text);
```

Numeric, Boolean, and character conversions allocate immutable strings. Their descriptors and null-terminated UTF-8 data live until process exit. The terminating zero byte is not included in `Length`; converting the zero `char` still produces a string with `Length == 1`.

`string.ToString()` does not allocate. It checks the receiver for null and returns the same reference.

Classes and arrays inherit the object methods. Enums format the first declared matching name or their underlying numeric value.

Structures receive the object methods through boxing. A structure can override `ToString`, `Equals(object)`, and `GetHashCode`.

## Strings and arrays

Strings and arrays expose language-provided members in addition to the library APIs:

- `string.Length` and read-only `string[index]` access, measured in UTF-8 code units.
- String concatenation with `+` and content equality with `==` and `!=`.
- `array.Length`, checked indexing, allocation, and `foreach` iteration.

These operations are compiler intrinsics rather than declarations in the bundled C~ sources.

## Runtime behavior

The GNU C23 runtime is part of each generated translation unit. Managed allocations live until process exit.

Invalid casts report `CTO0001`. Null unboxing reports `CTO0002`. Type-mismatched unboxing reports `CTO0003`.

Standard-library declarations use native `[Extern]` bindings internally. Those symbol names are an implementation detail; user native interop remains governed by [C_ABI.md](C_ABI.md).

## Non-normative roadmap

Future library work can add `System.Math`, `System.Convert`, parsing, richer strings, collections, file and stream I/O, clocks, and date/time APIs.
