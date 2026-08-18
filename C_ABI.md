# C backend and ABI

## Status

This document defines the generated C contract for C~ draft 0.3.

The output is a single C11 translation unit. The C source format is deterministic, but generated internal symbol names are a compiler ABI rather than a user-facing source API. Changes to this document require conformance tests.

## Target requirements

The generated file includes only C standard-library headers. Compile-time assertions require:

- Eight-bit bytes.
- Exact `int8_t`, `uint8_t`, `int16_t`, `uint16_t`, `int32_t`, and `uint32_t` types when used.
- Two's-complement `int32_t`.
- A four-byte IEEE-754 binary32 `float`.
- C11 language support.

References and unsafe pointers use native C pointer width. A 64-bit C target therefore uses 64-bit references and pointers. C~ scalar integer sizes do not change with the target.

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
| `float` | `float` |
| `T*` | the mapped C type followed by `*` |

Signed addition, subtraction, multiplication, negation, and shifts use generated helpers to avoid C signed-overflow undefined behavior. Draft 0.3 defines two's-complement wrapping. Integer division and remainder check zero before the C operation.

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
| `ct_l_` | User local |
| `ct_tmp_` | Lowering temporary |

Method names append a structural code for every parameter type. Overloads therefore have distinct C symbols without hashes.

All generated user definitions have translation-unit-local linkage. `public` and `internal` are C~ access rules; they do not export a native symbol. The exceptions are C `main` and declarations created by `[Extern]`.

## Classes and structures

A class lowers to a C structure and is used through a pointer:

```c
typedef struct ct_t_... ct_t_...;
struct ct_t_... {
    int32_t field;
};
```

`new` calls a constructor factory. The factory allocates zeroed storage, runs field initializers and the selected constructor body, and returns the object pointer.

A structure lowers to the same C structure form but is passed, returned, assigned, and stored by value. A structure constructor initializes a zeroed local value and returns that value.

Instance methods and property accessors receive a first `ct_self` pointer. Static members do not.

Draft 0.3 has no base-object header, virtual table, interface map, or run-time type information.

## Enumerations

An enum lowers to a typedef of its declared fixed-width underlying C type. Members lower to typed preprocessor constants so they remain valid C case labels.

The compiler validates each explicit and implicit enum value against the underlying range.

## Arrays

Every used element type receives one array structure:

```c
typedef struct ct_a_... {
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
    int32_t Length;
    const uint8_t* Data;
} ct_string;
```

`Length` counts UTF-8 code units. `Data` is followed by a zero byte for native boundary convenience, but embedded zero bytes are valid and all C~ operations use `Length`.

String literals use static byte arrays and string descriptors. Concatenation allocates a new descriptor and byte array. A null concatenation operand is treated as an empty string.

String equality compares contents. Other class and array equality compares pointer identity.

## Static initialization

Static storage is emitted with a C zero initializer. The generated `ct_module_init` function then evaluates explicit field initializers.

Types are initialized in ordinal fully qualified name order. Fields within one type use source declaration order. The generated C `main` calls `ct_module_init` exactly once before the C~ entry method.

The language does not expose the generated initialization function.

## Entry point

Exactly one method must have `[EntryPoint]`. It must be a body-bearing `static void` method with no parameters.

The generated wrapper is:

```c
int main(void)
{
    ct_keep_symbols();
    ct_module_init();
    mangled_entry_method();
    return EXIT_SUCCESS;
}
```

`ct_keep_symbols` references translation-unit-local functions and fields. It has no observable behavior. Its purpose is to keep strict GCC and Clang unused-symbol warnings from rejecting valid C~ programs.

## Extern methods

`[Extern("symbol")]` applies to a static, bodyless method. The symbol string must be a portable C identifier.

The compiler emits an external C prototype using the mappings in this document. The native definition must use exactly that ABI. Arrays, strings, classes, and structures are C~ runtime layouts, not libc substitutes.

An extern declaration is linked only when generated code calls it. The compiler does not choose libraries or invoke a linker.

## Runtime failures

The embedded runtime prints one line to standard error and exits with `EXIT_FAILURE`.

| Code | Failure |
| --- | --- |
| `CTN0001` | Managed null access |
| `CTA0001` | Negative array length |
| `CTA0002` | Array allocation-size overflow |
| `CTA0003` | Array or string index out of range |
| `CTM0001` | Allocation failure |
| `CTI0001` | Integer division or remainder by zero |
| `CTS0001` | String length overflow |

Unsafe pointer dereference and indexing do not use these managed checks.

## Lifetime

Class instances, arrays, concatenated strings, and their data use zero-initialized program-lifetime allocation. The generated runtime does not free them.

This preserves managed reference identity and removes use-after-free from safe C~ code. A future collector can replace the allocator without changing source semantics or object layouts described here.
