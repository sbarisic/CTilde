# C~ standard library

## Status

This document is the canonical standard-library reference for C~ Draft 0.45 and runtime ABI 18. Draft 0.45 adds the ESP-IDF managed-process surface and Managed Module ABI 1. Draft 0.43 remains the runtime-service-provider revision that made the common standard library available to freestanding and ESP-IDF projects. ESP-IDF keeps its built-in platform adapters unless a complete service group is overridden. Debug metadata remains version 3.

The physical sources are also a first-class project at `CTilde/StandardLibrary/ctilde.json`, wrapped by `CTilde.StandardLibrary.ctproj` in the focused `CTilde.StandardLibrary.sln`. Its `kind` is `standard-library`: Check and Build validate hosted baseline/full, Cosmopolitan full, ESP-IDF full, and freestanding baseline/full compositions without requiring an application entry point or emitting a binary. Clean is a no-op and Run is unavailable.

The Cosmopolitan profile reuses the hosted object, exception, console, environment, math, file-I/O, threading, and TLS surface through Cosmopolitan's portable POSIX facade. It does not expose ESP-IDF, MMIO/register, freestanding runtime-role, or dynamic-library APIs. Draft 0.24 has measured managed strings/arrays, ARC, exceptions, `defer`, console, file output, threads, mutexes, initialization, and shutdown on WSL/Linux and Windows.

`System.Runtime.Target` exposes compiler constants for `Profile`, `Architecture`, and byte-sized `PointerSize`. `System.Runtime.Mmio` provides exact-width `Read`, `Write`, `ReadRelaxed`, `WriteRelaxed`, and `Barrier` intrinsics for fixed-width integers and enums. Ordered accesses use a full target I/O barrier before and after the volatile access; relaxed accesses emit only the access.

`System.Runtime.Cpu` provides allocation-free ordinary-memory barriers, pause hints, byte swaps, population counts, and leading-zero counts. `System.Endian` converts `ushort` and `uint` values to and from the nominal `be16`, `be32`, `le16`, and `le32` wire-order types. `PhysicalAddress`, `VirtualAddress`, and `IoAddress` are strict `nuint` newtypes; conversion between address domains requires an explicit conversion through `nuint`.

Draft 0.40 adds nanosecond durations, monotonic timing, deterministic PCG32 random generation, spin primitives, and common scalar math without changing runtime ABI 16 or debug metadata version 3. Draft 0.39 remains the historical native-import revision; Drafts 0.38 and 0.37 remain the SIMD geometry and scalar-layout geometry revisions. UTF-8 helpers are `[NoAlloc]`, but intentionally not `[NoRuntime]`. Callback-driven generic operations inherit the callback's effects.

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

The standard library also declares `NullReferenceException`, `IndexOutOfRangeException`, `DivideByZeroException`, `InvalidCastException`, `OverflowException`, `ArgumentException`, `ArgumentOutOfRangeException`, `OutOfMemoryException`, `ThreadStateException`, `SynchronizationLockException`, `ObjectDisposedException`, `EndOfStreamException`, and `DecoderFallbackException`. The runtime preinitializes compiler-raised fault objects during `ct_runtime_initialize`. Managed runtime checks throw these singletons without allocating, including inside strict `[NoAlloc]` call paths. Their diagnostic code and source location are per-thread exception-origin metadata rather than mutable fields on the shared object.

Freestanding exposes these types so common library code has one surface, but source `throw`, `try`, and `catch` regions remain unavailable. Compiler and standard-library failures call the configured panic provider directly.

## Console

`System.Console` provides standard-output operations:

```csharp
public static class Console
{
    public static int Read();
    public static string ReadLine();

    public static void Write(string value);
    public static void Write(char value);
    public static void Write(rune value);
    public static void Write(int value);
    public static void Write(uint value);
    public static void Write(long value);
    public static void Write(ulong value);
    public static void Write(nint value);
    public static void Write(nuint value);
    public static void Write(float value);
    public static void Write(double value);
    public static void Write(bool value);
    public static void Write(object value);

    public static void WriteLine();
    public static void WriteLine(string value);
    public static void WriteLine(char value);
    public static void WriteLine(rune value);
    public static void WriteLine(int value);
    public static void WriteLine(uint value);
    public static void WriteLine(long value);
    public static void WriteLine(ulong value);
    public static void WriteLine(nint value);
    public static void WriteLine(nuint value);
    public static void WriteLine(float value);
    public static void WriteLine(double value);
    public static void WriteLine(bool value);
    public static void WriteLine(object value);
}
```

Smaller integer types use the language overload rules. A signed widening target is better when other rules do not decide. Strings write their exact UTF-8 bytes. A null string writes no bytes. A `char` writes one UTF-8 code unit. A `rune` writes one Unicode scalar as UTF-8. Booleans write `True` or `False`. A `float` uses nine significant digits. A `double` uses 17 significant digits.

`WriteLine(value)` writes the value followed by one newline byte. Parameterless `WriteLine()` writes only the newline.

`Read()` returns the next input byte as `0..255` or `-1` at EOF. `ReadLine()` flushes standard output, reads one UTF-8 line, removes LF and one preceding CR, and returns an owned string. It returns `null` only when EOF occurs before any byte. Hosted and ESP-IDF builds use their platform console unless overridden; freestanding uses the complete console provider group. Invalid UTF-8, input errors, and lines beyond the managed-string length limit throw `System.IO.IOException` on exception-capable targets and route to panic on freestanding. These input methods can allocate and are unavailable to `[NoAlloc]` call paths.

`Console.InputEncoding` and `OutputEncoding` are read-only and return `System.Text.Encoding.UTF8`. On Windows, runtime startup switches only attached console handles to UTF-8 and remembers their prior code pages. Normal shutdown flushes and restores those pages after static finalization. Redirected streams and pipes remain byte-exact UTF-8. `Encoding.UTF8` is strict and emits no preamble; `Encoding.UTF8WithBom` is equally strict and identifies a three-byte UTF-8 preamble for text writers.

## Invariant parsing

`bool`, every fixed-width and native-width integer, `float`, and `double` expose `Parse(string)` and `TryParse(string, out T)` through their compiler-owned `System.*` surfaces. Numeric types also accept `System.Globalization.NumberStyles`. `Integer`, `Float`, and `HexNumber` are the supported composite styles. Invalid flag combinations throw `ArgumentException`; `TryParse` returns false and clears its output only for input null, syntax failure, or overflow.

Decimal integer parsing accepts an optional sign when enabled and checks the exact destination range. Hexadecimal input has the destination's fixed width, so signed values use two's-complement interpretation. Boolean input accepts only ASCII-case-insensitive `True` and `False`. Floating input supports decimal points, exponents, signed zero, `NaN`, and positive or negative `Infinity`; the pinned Ryu parser produces deterministic nearest-even bits without locale or libc conversion. `Parse` throws `ArgumentNullException`, `FormatException` (`CTP0001`), or `OverflowException` (`CTP0002`). `System.Convert` forwards string input to these default parsers.

`System.Enum.Parse<T>` and `TryParse<T>` accept declared names, underlying decimal values, and comma-separated name combinations. The optional ignore-case form performs ASCII-only case folding. Numeric input must fit the declared underlying type. Aliases are accepted, unknown names fail, and non-enum type arguments are rejected during compilation.

## Math

`System.Math` provides allocation-free single- and double-precision functions on every target:

```csharp
public static class Math
{
    public const float Pi = 3.14159265358979323846f;
    public const float E = 2.71828182845904523536f;
    public const float Tau = 6.28318530717958647692f;
    public const double Pi64 = 3.14159265358979323846264338327950288d;
    public const double E64 = 2.71828182845904523536028747135266250d;
    public const double Tau64 = 6.28318530717958647692528676655900576d;

    [NoAlloc] public static float Sqrt(float value);
    [NoAlloc] public static float Acos(float value);
    [NoAlloc] public static float Abs(float value);
    [NoAlloc] public static float Tan(float value);
    [NoAlloc] public static float Min(float left, float right);
    [NoAlloc] public static float Max(float left, float right);
    [NoAlloc] public static float Sin(float value);
    [NoAlloc] public static float Cos(float value);
    [NoAlloc] public static float Floor(float value);
    [NoAlloc] public static float Ceiling(float value);
    [NoAlloc] public static float Asin(float value);
    [NoAlloc] public static float Atan(float value);
    [NoAlloc] public static float Atan2(float y, float x);
    [NoAlloc] public static float Exp(float value);
    [NoAlloc] public static float Log(float value);
    [NoAlloc] public static float Log2(float value);
    [NoAlloc] public static float Log10(float value);
    [NoAlloc] public static float Pow(float value, float power);
    [NoAlloc] public static float Round(float value);
    [NoAlloc] public static float Truncate(float value);

    [NoAlloc] public static double Sqrt(double value);
    [NoAlloc] public static double Acos(double value);
    [NoAlloc] public static double Abs(double value);
    [NoAlloc] public static double Tan(double value);
    [NoAlloc] public static double Min(double left, double right);
    [NoAlloc] public static double Max(double left, double right);
    [NoAlloc] public static double Sin(double value);
    [NoAlloc] public static double Cos(double value);
    [NoAlloc] public static double Floor(double value);
    [NoAlloc] public static double Ceiling(double value);
    [NoAlloc] public static double Asin(double value);
    [NoAlloc] public static double Atan(double value);
    [NoAlloc] public static double Atan2(double y, double x);
    [NoAlloc] public static double Exp(double value);
    [NoAlloc] public static double Log(double value);
    [NoAlloc] public static double Log2(double value);
    [NoAlloc] public static double Log10(double value);
    [NoAlloc] public static double Pow(double value, double power);
    [NoAlloc] public static double Round(double value);
    [NoAlloc] public static double Truncate(double value);
}
```

`Pi`, `E`, and `Tau` are the nearest C~ `float` constants; the `64` variants are the nearest `double` constants. Angles use radians. Hosted, Cosmopolitan, and default ESP-IDF overloads map to the corresponding target C library functions. Freestanding dispatches through the matching unary or binary float/double provider role; ESP-IDF can override the same roles individually.

NaN, infinity, signed-zero, rounding, and domain behavior follow the target C library. `Min` and `Max` return the numeric operand when exactly one operand is NaN. C~ does not expose `errno` or floating-point exception state. These functions do not throw C~ exceptions. The native-build driver links `libm` on Unix and WSL. Manual GNU links must place `-lm` after the generated translation unit.

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

All vector types also provide componentwise `Abs`, `Min`, `Max`, `Clamp`, and `Lerp`, plus `Distance` and `DistanceSquared`. These helpers allocate no managed storage and retain ordinary IEEE behavior.

`System.Simd` provides explicit `F32x4`, `I32x4`, `U32x4`, and `Mask32x4` values with deterministic 16-byte storage. The surface includes unary and arithmetic operations, integer bitwise operations, comparisons, selection, lane access and shuffle, fixed-order reductions, deterministic conversions, bit-preserving reinterpretation, checked managed-array and native-buffer load/store on runtime-backed targets, and unaligned-safe unsafe pointer load/store on every target. `F32x4.MultiplyAdd` permits a fused instruction only when target compiler macros prove FMA support; otherwise it performs multiply followed by add. A fixed toolchain configuration is deterministic, while fused and non-fused builds can differ by one rounding.

`Vec4` keeps scalar geometry semantics. Manifest `cpuFeatures: ["simd128"]` or CLI `--cpu-feature simd128` enables compiler-verified x86 or Arm intrinsic lowering; `Target.HasFeature(CpuFeature.Simd128)` exposes the selected contract to C~ code. Unsupported architectures retain the scalar source implementation and do not accept the feature flag. SIMD values remain forbidden in exports, externs, callbacks, unmanaged function pointers, and public native data.

## Matrices and quaternions

`System.Matrix3x2`, `System.Matrix4x4`, and `System.Quaternion` are mutable single-precision value types with public scalar fields. Their natural layouts are 24, 64, and 16 bytes. They allocate no managed storage, are available on every target, and follow ordinary aggregate ARC and native-layout rules.

Matrices use row-major fields and row-vector composition: `A * B` applies `A` and then `B`. `Matrix3x2` supplies affine translation, scale, rotation, skew, composition, interpolation, determinant, inversion, and named point and vector transforms. `Matrix4x4` additionally supplies transpose, axis-angle and quaternion rotation, right-handed look-at, perspective and orthographic projections with zero-to-one depth, projective transforms, and inverse-transpose normal transforms. A failed matrix inversion returns false and writes the zero matrix.

`Quaternion` supplies length, dot, conjugate, normalization, inversion, multiplication, interpolation, axis-angle and yaw-pitch-roll construction, matrix conversion, and `Vec3` transformation. `Slerp` follows the shortest path. Failed `TryNormalize` and `TryInverse` calls write `Identity`; the ordinary methods retain IEEE results for degenerate inputs.

## Time and random generation

`System.TimeSpan` is an eight-byte readonly value containing one signed nanosecond count. It is available on every target. `Zero`, exact nanoseconds, truncated whole microseconds, milliseconds, and seconds, fractional millisecond and second totals, integer unit factories, arithmetic, equality, and ordering allocate no storage. Negative durations are valid, and integer construction and arithmetic use the language's wrapping rules.

`System.Diagnostics.Stopwatch` is available on every target. It is a mutable allocation-free value with `StartNew`, `Start`, `Stop`, `Reset`, `Restart`, `IsRunning`, `Elapsed`, `ElapsedNanoseconds`, and truncated `ElapsedMilliseconds`. `GetTimestampNanoseconds()` reads the same monotonic clock directly. Repeated starts and stops are idempotent, copied values have independent state, and instances are not thread-safe. The runtime uses `QueryPerformanceCounter`, `clock_gettime(CLOCK_MONOTONIC)`, `esp_timer_get_time`, or `Runtime.MonotonicNanoseconds` according to the target and override. Clock support is omitted unless reachable; a failed query is fatal code `CTK0001`.

`System.Diagnostics.Process` is available to ESP-IDF firmware that links the shared managed-module runtime. `Start(path, args)` loads an application module beneath `/storage/modules`, creates an independent process instance, copies its arguments, and starts its entry point on a FreeRTOS task. `Id`, `State`, `HasExited`, and `ExitCode` inspect the process. `Wait()` and timed `Wait(uint, out int)` wait for completion. `Cancel()` requests cooperative cancellation, exposed inside the current process as `Process.IsCancellationRequested`. `Terminate(uint)` and `Terminate(TimeSpan)` request cancellation and then force cleanup after the grace period.

`Send`, `Receive`, and timed `TryReceive` use copied `byte[]` messages. Managed object references never cross a process boundary. Processes loaded from the same module have separate mutable statics, allocation accounting, arguments, cancellation state, and mailboxes; code and immutable module metadata are shared. A completed process has released its module graph before `HasExited` becomes true. Managed modules are trusted native code without memory protection. Forced termination can reclaim runtime-tracked tasks, allocations, and resources, but behavior is undefined if unsafe code has leaked pointers, locks, callbacks, interrupt registrations, or untracked native resources.

`System.Random` is an allocation-free value available on every target. Its default constructor uses seed zero; the `ulong` constructor and `Reseed` select another stable sequence. `NextUInt()` implements PCG-XSH-RR 64/32 with the fixed Draft 0.40 state transition. `NextUInt(maxExclusive)` and `NextInt(minInclusive, maxExclusive)` use rejection sampling for unbiased half-open ranges. `NextFloat()` returns a value in `[0,1)` from 24 random bits. Invalid ranges throw `ArgumentOutOfRangeException`. Seeded sequences are a cross-target compatibility contract.

## Threading

`System.Threading` is available on every target. Freestanding use requires the thread or mutex provider group plus allocation, free, and runtime TLS:

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

public struct SpinWait
{
    public int Count { [NoAlloc] get; }
    public bool NextSpinWillYield { [NoAlloc] get; }
    [NoAlloc] public void SpinOnce();
    [NoAlloc] public void Reset();
}

public struct SpinLock
{
    public bool IsHeld { [NoAlloc] get; }
    [NoAlloc] public bool TryEnter();
    [NoAlloc] public void Enter();
    [NoAlloc] public void Exit();
}
```

Atomics accept Boolean, integral, native-integral, enum, and unsafe-pointer storage. They are non-copyable. Pointer atomics omit fetch operations; arithmetic fetches require integral storage and bitwise fetches require Boolean or integral storage. Invalid dynamic memory orders throw `ArgumentException` without managed allocation.

Threads run on `_beginthreadex`, POSIX threads, FreeRTOS tasks, or the configured freestanding provider. `Start` publishes delegate state, `Join` acquires worker completion, and non-default priority failures are explicit. The runtime retains the worker state through completion. Mutexes are recursive and provide acquire/release ordering. Prefer `lock (mutex) { ... }` when lexical cleanup is possible.

`SpinWait` performs exponentially increasing `Cpu.Pause` work for ten calls and then calls `Thread.Yield`; its counter saturates. `SpinLock` is non-recursive and unfair, does not track thread ownership, and uses acquire compare-exchange plus release store. It contains `Atomic<int>` and is therefore non-copyable. The caller that successfully enters must call `Exit`.

## File and directory I/O

Every target exposes synchronous binary files, directories, metadata, streams, and strict UTF-8 text. Hosted and Cosmopolitan use their platform implementations, ESP-IDF uses VFS defaults unless overridden, and freestanding requires the file and filesystem provider groups:

```csharp
namespace System.IO;

public enum FileMode : byte { Open, Create, Append, CreateNew, OpenOrCreate, Truncate }
public enum FileAccess : byte { Read, Write, ReadWrite }
public enum SeekOrigin : byte { Begin, Current, End }

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
    public static long Seek([Borrowed] FileHandle file, long offset, SeekOrigin origin);
    public static long Position([Borrowed] FileHandle file);
    public static long Length([Borrowed] FileHandle file);
    public static void SetLength([Borrowed] FileHandle file, long length);
    public static void Flush([Borrowed] FileHandle file);
    public static void Close([Consumes] FileHandle file);
}
```

`Open` accepts UTF-8 paths, rejects embedded NUL bytes, and either returns a non-null owned handle or fails. Native adapters throw `IOException`; freestanding and explicit ESP-IDF provider status failures route to panic. Windows converts paths to UTF-16 and opens them through the wide CRT API; POSIX hosts pass validated UTF-8 bytes to `fopen`. Provider paths receive borrowed validated UTF-8 plus explicit byte lengths. Files always use binary mode.

`Open` supports explicit create-new, open-or-create, and existing-file truncation in addition to the original modes. Unsupported mode/access combinations throw with `EINVAL`. File offsets and lengths are signed 64-bit values. `SetLength` requires writable access; negative lengths fail.

`Read` copies at most the destination length and returns a `nuint` count. Zero from a nonempty destination means EOF. Buffer and string writes complete fully or throw; string writes emit exact UTF-8 bytes without a newline. `Close` flushes, closes, frees native handle storage, and consumes ownership even when the native close reports an error.

Every successful `Open` must transfer, return, consume, or reserve its handle. The normal pattern is:

```csharp
FileHandle file = File.Open(path, FileMode.Open, FileAccess.Read);
defer File.Close(file);
```

`Stream` defines capabilities, length and position, ranged reads/writes, byte operations, `ReadExactly`, seek, truncation, flush, copy, and idempotent disposal. `FileStream` owns one private runtime handle and throws `ObjectDisposedException` after disposal. Stream instances are not thread-safe. `StreamReader` buffers in 4096-byte chunks, strips one leading UTF-8 BOM, handles LF and CRLF, and validates strict UTF-8 across the complete input. `StreamWriter` uses a 4096-byte output buffer, deterministic LF endings, optional `leaveOpen`, and writes one BOM only when `UTF8WithBom` begins an empty seekable stream.

`File` includes existence, copy/move/delete, metadata, byte-array, text, line, append, and stream helpers. `Directory` includes existence, recursive creation/deletion, move, current-directory access, and ordinally sorted full-path enumeration. Recursive deletion never follows symbolic links or Windows reparse points. `Path` provides target separators, combine, file/directory name, extension, root, and rooted-path operations.

`FileMetadata` reports `FileSystemEntryKind`, portable `FileAttributes`, length, and creation/access/modification timestamps with explicit availability flags. Each `FileTimestamp` stores signed Unix seconds plus nanoseconds. POSIX metadata uses `lstat`; Windows reports reparse points as links. `IOException.ErrorCode` is host-dependent and its operation identifies the failing API. Freestanding providers return the portable status plus a backend-defined native code to the panic boundary. Concurrent access to one handle or stream requires external synchronization. Async I/O, sharing controls, watchers, memory mapping, globbing, and lazy enumeration are not part of Draft 0.43.

## Environment

`System.Environment` provides process control:

```csharp
public static class Environment
{
    public static void Exit(int code);
}
```

`Exit` terminates the process immediately with the supplied native exit code. It does not run pending finally blocks or defers.

Hosted and Cosmopolitan terminate through their process adapter. Freestanding dispatches to `Runtime.Exit`. ESP-IDF permits `Environment.Exit` only when `Runtime.Exit` is implemented; firmware that specifically needs a reset can call `Esp.Idf.EspSystem.Restart` without overriding the process-style service.

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

### Target runtime roles

Freestanding and ESP-IDF expose compiler-recognized roles through `System.Runtime.Runtime`. The core result types are:

```csharp
public enum RuntimeStatus : byte { Success, EndOfStream, BufferTooSmall, NotFound, /* ... */ }

public readonly struct RuntimeResult
{
    public readonly RuntimeStatus Status;
    public readonly int NativeCode;
}

public readonly struct RuntimeTransferResult
{
    public readonly RuntimeStatus Status;
    public readonly int NativeCode;
    public readonly nuint Count;
}

public readonly struct RuntimePanicInfo
{
    public unsafe readonly byte* Code;
    public unsafe readonly byte* File;
    public readonly int Line;
    public readonly RuntimeStatus Status;
    public readonly int NativeCode;
}
```

`[RuntimeImpl]` selects typed implementations for allocation, free, panic, exit, console input/output/flush, monotonic time, scalar math, file handles and transfer, path metadata and mutation, directory enumeration, current-directory access, threads, mutexes, and runtime thread-local state. These are compiler roots, not ordinary library dispatch APIs, and have no runtime storage. Freestanding requires only the roles reached by the program, with complete file, filesystem, thread, and mutex groups. ESP-IDF retains platform defaults; declaring any member of a grouped override requires the complete group. The exact role list, signatures, bootstrap-safe closure, status semantics, and allocator contract are normative in [LANGUAGE.md](LANGUAGE.md).

## Runtime native buffers

`System.Runtime.NativeBuffer<T>` and `ReadOnlyNativeBuffer<T>` are compiler-intrinsic stack-only views. They are available to every target and retain stricter escape and unmanaged-element rules than ordinary generics.

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

## Strings, segments, and formatting

The built-in `string` type uses the compiler-recognized `System.String` declaration. Its `Length`, indexes, ranges, and segments count UTF-8 bytes. Instance operations include ordinal `Contains`, `StartsWith`, `EndsWith`, `IndexOf`, `LastIndexOf`, `Substring`, `Insert`, `Remove`, `Replace`, single-byte `Trim` variants, `ToCharArray`, `CopyTo`, `Split`, and `EnumerateSplit`. Static operations include `Empty`, `IsNullOrEmpty`, `CompareOrdinal`, `Concat`, `Join`, and `Format`.

`Split` accepts a `char` or nonempty `string` separator, an optional result count, and `StringSplitOptions.None` or `RemoveEmptyEntries`. Empty entries are preserved by default. `StringSegment` retains the source string and exposes a byte `Start`, `Length`, read-only indexing, `IsEmpty`, and materializing `ToString()`.

`System.Text.StringBuilder` owns a managed byte array. It supports capacity construction, `EnsureCapacity`, `Clear`, scalar, string, and object `Append`, `AppendLine`, `AppendFormat`, and exact `ToString()` materialization. Capacity grows geometrically and is independent from the final string size.

Composite formatting is invariant and supports escaped braces, indexed arguments, alignment, integral `D`/`d` and `X`/`x`, and floating-point `F`/`f` and `G`/`g` with precision from 0 through 99. Null arguments produce empty text. Built-in scalars and `System.IFormattable` values consume the format specification; other objects use `ToString()`. Floating-point output uses the vendored Ryu implementation for deterministic nearest-even fixed and shortest-round-trip conversion. Invalid formats throw `FormatException` and report `CTS0006` when unhandled.

`System.Text.Ascii` provides explicitly ASCII-only whitespace, letter, digit, upper/lower conversion, ordinal ignore-case comparison, and equality helpers. General string operations remain ordinal and case-sensitive; Unicode casing, normalization, collation, and grapheme segmentation are not implied.

These APIs are present in hosted, Cosmopolitan, ESP-IDF, and freestanding profiles. Freestanding operations that allocate require the configured allocate and free roles. Their validation failures route through the panic role because the freestanding profile has no catchable exception regions; managed profiles raise the documented exception types.

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

`System.Text.Utf8.GetString` and `TryGetString` copy a bounded NUL-terminated `byte*` or an exact `ReadOnlyNativeBuffer<byte>` into an owned managed string. A null pointer maps successfully to a null string. Pointer input must find its terminator within `maxBytes`; buffer input preserves embedded NUL bytes. Both forms validate canonical UTF-8. Throwing conversion reports `CTS0004` for invalid UTF-8 or `CTS0005` for a missing terminator; `Try` conversion returns false and clears its result.

`Utf8.GetByteCount` returns the exact managed byte length. `TryCopyTo` copies exact UTF-8 bytes to a `NativeBuffer<byte>`, optionally appends one NUL byte, and returns false with zero bytes written when the destination is too small. Native ownership and deallocation remain explicit, and native imports still reject direct managed-string parameters and results.

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
| `double` | 17-significant-digit binary64 text |
| `bool` | `True` or `False` |
| `char` | A one-code-unit string |
| `rune` | One Unicode scalar encoded as UTF-8 |
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

### Generic containers and array algorithms

`System.Pair<TFirst,TSecond>` stores immutable `First` and `Second` values. `System.Option<T>` supplies `Some`, `None`, `HasValue`, `TryGet`, `Or`, and `Map`. `System.Result<TOk,TErr>` supplies `Ok`, `Err`, `IsOk`, `IsErr`, both `TryGet` branches, `OkOr`, `ErrOr`, `MapOk`, and `MapErr`. Reference-bearing closed instances use ordinary ARC copy, move, and cleanup.

The callback types are `Predicate<T>`, `Equality<T>`, `Ordering<T>`, `Mapper<TInput,TOutput>`, `Folder<TState,TValue>`, and `Visitor<T>`. `System.Collections.ArrayAlgorithms` provides `Copy`, `Contains`, `IndexOf`, `FindIndex`, `Any`, `All`, `Count`, `ForEach`, `Map`, `Filter`, `Fold`, `Reversed`, and `Sorted`. All callback traversal is left to right. `Any` and `All` short-circuit, `Filter` invokes its predicate exactly once per input, and `Sorted` is stable. Allocating operations return new arrays and never mutate the source. Equality and ordering are explicit; there is no boxing fallback.

`System.IDisposable`, `IEnumerator<T>`, and `IEnumerable<T>` define deterministic enumeration. `System.Collections.List<T>` uses contiguous storage; `Stack<T>` enumerates top to bottom; and circular-buffer `Queue<T>` enumerates front to back. Checked operations throw `ArgumentOutOfRangeException` or `InvalidOperationException`; failed `Try` operations write `default(T)`.

`Map<TKey,TValue>` and `Set<T>` require supplied `Hasher<T>` and `Equality<T>` callbacks. They use cached hashes, power-of-two buckets, a 75 percent growth threshold, free-entry reuse, and insertion-order links. Growth never reinvokes callbacks. Equal keys must have equal hashes. Mutation during an active callback is invalid, callback failure is transactional, replacement keeps map order, and remove-then-add places an item last. Enumerators are versioned and throw after mutation; collections are not synchronized.

### UTF-8 rune helpers

`System.Text.Utf8.TryDecode(string,int,out rune,out int)` reads one scalar at a byte offset. The native-buffer overload validates external bytes, and `TryEncode` writes one scalar to a writable native buffer. All are allocation-free. Failure writes NUL and a zero count; an encode failure does not modify the destination. Decoding rejects continuation starts, truncated and overlong sequences, surrogates, and scalars above `U+10FFFF`.

Array storage and indexing remain compiler intrinsics. The string, split, builder, formatting, ASCII, and UTF-8 surfaces are ordinary bundled C~ declarations backed only by reachability-pruned runtime helpers where raw string creation or deterministic numeric conversion requires them.

## Runtime behavior

The GNU C23 program defines one runtime. Unity output embeds it once; modular sources import the shared implementation. Managed allocations use atomic automatic reference counting and are reclaimed on the thread that releases the last owned reference. Each attached thread has independent exception, cleanup, origin-diagnostic, and iterative-release state. Static managed fields live until reverse module finalization, static strings and fault objects are immortal, and reference cycles leak. Panics, `Environment.Exit`, abort, reset, and power loss do not promise ARC or defer cleanup.

Invalid casts and type-mismatched unboxing throw `InvalidCastException` with origin codes `CTO0001` and `CTO0003`. Null unboxing throws `NullReferenceException` with `CTO0002`.

An unhandled exception reports `CTE0001`, its fully qualified runtime type, and its origin. Throwing a null exception reference throws `NullReferenceException` with `CTE0002`. An exception escaping a supported synchronous unmanaged callback panics with `CTE0003`. Hosted fatal termination exits with `EXIT_FAILURE`; ESP-IDF applies its configured panic policy after writing the diagnostic; freestanding faults call `Runtime.Panic` directly.

Null, bounds, divide-by-zero, cast/unbox, size-overflow, embedded-NUL, and attached managed-OOM conditions are catchable through the built-in allocation-free exceptions. Hosted console and file failures continue to create catchable `System.IO.IOException` values. Unattached native entry (`CTT0001`), invalid lifecycle (`CTT0002`), ABI mismatch, ARC corruption, cleanup corruption, and native-boundary exception escape remain panics. Runtime and thread lifecycle are native ABI operations and intentionally have no C~ standard-library wrapper.

Standard-library declarations use native `[Extern]` bindings internally. Known C~-heap-free console, process, object, and ESP-IDF shims also carry `[NoAlloc]`; allocation-producing configuration and formatting paths remain uncontracted. `[NoAlloc]` on any extern is a trusted native contract, not an inspection of its implementation. Those symbol names are an implementation detail; user native interop remains governed by [C_ABI.md](C_ABI.md).

## Non-normative roadmap

The initial Cosmopolitan x64 audit has passed one portable managed-runtime APE on Linux/WSL and Windows. Broader math, environment, exports/callbacks, Unicode-path, custom-section, and final-retention cases remain explicit acceptance work. Arm64 and fat-image claims remain later gates; see [COSMOPOLITAN.md](COSMOPOLITAN.md).

The checked library roadmap includes SIMD buffer operations and safe long-lived native-resource storage. Later work can add wall-clock calendars, culture-aware formatting, Unicode casing and normalization, and regular expressions. Unicode escape syntax remains language work rather than a standard-library helper. [TODO.md](TODO.md) contains the active list.

Project binding manifests can add generated source-compatible ESP-IDF APIs alongside this handwritten surface. Their tracked C~ declarations use ordinary extern and ownership contracts, while project-private adapters consume the installed public headers, native constants, validated initializer macros, nested configuration fields, bounded fixed UTF-8 arrays, and selected output structures. Generated APIs are project declarations, not additions to the embedded standard library. `[NoAlloc]` describes only C~-heap behavior; a generated ESP-IDF call may allocate native memory. Long-lived owned-resource fields and retained callback lifetime rules remain deferred. Generated bindings do not infer `[InterruptSafe]`.
