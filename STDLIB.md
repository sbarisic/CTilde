# C~ standard library

## Status

This document is the canonical standard-library reference for C~ draft 0.9. Object, exception, console, and runtime memory declarations are available to every target. ESP declarations are loaded only for the ESP-IDF target.

All public `System`, compiler-intrinsic, and `Esp.Idf` APIs have embedded XML documentation. The compiler loads these sidecars into the same immutable documentation index as source `///` comments. Keeping descriptions outside the built-in `.ct` files preserves their virtual source locations and generated source-line metadata. ESP descriptions are available only when the compilation target is `esp-idf`.

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

## Exception

`System.Exception` is the root type for values that C~ code can throw and catch.

```csharp
public class Exception
{
    public Exception();
    public Exception(string message);
    public string Message { get; }
    public override string ToString();
}
```

The parameterless constructor uses an empty message. The string constructor also converts a null message to an empty string.

`ToString()` returns the fully qualified runtime type name. It appends `": "` and `Message` when the message is not empty. Derived exception classes inherit this behavior, so the result uses the derived runtime type name.

## Console

`System.Console` provides standard-output operations:

```csharp
public static class Console
{
    public static void Write(string value);
    public static void Write(char value);
    public static void Write(int value);
    public static void Write(uint value);
    public static void Write(long value);
    public static void Write(ulong value);
    public static void Write(nint value);
    public static void Write(nuint value);
    public static void Write(float value);
    public static void Write(bool value);
    public static void Write(object value);

    public static void WriteLine();
    public static void WriteLine(string value);
    public static void WriteLine(char value);
    public static void WriteLine(int value);
    public static void WriteLine(uint value);
    public static void WriteLine(long value);
    public static void WriteLine(ulong value);
    public static void WriteLine(nint value);
    public static void WriteLine(nuint value);
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

`Exit` terminates the process immediately with the supplied native exit code. It does not run pending finally blocks or defers.

`Environment.Exit` is hosted-only. An ESP-IDF compilation that calls it reports `CT4105`. Firmware that intentionally needs a reset must call `Esp.Idf.EspSystem.Restart`.

## Runtime memory

`System.Runtime.Memory` exposes two unsafe interop operations:

```csharp
public static class Memory
{
    [NoAlloc]
    public static unsafe void Retain(object value);

    [NoAlloc]
    public static unsafe void Release(object value);
}
```

`Retain` and `Release` manipulate an additional untracked ARC ownership count. `null` is a no-op. These methods require an unsafe method or block. Unbalanced calls can leak memory, create dangling references, or double-release an object. Normal C~ code does not need them.

## Runtime native buffers

`System.Runtime.NativeBuffer<T>` and `ReadOnlyNativeBuffer<T>` are compiler-intrinsic stack-only views. They are available to every target but do not enable user-defined generic types.

```csharp
NativeBuffer<byte> writable = new NativeBuffer<byte>(pointer, length);
ReadOnlyNativeBuffer<byte> readable = writable;

nuint count = readable.Length;
byte* address = writable.Pointer;
byte value = readable[0];
writable[0] = value;
```

Construction, pointer access, and `stackalloc` use require an unsafe context. Elements must be complete unmanaged types. Indexing is bounds-checked and uses `nuint`; failures report `CTB0001`. Negative runtime `int` stack counts report `CTB0002`, and count-by-element-size overflow reports `CTB0003`. Zero length is represented by a null pointer and zero count.

Views can be local values and synchronous value parameters. They cannot be stored in managed state, boxed, returned, or passed by `ref`, `in`, or `out`. Native ABI parameters flatten to a data pointer followed by `size_t` length; read-only views use a `const` data pointer.

## Scoped native UTF-8 strings

```csharp
public readonly struct NativeUtf8String
{
    public static NativeUtf8String Borrow(string value);
    public static NativeUtf8String Null { get; }
    public nuint ByteLength { get; }
    public unsafe byte* Pointer { get; }
}
```

`Borrow` retains the managed string for the view's lexical lifetime and does not allocate. It rejects null and embedded NUL bytes; dynamic embedded NUL reports `CTS0003`. Native boundaries receive `const char*`. The view is stack-only and cannot be stored, boxed, returned, or retained. `Null` requires `[Nullable]` at the receiving native parameter.

## ESP-IDF

The ESP-IDF target adds fixed-width wrappers around FreeRTOS, system, heap, GPIO, and WS2812 operations:

```csharp
namespace Esp.Idf;

public static class FreeRtos
{
    public static void DelayMilliseconds(uint milliseconds);
    public static uint GetTickCount();
    public static uint GetStackHighWaterMark();
}

public static class EspSystem
{
    public static void Restart();
    public static uint GetFreeHeapSize();
    public static uint GetMinimumFreeHeapSize();
}

public static class EspTimer
{
    [NoAlloc]
    public static long GetTimeMicroseconds();
}

public static class Gpio
{
    public static EspError ConfigureInput(int pin);
    public static EspError ConfigureOutput(int pin);
    public static EspError Write(int pin, bool high);
    public static bool Read(int pin);
}

public static class Ws2812
{
    public static EspError Configure(int pin, uint ledCount);
    public static EspError SetPixel(uint index, uint red, uint green, uint blue);
    public static EspError Refresh();
    public static EspError Clear();
}

public readonly struct EspError
{
    public int Code { get; }
    public bool IsSuccess { get; }
    public string GetName();
    public void ThrowIfError();
}
```

Positive delays yield the current FreeRTOS task and wait at least one tick. The stack high-water mark is the minimum free stack space in bytes. `EspTimer.GetTimeMicroseconds()` returns the signed 64-bit monotonic time since boot through `esp_timer_get_time()` and does not allocate. GPIO configuration and writes preserve the exact `esp_err_t` code in `EspError`. `Read` remains Boolean data and requires a valid pin that the program configured first.

`Ws2812` owns one firmware-lifetime RMT strip. The first successful `Configure` fixes its output pin and positive LED count; the same configuration is idempotent, while a conflicting configuration returns an error. `SetPixel` accepts indexes below that count and RGB components from 0 through 255, updates the native pixel buffer, and requires `Refresh` to transmit it. `Clear` turns off every pixel immediately.

`EspError.IsSuccess` tests for `ESP_OK`. `GetName()` copies `esp_err_to_name()` into an owned C~ string. `ThrowIfError()` throws `System.Exception` containing the symbolic name and numeric code and is not allowed in `[NoAlloc]` code.

These APIs are synchronous and are intended for the C~ entry task. They do not define callback, multi-task, or interrupt-safe C~ execution.

## Scalar ToString

The following built-in values provide an intrinsic, zero-argument `ToString()` method:

| Receiver | Result |
| --- | --- |
| `byte`, `ushort`, `uint`, `ulong`, `nuint` | Unsigned decimal text |
| `sbyte`, `short`, `int`, `long`, `nint` | Signed decimal text |
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

The GNU C23 runtime is part of each generated translation unit. Managed allocations use single-threaded automatic reference counting and are reclaimed when the last owned reference is released. Static managed fields live until termination, static strings are immortal, and reference cycles leak. Fatal failures, `Environment.Exit`, abort, reset, and power loss do not promise ARC or defer cleanup.

Invalid casts report `CTO0001`. Null unboxing reports `CTO0002`. Type-mismatched unboxing reports `CTO0003`.

An unhandled exception reports `CTE0001`, its fully qualified runtime type, and its non-empty message. Throwing a null exception reference reports `CTE0002`. An exception escaping a supported synchronous unmanaged callback reports fatal `CTE0003`. Hosted failures exit with `EXIT_FAILURE`; ESP-IDF failures call `abort()` after writing the diagnostic.

Other runtime failures remain fatal and are not catchable in draft 0.9. Same-task native entry failures report `CTT0001`; dynamic embedded NUL reports `CTS0003`.

Standard-library declarations use native `[Extern]` bindings internally. Known C~-heap-free console, process, object, and ESP-IDF shims also carry `[NoAlloc]`; allocation-producing configuration and formatting paths remain uncontracted. `[NoAlloc]` on any extern is a trusted native contract, not an inspection of its implementation. Those symbol names are an implementation detail; user native interop remains governed by [C_ABI.md](C_ABI.md).

## Non-normative roadmap

Future library work can add `System.Math`, `System.Convert`, parsing, richer strings, collections, file and stream I/O, clocks, and date/time APIs.

ESP-IDF interop can next add generated source-compatible bindings, long-lived owned-resource fields, retained callback lifetime rules, public task attachment, and compiler-checked ISR profiles. Generated adapters should consume public ESP-IDF headers and default configuration macros rather than exposing native configuration-structure layouts directly.
