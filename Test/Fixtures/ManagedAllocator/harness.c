#define _POSIX_C_SOURCE 200809L
#include <assert.h>
#include <pthread.h>
#include <semaphore.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct ct_process ct_process;
typedef struct ct_module ct_module;
typedef struct ct_managed_module_descriptor_v3 { int Identity; } ct_managed_module_descriptor_v3;
typedef struct ct_allocation {
    struct ct_allocation *Previous, *Next;
    ct_process *Process;
    ct_module *Module;
    size_t Size;
    max_align_t Alignment;
} ct_allocation;
struct ct_module { const ct_managed_module_descriptor_v3 *Descriptor; bool Stopping; uint32_t LiveAllocations; };
struct ct_process {
    uint32_t RuntimeGate, InstanceCount;
    struct { ct_module *Module; } Instances[1];
    size_t HeapBytes, HeapLimit;
    ct_allocation *Allocations;
    pthread_mutex_t AllocationLock;
};
typedef struct ct_execution_context { ct_process *Process; uint32_t RuntimeOperationDepth; } ct_execution_context;
static _Thread_local ct_execution_context context;
static ct_execution_context *current_context(void) { return &context; }
static void await_forced_task_deletion(void) { abort(); }
#define CT_RUNTIME_GATE_STOPPED UINT32_C(0x80000000)
#define CT_RUNTIME_GATE_COUNT UINT32_C(0x7fffffff)
#define portENTER_CRITICAL(lock) assert(pthread_mutex_lock(lock) == 0)
#define portEXIT_CRITICAL(lock) assert(pthread_mutex_unlock(lock) == 0)

static bool inject_cas_aba;
static bool esp_cpu_compare_and_set(volatile uint32_t *value, uint32_t expected, uint32_t desired) {
    if (inject_cas_aba) {
        inject_cas_aba = false;
        /* Another writer changed the value and restored it before failure was observed. */
        return false;
    }
    return __atomic_compare_exchange_n(value, &expected, desired, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
}

static sem_t allocated, proceed;
static int pause_allocation, fail_allocation;
static void *test_calloc(size_t count, size_t size) {
    if (__atomic_exchange_n(&pause_allocation, 0, __ATOMIC_SEQ_CST)) {
        sem_post(&allocated);
        sem_wait(&proceed);
    }
    if (__atomic_load_n(&fail_allocation, __ATOMIC_SEQ_CST)) return NULL;
    return calloc(count, size);
}
typedef void *SemaphoreHandle_t;
static int live_semaphores;
static bool fail_semaphore, fail_resource;
static uintptr_t native_value;
static void (*native_release)(uintptr_t);
static SemaphoreHandle_t xSemaphoreCreateBinary(void) {
    if (fail_semaphore) return NULL;
    ++live_semaphores;
    return malloc(1);
}
static void vSemaphoreDelete(SemaphoreHandle_t value) { assert(live_semaphores > 0); --live_semaphores; free(value); }
static uintptr_t ctilde_managed_native_resource_register(uintptr_t value, void (*release)(uintptr_t)) {
    assert(context.RuntimeOperationDepth > 0);
    if (fail_resource) return 0;
    assert(!native_value);
    native_value = value; native_release = release;
    return 1;
}
static bool ctilde_managed_native_resource_release(uintptr_t token) {
    assert(token == 1 && native_value);
    native_release(native_value); native_value = 0;
    return true;
}
#define calloc test_calloc
#include "allocator_under_test.inc"
#undef calloc

static ct_managed_module_descriptor_v3 descriptor;
static ct_module module = { .Descriptor = &descriptor };
static ct_process process = { .InstanceCount = 1, .Instances = {{ &module }}, .AllocationLock = PTHREAD_MUTEX_INITIALIZER };
static void *reserved_allocation;
static void *reserve_worker(void *unused) {
    (void)unused;
    context.Process = &process;
    reserved_allocation = api_allocate(60, &descriptor);
    return NULL;
}
static void *stress_worker(void *unused) {
    (void)unused;
    context.Process = &process;
    for (int round = 0; round < 2000; ++round) {
        void *values[8];
        for (int i = 0; i < 8; ++i) { values[i] = api_allocate((size_t)(i + 1), &descriptor); assert(values[i]); }
        for (int i = 7; i >= 0; --i) api_free(values[i]);
    }
    assert(context.RuntimeOperationDepth == 0);
    return NULL;
}
int main(void) {
    uint32_t atomic_value = 5, expected = 5;
    inject_cas_aba = true;
    assert(ctilde_managed_atomic_compare_exchange_u32(&atomic_value, &expected, 7));
    assert(atomic_value == 7 && expected == 5 && !inject_cas_aba);
    expected = 5;
    assert(!ctilde_managed_atomic_compare_exchange_u32(&atomic_value, &expected, 9));
    assert(atomic_value == 7 && expected == 7);
    context.Process = &process;
    void *done = NULL;
    fail_allocation = 1;
    assert(!ctilde_managed_thread_payload_allocate(32, &done) && !done);
    fail_allocation = 0; fail_semaphore = true;
    assert(!ctilde_managed_thread_payload_allocate(32, &done) && !done);
    fail_semaphore = false; fail_resource = true;
    assert(!ctilde_managed_thread_payload_allocate(32, &done) && !done && !live_semaphores);
    fail_resource = false;
    void *payload = ctilde_managed_thread_payload_allocate(32, &done);
    assert(payload && done && live_semaphores == 1 && context.RuntimeOperationDepth == 0);
    ctilde_managed_thread_payload_free(payload);
    assert(!native_value && !live_semaphores && !process.RuntimeGate);
    assert(ctilde_managed_thread_payload_allocate(32, &done));
    /* Forced cleanup invokes the registered resident callback without a managed destructor. */
    native_release(native_value); native_value = 0;
    assert(!live_semaphores && !process.RuntimeGate);
    sem_init(&allocated, 0, 0); sem_init(&proceed, 0, 0);
    process.HeapLimit = 100;
    pause_allocation = 1;
    pthread_t worker;
    assert(pthread_create(&worker, NULL, reserve_worker, NULL) == 0);
    sem_wait(&allocated);
    assert(process.HeapBytes == 60);
    assert(api_allocate(60, &descriptor) == NULL);
    sem_post(&proceed); pthread_join(worker, NULL);
    assert(reserved_allocation); api_free(reserved_allocation);
    fail_allocation = 1;
    assert(api_allocate(30, &descriptor) == NULL && process.HeapBytes == 0);
    fail_allocation = 0;
    process.HeapLimit = 0;
    pthread_t workers[4];
    for (int i = 0; i < 4; ++i) assert(pthread_create(&workers[i], NULL, stress_worker, NULL) == 0);
    for (int i = 0; i < 4; ++i) pthread_join(workers[i], NULL);
    assert(process.Allocations == NULL && process.HeapBytes == 0 && module.LiveAllocations == 0 && process.RuntimeGate == 0);
    for (int i = 0; i < 10; ++i) assert(api_allocate(32, &descriptor));
    ct_allocation *previous = NULL;
    int count = 0;
    for (ct_allocation *entry = process.Allocations; entry; entry = entry->Next) { assert(entry->Previous == previous); previous = entry; ++count; }
    assert(count == 10 && process.HeapBytes == 320);
    process.RuntimeGate = CT_RUNTIME_GATE_STOPPED;
    assert(!begin_runtime_operation(&context));
    release_arena(&process);
    assert(process.Allocations == NULL && process.HeapBytes == 0 && module.LiveAllocations == 0);
    puts("{\"passed\":true,\"concurrentAllocations\":64000,\"quotaReservation\":true,\"failureRollback\":true,\"cleanup\":true,\"strongCasAba\":true,\"nativeThreadPayloadCleanup\":true}");
    return 0;
}
