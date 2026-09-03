#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION 1u
#define CT_DIAGNOSTICS_MODULE_NAME_CAPACITY 64u
#define CT_DIAGNOSTICS_MODULE_VERSION_CAPACITY 32u

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

typedef struct ct_managed_diagnostics_host_api_v1 {
    uint32_t Size;
    uint32_t Version;
    uint32_t CoreCount;
    BaseType_t NoAffinity;
    size_t (*Processes)(ct_diagnostics_process_info *output, size_t capacity);
    size_t (*Modules)(ct_diagnostics_module_info *output, size_t capacity);
    uint32_t (*ProcessForTask)(uintptr_t task_handle);
    bool (*ProcessHasExited)(uintptr_t handle);
    void (*ProcessTerminate)(uintptr_t handle, uint32_t grace_milliseconds);
    void (*HeapGetInfo)(multi_heap_info_t *info, uint32_t capabilities);
    size_t (*HeapGetTotalSize)(uint32_t capabilities);
    bool (*HeapCheckIntegrityAll)(bool print_errors);
    UBaseType_t (*TaskGetCount)(void);
    UBaseType_t (*TaskGetSystemState)(
        TaskStatus_t *tasks,
        UBaseType_t capacity,
        configRUN_TIME_COUNTER_TYPE *total_run_time);
    BaseType_t (*TaskGetCoreId)(TaskHandle_t task);
    TaskHandle_t (*TaskGetIdleHandleForCore)(BaseType_t core);
    void (*TaskDelay)(TickType_t ticks);
    esp_err_t (*LittleFsInfo)(const char *partition_label, size_t *total_bytes, size_t *used_bytes);
    const char *(*ErrorName)(esp_err_t code);
} ct_managed_diagnostics_host_api_v1;

const ct_managed_diagnostics_host_api_v1 *ct_managed_diagnostics_host_v1(void);

