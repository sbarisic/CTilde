#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define CT_MANAGED_SHELL_HOST_API_VERSION 1u
#define CT_MANAGED_SHELL_NAME_CAPACITY 64u
#define CT_MANAGED_SHELL_VERSION_CAPACITY 32u

typedef struct ct_managed_shell_process_info {
    uint32_t Id;
    uint32_t State;
    int32_t ExitCode;
    size_t HeapBytes;
    size_t HeapLimit;
    uint32_t TaskCount;
    char ModuleName[CT_MANAGED_SHELL_NAME_CAPACITY];
} ct_managed_shell_process_info;

typedef struct ct_managed_shell_module_info {
    char Name[CT_MANAGED_SHELL_NAME_CAPACITY];
    char Version[CT_MANAGED_SHELL_VERSION_CAPACITY];
    uint32_t LoadReferences;
    uint32_t ActiveCalls;
    uint32_t LiveAllocations;
    bool Stopping;
} ct_managed_shell_module_info;

typedef struct ct_managed_shell_host_api_v1 {
    uint32_t Size;
    uint32_t Version;
    size_t (*Processes)(ct_managed_shell_process_info *output, size_t capacity);
    size_t (*Modules)(ct_managed_shell_module_info *output, size_t capacity);
    size_t (*FreeHeap)(void);
    size_t (*MinimumFreeHeap)(void);
    bool (*SetForeground)(uint32_t process_id);
    void (*TerminateDescendants)(uint32_t process_id, uint32_t grace_milliseconds);
    void (*PromptStarted)(void);
    void (*ProcessStarting)(void);
    void (*ProcessStarted)(uint32_t process_id);
    void (*ProcessStartFailed)(void);
} ct_managed_shell_host_api_v1;

const ct_managed_shell_host_api_v1 *ct_managed_shell_host_v1(void);
