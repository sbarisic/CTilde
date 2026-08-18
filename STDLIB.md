# C~ standard library

## Status

This document is the canonical reference for the standard library bundled with C~ draft 0.3. The library is versioned with the compiler and is included in every compilation. There is currently no option to replace or disable it.

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

    public static void WriteLine();
    public static void WriteLine(string value);
    public static void WriteLine(char value);
    public static void WriteLine(int value);
    public static void WriteLine(uint value);
    public static void WriteLine(float value);
    public static void WriteLine(bool value);
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

`string.ToString()` does not allocate. It checks the receiver for null and returns the same reference. Supplying arguments to any scalar `ToString` is a compile-time overload error (`CT2122`). Classes, structures, enums, arrays, and pointers do not inherit or receive this method.

## Strings and arrays

Strings and arrays expose language-provided members in addition to the library APIs:

- `string.Length` and read-only `string[index]` access, measured in UTF-8 code units.
- String concatenation with `+` and content equality with `==` and `!=`.
- `array.Length`, checked indexing, allocation, and `foreach` iteration.

These operations are compiler intrinsics rather than declarations in the bundled C~ sources.

## Runtime behavior

The standard library is backed by a small GNU C23 runtime embedded in each generated translation unit. Managed strings created by conversions use program-lifetime allocation. Allocation failure reports `CTM0001`, null `string.ToString()` reports `CTN0001`, and an unexpected native formatting failure reports `CTS0002`; runtime failures write to standard error and terminate with `EXIT_FAILURE`.

Standard-library declarations use native `[Extern]` bindings internally. Those symbol names are an implementation detail; user native interop remains governed by [C_ABI.md](C_ABI.md).

## Non-normative roadmap

Future library work may add `System.Object`, inheritance and boxing, enum formatting, `System.Math`, `System.Convert`, parsing, richer string operations, collections, file and stream I/O, clocks, and date/time APIs. None of those APIs are available in draft 0.3.
