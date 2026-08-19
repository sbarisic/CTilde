# C backend and ABI

## Status

This document defines the generated C contract for C~ draft 0.10. Draft 0.10 makes ARC atomic, moves cleanup and exception state into attached-thread storage, and adds native attachment to the draft 0.9 export and callback ABI. Every generated translation unit uses the new runtime, so draft 0.10 generated objects and runtime entry points are ABI-incompatible with draft 0.9.

C~ draft 0.11 adds source-level arithmetic operators without changing this ABI. Draft 0.12 uses them for allocation-free standard-library vectors and intentionally changes internal generated C through reachability pruning, direct-defer cleanup, scalar string-build fusion, and removal of `ct_keep_symbols`; it does not change the public-header signature prefix. Operator functions remain internal `ct_op_*` symbols and are never exported directly. A vector layout appears in a generated header only when an ordinary exported signature uses that unmanaged structure. Managed layouts, reference-return conventions, runtime entry points, and native ownership contracts remain draft 0.10 compatible.

The output is a single GNU C23 translation unit for a selected target profile. GCC-compatible extensions are permitted by default. The C source format is deterministic, but generated internal symbol names are a compiler ABI rather than a user-facing source API. Changes to this document require conformance tests.

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

## Name encoding

Every user name is encoded from its UTF-8 bytes:

1. Prefix the component with an underscore, its decimal byte length, and another underscore.
2. Copy ASCII letters and digits.
3. Encode every other byte as an underscore followed by two uppercase hexadecimal digits.

Dots, underscores, Unicode bytes, and C punctuation therefore cannot collide.

Generated prefixes identify symbol kinds:

| Prefix | Meaning |
| --- | --- |
| `ct_t` | User type |
| `ct_m_` | User method |
| `ct_ctor_` | Constructor factory |
| `ct_f_` | Static field |
| `ct_get_`, `ct_set_` | Property accessors |
| `ct_a_` | Specialized array type |
| `ct_desc_` | Runtime type descriptor |
| `ct_vtable_`, `ct_vthunk_` | Virtual dispatch table and receiver thunk |
| `ct_new_delegate_`, `ct_drop_delegate_`, `ct_delegate_thunk_` | Managed delegate factory, drop callback, and invocation thunk |
| `ct_callback_` | C ABI trampoline for a C~ static method address |
| `ct_init_` | Non-allocating class constructor initializer |
| `ct_box_`, `ct_unbox_` | Value box layout and conversion helper |
| `ct_l_` | User local |
| `ct_lp_`, `ct_pp_` | Durable automatic local and parameter slot used by exception lowering |
| `ct_tmp_` | Lowering temporary |
| `ct_eh_` | Lexical exception handler frame |
| `ct_ep_`, `ct_ex_`, `ct_er_` | Pending cleanup action, exception, and return payload |

Method names append a structural code for every parameter type. `nint` and `nuint` use `ni` and `nu`. A non-value parameter prefixes its type code with `ref`, `in`, or `out`; existing value-parameter names are unchanged. Overloads therefore have distinct C symbols without hashes.

All generated user definitions have translation-unit-local linkage. `public` and `internal` are C~ access rules; they do not export a native symbol. The exceptions are C `main` and declarations created by `[Extern]`.

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

C~ calls pass an address. An `out` destination containing managed references is dropped and zeroed before the call. The callee uses normal strong-slot replacement and must assign every `out` parameter on normal return. Extern and unmanaged-function-pointer by-reference element types must be unmanaged ABI-safe.

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
    element_type* Data;
} ct_a_...;
```

An array value is a pointer to this structure. Array construction checks:

- The length is not negative.
- `length * sizeof(element)` fits `size_t`.
- Allocation succeeds.

Indexing checks the receiver for null and verifies `0 <= index < Length` before accessing `Data[index]`.

Zero-length arrays have a null `Data` pointer and a non-null array object.

## Strings

`string` is a pointer to:

```c
typedef struct ct_string {
    ct_object Object;
    int32_t Length;
    const uint8_t* Data;
} ct_string;
```

`Length` counts UTF-8 code units. `Data` is followed by a zero byte for native boundary convenience, but embedded zero bytes are valid and all C~ operations use `Length`.

String literals use static byte arrays and string objects. Every dynamic string stores `Data[Length] == 0`, including concatenation results. A null concatenation operand is treated as an empty string. Nested concatenations containing built-in scalar `ToString()` calls are flattened, evaluated once from left to right, formatted into bounded automatic buffers, and copied into one allocated string object and one allocated byte array. User-defined `ToString()` calls remain ordinary calls.

String equality compares contents. Other class and array equality compares pointer identity.

## Static initialization

Static storage is emitted with a C zero initializer. The generated `ct_module_init` function then evaluates explicit field initializers.

Types are initialized in ordinal fully qualified name order. Fields within one type use source declaration order. The selected entry wrapper calls `ct_module_init` exactly once before the C~ entry method.

The language does not expose the generated initialization function.

## Entry point

Exactly one method must have `[EntryPoint]`. It must be a body-bearing `static void` method with no parameters.

The hosted wrapper is:

```c
int main(void)
{
    ct_module_init();
    mangled_entry_method();
    return EXIT_SUCCESS;
}
```

No target emits `ct_keep_symbols`. Compiler reachability removes unreachable user functions and metadata before emission. Conservatively retained translation-local runtime definitions use portable unused annotations and narrowly scoped MSVC warning handling. ESP-IDF can therefore continue linker garbage collection. Its wrapper disables buffering for `stdout` and `stderr`, initializes the module, and calls the C~ entry method:

```c
void app_main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    ct_module_init();
    mangled_entry_method();
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

Every draft 0.10 program emits thread-entry machinery. Hosted output keeps a `ct_thread_state*` in C thread-local storage. ESP output stores it in the configured `CTILDE_FREERTOS_TLS_INDEX` FreeRTOS application slot and registers a task-deletion check. The entrypoint installs an automatic primary state before module initialization. Native-created threads call `ct_thread_attach` after initialization and `ct_thread_detach` before exit.

Value parameters are borrowed by default. `[Retained]` on a direct managed-reference extern parameter causes C~ to retain immediately before the call and transfer that count to native code. Managed-reference returns are owned by default. `[ReturnsBorrowed]` on a direct managed-reference extern result causes C~ to retain the returned value immediately. Structures containing references remain borrowed as extern arguments and owned as returns. Managed or reference-bearing extern by-reference parameters are rejected.

The runtime exports `ct_thread_attach()`, `ct_thread_detach()`, `ct_retain(ct_object*)`, and `ct_release(ct_object*)`. Attachment allocates native thread-state storage rather than managed storage. Retain and release accept null but still require an attached thread. A native owner must eventually balance every owned or retained reference. Retain overflow terminates with `CTM0002`; invalid release or underflow terminates with `CTM0003`.

ESP-IDF reserves `app_main` and the built-in `ct_esp_*` shim names. The checked shim ABI uses scalar types, opaque native typedefs, `const char*`, and explicit pointer/`size_t` pairs; ESP-IDF configuration structures, RMT channels, and `led_strip_handle_t` do not cross the C~ boundary. `ct_esp_timer_get_time_us` forwards `esp_timer_get_time()`. GPIO and `ct_esp_ws2812_*` operations return exact `esp_err_t` values.

## Future native interop constraints

This section records constraints that remain after draft 0.10.

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

## Runtime failures

The runtime prints one line to standard error. Hosted output exits with `EXIT_FAILURE`; ESP-IDF output uses compact source filenames and calls `abort()`.

| Code | Failure |
| --- | --- |
| `CTN0001` | Managed null access |
| `CTA0001` | Negative array length |
| `CTA0002` | Array allocation-size overflow |
| `CTA0003` | Array or string index out of range |
| `CTM0001` | Allocation failure |
| `CTM0002` | Reference-count overflow or invalid retain |
| `CTM0003` | Invalid release, underflow, or cleanup corruption |
| `CTI0001` | Integer division or remainder by zero |
| `CTS0001` | String length overflow |
| `CTS0002` | Native scalar formatting failure |
| `CTS0003` | Embedded NUL in a dynamic `NativeUtf8String` borrow |
| `CTO0001` | Invalid managed reference cast |
| `CTO0002` | Null unboxing |
| `CTO0003` | Boxed type mismatch |
| `CTE0001` | Unhandled C~ exception |
| `CTE0002` | Null thrown reference |
| `CTE0003` | C~ exception escaped a native export or callback barrier |
| `CTT0001` | Native entry or ARC operation occurred on an unattached thread or task |
| `CTT0002` | Attachment, detachment, task exit, or runtime shutdown violated the thread lifecycle |
| `CTB0001` | Native-buffer index out of range |
| `CTB0002` | Negative stack-allocation count |
| `CTB0003` | Stack-allocation size overflow |

Unsafe pointer dereference and indexing do not use these managed checks.

The existing null, array, allocation, division, string, cast, and unboxing failures remain fatal. They do not enter the exception handler stack.

## Lifetime

Class instances, arrays, dynamic strings, boxes, exception objects, and reference-bearing structure values use automatic reference counting. Strong-slot replacement retains the new value, moves out the old value, stores the new value, and releases the old value. Owned temporaries and locals register automatic cleanup records; transfer operations disarm the corresponding record. Parameters and `this` are borrowed, while managed and reference-bearing structure returns are owned.

`ct_retain` uses an atomic compare/exchange loop. `ct_release` performs a release decrement and an acquire fence before the zero-count thread pushes the object through `ReleaseNext` onto its thread-local LIFO worklist. A drain already in progress pushes newly dead objects onto that same worklist, so long destruction chains do not recurse on the C stack. Class drops cover the full base layout, array drops cover reference-bearing elements and `Data`, dynamic strings free `Data`, and boxes and structures recursively drop nested references. Matching generated retain helpers preserve nested structure ownership during by-value copies.

Exception frames, pending actions, defer captures, and ownership cleanup records use automatic storage and do not call `ct_alloc`. Static fields own their values until process termination; static and empty strings are immortal. Reference cycles leak. `CT_MEMORY_DIAGNOSTICS` enables conformance-only live-object and live-allocation counters without adding a production API or cost.
