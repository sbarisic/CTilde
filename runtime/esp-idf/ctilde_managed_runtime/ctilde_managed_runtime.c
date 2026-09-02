#include "ctilde_managed_runtime.h"

#include <errno.h>
#include <inttypes.h>
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#include "esp_dlfcn.h"
#include "esp_err.h"
#include "esp_elf.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "private/elf_symbol.h"

#ifdef __getreent
#undef __getreent
#endif
extern void *__getreent(void);
extern double __extendsfdf2(float value);
extern uint64_t __udivdi3(uint64_t dividend, uint64_t divisor);
extern uint64_t __umoddi3(uint64_t dividend, uint64_t divisor);

#ifndef CONFIG_CTILDE_MANAGED_TLS_INDEX
#define CONFIG_CTILDE_MANAGED_TLS_INDEX 2
#endif
#ifndef CONFIG_CTILDE_MANAGED_MAX_MODULES
#define CONFIG_CTILDE_MANAGED_MAX_MODULES 16
#endif
#ifndef CONFIG_CTILDE_MANAGED_MAX_PROCESSES
#define CONFIG_CTILDE_MANAGED_MAX_PROCESSES 128
#endif

static_assert(CONFIG_CTILDE_MANAGED_TLS_INDEX < CONFIG_FREERTOS_THREAD_LOCAL_STORAGE_POINTERS,
    "CONFIG_CTILDE_MANAGED_TLS_INDEX must select an allocated FreeRTOS TLS slot");

#define CT_MODULE_PATH_MAX 256
#define CT_MODULE_NAME_MAX 64
#define CT_MODULE_VERSION_MAX 32
#define CT_MAX_DEPENDENCIES 16
#define CT_MAX_PROCESS_MODULES 16
#define CT_MAX_ARGUMENTS 32
#define CT_MAILBOX_DEPTH 16
#define CT_THREAD_STATE_BYTES 128

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
};

typedef struct ct_binary_dependency_v1 {
    char Name[64];
    char Version[32];
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
    char Name[64];
    char Version[32];
    uint8_t BuildIdentity[32];
    uint8_t ApiHash[32];
    ct_binary_dependency_v1 Dependencies[];
} ct_binary_manifest_v1;

typedef struct ct_module ct_module;
typedef struct ct_process ct_process;

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

typedef struct ct_module_instance {
    ct_module *Module;
    void *State;
    bool Initialized;
} ct_module_instance;

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
    const ct_managed_module_descriptor_v1 *Descriptor;
    ct_module *Dependencies[CT_MAX_DEPENDENCIES];
    uint32_t DependencyCount;
    uint32_t References;
    uint32_t ActiveCalls;
    uint32_t LiveAllocations;
};

typedef struct ct_execution_context {
    ct_process *Process;
    ct_module *Module;
    void *ThreadState;
    uint8_t PrimaryThreadState[CT_THREAD_STATE_BYTES] __attribute__((aligned(8)));
} ct_execution_context;

struct ct_process {
    bool Used;
    bool Cleaned;
    bool CleanupQueued;
    uint32_t Id;
    volatile ct_managed_process_state State;
    volatile bool Cancellation;
    int32_t ExitCode;
    ct_module *Root;
    TaskHandle_t MainTask;
    uint32_t TaskCount;
    size_t HeapBytes;
    size_t HeapLimit;
    ct_allocation *Allocations;
    ct_module_instance Instances[CT_MAX_PROCESS_MODULES];
    uint32_t InstanceCount;
    char *Arguments[CT_MAX_ARGUMENTS];
    size_t ArgumentLengths[CT_MAX_ARGUMENTS];
    int32_t ArgumentCount;
    ct_execution_context Context;
    StaticSemaphore_t CompletionStorage;
    SemaphoreHandle_t Completion;
    StaticQueue_t MailboxStorage;
    uint8_t MailboxBuffer[CT_MAILBOX_DEPTH * sizeof(ct_message *)];
    QueueHandle_t Mailbox;
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
static ct_type_registration s_types[128];
static uint32_t s_next_process_id = 1;
static StaticSemaphore_t s_registry_storage;
static SemaphoreHandle_t s_registry;
static StaticQueue_t s_reaper_queue_storage;
static uint8_t s_reaper_queue_buffer[16 * sizeof(ct_process *)];
static QueueHandle_t s_reaper_queue;
static bool s_initialized;

static void cleanup_process(ct_process *process, bool forced);
static void release_module(ct_module *module);
static void *api_current_thread_state(void);
static void api_set_thread_state(void *state);

static ct_execution_context *current_context(void)
{
    return (ct_execution_context *)pvTaskGetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX);
}

static ct_process *current_process(void)
{
    ct_execution_context *context = current_context();
    return context == NULL ? NULL : context->Process;
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

static int resolve_module_path_bytes(const uint8_t *data, size_t length, char output[CT_MODULE_PATH_MAX])
{
    if (data == NULL || length == 0 || length >= CT_MODULE_PATH_MAX || memchr(data, 0, length) != NULL) {
        return -EINVAL;
    }
    char relative[CT_MODULE_PATH_MAX];
    (void)memcpy(relative, data, length);
    relative[length] = '\0';
    const char *root = CTILDE_MANAGED_MODULE_ROOT;
    const size_t root_length = strlen(root);
    const char *suffix = relative;
    if (relative[0] == '/') {
        if (strncmp(relative, root, root_length) != 0 || relative[root_length] != '/') {
            return -EPERM;
        }
        suffix = relative + root_length + 1;
    }
    if (*suffix == '\0' || strstr(suffix, "\\") != NULL) {
        return -EINVAL;
    }
    const char *segment = suffix;
    while (*segment != '\0') {
        const char *end = strchr(segment, '/');
        const size_t segment_length = end == NULL ? strlen(segment) : (size_t)(end - segment);
        if (segment_length == 0 || (segment_length == 1 && segment[0] == '.') ||
            (segment_length == 2 && segment[0] == '.' && segment[1] == '.')) {
            return -EPERM;
        }
        if (end == NULL) break;
        segment = end + 1;
    }
    const int written = snprintf(output, CT_MODULE_PATH_MAX, "%s/%s", root, suffix);
    return written > 0 && written < CT_MODULE_PATH_MAX ? 0 : -ENAMETOOLONG;
}

static int read_manifest(const char *path, ct_binary_manifest_v1 **result, uint8_t **owned, size_t *file_size)
{
    *result = NULL;
    *owned = NULL;
    FILE *file = fopen(path, "rb");
    if (file == NULL) return -errno;
    if (fseek(file, 0, SEEK_END) != 0) { fclose(file); return -EIO; }
    const long length = ftell(file);
    if (length < 52 || length > 4 * 1024 * 1024 || fseek(file, 0, SEEK_SET) != 0) { fclose(file); return -ENOEXEC; }
    uint8_t *bytes = (uint8_t *)malloc((size_t)length);
    if (bytes == NULL) { fclose(file); return -ENOMEM; }
    if (fread(bytes, 1, (size_t)length, file) != (size_t)length) { free(bytes); fclose(file); return -EIO; }
    fclose(file);
    if (bytes[0] != 0x7f || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' || bytes[4] != 1 || bytes[5] != 1) {
        free(bytes); return -ENOEXEC;
    }
    const uint16_t elf_type = (uint16_t)bytes[16] | (uint16_t)((uint16_t)bytes[17] << 8);
    const uint16_t machine = (uint16_t)bytes[18] | (uint16_t)((uint16_t)bytes[19] << 8);
#if CONFIG_IDF_TARGET_ARCH_XTENSA
    const uint16_t expected_machine = 94;
    const uint32_t expected_architecture = 1;
#else
    const uint16_t expected_machine = 243;
    const uint32_t expected_architecture = 2;
#endif
    if (elf_type != 3 || machine != expected_machine) { free(bytes); return -ENOEXEC; }
    static const uint8_t magic[8] = { 'C', 'T', 'M', 'O', 'D', 1, 0, 0 };
    ct_binary_manifest_v1 *manifest = NULL;
    for (size_t offset = 0; offset + sizeof(ct_binary_manifest_v1) <= (size_t)length; ++offset) {
        if (memcmp(bytes + offset, magic, sizeof(magic)) == 0) {
            manifest = (ct_binary_manifest_v1 *)(void *)(bytes + offset);
            if (manifest->HeaderSize < offsetof(ct_binary_manifest_v1, Dependencies) || manifest->TotalSize < manifest->HeaderSize ||
                manifest->TotalSize > (size_t)length - offset || manifest->DependencyCount > CT_MAX_DEPENDENCIES) {
                free(bytes); return -ENOEXEC;
            }
            break;
        }
    }
    if (manifest == NULL || manifest->RuntimeAbi != CTILDE_RUNTIME_ABI_VERSION ||
        manifest->ModuleAbi != CTILDE_MANAGED_MODULE_ABI_VERSION || manifest->Architecture != expected_architecture ||
        !contained_string(manifest->Name, sizeof(manifest->Name)) || !contained_string(manifest->Version, sizeof(manifest->Version))) {
        free(bytes); return -ENOEXEC;
    }
    for (uint32_t index = 0; index < manifest->DependencyCount; ++index) {
        if (!contained_string(manifest->Dependencies[index].Name, sizeof(manifest->Dependencies[index].Name)) ||
            !contained_string(manifest->Dependencies[index].Version, sizeof(manifest->Dependencies[index].Version))) {
            free(bytes); return -ENOEXEC;
        }
    }
    *result = manifest;
    *owned = bytes;
    if (file_size != NULL) *file_size = (size_t)length;
    return 0;
}

int ctilde_managed_preflight(const char *path, char *error, size_t error_capacity)
{
    char resolved[CT_MODULE_PATH_MAX];
    const int path_result = resolve_module_path_bytes((const uint8_t *)path, path == NULL ? 0 : strlen(path), resolved);
    if (path_result != 0) { set_error(error, error_capacity, "module path escapes /storage/modules"); return path_result; }
    ct_binary_manifest_v1 *manifest;
    uint8_t *bytes;
    const int result = read_manifest(resolved, &manifest, &bytes, NULL);
    if (result != 0) set_error(error, error_capacity, "invalid or incompatible managed ELF manifest");
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

static int load_module_recursive(const char *path, const char *const *chain, size_t depth, ct_module **output)
{
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
        if (exact && !existing->Stopping && !existing->Loading) ++existing->References;
        xSemaphoreGive(s_registry);
        free(manifest_bytes);
        if (!exact || existing->Stopping || existing->Loading) return -EEXIST;
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
        char dependency_path[CT_MODULE_PATH_MAX];
        const int written = snprintf(dependency_path, sizeof(dependency_path), "%s/%s.ctm", CTILDE_MANAGED_MODULE_ROOT, manifest->Dependencies[index].Name);
        if (written <= 0 || written >= (int)sizeof(dependency_path) ||
            (result = load_module_recursive(dependency_path, next_chain, depth + 1, &module->Dependencies[index])) != 0 ||
            !exact_dependency(&manifest->Dependencies[index], module->Dependencies[index])) {
            if (result == 0) result = -ESTALE;
            goto fail;
        }
        ++module->DependencyCount;
    }

    const char *loader_name = strrchr(path, '/');
    loader_name = loader_name == NULL ? path : loader_name + 1;
    module->DynamicHandle = dlopen(loader_name, RTLD_NOW);
    if (module->DynamicHandle == NULL) { result = -ENOEXEC; goto fail; }
    typedef const ct_managed_module_descriptor_v1 *(*descriptor_function)(void);
    typedef int32_t (*bind_runtime_function)(const ct_runtime_api_v18 *runtime);
    descriptor_function get_descriptor = (descriptor_function)dlsym(module->DynamicHandle, "ct_managed_module_descriptor");
    bind_runtime_function bind_runtime = (bind_runtime_function)dlsym(module->DynamicHandle, "ct_managed_module_bind_runtime");
    const ct_managed_module_descriptor_v1 *descriptor = get_descriptor == NULL ? NULL : get_descriptor();
    if (descriptor == NULL || bind_runtime == NULL) {
        ESP_LOGE(TAG, "Module '%s' does not export the Module ABI 1 descriptor/bind functions", manifest->Name);
        result = -ENOEXEC;
        goto fail;
    }
    if (descriptor->Size != sizeof(*descriptor) || descriptor->RuntimeAbi != CTILDE_RUNTIME_ABI_VERSION ||
        descriptor->ModuleAbi != CTILDE_MANAGED_MODULE_ABI_VERSION || descriptor->Name == NULL || descriptor->Version == NULL ||
        strcmp(descriptor->Name, manifest->Name) != 0 || strcmp(descriptor->Version, manifest->Version) != 0 ||
        descriptor->DependencyCount != manifest->DependencyCount) {
        ESP_LOGE(TAG, "Module descriptor mismatch: descriptor=%p size=%u/%u runtime=%u module=%u name=%p version=%p dependencies=%u/%u",
            (const void *)descriptor,
            (unsigned)descriptor->Size, (unsigned)sizeof(*descriptor), (unsigned)descriptor->RuntimeAbi,
            (unsigned)descriptor->ModuleAbi, (const void *)descriptor->Name,
            (const void *)descriptor->Version, (unsigned)descriptor->DependencyCount,
            (unsigned)manifest->DependencyCount);
        result = -ENOEXEC;
        goto fail;
    }
    if (bind_runtime(ctilde_runtime_api_v18()) != 0) {
        ESP_LOGE(TAG, "Module '%s' rejected Runtime ABI %u", manifest->Name, CTILDE_RUNTIME_ABI_VERSION);
        result = -ELIBBAD;
        goto fail;
    }
    module->Descriptor = descriptor;
    module->Loading = false;
    free(manifest_bytes);
    *output = module;
    return 0;

fail:
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
    if (module->ActiveCalls != 0 || module->LiveAllocations != 0) { xSemaphoreGive(s_registry); abort(); }
    ct_module *dependencies[CT_MAX_DEPENDENCIES];
    const uint32_t dependency_count = module->DependencyCount;
    for (uint32_t index = 0; index < dependency_count; ++index) dependencies[index] = module->Dependencies[index];
    void *handle = module->DynamicHandle;
    for (size_t index = 0; index < sizeof(s_types) / sizeof(s_types[0]); ++index) {
        if (s_types[index].Used && s_types[index].Owner == module) (void)memset(&s_types[index], 0, sizeof(s_types[index]));
    }
    (void)memset(module, 0, sizeof(*module));
    xSemaphoreGive(s_registry);
    if (handle != NULL && dlclose(handle) != 0) abort();
    for (uint32_t index = dependency_count; index > 0; --index) release_module(dependencies[index - 1]);
}

static ct_process *process_from_handle(uintptr_t handle)
{
    const uint32_t id = (uint32_t)handle;
    if (id == 0) return NULL;
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        if (s_processes[index].Used && s_processes[index].Id == id) return &s_processes[index];
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

static void *api_allocate(size_t size, const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    if (context == NULL || context->Process == NULL || descriptor == NULL) return NULL;
    ct_process *process = context->Process;
    ct_module *module = NULL;
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        if (process->Instances[index].Module->Descriptor == descriptor) { module = process->Instances[index].Module; break; }
    }
    if (module == NULL || module->Stopping || size > SIZE_MAX - sizeof(ct_allocation) ||
        (process->HeapLimit != 0 && size > process->HeapLimit - process->HeapBytes)) return NULL;
    ct_allocation *allocation = (ct_allocation *)calloc(1, sizeof(ct_allocation) + size);
    if (allocation == NULL) return NULL;
    allocation->Process = process;
    allocation->Module = module;
    allocation->Size = size;
    allocation->Next = process->Allocations;
    if (process->Allocations != NULL) process->Allocations->Previous = allocation;
    process->Allocations = allocation;
    process->HeapBytes += size;
    ++module->LiveAllocations;
    return allocation + 1;
}

static void api_free(void *value)
{
    if (value == NULL) return;
    ct_allocation *allocation = ((ct_allocation *)value) - 1;
    ct_process *process = allocation->Process;
    if (process == NULL || allocation->Module == NULL || allocation->Size > process->HeapBytes || allocation->Module->LiveAllocations == 0) abort();
    if (allocation->Previous != NULL) allocation->Previous->Next = allocation->Next; else process->Allocations = allocation->Next;
    if (allocation->Next != NULL) allocation->Next->Previous = allocation->Previous;
    process->HeapBytes -= allocation->Size;
    --allocation->Module->LiveAllocations;
    (void)memset(allocation + 1, 0xDD, allocation->Size);
    free(allocation);
}

static void api_runtime_fault(const char *code, const char *file, int32_t line)
{
    ct_process *process = current_process();
    ESP_LOGE(TAG, "process fault %s at %s:%" PRId32, code == NULL ? "?" : code, file == NULL ? "?" : file, line);
    if (process == NULL) abort();
    process->State = CT_PROCESS_FAILED;
    process->ExitCode = -1;
    vTaskDelete(NULL);
    abort();
}

static const ct_type_descriptor *api_register_type(const void *value)
{
    const ct_type_descriptor *descriptor = (const ct_type_descriptor *)value;
    ct_execution_context *context = current_context();
    if (descriptor == NULL || context == NULL || context->Module == NULL) api_runtime_fault("CTT0010", "<type-register>", 0);
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
            if (!compatible) api_runtime_fault("CTT0011", "<type-register>", 0);
            return result;
        }
    }
    if (free_slot == NULL) { xSemaphoreGive(s_registry); api_runtime_fault("CTT0012", "<type-register>", 0); }
    *free_slot = (ct_type_registration){ true, descriptor->FingerprintHigh, descriptor->FingerprintLow, descriptor, context->Module };
    xSemaphoreGive(s_registry);
    return descriptor;
}

static void api_unregister_types(const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    ct_module *module = context == NULL ? NULL : context->Module;
    if (module == NULL || module->Descriptor != descriptor) api_runtime_fault("CTT0013", "<type-unregister>", 0);
    xSemaphoreTake(s_registry, portMAX_DELAY);
    for (size_t index = 0; index < sizeof(s_types) / sizeof(s_types[0]); ++index) {
        if (s_types[index].Used && s_types[index].Owner == module) (void)memset(&s_types[index], 0, sizeof(s_types[index]));
    }
    xSemaphoreGive(s_registry);
}

static void *api_current_module_state(const ct_managed_module_descriptor_v1 *descriptor)
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

static void api_enter_call(const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    if (context == NULL) api_runtime_fault("CTT0014", "<managed-call>", 0);
    for (uint32_t index = 0; index < context->Process->InstanceCount; ++index) {
        ct_module *module = context->Process->Instances[index].Module;
        if (module->Descriptor == descriptor) {
            if (module->Stopping) api_runtime_fault("CTT0015", "<managed-call>", 0);
            ++module->ActiveCalls;
            context->Module = module;
            return;
        }
    }
    api_runtime_fault("CTT0016", "<managed-call>", 0);
}

static void api_leave_call(const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    if (context == NULL || context->Module == NULL || context->Module->Descriptor != descriptor || context->Module->ActiveCalls == 0) abort();
    --context->Module->ActiveCalls;
    context->Module = context->Process->Root;
}

static int32_t api_service(uint32_t service, void *payload, size_t size)
{
    ct_process *process = current_process();
    if (process == NULL) return -EINVAL;
    if (service == CT_RUNTIME_SERVICE_THREAD_ATTACH) { ++process->TaskCount; return 0; }
    if (service == CT_RUNTIME_SERVICE_THREAD_DETACH) { if (process->TaskCount <= 1) return -EINVAL; --process->TaskCount; return 0; }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_WRITE) {
        if (payload == NULL || size != sizeof(ct_runtime_console_transfer_v18)) return -EINVAL;
        ct_runtime_console_transfer_v18 *transfer = (ct_runtime_console_transfer_v18 *)payload;
        if (transfer->Length != 0 && transfer->Data == NULL) return -EINVAL;
        transfer->Count = fwrite(transfer->Data, 1u, transfer->Length, stdout);
        transfer->Eof = false;
        return transfer->Count == transfer->Length ? 0 : -(errno == 0 ? EIO : errno);
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_READ) {
        if (payload == NULL || size != sizeof(ct_runtime_console_transfer_v18)) return -EINVAL;
        ct_runtime_console_transfer_v18 *transfer = (ct_runtime_console_transfer_v18 *)payload;
        if (transfer->Length != 0 && transfer->Data == NULL) return -EINVAL;
        clearerr(stdin);
        transfer->Count = fread(transfer->Data, 1u, transfer->Length, stdin);
        transfer->Eof = feof(stdin);
        if (ferror(stdin) && errno != EAGAIN && errno != EWOULDBLOCK) return -(errno == 0 ? EIO : errno);
        clearerr(stdin);
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_FLUSH)
        return fflush(stdout) == 0 ? 0 : -(errno == 0 ? EIO : errno);
    return -ENOSYS;
}

static const ct_runtime_api_v18 s_runtime_api = {
    sizeof(ct_runtime_api_v18), CTILDE_RUNTIME_ABI_VERSION, api_allocate, api_free, api_free, NULL, api_runtime_fault,
    api_register_type, api_unregister_types, api_current_process, api_current_module_state,
    api_current_thread_state, api_set_thread_state, ct_managed_process_cancellation_requested,
    api_enter_call, api_leave_call, api_service
};

const ct_runtime_api_v18 *ctilde_runtime_api_v18(void)
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
        if (allocation->Module == NULL || allocation->Module->LiveAllocations == 0) abort();
        --allocation->Module->LiveAllocations;
        free(allocation);
    }
    process->HeapBytes = 0;
}

static void cleanup_process(ct_process *process, bool forced)
{
    if (__atomic_exchange_n(&process->Cleaned, true, __ATOMIC_ACQ_REL)) return;
    ct_execution_context *previous_context = current_context();
    ct_execution_context cleanup_context = { .Process = process, .Module = process->Root };
    cleanup_context.ThreadState = cleanup_context.PrimaryThreadState;
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, &cleanup_context);
    if (!forced) {
        for (uint32_t index = process->InstanceCount; index > 0; --index) {
            ct_module_instance *instance = &process->Instances[index - 1];
            if (instance->Initialized) {
                cleanup_context.Module = instance->Module;
                ++instance->Module->ActiveCalls;
                instance->Module->Descriptor->Finalize();
                --instance->Module->ActiveCalls;
                instance->Initialized = false;
            }
        }
    }
    release_arena(process);
    for (uint32_t index = process->InstanceCount; index > 0; --index) {
        free(process->Instances[index - 1].State);
        process->Instances[index - 1].State = NULL;
    }
    process->InstanceCount = 0;
    ct_message *message = NULL;
    while (xQueueReceive(process->Mailbox, &message, 0) == pdTRUE) free(message);
    for (int32_t index = 0; index < process->ArgumentCount; ++index) { free(process->Arguments[index]); process->Arguments[index] = NULL; }
    process->ArgumentCount = 0;
    ct_module *root = process->Root;
    process->Root = NULL;
    process->MainTask = NULL;
    process->TaskCount = 0;
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, previous_context);
    release_module(root);
    if (process->State < CT_PROCESS_EXITED)
        process->State = forced ? CT_PROCESS_FAILED : CT_PROCESS_EXITED;
    xSemaphoreGive(process->Completion);
}

static void tls_deleted(int index, void *value)
{
    (void)index;
    ct_execution_context *context = (ct_execution_context *)value;
    if (context == NULL || context->Process == NULL) return;
    ct_process *process = context->Process;
    process->CleanupQueued = true;
    (void)xQueueSend(s_reaper_queue, &process, 0);
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
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        ct_module_instance *instance = &process->Instances[index];
        process->Context.Module = instance->Module;
        ++instance->Module->ActiveCalls;
        instance->Module->Descriptor->Initialize();
        --instance->Module->ActiveCalls;
        instance->Initialized = true;
    }
    process->Context.Module = process->Root;
    process->State = CT_PROCESS_RUNNING;
    void *managed_arguments = process->Root->Descriptor->CreateArguments(process->ArgumentCount,
        (const char *const *)process->Arguments, process->ArgumentLengths);
    ++process->Root->ActiveCalls;
    process->ExitCode = process->Root->Descriptor->Main(managed_arguments);
    --process->Root->ActiveCalls;
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, NULL);
    cleanup_process(process, false);
    vTaskDelete(NULL);
}

static ct_process *allocate_process(void)
{
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        if (!s_processes[index].Used) {
            ct_process *process = &s_processes[index];
            (void)memset(process, 0, sizeof(*process));
            process->Used = true;
            process->Id = s_next_process_id++;
            process->Completion = xSemaphoreCreateBinaryStatic(&process->CompletionStorage);
            process->Mailbox = xQueueCreateStatic(CT_MAILBOX_DEPTH, sizeof(ct_message *), process->MailboxBuffer, &process->MailboxStorage);
            return process;
        }
    }
    return NULL;
}

uintptr_t ct_managed_process_start(const void *path_value, const void *arguments_value)
{
    const ct_managed_string *path = (const ct_managed_string *)path_value;
    const ct_managed_array *arguments = (const ct_managed_array *)arguments_value;
    if (!s_initialized || path == NULL || arguments == NULL || path->Length <= 0 || arguments->Length < 0 || arguments->Length > CT_MAX_ARGUMENTS) return 0;
    char path_buffer[CT_MODULE_PATH_MAX];
    if (resolve_module_path_bytes(path->Data, (size_t)path->Length, path_buffer) != 0) return 0;
    ct_module *module = NULL;
    const char *chain[1] = { NULL };
    if (load_module_recursive(path_buffer, chain, 0, &module) != 0 || module->Descriptor->Kind != 1 || module->Descriptor->Main == NULL || module->Descriptor->CreateArguments == NULL) return 0;
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_process *process = allocate_process();
    xSemaphoreGive(s_registry);
    if (process == NULL) { release_module(module); return 0; }
    process->Root = module;
    process->State = CT_PROCESS_STARTING;
    process->HeapLimit = (size_t)module->Descriptor->HeapLimitBytes;
    process->ArgumentCount = arguments->Length;
    ct_managed_string *const *values = (ct_managed_string *const *)(const void *)arguments->Data;
    for (int32_t index = 0; index < arguments->Length; ++index) {
        if (values[index] == NULL || values[index]->Length < 0) { process->State = CT_PROCESS_FAILED; cleanup_process(process, true); return 0; }
        const size_t length = (size_t)values[index]->Length;
        process->Arguments[index] = (char *)malloc(length + 1);
        if (process->Arguments[index] == NULL) { process->State = CT_PROCESS_FAILED; cleanup_process(process, true); return 0; }
        (void)memcpy(process->Arguments[index], values[index]->Data, length);
        process->Arguments[index][length] = '\0';
        process->ArgumentLengths[index] = length;
    }
    if (add_instance_graph(process, module) != 0) { process->State = CT_PROCESS_FAILED; cleanup_process(process, true); return 0; }
    const uint32_t stack_words = (module->Descriptor->MainTaskStackBytes + sizeof(StackType_t) - 1) / sizeof(StackType_t);
    process->TaskCount = 1;
    if (xTaskCreate(process_main, module->Name, stack_words, process, tskIDLE_PRIORITY + 1, &process->MainTask) != pdPASS) {
        process->TaskCount = 0;
        process->State = CT_PROCESS_FAILED; cleanup_process(process, true); return 0;
    }
    return (uintptr_t)process->Id;
}

uint32_t ct_managed_process_id(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL ? 0 : process->Id; }
ct_managed_process_state ct_managed_process_get_state(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL ? CT_PROCESS_FAILED : process->State; }
bool ct_managed_process_has_exited(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL || process->State >= CT_PROCESS_EXITED; }
int32_t ct_managed_process_exit_code(uintptr_t handle) { ct_process *process = process_from_handle(handle); return process == NULL || process->State < CT_PROCESS_EXITED ? 0 : process->ExitCode; }
void ct_managed_process_cancel(uintptr_t handle) { ct_process *process = process_from_handle(handle); if (process != NULL && process->State < CT_PROCESS_EXITED) { process->Cancellation = true; process->State = CT_PROCESS_CANCELLING; } }
bool ct_managed_process_cancellation_requested(void) { ct_process *process = current_process(); return process != NULL && process->Cancellation; }

bool ct_managed_process_try_wait(uintptr_t handle, uint32_t timeout_milliseconds, int32_t *exit_code)
{
    ct_process *process = process_from_handle(handle);
    if (process == NULL) return false;
    const TickType_t ticks = timeout_milliseconds == UINT32_MAX ? portMAX_DELAY : pdMS_TO_TICKS(timeout_milliseconds);
    if (xSemaphoreTake(process->Completion, ticks) != pdTRUE) return false;
    xSemaphoreGive(process->Completion);
    if (exit_code != NULL) *exit_code = process->ExitCode;
    return true;
}

int32_t ct_managed_process_wait(uintptr_t handle)
{
    int32_t result = 0;
    (void)ct_managed_process_try_wait(handle, UINT32_MAX, &result);
    return result;
}

void ct_managed_process_terminate(uintptr_t handle, uint32_t grace_milliseconds)
{
    ct_process *process = process_from_handle(handle);
    if (process == NULL || process->State >= CT_PROCESS_EXITED) return;
    ct_managed_process_cancel(handle);
    int32_t ignored;
    if (ct_managed_process_try_wait(handle, grace_milliseconds, &ignored)) return;
    process->State = CT_PROCESS_TERMINATED;
    process->ExitCode = -2;
    TaskHandle_t task = process->MainTask;
    if (task != NULL) vTaskDelete(task);
}

void ct_managed_process_send(uintptr_t handle, const void *payload_value)
{
    ct_process *process = process_from_handle(handle);
    const ct_managed_array *payload = (const ct_managed_array *)payload_value;
    if (process == NULL || payload == NULL || payload->Length < 0 || process->State >= CT_PROCESS_EXITED) return;
    const size_t length = (size_t)payload->Length;
    ct_message *message = (ct_message *)malloc(sizeof(ct_message) + length);
    if (message == NULL) return;
    message->Length = length;
    if (length != 0) (void)memcpy(message->Data, payload->Data, length);
    if (xQueueSend(process->Mailbox, &message, portMAX_DELAY) != pdTRUE) free(message);
}

bool ct_managed_process_try_receive(uintptr_t handle, uint32_t timeout_milliseconds, const void *type_template, void **payload)
{
    (void)type_template;
    ct_process *process = process_from_handle(handle);
    ct_execution_context *context = current_context();
    if (process == NULL || context == NULL || context->Module == NULL || payload == NULL) return false;
    ct_message *message = NULL;
    const TickType_t ticks = timeout_milliseconds == UINT32_MAX ? portMAX_DELAY : pdMS_TO_TICKS(timeout_milliseconds);
    if (xQueueReceive(process->Mailbox, &message, ticks) != pdTRUE) return false;
    *payload = context->Module->Descriptor->CreateBytes(message->Data, message->Length);
    free(message);
    return true;
}

void *ct_managed_process_receive(uintptr_t handle, const void *type_template)
{
    void *result = NULL;
    while (!ct_managed_process_try_receive(handle, UINT32_MAX, type_template, &result)) { }
    return result;
}

size_t ctilde_managed_processes(ct_managed_process_info *output, size_t capacity)
{
    size_t count = 0;
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_PROCESSES; ++index) {
        ct_process *process = &s_processes[index];
        if (!process->Used) continue;
        if (output != NULL && count < capacity) output[count] = (ct_managed_process_info){ process->Id, process->State, process->ExitCode, process->HeapBytes, process->HeapLimit, process->TaskCount, process->Root == NULL ? NULL : process->Root->Name };
        ++count;
    }
    return count;
}

size_t ctilde_managed_modules(ct_managed_module_info *output, size_t capacity)
{
    size_t count = 0;
    for (size_t index = 0; index < CONFIG_CTILDE_MANAGED_MAX_MODULES; ++index) {
        ct_module *module = &s_modules[index];
        if (!module->Used) continue;
        if (output != NULL && count < capacity) output[count] = (ct_managed_module_info){ module->Name, module->Version, module->References, module->ActiveCalls, module->LiveAllocations, module->Stopping };
        ++count;
    }
    return count;
}

static const struct esp_elfsym s_symbols[] = {
    ESP_ELFSYM_EXPORT(ct_managed_process_start), ESP_ELFSYM_EXPORT(ct_managed_process_id),
    ESP_ELFSYM_EXPORT(ct_managed_process_get_state), ESP_ELFSYM_EXPORT(ct_managed_process_has_exited),
    ESP_ELFSYM_EXPORT(ct_managed_process_exit_code), ESP_ELFSYM_EXPORT(ct_managed_process_cancel),
    ESP_ELFSYM_EXPORT(ct_managed_process_terminate), ESP_ELFSYM_EXPORT(ct_managed_process_wait),
    ESP_ELFSYM_EXPORT(ct_managed_process_try_wait), ESP_ELFSYM_EXPORT(ct_managed_process_send),
    ESP_ELFSYM_EXPORT(ct_managed_process_receive), ESP_ELFSYM_EXPORT(ct_managed_process_try_receive),
    ESP_ELFSYM_EXPORT(ct_managed_process_cancellation_requested),
    ESP_ELFSYM_EXPORT(memcpy), ESP_ELFSYM_EXPORT(memset), ESP_ELFSYM_EXPORT(memcmp), ESP_ELFSYM_EXPORT(memchr),
    ESP_ELFSYM_EXPORT(strlen), ESP_ELFSYM_EXPORT(snprintf), ESP_ELFSYM_EXPORT(fprintf), ESP_ELFSYM_EXPORT(fwrite),
    ESP_ELFSYM_EXPORT(fputc), ESP_ELFSYM_EXPORT(fputs), ESP_ELFSYM_EXPORT(fflush),
    ESP_ELFSYM_EXPORT(longjmp), ESP_ELFSYM_EXPORT(esp_err_to_name), ESP_ELFSYM_EXPORT(__getreent),
    ESP_ELFSYM_EXPORT(__extendsfdf2), ESP_ELFSYM_EXPORT(__udivdi3), ESP_ELFSYM_EXPORT(__umoddi3),
    ESP_ELFSYM_END
};

int ctilde_managed_runtime_initialize(void)
{
    if (s_initialized) return 0;
    s_registry = xSemaphoreCreateMutexStatic(&s_registry_storage);
    s_reaper_queue = xQueueCreateStatic(16, sizeof(ct_process *), s_reaper_queue_buffer, &s_reaper_queue_storage);
    if (s_registry == NULL || s_reaper_queue == NULL) return -ENOMEM;
    if (esp_elf_register_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols) != 0) return -EIO;
    if (xTaskCreate(reaper_main, "ctilde_reaper", 4096 / sizeof(StackType_t), NULL, tskIDLE_PRIORITY + 1, NULL) != pdPASS) return -ENOMEM;
    s_initialized = true;
    return 0;
}
