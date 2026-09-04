# C backend and ABI

## Status

This document defines the generated C contract for C~ draft 0.49, Runtime ABI 22, and Managed Module ABI 3. Runtime ABI 22 replaces importer-owned managed call transitions with cleanup-safe provider stubs and adds process-local code overlays. Module ABI 3 adds overlay capabilities and mutable call-target slots to the deterministic managed module contract. Ordinary native exports retain their earlier layouts unless this document states otherwise.

Draft 0.49 uses Runtime ABI 22 and debug metadata version 3. CPU, floating-point, LTO, PGO, and stack instrumentation affect native code generation only. SIMD values cannot cross ordinary native boundaries; matrices and quaternions use the existing natural-layout aggregate rules. Ordinary effect and stack contracts do not change native signatures, public headers, name mangling, or ABI identity. ABI 22 output is not ABI-compatible with ABI 21 or older managed modules. `[Export]`, function/data `[Extern]`, linker symbols, and documented runtime ABI names remain stable native names; all other generated names are implementation artifacts. Managed import and call-target slots are a separate Module ABI 3 mechanism and do not weaken native-boundary validation.

Debug information is additive and does not change Runtime ABI 22. Source-debug output may contain `#line` directives and private non-inlined exception hooks. Instrumented debug-preparation output additionally contains logical probes, a private debugger control block, per-thread debug frames, and optional private allocation-registry or guarded-allocation prefixes. These layouts exist only inside the matching instrumented image, are absent from ordinary output, and are not exported native contracts. Debug-map and target-descriptor version 3 include aggregate layout metadata alongside closed-generic names, interface views, atomic storage, runtime thread IDs, Thread/Mutex presentation, and optional resident-stub/overlay placement.

The default output is one GNU C23 translation unit. Modular output uses the same optimized program and runtime fragments to produce shared public/internal headers, one runtime implementation, one `source_<stable-hash>.c` file per reachable source identity, one entry/module-lifecycle file, a deterministic JSON symbol map, and an ESP-IDF CMake source fragment. Export wrappers share their defining source partition. GCC-compatible extensions are permitted by default. Changes to this document require conformance tests.

## Target requirements

The generated file includes only C standard-library headers. Compile-time assertions require:

- Eight-bit bytes.
- Exact `int8_t`, `uint8_t`, `int16_t`, `uint16_t`, `int32_t`, `uint32_t`, `int64_t`, and `uint64_t` types when used.
- Two's-complement `int32_t`.
- Two's-complement `int64_t`.
- `intptr_t` and `uintptr_t` matching data-pointer width when native-sized integers are used.
- `size_t` representable by `uintptr_t` when native buffers are used.
- A four-byte IEEE-754 binary32 `float`.
- An eight-byte IEEE-754 binary64 `double`.
- C23 language support. The native test driver first uses `-std=gnu23`. It retries with `-std=gnu2x` only after an option error. `CTILDE_C_STANDARD` selects an explicit dialect and disables this retry.

References, unsafe pointers, `nint`, and `nuint` use native C pointer width. A 64-bit C target therefore uses 64-bit values for all four. Fixed-width C~ scalar sizes do not change with the target.

The ESP-IDF profile additionally asserts four-byte pointers and includes `ctilde_esp_shim.h`. ESP-IDF selects the concrete Xtensa or RISC-V compiler; C~ has no per-chip backend.

The freestanding profile supports GNU-compatible GCC and Clang ELF drivers only. It includes only `stdbool.h`, `stddef.h`, `stdint.h`, `inttypes.h`, `limits.h`, and `float.h`, uses internal byte loops instead of libc memory/string calls, and emits no CRT, libm, pthread, TLS, console, filesystem, exception, or process dependency. The native build uses `-ffreestanding`, `-fno-builtin`, `-fno-stack-protector`, section splitting, `-nostdlib`, `-nostartfiles`, a caller-selected linker script, and an explicit entry symbol. Compiler predefined macros must match the declared C~ architecture.

Cosmopolitan x86-64 is a Draft 0.24 target. It uses the current ABI 19 hosted object/runtime semantics while linking through `x86_64-unknown-cosmo-cc`. The unwrapped APE is the distribution artifact and `<image>.dbg` is the retained ELF/DWARF carrier. C~ does not reconstruct Cosmopolitan startup objects, linker scripts, register reservations, or TLS flags. Arm64 and fat-image contracts remain deferred; see [COSMOPOLITAN.md](COSMOPOLITAN.md).

Hosted programs that use console input or `System.IO` additionally include the C error and Windows wide-path headers required by their platform branch. ESP-IDF uses its libc/VFS adapters by default, while declared complete provider groups replace them. Freestanding output contains only provider bridges. All three forms are absent when the corresponding APIs are unreachable.

## Scalar mapping

| C~ type | C type |
| --- | --- |
| `bool` | `bool` |
| `byte`, `char` | `uint8_t` |
| `sbyte` | `int8_t` |
| `short` | `int16_t` |
| `ushort` | `uint16_t` |
| `int` | `int32_t` |
| `uint` | `uint32_t` |
| `long` | `int64_t` |
| `ulong` | `uint64_t` |
| `nint` | `intptr_t` |
| `nuint` | `uintptr_t` |
| `float` | `float` |
| `double` | `double` |
| `rune` | `uint32_t` |
| `T*` | the mapped C type followed by `*` |
| `void*` | `void*` |
| `delegate* unmanaged<P..., R>` | exact `R (*)(P...)` function pointer |

A nominal `newtype N : T` emits a named `typedef` with the representation and ABI of `T`. The standard `be16`, `be32`, `le16`, and `le32` types are such typedefs; `Endian` calls fold constants and lower to identity or `ct_cpu_bswap16`/`ct_cpu_bswap32`. A `[BitField(typeof(T))]` structure also emits a scalar typedef of `T`; its views emit masks and shifts rather than C bitfields. An inline `T[N]` value emits a deterministic named wrapper `struct` containing exactly `T Data[N]`; it is passed, returned, assigned, and stored by value. Generated retain/drop helpers traverse every element when `T` contains managed references. Public headers expose complete unmanaged nominal and bitfield types.

Signed arithmetic uses generated helpers to avoid C signed-overflow undefined behavior. Draft 0.10 defines two's-complement wrapping for fixed and native-width signed integers. Native shifts derive their mask from `sizeof(uintptr_t) * CHAR_BIT`.

The emitter writes finite float constants with a decimal point and an `f` suffix and finite double constants with sufficient binary64 precision. It preserves negative zero. Folded non-finite values use the `<math.h>` forms `NAN`, `INFINITY`, and `(-INFINITY)`.

## Generated names and symbol map

The compiler first constructs canonical identities for types, fields, methods, constructors, operators, accessors, descriptors, vtables, and generated thunks. A method identity includes its fully qualified containing type, semantic member name, parameter passing kinds, canonical parameter types, and result type. Composite types use recursive canonical forms. Closed generic instantiations extend the identity grammar without changing the mangling scheme.

Except for `[Export]`, `[Extern]`, entry, and runtime ABI names, a globally visible generated name is a category prefix followed by the lowercase first 96 bits of SHA-256 over its canonical identity. The compiler diagnoses any collision before writing output. Names are compact and deterministic under input reordering.

Generated prefixes identify symbol kinds:

| Prefix | Meaning |
| --- | --- |
| `ct_t_` | User type |
| `ct_m_` | User method |
| `ct_c_` | Constructor factory |
| `ct_f_` | Static field |
| `ct_g_`, `ct_s_` | Property accessors |
| `ct_o_` | Operator method |
| `ct_d_`, `ct_v_`, `ct_h_` | Descriptor, vtable, and thunk |
| `ct_i_`, `ct_x_`, `ct_n_`, `ct_k_` | Constructor initializer, drop helper, delegate factory, and native callback adapter |
| `ct_a_` | Specialized array type |
| `ct_box_`, `ct_unbox_` | Value box layout and conversion helper |
| `ct_l_` | User local |
| `ct_lp_`, `ct_pp_` | Durable automatic local and parameter slot used by exception lowering |
| `ct_tmp_` | Lowering temporary |
| `ct_eh_` | Lexical exception handler frame |
| `ct_ep_`, `ct_ex_`, `ct_er_` | Pending cleanup action, exception, and return payload |

Unity definitions use translation-unit-local linkage where possible. Modular definitions used by another artifact have internal-header declarations and external linkage but remain compiler-private. `public` and `internal` are C~ access rules; they do not export a native symbol.

`Compilation.EmitSymbolMap`, CLI `--symbol-map`, and modular bundles emit version 1 JSON sorted by compact name. Each entry includes the compact name, full canonical identity, kind, signature/result type, and source location. The map declares Runtime ABI 22.

## Managed object header

Every class, string, array, and box starts with this header:

```c
typedef struct ct_object {
    const ct_type_descriptor* Type;
    ct_atomic_u32 IdentityHash;
    ct_atomic_u32 RefCount;
    struct ct_object* ReleaseNext;
} ct_object;
```

`ct_atomic_u32` is a private four-byte atomic representation with the same alignment as `uint32_t`; the emitter verifies both properties, so the header size, alignment, and offsets remain unchanged. Heap objects start with `IdentityHash == 0` and `RefCount == 1`. The default object hash assigns one stable nonzero identity on first use with an atomic compare/exchange; the numeric value is not an allocation-order contract. `UINT32_MAX` marks immortal static strings. The descriptor stores a type name, base descriptor, primary vtable, immutable interface-table pointer and count, type ID, size, value-type flag, and generated `Drop` callback. `ReleaseNext` links zero-count objects into the final-releasing thread's allocation-free iterative LIFO worklist.

The vtable contains typed function pointers. A generated thunk converts `ct_object*` to the method's declaring type. Calls through interface or unsealed static receiver types use the vtable; calls whose static class receiver or selected override is sealed use the resolved method, accessor, or delegate thunk directly.

Descriptors and vtables are emitted as portable C `const` data. The empty string and every literal string use a distinct compatible `const` wrapper object. On ESP-IDF, retained `ct_d_*`, `ct_v_*`, and `ct_sl_*` symbols must resolve to flash-backed read-only ELF sections. Mutable static fields and the preinitialized runtime-fault objects remain writable. The public header continues to expose only opaque managed objects.

The ABI 15 32-bit layout contract has these exact facts, measured on the connected ESP32 with ESP-IDF 6.0.2. Size and alignment values are bytes:

| Layout | Size | Alignment | Additional fact |
| --- | ---: | ---: | --- |
| `ct_object` | 16 | 4 | Four four-byte header fields |
| `ct_string` header | 20 | 4 | `Data` offset 20 |
| `ct_type_descriptor` | 40 | 4 | Immutable metadata, including interface table pointer/count |
| `ct_vtable` | 12 | 4 | Probe fixture base virtual surface |
| Empty representative class | 16 | 4 | Header only |
| Representative mixed-field class | 28 | 4 | Header plus scalar/reference fields and padding |
| Reference-bearing structure | 12 | 4 | Inline value layout |
| `int[]` header | 20 | 4 | `Data` offset 20; element stride 4 |
| Reference-structure array header | 20 | 4 | `Data` offset 20; element stride 12 |
| Boxed `int` | 20 | 4 | Header plus four-byte value |
| Boxed reference-bearing structure | 28 | 4 | Header plus 12-byte value |

The accepted versioned memory fixture contains 720 bytes of descriptors, 204 bytes of vtables, and 496 bytes of literal-object storage. The descriptor and vtable totals include the ABI 15 interface metadata and concurrency fault types.

## Classes and structures

A class lowers to a C structure and is used through a pointer:

```c
struct ct_t_Base {
    ct_object ct_header;
};

struct ct_t_Derived {
    ct_t_Base ct_base;
    int32_t field;
};
```

The base structure is the first member. An upcast uses this prefix and keeps the original managed identity.

`new` calls an allocation factory. The factory installs the most-derived descriptor and calls non-allocating constructor initializers.

A structure lowers to the same C structure form but is passed, returned, assigned, and stored by value. A structure constructor initializes a zeroed local value and returns that value.

Natural sequential structures use ordinary C member layout. `[Packed(n)]` surrounds each generated aggregate declaration with balanced compiler-specific packing state and restores the prior state afterward. A union emits a native C `union`, with an assertion that every member has offset zero; an empty union contains one private byte.

`[Align(n)]` lowers to `__declspec(align(n))` on MSVC/clang-cl or `__attribute__((aligned(n)))` on GCC/Clang. The annotation is present on eligible aggregate declarations and field, static, and local storage. Aligned newtypes use a compiler-specific aligned typedef. Generated `CT_ALIGNOF` assertions verify the requested minimum. Alignment participates in exported-header signatures but does not define final-image placement.

An explicit-layout structure emits an outer structure containing one overlay union. Each source field has a deterministic packed carrier structure containing its byte prefix and value member, and generated field accesses use the complete nested carrier path. The overlay also contains alignment-marker members for the natural field alignments, capped by `[Packed(n)]` when present. Generated compile-time assertions verify every requested byte offset and each applicable packing contract. Unity files, modular files, and exported headers use this same renderer, and exported aggregate definitions are emitted in dependency order.

`sizeof(T)` lowers to C `sizeof`, `offsetof(T, Field)` to `offsetof` over the generated access path, and `alignof(T)` to `__alignof` on MSVC or `_Alignof` on GCC and Clang. Each result is converted to `uintptr_t`. The expressions remain symbolic through C~ constant binding; native C performs the target layout evaluation.

Instance methods and property accessors receive a first `ct_self` pointer. Static members do not.

A box stores an object header followed by one copied scalar, enum, structure, or pointer value.

### Interfaces and closed generics

An interface reference uses `ct_object*`; it does not add a second pointer or allocate for class receivers. Every emitted interface contract contributes deterministic typed slots to the generated vtable shape. Each concrete class and boxed structure descriptor also owns an immutable `ct_interface_entry` array mapping implemented interface descriptors to the concrete vtable. Class entries use receiver-adjusting thunks; boxed-structure entries use thunks that address the inline boxed value. `ct_type_is_assignable` walks these tables for casts, `is`, and `as`.

Each reachable closed generic type has a canonical identity containing its definition and recursively canonical arguments. A constant argument contributes its declared integral type and canonical checked value. It receives an independent C layout, descriptor, vtable, drop/retain helpers, methods, and static fields. Each reachable closed generic method similarly receives its own substituted signature and function. The same identities feed mangling, modular ownership, headers, symbol maps, and version-3 debug metadata. Open generic identities are compiler-only and never appear in emitted C or the public native header.

`Atomic<T>` is emitted as ordinary aligned scalar storage inside a non-copyable generated structure. Its operations call private width-aware helpers using MSVC Interlocked operations or GCC/Clang atomics. A C~ `volatile` field remains an ordinary scalar declaration; all generated accesses use the same helpers with acquire loads and release stores.

`Thread` and `Mutex` are managed objects with private native payload pointers. Payload memory uses native `calloc`/`free`, not `ct_alloc`, and their generated descriptor drop callbacks release the target-specific handle. These payload layouts are private implementation details and cannot cross `[Extern]` or `[Export]` boundaries. Hosted builds link pthread support only when reachable output references the POSIX shims; ESP-IDF uses FreeRTOS tasks and recursive mutexes.

## Delegates and unmanaged function pointers

Each named delegate lowers to a sealed managed structure containing `ct_object`, a typed invocation-thunk pointer, and an optional owned `ct_object*` receiver. Its descriptor drop callback releases that receiver. Construction allocates the delegate object and retains a captured instance receiver. Invocation null-checks the delegate and calls its typed thunk. Virtual thunks dispatch through the receiver's current vtable; `base` captures use a direct thunk.

Unmanaged function-pointer types are structural. The emitter owns a C declarator renderer that places names inside the required parentheses:

```c
int32_t (*callback)(int32_t);
```

Locals, fields, parameters, results, casts, extern prototypes, and trampolines use the correctly parenthesized form; deterministic structural typedefs remain available for internal composite layouts. By-reference signature elements use `T*` for `ref` and `out` and `const T*` for `in`. Taking the address of an extern method uses the native symbol directly. Taking the address of a C~ static method emits a translation-unit-local C ABI trampoline. A callback trampoline installs a C~ handler boundary; an escaping exception is cleaned up and reported as fatal `CTE0003` instead of unwinding into native code. This contract is valid only for synchronous invocation on the current C~ task.

## By-reference parameters

C~ methods, constructors, delegates, externs, and unmanaged function pointers use the same mappings:

| C~ parameter | C parameter |
| --- | --- |
| `T value` | `T value` |
| `ref T value` | `T* value` |
| `in T value` | `const T* value` |
| `out T value` | `T* value` |

C~ calls pass an address. `out T` is an uninitialized destination, not a preexisting strong slot. The caller drops an initialized managed value, clears the destination to a safe empty state, and marks it uninitialized before entry. The callee's first assignment constructs or moves directly into the destination without reading, retaining, or dropping its old contents. Later assignments use normal strong-slot replacement. The same rule applies to methods, constructors, delegates, unmanaged function pointers, externs, and exported declarations. The callee must assign every `out` parameter on normal return. Extern and unmanaged-function-pointer by-reference element types must be unmanaged ABI-safe.

## Native buffers and stack allocation

Each used intrinsic buffer type has a local value representation containing `Data` and `Length`, where `Length` is `size_t`. A writable view stores `T*`; a read-only view stores `const T*`. Buffer values never cross the C ABI as structures. One C~ value parameter expands in place:

```c
/* NativeBuffer<T> */         T* name_data, size_t name_length
/* ReadOnlyNativeBuffer<T> */ const T* name_data, size_t name_length
```

The same adjacent expansion applies to C~ calls, extern prototypes, delegate thunks, and unmanaged function pointers. Buffer parameters cannot be `ref`, `in`, or `out`, and buffer returns are prohibited.

`stackalloc` computes and validates `count * sizeof(T)`, reports `CTB0002` for a negative runtime `int` count and `CTB0003` for size overflow, and then uses `_alloca` on MSVC or compiler alloca support on GCC/Clang. It does not use a C variable-length array or the managed heap. A zero count produces a null data pointer. Checked indexing reports `CTB0001`.

## Enumerations

An enum lowers to a typedef of its declared fixed-width underlying C type. Members lower to typed preprocessor constants so they remain valid C case labels.

The compiler validates each explicit and implicit enum value against the underlying range.

## Arrays

Every used element type receives one array structure:

```c
typedef struct ct_a_... {
    ct_object Object;
    int32_t Length;
    element_type Data[CT_FLEXIBLE_ARRAY];
} ct_a_...;
```

An array value is a pointer to this structure. Array construction checks:

- The length is not negative.
- `length * sizeof(element)` fits `size_t`.
- Allocation succeeds.

Indexing checks the receiver for null and verifies `0 <= index < Length` before accessing `Data[index]`.

The object header, length, and aligned element storage occupy one checked allocation. Zero-length arrays remain non-null and have no element storage. Array drop walks reference-bearing elements in reverse ownership order and frees only the enclosing allocation.

## Strings

`string` is a pointer to:

```c
typedef struct ct_string {
    ct_object Object;
    int32_t Length;
    uint8_t Data[CT_FLEXIBLE_ARRAY];
} ct_string;
```

`Length` counts UTF-8 code units. `Data` is followed by a zero byte for native boundary convenience, but embedded zero bytes are valid and all C~ operations use `Length`.

Dynamic strings use one checked allocation containing the object, length, UTF-8 bytes, and trailing zero. Static strings use compatible wrapper layouts and are immortal. Every string stores `Data[Length] == 0`. A null concatenation operand is treated as an empty string. Nested concatenations containing built-in scalar `ToString()` calls are flattened, evaluated once from left to right, formatted into bounded automatic buffers, and copied into one string allocation. User-defined `ToString()` calls remain ordinary calls.

The compiler-recognized `System.String` declaration contributes methods and its `System.IFormattable` interface view without contributing fields, constructors, or a separate descriptor layout. Its primary object header and `ct_string` storage remain unchanged. Ordinal search, checked creation and copying, splitting, builder support, and numeric-formatting helpers are private and emitted only when reachable. Floating-point composite formatting embeds the required deterministic Ryu functions and tables in generated runtime support; they are not public ABI symbols.

String equality compares contents. Other class and array equality compares pointer identity.

## Static initialization

Static storage is emitted with a C zero initializer. Every program emits an ABI-versioned `ct_module_descriptor` with module name and initialize/finalize callbacks.

Types initialize in ordinal fully qualified name order. Fields within one type use source declaration order. Finalization drops managed static fields in exact reverse order and clears every slot. Partial initialization failure invokes the same reverse finalizer for fields constructed so far.

The language does not expose the generated initialization function.

## Entry point

Exactly one method must have `[EntryPoint]`. It must be a body-bearing `static void` method with no parameters.

The hosted wrapper is:

```c
int main(void)
{
    ct_runtime_initialize(NULL);
    mangled_entry_method();
    ct_runtime_shutdown();
    return EXIT_SUCCESS;
}
```

No target emits `ct_keep_symbols`. Compiler reachability removes unreachable user functions and metadata before emission. Conservatively retained translation-local runtime definitions use portable unused annotations and narrowly scoped MSVC warning handling. ESP-IDF can therefore continue linker garbage collection. Its wrapper disables buffering for `stdout` and `stderr`, initializes the module, and calls the C~ entry method:

```c
void app_main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    ct_runtime_initialize(NULL);
    mangled_entry_method();
    ct_runtime_shutdown();
}
```

Returning from the C~ entry method returns from `app_main`; it does not stop the FreeRTOS scheduler.

Freestanding rejects `[EntryPoint]` and emits neither wrapper. If ordinary runtime code is reachable, its native header defines `CTILDE_HAS_RUNTIME 1` and declares:

```c
void ct_runtime_initialize(void);
void ct_runtime_shutdown(void);
```

The caller invokes initialization before an ordinary exported wrapper and invokes shutdown after the last call. Initialization establishes one compiler-owned execution state, validates ABI 19, and initializes static fields. Shutdown finalizes statics, drains ARC releases, and validates that cleanup state is empty. There are no public attach/detach operations. A naked-only image defines `CTILDE_HAS_RUNTIME 0`, omits these declarations, and emits no managed runtime.

Runtime-role bridges call unique C~ implementations selected by `[RuntimeImpl(Runtime.*)]`. ABI 19 includes allocation/free/panic/exit; console transfer and flush; monotonic time; path and scalar-math dispatch; file, metadata, directory, and current-directory operations; thread creation/join/close/sleep/yield; runtime TLS get/set; and mutex lifecycle operations. Paths cross this private boundary as borrowed `ct_native_utf8_string` values. Handles are `uintptr_t`. Result structures carry the stable byte-sized status ordinal, native error code, and optional transferred byte count. File and directory metadata use fixed-width fields and Unix-second/nanosecond timestamps with explicit availability bits.

Allocation changes zero to one, panics on null, and clears returned storage through an internal loop. Generated deallocation does not pass null. Freestanding faults and failed services call the panic bridge directly; a returning panic is followed by an infinite compiler barrier loop. ESP-IDF uses its libc/VFS, timer, FreeRTOS, and TLS adapters by default; a complete declared service group replaces the corresponding default bridge.

A narrow `[Naked]` export emits one GNU `__attribute__((naked, noreturn))` definition. Its basic assembly is copied without the normal operand or percent transformation. It has no wrapper, prologue, epilogue, runtime-ready check, cleanup, exception barrier, or implicit return. Its section and naked state participate in native-header signature identity.

## ESP-IDF managed-module ABI

Managed modules are ELF32 `ET_DYN` images for the resolved ESP-IDF architecture. They contain a `.ctilde.manifest` record whose fixed-width header identifies Runtime ABI 22, Managed Module ABI 3, architecture, kind, canonical name and version, build identity, API hash, task stack, heap limit, exact dependencies, and overlay capability. Names occupy a 64-byte array and accept at most 63 ASCII bytes; versions occupy a 32-byte array and accept at most 31 ASCII bytes. The compiler applies these limits before emitting C, and the loader validates the record from bytes before relocation. Module files are direct children of `/sd/modules` or `/storage/modules`; bare names search SD first and use LittleFS only when the SD entry is absent.

The only fixed dynamic ABI entries are `ct_managed_module_descriptor()` and `ct_managed_module_bind_runtime(const ct_runtime_api_v22*)`. Module ABI 3 descriptors retain exact dependency, export, and import arrays and add overlay capability plus mutable call-target slots. An import names its dependency and 256-bit callable identity and supplies an address slot plus provider-descriptor slot; the loader resolves every slot before publication or rejects the load. Every callable export names a stable provider-owned resident stub. The preflight manifest is retained as a dynamic object so `project_so(...)` section garbage collection cannot discard it. Other generated definitions have hidden visibility and resolve through module-relative relocations.

`ct_managed_module_descriptor_v3` identifies the module and supplies dependency/import/export tables, per-process static-state size/alignment, initialize/finalize callbacks, application entry, argument construction, byte-message construction, resource limits, `HasOverlays`, the maximum payload size, and a call-target array. Function and string pointers in the descriptor are relocated module addresses. The firmware keeps resident module bytes mapped while any registered runtime reference can reach those addresses.

`ct_runtime_api_v22` begins with `Size` and `AbiVersion`, followed by allocation/free/final-release, exception/fault, canonical type registration, current process/module/thread state, cancellation, managed call entry/leave, and generic runtime-service slots. A module checks the size and ABI before storing the borrowed table pointer. Console services keep ordinals 16, 17, and 18 with the ABI 19 transfer layout; filesystem services keep ordinals 32 through 58. Draft 0.48 process-start redirection and reader/writer entry points remain outside the table. Runtime ABI 22 thread attach accepts a sized process-context payload so a generated FreeRTOS worker can inherit its creator; overlay-enabled closures still reject source-created workers. Console streams are reference-counted UART or pipe endpoints. A requested redirect creates an independent 8 KiB buffer, while a nonredirected stream shares its parent endpoint. Writers are serialized, shared stdin checks foreground ownership, and cancellation or closure wakes blocked transfers.

`ct_managed_call_target_v3` is a 32-bit-target record containing `Size`, `Placement`, `OverlayId`, `Reserved`, and `Body`. A resident target stores its relocated body address. An overlay target stores a payload-relative body offset which the runtime patches after validating the package directory. `ct_managed_call_frame_v22` is eight opaque native words. `EnterManagedCall(descriptor, target, frame)` saves the preceding module and overlay state and returns the callable body address; `LeaveManagedCall(frame)` restores it. Generated stubs register `LeaveManagedCall` as a resident cleanup before entry, so the same balanced transition runs on normal return and `longjmp` exception unwinding.

An overlay-enabled `.ctm` contains a loadable resident ELF followed by an aligned deterministic schema-3 overlay container and footer. The directory orders ordinal overlay names and canonical method identities and records payload file/memory sizes, alignment, relocation ranges, SHA-256 values, target indices, overlay IDs, and body offsets. Overlay text and associated literal pools are absent from every `PT_LOAD` segment. The loader accepts only the audited Xtensa relocations, derives resident addresses from the descriptor load bias, rejects direct cross-overlay body references, and applies relocations in one 16-byte-aligned process-local executable window.

Every generated `ct_type_descriptor` carries a stable 128-bit fingerprint in addition to its module-local address. Casts, interface matching, boxing, and unboxing accept equal fingerprints. `RegisterType` returns an existing canonical descriptor when the fingerprint, name, size, alignment, and value/reference kind agree, rejects incompatible collisions, and otherwise registers the provider descriptor until that module is removed. Runtime ABI 22 retains the sized `ct_type_ops` contract for value size/alignment, ARC copy/drop, equality, hashing, and comparison. The current generated descriptors reserve the operation pointer; shared unboxed standard-library generic implementations and authoritative substitution of every embedded local descriptor remain incomplete.

Each managed process owns its current-directory string, file handles, and directory-enumeration handles. Runtime cleanup closes them on normal exit, cancellation, or forced termination. A storage-generation invalidation closes handles under the unavailable mount prefix and resets affected current directories before the VFS unmount. The ABI exposes no FatFs, SDMMC, BDL, `FILE*`, or ESP-IDF handle.

The ESP-IDF host also publishes the private-layout `ct_managed_process_*` entry points used by `System.Diagnostics.Process`. `ct_managed_process_current()` returns the current logical process identifier, or zero outside managed execution, so a module can address its own copied-message mailbox without exposing a native process pointer. Completion becomes observable only after process cleanup releases the module graph and signals its wait object.

Firmware inspection copies names into fixed 64-byte module-name and 32-byte version fields while holding the registry lock; no returned pointer aliases unloadable module storage. Module snapshots report direct load-graph references as `LoadReferences`, distinct from process count. Process handles become visible through a one-way publication slot only after their FreeRTOS TLS cleanup callback is installed. Forced deletion closes a combined stop-bit/active-operation gate before it removes the task, so allocator lists, type registrations, managed-call counters, mailboxes, and bounded console output are never observed halfway through a runtime mutation.

Managed allocations have a firmware-private prefix recording process, provider module, size, and allocation-list links; the public managed object header begins after that prefix and remains unchanged. Mutable static access asks the runtime for the current process's instance of the descriptor. Module string literals and descriptors may not survive unload unless rooted through a runtime-owned canonical representation.

Managed import/export identities derive from the canonical containing type, member, parameter passing, concrete parameter and result types, ownership, and effects. They are separate from `[Export]`, which remains native C interoperability. Schema-3 metadata contains deterministic public declaration trees, exact dependency identities, and overlay summaries. The consumer binds those trees as dependency-owned source, excludes them from project export roots, enforces binary-local `internal`, and emits checked call slots without compiling provider bodies. Placement changes exact build identity but not public API hash. Public managed signatures remain concrete and non-generic.

The [ManagedShell example](examples/ManagedShell/README.md) contains separate solution projects for the firmware host and its loadable applications and libraries. The diagnostics tools use the example-local `ct_managed_diagnostics_host_v1()` accessor with protocol version 2. `shell.ctm` uses a separate copied-snapshot/foreground-control accessor. `system.ssh.ctm` uses `ct_managed_ssh_host_v2()`, whose generation-tagged opaque tokens cover sockets, hash operations, P-256/X25519 keys, and AES-GCM contexts. The firmware stores only resident cleanup callbacks in the process resource ledger; it never stores a module callback or exposes an ESP-IDF descriptor or MbedTLS pointer. These accessors are example-local contracts, not Runtime ABI 22 or Managed Module ABI 3.

## Console and file I/O

Console and file declarations bind to compiler-owned external symbols. The emitter defines those symbols only when a resolved call uses them. Hosted, Cosmopolitan, and default ESP-IDF adapter failures create `System.IO.IOException`. Freestanding and explicit ESP-IDF provider status failures call the panic boundary directly.

`Console.ReadLine` accumulates native bytes, validates complete UTF-8, and copies the result into an ARC-owned `ct_string`. It frees its temporary native buffer before returning or throwing. EOF before any byte returns a null managed reference; other lines return an owned string.

On Windows, hosted startup queries attached input/output console handles, saves their code pages, and selects `CP_UTF8` before static initialization. Shutdown flushes and restores those pages after static finalization. Redirected files and pipes are never passed through console code-page conversion. ESP-IDF uses libc/VFS by default; freestanding and ESP-IDF overrides receive explicit byte spans and status results.

`System.IO.FileHandle` has the nominal C representation `uintptr_t`. A nonzero value identifies a native wrapper containing `FILE*` and the declared access mode; this wrapper is not a managed object. `File.Open` produces ownership, borrowed read/write calls preserve it, and `File.Close` consumes it, calls `fclose`, frees the wrapper, and only then throws a close error if necessary.

The file ABI uses the ordinary mappings:

```c
uintptr_t ct_host_file_open(ct_string* path, file_mode mode, file_access access);
uintptr_t ct_host_file_read(uintptr_t file, uint8_t* data, size_t length);
void ct_host_file_write_buffer(uintptr_t file, const uint8_t* data, size_t length);
void ct_host_file_write_string(uintptr_t file, ct_string* value);
int64_t ct_host_file_seek(uintptr_t file, int64_t offset, seek_origin origin);
int64_t ct_host_file_position(uintptr_t file);
int64_t ct_host_file_length(uintptr_t file);
void ct_host_file_set_length(uintptr_t file, int64_t length);
void ct_host_file_flush(uintptr_t file);
void ct_host_file_close(uintptr_t file);
```

Managed `FileStream` stores the same handle in private `uintptr_t` storage and delegates to compiler-owned `ct_host_stream_*` wrappers. Its public layout is not native ABI. Directory, path, and metadata helpers likewise use compiler-owned symbols and managed strings or arrays; `FileMetadata` is a natural-layout value with signed Unix seconds and nanoseconds. Windows converts validated UTF-8 paths to UTF-16 before CRT or Win32 calls; POSIX uses validated, zero-terminated UTF-8. File contents remain uninterpreted bytes until an explicit strict `Encoding.UTF8` decode.

## Extern methods

`[Extern("symbol")]` applies to a static, bodyless method. The symbol string must be a portable C identifier. It cannot be a C23 keyword. It cannot start with an underscore.

`[NoAlloc]`, `[NoThrow]`, `[NoBlock]`, and `[NoRuntime]` on an extern are independent trusted assertions about its C~ semantic effects. The compiler cannot inspect native code. An unannotated extern is therefore unknown for every effect. These contracts are analysis-only: generated declarations and public headers remain unchanged. Generated ESP-IDF binding manifests continue to provide only their established `noAlloc` flag.

The compiler emits an external C prototype using the mappings in this document. The native definition must use exactly that ABI. Arrays, strings, classes, and structures are C~ runtime layouts, not libc substitutes.

External names cannot collide with `main`, runtime definitions, or generated symbols. Compiler-owned symbols include descriptors, vtables, thunks, box helpers, exception handlers, cleanup state, and durable local-storage prefixes.

An extern declaration is linked only when generated code calls it. The compiler does not choose libraries or invoke a linker.

An extern function must not raise a C~ exception or call `longjmp` into C~ handler state. A generated synchronous callback trampoline uses the invoking attached thread's handler state, catches escaping C~ exceptions, and terminates with `CTE0003`. Other exception propagation across native boundaries is unsupported.

## Hosted native imports

`[NativeImport("foo")]` and `[NativeImport("foo", "symbol")]` reuse the extern C ABI mappings but resolve through private function-pointer slots during hosted runtime startup. Source names are logical and extensionless. Windows maps `foo` to the compile-time wide name `foo.dll` and uses `LoadLibraryExW` with default loader directories, `GetProcAddress`, and `FreeLibrary`. Linux maps it to `libfoo.so` and uses `dlopen(RTLD_NOW | RTLD_LOCAL)`, `dlsym`, `dlerror`, and `dlclose`. Linux links `-ldl` only when reachable generated output contains loader support. The operating-system search path remains authoritative; there is no application-directory fallback in C~.

An application can make an ordinary native dependency available beside its executable through `hosted.runtimeFiles` in `ctilde.json`. Entries select an explicit source by operating system and architecture and name one destination file. This is build packaging, not a loader-handle API or a change to native-import name mapping. Selected Linux outputs receive `$ORIGIN` runtime search metadata so an extensionless import can resolve a staged `libfoo.so` through the operating-system loader.

Reachable imports are ordered by ordinal logical library and symbol. Compatible declarations with the same `(library, symbol, native signature)` share one slot; the slot identity includes passing, ownership, nullability, and callback adaptation facts. Distinct libraries may expose the same symbol. Unreachable imports emit no handle, filename, symbol lookup, or linker dependency. Symbol maps record the logical library and symbol; public native headers do not expose loader handles or slots.

The loader address is transferred into the structurally typed function-pointer slot with a checked size assertion and byte copy. Generated code does not alias data and function pointers or rely on a warning-producing cast. Taking the C~ method address reads the resolved slot directly. Resolution runs after panic and runtime-fault setup and before C~ static initialization. Libraries remain loaded through reverse static finalization and unload afterward in reverse load order. Failures report `CTI0001`, `CTI0002`, or `CTI0003` with logical and mapped names, symbol when applicable, declaration location, and native loader details before entering the configured panic path.

This facility loads ordinary native C ABI libraries only. It does not expose handles, provide automatic marshalling, register C~-managed module descriptors, or independently change the selected runtime ABI. Cosmopolitan, ESP-IDF, freestanding, macOS, versioned `.so` names, and non-default calling conventions are outside Draft 0.39 native-import support.

The [HostedNativeImport example](examples/HostedNativeImport/README.md) provides a complete typed C~ declaration, stateful C plug-in, and MSVC/WSL build matrix. It is intentionally separate from the ESP-IDF Managed Module ABI described above.

## Native section placement

The emitter creates deterministic placement macros ordered by ordinal section name and identified by a stable hash of section kind and name. Code declarations and definitions use the same macro. MSVC and clang-cl lower it to `__declspec(code_seg("name"))`; GCC and Clang lower it to `__attribute__((section("name")))`.

Static data definitions use `#pragma section("name", read, write)` with `__declspec(allocate("name"))` on MSVC and clang-cl, or `__attribute__((section("name")))` on GCC and Clang. Generated modular `extern` declarations omit this definition-only data annotation. `readonly` and `volatile` C~ fields still use writable custom sections because module initialization can write their initial value.

An exported method places both its internal implementation and native wrapper, and its public-header prototype carries the same code annotation. The section name participates in the public-header signature hash. An entry method places only its internal implementation; generated hosted and ESP-IDF startup wrappers remain unannotated. Placement does not change generated names, linkage, ownership, public signatures, reachability, initialization order, runtime ABI, or debug metadata.

Section placement affects the native object only. Linker scripts remain responsible for section ordering, retention, memory-region mapping, and final addresses. Alignment and weak-symbol behavior are separate contracts.

## ESP-IDF interrupt entry and residency

An `[Interrupt]` export emits one external `IRAM_ATTR void symbol(void* context)` definition. It does not emit the normal export wrapper, runtime attachment or readiness checks, `setjmp` exception barrier, ARC cleanup, or debug probes. The public native header includes `<esp_attr.h>` and carries `IRAM_ATTR` on the prototype. The `interrupt` bit participates in the deterministic header signature hash.

Every reachable C~ helper in the interrupt call closure is also emitted with `IRAM_ATTR`. Because ESP-IDF expands that macro with `__COUNTER__`, modular internal prototypes deliberately omit the placement macro; repeating it on a prototype and definition would select conflicting `.iram1.*` subsections. The definition remains authoritative for residency. Referenced compiler-owned unmanaged static definitions use `DRAM_ATTR`; generated modular `extern` declarations omit that definition-only annotation.

An extern method, extern data symbol, inline assembly block, or assembly function reached from interrupt code requires `[InterruptSafe]`. The attribute is trusted residency and execution-context metadata only. `[NoRuntime]`, `[NoBlock]`, `[NoThrow]`, and `[NoAlloc]` remain independent semantic assertions. Symbol-map version 1 additively records `interrupt`, `interruptSafe`, `codeResidency`, `dataResidency`, `assemblyFunction`, and `constInit`; runtime ABI and debug metadata versions do not change.

## Linker addresses, retention, and registers

`[LinkerSymbol("name")]` emits a sorted `extern unsigned char name[]` declaration and no definition. A read casts that array address to the declared pointer, `uintptr_t`, or `uintptr_t`-backed newtype. Public declarations appear in the native header and participate in its signature hash. Compatible duplicates coalesce through the native-symbol validator.

`[Used]` emits `__attribute__((used, retain))` for GNU or Clang ELF definitions. MSVC and clang-cl give retained definitions external compact names and emit architecture-correct `/INCLUDE` directives; an exported wrapper receives its own directive. Unsupported object formats fail with `CT4111`. Symbol maps distinguish `used` and `linkerRetained`. Weak linkage remains deferred.

`[Register(address)]` emits no object storage. A whole-field read or write casts the checked address to a naturally sized volatile pointer and surrounds the access with `ct_mmio_barrier`. A direct bit-view write uses one volatile load and one volatile store with mask-and-shift update logic; it is deliberately non-atomic. Readonly registers omit write storage. The generated C never takes the address of a register field.

Source identities normalize each input against its `SourceOwnerIdentity.SourceIdentityRoot`, preserve bundled virtual paths, and hash pathless source contents. Repository owners include their canonical module path and exact locked revision. The first 96 bits of SHA-256 over that identity form each modular source filename. Duplicate identities report `CT4112`; source input order does not affect artifacts. Draft 0.44 emits shared layouts and ABI assertions in `ctilde_types.h`, runtime support in `ctilde_runtime_internal.h`, and owned declarations in `source_<hash>.h`. Generated implementation units do not include the broad `ctilde_internal.h`; it remains a compatibility umbrella. Object-cache keys include only each source's transitive generated-header closure, plus instrumentation settings and preserved stack sidecars.

Symbol-map schema 1 additively records native entry/export names, task stack bytes, and declared stack-usage bytes. GCC stack reporting uses `-fstack-usage -fcallgraph-info=su`; LTO consumes final `.ltrans` output. Stack-report schema 1 records target/toolchain data, frame qualifiers, native roots, lower and complete bounds, longest paths, contracts, task headroom, and explicit unknown boundaries. These reports and attributes do not change Runtime ABI 22 or public-header signatures.

## Portable CPU lowering

`Cpu.MemoryBarrier` emits a full compiler and ordinary-memory barrier appropriate to x86/x64, ARM32/ARM64, Xtensa, or RISC-V. It remains distinct from the MMIO I/O barrier. `Cpu.Pause` emits the baseline target hint or a conservative compiler-safe no-op. Byte swap, population count, and leading-zero count use deterministic inline helpers and do not require optional instruction-set extensions. These helpers allocate no C~ storage, call no C~ runtime service, and do not independently change the selected runtime ABI.

## Opaque ownership and exports

`[NativeType("typedef", "header")]` uses the native typedef directly and adds its header wherever the generated translation unit or public export header requires it. Opaque values are nominal even when two declarations name the same C representation. Value inputs are borrowed unless annotated. `[Consumes]` and opaque `[Retained]` transfer ownership; `[Creates] out` and `[ReturnsOwned]` produce an owned value. Non-null contracts call the runtime null check before native entry.

`[Export("symbol")]` emits an external wrapper around the internal mangled C~ method. The wrapper first verifies thread attachment, checks the already-published module initialization state, translates buffer pairs and UTF-8 pointers, and installs a per-thread exception barrier. An escaping C~ exception terminates with `CTE0003`; it never crosses the native frame.

`EmitCHeader` emits a deterministic guarded header with `<stdbool.h>`, `<stddef.h>`, `<stdint.h>`, required native headers, `extern "C"`, reachable unmanaged enum and structure layouts, ownership comments, exported prototypes, an opaque `ct_object`, and the attachment and ARC entry points. CLI `--header` generates the C translation unit and header in memory before replacing either requested output.

One `ct_runtime_initialize`/`ct_runtime_shutdown` pair owns the process runtime. Generated modules import this state; they do not embed independent ARC, thread-local, exception, or lifetime state. Hosted output keeps a `ct_thread_state*` in C thread-local storage. ESP output stores it in the configured `CTILDE_FREERTOS_TLS_INDEX` FreeRTOS application slot and registers a task-deletion check.

```c
typedef struct ct_runtime_config {
    uint32_t Size;
    ct_panic_handler PanicHandler;
    void* PanicContext;
} ct_runtime_config;

void ct_runtime_initialize(const ct_runtime_config* config);
void ct_runtime_shutdown(void);
void ct_thread_attach(void);
void ct_thread_detach(void);
void ct_retain(ct_object* value);
void ct_release(ct_object* value);
```

Initialization attaches the calling primary thread, creates immortal fault singletons, initializes the module descriptor, and publishes the ready phase. Shutdown requires every secondary thread to be detached, finalizes modules, drains ARC work, and detaches the primary thread. A panic invokes the configured handler, prints and flushes its diagnostic, then applies the selected ESP-IDF policy: `abort` uses the existing abort path, `restart` calls `esp_restart`, and `halt` enters `esp_system_abort` after the build driver verifies `CONFIG_ESP_SYSTEM_PANIC_PRINT_HALT=y`. Hosted output retains process failure. Runtime phase misuse, unattached entry, refcount or cleanup corruption, ABI mismatch, pre-attachment allocation failure, and exceptions escaping callbacks or exports are panics.

Modules cannot unload while any descriptor, vtable, delegate, object, interface view, closed-generic instantiation, or generated function pointer from the module remains live. Independent DLL loading and dynamic module registration are not part of draft 0.25.

Value parameters are borrowed by default. `[Retained]` on a direct managed-reference extern parameter causes C~ to retain immediately before the call and transfer that count to native code. Managed-reference returns are owned by default. `[ReturnsBorrowed]` on a direct managed-reference extern result causes C~ to retain the returned value immediately. Structures containing references remain borrowed as extern arguments and owned as returns. Managed or reference-bearing extern by-reference parameters are rejected.

The runtime exports `ct_thread_attach()`, `ct_thread_detach()`, `ct_retain(ct_object*)`, and `ct_release(ct_object*)`. Attachment allocates native thread-state storage rather than managed storage. Retain and release accept null but still require an attached thread. A native owner must eventually balance every owned or retained reference. Retain overflow terminates with `CTM0002`; invalid release or underflow terminates with `CTM0003`.

ESP-IDF reserves `app_main` and the built-in `ct_esp_*` shim names. The checked shim ABI uses scalar types, opaque native typedefs, `const char*`, and explicit pointer/`size_t` pairs; ESP-IDF configuration structures, RMT channels, and `led_strip_handle_t` do not cross the C~ boundary. `ct_esp_timer_get_time_us` forwards `esp_timer_get_time()`. GPIO and `ct_esp_ws2812_*` operations return exact `esp_err_t` values.

Header-driven project bindings emit reserved project-private `ct_idf_*` adapter symbols derived from the canonical manifest identity and selected signature. These adapters are compiled by the owning IDF component and are not exported through the generated native header. Constants are read through native getters; configuration and output structures remain inside adapter translation units. Validated adapters can apply function-like initializer macros, preserve mixed native parameter order, map nested fields and bounded fixed UTF-8 arrays, and expose selected output fields. Generated C~ declarations reuse the existing extern, buffer, UTF-8, opaque, nullable-return, ownership, and synchronous-callback conventions. This does not change Runtime ABI 19.

## Future native interop constraints

Draft 0.24 has verified ordinary generated runtime symbols through the ELF carrier, but `[Used]`, custom `[Section]`, callback metadata, and arbitrary native inputs have not completed Cosmopolitan-specific acceptance. Host ABI objects and general shared libraries are not compatible inputs.

This section records constraints that remain after draft 0.44.

Fixed-width SIMD values are internal C~ value types. They are rejected in `[Export]`, `[Extern]`, unmanaged function pointers, synchronous native callbacks, public native data, and generated public headers. Their C~ storage remains an exact 16-byte lane aggregate even when generated helpers use x86/Arm intrinsics or scalar code internally. Any future public SIMD ABI must define an explicit flattened storage contract per calling convention and Cosmopolitan architecture slice rather than inheriting a compiler's register ABI.

Public ESP-IDF headers are the source of truth for native declarations. ESP-IDF promises source compatibility but does not promise stable enum values or structure layouts between releases. The binding generator therefore compiles generated C adapters against the selected configured headers. It does not copy configuration-structure layouts or numeric enum values into a version-independent C~ ABI.

Native-sized integers, scoped pointer-plus-length buffers, and `NativeUtf8String` cover synchronous byte and NUL-terminated UTF-8 input. A UTF-8 view lowers to `{ ct_string* Owner; const uint8_t* Data; size_t ByteLength; }` inside C~ and flattens to `const char*` at an extern or export boundary. It retains its managed owner and is dropped lexically. Managed C~ strings and arrays never convert implicitly to `char*` or flat C arrays.

Draft 0.41 checked native conversion is an explicit runtime copy, not an ABI conversion. Pointer input scans at most the declared bound, requires a terminator, validates canonical UTF-8, and copies the bytes into one owned `ct_string`. Exact native-buffer input performs the same validation over its complete byte length and preserves embedded zero bytes. Managed-to-native copying writes only to caller-provided storage and transfers no ownership. Native imports continue to reject direct managed-string parameters and returns.

Opaque handles are distinct C~ value types whose generated C representation uses the `[NativeType]` typedef from its public header. The bound ownership contract distinguishes borrowed, created, consumed, nullable, retained, owned-return, and borrowed-return values. Owned locals are move-only, and a deferred release reserves their cleanup obligation.

Delegates remain ABI-incompatible with unmanaged function pointers. `[SynchronousCallback]` lowers one delegate parameter to a typed callback followed immediately by `void* context`; generated adapters retain the delegate until the extern call returns and use that context to invoke its target. Native code may invoke the callback on attached worker threads only if all invocations complete and the workers join before the extern call returns. Retained callbacks still require explicit registration and unregistration lifetime.

No native call or callback may unwind a C~ exception through C frames. A callback trampoline must attach to defined per-task runtime state before it uses exceptions, managed allocation, virtual dispatch, or other task-sensitive services. An ESP-IDF interrupt entry is the restricted exception-free `void(void*)` profile described above; other ISR signatures and retained callback lifetimes remain deferred.

## Exceptions

The compiler includes `<setjmp.h>` only when the program uses real exception regions or callback exception barriers. A method containing only ordinary `defer` does not create a `jmp_buf`. When required, the generated runtime defines:

```c
typedef struct ct_exception_frame {
    jmp_buf* Target;
    struct ct_exception_frame* Previous;
    struct ct_cleanup_record* CleanupBoundary;
} ct_exception_frame;
```

Each attached thread owns a `ct_thread_state` containing its active handler, current owned exception, top automatic cleanup record, and iterative release worklist. `setjmp` targets and cleanup records remain automatic storage on that thread's C stack. A throw can unwind only frames registered by the current thread.

Each invocation of a method that contains `try` creates its `jmp_buf` values and handler frames on the C stack. CFG liveness places only values modified after `setjmp` and live after a possible `longjmp` in compiler-generated volatile durable storage. Unrelated parameters, locals, and hot-loop state remain ordinary automatic values.

Each lexical try statement owns fixed handler storage for one invocation. A loop reuses that lexical frame. The compiler never copies a `jmp_buf`.

The generated `setjmp` call is the controlling expression of an `if`. Normal and exceptional paths pop the active frame before a catch starts. Catch matching follows the runtime descriptor base chain. Throw retains the exception into the global slot, unwinds automatic cleanup records to the selected handler boundary, and then performs `longjmp`. A catch moves the global ownership into a cleanup-tracked slot. Rethrow establishes the next current-exception ownership before unwinding the caught slot.

Finally lowering stores one pending action: normal completion, return, break, continue, or exception. Every exit that crosses the cleanup region goes to the finally block. Normal finally completion resumes the saved action. A throw from finally replaces it. `defer` captures its converted receiver and arguments into automatic state immediately. It pushes capture ownership below a deferred-invocation cleanup record, preserving LIFO invocation and release on fallthrough, transfer, or exception without synthetic try/finally lowering. Unhandled propagation unwinds outstanding cleanup records before termination, so an older enclosing defer still runs when a newer cleanup throws.

`Environment.Exit` calls native process termination directly. It does not unwind C~ handlers and does not run finally blocks or defers.

## Runtime faults and panics

Hosted and ESP-IDF runtime checks throw immortal, preinitialized standard-library exception objects without allocating. Per-thread origin metadata retains the original diagnostic code, file, and line across calls, cleanup, and rethrow. Unhandled faults print that origin and terminate through the platform policy. Freestanding runtime checks instead call the panic role directly and are not catchable.

| Code | Failure |
| --- | --- |
| `CTN0001` | `NullReferenceException`, or `ArgumentNullException` for a required library argument |
| `CTO0002`, `CTE0002` | `NullReferenceException` |
| `CTA0001`, `CTA0002`, `CTB0002`, `CTB0003`, `CTS0001` | `OverflowException` |
| `CTA0003`, `CTB0001` | `IndexOutOfRangeException` |
| `CTM0001` after attachment | `OutOfMemoryException` |
| `CTM0002` | Reference-count overflow or invalid retain |
| `CTM0003` | Invalid release, underflow, or cleanup corruption |
| `CTD0001` | `DivideByZeroException` |
| `CTR0001` | `ArgumentOutOfRangeException` |
| `CTS0002` | Native scalar formatting failure |
| `CTS0003` | `ArgumentException` at a native UTF-8 boundary |
| `CTS0004` | Invalid canonical UTF-8 during native-to-managed conversion |
| `CTS0005` | No NUL terminator within the supplied native pointer bound |
| `CTS0006` | Invalid composite or scalar format specification |
| `CTP0001` | Invalid invariant scalar or enum format |
| `CTP0002` | Parsed value exceeded its destination range |
| `CTP0003` | Invalid `NumberStyles` combination |
| `CTO0001`, `CTO0003` | `InvalidCastException` |
| `CTE0001` | Unhandled C~ exception |
| `CTE0003` | C~ exception escaped a native export or callback barrier |
| `CTT0001` | Native entry or ARC operation occurred on an unattached thread or task |
| `CTT0002` | Attachment, detachment, task exit, or runtime shutdown violated the thread lifecycle |
| `CTK0001` | Fatal monotonic-clock query or conversion failure |

Unsafe pointer dereference and indexing do not use these managed checks.

Draft 0.39 assigns the native-import failures `CTI0001` through `CTI0003`; the integer divide-by-zero runtime fault is `CTD0001`. Draft 0.40 adds catchable random-range origin `CTR0001` and fatal monotonic-clock code `CTK0001`. Draft 0.41 adds checked native UTF-8 failures `CTS0004` and `CTS0005` plus formatting failure `CTS0006`. Draft 0.42 adds invariant parsing failures `CTP0001` through `CTP0003`.

`CTM0002`, `CTM0003`, `CTE0003`, `CTT0001`, `CTT0002`, ABI mismatch, and cleanup corruption remain panics. Allocation failure before thread attachment is also a panic. `CTILDE_CONFORMANCE` enables allocation-failure injection for tests only; production builds expose no injection API. `Environment.Exit`, native `abort`, reset, and power loss bypass managed cleanup.

## Lifetime

Class instances, arrays, dynamic strings, boxes, exception objects, and reference-bearing structure values use automatic reference counting. Strong-slot replacement retains the new value, moves out the old value, stores the new value, and releases the old value. Owned temporaries and locals register automatic cleanup records; transfer operations disarm the corresponding record. Parameters and `this` are borrowed, while managed and reference-bearing structure returns are owned.

Compiler-generated ownership operations use private inline retain/release fast paths. Retain performs the atomic compare/exchange directly. Release performs the release decrement directly and enters the attached thread's existing destruction worklist only when it removes the final reference. The public `ct_retain` and `ct_release` functions remain strict attachment-checked wrappers, including for null. Final release performs an acquire fence before pushing the object through `ReleaseNext`; a drain already in progress pushes newly dead objects onto that same worklist, so long destruction chains do not recurse on the C stack. Class drops cover the full base layout, array drops cover reference-bearing inline elements, and boxes and structures recursively drop nested references. String and array drops free only their single enclosing allocations. Matching generated retain helpers preserve nested structure ownership during by-value copies.

Exception frames, pending actions, defer captures, and ownership cleanup records use automatic storage and do not call `ct_alloc`. Static fields own their values until process termination; static and empty strings are immortal. Reference cycles leak. `CT_MEMORY_DIAGNOSTICS` enables conformance-only live-object and live-allocation counters without adding a production API or cost.
