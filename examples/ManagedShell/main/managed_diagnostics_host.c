#include "managed_diagnostics_host_api.h"

#include <stdlib.h>
#include <string.h>

#include "ctilde_managed_runtime.h"
#include "esp_elf.h"
#include "esp_err.h"
#include "esp_heap_caps.h"
#include "esp_littlefs.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

_Static_assert(sizeof(ct_diagnostics_process_info) == sizeof(ct_managed_process_info),
    "Diagnostics process snapshot layout must match the managed runtime");
_Static_assert(sizeof(ct_diagnostics_module_info) == sizeof(ct_managed_module_info),
    "Diagnostics module snapshot layout must match the managed runtime");

static size_t processes(ct_diagnostics_process_info *output, size_t capacity)
{
    return ctilde_managed_processes((ct_managed_process_info *)(void *)output, capacity);
}

static size_t modules(ct_diagnostics_module_info *output, size_t capacity)
{
    return ctilde_managed_modules((ct_managed_module_info *)(void *)output, capacity);
}

static uint32_t heap_capabilities(ct_diagnostics_heap_kind kind)
{
    switch (kind) {
        case CT_DIAGNOSTICS_HEAP_DEFAULT: return MALLOC_CAP_DEFAULT;
        case CT_DIAGNOSTICS_HEAP_8BIT: return MALLOC_CAP_8BIT;
        case CT_DIAGNOSTICS_HEAP_32BIT: return MALLOC_CAP_32BIT;
        case CT_DIAGNOSTICS_HEAP_INTERNAL: return MALLOC_CAP_INTERNAL;
        case CT_DIAGNOSTICS_HEAP_DMA: return MALLOC_CAP_DMA;
        case CT_DIAGNOSTICS_HEAP_EXECUTABLE: return MALLOC_CAP_EXEC;
        case CT_DIAGNOSTICS_HEAP_SPIRAM: return MALLOC_CAP_SPIRAM;
        default: return MALLOC_CAP_DEFAULT;
    }
}

static void heap_get_info(ct_diagnostics_heap_info *output, ct_diagnostics_heap_kind kind)
{
    multi_heap_info_t info = { 0 };
    heap_caps_get_info(&info, heap_capabilities(kind));
    output->TotalFreeBytes = info.total_free_bytes;
    output->TotalAllocatedBytes = info.total_allocated_bytes;
    output->LargestFreeBlock = info.largest_free_block;
    output->MinimumFreeBytes = info.minimum_free_bytes;
    output->AllocatedBlocks = info.allocated_blocks;
    output->FreeBlocks = info.free_blocks;
    output->TotalBlocks = info.total_blocks;
}

static size_t heap_get_total_size(ct_diagnostics_heap_kind kind)
{
    return heap_caps_get_total_size(heap_capabilities(kind));
}

static uint32_t task_get_count(void)
{
    return (uint32_t)uxTaskGetNumberOfTasks();
}

static ct_diagnostics_task_state task_state(eTaskState state)
{
    switch (state) {
        case eRunning: return CT_DIAGNOSTICS_TASK_RUNNING;
        case eReady: return CT_DIAGNOSTICS_TASK_READY;
        case eBlocked: return CT_DIAGNOSTICS_TASK_BLOCKED;
        case eSuspended: return CT_DIAGNOSTICS_TASK_SUSPENDED;
        case eDeleted: return CT_DIAGNOSTICS_TASK_DELETED;
        default: return CT_DIAGNOSTICS_TASK_INVALID;
    }
}

static uint32_t tasks(
    ct_diagnostics_task_info *output,
    uint32_t capacity,
    uint64_t *total_run_time)
{
    if (output == NULL || capacity == 0u) return 0u;
    TaskStatus_t *raw = (TaskStatus_t *)calloc((size_t)capacity, sizeof(*raw));
    if (raw == NULL) return 0u;
    configRUN_TIME_COUNTER_TYPE total = 0;
    const UBaseType_t captured = uxTaskGetSystemState(raw, (UBaseType_t)capacity, &total);
    for (UBaseType_t index = 0u; index < captured; ++index) {
        output[index].Handle = (uintptr_t)raw[index].xHandle;
        output[index].Number = (uint32_t)raw[index].xTaskNumber;
        output[index].Priority = (uint32_t)raw[index].uxCurrentPriority;
        output[index].State = task_state(raw[index].eCurrentState);
        output[index].Core = (int32_t)xTaskGetCoreID(raw[index].xHandle);
        output[index].RunTime = (uint64_t)raw[index].ulRunTimeCounter;
        output[index].StackMinimumBytes =
            (size_t)raw[index].usStackHighWaterMark * sizeof(StackType_t);
        const char *name = raw[index].pcTaskName == NULL ? "?" : raw[index].pcTaskName;
        (void)strncpy(output[index].Name, name, sizeof(output[index].Name) - 1u);
        output[index].Name[sizeof(output[index].Name) - 1u] = '\0';
    }
    free(raw);
    if (total_run_time != NULL) *total_run_time = (uint64_t)total;
    return (uint32_t)captured;
}

static uintptr_t task_get_idle_handle_for_core(int32_t core)
{
    return (uintptr_t)xTaskGetIdleTaskHandleForCore((BaseType_t)core);
}

static void task_delay_milliseconds(uint32_t milliseconds)
{
    vTaskDelay(pdMS_TO_TICKS(milliseconds));
}

static int32_t littlefs_info(const char *partition_label, size_t *total_bytes, size_t *used_bytes)
{
    return (int32_t)esp_littlefs_info(partition_label, total_bytes, used_bytes);
}

static const char *error_name(int32_t code)
{
    return esp_err_to_name((esp_err_t)code);
}

static const ct_managed_diagnostics_host_api_v1 s_host_api = {
    .Size = sizeof(ct_managed_diagnostics_host_api_v1),
    .Version = CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION,
    .CoreCount = configNUMBER_OF_CORES,
    .NoAffinity = tskNO_AFFINITY,
    .Processes = processes,
    .Modules = modules,
    .ProcessForTask = ctilde_managed_process_for_task,
    .ProcessHasExited = ct_managed_process_has_exited,
    .ProcessTerminate = ct_managed_process_terminate,
    .HeapGetInfo = heap_get_info,
    .HeapGetTotalSize = heap_get_total_size,
    .HeapCheckIntegrityAll = heap_caps_check_integrity_all,
    .TaskGetCount = task_get_count,
    .Tasks = tasks,
    .TaskGetIdleHandleForCore = task_get_idle_handle_for_core,
    .TaskDelayMilliseconds = task_delay_milliseconds,
    .LittleFsInfo = littlefs_info,
    .ErrorName = error_name,
};

const ct_managed_diagnostics_host_api_v1 *ct_managed_diagnostics_host_v1(void)
{
    return &s_host_api;
}

static const struct esp_elfsym s_host_symbols[] = {
    ESP_ELFSYM_EXPORT(ct_managed_diagnostics_host_v1),
    ESP_ELFSYM_END
};

int ct_managed_diagnostics_host_initialize(void)
{
    return esp_elf_register_symbol(
        (esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_host_symbols);
}
