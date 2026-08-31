#ifndef CTILDE_EXPORTS_3284E6F54169A5C0_H
#define CTILDE_EXPORTS_3284E6F54169A5C0_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#if defined(__cplusplus)
#define CT_ALIGNOF(type) alignof(type)
#elif defined(_MSC_VER)
#define CT_ALIGNOF(type) __alignof(type)
#else
#define CT_ALIGNOF(type) _Alignof(type)
#endif
#if defined(_MSC_VER)
#define CT_ALIGN(n) __declspec(align(n))
#else
#define CT_ALIGN(n) __attribute__((aligned(n)))
#endif
#if defined(_MSC_VER)
#define CT_ALIGNED_TYPEDEF(base, name, n) typedef __declspec(align(n)) base name
#else
#define CT_ALIGNED_TYPEDEF(base, name, n) typedef base name __attribute__((aligned(n)))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define CTILDE_RUNTIME_ABI_VERSION UINT32_C(16)

typedef struct ct_object ct_object;
typedef struct ct_panic_info { const char* Code; const char* File; int32_t Line; } ct_panic_info;
typedef void (*ct_panic_handler)(const ct_panic_info* info, void* context);
typedef struct ct_runtime_config { uint32_t Size; ct_panic_handler PanicHandler; void* PanicContext; } ct_runtime_config;

void ct_runtime_initialize(const ct_runtime_config* config);
void ct_runtime_shutdown(void);
void ct_thread_attach(void);
void ct_thread_detach(void);
void ct_retain(ct_object* value);
void ct_release(ct_object* value);

#if defined(CTILDE_CONFORMANCE)
void ct_runtime_test_fail_allocation_after(int32_t successful_allocations);
#endif


#undef CT_ALIGNED_TYPEDEF
#undef CT_ALIGN
#undef CT_ALIGNOF
#ifdef __cplusplus
}
#endif

#endif
