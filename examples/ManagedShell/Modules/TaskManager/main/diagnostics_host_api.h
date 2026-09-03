#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION 2u
#define CT_DIAGNOSTICS_MODULE_NAME_CAPACITY 64u
#define CT_DIAGNOSTICS_MODULE_VERSION_CAPACITY 32u
#define CT_DIAGNOSTICS_TASK_NAME_CAPACITY 32u

typedef enum ct_diagnostics_process_state {
    CT_DIAGNOSTICS_PROCESS_STARTING,
    CT_DIAGNOSTICS_PROCESS_RUNNING,
    CT_DIAGNOSTICS_PROCESS_CANCELLING,
    CT_DIAGNOSTICS_PROCESS_EXITED,
    CT_DIAGNOSTICS_PROCESS_FAILED,
    CT_DIAGNOSTICS_PROCESS_TERMINATED,
} ct_diagnostics_process_state;

typedef struct ct_diagnostics_process_info {
    uint32_t Id;
    ct_diagnostics_process_state State;
    int32_t ExitCode;
    size_t HeapBytes;
    size_t HeapLimit;
    uint32_t TaskCount;
    char ModuleName[CT_DIAGNOSTICS_MODULE_NAME_CAPACITY];
} ct_diagnostics_process_info;

typedef struct ct_diagnostics_module_info {
    char Name[CT_DIAGNOSTICS_MODULE_NAME_CAPACITY];
    char Version[CT_DIAGNOSTICS_MODULE_VERSION_CAPACITY];
    uint32_t LoadReferences;
    uint32_t ActiveCalls;
    uint32_t LiveAllocations;
    bool Stopping;
} ct_diagnostics_module_info;

typedef enum ct_diagnostics_heap_kind {
    CT_DIAGNOSTICS_HEAP_DEFAULT,
    CT_DIAGNOSTICS_HEAP_8BIT,
    CT_DIAGNOSTICS_HEAP_32BIT,
    CT_DIAGNOSTICS_HEAP_INTERNAL,
    CT_DIAGNOSTICS_HEAP_DMA,
    CT_DIAGNOSTICS_HEAP_EXECUTABLE,
    CT_DIAGNOSTICS_HEAP_SPIRAM,
} ct_diagnostics_heap_kind;

typedef struct ct_diagnostics_heap_info {
    size_t TotalFreeBytes;
    size_t TotalAllocatedBytes;
    size_t LargestFreeBlock;
    size_t MinimumFreeBytes;
    size_t AllocatedBlocks;
    size_t FreeBlocks;
    size_t TotalBlocks;
} ct_diagnostics_heap_info;

typedef enum ct_diagnostics_task_state {
    CT_DIAGNOSTICS_TASK_RUNNING,
    CT_DIAGNOSTICS_TASK_READY,
    CT_DIAGNOSTICS_TASK_BLOCKED,
    CT_DIAGNOSTICS_TASK_SUSPENDED,
    CT_DIAGNOSTICS_TASK_DELETED,
    CT_DIAGNOSTICS_TASK_INVALID,
} ct_diagnostics_task_state;

typedef struct ct_diagnostics_task_info {
    uintptr_t Handle;
    uint32_t Number;
    uint32_t Priority;
    ct_diagnostics_task_state State;
    int32_t Core;
    uint64_t RunTime;
    size_t StackMinimumBytes;
    char Name[CT_DIAGNOSTICS_TASK_NAME_CAPACITY];
} ct_diagnostics_task_info;

typedef struct ct_managed_diagnostics_host_api_v1 {
    uint32_t Size;
    uint32_t Version;
    uint32_t CoreCount;
    int32_t NoAffinity;
    size_t (*Processes)(ct_diagnostics_process_info *output, size_t capacity);
    size_t (*Modules)(ct_diagnostics_module_info *output, size_t capacity);
    uint32_t (*ProcessForTask)(uintptr_t task_handle);
    bool (*ProcessHasExited)(uintptr_t handle);
    void (*ProcessTerminate)(uintptr_t handle, uint32_t grace_milliseconds);
    void (*HeapGetInfo)(ct_diagnostics_heap_info *info, ct_diagnostics_heap_kind kind);
    size_t (*HeapGetTotalSize)(ct_diagnostics_heap_kind kind);
    bool (*HeapCheckIntegrityAll)(bool print_errors);
    uint32_t (*TaskGetCount)(void);
    uint32_t (*Tasks)(ct_diagnostics_task_info *tasks, uint32_t capacity, uint64_t *total_run_time);
    uintptr_t (*TaskGetIdleHandleForCore)(int32_t core);
    void (*TaskDelayMilliseconds)(uint32_t milliseconds);
    int32_t (*LittleFsInfo)(const char *partition_label, size_t *total_bytes, size_t *used_bytes);
    const char *(*ErrorName)(int32_t code);
} ct_managed_diagnostics_host_api_v1;

const ct_managed_diagnostics_host_api_v1 *ct_managed_diagnostics_host_v1(void);
