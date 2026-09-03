#include "ctilde_managed_runtime.h"

#include <errno.h>
#include <dirent.h>
#include <fcntl.h>
#include <inttypes.h>
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include "esp_dlfcn.h"
#include "esp_err.h"
#include "esp_elf.h"
#include "esp_log.h"
#include "esp_timer.h"
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
static_assert(CTILDE_MANAGED_MODULE_NAME_CAPACITY == 64u,
    "Managed Module ABI 1 requires 64-byte name fields");
static_assert(CTILDE_MANAGED_MODULE_VERSION_CAPACITY == 32u,
    "Managed Module ABI 1 requires 32-byte version fields");

#define CT_MODULE_PATH_MAX 256
#define CT_MODULE_NAME_MAX CTILDE_MANAGED_MODULE_NAME_CAPACITY
#define CT_MODULE_VERSION_MAX CTILDE_MANAGED_MODULE_VERSION_CAPACITY
#define CT_MAX_DEPENDENCIES 16
#define CT_MAX_PROCESS_MODULES 16
#define CT_MAX_ARGUMENTS 32
#define CT_MAILBOX_DEPTH 16
#define CT_THREAD_STATE_BYTES 128
#define CT_MAX_CALL_DEPTH CT_MAX_PROCESS_MODULES
#define CT_RUNTIME_GATE_STOPPED UINT32_C(0x80000000)
#define CT_RUNTIME_GATE_COUNT UINT32_C(0x7fffffff)
#define CT_TERMINATION_NONE UINT32_C(0)
#define CT_TERMINATION_PUBLISHING UINT32_C(1)
#define CT_TERMINATION_QUEUED UINT32_C(2)
#define CT_TERMINATION_POLL_MILLISECONDS UINT32_C(10)
#define CT_INITIALIZATION_UNINITIALIZED UINT32_C(0)
#define CT_INITIALIZATION_RUNNING UINT32_C(1)
#define CT_INITIALIZATION_READY UINT32_C(2)

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
    ct_module *PreviousModules[CT_MAX_CALL_DEPTH];
    uint32_t CallDepth;
    uint32_t RuntimeOperationDepth;
    uint8_t PrimaryThreadState[CT_THREAD_STATE_BYTES] __attribute__((aligned(8)));
} ct_execution_context;

struct ct_process {
    bool Used;
    bool Cleaned;
    bool Completed;
    bool CleanupQueued;
    uint32_t TerminationRequestState;
    bool TerminationDispatched;
    bool ForceDeleteIssued;
    uint32_t Id;
    volatile ct_managed_process_state State;
    volatile bool Cancellation;
    int32_t ExitCode;
    ct_module *Root;
    char RootName[CT_MODULE_NAME_MAX];
    TaskHandle_t MainTask;
    uint32_t TaskCount;
    uint32_t RuntimeGate;
    size_t HeapBytes;
    size_t HeapLimit;
    ct_allocation *Allocations;
    ct_module_instance Instances[CT_MAX_PROCESS_MODULES];
    uint32_t InstanceCount;
    char *Arguments[CT_MAX_ARGUMENTS];
    size_t ArgumentLengths[CT_MAX_ARGUMENTS];
    int32_t ArgumentCount;
    char *CurrentDirectory;
    ct_process_file *Files;
    ct_process_directory *Directories;
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
static uint32_t s_published_process_ids[CONFIG_CTILDE_MANAGED_MAX_PROCESSES];
static ct_type_registration s_types[128];
static uint32_t s_next_process_id = 1;
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
    if (bytes[0] != 0x7f || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' ||
        bytes[4] != 1 || bytes[5] != 1 || bytes[6] != 1) {
        free(bytes); return -ENOEXEC;
    }
    const uint16_t elf_type = read_u16_le(bytes + 16);
    const uint16_t machine = read_u16_le(bytes + 18);
#if CONFIG_IDF_TARGET_ARCH_XTENSA
    const uint16_t expected_machine = 94;
    const uint32_t expected_architecture = 1;
#else
    const uint16_t expected_machine = 243;
    const uint32_t expected_architecture = 2;
#endif
    if (elf_type != 3 || machine != expected_machine || read_u16_le(bytes + 40) != 52) { free(bytes); return -ENOEXEC; }

    const size_t section_table = read_u32_le(bytes + 32);
    const uint16_t section_entry_size = read_u16_le(bytes + 46);
    const uint16_t section_count = read_u16_le(bytes + 48);
    const uint16_t section_names_index = read_u16_le(bytes + 50);
    if (section_entry_size != 40 || section_count == 0 || section_names_index >= section_count ||
        !byte_range(section_table, (size_t)section_entry_size * section_count, (size_t)length)) {
        free(bytes); return -ENOEXEC;
    }
    const uint8_t *names_header = bytes + section_table + (size_t)section_entry_size * section_names_index;
    const size_t names_offset = read_u32_le(names_header + 16);
    const size_t names_size = read_u32_le(names_header + 20);
    if (read_u32_le(names_header + 4) != 3 || !byte_range(names_offset, names_size, (size_t)length)) {
        free(bytes); return -ENOEXEC;
    }

    const uint8_t *manifest_section = NULL;
    size_t manifest_section_size = 0;
    for (uint16_t index = 0; index < section_count; ++index) {
        const uint8_t *section = bytes + section_table + (size_t)section_entry_size * index;
        const size_t name_offset = read_u32_le(section);
        if (name_offset >= names_size) { free(bytes); return -ENOEXEC; }
        const char *name = (const char *)(const void *)(bytes + names_offset + name_offset);
        if (memchr(name, '\0', names_size - name_offset) == NULL) { free(bytes); return -ENOEXEC; }
        if (strcmp(name, ".ctilde.manifest") != 0) continue;
        if (manifest_section != NULL || read_u32_le(section + 4) != 1) { free(bytes); return -ENOEXEC; }
        const size_t offset = read_u32_le(section + 16);
        const size_t size = read_u32_le(section + 20);
        if (!byte_range(offset, size, (size_t)length)) { free(bytes); return -ENOEXEC; }
        manifest_section = bytes + offset;
        manifest_section_size = size;
    }

    const size_t fixed_size = offsetof(ct_binary_manifest_v1, Dependencies);
    if (manifest_section == NULL || manifest_section_size < fixed_size) { free(bytes); return -ENOEXEC; }
    ct_binary_manifest_v1 header;
    (void)memset(&header, 0, sizeof(header));
    (void)memcpy(&header, manifest_section, fixed_size);
    static const uint8_t magic[8] = { 'C', 'T', 'M', 'O', 'D', 1, 0, 0 };
    if (memcmp(header.Magic, magic, sizeof(magic)) != 0 || header.HeaderSize != fixed_size ||
        header.DependencyCount > CT_MAX_DEPENDENCIES) {
        free(bytes); return -ENOEXEC;
    }
    const size_t required_size = fixed_size + (size_t)header.DependencyCount * sizeof(ct_binary_dependency_v1);
    if (header.TotalSize != required_size || required_size > manifest_section_size) { free(bytes); return -ENOEXEC; }
    ct_binary_manifest_v1 *manifest = (ct_binary_manifest_v1 *)malloc(required_size);
    if (manifest == NULL) { free(bytes); return -ENOMEM; }
    (void)memcpy(manifest, manifest_section, required_size);
    free(bytes);

    if (manifest->RuntimeAbi != CTILDE_RUNTIME_ABI_VERSION ||
        manifest->ModuleAbi != CTILDE_MANAGED_MODULE_ABI_VERSION || manifest->Architecture != expected_architecture ||
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

static bool hash_matches_text(const uint8_t expected[32], const char *actual)
{
    static const char digits[] = "0123456789abcdef";
    if (actual == NULL || strlen(actual) != 64) return false;
    for (size_t index = 0; index < 32; ++index) {
        if (actual[index * 2] != digits[expected[index] >> 4] || actual[index * 2 + 1] != digits[expected[index] & 15]) return false;
    }
    return true;
}

static bool descriptor_matches_manifest(const ct_managed_module_descriptor_v1 *descriptor, const ct_binary_manifest_v1 *manifest)
{
    if (descriptor == NULL || descriptor->Size != sizeof(*descriptor) ||
        descriptor->RuntimeAbi != CTILDE_RUNTIME_ABI_VERSION || descriptor->ModuleAbi != CTILDE_MANAGED_MODULE_ABI_VERSION ||
        descriptor->Kind != manifest->Kind || descriptor->Name == NULL || descriptor->Version == NULL ||
        strcmp(descriptor->Name, manifest->Name) != 0 || strcmp(descriptor->Version, manifest->Version) != 0 ||
        !hash_matches_text(manifest->BuildIdentity, descriptor->BuildIdentity) ||
        !hash_matches_text(manifest->ApiHash, descriptor->ApiHash) ||
        descriptor->DependencyCount != manifest->DependencyCount ||
        (descriptor->DependencyCount != 0 && descriptor->Dependencies == NULL) ||
        descriptor->MainTaskStackBytes != manifest->MainTaskStackBytes ||
        descriptor->HeapLimitBytes != manifest->HeapLimitBytes ||
        descriptor->StaticStateAlignment == 0 || descriptor->StaticStateAlignment > _Alignof(max_align_t) ||
        (descriptor->StaticStateAlignment & (descriptor->StaticStateAlignment - 1)) != 0 ||
        descriptor->Initialize == NULL || descriptor->Finalize == NULL || descriptor->CreateBytes == NULL ||
        (descriptor->Kind == 1 && (descriptor->Main == NULL || descriptor->CreateArguments == NULL)) ||
        (descriptor->Kind == 2 && (descriptor->Main != NULL || descriptor->CreateArguments != NULL))) return false;
    for (uint32_t index = 0; index < manifest->DependencyCount; ++index) {
        const ct_binary_dependency_v1 *expected = &manifest->Dependencies[index];
        const ct_managed_dependency_v1 *actual = &descriptor->Dependencies[index];
        if (actual->Name == NULL || actual->Version == NULL || strcmp(actual->Name, expected->Name) != 0 ||
            strcmp(actual->Version, expected->Version) != 0 ||
            !hash_matches_text(expected->BuildIdentity, actual->BuildIdentity) ||
            !hash_matches_text(expected->ApiHash, actual->ApiHash)) return false;
    }
    return true;
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

    module->DynamicHandle = dlopen(path, RTLD_NOW);
    if (module->DynamicHandle == NULL) { result = -ENOEXEC; goto fail; }
    typedef const ct_managed_module_descriptor_v1 *(*descriptor_function)(void);
    typedef int32_t (*bind_runtime_function)(const ct_runtime_api_v19 *runtime);
    descriptor_function get_descriptor = (descriptor_function)dlsym(module->DynamicHandle, "ct_managed_module_descriptor");
    bind_runtime_function bind_runtime = (bind_runtime_function)dlsym(module->DynamicHandle, "ct_managed_module_bind_runtime");
    const ct_managed_module_descriptor_v1 *descriptor = get_descriptor == NULL ? NULL : get_descriptor();
    if (descriptor == NULL || bind_runtime == NULL) {
        ESP_LOGE(TAG, "Module '%s' does not export the Module ABI 1 descriptor/bind functions", manifest->Name);
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
    if (bind_runtime(ctilde_runtime_api_v19()) != 0) {
        ESP_LOGE(TAG, "Module '%s' rejected Runtime ABI %u", manifest->Name, CTILDE_RUNTIME_ABI_VERSION);
        result = -ELIBBAD;
        goto fail;
    }
    xSemaphoreTake(s_registry, portMAX_DELAY);
    module->Descriptor = descriptor;
    module->Loading = false;
    xSemaphoreGive(s_registry);
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

static void *api_allocate(size_t size, const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    if (context == NULL || context->Process == NULL || descriptor == NULL) return NULL;
    if (!begin_runtime_operation(context)) await_forced_task_deletion();
    ct_process *process = context->Process;
    ct_module *module = NULL;
    for (uint32_t index = 0; index < process->InstanceCount; ++index) {
        if (process->Instances[index].Module->Descriptor == descriptor) { module = process->Instances[index].Module; break; }
    }
    const size_t heap_bytes = __atomic_load_n(&process->HeapBytes, __ATOMIC_ACQUIRE);
    if (module == NULL || module->Stopping || size > SIZE_MAX - sizeof(ct_allocation) ||
        (process->HeapLimit != 0 && (heap_bytes > process->HeapLimit ||
            size > process->HeapLimit - heap_bytes))) {
        end_runtime_operation(context);
        return NULL;
    }
    ct_allocation *allocation = (ct_allocation *)calloc(1, sizeof(ct_allocation) + size);
    if (allocation == NULL) {
        end_runtime_operation(context);
        return NULL;
    }
    allocation->Process = process;
    allocation->Module = module;
    allocation->Size = size;
    allocation->Next = process->Allocations;
    if (process->Allocations != NULL) process->Allocations->Previous = allocation;
    process->Allocations = allocation;
    (void)__atomic_add_fetch(&process->HeapBytes, size, __ATOMIC_ACQ_REL);
    (void)__atomic_add_fetch(&module->LiveAllocations, 1u, __ATOMIC_ACQ_REL);
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
    if (process == NULL || process != context->Process || allocation->Module == NULL ||
        allocation->Size > __atomic_load_n(&process->HeapBytes, __ATOMIC_ACQUIRE) ||
        __atomic_load_n(&allocation->Module->LiveAllocations, __ATOMIC_ACQUIRE) == 0) abort();
    if (allocation->Previous != NULL) allocation->Previous->Next = allocation->Next; else process->Allocations = allocation->Next;
    if (allocation->Next != NULL) allocation->Next->Previous = allocation->Previous;
    if (__atomic_fetch_sub(&process->HeapBytes, allocation->Size, __ATOMIC_ACQ_REL) < allocation->Size ||
        __atomic_fetch_sub(&allocation->Module->LiveAllocations, 1u, __ATOMIC_ACQ_REL) == 0) abort();
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

static void api_unregister_types(const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    ct_module *module = context == NULL ? NULL : context->Module;
    if (module == NULL || module->Descriptor != descriptor) api_runtime_fault("CTT0013", "<type-unregister>", 0);
    /* Module finalizers run once per process instance. Canonical descriptors
       remain globally valid while any process or dependent module keeps the
       provider loaded; release_module removes them immediately before dlclose. */
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
    if (!enter_context_call(context, target)) api_runtime_fault("CTT0018", "<managed-call-depth>", 0);
}

static void api_leave_call(const ct_managed_module_descriptor_v1 *descriptor)
{
    ct_execution_context *context = current_context();
    if (context == NULL || context->Module == NULL || context->Module->Descriptor != descriptor ||
        !leave_context_call(context, context->Module)) abort();
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

void ctilde_managed_storage_invalidate_prefix(const char *prefix, uint64_t generation)
{
    (void)generation;
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || prefix == NULL) return;
    xSemaphoreTake(s_registry, portMAX_DELAY);
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
    }
    xSemaphoreGive(s_registry);
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
    if (process == NULL) return -EINVAL;
    if (service == CT_RUNTIME_SERVICE_THREAD_ATTACH) {
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        (void)__atomic_add_fetch(&process->TaskCount, 1u, __ATOMIC_ACQ_REL);
        end_runtime_operation(context);
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_THREAD_DETACH) {
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        uint32_t count = __atomic_load_n(&process->TaskCount, __ATOMIC_ACQUIRE);
        do {
            if (count <= 1) {
                end_runtime_operation(context);
                return -EINVAL;
            }
        } while (!__atomic_compare_exchange_n(&process->TaskCount, &count, count - 1, false,
            __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE));
        end_runtime_operation(context);
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_WRITE) {
        if (payload == NULL || size != sizeof(ct_runtime_console_transfer_v19)) return -EINVAL;
        ct_runtime_console_transfer_v19 *transfer = (ct_runtime_console_transfer_v19 *)payload;
        if (transfer->Length != 0 && transfer->Data == NULL) return -EINVAL;
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        transfer->Count = fwrite(transfer->Data, 1u, transfer->Length, stdout);
        transfer->Eof = false;
        const int32_t result = transfer->Count == transfer->Length ? 0 : -(errno == 0 ? EIO : errno);
        end_runtime_operation(context);
        return result;
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_READ) {
        if (payload == NULL || size != sizeof(ct_runtime_console_transfer_v19)) return -EINVAL;
        ct_runtime_console_transfer_v19 *transfer = (ct_runtime_console_transfer_v19 *)payload;
        if (transfer->Length != 0 && transfer->Data == NULL) return -EINVAL;
        clearerr(stdin);
        transfer->Count = fread(transfer->Data, 1u, transfer->Length, stdin);
        transfer->Eof = feof(stdin);
        if (ferror(stdin) && errno != EAGAIN && errno != EWOULDBLOCK) return -(errno == 0 ? EIO : errno);
        clearerr(stdin);
        return 0;
    }
    if (service == CT_RUNTIME_SERVICE_CONSOLE_FLUSH) {
        if (!begin_runtime_operation(context)) await_forced_task_deletion();
        const int32_t result = fflush(stdout) == 0 ? 0 : -(errno == 0 ? EIO : errno);
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

static const ct_runtime_api_v19 s_runtime_api = {
    sizeof(ct_runtime_api_v19), CTILDE_RUNTIME_ABI_VERSION, api_allocate, api_free, api_free, NULL, api_runtime_fault,
    api_register_type, api_unregister_types, api_current_process, api_current_module_state,
    api_current_thread_state, api_set_thread_state, ct_managed_process_cancellation_requested,
    api_enter_call, api_leave_call, api_service
};

const ct_runtime_api_v19 *ctilde_runtime_api_v19(void)
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
    close_process_io(process);
    free(process->CurrentDirectory);
    process->CurrentDirectory = NULL;
    xSemaphoreGive(s_registry);
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
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_module *root = process->Root;
    process->Root = NULL;
    xSemaphoreGive(s_registry);
    __atomic_store_n(&process->MainTask, NULL, __ATOMIC_RELEASE);
    __atomic_store_n(&process->TaskCount, 0u, __ATOMIC_RELEASE);
    vTaskSetThreadLocalStoragePointer(NULL, CONFIG_CTILDE_MANAGED_TLS_INDEX, previous_context);
    release_module(root);
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
        if (!s_processes[index].Used) {
            ct_process *process = &s_processes[index];
            (void)memset(process, 0, sizeof(*process));
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
    __atomic_store_n(&process->State, CT_PROCESS_FAILED, __ATOMIC_RELEASE);
    cleanup_process(process, true);
    vSemaphoreDelete(process->Completion);
    vQueueDelete(process->Mailbox);
    xSemaphoreTake(s_registry, portMAX_DELAY);
    (void)memset(process, 0, sizeof(*process));
    xSemaphoreGive(s_registry);
    return 0;
}

static uintptr_t start_process_core(const void *path_value, const void *arguments_value)
{
    const ct_managed_string *path = (const ct_managed_string *)path_value;
    const ct_managed_array *arguments = (const ct_managed_array *)arguments_value;
    if (!__atomic_load_n(&s_initialized, __ATOMIC_ACQUIRE) || path == NULL || arguments == NULL || path->Length <= 0 || arguments->Length < 0 || arguments->Length > CT_MAX_ARGUMENTS) return 0;
    char path_buffer[CT_MODULE_PATH_MAX];
    if (resolve_module_path_bytes(path->Data, (size_t)path->Length, path_buffer) != 0) return 0;
    ct_module *module = NULL;
    const char *chain[1] = { NULL };
    if (load_module_recursive(path_buffer, chain, 0, &module) != 0) return 0;
    if (module->Descriptor->Kind != 1 || module->Descriptor->Main == NULL || module->Descriptor->CreateArguments == NULL) {
        release_module(module);
        return 0;
    }
    xSemaphoreTake(s_registry, portMAX_DELAY);
    ct_process *process = allocate_process();
    xSemaphoreGive(s_registry);
    if (process == NULL) { release_module(module); return 0; }
    process->Root = module;
    (void)snprintf(process->RootName, sizeof(process->RootName), "%s", module->Name);
    process->State = CT_PROCESS_STARTING;
    process->HeapLimit = (size_t)module->Descriptor->HeapLimitBytes;
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
    if (add_instance_graph(process, module) != 0) return fail_unpublished_process_start(process);
    process->TaskCount = 1;
    if (xTaskCreate(process_main, module->Name, module->Descriptor->MainTaskStackBytes, process,
            tskIDLE_PRIORITY + 1, &process->MainTask) != pdPASS) {
        process->TaskCount = 0;
        return fail_unpublished_process_start(process);
    }
    const size_t process_index = (size_t)(process - s_processes);
    while (__atomic_load_n(&s_published_process_ids[process_index], __ATOMIC_ACQUIRE) != process->Id) vTaskDelay(1);
    return (uintptr_t)process->Id;
}

uintptr_t ct_managed_process_start(const void *path_value, const void *arguments_value)
{
    ct_execution_context *context = current_context();
    if (context != NULL && !begin_runtime_operation(context)) await_forced_task_deletion();
    const uintptr_t result = start_process_core(path_value, arguments_value);
    if (context != NULL) end_runtime_operation(context);
    return result;
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
        __atomic_store_n(&process->ExitCode, -2, __ATOMIC_RELEASE);
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
    ESP_ELFSYM_EXPORT(ct_managed_process_start), ESP_ELFSYM_EXPORT(ct_managed_process_current),
    ESP_ELFSYM_EXPORT(ct_managed_process_id),
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
