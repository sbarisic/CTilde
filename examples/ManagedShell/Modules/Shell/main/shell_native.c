#include "managed_shell_host_api.h"
#include "diagnostics_host_api.h"

#include <errno.h>
#include <stddef.h>
#include <stdint.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

/* Use the generated process console bridge, including redirected SSH pipes. */
extern void ct_runtime_console_write(const uint8_t *data, size_t length);
extern size_t ct_runtime_console_read(uint8_t *data, size_t length, bool *eof);

int32_t ct_shell_read_input(bool *eof)
{
    uint8_t value = 0u;
    const size_t count = ct_runtime_console_read(&value, 1u, eof);
    return count == 0u ? -1 : (int32_t)value;
}

static void memory_line(const char *format, ...)
{
    char line[160];
    va_list arguments;
    va_start(arguments, format);
    const int length = vsnprintf(line, sizeof(line), format, arguments);
    va_end(arguments);
    if (length > 0) {
        const size_t count = (size_t)length < sizeof(line) ? (size_t)length : sizeof(line) - 1u;
        ct_runtime_console_write((const uint8_t *)line, count);
    }
}

static const ct_managed_shell_host_api_v1 *host(void)
{
    const ct_managed_shell_host_api_v1 *api = ct_managed_shell_host_v1();
    return api != NULL && api->Version == CT_MANAGED_SHELL_HOST_API_VERSION &&
        api->Size >= sizeof(*api) ? api : NULL;
}

uint32_t ct_shell_process_count(void)
{
    const ct_managed_shell_host_api_v1 *api = host();
    return api == NULL ? 0u : (uint32_t)api->Processes(NULL, 0u);
}

int32_t ct_shell_process_get(uint32_t index, uint32_t *id, uint32_t *state,
    int32_t *exit_code, size_t *heap_bytes, size_t *heap_limit, uint32_t *tasks,
    uint8_t *name, size_t name_capacity)
{
    const ct_managed_shell_host_api_v1 *api = host();
    ct_managed_shell_process_info values[16];
    if (api == NULL || id == NULL || state == NULL || exit_code == NULL ||
        heap_bytes == NULL || heap_limit == NULL || tasks == NULL || name == NULL ||
        name_capacity == 0u) return -EINVAL;
    const size_t count = api->Processes(values, 16u);
    if (index >= count || index >= 16u) return -ENOENT;
    const ct_managed_shell_process_info *value = &values[index];
    *id = value->Id; *state = value->State; *exit_code = value->ExitCode;
    *heap_bytes = value->HeapBytes; *heap_limit = value->HeapLimit; *tasks = value->TaskCount;
    const size_t length = strnlen(value->ModuleName, sizeof(value->ModuleName));
    if (length + 1u > name_capacity) return -ENOBUFS;
    (void)memcpy(name, value->ModuleName, length); name[length] = 0u;
    return 0;
}

uint32_t ct_shell_module_count(void)
{
    const ct_managed_shell_host_api_v1 *api = host();
    return api == NULL ? 0u : (uint32_t)api->Modules(NULL, 0u);
}

int32_t ct_shell_module_get(uint32_t index, uint32_t *references, uint32_t *calls,
    uint32_t *allocations, bool *stopping, uint8_t *name, size_t name_capacity,
    uint8_t *version, size_t version_capacity)
{
    const ct_managed_shell_host_api_v1 *api = host();
    ct_managed_shell_module_info values[16];
    if (api == NULL || references == NULL || calls == NULL || allocations == NULL ||
        stopping == NULL || name == NULL || version == NULL || name_capacity == 0u ||
        version_capacity == 0u) return -EINVAL;
    const size_t count = api->Modules(values, 16u);
    if (index >= count || index >= 16u) return -ENOENT;
    const ct_managed_shell_module_info *value = &values[index];
    *references = value->LoadReferences; *calls = value->ActiveCalls;
    *allocations = value->LiveAllocations; *stopping = value->Stopping;
    const size_t name_length = strnlen(value->Name, sizeof(value->Name));
    const size_t version_length = strnlen(value->Version, sizeof(value->Version));
    if (name_length + 1u > name_capacity || version_length + 1u > version_capacity) return -ENOBUFS;
    (void)memcpy(name, value->Name, name_length); name[name_length] = 0u;
    (void)memcpy(version, value->Version, version_length); version[version_length] = 0u;
    return 0;
}

void ct_shell_print_memory(void)
{
    const ct_managed_diagnostics_host_api_v1 *api = ct_managed_diagnostics_host_v1();
    if (api == NULL || api->Version != CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION ||
        api->Size < sizeof(*api)) {
        memory_line("free: memory diagnostics unavailable\n");
        return;
    }
    static const struct {
        const char *Name;
        ct_diagnostics_heap_kind Kind;
    } pools[] = {
        { "default", CT_DIAGNOSTICS_HEAP_DEFAULT },
        { "8-bit", CT_DIAGNOSTICS_HEAP_8BIT },
        { "32-bit", CT_DIAGNOSTICS_HEAP_32BIT },
        { "internal", CT_DIAGNOSTICS_HEAP_INTERNAL },
        { "DMA", CT_DIAGNOSTICS_HEAP_DMA },
        { "executable", CT_DIAGNOSTICS_HEAP_EXECUTABLE },
        { "SPIRAM", CT_DIAGNOSTICS_HEAP_SPIRAM },
    };
    ct_diagnostics_heap_info info[sizeof(pools) / sizeof(pools[0])] = { 0 };
    size_t totals[sizeof(pools) / sizeof(pools[0])];
    for (size_t index = 0u; index < sizeof(pools) / sizeof(pools[0]); ++index) {
        totals[index] = api->HeapGetTotalSize(pools[index].Kind);
        api->HeapGetInfo(&info[index], pools[index].Kind);
    }
    memory_line("free heap: %zu, minimum: %zu\n",
        info[0].TotalFreeBytes, info[0].MinimumFreeBytes);
    memory_line("RAM bytes (capability pools overlap; do not sum)\n");
    memory_line("%-12s %10s %10s %10s %7s\n", "Pool", "Free", "Used", "Total", "Free %");
    for (size_t index = 0u; index < sizeof(pools) / sizeof(pools[0]); ++index) {
        if (totals[index] == 0u) {
            memory_line("%-12s %s\n", pools[index].Name, "not configured");
        } else {
            const size_t free_bytes = info[index].TotalFreeBytes;
            const size_t used = totals[index] > free_bytes ? totals[index] - free_bytes : 0u;
            const unsigned tenths = (unsigned)((uint64_t)free_bytes * 1000u / totals[index]);
            memory_line("%-12s %10zu %10zu %10zu %5u.%u%%\n", pools[index].Name,
                free_bytes, used, totals[index], tenths / 10u, tenths % 10u);
        }
    }
}
bool ct_shell_set_foreground(uint32_t id) { const ct_managed_shell_host_api_v1 *api = host(); return api != NULL && api->SetForeground(id); }
void ct_shell_terminate_descendants(uint32_t id, uint32_t grace) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->TerminateDescendants(id, grace); }
void ct_shell_prompt_started(void) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->PromptStarted(); }
void ct_shell_process_starting(void) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->ProcessStarting(); }
void ct_shell_process_started(uint32_t id) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->ProcessStarted(id); }
void ct_shell_process_start_failed(void) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->ProcessStartFailed(); }
