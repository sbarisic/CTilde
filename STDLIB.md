# C~ standard library

## Status

This document is the canonical standard-library reference for C~ draft 0.24 and runtime ABI 16. Hosted, Cosmopolitan, and ESP-IDF retain the object, exception, console, math, vector, concurrency, target, MMIO, CPU, endian, and address facilities appropriate to each profile. Freestanding exposes only the object/storage core, primitive and managed storage metadata, `Memory`, target queries, MMIO, CPU, endian helpers, inline arrays, and newtypes. It does not load exceptions, console, environment/process, managed threading, ESP-IDF APIs, hosted I/O, or libm-backed math.

The Cosmopolitan profile reuses the hosted object, exception, console, environment, math, file-I/O, threading, and TLS surface through Cosmopolitan's portable POSIX facade. It does not expose ESP-IDF, MMIO/register, freestanding runtime-role, or dynamic-library APIs. Draft 0.24 has measured managed strings/arrays, ARC, exceptions, `defer`, console, file output, threads, mutexes, initialization, and shutdown on WSL/Linux and Windows.

`System.Runtime.Target` exposes compiler constants for `Profile`, `Architecture`, and byte-sized `PointerSize`. `System.Runtime.Mmio` provides exact-width `Read`, `Write`, `ReadRelaxed`, `WriteRelaxed`, and `Barrier` intrinsics for fixed-width integers and enums. Ordered accesses use a full target I/O barrier before and after the volatile access; relaxed accesses emit only the access.

`System.Runtime.Cpu` provides allocation-free ordinary-memory barriers, pause hints, byte swaps, population counts, and leading-zero counts. `System.Endian` converts `ushort` and `uint` values to and from the nominal `be16`, `be32`, `le16`, and `le32` wire-order types. `PhysicalAddress`, `VirtualAddress`, and `IoAddress` are strict `nuint` newtypes; conversion between address domains requires an explicit conversion through `nuint`.

Draft 0.24 marks target queries, CPU operations, MMIO, and endian conversion with trusted `[NoRuntime]` and `[NoBlock]` contracts in addition to `[NoThrow]` and `[NoAlloc]`. Atomic operations are non-blocking, but dynamic memory-order validation can throw and use the managed runtime. `Thread.Join`, nonzero or dynamic `Thread.Sleep`, and `Mutex.Enter` are blocking; `TryEnter`, `Yield`, and CPU pause hints are not. Console, file, and unannotated native I/O remain conservative effect boundaries.

`[Interrupt]` and `[InterruptSafe]` are compiler-defined attributes rather than ordinary runtime APIs. On ESP-IDF, an interrupt entry has the exact exported `void(void*)` signature and runs without runtime attachment, managed cleanup, exception machinery, or blocking calls. Interrupt-safe externs and assembly must also declare their ordinary effect contracts independently.

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

The standard library also declares `NullReferenceException`, `IndexOutOfRangeException`, `DivideByZeroException`, `InvalidCastException`, `OverflowException`, `ArgumentException`, `OutOfMemoryException`, `ThreadStateException`, and `SynchronizationLockException`. The runtime preinitializes one immortal object of each type during `ct_runtime_initialize`. Managed runtime checks throw these singletons without allocating, including inside strict `[NoAlloc]` call paths. Their diagnostic code and source location are per-thread origin metadata rather than mutable fields on the shared object.

## Console

`System.Console` provides standard-output operations:

```csharp
public static class Console
{
    // Hosted only.
    public static int Read();
    public static string ReadLine();

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

On hosted targets, `Read()` returns the next input byte as `0..255` or `-1` at EOF. `ReadLine()` flushes standard output, reads one UTF-8 line, removes LF and one preceding CR, and returns an owned string. It returns `null` only when EOF occurs before any byte. Invalid UTF-8, input errors, and lines beyond the managed-string length limit throw `System.IO.IOException`. These input methods can allocate and are unavailable to `[NoAlloc]` call paths and ESP-IDF.

## Math

`System.Math` provides allocation-free single-precision functions on every target:

```csharp
public static class Math
{
    public const float Pi = 3.14159265358979323846f;

    [NoAlloc] public static float Sqrt(float value);
    [NoAlloc] public static float Abs(float value);
    [NoAlloc] public static float Tan(float value);
    [NoAlloc] public static float Min(float left, float right);
    [NoAlloc] public static float Max(float left, float right);
    [NoAlloc] public static float Sin(float value);
    [NoAlloc] public static float Cos(float value);
    [NoAlloc] public static float Floor(float value);
    [NoAlloc] public static float Ceiling(float value);
}
```

`Pi` is the nearest representable C~ `float` to pi. Angles use radians. Functions map to the target C library's `sqrtf`, `fabsf`, `tanf`, `fminf`, `fmaxf`, `sinf`, `cosf`, `floorf`, and `ceilf` operations. Their NaN, infinity, signed-zero, rounding, and domain behavior follows that implementation. In particular, `Min` and `Max` return the numeric operand when exactly one operand is NaN, as specified for C `fminf` and `fmaxf`. C~ does not expose `errno` or floating-point exception state, and these functions do not throw C~ exceptions. The C~ native-build driver links `libm` on Unix and WSL; manual GNU links of math-using generated C must place `-lm` after the generated translation unit.

## Vectors

`System.Vec2`, `System.Vec3`, and `System.Vec4` are mutable single-precision value types available on every target. Their component fields are named `X`, `Y`, `Z`, and `W` as applicable. Each type provides zero, scalar-splat, and component constructors; `Zero`, `One`, and axis-unit properties; componentwise arithmetic; scalar scaling; dot product; length; squared length; and normalization. `Vec3` also provides a right-handed cross product.

The `Vec3` surface is representative:

```csharp
public struct Vec3
{
    public float X;
    public float Y;
    public float Z;

    public Vec3();
    public Vec3(float value);
    public Vec3(float x, float y, float z);

    [NoAlloc] public static Vec3 Zero { get; }
    [NoAlloc] public static Vec3 One { get; }
    [NoAlloc] public static Vec3 UnitX { get; }
    [NoAlloc] public static Vec3 UnitY { get; }
    [NoAlloc] public static Vec3 UnitZ { get; }

    [NoAlloc] public static Vec3 operator +(Vec3 value);
    [NoAlloc] public static Vec3 operator -(Vec3 value);
    [NoAlloc] public static Vec3 operator +(Vec3 left, Vec3 right);
    [NoAlloc] public static Vec3 operator -(Vec3 left, Vec3 right);
    [NoAlloc] public static Vec3 operator *(Vec3 left, Vec3 right);
    [NoAlloc] public static Vec3 operator /(Vec3 left, Vec3 right);
    [NoAlloc] public static Vec3 operator *(Vec3 value, float scale);
    [NoAlloc] public static Vec3 operator *(float scale, Vec3 value);
    [NoAlloc] public static Vec3 operator /(Vec3 value, float scale);

    [NoAlloc] public float Dot(Vec3 other);
    [NoAlloc] public Vec3 Cross(Vec3 other);
    [NoAlloc] public float LengthSquared();
    [NoAlloc] public float Length();
    [NoAlloc] public Vec3 Normalize();
}
```

`Vec2` provides `UnitX` and `UnitY`; `Vec4` additionally provides `UnitZ` and `UnitW`. Vector-vector multiplication and division operate component by component. Dot products remain explicit. Normalization divides by the native square-root result without a special zero check, so normalizing a zero vector produces NaN components according to target floating-point behavior. Vector declarations are loaded into compilation only when the corresponding exact type name appears in source; editor services load all three for completion and embedded-source navigation.

## Threading

`System.Threading` is available on hosted and ESP-IDF targets:

```csharp
public enum MemoryOrder { Relaxed, Acquire, Release, AcquireRelease, SequentiallyConsistent }

public struct Atomic<T>
{
    public Atomic(T value);
    [NoAlloc] public T Load(MemoryOrder order);
    [NoAlloc] public void Store(T value, MemoryOrder order);
    [NoAlloc] public T Exchange(T value, MemoryOrder order);
    [NoAlloc] public T CompareExchange(T value, T comparand, MemoryOrder successOrder, MemoryOrder failureOrder);
    [NoAlloc] public T FetchAdd(T value, MemoryOrder order);
    [NoAlloc] public T FetchSubtract(T value, MemoryOrder order);
    [NoAlloc] public T FetchAnd(T value, MemoryOrder order);
    [NoAlloc] public T FetchOr(T value, MemoryOrder order);
    [NoAlloc] public T FetchXor(T value, MemoryOrder order);
}

public static class Atomic { [NoAlloc] public static void Fence(MemoryOrder order); }
public delegate void ThreadStart();
public enum ThreadPriority { Lowest, BelowNormal, Normal, AboveNormal, Highest }

public sealed class Thread
{
    public Thread(ThreadStart start);
    public Thread(ThreadStart start, uint stackSizeBytes);
    public Thread(ThreadStart start, uint stackSizeBytes, ThreadPriority priority);
    public bool IsAlive { [NoAlloc] get; }
    public uint Id { [NoAlloc] get; }
    public ThreadPriority Priority { [NoAlloc] get; }
    [NoAlloc] public void Start();
    [NoAlloc] public void Join();
    [NoAlloc] public static void Sleep(uint milliseconds);
    [NoAlloc] public static void Yield();
}

public sealed class Mutex
{
    public Mutex();
    [NoAlloc] public void Enter();
    [NoAlloc] public bool TryEnter();
    [NoAlloc] public void Exit();
}
```

Atomics accept Boolean, integral, native-integral, enum, and unsafe-pointer storage. They are non-copyable. Pointer atomics omit fetch operations; arithmetic fetches require integral storage and bitwise fetches require Boolean or integral storage. Invalid dynamic memory orders throw `ArgumentException` without managed allocation.

Threads run on `_beginthreadex`, POSIX threads, or FreeRTOS tasks. `Start` publishes delegate state, `Join` acquires worker completion, and non-default priority failures are explicit. The runtime retains the worker state through completion. Mutexes are recursive and provide acquire/release ordering. Prefer `lock (mutex) { ... }` when lexical cleanup is possible.

## Hosted file I/O

The hosted target adds synchronous binary-file operations:

```csharp
namespace System.IO;

public enum FileMode : byte { Open, Create, Append }
public enum FileAccess : byte { Read, Write, ReadWrite }

[NativeType("uintptr_t", "stdint.h")]
public opaque FileHandle;

public class IOException : Exception
{
    public IOException(string message);
    public IOException(string message, int errorCode);
    public int ErrorCode { get; }
}

public static class File
{
    [ReturnsOwned]
    public static FileHandle Open(string path, FileMode mode, FileAccess access);
    public static unsafe nuint Read([Borrowed] FileHandle file, NativeBuffer<byte> destination);
    public static unsafe void Write([Borrowed] FileHandle file, ReadOnlyNativeBuffer<byte> source);
    public static void Write([Borrowed] FileHandle file, string value);
    public static void Close([Consumes] FileHandle file);
}
```

`Open` accepts UTF-8 paths, rejects embedded NUL bytes, and either returns a non-null owned handle or throws `IOException`. Windows converts paths to UTF-16 and opens them through the wide CRT API; POSIX hosts pass validated UTF-8 bytes to `fopen`. Files always use binary mode.

`Open` supports `Open` with every access, `Create` with `Write` or `ReadWrite`, and `Append` with `Write`. Other combinations throw with `EINVAL`. `Open` never truncates, `Create` creates or truncates, and `Append` creates or writes at the end.

`Read` copies at most the destination length and returns a `nuint` count. Zero from a nonempty destination means EOF. Buffer and string writes complete fully or throw; string writes emit exact UTF-8 bytes without a newline. `Close` flushes, closes, frees native handle storage, and consumes ownership even when the native close reports an error.

Every successful `Open` must transfer, return, consume, or reserve its handle. The normal pattern is:

```csharp
FileHandle file = File.Open(path, FileMode.Open, FileAccess.Read);
defer File.Close(file);
```

`IOException.ErrorCode` is host-dependent. Concurrent access to one handle requires external native synchronization. Seeking, directories, metadata, deletion, streams, automatic disposal, file locking, and asynchronous I/O are not part of this subset.

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

`System.Runtime.Memory` exposes two unsafe production interop operations:

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

Hosted and ESP-IDF conformance builds compiled with `CTILDE_CONFORMANCE` also expose `Memory.TestFailAllocationAfter(int successfulAllocations)`. It injects managed allocation failure for runtime tests and is not available in production or the freestanding library.

### Freestanding runtime roles

Freestanding adds these compiler-recognized declarations:

```csharp
public enum Runtime { Allocate, Free, Panic }

public readonly struct RuntimePanicInfo
{
    public unsafe readonly byte* Code;
    public unsafe readonly byte* File;
    public readonly int Line;
}
```

`[RuntimeImpl(Runtime.Allocate)]`, `[RuntimeImpl(Runtime.Free)]`, and `[RuntimeImpl(Runtime.Panic)]` select the user implementations of managed allocation, deallocation, and terminal failure. They are not ordinary library dispatch APIs and have no runtime storage. Panic is required for reachable ordinary freestanding code; allocation and free are required only when the reachable program needs the managed heap. The exact signatures, bootstrap-safe closure, and allocator contract are normative in [LANGUAGE.md](LANGUAGE.md).

## Runtime native buffers

`System.Runtime.NativeBuffer<T>` and `ReadOnlyNativeBuffer<T>` are compiler-intrinsic stack-only views. They are available to every target and retain stricter escape and unmanaged-element rules than ordinary Draft 0.24 generics.

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

Numeric, Boolean, and character conversions allocate immutable strings in one contiguous object-and-data allocation. The terminating zero byte is not included in `Length`; converting the zero `char` still produces a string with `Length == 1`.

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

The GNU C23 program defines one runtime. Unity output embeds it once; modular sources import the shared implementation. Managed allocations use atomic automatic reference counting and are reclaimed on the thread that releases the last owned reference. Each attached thread has independent exception, cleanup, origin-diagnostic, and iterative-release state. Static managed fields live until reverse module finalization, static strings and fault objects are immortal, and reference cycles leak. Panics, `Environment.Exit`, abort, reset, and power loss do not promise ARC or defer cleanup.

Invalid casts and type-mismatched unboxing throw `InvalidCastException` with origin codes `CTO0001` and `CTO0003`. Null unboxing throws `NullReferenceException` with `CTO0002`.

An unhandled exception reports `CTE0001`, its fully qualified runtime type, and its origin. Throwing a null exception reference throws `NullReferenceException` with `CTE0002`. An exception escaping a supported synchronous unmanaged callback panics with `CTE0003`. Hosted fatal termination exits with `EXIT_FAILURE`; ESP-IDF calls `abort()` after writing the diagnostic.

Null, bounds, divide-by-zero, cast/unbox, size-overflow, embedded-NUL, and attached managed-OOM conditions are catchable through the built-in allocation-free exceptions. Hosted console and file failures continue to create catchable `System.IO.IOException` values. Unattached native entry (`CTT0001`), invalid lifecycle (`CTT0002`), ABI mismatch, ARC corruption, cleanup corruption, and native-boundary exception escape remain panics. Runtime and thread lifecycle are native ABI operations and intentionally have no C~ standard-library wrapper.

Standard-library declarations use native `[Extern]` bindings internally. Known C~-heap-free console, process, object, and ESP-IDF shims also carry `[NoAlloc]`; allocation-producing configuration and formatting paths remain uncontracted. `[NoAlloc]` on any extern is a trusted native contract, not an inspection of its implementation. Those symbol names are an implementation detail; user native interop remains governed by [C_ABI.md](C_ABI.md).

## Non-normative roadmap

The initial Cosmopolitan x64 audit has passed one portable managed-runtime APE on Linux/WSL and Windows. Broader math, environment, exports/callbacks, Unicode-path, custom-section, and final-retention cases remain explicit acceptance work. Arm64 and fat-image claims remain later gates; see [COSMOPOLITAN.md](COSMOPOLITAN.md).

Future library work can add `System.Convert`, parsing, richer strings, collections, higher-level streams and text files, directories, clocks, and date/time APIs.

Project binding manifests can add generated source-compatible ESP-IDF APIs alongside this handwritten surface. Their tracked C~ declarations use ordinary extern and ownership contracts, while project-private adapters consume the installed public headers, native constants, validated initializer macros, nested configuration fields, bounded fixed UTF-8 arrays, and selected output structures. Generated APIs are project declarations, not additions to the embedded standard library. `[NoAlloc]` describes only C~-heap behavior; a generated ESP-IDF call may allocate native memory. Long-lived owned-resource fields and retained callback lifetime rules remain deferred. Generated bindings do not infer `[InterruptSafe]`.
