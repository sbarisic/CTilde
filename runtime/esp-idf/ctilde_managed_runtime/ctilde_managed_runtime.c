#include "ctilde_managed_runtime.h"

#include <errno.h>
#include <dirent.h>
#include <fcntl.h>
#include <inttypes.h>
#include <limits.h>
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include "esp_dlfcn.h"
#include "esp_cpu.h"
#include "esp_err.h"
#include "esp_elf.h"
#include "esp_heap_caps.h"
#include "esp_ipc.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/stream_buffer.h"
#include "freertos/task.h"
#include "private/elf_symbol.h"
#include "psa/crypto.h"
#include "soc/soc_caps.h"

#ifdef __getreent
#undef __getreent
#endif
extern void *__getreent(void);
extern double __extendsfdf2(float value);
extern uint64_t __udivdi3(uint64_t dividend, uint64_t divisor);
extern uint64_t __umoddi3(uint64_t dividend, uint64_t divisor);
extern uint64_t __ashldi3(uint64_t value, int shift);
/* Generated Atomic<T> helpers dispatch by width, including 64-bit operations.
   Use the IDF implementations so modules share the platform's atomic lock. */
extern uint64_t ct_idf_atomic_load_8(const volatile void *storage, int order) __asm__("__atomic_load_8");
extern bool ct_idf_atomic_compare_exchange_8(volatile void *storage, void *expected,
    uint64_t desired, bool weak, int success, int failure) __asm__("__atomic_compare_exchange_8");

#ifndef CONFIG_CTILDE_MANAGED_TLS_INDEX
#define CONFIG_CTILDE_MANAGED_TLS_INDEX 2
#endif
#ifndef CONFIG_CTILDE_MANAGED_MAX_MODULES
#define CONFIG_CTILDE_MANAGED_MAX_MODULES 16
#endif
#ifndef CONFIG_CTILDE_MANAGED_MAX_PROCESSES
#define CONFIG_CTILDE_MANAGED_MAX_PROCESSES 16
#endif
#ifndef CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH
#define CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH 64
#endif

static_assert(CONFIG_CTILDE_MANAGED_TLS_INDEX < CONFIG_FREERTOS_THREAD_LOCAL_STORAGE_POINTERS,
    "CONFIG_CTILDE_MANAGED_TLS_INDEX must select an allocated FreeRTOS TLS slot");
static_assert(CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH >= 16 && CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH <= 256,
    "CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH must be between 16 and 256");
static_assert(CTILDE_MANAGED_MODULE_NAME_CAPACITY == 64u,
    "Managed Module ABI 3 requires 64-byte name fields");
static_assert(CTILDE_MANAGED_MODULE_VERSION_CAPACITY == 32u,
    "Managed Module ABI 3 requires 32-byte version fields");

static void publish_executable_memory_on_cpu(void *unused)
{
    (void)unused;
#if CONFIG_IDF_TARGET_ARCH_XTENSA
    /* Xtensa requires ISYNC after a loader or self-modifying code changes
       instructions, including configurations whose IRAM has no I-cache. */
    __asm__ __volatile__("memw\n\tisync" ::: "memory");
#elif CONFIG_IDF_TARGET_ARCH_RISCV
    __asm__ __volatile__("fence.i" ::: "memory");
#else
#error Unsupported managed-module processor architecture
#endif
}

void ctilde_managed_memory_sample(const char *phase, uint32_t subject)
{
#ifdef CONFIG_CTILDE_MANAGED_MEMORY_TRACE
    if (phase == NULL) return;
    for (const char *cursor = phase; *cursor != '\0'; ++cursor) {
        if (!((*cursor >= 'a' && *cursor <= 'z') || (*cursor >= 'A' && *cursor <= 'Z') ||
              (*cursor >= '0' && *cursor <= '9') || *cursor == '_' || *cursor == '-')) return;
    }
    multi_heap_info_t bytes, executable;
    heap_caps_get_info(&bytes, MALLOC_CAP_8BIT);
    heap_caps_get_info(&executable, MALLOC_CAP_EXEC);
    ESP_LOGI("ctilde.memory", "CT_MEMORY {\"schemaVersion\":1,\"phase\":\"%s\","
        "\"subject\":%" PRIu32 ",\"timestampUs\":%" PRIi64 ",\"scope\":\"global\","
        "\"byteAddressable\":{\"freeBytes\":%zu,\"allocatedBytes\":%zu,\"largestBlockBytes\":%zu,"
        "\"minimumFreeBytes\":%zu,\"allocatedBlocks\":%zu},"
        "\"executable\":{\"freeBytes\":%zu,\"largestBlockBytes\":%zu}}",
        phase, subject, esp_timer_get_time(), bytes.total_free_bytes, bytes.total_allocated_bytes,
        bytes.largest_free_block, bytes.minimum_free_bytes, bytes.allocated_blocks,
        executable.total_free_bytes, executable.largest_free_block);
#else
    (void)phase;
    (void)subject;
#endif
}

static int publish_executable_memory(void)
{
    publish_executable_memory_on_cpu(NULL);
#if SOC_CPU_CORES_NUM > 1 && defined(CONFIG_ESP_IPC_ENABLE)
    const BaseType_t current_core = xPortGetCoreID();
    for (uint32_t core = 0; core < SOC_CPU_CORES_NUM; ++core) {
        if ((BaseType_t)core == current_core) continue;
        const esp_err_t result = esp_ipc_call_blocking(core, publish_executable_memory_on_cpu, NULL);
        if (result != ESP_OK) return -EIO;
    }
#endif
    return 0;
}

bool __attribute__((noinline)) ctilde_managed_atomic_compare_exchange_u32(
    volatile uint32_t *value, uint32_t *expected, uint32_t desired)
{
    if (value == NULL || expected == NULL) return false;
    const uint32_t requested = *expected;
    __atomic_thread_fence(__ATOMIC_SEQ_CST);
    for (;;) {
        const uint32_t observed = __atomic_load_n(value, __ATOMIC_SEQ_CST);
        if (observed != requested) {
            *expected = observed;
            return false;
        }
        if (esp_cpu_compare_and_set(value, requested, desired)) {
            __atomic_thread_fence(__ATOMIC_SEQ_CST);
            return true;
        }
        /* Retry an intervening write. A later load must not report success for
           a failed CAS merely because the value changed back to requested. */
    }
}

#define CT_MODULE_PATH_MAX 256
#define CT_MODULE_NAME_MAX CTILDE_MANAGED_MODULE_NAME_CAPACITY
#define CT_MODULE_VERSION_MAX CTILDE_MANAGED_MODULE_VERSION_CAPACITY
#define CT_MAX_DEPENDENCIES 16
#define CT_MAX_PROCESS_MODULES 16
#define CT_MAX_ARGUMENTS 32
#define CT_MAILBOX_DEPTH 16
#define CT_THREAD_STATE_BYTES 128
#define CT_PROCESS_PIPE_BYTES 8192
#define CT_MAX_CALL_DEPTH CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH
#define CT_OVERLAY_STAGING_WORDS 128u
#define CT_RUNTIME_GATE_STOPPED UINT32_C(0x80000000)
#define CT_RUNTIME_GATE_COUNT UINT32_C(0x7fffffff)
#define CT_TERMINATION_NONE UINT32_C(0)
#define CT_TERMINATION_PUBLISHING UINT32_C(1)
#define CT_TERMINATION_QUEUED UINT32_C(2)
#define CT_TERMINATION_POLL_MILLISECONDS UINT32_C(10)
#define CT_INITIALIZATION_UNINITIALIZED UINT32_C(0)
#define CT_INITIALIZATION_RUNNING UINT32_C(1)
#define CT_INITIALIZATION_READY UINT32_C(2)
#define CT_OVERLAY_NAME_CAPACITY 32u
#define CT_OVERLAY_RELOCATION_WINDOW 1u
#define CT_OVERLAY_RELOCATION_RESIDENT_EXECUTABLE 2u
#define CT_OVERLAY_RELOCATION_RESIDENT_DATA 3u
#define CT_OVERLAY_RELOCATION_RESIDENT_EXECUTABLE_INDIRECT 4u
#define CT_OVERLAY_RELOCATION_RESIDENT_DATA_INDIRECT 5u

static const char *TAG = "ctilde.modules";

typedef struct ct_object_header {
    const ct_type_descriptor *Type;
    uint32_t IdentityHash;
    uint32_t RefCount;
    void *ReleaseNext;
} ct_object_header;

typedef struct ct_managed_string {
    ct_object_header Object;
    int32_t Length;
    uint8_t Data[];
} ct_managed_string;

typedef struct ct_managed_array {
    ct_object_header Object;
    int32_t Length;
    uint8_t Data[];
} ct_managed_array;

struct ct_type_descriptor {
    const char *Name;
    const ct_type_descriptor *Base;
    const void *VTable;
    const void *Interfaces;
    uint32_t InterfaceCount;
    uint32_t TypeId;
    size_t Size;
    size_t Alignment;
    bool IsValue;
    void (*Drop)(void *);
    uint64_t FingerprintHigh;
    uint64_t FingerprintLow;
    const ct_type_ops *Ops;
};

typedef struct ct_binary_dependency_v1 {
    char Name[CT_MODULE_NAME_MAX];
    char Version[CT_MODULE_VERSION_MAX];
    uint8_t BuildIdentity[32];
    uint8_t ApiHash[32];
} ct_binary_dependency_v1;

typedef struct ct_binary_manifest_v1 {
    uint8_t Magic[8];
    uint32_t HeaderSize;
    uint32_t TotalSize;
    uint32_t RuntimeAbi;
    uint32_t ModuleAbi;
    uint32_t Architecture;
    uint32_t Kind;
    uint32_t DependencyCount;
    uint32_t MainTaskStackBytes;
    uint64_t HeapLimitBytes;
    char Name[CT_MODULE_NAME_MAX];
    char Version[CT_MODULE_VERSION_MAX];
    uint8_t BuildIdentity[32];
    uint8_t ApiHash[32];
    ct_binary_dependency_v1 Dependencies[];
} ct_binary_manifest_v1;

typedef struct ct_module ct_module;
typedef struct ct_process ct_process;

typedef struct ct_process_file {
    struct ct_process_file *Next;
    FILE *Stream;
    char Path[CT_MODULE_PATH_MAX];
} ct_process_file;

typedef struct ct_process_directory {
    struct ct_process_directory *Next;
    DIR *Directory;
    char Path[CT_MODULE_PATH_MAX];
    char *PendingName;
    uint8_t PendingKind;
    uint32_t PendingAttributes;
    int64_t PendingLength;
} ct_process_directory;

typedef struct ct_allocation {
    struct ct_allocation *Previous;
    struct ct_allocation *Next;
    ct_process *Process;
    ct_module *Module;
    size_t Size;
    max_align_t Alignment;
} ct_allocation;

typedef struct ct_message {
    size_t Length;
    uint8_t Data[];
} ct_message;

typedef enum ct_console_endpoint_kind {
    CT_CONSOLE_ENDPOINT_UART,
    CT_CONSOLE_ENDPOINT_PIPE,
} ct_console_endpoint_kind;

typedef struct ct_console_endpoint {
    ct_console_endpoint_kind Kind;
    volatile uint32_t References;
    volatile uint32_t Children;
    volatile uint32_t ForegroundProcess;
    volatile bool ParentClosed;
    volatile bool ChildrenClosed;
    StreamBufferHandle_t Buffer;
    SemaphoreHandle_t WriteLock;
    ct_process *Owner;
    uint8_t OwnerStream;
} ct_console_endpoint;

typedef struct ct_native_resource {
    struct ct_native_resource *Next;
    uintptr_t Token;
    uintptr_t Value;
    ct_managed_native_resource_release_fn Release;
} ct_native_resource;

typedef struct ct_termination_request {
    ct_process *Process;
    uint32_t GraceMilliseconds;
    int64_t RequestedAtMicroseconds;
} ct_termination_request;

typedef struct ct_pending_termination {
    ct_process *Process;
    int64_t DeadlineMicroseconds;
    TaskHandle_t Task;
    bool InfiniteGrace;
    bool DrainingOperations;
} ct_pending_termination;

typedef struct ct_module_instance {
    ct_module *Module;
    void *State;
    bool Initialized;
} ct_module_instance;

typedef struct ct_overlay_entry_v3 {
    uint32_t Id;
    uint32_t FileOffset;
    uint32_t StoredSize;
    uint32_t MemorySize;
    uint32_t Alignment;
    uint32_t RelocationStart;
    uint32_t RelocationCount;
    uint32_t FunctionStart;
    uint32_t FunctionCount;
    char Name[CT_OVERLAY_NAME_CAPACITY];
    uint8_t Sha256[32];
} ct_overlay_entry_v3;

typedef struct ct_overlay_function_v3 {
    uint32_t TargetIndex;
    uint32_t OverlayId;
    uint32_t BodyOffset;
} ct_overlay_function_v3;

typedef struct ct_overlay_relocation_v3 {
    uint32_t Offset;
    uint32_t Kind;
    uint32_t Target;
    int32_t Addend;
} ct_overlay_relocation_v3;

static_assert(sizeof(ct_overlay_entry_v3) == 100u, "overlay directory entry layout changed");
static_assert(sizeof(ct_overlay_function_v3) == 12u, "overlay function layout changed");
static_assert(sizeof(ct_overlay_relocation_v3) == 16u, "overlay relocation layout changed");

struct ct_module {
    bool Used;
    bool Loading;
    bool Stopping;
    char Path[CT_MODULE_PATH_MAX];
    char Name[CT_MODULE_NAME_MAX];
    char Version[CT_MODULE_VERSION_MAX];
    uint8_t BuildIdentity[32];
    uint8_t ApiHash[32];
    void *DynamicHandle;
    const ct_managed_module_descriptor_v4 *Descriptor;
    ct_module *Dependencies[CT_MAX_DEPENDENCIES];
    uint32_t DependencyCount;
    uint32_t References;
    uint32_t ActiveCalls;
    uint32_t LiveAllocations;
    FILE *OverlayStream;
    SemaphoreHandle_t OverlayLock;
    uint8_t *OverlayDirectory;
    ct_overlay_entry_v3 *Overlays;
    ct_overlay_function_v3 *OverlayFunctions;
    ct_overlay_relocation_v3 *OverlayRelocations;
    uint32_t OverlayCount;
    uint32_t OverlayFunctionCount;
    uint32_t OverlayRelocationCount;
    uint32_t OverlayRelocationFileOffset;
    uint32_t MaximumOverlayBytes;
    uint32_t DescriptorVma;
    uint32_t TextAnchorVma;
    uintptr_t ExecutableLoadBias;
    uintptr_t DataLoadBias;
    bool OverlayUnavailable;
};

typedef struct ct_child_task ct_child_task;

typedef struct ct_execution_context {
    ct_process *Process;
    ct_module *Module;
    void *ThreadState;
    ct_child_task *ChildTask;
    ct_module *PreviousModules[CT_MAX_CALL_DEPTH];
    uint32_t CallDepth;
    uint32_t RuntimeOperationDepth;
    uint8_t PrimaryThreadState[CT_THREAD_STATE_BYTES] __attribute__((aligned(8)));
} ct_execution_context;

struct ct_child_task {
    ct_child_task *Next;
    TaskHandle_t Handle;
    ct_execution_context Context;
};

struct ct_process {
    bool Used;
    bool Cleaned;
    bool Completed;
    bool CleanupQueued;
    uint32_t TerminationRequestState;
    bool TerminationDispatched;
    bool ForceDeleteIssued;
    uint32_t Id;
    uint32_t ParentId;
    volatile ct_managed_process_state State;
    volatile bool Cancellation;
    int32_t ExitCode;
    ct_module *Root;
    char RootName[CT_MODULE_NAME_MAX];
    TaskHandle_t MainTask;
    uint32_t TaskCount;
    bool HasOverlays;
    volatile uint32_t *OverlayWindow;
    size_t OverlayWindowSize;
    ct_module *LogicalOverlayModule;
    uint32_t LogicalOverlayId;
    ct_module *LoadedOverlayModule;
    uint32_t LoadedOverlayId;
    uint32_t OverlayGeneration;
    uint32_t RuntimeGate;
    size_t HeapBytes;
    size_t HeapLimit;
    ct_allocation *Allocations;
    portMUX_TYPE AllocationLock;
    ct_module_instance Instances[CT_MAX_PROCESS_MODULES];
    uint32_t InstanceCount;
    char *Arguments[CT_MAX_ARGUMENTS];
    size_t ArgumentLengths[CT_MAX_ARGUMENTS];
    int32_t ArgumentCount;
    char *CurrentDirectory;
    ct_process_file *Files;
    ct_process_directory *Directories;
    ct_native_resource *NativeResources;
    ct_child_task *ChildTasks;
    ct_execution_context Context;
    StaticSemaphore_t CompletionStorage;
    SemaphoreHandle_t Completion;
    StaticQueue_t MailboxStorage;
    uint8_t MailboxBuffer[CT_MAILBOX_DEPTH * sizeof(ct_message *)];
    QueueHandle_t Mailbox;
    ct_console_endpoint *Streams[3];
    bool OwnsParentStream[3];
};

typedef struct ct_type_registration {
    bool Used;
    uint64_t High;
    uint64_t Low;
    const ct_type_descriptor *Descriptor;
    ct_module *Owner;
} ct_type_registration;

static ct_module s_modules[CONFIG_CTILDE_MANAGED_MAX_MODULES];
static ct_process s_processes[CONFIG_CTILDE_MANAGED_MAX_PROCESSES];
static uint32_t s_published_process_ids[CONFIG_CTILDE_MANAGED_MAX_PROCESSES];
static ct_type_registration s_types[128];
static uint32_t s_next_process_id = 1;
static uintptr_t s_next_resource_token = 1u;
static ct_console_endpoint s_uart_streams[3];
static void (*s_uart_activity_hook)(void);
static StaticSemaphore_t s_registry_storage;
static SemaphoreHandle_t s_registry;
static StaticQueue_t s_reaper_queue_storage;
static uint8_t s_reaper_queue_buffer[CONFIG_CTILDE_MANAGED_MAX_PROCESSES * sizeof(ct_process *)];
static QueueHandle_t s_reaper_queue;
static StaticQueue_t s_termination_queue_storage;
static uint8_t s_termination_queue_buffer[CONFIG_CTILDE_MANAGED_MAX_PROCESSES * sizeof(ct_termination_request)];
static QueueHandle_t s_termination_queue;
static ct_pending_termination s_pending_terminations[CONFIG_CTILDE_MANAGED_MAX_PROCESSES];
static bool s_initialized;
static uint32_t s_initialization_state;

static void cleanup_process(ct_process *process, bool forced);
static void release_module(ct_module *module);
static ct_process *process_from_handle(uintptr_t handle);
static void *api_current_thread_state(void);
static void api_set_thread_state(void *state);
static void tls_deleted(int index, void *value);

static ct_execution_context *current_context(void)
{
    return (ct_execution_context *)pvTaskGetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX);
}

static ct_process *current_process(void)
{
    ct_execution_context *context = current_context();
    return context == NULL ? NULL : context->Process;
}

static ct_console_endpoint *create_pipe_endpoint(ct_process *owner, uint8_t stream)
{
    ct_console_endpoint *endpoint = calloc(1u, sizeof(*endpoint));
    if (endpoint == NULL) return NULL;
    endpoint->Kind = CT_CONSOLE_ENDPOINT_PIPE;
    endpoint->References = 2u; /* child side plus the exposed parent side */
    endpoint->Children = 1u;
    endpoint->Owner = owner;
    endpoint->OwnerStream = stream;
    endpoint->Buffer = xStreamBufferCreate(CT_PROCESS_PIPE_BYTES, 1u);
    endpoint->WriteLock = xSemaphoreCreateMutex();
    if (endpoint->Buffer == NULL || endpoint->WriteLock == NULL) {
        if (endpoint->Buffer != NULL) vStreamBufferDelete(endpoint->Buffer);
        if (endpoint->WriteLock != NULL) vSemaphoreDelete(endpoint->WriteLock);
        free(endpoint);
        return NULL;
    }
    return endpoint;
}

static void destroy_endpoint(ct_console_endpoint *endpoint)
{
    if (endpoint == NULL || endpoint->Kind == CT_CONSOLE_ENDPOINT_UART) return;
    if (endpoint->Owner != NULL && endpoint->OwnerStream < 3u &&
        endpoint->Owner->Streams[endpoint->OwnerStream] == endpoint)
        endpoint->Owner->Streams[endpoint->OwnerStream] = NULL;
    if (endpoint->Buffer != NULL) vStreamBufferDelete(endpoint->Buffer);
    if (endpoint->WriteLock != NULL) vSemaphoreDelete(endpoint->WriteLock);
    free(endpoint);
}

static ct_console_endpoint *retain_endpoint(ct_console_endpoint *endpoint)
{
    if (endpoint == NULL) return NULL;
    if (endpoint->Kind == CT_CONSOLE_ENDPOINT_UART) return endpoint;
    (void)__atomic_add_fetch(&endpoint->References, 1u, __ATOMIC_ACQ_REL);
    (void)__atomic_add_fetch(&endpoint->Children, 1u, __ATOMIC_ACQ_REL);
    return endpoint;
}

static void release_endpoint_reference(ct_console_endpoint *endpoint)
{
    if (endpoint == NULL || endpoint->Kind == CT_CONSOLE_ENDPOINT_UART) return;
    if (__atomic_fetch_sub(&endpoint->References, 1u, __ATOMIC_ACQ_REL) == 1u)
        destroy_endpoint(endpoint);
}

static void release_endpoint_child(ct_process *process, size_t stream)
{
    ct_console_endpoint *endpoint = process->Streams[stream];
    if (endpoint == NULL) return;
    if (endpoint->Kind == CT_CONSOLE_ENDPOINT_UART) {
        if (stream == 0u && __atomic_load_n(&endpoint->ForegroundProcess, __ATOMIC_ACQUIRE) == process->Id) {
            ct_process *parent = process_from_handle((uintptr_t)process->ParentId);
            const uint32_t replacement = parent != NULL && parent->Streams[0] == endpoint ? parent->Id : 0u;
            __atomic_store_n(&endpoint->ForegroundProcess, replacement, __ATOMIC_RELEASE);
        }
        process->Streams[stream] = NULL;
        return;
    }
    uint32_t children = __atomic_fetch_sub(&endpoint->Children, 1u, __ATOMIC_ACQ_REL);
    if (children == 0u) abort();
    if (children == 1u) __atomic_store_n(&endpoint->ChildrenClosed, true, __ATOMIC_RELEASE);
    if (stream == 0u && __atomic_load_n(&endpoint->ForegroundProcess, __ATOMIC_ACQUIRE) == process->Id) {
        ct_process *parent = process_from_handle((uintptr_t)process->ParentId);
        const uint32_t replacement = parent != NULL && parent->Streams[0] == endpoint ? parent->Id : 0u;
        __atomic_store_n(&endpoint->ForegroundProcess, replacement, __ATOMIC_RELEASE);
    }
    if (!process->OwnsParentStream[stream]) process->Streams[stream] = NULL;
    release_endpoint_reference(endpoint);
}

static bool process_is_descendant_of(const ct_process *process, uint32_t ancestor)
{
    uint32_t parent = process == NULL ? 0u : process->ParentId;
    for (size_t depth = 0; parent != 0u && depth < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++depth) {
        if (parent == ancestor) return true;
        ct_process *candidate = process_from_handle((uintptr_t)parent);
        if (candidate == NULL || candidate == process) break;
        process = candidate;
        parent = process->ParentId;
    }
    return false;
}

static bool begin_runtime_operation(ct_execution_context *context)
{
    if (context == NULL || context->Process == NULL) return false;
    ct_process *process = context->Process;
    uint32_t gate = __atomic_load_n(&process->RuntimeGate, __ATOMIC_ACQUIRE);
    for (;;) {
        if (context->RuntimeOperationDepth == 0 && (gate & CT_RUNTIME_GATE_STOPPED) != 0) return false;
        if ((gate & CT_RUNTIME_GATE_COUNT) == CT_RUNTIME_GATE_COUNT) abort();
        if (__atomic_compare_exchange_n(&process->RuntimeGate, &gate, gate + 1u, false,
                __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
            context->RuntimeOperationDepth++;
            return true;
        }
    }
}

static void end_runtime_operation(ct_execution_context *context)
{
    if (context == NULL || context->Process == NULL || context->RuntimeOperationDepth == 0) abort();
    context->RuntimeOperationDepth--;
    const uint32_t gate = __atomic_fetch_sub(&context->Process->RuntimeGate, 1u, __ATOMIC_ACQ_REL);
    if ((gate & CT_RUNTIME_GATE_COUNT) == 0) abort();
}

static void abandon_runtime_operations(ct_execution_context *context)
{
    if (context == NULL || context->Process == NULL || context->RuntimeOperationDepth == 0) return;
    const uint32_t depth = context->RuntimeOperationDepth;
    context->RuntimeOperationDepth = 0;
    const uint32_t gate = __atomic_fetch_sub(&context->Process->RuntimeGate, depth, __ATOMIC_ACQ_REL);
    if ((gate & CT_RUNTIME_GATE_COUNT) < depth) abort();
}

static void await_forced_task_deletion(void)
{
    for (;;) vTaskDelay(portMAX_DELAY);
}

static bool enter_context_call(ct_execution_context *context, ct_module *module)
{
    if (context == NULL || module == NULL || context->CallDepth >= CT_MAX_CALL_DEPTH) return false;
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    const uint32_t depth = context->CallDepth++;
    context->PreviousModules[depth] = context->Module;
    (void)__atomic_add_fetch(&module->ActiveCalls, 1u, __ATOMIC_ACQ_REL);
    context->Module = module;
    end_runtime_operation(context);
    return true;
}

static bool leave_context_call(ct_execution_context *context, ct_module *module)
{
    if (context == NULL || context->CallDepth == 0 || context->Module != module) return false;
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    const uint32_t depth = --context->CallDepth;
    if (__atomic_fetch_sub(&module->ActiveCalls, 1u, __ATOMIC_ACQ_REL) == 0) abort();
    context->Module = context->PreviousModules[depth];
    context->PreviousModules[depth] = NULL;
    end_runtime_operation(context);
    return true;
}

static void abandon_context_calls(ct_execution_context *context)
{
    if (context == NULL) return;
    while (context->CallDepth != 0) {
        const uint32_t depth = --context->CallDepth;
        ct_module *module = context->Module;
        if (module == NULL || __atomic_fetch_sub(&module->ActiveCalls, 1u, __ATOMIC_ACQ_REL) == 0) abort();
        context->Module = context->PreviousModules[depth];
        context->PreviousModules[depth] = NULL;
    }
}

static void set_error(char *error, size_t capacity, const char *message)
{
    if (error != NULL && capacity != 0) {
        (void)snprintf(error, capacity, "%s", message);
    }
}

static bool contained_string(const char *value, size_t capacity)
{
    return value != NULL && memchr(value, '\0', capacity) != NULL && value[0] != '\0';
}

static bool ascii_letter(char value)
{
    return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
}

static bool ascii_digit(char value)
{
    return value >= '0' && value <= '9';
}

static bool ascii_alphanumeric(char value)
{
    return ascii_letter(value) || ascii_digit(value);
}

static bool canonical_module_name(const char *value, size_t capacity)
{
    if (!contained_string(value, capacity) || !ascii_letter(*value)) return false;
    for (const char *cursor = value + 1; *cursor != '\0'; ++cursor) {
        if (ascii_alphanumeric(*cursor)) continue;
        if (*cursor != '.' || !ascii_letter(cursor[1])) return false;
        ++cursor;
    }
    return true;
}

static bool canonical_overlay_name(const char *value, size_t capacity)
{
    if (!contained_string(value, capacity) || !ascii_letter(*value)) return false;
    for (const char *cursor = value + 1; *cursor != '\0'; ++cursor)
        if (!ascii_alphanumeric(*cursor) && *cursor != '_' && *cursor != '-') return false;
    return true;
}

static bool exact_module_version(const char *value, size_t capacity)
{
    if (!contained_string(value, capacity)) return false;
    const char *cursor = value;
    for (uint32_t part = 0; part < 3; ++part) {
        if (!ascii_digit(*cursor) || (*cursor == '0' && ascii_digit(cursor[1]))) return false;
        do { ++cursor; } while (ascii_digit(*cursor));
        if (part != 2) {
            if (*cursor != '.') return false;
            ++cursor;
        }
    }
    if (*cursor == '-') {
        const char *start = ++cursor;
        while (ascii_alphanumeric(*cursor) || *cursor == '.' || *cursor == '-') ++cursor;
        if (cursor == start) return false;
    }
    if (*cursor == '+') {
        const char *start = ++cursor;
        while (ascii_alphanumeric(*cursor) || *cursor == '.' || *cursor == '-') ++cursor;
        if (cursor == start) return false;
    }
    return *cursor == '\0';
}

static uint16_t read_u16_le(const uint8_t *value)
{
    return (uint16_t)value[0] | (uint16_t)((uint16_t)value[1] << 8);
}

static uint32_t read_u32_le(const uint8_t *value)
{
    return (uint32_t)value[0] | ((uint32_t)value[1] << 8) | ((uint32_t)value[2] << 16) | ((uint32_t)value[3] << 24);
}

static bool byte_range(size_t offset, size_t size, size_t total)
{
    return offset <= total && size <= total - offset;
}

static int resolve_module_path_bytes(const uint8_t *data, size_t length, char output[CT_MODULE_PATH_MAX])
{
    if (data == NULL || length == 0 || length >= CT_MODULE_PATH_MAX || memchr(data, 0, length) != NULL) {
        return -EINVAL;
    }
    char relative[CT_MODULE_PATH_MAX];
    (void)memcpy(relative, data, length);
    relative[length] = '\0';
    const char *suffix = relative;
    if (relative[0] == '/') {
        const size_t sd_length = strlen(CTILDE_MANAGED_MODULE_SD_ROOT);
        const size_t fallback_length = strlen(CTILDE_MANAGED_MODULE_FALLBACK_ROOT);
        const bool under_sd = strncmp(relative, CTILDE_MANAGED_MODULE_SD_ROOT, sd_length) == 0 && relative[sd_length] == '/';
        const bool under_fallback = strncmp(relative, CTILDE_MANAGED_MODULE_FALLBACK_ROOT, fallback_length) == 0 && relative[fallback_length] == '/';
        if (!under_sd && !under_fallback) {
            return -EPERM;
        }
        suffix = relative + (under_sd ? sd_length : fallback_length) + 1;
    }
    /* Espressif's current dlopen registry keys modules by basename. Restrict
       callers to one unambiguous loader namespace so preflight and relocation
       can never select different files with the same leaf name. */
    if (*suffix == '\0' || strchr(suffix, '/') != NULL || strchr(suffix, '\\') != NULL) {
        return -EINVAL;
    }
    if (strcmp(suffix, ".") == 0 || strcmp(suffix, "..") == 0) return -EPERM;
    if (relative[0] == '/') {
        (void)memcpy(output, relative, length + 1);
        return 0;
    }
    char sd_path[CT_MODULE_PATH_MAX];
    int written = snprintf(sd_path, sizeof(sd_path), "%s/%s", CTILDE_MANAGED_MODULE_SD_ROOT, suffix);
    if (written <= 0 || written >= (int)sizeof(sd_path)) return -ENAMETOOLONG;
    struct stat info;
    if (stat(sd_path, &info) == 0) {
        (void)memcpy(output, sd_path, (size_t)written + 1);
        return 0;
    }
    /* Only absence selects the LittleFS fallback. Permission and media errors
       must surface so a present but unreadable SD module is never hidden by a
       different fallback binary. */
    const int sd_error = errno;
    if (sd_error != ENOENT && sd_error != ENOTDIR && sd_error != ENODEV) return -sd_error;
    written = snprintf(output, CT_MODULE_PATH_MAX, "%s/%s", CTILDE_MANAGED_MODULE_FALLBACK_ROOT, suffix);
    return written > 0 && written < CT_MODULE_PATH_MAX ? 0 : -ENAMETOOLONG;
}

static int read_file_range(FILE *file, size_t offset, void *buffer, size_t length)
{
    if (offset > LONG_MAX || fseek(file, (long)offset, SEEK_SET) != 0) return -EIO;
    return fread(buffer, 1, length, file) == length ? 0 : -EIO;
}

static int read_manifest(const char *path, ct_binary_manifest_v1 **result, uint8_t **owned, size_t *file_size)
{
    *result = NULL;
    *owned = NULL;
    FILE *file = fopen(path, "rb");
    if (file == NULL) return -errno;
    if (fseek(file, 0, SEEK_END) != 0) { fclose(file); return -EIO; }
    const long length = ftell(file);
    if (length < 52 || length > 4 * 1024 * 1024) { fclose(file); return -ENOEXEC; }

    uint8_t elf_header[52];
    int io_result = read_file_range(file, 0, elf_header, sizeof(elf_header));
    if (io_result != 0) { fclose(file); return io_result; }
    if (elf_header[0] != 0x7f || elf_header[1] != 'E' || elf_header[2] != 'L' || elf_header[3] != 'F' ||
        elf_header[4] != 1 || elf_header[5] != 1 || elf_header[6] != 1) {
        fclose(file); return -ENOEXEC;
    }
    const uint16_t elf_type = read_u16_le(elf_header + 16);
    const uint16_t machine = read_u16_le(elf_header + 18);
#if CONFIG_IDF_TARGET_ARCH_XTENSA
    const uint16_t expected_machine = 94;
    const uint32_t expected_architecture = 1;
#else
    const uint16_t expected_machine = 243;
    const uint32_t expected_architecture = 2;
#endif
    if (elf_type != 3 || machine != expected_machine || read_u16_le(elf_header + 40) != 52) {
        fclose(file); return -ENOEXEC;
    }

    const size_t section_table = read_u32_le(elf_header + 32);
    const uint16_t section_entry_size = read_u16_le(elf_header + 46);
    const uint16_t section_count = read_u16_le(elf_header + 48);
    const uint16_t section_names_index = read_u16_le(elf_header + 50);
    if (section_entry_size != 40 || section_count == 0 || section_names_index >= section_count ||
        !byte_range(section_table, (size_t)section_entry_size * section_count, (size_t)length)) {
        fclose(file); return -ENOEXEC;
    }

    uint8_t section_header[40];
    io_result = read_file_range(file,
        section_table + (size_t)section_entry_size * section_names_index,
        section_header,
        sizeof(section_header));
    if (io_result != 0) { fclose(file); return io_result; }
    const size_t names_offset = read_u32_le(section_header + 16);
    const size_t names_size = read_u32_le(section_header + 20);
    if (read_u32_le(section_header + 4) != 3 || !byte_range(names_offset, names_size, (size_t)length)) {
        fclose(file); return -ENOEXEC;
    }

    static const char manifest_name[] = ".ctilde.manifest";
    bool found_manifest = false;
    size_t manifest_section_offset = 0;
    size_t manifest_section_size = 0;
    for (uint16_t index = 0; index < section_count; ++index) {
        io_result = read_file_range(file,
            section_table + (size_t)section_entry_size * index,
            section_header,
            sizeof(section_header));
        if (io_result != 0) { fclose(file); return io_result; }
        const size_t name_offset = read_u32_le(section_header);
        if (name_offset >= names_size) { fclose(file); return -ENOEXEC; }
        if (names_size - name_offset < sizeof(manifest_name)) continue;
        char name[sizeof(manifest_name)];
        io_result = read_file_range(file, names_offset + name_offset, name, sizeof(name));
        if (io_result != 0) { fclose(file); return io_result; }
        if (memcmp(name, manifest_name, sizeof(manifest_name)) != 0) continue;
        if (found_manifest || read_u32_le(section_header + 4) != 1) { fclose(file); return -ENOEXEC; }
        const size_t offset = read_u32_le(section_header + 16);
        const size_t size = read_u32_le(section_header + 20);
        if (!byte_range(offset, size, (size_t)length)) { fclose(file); return -ENOEXEC; }
        found_manifest = true;
        manifest_section_offset = offset;
        manifest_section_size = size;
    }

    const size_t fixed_size = offsetof(ct_binary_manifest_v1, Dependencies);
    if (!found_manifest || manifest_section_size < fixed_size) { fclose(file); return -ENOEXEC; }
    ct_binary_manifest_v1 header;
    (void)memset(&header, 0, sizeof(header));
    io_result = read_file_range(file, manifest_section_offset, &header, fixed_size);
    if (io_result != 0) { fclose(file); return io_result; }
    static const uint8_t magic[5] = { 'C', 'T', 'M', 'O', 'D' };
    if (memcmp(header.Magic, magic, sizeof(magic)) != 0 ||
        header.ModuleAbi > UINT8_MAX || header.Magic[5] != (uint8_t)header.ModuleAbi ||
        header.Magic[6] != 0u || header.Magic[7] != 0u || header.HeaderSize != fixed_size ||
        header.DependencyCount > CT_MAX_DEPENDENCIES) {
        fclose(file); return -ENOEXEC;
    }
    const size_t required_size = fixed_size + (size_t)header.DependencyCount * sizeof(ct_binary_dependency_v1);
    if (header.TotalSize != required_size || required_size > manifest_section_size) {
        fclose(file); return -ENOEXEC;
    }
    ct_binary_manifest_v1 *manifest = (ct_binary_manifest_v1 *)malloc(required_size);
    if (manifest == NULL) { fclose(file); return -ENOMEM; }
    io_result = read_file_range(file, manifest_section_offset, manifest, required_size);
    fclose(file);
    if (io_result != 0) { free(manifest); return io_result; }

    if (manifest->RuntimeAbi != CTILDE_RUNTIME_ABI_VERSION || manifest->ModuleAbi != CTILDE_MANAGED_MODULE_ABI_VERSION) {
        ESP_LOGE(TAG, "Module ABI mismatch: found runtime=%u module=%u; firmware requires runtime=%u module=%u. Rebuild all modules.",
            (unsigned)manifest->RuntimeAbi, (unsigned)manifest->ModuleAbi,
            (unsigned)CTILDE_RUNTIME_ABI_VERSION, (unsigned)CTILDE_MANAGED_MODULE_ABI_VERSION);
        free(manifest); return -EPROTONOSUPPORT;
    }
    if (manifest->Architecture != expected_architecture ||
        (manifest->Kind != 1 && manifest->Kind != 2) ||
        !canonical_module_name(manifest->Name, sizeof(manifest->Name)) ||
        !exact_module_version(manifest->Version, sizeof(manifest->Version)) ||
        manifest->MainTaskStackBytes < 2048 || manifest->MainTaskStackBytes % 16 != 0 ||
        manifest->HeapLimitBytes > SIZE_MAX) {
        free(manifest); return -ENOEXEC;
    }
    for (uint32_t index = 0; index < manifest->DependencyCount; ++index) {
        const ct_binary_dependency_v1 *dependency = &manifest->Dependencies[index];
        if (!canonical_module_name(dependency->Name, sizeof(dependency->Name)) ||
            !exact_module_version(dependency->Version, sizeof(dependency->Version)) ||
            strcmp(dependency->Name, manifest->Name) == 0) {
            free(manifest); return -ENOEXEC;
        }
        for (uint32_t previous = 0; previous < index; ++previous) {
            if (strcmp(manifest->Dependencies[previous].Name, dependency->Name) == 0) {
                free(manifest); return -ENOEXEC;
            }
        }
    }
    *result = manifest;
    *owned = (uint8_t *)(void *)manifest;
    if (file_size != NULL) *file_size = (size_t)length;
    return 0;
}

int ctilde_managed_preflight(const char *path, char *error, size_t error_capacity)
{
    char resolved[CT_MODULE_PATH_MAX];
    const int path_result = resolve_module_path_bytes((const uint8_t *)path, path == NULL ? 0 : strlen(path), resolved);
    if (path_result != 0) {
        set_error(error, error_capacity, "module path must be a bare name or a direct child of /sd/modules or /storage/modules");
        return path_result;
    }
    ct_binary_manifest_v1 *manifest;
    uint8_t *bytes;
    const int result = read_manifest(resolved, &manifest, &bytes, NULL);
    if (result != 0) set_error(error, error_capacity, result == -EPROTONOSUPPORT
        ? "module ABI mismatch; rebuild all modules for Runtime ABI 23 / Module ABI 4"
        : "invalid or incompatible managed ELF manifest");
    free(bytes);
    return result;
}

static ct_module *find_module(const char *name)
{
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_MODULES; ++index) {
        if (s_modules[index].Used && strcmp(s_modules[index].Name, name) == 0) return &s_modules[index];
    }
    return NULL;
}

static ct_module *allocate_module_slot(void)
{
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_MODULES; ++index) {
        if (!s_modules[index].Used) {
            (void)memset(&s_modules[index], 0, sizeof(s_modules[index]));
            s_modules[index].Used = true;
            s_modules[index].Loading = true;
            return &s_modules[index];
        }
    }
    return NULL;
}

static bool exact_dependency(const ct_binary_dependency_v1 *expected, const ct_module *actual)
{
    return strcmp(expected->Name, actual->Name) == 0 && strcmp(expected->Version, actual->Version) == 0 &&
        memcmp(expected->BuildIdentity, actual->BuildIdentity, 32) == 0 && memcmp(expected->ApiHash, actual->ApiHash, 32) == 0;
}

static bool hash_matches_text(const uint8_t expected[32], const char *actual)
{
    static const char digits[] = "0123456789abcdef";
    if (actual == NULL || strlen(actual) != 64) return false;
    for (size_t index = 0; index < 32; ++index) {
        if (actual[index * 2] != digits[expected[index] >> 4] || actual[index * 2 + 1] != digits[expected[index] & 15]) return false;
    }
    return true;
}

static bool descriptor_matches_manifest(const ct_managed_module_descriptor_v4 *descriptor, const ct_binary_manifest_v1 *manifest)
{
    if (descriptor == NULL || descriptor->Size != sizeof(*descriptor) ||
        descriptor->RuntimeAbi != CTILDE_RUNTIME_ABI_VERSION || descriptor->ModuleAbi != CTILDE_MANAGED_MODULE_ABI_VERSION ||
        descriptor->Kind != manifest->Kind || descriptor->Name == NULL || descriptor->Version == NULL ||
        strcmp(descriptor->Name, manifest->Name) != 0 || strcmp(descriptor->Version, manifest->Version) != 0 ||
        !hash_matches_text(manifest->BuildIdentity, descriptor->BuildIdentity) ||
        !hash_matches_text(manifest->ApiHash, descriptor->ApiHash) ||
        descriptor->DependencyCount != manifest->DependencyCount || descriptor->ExportCount > 256u || descriptor->ImportCount > 256u ||
        descriptor->CallTargetCount > 4096u ||
        (descriptor->DependencyCount != 0 && descriptor->Dependencies == NULL) ||
        (descriptor->ExportCount != 0 && descriptor->Exports == NULL) ||
        (descriptor->ImportCount != 0 && descriptor->Imports == NULL) ||
        (descriptor->CallTargetCount != 0 && descriptor->CallTargets == NULL) ||
        descriptor->HasOverlays > 1u || (descriptor->HasOverlays == 0u && descriptor->MaximumOverlayBytes != 0u) ||
        descriptor->MainTaskStackBytes != manifest->MainTaskStackBytes ||
        descriptor->HeapLimitBytes != manifest->HeapLimitBytes ||
        descriptor->StaticStateAlignment == 0 || descriptor->StaticStateAlignment > _Alignof(max_align_t) ||
        (descriptor->StaticStateAlignment & (descriptor->StaticStateAlignment - 1)) != 0 ||
        descriptor->Initialize == NULL || descriptor->Finalize == NULL || descriptor->CreateBytes == NULL ||
        (descriptor->Kind == 1 && (descriptor->Main == NULL || descriptor->CreateArguments == NULL)) ||
        (descriptor->Kind == 2 && (descriptor->Main != NULL || descriptor->CreateArguments != NULL))) return false;
    for (uint32_t index = 0; index < manifest->DependencyCount; ++index) {
        const ct_binary_dependency_v1 *expected = &manifest->Dependencies[index];
        const ct_managed_dependency_v2 *actual = &descriptor->Dependencies[index];
        if (actual->Name == NULL || actual->Version == NULL || strcmp(actual->Name, expected->Name) != 0 ||
            strcmp(actual->Version, expected->Version) != 0 ||
            !hash_matches_text(expected->BuildIdentity, actual->BuildIdentity) ||
            !hash_matches_text(expected->ApiHash, actual->ApiHash)) return false;
    }
    for (uint32_t index = 0; index < descriptor->ExportCount; ++index) {
        const ct_managed_export_v2 *exported = &descriptor->Exports[index];
        if (exported->Identity == NULL || strlen(exported->Identity) != 64u || exported->Address == NULL) return false;
        for (uint32_t previous = 0; previous < index; ++previous)
            if (strcmp(descriptor->Exports[previous].Identity, exported->Identity) == 0) return false;
    }
    for (uint32_t index = 0; index < descriptor->ImportCount; ++index) {
        const ct_managed_import_v4 *imported = &descriptor->Imports[index];
        if (imported->Dependency == NULL || imported->Identity == NULL || strlen(imported->Identity) != 64u ||
            imported->AddressSlot == NULL || imported->ModuleSlot == NULL) return false;
        bool dependency_found = false;
        for (uint32_t dependency = 0; dependency < descriptor->DependencyCount; ++dependency)
            if (strcmp(descriptor->Dependencies[dependency].Name, imported->Dependency) == 0) dependency_found = true;
        if (!dependency_found) return false;
        for (uint32_t previous = 0; previous < index; ++previous)
            if (strcmp(descriptor->Imports[previous].Dependency, imported->Dependency) == 0 &&
                strcmp(descriptor->Imports[previous].Identity, imported->Identity) == 0) return false;
    }
    bool found_overlay_target = false;
    for (uint32_t index = 0; index < descriptor->CallTargetCount; ++index) {
        const ct_managed_call_target_v4 *target = &descriptor->CallTargets[index];
        if (target->Size != sizeof(*target) || target->Reserved != 0u || target->Placement > 1u ||
            (target->Placement == 0u && (target->OverlayId != 0u || target->Body == (uintptr_t)0)) ||
            (target->Placement == 1u && target->OverlayId == 0u)) return false;
        found_overlay_target |= target->Placement == 1u;
    }
    if (found_overlay_target != (descriptor->HasOverlays != 0u)) return false;
    return true;
}

static int bind_module_imports(ct_module *module)
{
    const ct_managed_module_descriptor_v4 *descriptor = module->Descriptor;
    for (uint32_t index = 0; index < descriptor->ImportCount; ++index) {
        const ct_managed_import_v4 *imported = &descriptor->Imports[index];
        ct_module *provider = NULL;
        for (uint32_t dependency = 0; dependency < module->DependencyCount; ++dependency) {
            if (strcmp(module->Dependencies[dependency]->Name, imported->Dependency) == 0) {
                provider = module->Dependencies[dependency];
                break;
            }
        }
        if (provider == NULL || provider->Descriptor == NULL) return -ELIBBAD;
        const ct_managed_export_v2 *resolved = NULL;
        for (uint32_t exported = 0; exported < provider->Descriptor->ExportCount; ++exported) {
            const ct_managed_export_v2 *candidate = &provider->Descriptor->Exports[exported];
            if (strcmp(candidate->Identity, imported->Identity) == 0) {
                resolved = candidate;
                break;
            }
        }
        if (resolved == NULL) return -ELIBBAD;
        *imported->AddressSlot = resolved->Address;
        *imported->ModuleSlot = provider->Descriptor;
    }
    return 0;
}

static uint32_t overlay_read_u32(const uint8_t *value)
{
    return (uint32_t)value[0] | ((uint32_t)value[1] << 8) | ((uint32_t)value[2] << 16) | ((uint32_t)value[3] << 24);
}

static int load_overlay_directory(ct_module *module, const char *path)
{
    static const uint8_t footer_magic[8] = { 'C', 'T', 'O', 'V', 'L', 'F', '3', 0 };
    static const uint8_t directory_magic[8] = { 'C', 'T', 'O', 'V', 'L', 'D', '3', 0 };
    FILE *stream = fopen(path, "rb");
    if (stream == NULL) return -errno;
    if (fseeko(stream, 0, SEEK_END) != 0) { const int error = errno; fclose(stream); return -error; }
    const off_t file_size = ftello(stream);
    uint8_t footer[24];
    if (file_size < (off_t)sizeof(footer) || file_size > 4 * 1024 * 1024 ||
        fseeko(stream, file_size - (off_t)sizeof(footer), SEEK_SET) != 0 ||
        fread(footer, 1, sizeof(footer), stream) != sizeof(footer) || memcmp(footer, footer_magic, 8) != 0) {
        fclose(stream);
        return 0;
    }
    const uint32_t directory_offset = overlay_read_u32(footer + 8);
    const uint32_t directory_size = overlay_read_u32(footer + 12);
    const uint32_t resident_size = overlay_read_u32(footer + 16);
    const uint32_t footer_overlay_count = overlay_read_u32(footer + 20);
    if (directory_size < 40u || directory_offset < resident_size ||
        (uint64_t)directory_offset + directory_size + sizeof(footer) != (uint64_t)file_size) {
        fclose(stream);
        return -ENOEXEC;
    }
    uint8_t header[40];
    if (fseeko(stream, (off_t)directory_offset, SEEK_SET) != 0 ||
        fread(header, 1, sizeof(header), stream) != sizeof(header) ||
        memcmp(header, directory_magic, 8) != 0 || overlay_read_u32(header + 8) != 3u) {
        fclose(stream); return -ENOEXEC;
    }
    const uint32_t overlay_count = overlay_read_u32(header + 12);
    const uint32_t function_count = overlay_read_u32(header + 16);
    const uint32_t relocation_count = overlay_read_u32(header + 20);
    const uint32_t maximum_size = overlay_read_u32(header + 24);
    const uint32_t directory_resident_size = overlay_read_u32(header + 28);
    const uint32_t descriptor_vma = overlay_read_u32(header + 32);
    const uint32_t text_anchor_vma = overlay_read_u32(header + 36);
    const uint64_t expected = 40u + (uint64_t)overlay_count * 100u + (uint64_t)function_count * 12u +
        (uint64_t)relocation_count * 16u;
    if (overlay_count == 0u || overlay_count > 256u || function_count == 0u || function_count > 4096u ||
        relocation_count > 65536u || overlay_count != footer_overlay_count || expected != directory_size ||
        directory_resident_size != resident_size || maximum_size == 0u || descriptor_vma == 0u || text_anchor_vma == 0u) {
        fclose(stream); return -ENOEXEC;
    }
    const size_t index_size = (size_t)overlay_count * sizeof(ct_overlay_entry_v3) +
        (size_t)function_count * sizeof(ct_overlay_function_v3);
    uint8_t *directory = calloc(1u, index_size);
    if (directory == NULL) { fclose(stream); return -ENOMEM; }
    ct_overlay_entry_v3 *overlays = (ct_overlay_entry_v3 *)(void *)directory;
    ct_overlay_function_v3 *functions = (ct_overlay_function_v3 *)(void *)(
        directory + (size_t)overlay_count * sizeof(*overlays));
    uint32_t previous_file_end = resident_size;
    uint32_t expected_relocation_start = 0u;
    uint32_t expected_function_start = 0u;
    for (uint32_t index = 0; index < overlay_count; ++index) {
        uint8_t source[100];
        if (fread(source, 1u, sizeof(source), stream) != sizeof(source)) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
        ct_overlay_entry_v3 *entry = &overlays[index];
        entry->Id = overlay_read_u32(source);
        entry->FileOffset = overlay_read_u32(source + 4);
        entry->StoredSize = overlay_read_u32(source + 8);
        entry->MemorySize = overlay_read_u32(source + 12);
        entry->Alignment = overlay_read_u32(source + 16);
        entry->RelocationStart = overlay_read_u32(source + 20);
        entry->RelocationCount = overlay_read_u32(source + 24);
        entry->FunctionStart = overlay_read_u32(source + 28);
        entry->FunctionCount = overlay_read_u32(source + 32);
        (void)memcpy(entry->Name, source + 36, sizeof(entry->Name));
        (void)memcpy(entry->Sha256, source + 68, sizeof(entry->Sha256));
        if (entry->Id != index + 1u || entry->Alignment != 16u ||
            !canonical_overlay_name(entry->Name, sizeof(entry->Name)) ||
            (index != 0u && strcmp(overlays[index - 1u].Name, entry->Name) >= 0) ||
            entry->FileOffset < previous_file_end || entry->FileOffset % entry->Alignment != 0u ||
            (uint64_t)entry->FileOffset + entry->StoredSize > directory_offset ||
            entry->StoredSize == 0u || entry->StoredSize > entry->MemorySize || entry->MemorySize > maximum_size ||
            (entry->StoredSize & (sizeof(uint32_t) - 1u)) != 0u ||
            (entry->MemorySize & (sizeof(uint32_t) - 1u)) != 0u ||
            entry->RelocationStart != expected_relocation_start ||
            entry->RelocationStart + entry->RelocationCount > relocation_count ||
            entry->FunctionStart != expected_function_start || entry->FunctionStart + entry->FunctionCount > function_count) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
        previous_file_end = entry->FileOffset + entry->StoredSize;
        expected_relocation_start += entry->RelocationCount;
        expected_function_start += entry->FunctionCount;
    }
    if (expected_relocation_start != relocation_count || expected_function_start != function_count) {
        free(directory); fclose(stream); return -ENOEXEC;
    }
    uint32_t observed_maximum_size = 0u;
    for (uint32_t index = 0; index < overlay_count; ++index)
        if (overlays[index].MemorySize > observed_maximum_size) observed_maximum_size = overlays[index].MemorySize;
    if (observed_maximum_size != maximum_size) {
        free(directory); fclose(stream); return -ENOEXEC;
    }
    for (uint32_t index = 0; index < function_count; ++index) {
        uint8_t source[12];
        if (fread(source, 1u, sizeof(source), stream) != sizeof(source)) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
        functions[index] = (ct_overlay_function_v3){ overlay_read_u32(source),
            overlay_read_u32(source + 4), overlay_read_u32(source + 8) };
        if (functions[index].OverlayId == 0u || functions[index].OverlayId > overlay_count ||
            functions[index].BodyOffset >= overlays[functions[index].OverlayId - 1u].MemorySize) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
        const ct_overlay_entry_v3 *owner = &overlays[functions[index].OverlayId - 1u];
        if (index < owner->FunctionStart || index >= owner->FunctionStart + owner->FunctionCount) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
    }
    const uint32_t relocation_file_offset = directory_offset + 40u +
        overlay_count * (uint32_t)sizeof(ct_overlay_entry_v3) +
        function_count * (uint32_t)sizeof(ct_overlay_function_v3);
    for (uint32_t index = 0; index < relocation_count; ++index) {
        uint8_t source[16];
        if (fread(source, 1u, sizeof(source), stream) != sizeof(source)) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
        const ct_overlay_relocation_v3 relocation = { overlay_read_u32(source),
            overlay_read_u32(source + 4), overlay_read_u32(source + 8),
            (int32_t)overlay_read_u32(source + 12) };
        if (relocation.Kind < CT_OVERLAY_RELOCATION_WINDOW ||
            relocation.Kind > CT_OVERLAY_RELOCATION_RESIDENT_DATA_INDIRECT) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
        bool owned = false;
        for (uint32_t overlay = 0; overlay < overlay_count; ++overlay) {
            const ct_overlay_entry_v3 *entry = &overlays[overlay];
            if (index < entry->RelocationStart || index >= entry->RelocationStart + entry->RelocationCount) continue;
            if ((relocation.Offset & (sizeof(uint32_t) - 1u)) != 0u ||
                relocation.Offset > entry->MemorySize ||
                entry->MemorySize - relocation.Offset < sizeof(uint32_t) ||
                (relocation.Kind == CT_OVERLAY_RELOCATION_WINDOW && relocation.Target >= entry->MemorySize)) {
                free(directory); fclose(stream); return -ENOEXEC;
            }
            owned = true;
            break;
        }
        if (!owned) {
            free(directory); fclose(stream); return -ENOEXEC;
        }
    }
    module->OverlayStream = stream;
    module->OverlayLock = xSemaphoreCreateMutex();
    module->OverlayDirectory = directory;
    module->Overlays = overlays;
    module->OverlayFunctions = functions;
    module->OverlayRelocations = NULL;
    module->OverlayCount = overlay_count;
    module->OverlayFunctionCount = function_count;
    module->OverlayRelocationCount = relocation_count;
    module->OverlayRelocationFileOffset = relocation_file_offset;
    module->MaximumOverlayBytes = maximum_size;
    module->DescriptorVma = descriptor_vma;
    module->TextAnchorVma = text_anchor_vma;
    if (module->OverlayLock == NULL) return -ENOMEM;
    return 0;
}

static void close_overlay_directory(ct_module *module)
{
    if (module->OverlayStream != NULL) { (void)fclose(module->OverlayStream); module->OverlayStream = NULL; }
    if (module->OverlayLock != NULL) { vSemaphoreDelete(module->OverlayLock); module->OverlayLock = NULL; }
    free(module->OverlayDirectory); module->OverlayDirectory = NULL;
    module->OverlayRelocations = NULL;
    module->OverlayFunctions = NULL;
    module->Overlays = NULL;
    module->OverlayCount = module->OverlayFunctionCount = module->OverlayRelocationCount = 0u;
    module->OverlayRelocationFileOffset = module->MaximumOverlayBytes = 0u;
}

typedef struct ct_elf_load_requirements {
    size_t ResidentExecutableBytes;
    size_t LargestExecutableSegmentBytes;
} ct_elf_load_requirements;

static uint16_t read_elf_u16(const uint8_t *value)
{
    return (uint16_t)value[0] | (uint16_t)((uint16_t)value[1] << 8u);
}

static uint32_t read_elf_u32(const uint8_t *value)
{
    return (uint32_t)value[0] | (uint32_t)value[1] << 8u |
        (uint32_t)value[2] << 16u | (uint32_t)value[3] << 24u;
}

static int inspect_elf_load_requirements(const char *path, ct_elf_load_requirements *requirements)
{
    if (path == NULL || requirements == NULL) return -EINVAL;
    (void)memset(requirements, 0, sizeof(*requirements));
    FILE *stream = fopen(path, "rb");
    if (stream == NULL) return -errno;
    uint8_t header[52];
    int result = 0;
    if (fread(&header, 1u, sizeof(header), stream) != sizeof(header) ||
        header[0] != 0x7fu || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
        header[4] != 1u || header[5] != 1u || read_elf_u16(&header[46]) != 40u) {
        result = -ENOEXEC;
        goto complete;
    }
    if (fseeko(stream, (off_t)read_elf_u32(&header[32]), SEEK_SET) != 0) {
        result = -errno;
        goto complete;
    }
    const uint16_t section_header_count = read_elf_u16(&header[48]);
    for (uint16_t index = 0u; index < section_header_count; ++index) {
        uint8_t section_header[40];
        if (fread(&section_header, 1u, sizeof(section_header), stream) != sizeof(section_header)) {
            result = -ENOEXEC;
            goto complete;
        }
        const uint32_t type = read_elf_u32(&section_header[4]);
        const uint32_t flags = read_elf_u32(&section_header[8]);
        const uint32_t memory_size = read_elf_u32(&section_header[20]);
        if (type != 1u || (flags & 6u) != 6u) continue;
        if (SIZE_MAX - requirements->ResidentExecutableBytes < memory_size) {
            result = -EOVERFLOW;
            goto complete;
        }
        requirements->ResidentExecutableBytes += memory_size;
        if (memory_size > requirements->LargestExecutableSegmentBytes)
            requirements->LargestExecutableSegmentBytes = memory_size;
    }
complete:
    (void)fclose(stream);
    return result;
}

static int inspect_module_overlay_requirement(const char *path, size_t *maximum_bytes)
{
    static const uint8_t footer_magic[8] = { 'C', 'T', 'O', 'V', 'L', 'F', '3', 0 };
    static const uint8_t directory_magic[8] = { 'C', 'T', 'O', 'V', 'L', 'D', '3', 0 };
    if (path == NULL || maximum_bytes == NULL) return -EINVAL;
    *maximum_bytes = 0u;
    FILE *stream = fopen(path, "rb");
    if (stream == NULL) return -errno;
    int result = 0;
    uint8_t footer[24];
    if (fseeko(stream, 0, SEEK_END) != 0) result = -errno;
    const off_t file_size = result == 0 ? ftello(stream) : -1;
    if (result == 0 && file_size >= (off_t)sizeof(footer)) {
        if (fseeko(stream, file_size - (off_t)sizeof(footer), SEEK_SET) != 0 ||
            fread(footer, 1u, sizeof(footer), stream) != sizeof(footer)) result = -ENOEXEC;
        else if (memcmp(footer, footer_magic, sizeof(footer_magic)) == 0) {
            const uint32_t directory_offset = read_elf_u32(footer + 8u);
            const uint32_t directory_size = read_elf_u32(footer + 12u);
            uint8_t header[40];
            if (directory_size < sizeof(header) ||
                (uint64_t)directory_offset + directory_size + sizeof(footer) != (uint64_t)file_size ||
                fseeko(stream, (off_t)directory_offset, SEEK_SET) != 0 ||
                fread(header, 1u, sizeof(header), stream) != sizeof(header) ||
                memcmp(header, directory_magic, sizeof(directory_magic)) != 0 ||
                read_elf_u32(header + 8u) != 3u) result = -ENOEXEC;
            else {
                const uint32_t value = read_elf_u32(header + 24u);
                if (value == 0u || value > 1024u * 1024u) result = -ENOEXEC;
                else *maximum_bytes = value;
            }
        }
    }
    (void)fclose(stream);
    return result;
}

static int inspect_process_overlay_requirement(const char *path, const char *const *chain,
    size_t depth, size_t *maximum_bytes, uint32_t *root_stack_bytes, char *root_name)
{
    if (path == NULL || maximum_bytes == NULL || depth >= CT_MAX_PROCESS_MODULES) return -ELOOP;
    ct_binary_manifest_v1 *manifest = NULL;
    uint8_t *manifest_bytes = NULL;
    int result = read_manifest(path, &manifest, &manifest_bytes, NULL);
    if (result != 0) return result;
    if (depth == 0u && root_stack_bytes != NULL) {
        *root_stack_bytes = manifest->MainTaskStackBytes;
        (void)snprintf(root_name, CT_MODULE_NAME_MAX, "%s", manifest->Name);
    }
    for (size_t index = 0; index < depth; ++index) {
        if (strcmp(chain[index], manifest->Name) == 0) {
            free(manifest_bytes);
            return -ELOOP;
        }
    }
    size_t module_maximum = 0u;
    result = inspect_module_overlay_requirement(path, &module_maximum);
    if (result != 0) {
        free(manifest_bytes);
        return result;
    }
    if (module_maximum > *maximum_bytes) *maximum_bytes = module_maximum;
    const char *next_chain[CT_MAX_PROCESS_MODULES];
    for (size_t index = 0; index < depth; ++index) next_chain[index] = chain[index];
    next_chain[depth] = manifest->Name;
    for (uint32_t index = 0; index < manifest->DependencyCount; ++index) {
        char dependency_name[CT_MODULE_NAME_MAX + 5];
        const int written = snprintf(dependency_name, sizeof(dependency_name), "%s.ctm",
            manifest->Dependencies[index].Name);
        char dependency_path[CT_MODULE_PATH_MAX];
        if (written <= 0 || written >= (int)sizeof(dependency_name)) result = -ENAMETOOLONG;
        else result = resolve_module_path_bytes((const uint8_t *)dependency_name,
            (size_t)written, dependency_path);
        if (result == 0) result = inspect_process_overlay_requirement(dependency_path,
            next_chain, depth + 1u, maximum_bytes, root_stack_bytes, root_name);
        if (result != 0) break;
    }
    free(manifest_bytes);
    return result;
}

static int load_module_recursive(const char *path, const char *const *chain, size_t depth, ct_module **output)
{
    ctilde_managed_memory_sample("module_load_begin", (uint32_t)depth);
    if (depth >= CT_MAX_PROCESS_MODULES) return -ELOOP;
    ct_binary_manifest_v1 *manifest;
    uint8_t *manifest_bytes;
    int result = read_manifest(path, &manifest, &manifest_bytes, NULL);
    if (result != 0) return result;
    for (size_t index = 0; index < depth; ++index) {
        if (strcmp(chain[index], manifest->Name) == 0) { free(manifest_bytes); return -ELOOP; }
    }
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_module *existing = find_module(manifest->Name);
    if (existing != NULL) {
        const bool exact = strcmp(existing->Version, manifest->Version) == 0 &&
            memcmp(existing->BuildIdentity, manifest->BuildIdentity, 32) == 0 && memcmp(existing->ApiHash, manifest->ApiHash, 32) == 0;
        const bool available = exact && !existing->Stopping && !existing->Loading;
        if (available) ++existing->References;
        xSemaphoreGive(s_registry);
        free(manifest_bytes);
        if (!available) return -EEXIST;
        *output = existing;
        return 0;
    }
    ct_module *module = allocate_module_slot();
    if (module == NULL) { xSemaphoreGive(s_registry); free(manifest_bytes); return -ENOSPC; }
    (void)snprintf(module->Path, sizeof(module->Path), "%s", path);
    (void)snprintf(module->Name, sizeof(module->Name), "%s", manifest->Name);
    (void)snprintf(module->Version, sizeof(module->Version), "%s", manifest->Version);
    (void)memcpy(module->BuildIdentity, manifest->BuildIdentity, 32);
    (void)memcpy(module->ApiHash, manifest->ApiHash, 32);
    module->References = 1;
    xSemaphoreGive(s_registry);

    const char *next_chain[CT_MAX_PROCESS_MODULES];
    for (size_t index = 0; index < depth; ++index) next_chain[index] = chain[index];
    next_chain[depth] = module->Name;
    for (uint32_t index = 0; index < manifest->DependencyCount; ++index) {
        char dependency_name[CT_MODULE_NAME_MAX + 5];
        const int written = snprintf(dependency_name, sizeof(dependency_name), "%s.ctm", manifest->Dependencies[index].Name);
        if (written <= 0 || written >= (int)sizeof(dependency_name)) { result = -ENAMETOOLONG; goto fail; }
        char dependency_path[CT_MODULE_PATH_MAX];
        result = resolve_module_path_bytes((const uint8_t *)dependency_name, (size_t)written, dependency_path);
        if (result != 0) goto fail;
        result = load_module_recursive(dependency_path, next_chain, depth + 1, &module->Dependencies[index]);
        if (result != 0) goto fail;
        ++module->DependencyCount;
        if (!exact_dependency(&manifest->Dependencies[index], module->Dependencies[index])) { result = -ESTALE; goto fail; }
    }

    result = load_overlay_directory(module, path);
    if (result != 0) goto fail;

    ct_elf_load_requirements load_requirements;
    result = inspect_elf_load_requirements(path, &load_requirements);
    if (result != 0) goto fail;
    const size_t executable_free_before_load = heap_caps_get_free_size(MALLOC_CAP_EXEC | MALLOC_CAP_32BIT);
    const size_t executable_largest_before_load = heap_caps_get_largest_free_block(MALLOC_CAP_EXEC | MALLOC_CAP_32BIT);
    ESP_LOGI(TAG, "Module '%s' load requirements: resident-executable=%u, largest-executable-segment=%u, "
        "overlay-window=%u, executable-free=%u, executable-largest=%u",
        manifest->Name, (unsigned)load_requirements.ResidentExecutableBytes,
        (unsigned)load_requirements.LargestExecutableSegmentBytes, (unsigned)module->MaximumOverlayBytes,
        (unsigned)executable_free_before_load, (unsigned)executable_largest_before_load);

    module->DynamicHandle = dlopen(path, RTLD_NOW);
    if (module->DynamicHandle == NULL) {
        ESP_LOGE(TAG, "Module '%s' executable allocation failed: requested=%u, resident-total=%u, "
            "overlay-window=%u, executable-free-before=%u, executable-largest-before=%u",
            manifest->Name, (unsigned)load_requirements.LargestExecutableSegmentBytes,
            (unsigned)load_requirements.ResidentExecutableBytes, (unsigned)module->MaximumOverlayBytes,
            (unsigned)executable_free_before_load, (unsigned)executable_largest_before_load);
        result = -ENOEXEC;
        goto fail;
    }
    result = publish_executable_memory();
    if (result != 0) {
        ESP_LOGE(TAG, "Module '%s' could not publish relocated executable memory on all cores", manifest->Name);
        goto fail;
    }
    typedef const ct_managed_module_descriptor_v4 *(*descriptor_function)(void);
    typedef int32_t (*bind_runtime_function)(const ct_runtime_api_v23 *runtime);
    descriptor_function get_descriptor = (descriptor_function)dlsym(module->DynamicHandle, "ct_managed_module_descriptor");
    bind_runtime_function bind_runtime = (bind_runtime_function)dlsym(module->DynamicHandle, "ct_managed_module_bind_runtime");
    void *text_anchor = module->OverlayCount == 0u ? NULL : dlsym(module->DynamicHandle, "ct_managed_module_text_anchor");
    const ct_managed_module_descriptor_v4 *descriptor = get_descriptor == NULL ? NULL : get_descriptor();
    if (descriptor == NULL || bind_runtime == NULL || (module->OverlayCount != 0u && text_anchor == NULL)) {
        ESP_LOGE(TAG, "Module '%s' does not export the Module ABI 3 descriptor, bind, or text-anchor functions", manifest->Name);
        result = -ENOEXEC;
        goto fail;
    }
    if (!descriptor_matches_manifest(descriptor, manifest)) {
        ESP_LOGE(TAG, "Module descriptor mismatch: descriptor=%p size=%u/%u runtime=%u module=%u name=%p version=%p dependencies=%u/%u",
            (const void *)descriptor,
            (unsigned)descriptor->Size, (unsigned)sizeof(*descriptor), (unsigned)descriptor->RuntimeAbi,
            (unsigned)descriptor->ModuleAbi, (const void *)descriptor->Name,
            (const void *)descriptor->Version, (unsigned)descriptor->DependencyCount,
            (unsigned)manifest->DependencyCount);
        result = -ENOEXEC;
        goto fail;
    }
    if (descriptor->CapabilityCount > 16u || (descriptor->CapabilityCount != 0u && descriptor->RequiredCapabilities == NULL)) {
        result = -ENOEXEC;
        goto fail;
    }
    for (uint32_t index = 0; index < descriptor->CapabilityCount; ++index) {
        const ct_capability_requirement *required = &descriptor->RequiredCapabilities[index];
        if (ctilde_managed_get_capability(required->Id, required->MajorVersion, required->MinimumSize) == NULL) {
            ESP_LOGE(TAG, "Module '%s' requires unavailable capability %u version %u size %u", module->Name,
                (unsigned)required->Id, (unsigned)required->MajorVersion, (unsigned)required->MinimumSize);
            result = -ENOSYS;
            goto fail;
        }
    }
    if (bind_runtime(ctilde_runtime_api_v23()) != 0) {
        ESP_LOGE(TAG, "Module '%s' rejected Runtime ABI %u", manifest->Name, CTILDE_RUNTIME_ABI_VERSION);
        result = -ELIBBAD;
        goto fail;
    }
    /* Managed contracts retain callable addresses in their own descriptor.
       The private dynamic handle needs no further name lookup. */
    if (esp_dldiscard_symbols(module->DynamicHandle) != 0) {
        result = -ENOEXEC;
        goto fail;
    }
    ctilde_managed_memory_sample("module_lookup_metadata_released", (uint32_t)depth);
    module->Descriptor = descriptor;
    module->DataLoadBias = (uintptr_t)(const void *)descriptor - (uintptr_t)module->DescriptorVma;
    module->ExecutableLoadBias = module->OverlayCount == 0u ? 0u :
        (uintptr_t)text_anchor - (uintptr_t)module->TextAnchorVma;
    if ((descriptor->HasOverlays != 0u) != (module->OverlayCount != 0u) ||
        descriptor->MaximumOverlayBytes != module->MaximumOverlayBytes) {
        ESP_LOGE(TAG, "Module '%s' overlay descriptor/container mismatch", manifest->Name);
        module->Descriptor = NULL;
        result = -ENOEXEC;
        goto fail;
    }
    uint8_t *patched_targets = descriptor->CallTargetCount == 0u ? NULL : calloc(descriptor->CallTargetCount, 1u);
    if (descriptor->CallTargetCount != 0u && patched_targets == NULL) {
        module->Descriptor = NULL;
        result = -ENOMEM;
        goto fail;
    }
    for (uint32_t index = 0; index < module->OverlayFunctionCount; ++index) {
        const ct_overlay_function_v3 *function = &module->OverlayFunctions[index];
        if (function->TargetIndex >= descriptor->CallTargetCount ||
            patched_targets[function->TargetIndex] != 0u ||
            descriptor->CallTargets[function->TargetIndex].Placement != 1u ||
            descriptor->CallTargets[function->TargetIndex].OverlayId != function->OverlayId) {
            free(patched_targets);
            module->Descriptor = NULL;
            result = -ENOEXEC;
            goto fail;
        }
        descriptor->CallTargets[function->TargetIndex].Body = function->BodyOffset;
        patched_targets[function->TargetIndex] = 1u;
    }
    for (uint32_t index = 0; index < descriptor->CallTargetCount; ++index) {
        if (descriptor->CallTargets[index].Placement == 1u && patched_targets[index] == 0u) {
            free(patched_targets);
            module->Descriptor = NULL;
            result = -ENOEXEC;
            goto fail;
        }
    }
    free(patched_targets);
    result = bind_module_imports(module);
    if (result != 0) {
        ESP_LOGE(TAG, "Module '%s' contains an unresolved managed import", manifest->Name);
        module->Descriptor = NULL;
        goto fail;
    }
    xSemaphoreTake(s_registry, portMAX_DELAY);
    module->Loading = false;
    xSemaphoreGive(s_registry);
    free(manifest_bytes);
    *output = module;
    ctilde_managed_memory_sample("module_load_complete", (uint32_t)depth);
    return 0;

fail:
    ESP_LOGE(TAG, "Module '%s' load failed before publication: %d", module->Name, result);
    close_overlay_directory(module);
    if (module->DynamicHandle != NULL) (void)dlclose(module->DynamicHandle);
    for (uint32_t index = module->DependencyCount; index > 0; --index) release_module(module->Dependencies[index - 1]);
    xSemaphoreTake(s_registry, portMAX_DELAY);
    (void)memset(module, 0, sizeof(*module));
    xSemaphoreGive(s_registry);
    free(manifest_bytes);
    return result;
}

static void release_module(ct_module *module)
{
    if (module == NULL) return;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    if (module->References == 0) { xSemaphoreGive(s_registry); abort(); }
    if (--module->References != 0) { xSemaphoreGive(s_registry); return; }
    module->Stopping = true;
    if (__atomic_load_n(&module->ActiveCalls, __ATOMIC_ACQUIRE) != 0 ||
        __atomic_load_n(&module->LiveAllocations, __ATOMIC_ACQUIRE) != 0) {
        xSemaphoreGive(s_registry);
        abort();
    }
    ct_module *dependencies[CT_MAX_DEPENDENCIES];
    const uint32_t dependency_count = module->DependencyCount;
    for (uint32_t index = 0; index < dependency_count; ++index) dependencies[index] = module->Dependencies[index];
    void *handle = module->DynamicHandle;
    for (size_t index = 0; index < sizeof(s_types) / sizeof(s_types[0]); ++index) {
        if (s_types[index].Used && s_types[index].Owner == module) (void)memset(&s_types[index], 0, sizeof(s_types[index]));
    }
    xSemaphoreGive(s_registry);
    if (handle != NULL && dlclose(handle) != 0) abort();
    close_overlay_directory(module);
    xSemaphoreTake(s_registry, portMAX_DELAY);
    if (!module->Used || !module->Stopping || module->References != 0) {
        xSemaphoreGive(s_registry);
        abort();
    }
    (void)memset(module, 0, sizeof(*module));
    xSemaphoreGive(s_registry);
    for (uint32_t index = dependency_count; index > 0; --index) release_module(dependencies[index - 1]);
}

static ct_process *process_from_handle(uintptr_t handle)
{
    const uint32_t id = (uint32_t)handle;
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || id == 0) return NULL;
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        if (__atomic_load_n(&s_published_process_ids[index], __ATOMIC_ACQUIRE) == id)
            return &s_processes[index];
    }
    return NULL;
}

static int add_instance_graph(ct_process *process, ct_module *module)
{
    for (uint32_t index = 0; index < process->InstanceCount; ++index) if (process->Instances[index].Module == module) return 0;
    for (uint32_t index = 0; index < module->DependencyCount; ++index) {
        const int result = add_instance_graph(process, module->Dependencies[index]);
        if (result != 0) return result;
    }
    if (process->InstanceCount >= CT_MAX_PROCESS_MODULES) return -ENOSPC;
    ct_module_instance *instance = &process->Instances[process->InstanceCount++];
    instance->Module = module;
    const size_t size = module->Descriptor->StaticStateSize == 0 ? 1 : module->Descriptor->StaticStateSize;
    instance->State = calloc(1, size);
    return instance->State == NULL ? -ENOMEM : 0;
}

static void *api_allocate(size_t size, const ct_managed_module_descriptor_v4 *descriptor)
{
    ct_execution_context *context = current_context();
    if (context == NULL || context->Process == NULL || descriptor == NULL) return NULL;
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_process *process = context->Process;
    ct_module *module = NULL;
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        if (process->Instances[index].Module->Descriptor == descriptor) { module = process->Instances[index].Module; break; }
    }
    portENTER_CRITICAL(&process->AllocationLock);
    const size_t heap_bytes = __atomic_load_n(&process->HeapBytes, __ATOMIC_ACQUIRE);
    if (module == NULL || module->Stopping || size > SIZE_MAX - sizeof(ct_allocation) ||
        size > SIZE_MAX - heap_bytes ||
        (process->HeapLimit != 0 && (heap_bytes > process->HeapLimit ||
            size > process->HeapLimit - heap_bytes))) {
        portEXIT_CRITICAL(&process->AllocationLock);
        end_runtime_operation(context);
        return NULL;
    }
    (void)__atomic_add_fetch(&process->HeapBytes, size, __ATOMIC_ACQ_REL);
    portEXIT_CRITICAL(&process->AllocationLock);
    ct_allocation *allocation = (ct_allocation *)calloc(1, sizeof(ct_allocation) + size);
    if (allocation == NULL) {
        portENTER_CRITICAL(&process->AllocationLock);
        (void)__atomic_sub_fetch(&process->HeapBytes, size, __ATOMIC_ACQ_REL);
        portEXIT_CRITICAL(&process->AllocationLock);
        end_runtime_operation(context);
        return NULL;
    }
    allocation->Process = process;
    allocation->Module = module;
    allocation->Size = size;
    portENTER_CRITICAL(&process->AllocationLock);
    allocation->Next = process->Allocations;
    if (process->Allocations != NULL) process->Allocations->Previous = allocation;
    process->Allocations = allocation;
    (void)__atomic_add_fetch(&module->LiveAllocations, 1u, __ATOMIC_ACQ_REL);
    portEXIT_CRITICAL(&process->AllocationLock);
    end_runtime_operation(context);
    return allocation + 1;
}

static void api_free(void *value)
{
    if (value == NULL) return;
    ct_execution_context *context = current_context();
    if (context == NULL || context->Process == NULL) abort();
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_allocation *allocation = ((ct_allocation *)value) - 1;
    ct_process *process = allocation->Process;
    if (process == NULL || process != context->Process) abort();
    portENTER_CRITICAL(&process->AllocationLock);
    if (allocation->Module == NULL ||
        allocation->Size > __atomic_load_n(&process->HeapBytes, __ATOMIC_ACQUIRE) ||
        __atomic_load_n(&allocation->Module->LiveAllocations, __ATOMIC_ACQUIRE) == 0) abort();
    if (allocation->Previous != NULL) allocation->Previous->Next = allocation->Next; else process->Allocations = allocation->Next;
    if (allocation->Next != NULL) allocation->Next->Previous = allocation->Previous;
    if (__atomic_fetch_sub(&process->HeapBytes, allocation->Size, __ATOMIC_ACQ_REL) < allocation->Size ||
        __atomic_fetch_sub(&allocation->Module->LiveAllocations, 1u, __ATOMIC_ACQ_REL) == 0) abort();
    portEXIT_CRITICAL(&process->AllocationLock);
    (void)memset(allocation + 1, 0xDD, allocation->Size);
    free(allocation);
    end_runtime_operation(context);
}

static void api_runtime_fault(const char *code, const char *file, int32_t line)
{
    ct_execution_context *context = current_context();
    ct_process *process = context == NULL ? NULL : context->Process;
    if (process == NULL || !begin_runtime_operation(context)) {
        if (process != NULL) await_forced_task_deletion();
        abort();
    }
    ESP_LOGE(TAG, "process fault %s at %s:%" PRId32, code == NULL ? "?" : code, file == NULL ? "?" : file, line);
    TaskHandle_t self = xTaskGetCurrentTaskHandle();
    TaskHandle_t expected = self;
    const bool owns_deletion = __atomic_compare_exchange_n(&process->MainTask, &expected, NULL, false,
        __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    if (!owns_deletion && !__atomic_load_n(&process->Cleaned, __ATOMIC_ACQUIRE)) {
        /* Terminate claimed the task first. Do not race its cross-core
           vTaskDelete with a second deletion of the same TCB. */
        abandon_runtime_operations(context);
        await_forced_task_deletion();
    }
    __atomic_store_n(&process->ExitCode, -1, __ATOMIC_RELEASE);
    __atomic_store_n(&process->State, CT_PROCESS_FAILED, __ATOMIC_RELEASE);
    abandon_runtime_operations(context);
    vTaskDelete(NULL);
    abort();
}

static const ct_type_descriptor *api_register_type(const void *value)
{
    const ct_type_descriptor *descriptor = (const ct_type_descriptor *)value;
    ct_execution_context *context = current_context();
    if (descriptor == NULL || context == NULL || context->Module == NULL) api_runtime_fault("CTT0010", "<type-register>", 0);
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_type_registration *free_slot = NULL;
    for (size_t index = 0; index < sizeof(s_types) / sizeof(s_types[0]); ++index) {
        ct_type_registration *slot = &s_types[index];
        if (!slot->Used) { if (free_slot == NULL) free_slot = slot; continue; }
        if (slot->High == descriptor->FingerprintHigh && slot->Low == descriptor->FingerprintLow) {
            const bool compatible = slot->Descriptor->Size == descriptor->Size && slot->Descriptor->Alignment == descriptor->Alignment &&
                slot->Descriptor->IsValue == descriptor->IsValue && strcmp(slot->Descriptor->Name, descriptor->Name) == 0;
            const ct_type_descriptor *result = compatible ? slot->Descriptor : NULL;
            xSemaphoreGive(s_registry);
            end_runtime_operation(context);
            if (!compatible) api_runtime_fault("CTT0011", "<type-register>", 0);
            return result;
        }
    }
    if (free_slot == NULL) {
        xSemaphoreGive(s_registry);
        end_runtime_operation(context);
        api_runtime_fault("CTT0012", "<type-register>", 0);
    }
    *free_slot = (ct_type_registration){ true, descriptor->FingerprintHigh, descriptor->FingerprintLow, descriptor, context->Module };
    xSemaphoreGive(s_registry);
    end_runtime_operation(context);
    return descriptor;
}

static void api_unregister_types(const ct_managed_module_descriptor_v4 *descriptor)
{
    ct_execution_context *context = current_context();
    ct_module *module = context == NULL ? NULL : context->Module;
    if (module == NULL || module->Descriptor != descriptor) api_runtime_fault("CTT0013", "<type-unregister>", 0);
    /* Module finalizers run once per process instance. Canonical descriptors
       remain globally valid while any process or dependent module keeps the
       provider loaded; release_module removes them immediately before dlclose. */
}

static void *api_current_module_state(const ct_managed_module_descriptor_v4 *descriptor)
{
    ct_process *process = current_process();
    if (process == NULL) return NULL;
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        if (process->Instances[index].Module->Descriptor == descriptor) return process->Instances[index].State;
    }
    return NULL;
}

static ct_process_context *api_current_process(void)
{
    return (ct_process_context *)(void *)current_process();
}

typedef struct ct_managed_call_frame_internal_v22 {
    ct_execution_context *Context;
    ct_module *Module;
    uintptr_t PreviousOverlayModule;
    uintptr_t PreviousOverlayId;
    uintptr_t Active;
} ct_managed_call_frame_internal_v22;

_Static_assert(sizeof(ct_managed_call_frame_internal_v22) <= sizeof(ct_managed_call_frame_v23),
    "managed call frame ABI storage is too small");
_Static_assert(sizeof(uintptr_t) != 4u || offsetof(ct_managed_module_descriptor_v4, MaximumOverlayBytes) == 112u,
    "Module ABI 3 descriptor overlay offset changed");

static const ct_overlay_entry_v3 *find_overlay(const ct_module *module, uint32_t id)
{
    if (module == NULL || id == 0u || id > module->OverlayCount) return NULL;
    const ct_overlay_entry_v3 *entry = &module->Overlays[id - 1u];
    return entry->Id == id ? entry : NULL;
}

static int load_process_overlay(ct_process *process, ct_module *module, uint32_t id)
{
    if (process == NULL || module == NULL || process->OverlayWindowSize == 0u ||
        __atomic_load_n(&module->OverlayUnavailable, __ATOMIC_ACQUIRE) ||
        module->OverlayLock == NULL) return -ENODEV;
    if (__atomic_load_n(&process->LoadedOverlayModule, __ATOMIC_ACQUIRE) == module &&
        __atomic_load_n(&process->LoadedOverlayId, __ATOMIC_ACQUIRE) == id) return 0;
    const ct_overlay_entry_v3 *entry = find_overlay(module, id);
    if (entry == NULL || entry->StoredSize == 0u || entry->StoredSize > entry->MemorySize ||
        entry->MemorySize > process->OverlayWindowSize ||
        (entry->StoredSize & (sizeof(uint32_t) - 1u)) != 0u ||
        (entry->MemorySize & (sizeof(uint32_t) - 1u)) != 0u) {
        ESP_LOGE(TAG, "Module '%s' overlay %" PRIu32 " has invalid bounds: stored=%u, memory=%u, "
            "window=%u, relocations=%u", module->Name, id,
            entry == NULL ? 0u : (unsigned)entry->StoredSize,
            entry == NULL ? 0u : (unsigned)entry->MemorySize,
            (unsigned)process->OverlayWindowSize,
            entry == NULL ? 0u : (unsigned)entry->RelocationCount);
        return -ENOEXEC;
    }
    if (process->OverlayWindow == NULL) {
        volatile uint32_t *window = heap_caps_aligned_alloc(16u, process->OverlayWindowSize,
            MALLOC_CAP_EXEC | MALLOC_CAP_32BIT);
        if (window == NULL) {
            ESP_LOGE(TAG, "Process %u module '%s' overlay allocation failed: requested=%u, "
                "executable-free=%u, executable-largest=%u",
                (unsigned)process->Id, module->Name, (unsigned)process->OverlayWindowSize,
                (unsigned)heap_caps_get_free_size(MALLOC_CAP_EXEC | MALLOC_CAP_32BIT),
                (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_EXEC | MALLOC_CAP_32BIT));
            return -ENOMEM;
        }
        __atomic_store_n(&process->OverlayWindow, window, __ATOMIC_RELEASE);
    }
    if ((((uintptr_t)process->OverlayWindow) & 15u) != 0u) {
        ESP_LOGE(TAG, "Process %u module '%s' overlay window is not 16-byte aligned: %" PRIuPTR,
            (unsigned)process->Id, module->Name, (uintptr_t)process->OverlayWindow);
        return -ENOEXEC;
    }
    uint32_t staging[CT_OVERLAY_STAGING_WORDS];
    uint8_t digest[32];
    size_t digest_length = 0;
    psa_hash_operation_t hash = PSA_HASH_OPERATION_INIT;
    psa_status_t hash_status = psa_crypto_init();
    if (hash_status == PSA_SUCCESS)
        hash_status = psa_hash_setup(&hash, PSA_ALG_SHA_256);
    xSemaphoreTake(module->OverlayLock, portMAX_DELAY);
    int result = 0;
    if (hash_status != PSA_SUCCESS) result = -EIO;
    else if (__atomic_load_n(&module->OverlayUnavailable, __ATOMIC_ACQUIRE) || module->OverlayStream == NULL) result = -ENODEV;
    else if (fseek(module->OverlayStream, (long)entry->FileOffset, SEEK_SET) != 0) {
        const int seek_error = errno == 0 ? EIO : errno;
        ESP_LOGE(TAG, "Module '%s' overlay %" PRIu32 " seek to %u failed: %d",
            module->Name, id, (unsigned)entry->FileOffset, seek_error);
        result = -seek_error;
    }
    else {
        __atomic_store_n(&process->LoadedOverlayModule, NULL, __ATOMIC_RELEASE);
        __atomic_store_n(&process->LoadedOverlayId, 0u, __ATOMIC_RELEASE);
        size_t copied = 0u;
        while (copied < entry->StoredSize) {
            const size_t remaining = entry->StoredSize - copied;
            const size_t chunk = remaining < sizeof(staging) ? remaining : sizeof(staging);
            if (fread(staging, 1, chunk, module->OverlayStream) != chunk) {
                result = -EIO;
                break;
            }
            hash_status = psa_hash_update(&hash, (const uint8_t *)(const void *)staging, chunk);
            if (hash_status != PSA_SUCCESS) {
                result = -EIO;
                break;
            }
            const size_t destination_word = copied / sizeof(uint32_t);
            for (size_t word = 0; word < chunk / sizeof(uint32_t); ++word)
                process->OverlayWindow[destination_word + word] = staging[word];
            copied += chunk;
        }
    }
    xSemaphoreGive(module->OverlayLock);
    if (result == 0) {
        hash_status = psa_hash_finish(&hash, digest, sizeof(digest), &digest_length);
        if (hash_status != PSA_SUCCESS) {
            (void)psa_hash_abort(&hash);
            result = -EIO;
        }
    } else {
        (void)psa_hash_abort(&hash);
    }
    if (result != 0) return result;
    if (digest_length != sizeof(digest) || memcmp(digest, entry->Sha256, sizeof(digest)) != 0)
        return -EBADMSG;
    for (size_t word = entry->StoredSize / sizeof(uint32_t);
         word < entry->MemorySize / sizeof(uint32_t); ++word)
        process->OverlayWindow[word] = 0u;
    xSemaphoreTake(module->OverlayLock, portMAX_DELAY);
    const uint64_t relocation_offset = (uint64_t)module->OverlayRelocationFileOffset +
        (uint64_t)entry->RelocationStart * sizeof(ct_overlay_relocation_v3);
    if (relocation_offset > LONG_MAX || module->OverlayStream == NULL ||
        fseek(module->OverlayStream, (long)relocation_offset, SEEK_SET) != 0) result = -EIO;
    for (uint32_t index = 0; result == 0 && index < entry->RelocationCount; ++index) {
        const uint32_t relocation_index = entry->RelocationStart + index;
        uint8_t source[16];
        if (relocation_index >= module->OverlayRelocationCount ||
            fread(source, 1u, sizeof(source), module->OverlayStream) != sizeof(source)) {
            result = -ENOEXEC;
            break;
        }
        const ct_overlay_relocation_v3 relocation = { overlay_read_u32(source),
            overlay_read_u32(source + 4), overlay_read_u32(source + 8),
            (int32_t)overlay_read_u32(source + 12) };
        if ((relocation.Offset & (sizeof(uint32_t) - 1u)) != 0u ||
            relocation.Offset > entry->MemorySize ||
            entry->MemorySize - relocation.Offset < sizeof(uint32_t)) {
            result = -ENOEXEC;
            break;
        }
        uintptr_t base;
        if (relocation.Kind == CT_OVERLAY_RELOCATION_WINDOW) {
            if (relocation.Target >= entry->MemorySize) { result = -ENOEXEC; break; }
            base = (uintptr_t)process->OverlayWindow + relocation.Target;
        } else if (relocation.Kind == CT_OVERLAY_RELOCATION_RESIDENT_EXECUTABLE) {
            base = esp_dlmap(module->DynamicHandle, relocation.Target);
            if (base == 0u) { result = -ENOEXEC; break; }
        } else if (relocation.Kind == CT_OVERLAY_RELOCATION_RESIDENT_DATA) {
            base = esp_dlmap(module->DynamicHandle, relocation.Target);
            if (base == 0u) { result = -ENOEXEC; break; }
        } else if (relocation.Kind == CT_OVERLAY_RELOCATION_RESIDENT_EXECUTABLE_INDIRECT) {
            const uintptr_t slot = module->ExecutableLoadBias + relocation.Target;
            uint32_t indirect;
            (void)memcpy(&indirect, (const void *)slot, sizeof(indirect));
            base = (uintptr_t)indirect;
        } else if (relocation.Kind == CT_OVERLAY_RELOCATION_RESIDENT_DATA_INDIRECT) {
            const uintptr_t slot = module->DataLoadBias + relocation.Target;
            uint32_t indirect;
            (void)memcpy(&indirect, (const void *)slot, sizeof(indirect));
            base = (uintptr_t)indirect;
        } else { result = -ENOEXEC; break; }
        const int64_t relocated = (int64_t)(uint32_t)base + (int64_t)relocation.Addend;
        if (relocated < 0 || relocated > UINT32_MAX) { result = -EOVERFLOW; break; }
        process->OverlayWindow[relocation.Offset / sizeof(uint32_t)] = (uint32_t)relocated;
    }
    xSemaphoreGive(module->OverlayLock);
    if (result != 0) {
        ESP_LOGE(TAG, "Module '%s' overlay %" PRIu32 " relocation stream failed: %d",
            module->Name, id, result);
        return result;
    }
    __builtin___clear_cache((char *)(uintptr_t)process->OverlayWindow,
        (char *)(uintptr_t)((uintptr_t)process->OverlayWindow + entry->MemorySize));
    result = publish_executable_memory();
    if (result != 0) return result;
    __atomic_store_n(&process->LoadedOverlayModule, module, __ATOMIC_RELEASE);
    __atomic_store_n(&process->LoadedOverlayId, id, __ATOMIC_RELEASE);
    (void)__atomic_add_fetch(&process->OverlayGeneration, 1u, __ATOMIC_RELEASE);
    return 0;
}

static uintptr_t overlay_body_address(ct_process *process, ct_module *module,
    const ct_managed_call_target_v4 *target)
{
    if (target->Placement == 0u) return target->Body;
    if (target->Placement != 1u || module->Descriptor == NULL || module->Descriptor->CallTargets == NULL) return 0u;
    const ct_overlay_entry_v3 *entry = find_overlay(module, target->OverlayId);
    if (entry == NULL || target->Body >= entry->MemorySize) return 0u;
    if (__atomic_load_n(&process->LoadedOverlayModule, __ATOMIC_ACQUIRE) != module ||
        __atomic_load_n(&process->LoadedOverlayId, __ATOMIC_ACQUIRE) != target->OverlayId) return 0u;
    return (uintptr_t)process->OverlayWindow + target->Body;
}

static void terminate_current_overlay_process(ct_process *process)
{
    if (process == NULL) abort();
    __atomic_store_n(&process->ExitCode, -3, __ATOMIC_RELEASE);
    __atomic_store_n(&process->Cancellation, true, __ATOMIC_RELEASE);
    ct_managed_process_terminate((uintptr_t)process->Id, 0u);
    /* A failed transition may already have overwritten the caller's payload.
       Stay in resident runtime code until the terminator deletes this task. */
    for (;;) vTaskDelay(portMAX_DELAY);
}

static uintptr_t api_enter_managed_call(const ct_managed_module_descriptor_v4 *descriptor,
    const ct_managed_call_target_v4 *call_target, ct_managed_call_frame_v23 *public_frame)
{
    ct_execution_context *context = current_context();
    if (context == NULL || call_target == NULL || public_frame == NULL ||
        call_target->Size != sizeof(*call_target) || call_target->Reserved != 0u)
        api_runtime_fault("CTT0014", "<managed-call>", 0);
    ct_managed_call_frame_internal_v22 *frame = (ct_managed_call_frame_internal_v22 *)(void *)public_frame;
    (void)memset(frame, 0, sizeof(*frame));
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_module *target = NULL;
    for (uint32_t index = 0; index < context->Process->InstanceCount; ++index) {
        ct_module *module = context->Process->Instances[index].Module;
        if (module->Descriptor == descriptor) {
            target = module;
            break;
        }
    }
    const bool stopping = target != NULL && target->Stopping;
    end_runtime_operation(context);
    if (target == NULL) api_runtime_fault("CTT0016", "<managed-call>", 0);
    if (stopping) api_runtime_fault("CTT0015", "<managed-call>", 0);
    frame->PreviousOverlayModule = (uintptr_t)__atomic_load_n(
        &context->Process->LogicalOverlayModule, __ATOMIC_ACQUIRE);
    frame->PreviousOverlayId = __atomic_load_n(&context->Process->LogicalOverlayId, __ATOMIC_ACQUIRE);
    if (call_target->Placement == 1u) {
        if (xTaskGetCurrentTaskHandle() != context->Process->MainTask || context->Process->TaskCount != 1u)
            api_runtime_fault("CTT0020", "<managed-overlay-task>", 0);
        const int overlay_result = load_process_overlay(context->Process, target, call_target->OverlayId);
        if (overlay_result != 0) {
            ESP_LOGE(TAG, "Module '%s' overlay %" PRIu32 " activation failed: %d",
                target->Name, call_target->OverlayId, overlay_result);
            terminate_current_overlay_process(context->Process);
        }
        __atomic_store_n(&context->Process->LogicalOverlayModule, target, __ATOMIC_RELEASE);
        __atomic_store_n(&context->Process->LogicalOverlayId, call_target->OverlayId, __ATOMIC_RELEASE);
    }
    if (!enter_context_call(context, target)) api_runtime_fault("CTT0018", "<managed-call-depth>", 0);
    frame->Context = context;
    frame->Module = target;
    frame->Active = 1u;
    const uintptr_t body = overlay_body_address(context->Process, target, call_target);
    if (call_target->Placement > 1u || body == (uintptr_t)0)
        api_runtime_fault("CTT0019", "<managed-overlay>", 0);
    return body;
}

static void api_leave_managed_call(ct_managed_call_frame_v23 *public_frame)
{
    if (public_frame == NULL) abort();
    ct_managed_call_frame_internal_v22 *frame = (ct_managed_call_frame_internal_v22 *)(void *)public_frame;
    if (frame->Active == 0u) return;
    if (frame->Context == NULL || frame->Module == NULL) abort();
    frame->Active = 0u;
    ct_process *process = frame->Context->Process;
    ct_module *previous_overlay_module = (ct_module *)frame->PreviousOverlayModule;
    const uint32_t previous_overlay_id = (uint32_t)frame->PreviousOverlayId;
    if ((__atomic_load_n(&process->LoadedOverlayModule, __ATOMIC_ACQUIRE) != previous_overlay_module ||
         __atomic_load_n(&process->LoadedOverlayId, __ATOMIC_ACQUIRE) != previous_overlay_id) &&
        previous_overlay_module != NULL) {
        const int overlay_result = load_process_overlay(process, previous_overlay_module, previous_overlay_id);
        if (overlay_result != 0) {
            ESP_LOGE(TAG, "Module '%s' overlay %" PRIu32 " restore failed: %d",
                previous_overlay_module->Name, previous_overlay_id, overlay_result);
            terminate_current_overlay_process(process);
        }
    }
    __atomic_store_n(&process->LogicalOverlayModule, previous_overlay_module, __ATOMIC_RELEASE);
    __atomic_store_n(&process->LogicalOverlayId, previous_overlay_id, __ATOMIC_RELEASE);
    if (!leave_context_call(frame->Context, frame->Module)) abort();
    frame->Context = NULL;
    frame->Module = NULL;
}

static int copy_runtime_path(ct_process *process, const uint8_t *data, size_t length,
    char output[CT_MODULE_PATH_MAX])
{
    if (data == NULL || length == 0 || memchr(data, 0, length) != NULL) return -EINVAL;
    size_t used = 0;
    if (data[0] != '/') {
        used = process->CurrentDirectory == NULL ? 0 : strlen(process->CurrentDirectory);
        if (used == 0) { output[0] = '/'; used = 1; }
        else (void)memcpy(output, process->CurrentDirectory, used);
        if (used > 1 && output[used - 1] != '/') output[used++] = '/';
    }
    if (used + length >= CT_MODULE_PATH_MAX) return -ENAMETOOLONG;
    (void)memcpy(output + used, data, length);
    used += length;
    output[used] = '\0';
    if (strstr(output, "/../") != NULL || strstr(output, "/./") != NULL ||
        strcmp(output, "/..") == 0 || strcmp(output, "/.") == 0 ||
        (used >= 3 && strcmp(output + used - 3, "/..") == 0) ||
        (used >= 2 && strcmp(output + used - 2, "/.") == 0)) return -EPERM;
    return 0;
}

static ct_process_file *find_process_file(ct_process *process, uintptr_t handle)
{
    for (ct_process_file *file = process->Files; file != NULL; file = file->Next)
        if ((uintptr_t)file == handle) return file->Stream == NULL ? NULL : file;
    return NULL;
}

static ct_process_directory *find_process_directory(ct_process *process, uintptr_t handle)
{
    for (ct_process_directory *directory = process->Directories; directory != NULL; directory = directory->Next)
        if ((uintptr_t)directory == handle) return directory->Directory == NULL ? NULL : directory;
    return NULL;
}

static int close_process_file(ct_process *process, ct_process_file *file)
{
    ct_process_file **link = &process->Files;
    while (*link != NULL && *link != file) link = &(*link)->Next;
    if (*link == NULL) return -EBADF;
    *link = file->Next;
    const int result = file->Stream == NULL ? 0 : fclose(file->Stream);
    free(file);
    return result == 0 ? 0 : -(errno == 0 ? EIO : errno);
}

static int close_process_directory(ct_process *process, ct_process_directory *directory)
{
    ct_process_directory **link = &process->Directories;
    while (*link != NULL && *link != directory) link = &(*link)->Next;
    if (*link == NULL) return -EBADF;
    *link = directory->Next;
    const int result = directory->Directory == NULL ? 0 : closedir(directory->Directory);
    free(directory->PendingName);
    free(directory);
    return result == 0 ? 0 : -(errno == 0 ? EIO : errno);
}

static void close_process_io(ct_process *process)
{
    while (process->Files != NULL) (void)close_process_file(process, process->Files);
    while (process->Directories != NULL) (void)close_process_directory(process, process->Directories);
}

static uint8_t metadata_kind(mode_t mode)
{
    if (S_ISREG(mode)) return 1;
    if (S_ISDIR(mode)) return 2;
#ifdef S_ISLNK
    if (S_ISLNK(mode)) return 3;
#endif
    return 4;
}

static uint32_t metadata_attributes(const char *path, const struct stat *value)
{
    uint32_t result = 0;
    if ((value->st_mode & S_IWUSR) == 0) result |= 1u;
    const char *name = strrchr(path, '/');
    name = name == NULL ? path : name + 1;
    if (name[0] == '.' && name[1] != '\0') result |= 2u;
    if (S_ISDIR(value->st_mode)) result |= 4u;
#ifdef S_ISLNK
    if (S_ISLNK(value->st_mode)) result |= 8u;
#endif
    return result;
}

static int stat_path(const char *path, struct stat *value) { return stat(path, value); }

static int make_directories(char *path)
{
    for (char *cursor = path + 1; *cursor != '\0'; ++cursor) {
        if (*cursor != '/') continue;
        *cursor = '\0';
        if (mkdir(path, 0777) != 0 && errno != EEXIST) { *cursor = '/'; return -errno; }
        *cursor = '/';
    }
    if (mkdir(path, 0777) != 0 && errno != EEXIST) return -errno;
    return 0;
}

static int remove_tree(const char *path)
{
    struct stat info;
    if (stat_path(path, &info) != 0) return -errno;
    if (!S_ISDIR(info.st_mode)) return unlink(path) == 0 ? 0 : -errno;
    DIR *directory = opendir(path);
    if (directory == NULL) return -errno;
    int result = 0;
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;
        char child[CT_MODULE_PATH_MAX];
        const int written = snprintf(child, sizeof(child), "%s/%s", path, entry->d_name);
        if (written <= 0 || written >= (int)sizeof(child)) { result = -ENAMETOOLONG; break; }
        result = remove_tree(child);
        if (result != 0) break;
    }
    const int close_result = closedir(directory);
    if (result == 0 && close_result != 0) result = -errno;
    if (result == 0 && rmdir(path) != 0) result = -errno;
    return result;
}

static bool path_has_prefix(const char *path, const char *prefix)
{
    const size_t length = strlen(prefix);
    return strncmp(path, prefix, length) == 0 && (path[length] == '\0' || path[length] == '/');
}

static bool process_uses_overlay_prefix(const ct_process *process, const char *prefix)
{
    if (process == NULL || prefix == NULL || !process->HasOverlays) return false;
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        const ct_module *module = process->Instances[index].Module;
        if (module != NULL && module->OverlayCount != 0u && path_has_prefix(module->Path, prefix)) return true;
    }
    return false;
}


bool ctilde_managed_storage_prefix_busy(const char *prefix)
{
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || prefix == NULL) return false;
    bool busy = false;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_process *process = &s_processes[index];
        if (process->Used && !__atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE) &&
            process_uses_overlay_prefix(process, prefix)) {
            busy = true;
            break;
        }
    }
    xSemaphoreGive(s_registry);
    return busy;
}

void ctilde_managed_storage_invalidate_prefix(const char *prefix, uint64_t generation)
{
    (void)generation;
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || prefix == NULL) return;
    uint32_t terminate_ids[CONFIG_CTILDE_MANAGED_MAX_PROCESSES] = { 0 };
    size_t terminate_count = 0u;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_MODULES; ++index) {
        ct_module *module = &s_modules[index];
        if (!module->Used || module->OverlayCount == 0u || !path_has_prefix(module->Path, prefix)) continue;
        __atomic_store_n(&module->OverlayUnavailable, true, __ATOMIC_RELEASE);
        if (module->OverlayLock != NULL) xSemaphoreTake(module->OverlayLock, portMAX_DELAY);
        if (module->OverlayStream != NULL) {
            (void)fclose(module->OverlayStream);
            module->OverlayStream = NULL;
        }
        if (module->OverlayLock != NULL) xSemaphoreGive(module->OverlayLock);
    }
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_process *process = &s_processes[index];
        if (!process->Used) continue;
        ct_process_file *file = process->Files;
        while (file != NULL) {
            ct_process_file *next = file->Next;
            if (file->Stream != NULL && path_has_prefix(file->Path, prefix)) {
                (void)fclose(file->Stream);
                file->Stream = NULL;
            }
            file = next;
        }
        ct_process_directory *directory = process->Directories;
        while (directory != NULL) {
            ct_process_directory *next = directory->Next;
            if (directory->Directory != NULL && path_has_prefix(directory->Path, prefix)) {
                (void)closedir(directory->Directory);
                directory->Directory = NULL;
            }
            directory = next;
        }
        if (process->CurrentDirectory != NULL && path_has_prefix(process->CurrentDirectory, prefix)) {
            free(process->CurrentDirectory);
            process->CurrentDirectory = malloc(2);
            if (process->CurrentDirectory != NULL) { process->CurrentDirectory[0] = '/'; process->CurrentDirectory[1] = '\0'; }
        }
        if (!__atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE) &&
            process_uses_overlay_prefix(process, prefix) && terminate_count < CONFIG_CTILDE_MANAGED_MAX_PROCESSES)
            terminate_ids[terminate_count++] = process->Id;
    }
    xSemaphoreGive(s_registry);
    /* Process termination waits and may re-enter the registry. Never invoke it
       while the registry lock used above is held. */
    for (size_t index = 0; index < terminate_count; ++index)
        ct_managed_process_terminate((uintptr_t)terminate_ids[index], 0u);
}

static int32_t api_file_service(ct_process *process, uint32_t service, void *payload, size_t size)
{
    if (service == CT_RUNTIME_SERVICE_FILE_OPEN) {
        if (payload == NULL || size != sizeof(ct_runtime_io_open_v19)) return -EINVAL;
        ct_runtime_io_open_v19 *request = payload;
        if (request->Size != sizeof(*request) || request->Mode > 5 || request->Access > 2) return -EINVAL;
        char path[CT_MODULE_PATH_MAX];
        int result = copy_runtime_path(process, request->Path, request->PathLength, path);
        if (result != 0) return result;
        int flags = request->Access == 0 ? O_RDONLY : request->Access == 1 ? O_WRONLY : O_RDWR;
        if (request->Mode == 1) flags |= O_CREAT | O_TRUNC;
        else if (request->Mode == 2) flags |= O_CREAT | O_APPEND;
        else if (request->Mode == 3) flags |= O_CREAT | O_EXCL;
        else if (request->Mode == 4) flags |= O_CREAT;
        else if (request->Mode == 5) flags |= O_TRUNC;
        int descriptor = open(path, flags, 0666);
        if (descriptor < 0) return -errno;
        const char *mode = request->Access == 0 ? "rb" :
            request->Mode == 2 ? (request->Access == 1 ? "ab" : "a+b") :
            request->Access == 1 ? "wb" : "r+b";
        FILE *stream = fdopen(descriptor, mode);
        if (stream == NULL) { const int error = errno; close(descriptor); return -error; }
        ct_process_file *file = calloc(1, sizeof(*file));
        if (file == NULL) { fclose(stream); return -ENOMEM; }
        file->Stream = stream;
        (void)snprintf(file->Path, sizeof(file->Path), "%s", path);
        file->Next = process->Files;
        process->Files = file;
        request->Handle = (uintptr_t)file;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_FILE_READ || service == CT_RUNTIME_SERVICE_FILE_WRITE) {
        if (payload == NULL || size != sizeof(ct_runtime_io_transfer_v19)) return -EINVAL;
        ct_runtime_io_transfer_v19 *request = payload;
        ct_process_file *file = request->Size == sizeof(*request) ? find_process_file(process, request->Handle) : NULL;
        if (file == NULL || (request->Length != 0 && request->Data == NULL)) return -EBADF;
        request->Count = service == CT_RUNTIME_SERVICE_FILE_READ
            ? fread(request->Data, 1, request->Length, file->Stream)
            : fwrite(request->Data, 1, request->Length, file->Stream);
        request->Eof = service == CT_RUNTIME_SERVICE_FILE_READ && feof(file->Stream);
        if (ferror(file->Stream)) { const int error = errno == 0 ? EIO : errno; clearerr(file->Stream); return -error; }
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_FILE_SEEK) {
        if (payload == NULL || size != sizeof(ct_runtime_io_seek_v19)) return -EINVAL;
        ct_runtime_io_seek_v19 *request = payload;
        ct_process_file *file = request->Size == sizeof(*request) ? find_process_file(process, request->Handle) : NULL;
        if (file == NULL || request->Origin > 2) return -EBADF;
        const int origin = request->Origin == 0 ? SEEK_SET : request->Origin == 1 ? SEEK_CUR : SEEK_END;
        if (fseeko(file->Stream, (off_t)request->Offset, origin) != 0) return -errno;
        const off_t position = ftello(file->Stream);
        if (position < 0) return -errno;
        request->Value = (int64_t)position;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_FILE_LENGTH || service == CT_RUNTIME_SERVICE_FILE_SET_LENGTH) {
        if (payload == NULL || size != sizeof(ct_runtime_io_value_v19)) return -EINVAL;
        ct_runtime_io_value_v19 *request = payload;
        ct_process_file *file = request->Size == sizeof(*request) ? find_process_file(process, request->Handle) : NULL;
        if (file == NULL) return -EBADF;
        if (service == CT_RUNTIME_SERVICE_FILE_SET_LENGTH)
            return ftruncate(fileno(file->Stream), (off_t)request->Value) == 0 ? 0 : -errno;
        const off_t current = ftello(file->Stream);
        if (current < 0 || fseeko(file->Stream, 0, SEEK_END) != 0) return -errno;
        const off_t length = ftello(file->Stream);
        if (length < 0 || fseeko(file->Stream, current, SEEK_SET) != 0) return -errno;
        request->Value = (int64_t)length;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_FILE_FLUSH || service == CT_RUNTIME_SERVICE_FILE_CLOSE) {
        if (payload == NULL || size != sizeof(ct_runtime_io_handle_v19)) return -EINVAL;
        ct_runtime_io_handle_v19 *request = payload;
        ct_process_file *file = request->Size == sizeof(*request) ? find_process_file(process, request->Handle) : NULL;
        if (file == NULL) return -EBADF;
        return service == CT_RUNTIME_SERVICE_FILE_CLOSE ? close_process_file(process, file) :
            (fflush(file->Stream) == 0 ? 0 : -errno);
    }
    return -ENOSYS;
}

static int32_t api_path_service(ct_process *process, uint32_t service, void *payload, size_t size)
{
    if (service == CT_RUNTIME_SERVICE_PATH_METADATA) {
        if (payload == NULL || size != sizeof(ct_runtime_io_metadata_v19)) return -EINVAL;
        ct_runtime_io_metadata_v19 *request = payload;
        char path[CT_MODULE_PATH_MAX];
        int result = request->Size == sizeof(*request) ? copy_runtime_path(process, request->Path, request->PathLength, path) : -EINVAL;
        if (result != 0) return result;
        struct stat info;
        if (stat_path(path, &info) != 0) return -errno;
        request->Kind = metadata_kind(info.st_mode);
        request->Attributes = metadata_attributes(path, &info);
        request->Length = (int64_t)info.st_size;
        request->HasCreationTime = false;
        request->HasAccessTime = true; request->AccessSeconds = (int64_t)info.st_atime; request->AccessNanoseconds = 0;
        request->HasModificationTime = true; request->ModificationSeconds = (int64_t)info.st_mtime; request->ModificationNanoseconds = 0;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_FILE_DELETE || service == CT_RUNTIME_SERVICE_DIRECTORY_CREATE ||
        service == CT_RUNTIME_SERVICE_DIRECTORY_DELETE || service == CT_RUNTIME_SERVICE_CURRENT_DIRECTORY_SET) {
        if (payload == NULL || size != sizeof(ct_runtime_io_path_flag_v19)) return -EINVAL;
        ct_runtime_io_path_flag_v19 *request = payload;
        char path[CT_MODULE_PATH_MAX];
        int result = request->Size == sizeof(*request) ? copy_runtime_path(process, request->Path, request->PathLength, path) : -EINVAL;
        if (result != 0) return result;
        if (service == CT_RUNTIME_SERVICE_FILE_DELETE) return unlink(path) == 0 ? 0 : -errno;
        if (service == CT_RUNTIME_SERVICE_DIRECTORY_CREATE) return make_directories(path);
        if (service == CT_RUNTIME_SERVICE_DIRECTORY_DELETE)
            return request->Flag ? remove_tree(path) : (rmdir(path) == 0 ? 0 : -errno);
        struct stat info;
        if (stat(path, &info) != 0) return -errno;
        if (!S_ISDIR(info.st_mode)) return -ENOTDIR;
        char *replacement = malloc(strlen(path) + 1);
        if (replacement == NULL) return -ENOMEM;
        (void)memcpy(replacement, path, strlen(path) + 1);
        free(process->CurrentDirectory);
        process->CurrentDirectory = replacement;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_PATH_MOVE) {
        if (payload == NULL || size != sizeof(ct_runtime_io_two_paths_v19)) return -EINVAL;
        ct_runtime_io_two_paths_v19 *request = payload;
        char source[CT_MODULE_PATH_MAX], destination[CT_MODULE_PATH_MAX];
        int result = request->Size == sizeof(*request) ? copy_runtime_path(process, request->Source, request->SourceLength, source) : -EINVAL;
        if (result == 0) result = copy_runtime_path(process, request->Destination, request->DestinationLength, destination);
        if (result != 0) return result;
        struct stat ignored;
        if (!request->Flag && stat_path(destination, &ignored) == 0) return -EEXIST;
        return rename(source, destination) == 0 ? 0 : -errno;
    }
    if (service == CT_RUNTIME_SERVICE_CURRENT_DIRECTORY_GET) {
        if (payload == NULL || size != sizeof(ct_runtime_io_transfer_v19)) return -EINVAL;
        ct_runtime_io_transfer_v19 *request = payload;
        if (request->Size != sizeof(*request)) return -EINVAL;
        const char *current = process->CurrentDirectory == NULL ? "/" : process->CurrentDirectory;
        request->Count = strlen(current);
        if (request->Length < request->Count) return -ENOBUFS;
        if (request->Count != 0 && request->Data == NULL) return -EINVAL;
        (void)memcpy(request->Data, current, request->Count);
        return 0;
    }
    return -ENOSYS;
}

static int32_t api_directory_service(ct_process *process, uint32_t service, void *payload, size_t size)
{
    if (service == CT_RUNTIME_SERVICE_DIRECTORY_OPEN) {
        if (payload == NULL || size != sizeof(ct_runtime_io_open_v19)) return -EINVAL;
        ct_runtime_io_open_v19 *request = payload;
        char path[CT_MODULE_PATH_MAX];
        int result = request->Size == sizeof(*request) ? copy_runtime_path(process, request->Path, request->PathLength, path) : -EINVAL;
        if (result != 0) return result;
        DIR *stream = opendir(path);
        if (stream == NULL) return -errno;
        ct_process_directory *directory = calloc(1, sizeof(*directory));
        if (directory == NULL) { closedir(stream); return -ENOMEM; }
        directory->Directory = stream;
        (void)snprintf(directory->Path, sizeof(directory->Path), "%s", path);
        directory->Next = process->Directories;
        process->Directories = directory;
        request->Handle = (uintptr_t)directory;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_DIRECTORY_READ) {
        if (payload == NULL || size != sizeof(ct_runtime_io_directory_read_v19)) return -EINVAL;
        ct_runtime_io_directory_read_v19 *request = payload;
        ct_process_directory *directory = request->Size == sizeof(*request) ? find_process_directory(process, request->Handle) : NULL;
        if (directory == NULL) return -EBADF;
        if (directory->PendingName == NULL) {
            struct dirent *entry;
            do { errno = 0; entry = readdir(directory->Directory); }
            while (entry != NULL && (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0));
            if (entry == NULL) return errno == 0 ? 1 : -errno;
            const size_t name_length = strlen(entry->d_name);
            directory->PendingName = malloc(name_length + 1);
            if (directory->PendingName == NULL) return -ENOMEM;
            (void)memcpy(directory->PendingName, entry->d_name, name_length + 1);
            char path[CT_MODULE_PATH_MAX];
            const int written = snprintf(path, sizeof(path), "%s/%s", directory->Path, entry->d_name);
            struct stat info;
            if (written > 0 && written < (int)sizeof(path) && stat_path(path, &info) == 0) {
                directory->PendingKind = metadata_kind(info.st_mode);
                directory->PendingAttributes = metadata_attributes(path, &info);
                directory->PendingLength = (int64_t)info.st_size;
            }
        }
        request->NameLength = strlen(directory->PendingName);
        if (request->NameCapacity < request->NameLength) return -ENOBUFS;
        if (request->NameLength != 0 && request->Name == NULL) return -EINVAL;
        (void)memcpy(request->Name, directory->PendingName, request->NameLength);
        request->Kind = directory->PendingKind;
        request->Attributes = directory->PendingAttributes;
        request->Length = directory->PendingLength;
        free(directory->PendingName);
        directory->PendingName = NULL;
        directory->PendingKind = 0;
        directory->PendingAttributes = 0;
        directory->PendingLength = 0;
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_DIRECTORY_CLOSE) {
        if (payload == NULL || size != sizeof(ct_runtime_io_handle_v19)) return -EINVAL;
        ct_runtime_io_handle_v19 *request = payload;
        ct_process_directory *directory = request->Size == sizeof(*request) ? find_process_directory(process, request->Handle) : NULL;
        return directory == NULL ? -EBADF : close_process_directory(process, directory);
    }
    return -ENOSYS;
}

static int32_t api_service(uint32_t service, void *payload, size_t size)
{
    ct_execution_context *context = current_context();
    ct_process *process = context == NULL ? NULL : context->Process;
    if (service == CT_RUNTIME_SERVICE_THREAD_ATTACH) {
        if (context != NULL || payload == NULL || size != sizeof(ct_runtime_thread_attach_v23)) return -EINVAL;
        const ct_runtime_thread_attach_v23 *request = payload;
        if (request->Size != sizeof(*request) || request->Process == NULL) return -EINVAL;
        const uintptr_t address = (uintptr_t)(void *)request->Process;
        const uintptr_t first = (uintptr_t)(void *)&s_processes[0];
        const uintptr_t limit = (uintptr_t)(void *)&s_processes[CONFIG_CTILDE_MANAGED_MAX_PROCESSES];
        if (address < first || address >= limit || (address - first) % sizeof(ct_process) != 0u) return -EINVAL;
        process = (ct_process *)(void *)request->Process;
        if (!process->Used || process->HasOverlays || __atomic_load_n(&process->Cleaned, __ATOMIC_ACQUIRE))
            return process->HasOverlays ? -ENOTSUP : -EINVAL;
        ct_child_task *child = calloc(1u, sizeof(*child));
        if (child == NULL) return -ENOMEM;
        child->Handle = xTaskGetCurrentTaskHandle();
        child->Context = (ct_execution_context){ .Process = process, .Module = process->Root, .ChildTask = child };
        child->Context.ThreadState = child->Context.PrimaryThreadState;
        xSemaphoreTake(s_registry, portMAX_DELAY);
        if (__atomic_load_n(&process->Cancellation, __ATOMIC_ACQUIRE) || process->Cleaned) {
            xSemaphoreGive(s_registry);
            free(child);
            return -ECANCELED;
        }
        child->Next = process->ChildTasks;
        process->ChildTasks = child;
        (void)__atomic_add_fetch(&process->TaskCount, 1u, __ATOMIC_ACQ_REL);
        xSemaphoreGive(s_registry);
        vTaskSetThreadLocalStoragePointerAndDelCallback(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX,
            &child->Context, tls_deleted);
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_THREAD_DETACH) {
        if (context == NULL || process == NULL || context->ChildTask == NULL || context->CallDepth != 0u ||
            context->RuntimeOperationDepth != 0u) return -EINVAL;
        ct_child_task *child = context->ChildTask;
        vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, NULL);
        xSemaphoreTake(s_registry, portMAX_DELAY);
        ct_child_task **link = &process->ChildTasks;
        while (*link != NULL && *link != child) link = &(*link)->Next;
        if (*link == child) {
            *link = child->Next;
            (void)__atomic_sub_fetch(&process->TaskCount, 1u, __ATOMIC_ACQ_REL);
        }
        xSemaphoreGive(s_registry);
        free(child);
        return 0;
    }
    if (process == NULL) return -EINVAL;
    if (service == CT_RUNTIME_SERVICE_CONSOLE_WRITE) {
        if (payload == NULL || size != sizeof(ct_runtime_console_transfer_v19)) return -EINVAL;
        ct_runtime_console_transfer_v19 *transfer = (ct_runtime_console_transfer_v19 *)payload;
        if (transfer->Length != 0 && transfer->Data == NULL) return -EINVAL;
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        ct_console_endpoint *endpoint = process->Streams[1];
        if (endpoint == NULL) {
            end_runtime_operation(context);
            return -EPIPE;
        }
        xSemaphoreTake(endpoint->WriteLock, portMAX_DELAY);
        if (endpoint->Kind == CT_CONSOLE_ENDPOINT_PIPE) {
            transfer->Count = 0u;
            while (transfer->Count < transfer->Length && !__atomic_load_n(&process->Cancellation, __ATOMIC_ACQUIRE)) {
                transfer->Count += xStreamBufferSend(endpoint->Buffer, transfer->Data + transfer->Count,
                    transfer->Length - transfer->Count, pdMS_TO_TICKS(10));
                if (__atomic_load_n(&endpoint->ParentClosed, __ATOMIC_ACQUIRE)) break;
            }
        } else {
            transfer->Count = fwrite(transfer->Data, 1u, transfer->Length, stdout);
        }
        xSemaphoreGive(endpoint->WriteLock);
        transfer->Eof = false;
        const int32_t result = transfer->Count == transfer->Length ? 0 : -(errno == 0 ? EIO : errno);
        end_runtime_operation(context);
        return result;
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_READ) {
        if (payload == NULL || size != sizeof(ct_runtime_console_transfer_v19)) return -EINVAL;
        ct_runtime_console_transfer_v19 *transfer = (ct_runtime_console_transfer_v19 *)payload;
        if (transfer->Length != 0 && transfer->Data == NULL) return -EINVAL;
        ct_console_endpoint *endpoint = process->Streams[0];
        if (endpoint == NULL) return -EPIPE;
        const uint32_t foreground = __atomic_load_n(&endpoint->ForegroundProcess, __ATOMIC_ACQUIRE);
        if (foreground != 0u && foreground != process->Id) {
            transfer->Count = 0u;
            transfer->Eof = false;
        } else if (endpoint->Kind == CT_CONSOLE_ENDPOINT_PIPE) {
            transfer->Count = xStreamBufferReceive(endpoint->Buffer, transfer->Data, transfer->Length, pdMS_TO_TICKS(10));
            transfer->Eof = transfer->Count == 0u && __atomic_load_n(&endpoint->ParentClosed, __ATOMIC_ACQUIRE);
        } else {
            clearerr(stdin);
            transfer->Count = fread(transfer->Data, 1u, transfer->Length, stdin);
            transfer->Eof = feof(stdin);
            if (ferror(stdin) && errno != EAGAIN && errno != EWOULDBLOCK) return -(errno == 0 ? EIO : errno);
            clearerr(stdin);
            if (transfer->Count != 0u && s_uart_activity_hook != NULL) s_uart_activity_hook();
        }
        /* The UART VFS is nonblocking. Yield an empty poll for one tick so a
           foreground interactive module cannot starve this core's idle task
           and trigger the task watchdog. */
        if (transfer->Count == 0u && !transfer->Eof) vTaskDelay(1u);
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_FLUSH) {
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        ct_console_endpoint *endpoint = process->Streams[1];
        const int32_t result = endpoint != NULL && (endpoint->Kind == CT_CONSOLE_ENDPOINT_PIPE || fflush(stdout) == 0)
            ? 0 : -(errno == 0 ? EIO : errno);
        end_runtime_operation(context);
        return result;
    }
    if (service == CT_RUNTIME_SERVICE_PATH_SEPARATOR) return '/';
    if (service >= CT_RUNTIME_SERVICE_FILE_OPEN && service <= CT_RUNTIME_SERVICE_FILE_CLOSE) {
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        xSemaphoreTake(s_registry, portMAX_DELAY);
        const int32_t result = api_file_service(process, service, payload, size);
        xSemaphoreGive(s_registry);
        end_runtime_operation(context);
        return result;
    }
    if (service >= CT_RUNTIME_SERVICE_PATH_METADATA && service <= CT_RUNTIME_SERVICE_CURRENT_DIRECTORY_SET) {
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        xSemaphoreTake(s_registry, portMAX_DELAY);
        int32_t result;
        if (service >= CT_RUNTIME_SERVICE_DIRECTORY_OPEN && service <= CT_RUNTIME_SERVICE_DIRECTORY_CLOSE)
            result = api_directory_service(process, service, payload, size);
        else
            result = api_path_service(process, service, payload, size);
        xSemaphoreGive(s_registry);
        end_runtime_operation(context);
        return result;
    }
    return -ENOSYS;
}

static int32_t capability_file_open(const uint8_t *path, size_t length, uint8_t mode,
    uint8_t access, uintptr_t *handle)
{
    if (handle == NULL) return -EINVAL;
    *handle = 0;
    ct_runtime_io_open_v19 request = { sizeof(request), path, length, mode, access, 0 };
    const int32_t result = api_service(CT_RUNTIME_SERVICE_FILE_OPEN, &request, sizeof(request));
    if (result == 0) *handle = request.Handle;
    return result;
}

static int32_t capability_file_read(uintptr_t handle, uint8_t *data, size_t length, size_t *count, bool *eof)
{
    if (count == NULL || eof == NULL) return -EINVAL;
    *count = 0;
    *eof = false;
    ct_runtime_io_transfer_v19 request = { sizeof(request), handle, data, length, 0, false };
    const int32_t result = api_service(CT_RUNTIME_SERVICE_FILE_READ, &request, sizeof(request));
    *count = request.Count;
    *eof = request.Eof;
    return result;
}

static int32_t capability_file_write(uintptr_t handle, const uint8_t *data, size_t length, size_t *count)
{
    if (count == NULL) return -EINVAL;
    *count = 0;
    ct_runtime_io_transfer_v19 request = { sizeof(request), handle, (uint8_t *)data, length, 0, false };
    const int32_t result = api_service(CT_RUNTIME_SERVICE_FILE_WRITE, &request, sizeof(request));
    *count = request.Count;
    return result;
}

static int32_t capability_file_seek(uintptr_t handle, int64_t offset, uint8_t origin, int64_t *position)
{
    if (position == NULL) return -EINVAL;
    *position = 0;
    ct_runtime_io_seek_v19 request = { sizeof(request), handle, offset, origin, 0 };
    const int32_t result = api_service(CT_RUNTIME_SERVICE_FILE_SEEK, &request, sizeof(request));
    if (result == 0) *position = request.Value;
    return result;
}

static int32_t capability_file_length(uintptr_t handle, int64_t *length)
{
    if (length == NULL) return -EINVAL;
    *length = 0;
    ct_runtime_io_value_v19 request = { sizeof(request), handle, 0 };
    const int32_t result = api_service(CT_RUNTIME_SERVICE_FILE_LENGTH, &request, sizeof(request));
    if (result == 0) *length = request.Value;
    return result;
}

static int32_t capability_file_flush(uintptr_t handle)
{
    ct_runtime_io_handle_v19 request = { sizeof(request), handle };
    return api_service(CT_RUNTIME_SERVICE_FILE_FLUSH, &request, sizeof(request));
}

static int32_t capability_file_close(uintptr_t handle)
{
    ct_runtime_io_handle_v19 request = { sizeof(request), handle };
    return api_service(CT_RUNTIME_SERVICE_FILE_CLOSE, &request, sizeof(request));
}

static void capability_process_delay(uint32_t milliseconds) { vTaskDelay(pdMS_TO_TICKS(milliseconds)); }
static uint64_t capability_process_clock(void) { return (uint64_t)esp_timer_get_time() / UINT64_C(1000); }

static const ct_filesystem_api_v1 s_filesystem_api = {
    sizeof(ct_filesystem_api_v1), 1u, capability_file_open, capability_file_read,
    capability_file_write, capability_file_seek, capability_file_length, capability_file_flush, capability_file_close
};
static const ct_process_api_v1 s_process_api = {
    sizeof(ct_process_api_v1), 1u, ct_managed_process_current, ct_managed_process_id,
    ct_managed_process_cancellation_requested, capability_process_delay, capability_process_clock,
    ctilde_managed_process_terminate_descendants
};
static const ct_core_api_v1 s_core_api = {
    sizeof(ct_core_api_v1), 1u, api_allocate, api_free, api_free, api_runtime_fault
};
static const ct_buffer_api_v1 s_buffer_api = {
    sizeof(ct_buffer_api_v1), 1u, memcpy, memmove, memset, memcmp, memchr,
    ctilde_buffer_hash_bytes, ctilde_buffer_validate_utf8, ctilde_buffer_encode_rune,
    ctilde_buffer_format_unsigned, ctilde_buffer_format_signed
};
static const ct_runtime_api_v23 s_runtime_api = {
    sizeof(ct_runtime_api_v23), CTILDE_RUNTIME_ABI_VERSION, api_allocate, api_free, api_free, NULL, api_runtime_fault,
    api_register_type, api_unregister_types, api_current_process, api_current_module_state,
    api_current_thread_state, api_set_thread_state, ct_managed_process_cancellation_requested,
    api_enter_managed_call, api_leave_managed_call, api_service, ctilde_managed_get_capability
};

const ct_runtime_api_v23 *ctilde_runtime_api_v23(void)
{
    return &s_runtime_api;
}

static void *api_current_thread_state(void)
{
    ct_execution_context *context = current_context();
    return context == NULL ? NULL : context->ThreadState;
}

static void api_set_thread_state(void *state)
{
    ct_execution_context *context = current_context();
    if (context == NULL) api_runtime_fault("CTT0017", "<thread-state>", 0);
    context->ThreadState = state;
}

static void release_arena(ct_process *process)
{
    while (process->Allocations != NULL) {
        ct_allocation *allocation = process->Allocations;
        process->Allocations = allocation->Next;
        if (allocation->Module == NULL ||
            __atomic_fetch_sub(&allocation->Module->LiveAllocations, 1u, __ATOMIC_ACQ_REL) == 0) abort();
        free(allocation);
    }
    __atomic_store_n(&process->HeapBytes, 0u, __ATOMIC_RELEASE);
}

static void release_process_streams(ct_process *process)
{
    for (size_t stream = 0; stream < 3u; ++stream) release_endpoint_child(process, stream);
}

static bool process_streams_released(const ct_process *process)
{
    for (size_t stream = 0; stream < 3u; ++stream)
        if (process->Streams[stream] != NULL) return false;
    return true;
}

static void close_process_parent_streams(ct_process *process)
{
    for (size_t stream = 0; stream < 3u; ++stream) {
        ct_console_endpoint *endpoint = process->Streams[stream];
        if (endpoint == NULL || !process->OwnsParentStream[stream]) continue;
        process->OwnsParentStream[stream] = false;
        __atomic_store_n(&endpoint->ParentClosed, true, __ATOMIC_RELEASE);
        process->Streams[stream] = NULL;
        endpoint->Owner = NULL;
        release_endpoint_reference(endpoint);
    }
}

static void release_native_resources(ct_process *process)
{
    while (process->NativeResources != NULL) {
        ct_native_resource *resource = process->NativeResources;
        process->NativeResources = resource->Next;
        resource->Release(resource->Value);
        free(resource);
    }
}

static void terminate_child_tasks(ct_process *process)
{
    for (;;) {
        xSemaphoreTake(s_registry, portMAX_DELAY);
        ct_child_task *child = process->ChildTasks;
        TaskHandle_t handle = child == NULL ? NULL : child->Handle;
        xSemaphoreGive(s_registry);
        if (handle == NULL) return;
        vTaskDelete(handle);
    }
}

static void cleanup_process(ct_process *process, bool forced)
{
    if (__atomic_exchange_n(&process->Cleaned, true, __ATOMIC_ACQ_REL)) return;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    const ct_managed_process_state state = __atomic_load_n(&process->State, __ATOMIC_ACQUIRE);
    if (state < CT_PROCESS_EXITED) {
        if (forced) __atomic_store_n(&process->ExitCode, -1, __ATOMIC_RELEASE);
        __atomic_store_n(&process->State, forced ? CT_PROCESS_FAILED : CT_PROCESS_EXITED, __ATOMIC_RELEASE);
    }
    xSemaphoreGive(s_registry);
    terminate_child_tasks(process);
    ct_execution_context *previous_context = current_context();
    ct_execution_context cleanup_context = { .Process = process, .Module = process->Root };
    cleanup_context.ThreadState = cleanup_context.PrimaryThreadState;
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, &cleanup_context);
    if (!forced) {
        for (uint32_t index = process->InstanceCount; index > 0; --index) {
            ct_module_instance *instance = &process->Instances[index - 1];
            if (instance->Initialized) {
                if (!enter_context_call(&cleanup_context, instance->Module)) abort();
                instance->Module->Descriptor->Finalize();
                if (!leave_context_call(&cleanup_context, instance->Module)) abort();
                instance->Initialized = false;
            }
        }
    }
    xSemaphoreTake(s_registry, portMAX_DELAY);
    release_process_streams(process);
    release_native_resources(process);
    close_process_io(process);
    free(process->CurrentDirectory);
    process->CurrentDirectory = NULL;
    xSemaphoreGive(s_registry);
    release_arena(process);
    if (process->OverlayWindow != NULL) {
        heap_caps_free((void *)(uintptr_t)process->OverlayWindow);
        process->OverlayWindow = NULL;
        process->OverlayWindowSize = 0u;
    }
    for (uint32_t index = process->InstanceCount; index > 0; --index) {
        free(process->Instances[index - 1].State);
        process->Instances[index - 1].State = NULL;
    }
    process->InstanceCount = 0;
    ct_message *message = NULL;
    while (xQueueReceive(process->Mailbox, &message, 0) == pdTRUE) free(message);
    for (int32_t index = 0; index < process->ArgumentCount; ++index) { free(process->Arguments[index]); process->Arguments[index] = NULL; }
    process->ArgumentCount = 0;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_module *root = process->Root;
    process->Root = NULL;
    xSemaphoreGive(s_registry);
    __atomic_store_n(&process->MainTask, NULL, __ATOMIC_RELEASE);
    __atomic_store_n(&process->TaskCount, 0u, __ATOMIC_RELEASE);
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, previous_context);
    release_module(root);
    ctilde_managed_memory_sample("process_resources_released", process->Id);
    __atomic_store_n(&process->Completed, true, __ATOMIC_RELEASE);
    xSemaphoreGive(process->Completion);
}

static void tls_deleted(int index, void *value)
{
    (void)index;
    ct_execution_context *context = (ct_execution_context *)value;
    if (context == NULL || context->Process == NULL) return;
    ct_process *process = context->Process;
    abandon_runtime_operations(context);
    abandon_context_calls(context);
    if (context->ChildTask != NULL) {
        ct_child_task *child = context->ChildTask;
        xSemaphoreTake(s_registry, portMAX_DELAY);
        ct_child_task **link = &process->ChildTasks;
        while (*link != NULL && *link != child) link = &(*link)->Next;
        if (*link == child) {
            *link = child->Next;
            (void)__atomic_sub_fetch(&process->TaskCount, 1u, __ATOMIC_ACQ_REL);
        }
        xSemaphoreGive(s_registry);
        free(child);
        return;
    }
    /* A fault in a module finalizer deletes the task that claimed cleanup. Let
       the reaper finish the remaining non-managed reclamation without invoking
       finalizers again. */
    if (!__atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE))
        __atomic_store_n(&process->Cleaned, false, __ATOMIC_RELEASE);
    if (__atomic_exchange_n(&process->CleanupQueued, true, __ATOMIC_ACQ_REL)) return;
    if (xQueueSend(s_reaper_queue, &process, 0) != pdTRUE) abort();
}

static void reaper_main(void *argument)
{
    (void)argument;
    for (;;) {
        ct_process *process = NULL;
        if (xQueueReceive(s_reaper_queue, &process, portMAX_DELAY) == pdTRUE && process != NULL) cleanup_process(process, true);
    }
}

static void process_main(void *argument)
{
    ct_process *process = (ct_process *)argument;
    process->Context = (ct_execution_context){ .Process = process, .Module = process->Root };
    process->Context.ThreadState = process->Context.PrimaryThreadState;
    vTaskSetThreadLocalStoragePointerAndDelCallback(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, &process->Context, tls_deleted);
    /* Start does not publish the process handle until deletion always has a
       TLS callback that can queue blocking cleanup on the reaper. */
    const size_t process_index = (size_t)(process - s_processes);
    __atomic_store_n(&s_published_process_ids[process_index], process->Id, __ATOMIC_RELEASE);
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        ct_module_instance *instance = &process->Instances[index];
        if (!enter_context_call(&process->Context, instance->Module)) api_runtime_fault("CTT0018", "<module-initialize>", 0);
        instance->Module->Descriptor->Initialize();
        if (!leave_context_call(&process->Context, instance->Module)) abort();
        instance->Initialized = true;
    }
    process->Context.Module = process->Root;
    ct_managed_process_state expected_state = CT_PROCESS_STARTING;
    (void)__atomic_compare_exchange_n(&process->State, &expected_state, CT_PROCESS_RUNNING, false,
        __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
    void *managed_arguments = process->Root->Descriptor->CreateArguments(process->ArgumentCount,
        (const char *const *)process->Arguments, process->ArgumentLengths);
    if (!enter_context_call(&process->Context, process->Root)) api_runtime_fault("CTT0018", "<module-main>", 0);
    const int32_t exit_code = process->Root->Descriptor->Main(managed_arguments);
    __atomic_store_n(&process->ExitCode, exit_code, __ATOMIC_RELEASE);
    if (!leave_context_call(&process->Context, process->Root)) abort();
    if (__atomic_load_n(&process->TaskCount, __ATOMIC_ACQUIRE) > 1u) {
        __atomic_store_n(&process->Cancellation, true, __ATOMIC_RELEASE);
        while (__atomic_load_n(&process->TaskCount, __ATOMIC_ACQUIRE) > 1u &&
            !__atomic_load_n(&process->ForceDeleteIssued, __ATOMIC_ACQUIRE))
            vTaskDelay(pdMS_TO_TICKS(10));
    }
    TaskHandle_t self = xTaskGetCurrentTaskHandle();
    TaskHandle_t expected = self;
    if (!__atomic_compare_exchange_n(&process->MainTask, &expected, NULL, false,
            __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
        /* Terminate owns deletion now. Keep the TLS cleanup callback installed
           and stop touching process or module state until that deletion runs. */
        for (;;) vTaskDelay(portMAX_DELAY);
    }
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, NULL);
    cleanup_process(process, false);
    vTaskDelete(NULL);
}

static ct_process *allocate_process(void)
{
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        if (s_processes[index].Used && __atomic_load_n(&s_processes[index].Completed, __ATOMIC_ACQUIRE) &&
            process_streams_released(&s_processes[index])) {
            __atomic_store_n(&s_published_process_ids[index], 0u, __ATOMIC_RELEASE);
            if (s_processes[index].Completion != NULL) vSemaphoreDelete(s_processes[index].Completion);
            if (s_processes[index].Mailbox != NULL) vQueueDelete(s_processes[index].Mailbox);
            (void)memset(&s_processes[index], 0, sizeof(s_processes[index]));
        }
        if (!s_processes[index].Used) {
            ct_process *process = &s_processes[index];
            (void)memset(process, 0, sizeof(*process));
            process->AllocationLock = (portMUX_TYPE)portMUX_INITIALIZER_UNLOCKED;
            process->Used = true;
            process->Id = s_next_process_id++;
            process->CurrentDirectory = malloc(2);
            if (process->CurrentDirectory == NULL) {
                (void)memset(process, 0, sizeof(*process));
                return NULL;
            }
            process->CurrentDirectory[0] = '/';
            process->CurrentDirectory[1] = '\0';
            process->Completion = xSemaphoreCreateBinaryStatic(&process->CompletionStorage);
            process->Mailbox = xQueueCreateStatic(CT_MAILBOX_DEPTH, sizeof(ct_message *), process->MailboxBuffer, &process->MailboxStorage);
            if (process->Completion == NULL || process->Mailbox == NULL) {
                if (process->Completion != NULL) vSemaphoreDelete(process->Completion);
                if (process->Mailbox != NULL) vQueueDelete(process->Mailbox);
                (void)memset(process, 0, sizeof(*process));
                return NULL;
            }
            return process;
        }
    }
    return NULL;
}

static uintptr_t fail_unpublished_process_start(ct_process *process)
{
    /* The reserved task has no TLS cleanup callback and cannot enter managed
       code until the starter sends its notification. Delete it before reuse. */
    if (process->MainTask != NULL) {
        vTaskDelete(process->MainTask);
        process->MainTask = NULL;
        process->TaskCount = 0u;
    }
    __atomic_store_n(&process->State, CT_PROCESS_FAILED, __ATOMIC_RELEASE);
    cleanup_process(process, true);
    vSemaphoreDelete(process->Completion);
    vQueueDelete(process->Mailbox);
    close_process_parent_streams(process);
    xSemaphoreTake(s_registry, portMAX_DELAY);
    (void)memset(process, 0, sizeof(*process));
    xSemaphoreGive(s_registry);
    return 0;
}

static void reserved_process_main(void *argument)
{
    (void)ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    process_main(argument);
}

static uintptr_t start_process_core(const void *path_value, const void *arguments_value,
    bool redirect_input, bool redirect_output, bool redirect_error)
{
    const ct_managed_string *path = (const ct_managed_string *)path_value;
    const ct_managed_array *arguments = (const ct_managed_array *)arguments_value;
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || path == NULL || arguments == NULL || path->Length <= 0 || arguments->Length < 0 || arguments->Length > CT_MAX_ARGUMENTS) return 0;
    char path_buffer[CT_MODULE_PATH_MAX];
    if (resolve_module_path_bytes(path->Data, (size_t)path->Length, path_buffer) != 0) return 0;
    const char *overlay_chain[1] = { NULL };
    size_t reserved_overlay_bytes = 0u;
    uint32_t reserved_stack_bytes = 0u;
    char reserved_task_name[CT_MODULE_NAME_MAX];
    const int overlay_result = inspect_process_overlay_requirement(path_buffer, overlay_chain, 0u,
        &reserved_overlay_bytes, &reserved_stack_bytes, reserved_task_name);
    if (overlay_result != 0) {
        ESP_LOGE(TAG, "Managed process overlay preflight failed: %d", overlay_result);
        return 0;
    }
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_process *process = allocate_process();
    xSemaphoreGive(s_registry);
    if (process == NULL) return 0;
    ctilde_managed_memory_sample("process_state_created", process->Id);
    process->TaskCount = 1u;
    if (xTaskCreate(reserved_process_main, reserved_task_name, reserved_stack_bytes, process,
            tskIDLE_PRIORITY + 1, &process->MainTask) != pdPASS) {
        ESP_LOGE(TAG, "Managed task reservation failed: stack=%u, free=%u, largest=%u",
            (unsigned)reserved_stack_bytes, (unsigned)heap_caps_get_free_size(MALLOC_CAP_8BIT),
            (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_8BIT));
        return fail_unpublished_process_start(process);
    }
    ctilde_managed_memory_sample("process_stack_reserved", process->Id);
    volatile uint32_t *reserved_overlay = NULL;
    if (reserved_overlay_bytes != 0u) {
        reserved_overlay = heap_caps_aligned_alloc(16u, reserved_overlay_bytes,
            MALLOC_CAP_EXEC | MALLOC_CAP_32BIT);
        if (reserved_overlay == NULL) {
            ESP_LOGE(TAG, "Managed process overlay reservation failed: requested=%u, executable-free=%u, "
                "executable-largest=%u", (unsigned)reserved_overlay_bytes,
                (unsigned)heap_caps_get_free_size(MALLOC_CAP_EXEC | MALLOC_CAP_32BIT),
                (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_EXEC | MALLOC_CAP_32BIT));
            return fail_unpublished_process_start(process);
        }
    }
    process->OverlayWindow = reserved_overlay;
    process->OverlayWindowSize = reserved_overlay_bytes;
    ct_module *module = NULL;
    const char *chain[1] = { NULL };
    const int module_result = load_module_recursive(path_buffer, chain, 0, &module);
    if (module_result != 0) {
        ESP_LOGE(TAG, "Managed process module load failed: %d", module_result);
        return fail_unpublished_process_start(process);
    }
    process->Root = module;
    if (module->Descriptor->Kind != 1 || module->Descriptor->Main == NULL || module->Descriptor->CreateArguments == NULL ||
        module->Descriptor->MainTaskStackBytes != reserved_stack_bytes)
        return fail_unpublished_process_start(process);
    ct_process *parent = current_process();
    process->ParentId = parent == NULL ? 0u : parent->Id;
    (void)snprintf(process->RootName, sizeof(process->RootName), "%s", module->Name);
    process->State = CT_PROCESS_STARTING;
    process->HeapLimit = (size_t)module->Descriptor->HeapLimitBytes;
    const bool redirects[3] = { redirect_input, redirect_output, redirect_error };
    for (size_t stream = 0; stream < 3u; ++stream) {
        if (redirects[stream]) {
            process->Streams[stream] = create_pipe_endpoint(process, (uint8_t)stream);
            process->OwnsParentStream[stream] = true;
        } else if (parent != NULL && parent->Streams[stream] != NULL) {
            process->Streams[stream] = retain_endpoint(parent->Streams[stream]);
        } else {
            process->Streams[stream] = retain_endpoint(&s_uart_streams[stream]);
        }
        if (process->Streams[stream] == NULL) {
            ESP_LOGE(TAG, "Managed process %u stream %u allocation failed", (unsigned)process->Id,
                (unsigned)stream);
            return fail_unpublished_process_start(process);
        }
    }
    if (__atomic_load_n(&process->Streams[0]->ForegroundProcess, __ATOMIC_ACQUIRE) == 0u)
        __atomic_store_n(&process->Streams[0]->ForegroundProcess, process->Id, __ATOMIC_RELEASE);
    process->ArgumentCount = arguments->Length;
    ct_managed_string *const *values = (ct_managed_string *const *)(const void *)arguments->Data;
    for (int32_t index = 0; index < arguments->Length; ++index) {
        if (values[index] == NULL || values[index]->Length < 0) return fail_unpublished_process_start(process);
        const size_t length = (size_t)values[index]->Length;
        process->Arguments[index] = (char *)malloc(length + 1);
        if (process->Arguments[index] == NULL) return fail_unpublished_process_start(process);
        (void)memcpy(process->Arguments[index], values[index]->Data, length);
        process->Arguments[index][length] = '\0';
        process->ArgumentLengths[index] = length;
    }
    const int instance_result = add_instance_graph(process, module);
    if (instance_result != 0) {
        ESP_LOGE(TAG, "Managed process %u instance graph failed: %d", (unsigned)process->Id,
            instance_result);
        return fail_unpublished_process_start(process);
    }
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        ct_module *instance_module = process->Instances[index].Module;
        process->HasOverlays |= instance_module->Descriptor->HasOverlays != 0u;
        if (instance_module->MaximumOverlayBytes > process->OverlayWindowSize) {
            ESP_LOGE(TAG, "Managed process overlay graph changed after preflight: reserved=%u, required=%u",
                (unsigned)process->OverlayWindowSize, (unsigned)instance_module->MaximumOverlayBytes);
            return fail_unpublished_process_start(process);
        }
    }
    ctilde_managed_memory_sample("process_ready", process->Id);
    xTaskNotifyGive(process->MainTask);
    const size_t process_index = (size_t)(process - s_processes);
    while (__atomic_load_n(&s_published_process_ids[process_index], __ATOMIC_ACQUIRE) != process->Id) vTaskDelay(1);
    return (uintptr_t)process->Id;
}

uintptr_t ct_managed_process_start(const void *path_value, const void *arguments_value)
{
    ct_execution_context *context = current_context();
    if (context != NULL && !begin_runtime_operation(context)) await_forced_task_deletion();
    const uintptr_t result = start_process_core(path_value, arguments_value, false, false, false);
    if (context != NULL) end_runtime_operation(context);
    return result;
}

uintptr_t ct_managed_process_start_redirected(const void *path_value, const void *arguments_value,
    bool redirect_input, bool redirect_output, bool redirect_error)
{
    ct_execution_context *context = current_context();
    if (context != NULL && !begin_runtime_operation(context)) await_forced_task_deletion();
    const uintptr_t result = start_process_core(path_value, arguments_value,
        redirect_input, redirect_output, redirect_error);
    if (context != NULL) end_runtime_operation(context);
    return result;
}

uintptr_t ct_managed_process_try_open(uint32_t id)
{
    return process_from_handle((uintptr_t)id) == NULL ? 0u : (uintptr_t)id;
}

uintptr_t ct_managed_process_current(void)
{
    ct_process *process = current_process();
    return process == NULL ? 0 : (uintptr_t)process->Id;
}

uint32_t ct_managed_process_id(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL ? 0 : process->Id; }
ct_managed_process_state ct_managed_process_get_state(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL ? CT_PROCESS_FAILED : __atomic_load_n(&process->State, __ATOMIC_ACQUIRE); }
bool ct_managed_process_has_exited(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL || __atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE); }
int32_t ct_managed_process_exit_code(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL || !__atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE) ? 0 : __atomic_load_n(&process->ExitCode, __ATOMIC_ACQUIRE); }
void ct_managed_process_cancel(uintptr_t handle)
{
    ct_process *process = process_from_handle(handle);
    if (process == NULL) return;
    ct_managed_process_state state = __atomic_load_n(&process->State, __ATOMIC_ACQUIRE);
    if (state >= CT_PROCESS_EXITED) return;
    /* Publish the observable cancellation flag first. A caller can be forcibly
       deleted after any instruction, so the state must never say CANCELLING
       while the flag that cooperative code reads is still false. */
    __atomic_store_n(&process->Cancellation, true, __ATOMIC_RELEASE);
    while (state < CT_PROCESS_EXITED && state != CT_PROCESS_CANCELLING) {
        if (__atomic_compare_exchange_n(&process->State, &state, CT_PROCESS_CANCELLING, false,
                __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) break;
    }
}
bool ct_managed_process_cancellation_requested(void) { ct_process *process = current_process(); return process != NULL && __atomic_load_n(&process->Cancellation, __ATOMIC_ACQUIRE); }

static bool valid_pipe_buffer(const ct_managed_array *buffer, int32_t offset, int32_t count)
{
    return buffer != NULL && buffer->Length >= 0 && offset >= 0 && count >= 0 &&
        offset <= buffer->Length && count <= buffer->Length - offset;
}

bool ct_managed_process_pipe_read(uintptr_t handle, int32_t stream, void *buffer_value,
    int32_t offset, int32_t count, uint32_t timeout_milliseconds, int32_t *bytes_read, bool *eof)
{
    ct_process *process = process_from_handle(handle);
    ct_managed_array *buffer = (ct_managed_array *)buffer_value;
    ct_console_endpoint *endpoint = process == NULL || stream < 0 || stream >= 3
        ? NULL : process->Streams[stream];
    if (bytes_read == NULL || eof == NULL || process == NULL || (stream != 1 && stream != 2) ||
        !valid_pipe_buffer(buffer, offset, count) || endpoint == NULL ||
        !process->OwnsParentStream[stream] || endpoint->Kind != CT_CONSOLE_ENDPOINT_PIPE) return false;
    *bytes_read = 0;
    *eof = false;
    const int64_t deadline = timeout_milliseconds == UINT32_MAX ? INT64_MAX :
        esp_timer_get_time() + (int64_t)timeout_milliseconds * INT64_C(1000);
    do {
        const size_t received = xStreamBufferReceive(endpoint->Buffer, buffer->Data + offset,
            (size_t)count, count == 0 ? 0 : pdMS_TO_TICKS(10));
        if (received != 0u) { *bytes_read = (int32_t)received; return true; }
        if (__atomic_load_n(&endpoint->ChildrenClosed, __ATOMIC_ACQUIRE)) {
            *eof = true;
            return true;
        }
        if (timeout_milliseconds == 0u) break;
    } while (esp_timer_get_time() < deadline && !ct_managed_process_cancellation_requested());
    return false;
}

bool ct_managed_process_pipe_write(uintptr_t handle, int32_t stream, const void *buffer_value,
    int32_t offset, int32_t count, uint32_t timeout_milliseconds, int32_t *bytes_written)
{
    ct_process *process = process_from_handle(handle);
    const ct_managed_array *buffer = (const ct_managed_array *)buffer_value;
    ct_console_endpoint *endpoint = process == NULL ? NULL : process->Streams[0];
    if (bytes_written == NULL || process == NULL || stream != 0 || !valid_pipe_buffer(buffer, offset, count) ||
        endpoint == NULL || !process->OwnsParentStream[0] || endpoint->Kind != CT_CONSOLE_ENDPOINT_PIPE ||
        __atomic_load_n(&endpoint->ParentClosed, __ATOMIC_ACQUIRE)) return false;
    *bytes_written = 0;
    const int64_t deadline = timeout_milliseconds == UINT32_MAX ? INT64_MAX :
        esp_timer_get_time() + (int64_t)timeout_milliseconds * INT64_C(1000);
    while (*bytes_written < count) {
        const size_t sent = xStreamBufferSend(endpoint->Buffer, buffer->Data + offset + *bytes_written,
            (size_t)(count - *bytes_written), count == 0 ? 0 : pdMS_TO_TICKS(10));
        *bytes_written += (int32_t)sent;
        if (*bytes_written == count) return true;
        if (__atomic_load_n(&endpoint->ChildrenClosed, __ATOMIC_ACQUIRE) || timeout_milliseconds == 0u) break;
        if (esp_timer_get_time() >= deadline || ct_managed_process_cancellation_requested()) break;
    }
    return false;
}

void ct_managed_process_pipe_close(uintptr_t handle, int32_t stream)
{
    ct_process *process = process_from_handle(handle);
    if (process == NULL || stream < 0 || stream >= 3 || !process->OwnsParentStream[stream]) return;
    ct_console_endpoint *endpoint = process->Streams[stream];
    if (endpoint == NULL) return;
    process->OwnsParentStream[stream] = false;
    __atomic_store_n(&endpoint->ParentClosed, true, __ATOMIC_RELEASE);
    /* Streams stores the child-side endpoint as well as identifying the
       exposed parent reference. Keep it reachable until the final child has
       released its side; otherwise an early parent Close leaks that reference
       and leaves the process slot permanently unreusable. */
    if (__atomic_load_n(&endpoint->ChildrenClosed, __ATOMIC_ACQUIRE))
        process->Streams[stream] = NULL;
    release_endpoint_reference(endpoint);
}

bool ct_managed_process_try_wait(uintptr_t handle, uint32_t timeout_milliseconds, int32_t *exit_code)
{
    ct_process *process = process_from_handle(handle);
    if (process == NULL) return false;
    const TickType_t ticks = timeout_milliseconds == UINT32_MAX ? portMAX_DELAY : pdMS_TO_TICKS(timeout_milliseconds);
    ct_execution_context *context = current_context();
    if (context != NULL && context->Process == process) return false;
    if (context == NULL) {
        if (xSemaphoreTake(process->Completion, ticks) != pdTRUE) return false;
        xSemaphoreGive(process->Completion);
    }
    else {
        const TickType_t started = xTaskGetTickCount();
        for (;;) {
            if (!begin_runtime_operation(context)) await_forced_task_deletion();
            const BaseType_t completed = xSemaphoreTake(process->Completion, 0);
            if (completed == pdTRUE) xSemaphoreGive(process->Completion);
            end_runtime_operation(context);
            if (completed == pdTRUE) break;
            if (ticks != portMAX_DELAY && xTaskGetTickCount() - started >= ticks) return false;
            vTaskDelay(1);
        }
    }
    if (exit_code != NULL) *exit_code = __atomic_load_n(&process->ExitCode, __ATOMIC_ACQUIRE);
    return true;
}

int32_t ct_managed_process_wait(uintptr_t handle)
{
    int32_t result = 0;
    (void)ct_managed_process_try_wait(handle, UINT32_MAX, &result);
    return result;
}

static void complete_pending_termination(ct_pending_termination *pending, bool forced)
{
    __atomic_store_n(&pending->Process->ForceDeleteIssued, forced, __ATOMIC_RELEASE);
    __atomic_store_n(&pending->Process->TerminationDispatched, true, __ATOMIC_RELEASE);
    (void)memset(pending, 0, sizeof(*pending));
}

static void add_pending_termination(const ct_termination_request *request)
{
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_pending_termination *pending = &s_pending_terminations[index];
        if (pending->Process != NULL) continue;
        pending->Process = request->Process;
        pending->InfiniteGrace = request->GraceMilliseconds == UINT32_MAX;
        pending->DeadlineMicroseconds = pending->InfiniteGrace ? 0 :
            request->RequestedAtMicroseconds + (int64_t)request->GraceMilliseconds * INT64_C(1000);
        return;
    }
    /* There can be at most one request per lifetime-stable process slot. */
    abort();
}

static bool termination_grace_elapsed(const ct_pending_termination *pending, int64_t now_microseconds)
{
    return !pending->InfiniteGrace && now_microseconds >= pending->DeadlineMicroseconds;
}

static void advance_pending_terminations(void)
{
    const int64_t now_microseconds = esp_timer_get_time();
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_pending_termination *pending = &s_pending_terminations[index];
        ct_process *process = pending->Process;
        if (process == NULL) continue;
        if (__atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE)) {
            complete_pending_termination(pending, false);
            continue;
        }
        if (!pending->DrainingOperations) {
            if (__atomic_load_n(&process->State, __ATOMIC_ACQUIRE) >= CT_PROCESS_EXITED) {
                complete_pending_termination(pending, false);
                continue;
            }
            if (!termination_grace_elapsed(pending, now_microseconds)) continue;
            pending->Task = __atomic_exchange_n(&process->MainTask, NULL, __ATOMIC_ACQ_REL);
            if (pending->Task == NULL) {
                complete_pending_termination(pending, false);
                continue;
            }
            (void)__atomic_fetch_or(&process->RuntimeGate, CT_RUNTIME_GATE_STOPPED, __ATOMIC_ACQ_REL);
            ct_message *wake = NULL;
            (void)xQueueSend(process->Mailbox, &wake, 0);
            pending->DrainingOperations = true;
        }
        if ((__atomic_load_n(&process->RuntimeGate, __ATOMIC_ACQUIRE) & CT_RUNTIME_GATE_COUNT) != 0)
            continue;
        __atomic_store_n(&process->ExitCode, -1, __ATOMIC_RELEASE);
        __atomic_store_n(&process->State, CT_PROCESS_TERMINATED, __ATOMIC_RELEASE);
        vTaskDelete(pending->Task);
        complete_pending_termination(pending, true);
    }
}

static void terminator_main(void *argument)
{
    (void)argument;
    for (;;) {
        ct_termination_request request = { 0 };
        bool has_pending = false;
        for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
            if (s_pending_terminations[index].Process != NULL) {
                has_pending = true;
                break;
            }
        }
        TickType_t poll_ticks = pdMS_TO_TICKS(CT_TERMINATION_POLL_MILLISECONDS);
        if (poll_ticks == 0) poll_ticks = 1;
        const TickType_t wait = has_pending ? poll_ticks : portMAX_DELAY;
        if (xQueueReceive(s_termination_queue, &request, wait) == pdTRUE && request.Process != NULL) {
            add_pending_termination(&request);
            while (xQueueReceive(s_termination_queue, &request, 0) == pdTRUE) {
                if (request.Process != NULL) add_pending_termination(&request);
            }
        }
        advance_pending_terminations();
    }
}

void ct_managed_process_terminate(uintptr_t handle, uint32_t grace_milliseconds)
{
    ct_process *process = process_from_handle(handle);
    if (process == NULL || __atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE)) return;
    ct_execution_context *context = current_context();
    const bool terminates_self = context != NULL && context->Process == process;
    for (;;) {
        const uint32_t request_state = __atomic_load_n(&process->TerminationRequestState, __ATOMIC_ACQUIRE);
        if (request_state == CT_TERMINATION_QUEUED) break;
        if (request_state == CT_TERMINATION_PUBLISHING) {
            vTaskDelay(1);
            continue;
        }
        if (context != NULL && !begin_runtime_operation(context)) await_forced_task_deletion();
        uint32_t expected = CT_TERMINATION_NONE;
        const bool publishes = __atomic_compare_exchange_n(&process->TerminationRequestState, &expected,
            CT_TERMINATION_PUBLISHING, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
        if (!publishes) {
            if (context != NULL) end_runtime_operation(context);
            continue;
        }
        ct_managed_process_cancel(handle);
        const ct_termination_request request = { process, grace_milliseconds, esp_timer_get_time() };
        const bool queued = xQueueSend(s_termination_queue, &request, 0) == pdTRUE;
        __atomic_store_n(&process->TerminationRequestState,
            queued ? CT_TERMINATION_QUEUED : CT_TERMINATION_NONE, __ATOMIC_RELEASE);
        if (context != NULL) end_runtime_operation(context);
        if (!queued) return;
        break;
    }
    if (terminates_self) await_forced_task_deletion();
    while (!__atomic_load_n(&process->TerminationDispatched, __ATOMIC_ACQUIRE)) vTaskDelay(1);
    if (__atomic_load_n(&process->ForceDeleteIssued, __ATOMIC_ACQUIRE)) {
        int32_t ignored;
        (void)ct_managed_process_try_wait(handle, UINT32_MAX, &ignored);
    }
}

static bool send_process_message(uintptr_t handle, const void *payload_value, ct_execution_context *caller)
{
    ct_process *process = process_from_handle(handle);
    const ct_managed_array *payload = (const ct_managed_array *)payload_value;
    if (process == NULL || payload == NULL || payload->Length < 0 ||
        __atomic_load_n(&process->State, __ATOMIC_ACQUIRE) >= CT_PROCESS_EXITED) return true;
    const size_t length = (size_t)payload->Length;
    ct_message *message = (ct_message *)malloc(sizeof(ct_message) + length);
    if (message == NULL) return true;
    message->Length = length;
    if (length != 0) (void)memcpy(message->Data, payload->Data, length);
    for (;;) {
        xSemaphoreTake(s_registry, portMAX_DELAY);
        const bool accepting = !__atomic_load_n(&process->Cleaned, __ATOMIC_ACQUIRE) &&
            __atomic_load_n(&process->State, __ATOMIC_ACQUIRE) < CT_PROCESS_EXITED;
        const BaseType_t sent = accepting ? xQueueSend(process->Mailbox, &message, 0) : pdFALSE;
        xSemaphoreGive(s_registry);
        if (sent == pdTRUE) return true;
        if (!accepting) { free(message); return true; }
        if (caller != NULL &&
            (__atomic_load_n(&caller->Process->RuntimeGate, __ATOMIC_ACQUIRE) & CT_RUNTIME_GATE_STOPPED) != 0) {
            free(message);
            return false;
        }
        vTaskDelay(1);
    }
}

void ct_managed_process_send(uintptr_t handle, const void *payload_value)
{
    ct_execution_context *context = current_context();
    if (context != NULL && !begin_runtime_operation(context)) await_forced_task_deletion();
    const bool completed = send_process_message(handle, payload_value, context);
    if (context != NULL) end_runtime_operation(context);
    if (!completed || (context != NULL &&
        (__atomic_load_n(&context->Process->RuntimeGate, __ATOMIC_ACQUIRE) & CT_RUNTIME_GATE_STOPPED) != 0))
        await_forced_task_deletion();
}

bool ct_managed_process_try_receive(uintptr_t handle, uint32_t timeout_milliseconds, const void *type_template, void **payload)
{
    (void)type_template;
    ct_process *process = process_from_handle(handle);
    ct_execution_context *context = current_context();
    if (process == NULL || context == NULL || context->Process != process || context->Module == NULL || payload == NULL) return false;
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_message *message = NULL;
    const TickType_t ticks = timeout_milliseconds == UINT32_MAX ? portMAX_DELAY : pdMS_TO_TICKS(timeout_milliseconds);
    if (xQueueReceive(process->Mailbox, &message, ticks) != pdTRUE) {
        end_runtime_operation(context);
        return false;
    }
    if (message == NULL) {
        end_runtime_operation(context);
        return false;
    }
    *payload = context->Module->Descriptor->CreateBytes(message->Data, message->Length);
    free(message);
    end_runtime_operation(context);
    return true;
}

void *ct_managed_process_receive(uintptr_t handle, const void *type_template)
{
    ct_process *process = process_from_handle(handle);
    ct_execution_context *context = current_context();
    if (process == NULL || context == NULL || context->Process != process || context->Module == NULL) return NULL;
    void *result = NULL;
    while (!ct_managed_process_try_receive(handle, UINT32_MAX, type_template, &result)) { }
    return result;
}

size_t ctilde_managed_processes(ct_managed_process_info *output, size_t capacity)
{
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE)) return 0;
    size_t count = 0;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_process *process = &s_processes[index];
        if (!process->Used || __atomic_load_n(&s_published_process_ids[index], __ATOMIC_ACQUIRE) != process->Id) continue;
        if (output != NULL && count < capacity) {
            ct_managed_process_info *info = &output[count];
            (void)memset(info, 0, sizeof(*info));
            info->Id = process->Id;
            info->State = __atomic_load_n(&process->State, __ATOMIC_ACQUIRE);
            info->ExitCode = __atomic_load_n(&process->ExitCode, __ATOMIC_ACQUIRE);
            info->HeapBytes = __atomic_load_n(&process->HeapBytes, __ATOMIC_ACQUIRE);
            info->HeapLimit = process->HeapLimit;
            info->TaskCount = __atomic_load_n(&process->TaskCount, __ATOMIC_ACQUIRE);
            (void)snprintf(info->ModuleName, sizeof(info->ModuleName), "%s", process->RootName);
        }
        ++count;
    }
    xSemaphoreGive(s_registry);
    return count;
}

uint32_t ctilde_managed_process_for_task(uintptr_t task_handle)
{
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || task_handle == 0u) return 0u;
    ct_execution_context *context = (ct_execution_context *)pvTaskGetThreadLocalStoragePointer(
        (TaskHandle_t)task_handle, CONFIG_CTILDE_MANAGED_TLS_INDEX);
    ct_process *process = context == NULL ? NULL : context->Process;
    if (process == NULL) return 0u;
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        if (process != &s_processes[index]) continue;
        if (!__atomic_load_n(&process->Used, __ATOMIC_ACQUIRE)) return 0u;
        const uint32_t id = __atomic_load_n(&process->Id, __ATOMIC_ACQUIRE);
        return __atomic_load_n(&s_published_process_ids[index], __ATOMIC_ACQUIRE) == id ? id : 0u;
    }
    return 0u;
}

bool ctilde_managed_overlay_debug_state(uint32_t process_id,
    ct_managed_overlay_debug_state_v22 *output)
{
    if (output == NULL || !__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE)) return false;
    bool found = false;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_process *process = process_from_handle((uintptr_t)process_id);
    if (process != NULL && process->HasOverlays &&
        !__atomic_load_n(&process->Completed, __ATOMIC_ACQUIRE)) {
        (void)memset(output, 0, sizeof(*output));
        output->Size = sizeof(*output);
        output->ProcessId = process->Id;
        output->WindowAddress = (uintptr_t)__atomic_load_n(&process->OverlayWindow, __ATOMIC_ACQUIRE);
        output->WindowSize = process->OverlayWindowSize;
        output->OverlayId = __atomic_load_n(&process->LoadedOverlayId, __ATOMIC_ACQUIRE);
        output->Generation = __atomic_load_n(&process->OverlayGeneration, __ATOMIC_ACQUIRE);
        ct_module *loaded_overlay_module = __atomic_load_n(
            &process->LoadedOverlayModule, __ATOMIC_ACQUIRE);
        if (loaded_overlay_module != NULL)
            (void)snprintf(output->ModuleName, sizeof(output->ModuleName), "%s",
                loaded_overlay_module->Name);
        found = true;
    }
    xSemaphoreGive(s_registry);
    return found;
}

bool ctilde_managed_process_set_foreground(uint32_t process_id)
{
    ct_process *caller = current_process();
    ct_process *target = process_from_handle((uintptr_t)process_id);
    if (caller == NULL || target == NULL || caller->Streams[0] == NULL ||
        target->Streams[0] != caller->Streams[0] ||
        (target != caller && !process_is_descendant_of(target, caller->Id))) return false;
    __atomic_store_n(&caller->Streams[0]->ForegroundProcess, target->Id, __ATOMIC_RELEASE);
    return true;
}

void ctilde_managed_process_terminate_descendants(uint32_t process_id,
    uint32_t grace_milliseconds)
{
    ct_process *caller = current_process();
    ct_process *root = process_from_handle((uintptr_t)process_id);
    if (caller == NULL || root == NULL || (root != caller && !process_is_descendant_of(root, caller->Id))) return;
    uint32_t ids[CONFIG_CTILDE_MANAGED_MAX_PROCESSES];
    size_t count = 0u;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_process *candidate = &s_processes[index];
        if (!candidate->Used || candidate == root || !process_is_descendant_of(candidate, root->Id)) continue;
        ids[count++] = candidate->Id;
    }
    xSemaphoreGive(s_registry);
    for (size_t index = count; index > 0u; --index)
        ct_managed_process_terminate((uintptr_t)ids[index - 1u], grace_milliseconds);
}

uintptr_t ctilde_managed_native_resource_register(uintptr_t value,
    ct_managed_native_resource_release_fn release)
{
    ct_process *process = current_process();
    if (process == NULL || value == 0u || release == NULL) return 0u;
    ct_native_resource *resource = malloc(sizeof(*resource));
    if (resource == NULL) return 0u;
    resource->Value = value;
    resource->Release = release;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    uintptr_t token = s_next_resource_token++;
    if (token == 0u) token = s_next_resource_token++;
    resource->Token = token;
    resource->Next = process->NativeResources;
    process->NativeResources = resource;
    xSemaphoreGive(s_registry);
    return token;
}

bool ctilde_managed_native_resource_release(uintptr_t token)
{
    ct_process *process = current_process();
    if (process == NULL || token == 0u) return false;
    ct_native_resource *resource = NULL;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_native_resource **link = &process->NativeResources;
    while (*link != NULL && (*link)->Token != token) link = &(*link)->Next;
    if (*link != NULL) {
        resource = *link;
        *link = resource->Next;
    }
    xSemaphoreGive(s_registry);
    if (resource == NULL) return false;
    resource->Release(resource->Value);
    free(resource);
    return true;
}

/* Thread payloads include native semaphores, which forced arena cleanup cannot
 * discover through managed destructors. Keep their owner resident and register
 * it before ending the allocation operation. No public ABI layout is involved. */
typedef struct ct_thread_payload_owner {
    uintptr_t Token;
    SemaphoreHandle_t Done;
    max_align_t Payload[];
} ct_thread_payload_owner;

static void release_thread_payload(uintptr_t value)
{
    ct_thread_payload_owner *owner = (ct_thread_payload_owner *)value;
    if (owner->Done != NULL) vSemaphoreDelete(owner->Done);
    free(owner);
}

void *ctilde_managed_thread_payload_allocate(size_t size, void **done)
{
    ct_execution_context *context = current_context();
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_thread_payload_owner *owner = size > SIZE_MAX - sizeof(*owner) ? NULL : calloc(1u, sizeof(*owner) + size);
    if (owner != NULL) {
        owner->Done = xSemaphoreCreateBinary();
        if (owner->Done != NULL)
            owner->Token = ctilde_managed_native_resource_register((uintptr_t)owner, release_thread_payload);
        if (owner->Token == 0u) { release_thread_payload((uintptr_t)owner); owner = NULL; }
    }
    *done = owner == NULL ? NULL : owner->Done;
    void *payload = owner == NULL ? NULL : (void *)owner->Payload;
    end_runtime_operation(context);
    return payload;
}

void ctilde_managed_thread_payload_free(void *payload)
{
    if (payload == NULL) return;
    ct_execution_context *context = current_context();
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_thread_payload_owner *owner = (ct_thread_payload_owner *)((uint8_t *)payload - offsetof(ct_thread_payload_owner, Payload));
    (void)ctilde_managed_native_resource_release(owner->Token);
    end_runtime_operation(context);
}

__attribute__((noreturn)) void ctilde_managed_thread_exit(void)
{
    /* Detachment can let the reaper unload the caller's module immediately.
     * Complete the final task instructions in resident firmware. */
    if (api_service(CT_RUNTIME_SERVICE_THREAD_DETACH, NULL, 0u) != 0) abort();
    vTaskDelete(NULL);
    abort();
}

void ctilde_managed_console_set_uart_activity_hook(void (*hook)(void))
{
    s_uart_activity_hook = hook;
}

size_t ctilde_managed_modules(ct_managed_module_info *output, size_t capacity)
{
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE)) return 0;
    size_t count = 0;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_MODULES; ++index) {
        ct_module *module = &s_modules[index];
        if (!module->Used) continue;
        if (output != NULL && count < capacity) {
            ct_managed_module_info *info = &output[count];
            (void)memset(info, 0, sizeof(*info));
            (void)snprintf(info->Name, sizeof(info->Name), "%s", module->Name);
            (void)snprintf(info->Version, sizeof(info->Version), "%s", module->Version);
            info->LoadReferences = module->References;
            info->ActiveCalls = __atomic_load_n(&module->ActiveCalls, __ATOMIC_ACQUIRE);
            info->LiveAllocations = __atomic_load_n(&module->LiveAllocations, __ATOMIC_ACQUIRE);
            info->Stopping = module->Stopping;
        }
        ++count;
    }
    xSemaphoreGive(s_registry);
    return count;
}

static const struct esp_elfsym s_symbols[] = {
    { "__atomic_load_8", (void *)ct_idf_atomic_load_8 },
    { "__atomic_compare_exchange_8", (void *)ct_idf_atomic_compare_exchange_8 },
    ESP_ELFSYM_EXPORT(ctilde_managed_atomic_compare_exchange_u32),
    ESP_ELFSYM_EXPORT(ctilde_managed_thread_payload_allocate),
    ESP_ELFSYM_EXPORT(ctilde_managed_thread_payload_free),
    ESP_ELFSYM_EXPORT(ctilde_managed_thread_exit),
    ESP_ELFSYM_EXPORT(ct_managed_process_start), ESP_ELFSYM_EXPORT(ct_managed_process_start_redirected),
    ESP_ELFSYM_EXPORT(ct_managed_process_try_open), ESP_ELFSYM_EXPORT(ct_managed_process_current),
    ESP_ELFSYM_EXPORT(ct_managed_process_id),
    ESP_ELFSYM_EXPORT(ct_managed_process_get_state), ESP_ELFSYM_EXPORT(ct_managed_process_has_exited),
    ESP_ELFSYM_EXPORT(ct_managed_process_exit_code), ESP_ELFSYM_EXPORT(ct_managed_process_cancel),
    ESP_ELFSYM_EXPORT(ct_managed_process_terminate), ESP_ELFSYM_EXPORT(ct_managed_process_wait),
    ESP_ELFSYM_EXPORT(ct_managed_process_try_wait), ESP_ELFSYM_EXPORT(ct_managed_process_send),
    ESP_ELFSYM_EXPORT(ctilde_managed_overlay_debug_state),
    ESP_ELFSYM_EXPORT(ct_managed_process_receive), ESP_ELFSYM_EXPORT(ct_managed_process_try_receive),
    ESP_ELFSYM_EXPORT(ct_managed_process_cancellation_requested),
    ESP_ELFSYM_EXPORT(ct_managed_process_pipe_read), ESP_ELFSYM_EXPORT(ct_managed_process_pipe_write),
    ESP_ELFSYM_EXPORT(ct_managed_process_pipe_close),
    ESP_ELFSYM_EXPORT(memcpy), ESP_ELFSYM_EXPORT(memset), ESP_ELFSYM_EXPORT(memcmp), ESP_ELFSYM_EXPORT(memchr),
    ESP_ELFSYM_EXPORT(strlen), ESP_ELFSYM_EXPORT(strnlen), ESP_ELFSYM_EXPORT(snprintf), ESP_ELFSYM_EXPORT(fprintf), ESP_ELFSYM_EXPORT(fwrite),
    ESP_ELFSYM_EXPORT(fputc), ESP_ELFSYM_EXPORT(fputs), ESP_ELFSYM_EXPORT(fflush),
    ESP_ELFSYM_EXPORT(setjmp), ESP_ELFSYM_EXPORT(longjmp), ESP_ELFSYM_EXPORT(esp_err_to_name), ESP_ELFSYM_EXPORT(__getreent),
    ESP_ELFSYM_EXPORT(__extendsfdf2), ESP_ELFSYM_EXPORT(__udivdi3), ESP_ELFSYM_EXPORT(__umoddi3),
    ESP_ELFSYM_EXPORT(__ashldi3),
    /* Source-created Thread helpers call the same FreeRTOS primitives as firmware. */
    ESP_ELFSYM_EXPORT(xQueueSemaphoreTake), ESP_ELFSYM_EXPORT(vQueueDelete),
    ESP_ELFSYM_EXPORT(xQueueGenericSend), ESP_ELFSYM_EXPORT(xQueueGenericCreate),
    ESP_ELFSYM_EXPORT(vTaskDelete), ESP_ELFSYM_EXPORT(vTaskDelay),
    ESP_ELFSYM_EXPORT(xTaskCreatePinnedToCore), ESP_ELFSYM_EXPORT(xTaskGetCurrentTaskHandle),
#if CONFIG_IDF_TARGET_ARCH_XTENSA
    ESP_ELFSYM_EXPORT(vPortYield),
#endif
    ESP_ELFSYM_END
};

static void rollback_runtime_initialization(bool symbols_registered, TaskHandle_t reaper_task,
    TaskHandle_t terminator_task)
{
    if (terminator_task != NULL) vTaskDelete(terminator_task);
    if (reaper_task != NULL) vTaskDelete(reaper_task);
    if (symbols_registered)
        (void)esp_elf_unregister_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols);
    if (s_termination_queue != NULL) {
        vQueueDelete(s_termination_queue);
        s_termination_queue = NULL;
    }
    if (s_reaper_queue != NULL) {
        vQueueDelete(s_reaper_queue);
        s_reaper_queue = NULL;
    }
    if (s_registry != NULL) {
        vSemaphoreDelete(s_registry);
        s_registry = NULL;
    }
    for (size_t stream = 0; stream < 3u; ++stream) {
        if (s_uart_streams[stream].WriteLock != NULL)
            vSemaphoreDelete(s_uart_streams[stream].WriteLock);
        (void)memset(&s_uart_streams[stream], 0, sizeof(s_uart_streams[stream]));
    }
    __atomic_store_n(&s_initialized, false, __ATOMIC_RELEASE);
    __atomic_store_n(&s_initialization_state, CT_INITIALIZATION_UNINITIALIZED, __ATOMIC_RELEASE);
}

int ctilde_managed_runtime_initialize(void)
{
    for (;;) {
        const uint32_t state = __atomic_load_n(&s_initialization_state, __ATOMIC_ACQUIRE);
        if (state == CT_INITIALIZATION_READY) return 0;
        if (state == CT_INITIALIZATION_UNINITIALIZED) {
            uint32_t expected = CT_INITIALIZATION_UNINITIALIZED;
            if (__atomic_compare_exchange_n(&s_initialization_state, &expected, CT_INITIALIZATION_RUNNING,
                false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) break;
        }
        vTaskDelay(1);
    }

    TaskHandle_t reaper_task = NULL;
    TaskHandle_t terminator_task = NULL;
    bool symbols_registered = false;
    int result = -ENOMEM;
    s_registry = xSemaphoreCreateMutexStatic(&s_registry_storage);
    s_reaper_queue = xQueueCreateStatic(CONFIG_CTILDE_MANAGED_MAX_PROCESSES, sizeof(ct_process *),
        s_reaper_queue_buffer, &s_reaper_queue_storage);
    s_termination_queue = xQueueCreateStatic(CONFIG_CTILDE_MANAGED_MAX_PROCESSES, sizeof(ct_termination_request),
        s_termination_queue_buffer, &s_termination_queue_storage);
    if (s_registry == NULL || s_reaper_queue == NULL || s_termination_queue == NULL) goto fail;
    result = ctilde_managed_register_capability(CT_CAP_CORE, &s_core_api);
    if (result != 0) goto fail;
    result = ctilde_managed_register_capability(CT_CAP_BUFFER, &s_buffer_api);
    if (result != 0) goto fail;
    result = ctilde_managed_register_capability(CT_CAP_FILESYSTEM, &s_filesystem_api);
    if (result != 0) goto fail;
    result = ctilde_managed_register_capability(CT_CAP_PROCESS, &s_process_api);
    if (result != 0) goto fail;
    result = -ENOMEM;
    for (size_t stream = 0; stream < 3u; ++stream) {
        s_uart_streams[stream].Kind = CT_CONSOLE_ENDPOINT_UART;
        s_uart_streams[stream].WriteLock = xSemaphoreCreateMutex();
        if (s_uart_streams[stream].WriteLock == NULL) goto fail;
    }
    if (esp_elf_register_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols) != 0) {
        result = -EIO;
        goto fail;
    }
    symbols_registered = true;
    if (xTaskCreate(reaper_main, "ctilde_reaper", 4096, NULL, tskIDLE_PRIORITY + 1, &reaper_task) != pdPASS)
        goto fail;
    if (xTaskCreate(terminator_main, "ctilde_terminator", 4096, NULL, tskIDLE_PRIORITY + 1,
        &terminator_task) != pdPASS) goto fail;
    __atomic_store_n(&s_initialized, true, __ATOMIC_RELEASE);
    __atomic_store_n(&s_initialization_state, CT_INITIALIZATION_READY, __ATOMIC_RELEASE);
    return 0;

fail:
    rollback_runtime_initialization(symbols_registered, reaper_task, terminator_task);
    return result;
}
