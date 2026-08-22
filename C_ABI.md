# C backend and ABI

## Status

This document defines the generated C contract for C~ draft 0.14. Draft 0.14 is a breaking runtime and storage ABI: it defines one runtime per process, adds explicit lifecycle and panic configuration, makes built-in runtime faults catchable without allocation, makes strings and arrays contiguous allocations, formalizes constructive `out` writes, adds versioned module descriptors, and replaces readable generated names with compact canonical-identity hashes.

Draft 0.11 arithmetic operators, draft 0.12 reachability and cleanup optimization, and draft 0.13 inline assembly remain part of the language. Draft 0.14 output is not ABI-compatible with older generated modules. `[Export]`, `[Extern]`, and documented runtime ABI names remain stable native names; all other generated names are implementation artifacts.

Debug information is additive and does not change runtime ABI 14. Source-debug output may contain `#line` directives and private non-inlined exception hooks. Instrumented debug-preparation output additionally contains logical probes, a private debugger control block, per-thread debug frames, and optional private allocation-registry or guarded-allocation prefixes. These layouts exist only inside the matching instrumented image, are absent from ordinary output, and are not exported native contracts. Debug-map and target-descriptor version 2 are tooling metadata, not link-time ABI artifacts.

The default output is one GNU C23 translation unit. Modular output uses the same optimized program and runtime fragments to produce shared public/internal headers, one runtime implementation, one `.c` file per reachable namespace, one entry/module-lifecycle file, a deterministic JSON symbol map, and an ESP-IDF CMake source fragment. GCC-compatible extensions are permitted by default. Changes to this document require conformance tests.

## Target requirements

The generated file includes only C standard-library headers. Compile-time assertions require:

- Eight-bit bytes.
- Exact `int8_t`, `uint8_t`, `int16_t`, `uint16_t`, `int32_t`, `uint32_t`, `int64_t`, and `uint64_t` types when used.
- Two's-complement `int32_t`.
- Two's-complement `int64_t`.
- `intptr_t` and `uintptr_t` matching data-pointer width when native-sized integers are used.
- `size_t` representable by `uintptr_t` when native buffers are used.
- A four-byte IEEE-754 binary32 `float`.
- C23 language support. The native test driver first uses `-std=gnu23`. It retries with `-std=gnu2x` only after an option error. `CTILDE_C_STANDARD` selects an explicit dialect and disables this retry.

References, unsafe pointers, `nint`, and `nuint` use native C pointer width. A 64-bit C target therefore uses 64-bit values for all four. Fixed-width C~ scalar sizes do not change with the target.

The ESP-IDF profile additionally asserts four-byte pointers and includes `ctilde_esp_shim.h`. ESP-IDF selects the concrete Xtensa or RISC-V compiler; C~ has no per-chip backend.

Hosted programs that use console input or `System.IO` additionally include the C error and Windows wide-path headers required by their platform branch. The support is absent when those APIs are unused and is never emitted for ESP-IDF.

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
| `T*` | the mapped C type followed by `*` |
| `void*` | `void*` |
| `delegate* unmanaged<P..., R>` | exact `R (*)(P...)` function pointer |

Signed arithmetic uses generated helpers to avoid C signed-overflow undefined behavior. Draft 0.10 defines two's-complement wrapping for fixed and native-width signed integers. Native shifts derive their mask from `sizeof(uintptr_t) * CHAR_BIT`.

The emitter writes finite float constants with a decimal point and an `f` suffix. It preserves negative zero. Folded non-finite values use the `<math.h>` forms `NAN`, `INFINITY`, and `(-INFINITY)`.

## Generated names and symbol map

The compiler first constructs canonical identities for types, fields, methods, constructors, operators, accessors, descriptors, vtables, and generated thunks. A method identity includes its fully qualified containing type, semantic member name, parameter passing kinds, canonical parameter types, and result type. Composite types use recursive canonical forms, so future generic instantiations can extend the identity grammar without changing the mangling scheme.

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

`Compilation.EmitSymbolMap`, CLI `--symbol-map`, and modular bundles emit version 1 JSON sorted by compact name. Each entry includes the compact name, full canonical identity, kind, signature/result type, and source location. The map declares runtime ABI 14.

## Managed object header

Every class, string, array, and box starts with this header:

```c
typedef struct ct_object {
    const ct_type_descriptor* Type;
    uint32_t IdentityHash;
    ct_atomic_u32 RefCount;
    struct ct_object* ReleaseNext;
} ct_object;
```

`ct_atomic_u32` is a private four-byte atomic representation with the same alignment as `uint32_t`; the emitter verifies both properties. The descriptor stores a type name, base descriptor, vtable, type ID, size, value-type flag, and generated `Drop` callback. Heap objects start with `RefCount == 1`; `UINT32_MAX` marks immortal static strings. `ReleaseNext` links zero-count objects into the final-releasing thread's allocation-free iterative LIFO worklist.

The vtable contains typed function pointers. A generated thunk converts `ct_object*` to the method's declaring type.

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

Instance methods and property accessors receive a first `ct_self` pointer. Static members do not.

A box stores an object header followed by one copied scalar, enum, structure, or pointer value.

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

## Hosted console and file I/O

Hosted input and file declarations bind to compiler-owned external symbols. The emitter defines those symbols only when a resolved call uses them. Each operation can create and throw `System.IO.IOException`, so using one also enables the ordinary per-thread C~ exception runtime.

`Console.ReadLine` accumulates native bytes, validates complete UTF-8, and copies the result into an ARC-owned `ct_string`. It frees its temporary native buffer before returning or throwing. EOF before any byte returns a null managed reference; other lines return an owned string.

`System.IO.FileHandle` has the nominal C representation `uintptr_t`. A nonzero value identifies a native wrapper containing `FILE*` and the declared access mode; this wrapper is not a managed object. `File.Open` produces ownership, borrowed read/write calls preserve it, and `File.Close` consumes it, calls `fclose`, frees the wrapper, and only then throws a close error if necessary.

The file ABI uses the ordinary mappings:

```c
uintptr_t ct_host_file_open(ct_string* path, file_mode mode, file_access access);
uintptr_t ct_host_file_read(uintptr_t file, uint8_t* data, size_t length);
void ct_host_file_write_buffer(uintptr_t file, const uint8_t* data, size_t length);
void ct_host_file_write_string(uintptr_t file, ct_string* value);
void ct_host_file_close(uintptr_t file);
```

The actual enum typedef names are deterministically mangled. Windows converts validated UTF-8 paths to UTF-16 before `_wfopen_s`; POSIX passes the validated, zero-terminated bytes to `fopen`. File contents remain uninterpreted bytes.

## Extern methods

`[Extern("symbol")]` applies to a static, bodyless method. The symbol string must be a portable C identifier. It cannot be a C23 keyword. It cannot start with an underscore.

`[NoAlloc]` on an extern is a trusted assertion that its native implementation does not allocate through the C~ heap. The compiler cannot inspect native code. An unannotated extern is therefore an allocation boundary for a contracted caller.

The compiler emits an external C prototype using the mappings in this document. The native definition must use exactly that ABI. Arrays, strings, classes, and structures are C~ runtime layouts, not libc substitutes.

External names cannot collide with `main`, runtime definitions, or generated symbols. Compiler-owned symbols include descriptors, vtables, thunks, box helpers, exception handlers, cleanup state, and durable local-storage prefixes.

An extern declaration is linked only when generated code calls it. The compiler does not choose libraries or invoke a linker.

An extern function must not raise a C~ exception or call `longjmp` into C~ handler state. A generated synchronous callback trampoline uses the invoking attached thread's handler state, catches escaping C~ exceptions, and terminates with `CTE0003`. Other exception propagation across native boundaries is unsupported.

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

Initialization attaches the calling primary thread, creates immortal fault singletons, initializes the module descriptor, and publishes the ready phase. Shutdown requires every secondary thread to be detached, finalizes modules, drains ARC work, and detaches the primary thread. A panic invokes the configured handler with the diagnostic and context; returning from the handler continues to the platform's default fatal termination. Runtime phase misuse, unattached entry, refcount or cleanup corruption, ABI mismatch, pre-attachment allocation failure, and exceptions escaping callbacks or exports are panics.

Modules cannot unload while any descriptor, vtable, delegate, object, or generated function pointer from the module remains live. Independent DLL loading and dynamic module registration are not part of draft 0.14.

Value parameters are borrowed by default. `[Retained]` on a direct managed-reference extern parameter causes C~ to retain immediately before the call and transfer that count to native code. Managed-reference returns are owned by default. `[ReturnsBorrowed]` on a direct managed-reference extern result causes C~ to retain the returned value immediately. Structures containing references remain borrowed as extern arguments and owned as returns. Managed or reference-bearing extern by-reference parameters are rejected.

The runtime exports `ct_thread_attach()`, `ct_thread_detach()`, `ct_retain(ct_object*)`, and `ct_release(ct_object*)`. Attachment allocates native thread-state storage rather than managed storage. Retain and release accept null but still require an attached thread. A native owner must eventually balance every owned or retained reference. Retain overflow terminates with `CTM0002`; invalid release or underflow terminates with `CTM0003`.

ESP-IDF reserves `app_main` and the built-in `ct_esp_*` shim names. The checked shim ABI uses scalar types, opaque native typedefs, `const char*`, and explicit pointer/`size_t` pairs; ESP-IDF configuration structures, RMT channels, and `led_strip_handle_t` do not cross the C~ boundary. `ct_esp_timer_get_time_us` forwards `esp_timer_get_time()`. GPIO and `ct_esp_ws2812_*` operations return exact `esp_err_t` values.

## Future native interop constraints

This section records constraints that remain after draft 0.14.

Public ESP-IDF headers are the source of truth for native declarations. ESP-IDF promises source compatibility but does not promise stable enum values or structure layouts between releases. A future binding generator must therefore compile generated C adapters against the selected ESP-IDF headers. It must not copy configuration-structure layouts or numeric enum values into a supposedly version-independent C~ ABI.

Native-sized integers, scoped pointer-plus-length buffers, and `NativeUtf8String` cover synchronous byte and NUL-terminated UTF-8 input. A UTF-8 view lowers to `{ ct_string* Owner; const uint8_t* Data; size_t ByteLength; }` inside C~ and flattens to `const char*` at an extern or export boundary. It retains its managed owner and is dropped lexically. Managed C~ strings and arrays never convert implicitly to `char*` or flat C arrays.

Opaque handles are distinct C~ value types whose generated C representation uses the `[NativeType]` typedef from its public header. The bound ownership contract distinguishes borrowed, created, consumed, nullable, retained, owned-return, and borrowed-return values. Owned locals are move-only, and a deferred release reserves their cleanup obligation.

Delegates remain ABI-incompatible with unmanaged function pointers. `[SynchronousCallback]` lowers one delegate parameter to a typed callback followed immediately by `void* context`; generated adapters retain the delegate until the extern call returns and use that context to invoke its target. Native code may invoke the callback on attached worker threads only if all invocations complete and the workers join before the extern call returns. Retained callbacks still require explicit registration and unregistration lifetime.

No native call or callback may unwind a C~ exception through C frames. A callback trampoline must attach to defined per-task runtime state before it uses exceptions, managed allocation, virtual dispatch, or other task-sensitive services. ISR entry points additionally require compiler-checked allocation, throwing, blocking, and IRAM/DRAM reachability restrictions.

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

Recoverable runtime checks throw immortal, preinitialized standard-library exception objects without allocating. Per-thread origin metadata retains the original diagnostic code, file, and line across calls, cleanup, and rethrow. Unhandled faults print that origin and terminate through the platform policy.

| Code | Failure |
| --- | --- |
| `CTN0001`, `CTO0002`, `CTE0002` | `NullReferenceException` |
| `CTA0001`, `CTA0002`, `CTB0002`, `CTB0003`, `CTS0001` | `OverflowException` |
| `CTA0003`, `CTB0001` | `IndexOutOfRangeException` |
| `CTM0001` after attachment | `OutOfMemoryException` |
| `CTM0002` | Reference-count overflow or invalid retain |
| `CTM0003` | Invalid release, underflow, or cleanup corruption |
| `CTI0001` | `DivideByZeroException` |
| `CTS0002` | Native scalar formatting failure |
| `CTS0003` | `ArgumentException` at a native UTF-8 boundary |
| `CTO0001`, `CTO0003` | `InvalidCastException` |
| `CTE0001` | Unhandled C~ exception |
| `CTE0003` | C~ exception escaped a native export or callback barrier |
| `CTT0001` | Native entry or ARC operation occurred on an unattached thread or task |
| `CTT0002` | Attachment, detachment, task exit, or runtime shutdown violated the thread lifecycle |

Unsafe pointer dereference and indexing do not use these managed checks.

`CTM0002`, `CTM0003`, `CTE0003`, `CTT0001`, `CTT0002`, ABI mismatch, and cleanup corruption remain panics. Allocation failure before thread attachment is also a panic. `CTILDE_CONFORMANCE` enables allocation-failure injection for tests only; production builds expose no injection API. `Environment.Exit`, native `abort`, reset, and power loss bypass managed cleanup.

## Lifetime

Class instances, arrays, dynamic strings, boxes, exception objects, and reference-bearing structure values use automatic reference counting. Strong-slot replacement retains the new value, moves out the old value, stores the new value, and releases the old value. Owned temporaries and locals register automatic cleanup records; transfer operations disarm the corresponding record. Parameters and `this` are borrowed, while managed and reference-bearing structure returns are owned.

`ct_retain` uses an atomic compare/exchange loop. `ct_release` performs a release decrement and an acquire fence before the zero-count thread pushes the object through `ReleaseNext` onto its thread-local LIFO worklist. A drain already in progress pushes newly dead objects onto that same worklist, so long destruction chains do not recurse on the C stack. Class drops cover the full base layout, array drops cover reference-bearing inline elements, and boxes and structures recursively drop nested references. String and array drops free only their single enclosing allocations. Matching generated retain helpers preserve nested structure ownership during by-value copies.

Exception frames, pending actions, defer captures, and ownership cleanup records use automatic storage and do not call `ct_alloc`. Static fields own their values until process termination; static and empty strings are immortal. Reference cycles leak. `CT_MEMORY_DIAGNOSTICS` enables conformance-only live-object and live-allocation counters without adding a production API or cost.
