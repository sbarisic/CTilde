#include "managed_shell_host.h"
#include "managed_shell_host_api.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "ctilde_managed_runtime.h"
#include "esp_elf.h"
#include "esp_heap_caps.h"
#include "esp_system.h"

extern void ct_managed_shell_led_prompt_started(void);
extern void ct_managed_shell_led_process_starting(void);
extern void ct_managed_shell_led_process_started(uint32_t process_id);
extern void ct_managed_shell_led_process_start_failed(void);

static size_t process_snapshot(ct_managed_shell_process_info *output, size_t capacity)
{
    _Static_assert(sizeof(ct_managed_shell_process_info) == sizeof(ct_managed_process_info),
        "shell process snapshots must match the runtime record");
    return ctilde_managed_processes((ct_managed_process_info *)(void *)output, capacity);
}

static size_t module_snapshot(ct_managed_shell_module_info *output, size_t capacity)
{
    _Static_assert(sizeof(ct_managed_shell_module_info) == sizeof(ct_managed_module_info),
        "shell module snapshots must match the runtime record");
    return ctilde_managed_modules((ct_managed_module_info *)(void *)output, capacity);
}

static size_t free_heap(void) { return (size_t)esp_get_free_heap_size(); }
static size_t minimum_free_heap(void) { return (size_t)esp_get_minimum_free_heap_size(); }

static const ct_managed_shell_host_api_v1 s_api = {
    .Size = sizeof(s_api),
    .Version = CT_MANAGED_SHELL_HOST_API_VERSION,
    .Processes = process_snapshot,
    .Modules = module_snapshot,
    .FreeHeap = free_heap,
    .MinimumFreeHeap = minimum_free_heap,
    .SetForeground = ctilde_managed_process_set_foreground,
    .TerminateDescendants = ctilde_managed_process_terminate_descendants,
    .PromptStarted = ct_managed_shell_led_prompt_started,
    .ProcessStarting = ct_managed_shell_led_process_starting,
    .ProcessStarted = ct_managed_shell_led_process_started,
    .ProcessStartFailed = ct_managed_shell_led_process_start_failed,
};

const ct_managed_shell_host_api_v1 *ct_managed_shell_host_v1(void) { return &s_api; }

static const struct esp_elfsym s_symbols[] = {
    ESP_ELFSYM_EXPORT(ct_managed_shell_host_v1),
    ESP_ELFSYM_END,
};

int ct_managed_shell_host_initialize(void)
{
    return esp_elf_register_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols);
}
