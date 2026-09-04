#include "managed_shell_host_api.h"

#include <errno.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

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

size_t ct_shell_free_heap(void) { const ct_managed_shell_host_api_v1 *api = host(); return api == NULL ? 0u : api->FreeHeap(); }
size_t ct_shell_minimum_free_heap(void) { const ct_managed_shell_host_api_v1 *api = host(); return api == NULL ? 0u : api->MinimumFreeHeap(); }
bool ct_shell_set_foreground(uint32_t id) { const ct_managed_shell_host_api_v1 *api = host(); return api != NULL && api->SetForeground(id); }
void ct_shell_terminate_descendants(uint32_t id, uint32_t grace) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->TerminateDescendants(id, grace); }
void ct_shell_prompt_started(void) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->PromptStarted(); }
void ct_shell_process_starting(void) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->ProcessStarting(); }
void ct_shell_process_started(uint32_t id) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->ProcessStarted(id); }
void ct_shell_process_start_failed(void) { const ct_managed_shell_host_api_v1 *api = host(); if (api != NULL) api->ProcessStartFailed(); }
