#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CTILDE_RUNTIME_ABI_VERSION 18u
#define CTILDE_MANAGED_MODULE_ABI_VERSION 1u
#define CTILDE_MANAGED_MODULE_ROOT "/storage/modules"

#define CT_RUNTIME_SERVICE_THREAD_ATTACH 1u
#define CT_RUNTIME_SERVICE_THREAD_DETACH 2u
#define CT_RUNTIME_SERVICE_CONSOLE_WRITE 16u
#define CT_RUNTIME_SERVICE_CONSOLE_READ 17u
#define CT_RUNTIME_SERVICE_CONSOLE_FLUSH 18u

typedef struct ct_runtime_console_transfer_v18 {
    uint8_t *Data;
    size_t Length;
    size_t Count;
    bool Eof;
} ct_runtime_console_transfer_v18;

typedef struct ct_type_descriptor ct_type_descriptor;
typedef struct ct_process_context ct_process_context;
typedef struct ct_managed_module_descriptor_v1 ct_managed_module_descriptor_v1;

typedef struct ct_runtime_api_v18 {
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
} ct_runtime_api_v18;

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
    const char *ModuleName;
} ct_managed_process_info;

typedef struct ct_managed_module_info {
    const char *Name;
    const char *Version;
    uint32_t ProcessReferences;
    uint32_t ActiveCalls;
    uint32_t LiveAllocations;
    bool Stopping;
} ct_managed_module_info;

int ctilde_managed_runtime_initialize(void);
size_t ctilde_managed_processes(ct_managed_process_info *output, size_t capacity);
size_t ctilde_managed_modules(ct_managed_module_info *output, size_t capacity);
int ctilde_managed_preflight(const char *path, char *error, size_t error_capacity);
const ct_runtime_api_v18 *ctilde_runtime_api_v18(void);

/* Managed standard-library entry points. The managed layouts are private to ABI 18. */
uintptr_t ct_managed_process_start(const void *path, const void *arguments);
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
