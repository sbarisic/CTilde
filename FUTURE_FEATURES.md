# Future language and project features

## Status

This document is the historical design record for feature groups implemented across Drafts 0.26 through 0.34. It is not normative and some final syntax differs; see [LANGUAGE.md](LANGUAGE.md) for the current contract.

The record covers source-owner identity, embedded resources, lambdas, Unicode runes, binary64 numbers, fixed-width SIMD, and source modules from repositories.

Each feature must satisfy the extension rules in [ARCHITECTURE.md](ARCHITECTURE.md). A complete revision includes syntax, binding, lowering, diagnostics, generated C, tools, documentation, and tests.

The implementation must preserve these current contracts:

- one shared parser, semantic model, typed IR, and C emitter for all targets.
- deterministic output that does not depend on input-file order.
- strict whole-program effect analysis, including `[NoAlloc]`.
- explicit ARC ownership and deterministic cleanup.
- one managed runtime in each final program.
- target-specific restrictions through validators, not parser forks.
- source-owned modular output and stable symbol identities.
- no network or file-system access inside ordinary semantic binding.

## Shared source-owner foundation

Embedded resources and repository modules both need a source owner. Add this foundation before either feature.

A source owner identifies one root project or one dependency module. It contains:

- a stable module path.
- an absolute content root in the CLI and language server.
- a normalized source identity root.
- a flag that identifies the root application.
- the exact locked dependency version when the owner is external.

Attach the source owner to each user syntax tree. Keep bundled standard-library and generated binding origins separate.

Use the source owner for these operations:

- resolve embedded-resource paths.
- enforce `internal` access.
- build canonical symbol identities.
- build source and resource artifact names.
- show dependency locations in diagnostics and editor navigation.
- order dependency initialization and finalization.

This foundation must not change current single-project behavior.

## Embedded resources

### Goal

Embed an immutable file in the final program. Let hosted, ESP-IDF, freestanding, and other C targets read the same bytes.

The first revision supports one file per declaration. It does not add directories, glob expansion, compression, or a runtime filesystem.

### Proposed surface

Use `EmbeddedResource` as the working type name. The final specification can select a different name.

```csharp
using System.Resources;

public static class Assets
{
    [Embed("assets/logo.bin")]
    public static readonly EmbeddedResource Logo;
}
```

The declaration has these rules:

- The field is static and readonly.
- The field type is exactly `EmbeddedResource`.
- The field has no initializer.
- `[Embed]` has one constant string argument.
- The path names one regular file.
- The path is relative to the source-owner root.

The type provides an allocation-free read-only API:

```csharp
public readonly struct EmbeddedResource
{
    public nuint Length { [NoRuntime] get; }
    public byte this[nuint index] { [NoRuntime] get; }

    [NoRuntime]
    public nuint Read(nuint offset, NativeBuffer<byte> destination);
}
```

The indexer checks the index. `Read` copies the available bytes and returns the copied count.

An empty resource has length zero. Its internal data address can use a one-byte sentinel.

Do not expose the backing address through safe code. An unsafe pointer view can be a later feature.

Do not represent a resource as a managed `byte[]`. A mutable array can consume RAM and permits writes through aliases.

Do not represent a resource as `ReadOnlyNativeBuffer<byte>`. Native buffers cannot escape their current scoped contract.

### Path and input rules

The CLI resolves the path before it creates the compilation. The compiler library receives immutable resource bytes and identity metadata.

The resolver must:

1. Normalize separators to `/` for identity.
2. Reject an empty path.
3. Reject an absolute path.
4. Reject `..` segments.
5. Resolve symbolic links and reject root escape.
6. Reject directories and special files.
7. Read the complete file once.
8. Calculate a SHA-256 content hash.
9. Report a stable source location for each error.

The language server uses the same resolver contract. It watches referenced resource files and invalidates the owning project snapshot.

The public compiler API needs an immutable resource-input type. Tests can supply bytes without a filesystem.

Normal semantic binding must not open files. This keeps compiler API results deterministic for fixed inputs.

### Binding and lowering

The declaration pass validates the attribute target and field shape. It binds the field to one resource input.

The bound field records:

- normalized logical path.
- source-owner identity.
- byte length.
- content hash.
- source declaration.
- resource input identity.

The typed IR treats a resource value as an immortal immutable value. Copies need no ARC operation.

The indexer lowers to a bounds check and one byte load. `Read` lowers to a checked range calculation and `memcpy`-style copy.

`Read` must handle overlapping destination storage safely. The backing resource remains immutable, so overlap should not normally occur.

Reachability should remove an unused resource. `[Used]` on its field must retain it in the final image.

Reject `EmbeddedResource` in exports, extern signatures, unmanaged function pointers, packed layouts, explicit layouts, and native headers.

### C emission

Emit portable constant C data for the first implementation:

```c
static const uint8_t ct_resource_...[] = { 0x89, 0x50, 0x4e, 0x47 };
```

Unity output emits each retained resource once. Modular output emits one `resource_<hash>.c` file per retained resource.

Add a resource-source artifact kind. Add these artifacts to hosted, freestanding, Cosmopolitan, and ESP-IDF source lists.

Include the resource content hash in object-cache identity. A resource-only edit should rebuild only its resource object and the final link.

ESP-IDF acceptance must inspect the map file. The bytes must stay in flash-backed read-only storage unless the native toolchain requires otherwise.

Freestanding output must not call allocation, file, console, exception, or runtime services to access a resource.

Generated C should format a fixed number of bytes per line. This keeps diffs and compiler memory use predictable.

Do not use C23 `#embed` until every accepted native compiler supports one compatible contract.

### Diagnostics and acceptance

Add positive and negative tests for:

- hosted unity and modular output.
- MSVC, GCC, and Clang reads.
- ESP32 and ESP32-C3 links.
- freestanding link and byte inspection.
- empty, one-byte, binary, NUL-containing, and invalid-UTF-8 files.
- missing files and root escape.
- duplicate content and distinct logical paths.
- unused-resource pruning and `[Used]` retention.
- deterministic output after repeated builds.
- language-server updates after a resource edit.
- rejection at every unsupported native boundary.

Do not claim target support from C emission alone. Inspect the final image or execute a byte-for-byte read test.

### Implementation order

1. Add source owners and immutable resource inputs.
2. Add project and language-server resource resolution.
3. Add the intrinsic type and standard-library documentation.
4. Validate `[Embed]` declarations and bind resource symbols.
5. Lower length, indexing, and copying.
6. Add unity and modular resource emission.
7. Add build-driver, cache, symbol-map, and editor integration.
8. Run the complete cross-target acceptance matrix.

## Binary64 `double`

### Goal

Add an IEEE-754 binary64 primitive without changing the meaning of current floating-point source.

### Literals and type rules

Keep an unsuffixed decimal literal as `float` for source compatibility. Add a case-insensitive `D` suffix for `double`.

```csharp
float small = 3.5;
double wide = 3.5d;
double tiny = 1.0e-12D;
```

Extend the numeric lexer to support decimal exponents for `float` and `double`. Do not add hexadecimal floating-point literals in this revision.

The literal parser must reject:

- mixed integer and floating suffixes.
- duplicate suffixes.
- a missing exponent digit.
- an overflowing finite literal.
- unsupported hexadecimal or binary floating forms.

Add `double` to the keyword, syntax, built-in type, display-name, layout, and semantic-token tables.

Define these conversions:

- `float` converts implicitly to `double`.
- Every integral primitive converts implicitly to `double`.
- `double` converts explicitly to `float`.
- `double` converts explicitly to integral primitives.
- Numeric promotion selects `double` before `float`.

Division by zero follows IEEE-754 behavior. NaN comparisons, infinities, signed zero, and rounding follow the selected native C implementation.

Do not add checked floating conversions or floating-point environment access in this revision.

### Standard library and runtime

Add `double` overloads for console output and supported `System.Math` functions. Keep the current `float` overloads.

Map the double overloads to `sqrt`, `fabs`, `tan`, `fmin`, `fmax`, `sin`, `cos`, `floor`, and `ceil`.

Keep the existing `Math.Pi` type unchanged. A separate binary64 constant needs its own documented name.

Add intrinsic support for:

- `double.ToString()` with 17 significant digits.
- string concatenation segments.
- boxing, unboxing, equality, and hash codes.
- arrays, inline arrays, fields, parameters, returns, generics, and `typeof`.
- compile-time constants and `static assert` expressions.

Do not add floating-point atomics. The current atomic restrictions remain in force.

### C and ABI mapping

Map `double` to C `double`. Emit compile-time checks for eight-byte IEEE-754 binary64.

Use round-trip C literals without an `f` suffix. Emit `NAN`, `INFINITY`, and `-INFINITY` where constant folding requires them.

Add `double` to public header mapping, layout snapshots, symbol maps, debug metadata, and native callback signatures.

This feature should not change the managed runtime layout. Confirm that conclusion through ABI snapshots before retaining runtime ABI 16.

### Diagnostics and acceptance

Test:

- literal boundaries and malformed exponents.
- every implicit and explicit conversion edge.
- mixed `float`, `double`, and integer promotion.
- constant folding for arithmetic and comparisons.
- NaN, infinity, signed zero, and subnormal behavior.
- 17-digit round trips.
- overload selection.
- boxing and generic specialization.
- native exports and callbacks.
- `sizeof`, `alignof`, fields, arrays, and inline arrays.
- MSVC, GCC, Clang, ESP-IDF, freestanding syntax, and Cosmopolitan when available.
- editor highlighting, completion, hover, signature help, and formatting.

### Implementation order

1. Extend numeric literal data so it preserves binary32 or binary64 type.
2. Add the keyword and built-in type.
3. Add conversions, promotion, operators, and constant folding.
4. Add C declarations, literals, layout checks, and ABI output.
5. Add runtime formatting, boxing, console, and math overloads.
6. Add language-server and VS Code support.
7. Run all cross-toolchain and snapshot tests.

## Unicode `rune`

### Goal

Add a Unicode scalar primitive while preserving `char` as one UTF-8 code unit.

`rune` uses four bytes. Valid values are `U+0000` through `U+D7FF` and `U+E000` through `U+10FFFF`.

### Literal syntax

Keep current character literals unchanged. Add a case-insensitive `R` suffix for a rune literal.

```csharp
char ascii = 'A';
rune letter = 'λ'r;
rune rocket = '🚀'r;
```

Support a direct UTF-8 scalar between quotes. Support `\uXXXX` and `\u{X...}` escapes in strings and rune literals.

A rune literal must contain exactly one scalar. Reject an empty literal, multiple scalars, surrogates, and values above `U+10FFFF`.

A `char` literal must still encode to exactly one UTF-8 byte. A non-ASCII character needs the rune suffix.

### Type rules

Map `rune` to an invariant scalar value, not an ordinary unsigned integer.

Permit:

- equality and ordering.
- `switch` values and constants.
- fields, arrays, inline arrays, parameters, returns, and generics.
- boxing, unboxing, `typeof`, `sizeof`, and `alignof`.
- a read-only `uint Value` property.
- a read-only `int Utf8Length` property.

Do not permit arithmetic, bitwise operators, increment, decrement, or implicit integer conversions.

Provide `Rune.TryCreate(uint value, out rune result)`. An invalid value returns false and writes the default rune.

An explicit conversion from `rune` to `uint` is optional if `Value` provides the same operation clearly.

### UTF-8 API

Add allocation-free UTF-8 operations with the primitive. A bare scalar type does not solve string traversal.

```csharp
namespace System.Text;

public static class Utf8
{
    [NoRuntime]
    public static bool TryDecode(string text, int byteOffset, out rune value, out int byteWidth);

    [NoRuntime]
    public static bool TryDecode(ReadOnlyNativeBuffer<byte> source, out rune value, out nuint byteWidth);

    [NoRuntime]
    public static bool TryEncode(rune value, NativeBuffer<byte> destination, out nuint written);
}
```

The string overload returns false for an invalid offset or a continuation-byte offset. Managed strings remain valid UTF-8.

The native-buffer overload validates external bytes. It rejects overlong encodings, surrogates, truncated sequences, and out-of-range values.

`TryEncode` requires at most four destination bytes. It returns false when the destination is too short.

Add `Console.Write(rune)` and `Console.WriteLine(rune)`. Add `rune.ToString()` as an allocating UTF-8 string conversion.

Keep `string.Length` and string indexing byte-based. Do not add hidden linear-time indexing by rune.

### C and ABI mapping

Map `rune` to `uint32_t`. Emit constants with `UINT32_C`.

Add public header mapping and layout snapshots. Native signatures can use `uint32_t`, but C~ must validate values at managed creation points.

Define boxing equality and hashing from the scalar value. Define text formatting from its UTF-8 encoding.

The compiler must not let an unchecked numeric cast create an invalid rune.

### Diagnostics and acceptance

Test:

- ASCII, two-byte, three-byte, and four-byte literals.
- direct source text and both Unicode escape forms.
- surrogate, out-of-range, empty, and multi-scalar literals.
- UTF-8 boundary values.
- overlong, truncated, continuation-only, and invalid external sequences.
- every destination length from zero through four.
- string byte offsets at leading and continuation bytes.
- console bytes and `ToString()` bytes.
- boxing, generics, arrays, switches, native signatures, and layout.
- allocation-effect behavior.
- editor tokenization and formatting.

### Implementation order

1. Extend quoted-literal decoding with scalar-aware values and Unicode escapes.
2. Add the rune suffix, keyword, and built-in type.
3. Add scalar validation, constants, comparisons, and layout.
4. Add intrinsic UTF-8 decode and encode operations.
5. Add console, string conversion, boxing, and native mapping.
6. Add language-server and VS Code support.
7. Run exact-byte tests on every accepted target.

## Lambdas

### Goal

Add concise delegate bodies without hiding capture ownership or lifetime.

Use named delegates as the only lambda target. Do not add an anonymous function type in the first revision.

### Stage 1: captureless lambdas

```csharp
public delegate int Transformer(int value);

Transformer twice = (int value) => value * 2;
Transformer negate = (int value) =>
{
    return -value;
};
```

A lambda needs a contextual named-delegate type. Assignment, argument, return, field initialization, and explicit casts can supply that type.

The first stage requires explicit parameter types. A later revision can infer parameter types from the target delegate.

The parameter count, passing kinds, parameter types, and return behavior must match the target delegate.

An expression body converts to the delegate return type. A block body uses normal return-coverage analysis.

A lambda cannot declare independent generic parameters. A lambda inside a generic method can use the enclosing type and constant parameters.

Lower a captureless lambda to one synthetic static method. Use the existing delegate factory with a null target and a stable thunk.

Delegate creation still allocates. Strict `[NoAlloc]` rejects it.

### Stage 2: explicit value captures

```csharp
int factor = 3;
Transformer scale = [factor](int value) => value * factor;
```

Use an explicit capture list. A lambda without a capture list must be captureless.

Use `[this]` to capture the current receiver. Do not capture `this` automatically.

Capture each value when the program creates the delegate:

- copy primitive and structure values.
- retain managed references.
- copy inline arrays by value.
- preserve immortal values without ARC traffic.

The synthetic capture fields are readonly. The lambda cannot assign a captured local.

Users can capture a managed state object when they need shared mutable state.

Reject captures of:

- `ref`, `in`, and `out` parameters or locals.
- native buffers and stack allocations.
- `NativeUtf8String` values.
- move-only native resources.
- unassigned locals.
- open method groups without a contextual delegate type.

Capture of a managed object can create an ARC cycle. Document this limit and keep the existing cycle behavior.

### Binding and lowering

Add lambda syntax nodes for:

- a capture list.
- a parameter list.
- an expression or block body.
- the `=>` token.

Parse lambdas in expression context before ordinary parenthesized expressions. Recovery must preserve full source round trips.

Bind the target delegate before the lambda body. Use the target signature to create parameter symbols and the expected return type.

Run a free-variable pass over the bound body. Report each undeclared capture and each unused capture.

Create stable synthetic symbols from the source-owner identity, containing method, and lambda span.

A captured lambda lowers to:

1. A synthetic sealed closure class.
2. Readonly fields for captured values.
3. One synthetic instance method for the body.
4. One closure allocation.
5. One ordinary delegate allocation that targets the closure.

The existing delegate layout already stores a retained target object and an invocation thunk. Reuse that ABI.

A captured lambda initially uses two allocations. A later optimizer can fuse the closure and delegate after measured benefit.

### Effects, reachability, and flow

Record allocation for closure and delegate creation. Analyze the lambda body like an ordinary synthetic method.

Keep indirect delegate invocation conservative for all effect categories. Effect-qualified delegates need a separate language design.

Keep `[NoRecursion]` rejection for unproved delegate dispatch. A later points-to analysis can close known local lambda targets.

Reachability must retain the synthetic body, closure type, delegate thunk, captured field types, and required runtime descriptors.

Definite-assignment analysis occurs at capture time. Later outer assignments do not change a captured value.

Exception propagation, return ownership, cleanup, and `defer` inside a lambda follow ordinary method rules.

### Debugging and tools

The language server must provide completion, hover, definitions, semantic tokens, and signature help inside a lambda.

Debug metadata must map the synthetic method to the lambda body. It must show parameters and captures with source names.

The debugger should hide the synthetic closure object by default. It can show a `Captures` scope when useful.

The formatter must preserve capture lists, expression bodies, and block bodies. The TextMate grammar must classify `=>` and parameters correctly.

### Diagnostics and acceptance

Test:

- expression and block bodies.
- every contextual conversion location.
- overload ambiguity and return conversion failures.
- static, instance, virtual, inherited, and generic calls inside bodies.
- primitive, structure, inline-array, managed-reference, and `this` captures.
- immediate capture timing.
- ARC retain, release, and cycle behavior.
- exceptions and `defer` inside lambda bodies.
- all forbidden scoped and by-reference captures.
- `[NoAlloc]`, other effects, and `[NoRecursion]` behavior.
- deterministic synthetic names and modular source ownership.
- source debugging and editor services.
- MSVC, GCC, Clang, ESP-IDF, and freestanding validation.

### Implementation order

1. Add `=>`, lambda syntax, recovery, and language-service tree support.
2. Add contextual binding for captureless expression lambdas.
3. Add block bodies, flow, effects, and synthetic static methods.
4. Add delegate lowering, reachability, debug metadata, and editor support.
5. Complete captureless cross-target acceptance.
6. Add explicit capture lists and free-variable analysis.
7. Add closure types, ARC ownership, capture diagnostics, and debug scopes.
8. Complete captured-lambda cross-target acceptance.

## Fixed-width 128-bit SIMD

### Goal and boundary

Add explicit portable SIMD values without changing the meaning, layout, or public API of the existing geometry types. Keep `Vec2`, `Vec3`, and `Vec4` as ordinary scalar structures. Native C auto-vectorization remains useful, but it is an optimization bonus rather than the C~ SIMD contract.

The first revision is exactly 128 bits wide and adds these working types in `System.Simd`:

```csharp
public struct F32x4;
public struct I32x4;
public struct U32x4;
public struct Mask32x4;
```

Do not begin with AVX-sized, native-width, SVE, or RISC-V V values. Scalable vectors need a later predicate-oriented API and must not be forced into a fixed four-lane abstraction.

### Source surface

The initial surface provides:

- `Zero`, `Splat`, and four-lane `Create` construction.
- `+`, `-`, `*`, and `/` for `F32x4`.
- wrapping `+`, `-`, and `*` for `I32x4` and `U32x4`.
- named `And`, `Or`, `Xor`, and `AndNot` operations.
- named comparisons that return `Mask32x4`.
- `Select(mask, whenTrue, whenFalse)`.
- lane extraction and replacement.
- one- and two-input compile-time shuffles.
- checked and unchecked loads and stores.
- explicit bit reinterpretation between the three data-vector types.

Use constant generic arguments for lane and shuffle immediates:

```csharp
float z = value.GetLane<2>();
F32x4 changed = value.WithLane<1>(42.0f);
F32x4 reversed = value.Shuffle<3, 2, 1, 0>();
```

Validate every lane during closed specialization. Invalid indices are compile-time diagnostics and do not become runtime checks. SIMD values expose lanes, not geometry components, so they do not provide mutable `X`, `Y`, `Z`, or `W` fields.

### Stable storage and layout

Each type has deterministic 16-byte value layout. Its source-level representation is conceptually stable storage rather than a promise that every value occupies a native SIMD register:

```c
typedef struct ct_f32x4 {
    float Lane[4];
} ct_f32x4;
```

Do not require native-register alignment for ordinary fields, arrays, boxes, or managed objects. Loads and stores must accept unaligned storage. Generated helpers move between stable storage and target vector values through intrinsic loads/stores or alias-safe byte copies; they must not use strict-aliasing-violating pointer casts.

This split preserves deterministic boxing and debugger layout, works with current allocators, and lets the native optimizer retain temporary values in registers.

### Target-neutral IR and C emission

Represent SIMD semantically in typed IR instead of lowering standard-library calls directly to backend spelling:

```csharp
IrSimdOperation(
    SimdOperation Operation,
    SimdShape Shape,
    ImmutableArray<IrValue> Inputs,
    ImmutableArray<int> Immediates)
```

`SimdShape` records lane type, lane count, total width, and signedness where relevant. Names such as `_mm_add_ps`, `vaddq_f32`, and compiler builtins never enter binding or target-neutral IR.

Initial C mappings are:

- GCC and Clang fixed-size vector helpers through `__attribute__((vector_size(16)))`, following the [GCC vector-extension contract](https://gcc.gnu.org/onlinedocs/gcc/Vector-Extensions.html) and Clang's [vector extensions](https://clang.llvm.org/docs/LanguageExtensions.html).
- MSVC x86-64 helpers through the documented [`__m128` and x86 intrinsic surface](https://learn.microsoft.com/en-us/cpp/intrinsics/x86-intrinsics-list?view=msvc-170).
- AArch64 helpers through Neon only after the Arm64 target and its [ACLE](https://arm-software.github.io/acle/main/acle.html) mapping pass acceptance.
- deterministic scalar helpers on targets without an accepted SIMD mapping.
- a future WebAssembly backend may map the same IR to `v128`; this does not add WebAssembly to the current backend or target list.

The compiler continues to emit deterministic C. Build drivers own architecture flags and may not silently enable a wider or less portable instruction set.

### CPU-feature model

Architecture and optional CPU capability are different compile-time facts. Add a target-independent feature model such as:

```csharp
public enum CpuFeature
{
    Simd128,
    Sse4_1,
    Avx2,
    Neon,
    Rvv
}

static if (Target.HasFeature(CpuFeature.Simd128))
{
    UseAcceleratedPath();
}
else
{
    UseScalarPath();
}
```

Add a canonical `cpuFeatures` manifest/CLI option with deterministic precedence and toolchain probing. `Simd128` controls whether the compiler may use the accepted hardware mapping; the four SIMD types and their scalar semantics remain available without it.

The proposed first policy enables portable 128-bit acceleration for verified x64 and Arm64 toolchains, requires explicit enablement for x86 and Arm32, and retains scalar lowering for current Xtensa and RISC-V targets. Do not claim a default until every affected build driver verifies its compiler macros and flags. More specific capabilities such as SSE4.1 and AVX2 are later opt-ins, not aliases for `Simd128`.

### Effects and memory access

Pure construction, arithmetic, comparison, mask, lane, shuffle, selection, and reinterpretation operations are trusted low-level intrinsics with `[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]`.

Memory access has two contracts:

```csharp
public static F32x4 Load(ReadOnlyNativeBuffer<float> source, nuint index);

[NoAlloc]
[NoThrow]
[NoBlock]
[NoRuntime]
public static unsafe F32x4 LoadUnchecked(float* source);
```

Checked buffer operations require four accessible lanes and participate in the existing bounds-check throw/runtime effects. Unchecked pointer operations make readable/writable extent and lifetime caller preconditions and are valid inside freestanding bootstrap and interrupt-safe closures. Feature requirements are capabilities, not a fifth effect category.

### Floating-point semantics

SIMD must not implicitly enable fast math. Specify before implementation:

- the exact order of horizontal sums and dot products.
- NaN and signed-zero behavior for minimum and maximum.
- whether multiply followed by add may contract.
- exact and approximate reciprocal and square-root operations.
- conversion behavior for NaN and out-of-range values.

Ordinary arithmetic stays precise under the existing scalar floating-point contract. Expose fused multiply-add, approximate reciprocal, and approximate square root only as explicitly named operations. Native optimization settings must not silently change these rules; Clang documents the relevant [floating-point controls](https://clang.llvm.org/docs/UsersManual.html).

### Native ABI boundary

The first revision prohibits SIMD types in `[Export]`, `[Extern]`, unmanaged function pointers, synchronous native callbacks, public extern/linker data, and generated public headers. Internal generated functions may pass SIMD values because all translation units share one compiler-owned target and feature configuration.

Later native interop may expose an explicit flattened 16-byte storage ABI. It must be specified independently for MSVC, System V, AArch64, Cosmopolitan slices, and any future WebAssembly host boundary rather than inheriting register calling conventions accidentally.

### Application-backed optimization

After explicit SIMD is complete, investigate recognizing scalar `Vec4` arithmetic and lowering it through `F32x4` without changing `Vec4` source or storage semantics. Treat this as an optimization that must preserve floating-point ordering and Debug behavior.

Do not assume that one `Vec3` maps efficiently to four lanes. Benchmark structure-of-arrays workloads such as:

```csharp
public struct Vec3x4
{
    public F32x4 X;
    public F32x4 Y;
    public F32x4 Z;
}
```

Use four-ray path-tracer traversal as the first application-backed workload. Record current scalar `Vec4` and buffer-loop vectorization reports before changing lowering so improvements are measurable.

### Diagnostics and acceptance

Test:

- exact size, layout, copying, boxing, arrays, inline arrays, fields, parameters, and returns.
- arithmetic, wrapping integer operations, comparison masks, selection, reinterpretation, lanes, and shuffles.
- invalid constant lanes and specialization identity.
- checked bounds effects and unchecked pointer contracts.
- strict floating-point results, including NaN and signed zero.
- scalar fallback equivalence on every current architecture.
- accepted SSE and Neon instruction selection without requiring optional extensions.
- CPU-feature manifest/CLI precedence, compiler probing, and `static if` pruning.
- deterministic unity and modular output, LTO, Debug, Release, symbol maps, and debug lane presentation.
- rejection at every native ABI boundary.
- MSVC, GCC, Clang, Cosmopolitan, freestanding, and ESP-IDF builds as applicable.
- native vectorization reports and the `Vec3x4` path-tracer benchmark.

### Implementation order

1. Capture current scalar `Vec4` and contiguous-buffer benchmarks plus native vectorization reports.
2. Specify exact 128-bit lane, mask, integer-wrapping, and floating-point semantics.
3. Add the four standard-library value types with deterministic scalar implementations.
4. Add constant lane and shuffle binding and validation.
5. Add `SimdShape` and `IrSimdOperation` to typed IR.
6. Add alias-safe GCC/Clang vector and MSVC intrinsic helpers while retaining scalar fallback.
7. Add `CpuFeature`, `Target.HasFeature`, manifest/CLI configuration, compiler probing, and build flags.
8. Validate every current layout, configuration, and supported native toolchain.
9. Add symbol-map/debug metadata, debugger lane display, completion, hover, and semantic services.
10. Experiment with transparent `Vec4` lowering and the `Vec3x4` path-tracer workload.

The first useful milestone is `F32x4` plus construction, loads/stores, arithmetic, comparisons, masks, selection, lanes, shuffles, scalar fallback, and verified SSE/Neon emission. Auto-vectorization remains enabled where native Release compilers provide it, but it never substitutes for these language semantics.

## Repository source modules

### Goal

Use versioned C~ source from a repository without introducing a second managed runtime or a binary package ABI.

Call this feature a source module. Keep `extern` for native C symbols and data.

The first revision compiles dependency source with the root application. It does not load precompiled C~ dynamic libraries.

### Module manifest and lock file

Extend `ctilde.json` with a canonical module path and direct dependencies:

```json
{
  "module": "github.com/example/robot",
  "sources": ["src/**/*.ct"],
  "dependencies": {
    "github.com/example/math": "v1.2.3",
    "github.com/example/protocol": "4f71c2d"
  }
}
```

The root manifest selects versions. Source imports select which modules a file can name.

Write exact resolution to `ctilde.lock`. Each entry records:

- canonical module path.
- requested version or revision.
- exact commit ID.
- normalized source-tree content hash.
- dependency manifest hash.
- direct dependency list.

Use one selected version for each module path in the complete graph. Reject a graph that needs two versions initially.

Do not resolve a branch tip during a normal check or build. A lock-file update requires an explicit command.

### CLI workflow

Add these commands:

```text
ctilde add github.com/example/math@v1.2.3
ctilde restore
ctilde update github.com/example/math
ctilde vendor
```

`add` updates the root manifest and lock file. `restore` downloads exact locked commits without changing versions.

`update` resolves a new requested version and rewrites the affected lock entries. `vendor` copies locked source into a project-owned directory.

Normal builds can restore a missing locked module when network access is allowed. They must not change the manifest or lock file.

Add an explicit offline mode. Offline check and build fail when the cache lacks a locked module.

Use the normal Git credential manager for private repositories. Do not store credentials in project files or the module cache.

### Source import syntax

Add an explicit per-file module alias:

```csharp
import math = "github.com/example/math";
using math.Linear;

public static class Program
{
    public static math.Linear.Vector Scale(math.Linear.Vector value)
    {
        return value * 2.0;
    }
}
```

The import path must exist in the current module's direct dependency list. Do not permit accidental access through a transitive dependency.

The alias is local to one source file. It occupies a separate namespace from value, type, and namespace names.

The grammar order becomes imports, using directives, one optional namespace, and type declarations.

Allow an imported alias in a using directive and a fully qualified type name. Do not make an import execute code.

The version does not appear in source. The manifest and lock file own version selection.

### Compilation model

Parse dependency files as ordinary user C~ source with a non-root source owner.

Compile the complete active graph as one semantic program. Keep generic specialization, reachability, effects, ARC, and target pruning whole-program.

Add the module path to canonical symbol identity. Diagnostic display can use `module::Namespace.Type` when two modules use the same namespace.

Enforce access by source owner:

- `private` remains limited to its containing type.
- `protected` follows class inheritance.
- `internal` is accessible only inside one module.
- `public` is accessible through a direct import.

Only the root module can declare `[EntryPoint]` or `[RuntimeImpl]` methods.

Reject dependency-owned `[Naked]`, `[TaskEntry]`, and `[Interrupt]` roots in the first revision. A later capability contract can permit them.

Allow ordinary `[Extern]` declarations, but do not let a dependency add native objects, archives, linker options, or build scripts.

The root project remains responsible for every required native symbol.

Order static initialization by dependency topology. Initialize dependencies before dependents and the root module last.

Finalize modules in exact reverse order. Keep current deterministic ordering inside each module.

Report dependency cycles with one stable module-path witness.

### Repository layout and cache

Require one library manifest at the selected repository directory. Support repository subdirectories through an explicit module path or manifest field.

A dependency manifest can contain:

- module identity and minimum compiler draft.
- source and exclusion globs.
- embedded-resource declarations.
- direct source-module dependencies.
- supported target profiles when the source is not portable.

It cannot contain root output paths, executable settings, debug launch settings, native compiler options, or toolchain installation actions.

Store restored modules in a content-addressed user cache. Treat cache contents as read-only.

The cache identity includes the canonical module path, exact commit, manifest hash, source hash, and resource hashes.

Validate every file path against the module root. Do not follow a symbolic link outside that root.

Do not execute repository hooks, generators, package scripts, CMake files, or native build files during restore.

Provide local replacements for development:

```json
{
  "replace": {
    "github.com/example/math": "../math"
  }
}
```

Require an explicit local path. Record replacement state in build identity and diagnostics.

Do not write a machine-local absolute replacement path into `ctilde.lock`.

### Language server and editor

The language server reads the manifest and lock file. It never updates versions or contacts the network during analysis.

If a locked module is missing, publish one project diagnostic with the required restore command.

Include dependency syntax trees in semantic snapshots. Open dependency definitions as read-only documents from the cache.

Watch:

- the root manifest and lock file.
- local replacement manifests, sources, and resources.
- vendored module files.
- cache completion state after an explicit restore.

Add import-path completion from direct dependencies. Add alias completion and definitions for imported module names.

Keep package-cache paths out of deterministic symbol maps. Store canonical module paths and normalized relative source paths instead.

### Security and reproducibility

Use HTTPS repository URLs by default. Reject URL credentials and unsupported transport schemes.

Pin an exact commit and content hash. Detect a moved tag or changed tree instead of accepting it silently.

Set bounded download sizes, file counts, source sizes, resource sizes, and recursion depth. Make limits configurable where large legitimate modules need them.

Use atomic cache population. Validate a complete temporary cache entry before publishing it under its final content key.

Use one process lock per cache entry. A canceled restore must not leave a valid-looking partial module.

Never run dependency code during restore. Native compilation runs only after C~ analysis succeeds.

### Diagnostics and acceptance

Test:

- exact tag and commit resolution.
- a moved tag or hash mismatch.
- missing, stale, and read-only lock files.
- offline cache hits and misses.
- direct and transitive dependencies.
- version conflicts and dependency cycles.
- module-local `internal` access.
- package alias conflicts and namespace collisions.
- generic types and methods across modules.
- whole-program effects, reachability, and target pruning.
- dependency initialization and reverse finalization order.
- dependency embedded resources.
- local replacements and vendored builds.
- cache races, cancellation, and partial downloads.
- editor navigation and diagnostics from dependency source.
- deterministic unity, modular, symbol-map, and native output.
- builds through every accepted target toolchain.

Use local test repositories for ordinary conformance. Keep live network tests separate and non-default.

### Implementation order

1. Add source-owner identity and module-aware `internal` access.
2. Add manifest module identity and exact dependency records.
3. Define and validate the lock-file format.
4. Add a read-only content-addressed module cache.
5. Add explicit restore, add, update, offline, vendor, and replacement workflows.
6. Add import syntax, aliases, and direct-dependency visibility.
7. Load dependency syntax trees into one whole-program compilation.
8. Add topological initialization, canonical identities, and modular emission.
9. Add language-server, schema, and VS Code support.
10. Add security, race, reproducibility, and cross-target acceptance tests.

## Dependency-ordered feature plan

Implement the work in this order:

1. Add source-owner identity without changing current behavior.
2. Add `double` and complete its primitive audit.
3. Add fixed-width SIMD scalar semantics, target-neutral IR, and accepted 128-bit mappings.
4. Add `rune` with UTF-8 decode and encode APIs.
5. Add embedded resources through immutable compilation inputs.
6. Add captureless lambdas.
7. Add explicit value captures and closure objects.
8. Add exact-pinned source modules and lock files.
9. Add module aliases, vendoring, local replacements, and richer update policies.

Do not combine all features into one draft. Each completed stage needs its own specification, implementation-status update, and acceptance evidence.

## Alternatives rejected

- **Managed arrays for embedded bytes:** they permit mutation and can move flash data into RAM.
- **Scoped native buffers for embedded bytes:** their current lifetime rules forbid storage and returns.
- **C23 `#embed` as the only implementation:** current accepted toolchains do not share one verified contract.
- **Change unsuffixed decimals to `double`:** this breaks current `float` source.
- **Change `char` into a Unicode scalar:** this removes the current one-byte systems type.
- **Treat `rune` as an unrestricted integer:** arithmetic can create invalid scalar values.
- **Implicit lambda captures:** they hide ARC retention, capture time, and cycle risks.
- **Mutable captured-variable cells in the first revision:** they add escape analysis and another hidden heap object.
- **Rely only on native auto-vectorization:** optimization heuristics, translation-unit boundaries, and compiler flags do not define portable source semantics.
- **Change `Vec4` into a native register type:** this mixes geometry with lane operations and destabilizes layout, alignment, ABI, boxing, and floating-point behavior.
- **Expose raw SSE, Neon, or AVX APIs as the portable surface:** this fragments ordinary source by architecture and leaks backend names into semantic lowering.
- **Use native-width or scalable vectors first:** AVX width varies by configuration, while SVE and RISC-V V require a predicate-oriented design rather than fixed `F32x4` semantics.
- **Append dependency files to the root project without module identity:** this exposes `internal` declarations and creates ambiguous names.
- **Put versions in source imports:** routine dependency updates would rewrite source files.
- **Compile each source module with its own runtime:** this breaks whole-program generics, effects, ARC, and lifecycle.
- **Run dependency build scripts:** this makes restore unsafe and non-deterministic.
- **Load precompiled C~ dynamic libraries first:** this needs a separate package ABI, runtime registration, and unloading design.
