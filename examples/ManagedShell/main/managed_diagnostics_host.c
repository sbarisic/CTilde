#include "managed_diagnostics_host_api.h"

#include "ctilde_managed_runtime.h"
#include "esp_elf.h"
#include "esp_littlefs.h"

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
    .HeapGetInfo = heap_caps_get_info,
    .HeapGetTotalSize = heap_caps_get_total_size,
    .HeapCheckIntegrityAll = heap_caps_check_integrity_all,
    .TaskGetCount = uxTaskGetNumberOfTasks,
    .TaskGetSystemState = uxTaskGetSystemState,
    .TaskGetCoreId = xTaskGetCoreID,
    .TaskGetIdleHandleForCore = xTaskGetIdleTaskHandleForCore,
    .TaskDelay = vTaskDelay,
    .LittleFsInfo = esp_littlefs_info,
    .ErrorName = esp_err_to_name,
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
