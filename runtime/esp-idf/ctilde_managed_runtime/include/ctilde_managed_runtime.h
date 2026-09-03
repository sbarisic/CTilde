#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CTILDE_RUNTIME_ABI_VERSION 19u
#define CTILDE_MANAGED_MODULE_ABI_VERSION 1u
#define CTILDE_MANAGED_MODULE_SD_ROOT "/sd/modules"
#define CTILDE_MANAGED_MODULE_FALLBACK_ROOT "/storage/modules"
#define CTILDE_MANAGED_MODULE_ROOT CTILDE_MANAGED_MODULE_FALLBACK_ROOT
#define CTILDE_MANAGED_MODULE_NAME_CAPACITY 64u
#define CTILDE_MANAGED_MODULE_VERSION_CAPACITY 32u

#define CT_RUNTIME_SERVICE_THREAD_ATTACH 1u
#define CT_RUNTIME_SERVICE_THREAD_DETACH 2u
#define CT_RUNTIME_SERVICE_CONSOLE_WRITE 16u
#define CT_RUNTIME_SERVICE_CONSOLE_READ 17u
#define CT_RUNTIME_SERVICE_CONSOLE_FLUSH 18u
#define CT_RUNTIME_SERVICE_FILE_OPEN 32u
#define CT_RUNTIME_SERVICE_FILE_READ 33u
#define CT_RUNTIME_SERVICE_FILE_WRITE 34u
#define CT_RUNTIME_SERVICE_FILE_SEEK 35u
#define CT_RUNTIME_SERVICE_FILE_LENGTH 36u
#define CT_RUNTIME_SERVICE_FILE_SET_LENGTH 37u
#define CT_RUNTIME_SERVICE_FILE_FLUSH 38u
#define CT_RUNTIME_SERVICE_FILE_CLOSE 39u
#define CT_RUNTIME_SERVICE_PATH_METADATA 48u
#define CT_RUNTIME_SERVICE_FILE_DELETE 49u
#define CT_RUNTIME_SERVICE_PATH_MOVE 50u
#define CT_RUNTIME_SERVICE_DIRECTORY_CREATE 51u
#define CT_RUNTIME_SERVICE_DIRECTORY_DELETE 52u
#define CT_RUNTIME_SERVICE_DIRECTORY_OPEN 53u
#define CT_RUNTIME_SERVICE_DIRECTORY_READ 54u
#define CT_RUNTIME_SERVICE_DIRECTORY_CLOSE 55u
#define CT_RUNTIME_SERVICE_CURRENT_DIRECTORY_GET 56u
#define CT_RUNTIME_SERVICE_CURRENT_DIRECTORY_SET 57u
#define CT_RUNTIME_SERVICE_PATH_SEPARATOR 58u

typedef struct ct_runtime_console_transfer_v19 {
    uint8_t *Data;
    size_t Length;
    size_t Count;
    bool Eof;
} ct_runtime_console_transfer_v19;

typedef struct ct_runtime_io_path_v19 { uint32_t Size; const uint8_t *Path; size_t PathLength; } ct_runtime_io_path_v19;
typedef struct ct_runtime_io_open_v19 { uint32_t Size; const uint8_t *Path; size_t PathLength; uint8_t Mode; uint8_t Access; uintptr_t Handle; } ct_runtime_io_open_v19;
typedef struct ct_runtime_io_transfer_v19 { uint32_t Size; uintptr_t Handle; uint8_t *Data; size_t Length; size_t Count; bool Eof; } ct_runtime_io_transfer_v19;
typedef struct ct_runtime_io_seek_v19 { uint32_t Size; uintptr_t Handle; int64_t Offset; uint8_t Origin; int64_t Value; } ct_runtime_io_seek_v19;
typedef struct ct_runtime_io_value_v19 { uint32_t Size; uintptr_t Handle; int64_t Value; } ct_runtime_io_value_v19;
typedef struct ct_runtime_io_handle_v19 { uint32_t Size; uintptr_t Handle; } ct_runtime_io_handle_v19;
typedef struct ct_runtime_io_two_paths_v19 { uint32_t Size; const uint8_t *Source; size_t SourceLength; const uint8_t *Destination; size_t DestinationLength; bool Flag; } ct_runtime_io_two_paths_v19;
typedef struct ct_runtime_io_path_flag_v19 { uint32_t Size; const uint8_t *Path; size_t PathLength; bool Flag; } ct_runtime_io_path_flag_v19;
typedef struct ct_runtime_io_metadata_v19 {
    uint32_t Size; const uint8_t *Path; size_t PathLength;
    uint8_t Kind; uint32_t Attributes; int64_t Length;
    bool HasCreationTime; int64_t CreationSeconds; int32_t CreationNanoseconds;
    bool HasAccessTime; int64_t AccessSeconds; int32_t AccessNanoseconds;
    bool HasModificationTime; int64_t ModificationSeconds; int32_t ModificationNanoseconds;
} ct_runtime_io_metadata_v19;
typedef struct ct_runtime_io_directory_read_v19 { uint32_t Size; uintptr_t Handle; uint8_t *Name; size_t NameCapacity; size_t NameLength; uint8_t Kind; uint32_t Attributes; int64_t Length; } ct_runtime_io_directory_read_v19;

typedef struct ct_type_descriptor ct_type_descriptor;
typedef struct ct_process_context ct_process_context;
typedef struct ct_managed_module_descriptor_v1 ct_managed_module_descriptor_v1;

typedef struct ct_runtime_api_v19 {
    uint32_t Size;
    uint32_t AbiVersion;
    void *(*Allocate)(size_t size, const ct_managed_module_descriptor_v1 *module);
    void (*Free)(void *value);
    void (*FinalRelease)(void *value);
    void (*Raise)(void *exception);
    void (*RuntimeFault)(const char *code, const char *file, int32_t line);
    const ct_type_descriptor *(*RegisterType)(const void *descriptor);
    void (*UnregisterTypes)(const ct_managed_module_descriptor_v1 *module);
    ct_process_context *(*CurrentProcess)(void);
    void *(*CurrentModuleState)(const ct_managed_module_descriptor_v1 *module);
    void *(*CurrentThreadState)(void);
    void (*SetThreadState)(void *state);
    bool (*CancellationRequested)(void);
    void (*EnterCall)(const ct_managed_module_descriptor_v1 *module);
    void (*LeaveCall)(const ct_managed_module_descriptor_v1 *module);
    int32_t (*Service)(uint32_t service, void *payload, size_t size);
} ct_runtime_api_v19;

typedef struct ct_managed_dependency_v1 {
    const char *Name;
    const char *Version;
    const char *BuildIdentity;
    const char *ApiHash;
} ct_managed_dependency_v1;

struct ct_managed_module_descriptor_v1 {
    uint32_t Size;
    uint32_t RuntimeAbi;
    uint32_t ModuleAbi;
    uint32_t Kind;
    const char *Name;
    const char *Version;
    const char *BuildIdentity;
    const char *ApiHash;
    uint32_t DependencyCount;
    const ct_managed_dependency_v1 *Dependencies;
    size_t StaticStateSize;
    size_t StaticStateAlignment;
    uint32_t MainTaskStackBytes;
    uint64_t HeapLimitBytes;
    void (*Initialize)(void);
    void (*Finalize)(void);
    int32_t (*Main)(void *arguments);
    void *(*CreateArguments)(int32_t count, const char *const *values, const size_t *lengths);
    void *(*CreateBytes)(const uint8_t *data, size_t length);
};

typedef enum ct_managed_process_state {
    CT_PROCESS_STARTING,
    CT_PROCESS_RUNNING,
    CT_PROCESS_CANCELLING,
    CT_PROCESS_EXITED,
    CT_PROCESS_FAILED,
    CT_PROCESS_TERMINATED,
} ct_managed_process_state;

typedef struct ct_managed_process_info {
    uint32_t Id;
    ct_managed_process_state State;
    int32_t ExitCode;
    size_t HeapBytes;
    size_t HeapLimit;
    uint32_t TaskCount;
    char ModuleName[CTILDE_MANAGED_MODULE_NAME_CAPACITY];
} ct_managed_process_info;

typedef struct ct_managed_module_info {
    char Name[CTILDE_MANAGED_MODULE_NAME_CAPACITY];
    char Version[CTILDE_MANAGED_MODULE_VERSION_CAPACITY];
    uint32_t LoadReferences;
    uint32_t ActiveCalls;
    uint32_t LiveAllocations;
    bool Stopping;
} ct_managed_module_info;

int ctilde_managed_runtime_initialize(void);
size_t ctilde_managed_processes(ct_managed_process_info *output, size_t capacity);
size_t ctilde_managed_modules(ct_managed_module_info *output, size_t capacity);
uint32_t ctilde_managed_process_for_task(uintptr_t task_handle);
int ctilde_managed_preflight(const char *path, char *error, size_t error_capacity);
const ct_runtime_api_v19 *ctilde_runtime_api_v19(void);

/* Managed standard-library entry points. The managed layouts are private to ABI 19. */

void ctilde_managed_storage_invalidate_prefix(const char *prefix, uint64_t generation);
uintptr_t ct_managed_process_start(const void *path, const void *arguments);
uintptr_t ct_managed_process_current(void);
uint32_t ct_managed_process_id(uintptr_t handle);
ct_managed_process_state ct_managed_process_get_state(uintptr_t handle);
bool ct_managed_process_has_exited(uintptr_t handle);
int32_t ct_managed_process_exit_code(uintptr_t handle);
void ct_managed_process_cancel(uintptr_t handle);
void ct_managed_process_terminate(uintptr_t handle, uint32_t grace_milliseconds);
int32_t ct_managed_process_wait(uintptr_t handle);
bool ct_managed_process_try_wait(uintptr_t handle, uint32_t timeout_milliseconds, int32_t *exit_code);
void ct_managed_process_send(uintptr_t handle, const void *payload);
void *ct_managed_process_receive(uintptr_t handle, const void *type_template);
bool ct_managed_process_try_receive(uintptr_t handle, uint32_t timeout_milliseconds, const void *type_template, void **payload);
bool ct_managed_process_cancellation_requested(void);

#ifdef __cplusplus
}
#endif
