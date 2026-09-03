#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "diagnostics_host_api.h"

#define CT_TASK_SNAPSHOT_ATTEMPTS 4u
#define CT_TASK_SNAPSHOT_SLACK 4u

typedef struct ct_memory_task_info {
    uintptr_t Handle;
    UBaseType_t Number;
    UBaseType_t Priority;
    eTaskState State;
    BaseType_t Core;
    size_t StackMinimumBytes;
    char Name[configMAX_TASK_NAME_LEN];
} ct_memory_task_info;

typedef struct ct_memory_task_snapshot {
    ct_memory_task_info *Items;
    size_t Count;
} ct_memory_task_snapshot;

typedef struct ct_memory_pool_info {
    const char *Name;
    uint32_t Capabilities;
    size_t Total;
    multi_heap_info_t Heap;
} ct_memory_pool_info;

static const ct_managed_diagnostics_host_api_v1 *host_api(void)
{
    const ct_managed_diagnostics_host_api_v1 *api = ct_managed_diagnostics_host_v1();
    if (api == NULL || api->Version != CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION ||
        api->Size < sizeof(ct_managed_diagnostics_host_api_v1)) return NULL;
    return api;
}

static size_t subtract_saturated(size_t value, size_t amount)
{
    return value > amount ? value - amount : 0u;
}

static size_t add_saturated(size_t left, size_t right)
{
    return left > SIZE_MAX - right ? SIZE_MAX : left + right;
}

static unsigned percentage_size(size_t value, size_t total)
{
    if (total == 0u) return 0u;
    if (value >= total) return 100u;
    return (unsigned)((value * 100u) / total);
}

static const char *process_state_name(ct_diagnostics_process_state state)
{
    switch (state) {
        case CT_DIAGNOSTICS_PROCESS_STARTING: return "starting";
        case CT_DIAGNOSTICS_PROCESS_RUNNING: return "running";
        case CT_DIAGNOSTICS_PROCESS_CANCELLING: return "cancelling";
        case CT_DIAGNOSTICS_PROCESS_EXITED: return "exited";
        case CT_DIAGNOSTICS_PROCESS_FAILED: return "failed";
        case CT_DIAGNOSTICS_PROCESS_TERMINATED: return "terminated";
        default: return "unknown";
    }
}

static const char *task_state_name(eTaskState state)
{
    switch (state) {
        case eRunning: return "running";
        case eReady: return "ready";
        case eBlocked: return "blocked";
        case eSuspended: return "suspended";
        case eDeleted: return "deleted";
        case eInvalid: return "invalid";
        default: return "unknown";
    }
}

static bool task_after(const ct_memory_task_info *left, const ct_memory_task_info *right)
{
    return left->Number > right->Number ||
        (left->Number == right->Number && left->Handle > right->Handle);
}

static void sort_tasks(ct_memory_task_info *items, size_t count)
{
    for (size_t index = 1u; index < count; ++index) {
        const ct_memory_task_info value = items[index];
        size_t destination = index;
        while (destination > 0u && task_after(&items[destination - 1u], &value)) {
            items[destination] = items[destination - 1u];
            destination--;
        }
        items[destination] = value;
    }
}

static void release_task_snapshot(ct_memory_task_snapshot *snapshot)
{
    free(snapshot->Items);
    snapshot->Items = NULL;
    snapshot->Count = 0u;
}

static bool capture_task_snapshot(
    const ct_managed_diagnostics_host_api_v1 *api,
    ct_memory_task_snapshot *snapshot)
{
    (void)memset(snapshot, 0, sizeof(*snapshot));
    for (unsigned attempt = 0u; attempt < CT_TASK_SNAPSHOT_ATTEMPTS; ++attempt) {
        const UBaseType_t before = api->TaskGetCount();
        const size_t capacity = (size_t)before + CT_TASK_SNAPSHOT_SLACK;
        TaskStatus_t *raw = (TaskStatus_t *)calloc(capacity, sizeof(*raw));
        ct_memory_task_info *items = (ct_memory_task_info *)calloc(capacity, sizeof(*items));
        if (raw == NULL || items == NULL) {
            free(raw);
            free(items);
            return false;
        }
        configRUN_TIME_COUNTER_TYPE total_run_time = 0;
        const UBaseType_t captured = api->TaskGetSystemState(raw, (UBaseType_t)capacity, &total_run_time);
        const UBaseType_t after = api->TaskGetCount();
        if (captured == 0u || captured != after) {
            free(raw);
            free(items);
            continue;
        }
        for (UBaseType_t index = 0u; index < captured; ++index) {
            ct_memory_task_info *item = &items[index];
            item->Handle = (uintptr_t)raw[index].xHandle;
            item->Number = raw[index].xTaskNumber;
            item->Priority = raw[index].uxCurrentPriority;
            item->State = raw[index].eCurrentState;
            item->Core = api->TaskGetCoreId(raw[index].xHandle);
            item->StackMinimumBytes = (size_t)raw[index].usStackHighWaterMark * sizeof(StackType_t);
            (void)snprintf(item->Name, sizeof(item->Name), "%s",
                raw[index].pcTaskName == NULL ? "?" : raw[index].pcTaskName);
        }
        free(raw);
        sort_tasks(items, captured);
        snapshot->Items = items;
        snapshot->Count = captured;
        return true;
    }
    return false;
}

static bool capture_processes(
    const ct_managed_diagnostics_host_api_v1 *api,
    ct_diagnostics_process_info **output,
    size_t *count)
{
    *output = NULL;
    *count = 0u;
    for (unsigned attempt = 0u; attempt < CT_TASK_SNAPSHOT_ATTEMPTS; ++attempt) {
        const size_t capacity = api->Processes(NULL, 0u);
        if (capacity == 0u) return true;
        ct_diagnostics_process_info *items =
            (ct_diagnostics_process_info *)calloc(capacity, sizeof(*items));
        if (items == NULL) return false;
        const size_t captured = api->Processes(items, capacity);
        if (captured <= capacity) {
            *output = items;
            *count = captured;
            return true;
        }
        free(items);
    }
    return false;
}

static bool capture_modules(
    const ct_managed_diagnostics_host_api_v1 *api,
    ct_diagnostics_module_info **output,
    size_t *count)
{
    *output = NULL;
    *count = 0u;
    for (unsigned attempt = 0u; attempt < CT_TASK_SNAPSHOT_ATTEMPTS; ++attempt) {
        const size_t capacity = api->Modules(NULL, 0u);
        if (capacity == 0u) return true;
        ct_diagnostics_module_info *items =
            (ct_diagnostics_module_info *)calloc(capacity, sizeof(*items));
        if (items == NULL) return false;
        const size_t captured = api->Modules(items, capacity);
        if (captured <= capacity) {
            *output = items;
            *count = captured;
            return true;
        }
        free(items);
    }
    return false;
}

static void print_pool(const ct_memory_pool_info *pool)
{
    if (pool->Capabilities == MALLOC_CAP_SPIRAM && pool->Total == 0u) {
        printf("  %s: not configured\n", pool->Name);
        return;
    }
    const size_t used = subtract_saturated(pool->Total, pool->Heap.total_free_bytes);
    printf("  %s: total=%zu used=%zu available=%zu largest=%zu minimum-free=%zu\n",
        pool->Name, pool->Total, used, pool->Heap.total_free_bytes,
        pool->Heap.largest_free_block, pool->Heap.minimum_free_bytes);
}

void ct_managed_diagnostics_print_memory(void)
{
    const ct_managed_diagnostics_host_api_v1 *api = host_api();
    if (api == NULL) {
        puts("Memory diagnostics\n  error: diagnostics host API 1 is unavailable");
        return;
    }
    ct_memory_pool_info pools[] = {
        { "default", MALLOC_CAP_DEFAULT, 0u, { 0 } },
        { "8-bit", MALLOC_CAP_8BIT, 0u, { 0 } },
        { "32-bit", MALLOC_CAP_32BIT, 0u, { 0 } },
        { "internal", MALLOC_CAP_INTERNAL, 0u, { 0 } },
        { "DMA", MALLOC_CAP_DMA, 0u, { 0 } },
        { "executable", MALLOC_CAP_EXEC, 0u, { 0 } },
        { "SPIRAM", MALLOC_CAP_SPIRAM, 0u, { 0 } },
    };
    for (size_t index = 0u; index < sizeof(pools) / sizeof(pools[0]); ++index) {
        pools[index].Total = api->HeapGetTotalSize(pools[index].Capabilities);
        api->HeapGetInfo(&pools[index].Heap, pools[index].Capabilities);
    }
    const ct_memory_pool_info *primary = &pools[0];
    const size_t used = subtract_saturated(primary->Total, primary->Heap.total_free_bytes);
    const size_t overhead = subtract_saturated(used, primary->Heap.total_allocated_bytes);
    const size_t peak_used = subtract_saturated(primary->Total, primary->Heap.minimum_free_bytes);
    const size_t fragmented = subtract_saturated(primary->Heap.total_free_bytes,
        primary->Heap.largest_free_block);
    const unsigned fragmentation = percentage_size(fragmented, primary->Heap.total_free_bytes);

    ct_diagnostics_process_info *processes = NULL;
    ct_diagnostics_module_info *modules = NULL;
    size_t process_count = 0u;
    size_t module_count = 0u;
    const bool processes_ok = capture_processes(api, &processes, &process_count);
    const bool modules_ok = capture_modules(api, &modules, &module_count);
    ct_memory_task_snapshot tasks;
    const bool tasks_ok = capture_task_snapshot(api, &tasks);
    size_t filesystem_total = 0u;
    size_t filesystem_used = 0u;
    const esp_err_t filesystem_result =
        api->LittleFsInfo("storage", &filesystem_total, &filesystem_used);
    const bool heap_ok = api->HeapCheckIntegrityAll(false);

    printf("RAM summary\n");
    printf("  total=%zu used=%zu available=%zu used-percent=%u%%\n",
        primary->Total, used, primary->Heap.total_free_bytes, percentage_size(used, primary->Total));
    printf("  allocated-payload=%zu allocator-overhead=%zu\n",
        primary->Heap.total_allocated_bytes, overhead);
    printf("  minimum-free=%zu peak-used=%zu largest-free-block=%zu fragmentation=%u%%\n",
        primary->Heap.minimum_free_bytes, peak_used, primary->Heap.largest_free_block, fragmentation);
    printf("  allocated-blocks=%zu free-blocks=%zu total-blocks=%zu\n",
        primary->Heap.allocated_blocks, primary->Heap.free_blocks, primary->Heap.total_blocks);

    printf("Capability pools (overlap; do not sum)\n");
    for (size_t index = 0u; index < sizeof(pools) / sizeof(pools[0]); ++index) print_pool(&pools[index]);

    printf("Managed processes\n");
    if (!processes_ok) {
        printf("  error: unable to allocate process snapshot\n");
    } else {
        size_t attributed_payload = 0u;
        for (size_t index = 0u; index < process_count; ++index)
            attributed_payload = add_saturated(attributed_payload, processes[index].HeapBytes);
        printf("  count=%zu attributed-payload=%zu\n", process_count, attributed_payload);
        for (size_t index = 0u; index < process_count; ++index) {
            const ct_diagnostics_process_info *process = &processes[index];
            if (process->HeapLimit == 0u) {
                printf("  pid=%" PRIu32 " state=%s module=%s heap=%zu limit=unlimited tasks=%" PRIu32 "\n",
                    process->Id, process_state_name(process->State), process->ModuleName,
                    process->HeapBytes, process->TaskCount);
            } else {
                printf("  pid=%" PRIu32 " state=%s module=%s heap=%zu limit=%zu tasks=%" PRIu32 "\n",
                    process->Id, process_state_name(process->State), process->ModuleName,
                    process->HeapBytes, process->HeapLimit, process->TaskCount);
            }
        }
    }

    printf("Managed modules\n");
    if (!modules_ok) {
        printf("  error: unable to allocate module snapshot\n");
    } else {
        printf("  count=%zu\n", module_count);
        for (size_t index = 0u; index < module_count; ++index) {
            const ct_diagnostics_module_info *module = &modules[index];
            printf("  module=%s version=%s load-refs=%" PRIu32 " active-calls=%" PRIu32
                " live-allocations=%" PRIu32 " stopping=%s\n", module->Name, module->Version,
                module->LoadReferences, module->ActiveCalls, module->LiveAllocations,
                module->Stopping ? "yes" : "no");
        }
    }

    printf("FreeRTOS tasks\n");
    if (!tasks_ok) {
        printf("  error: unable to capture stable task snapshot\n");
    } else {
        printf("  count=%zu\n", tasks.Count);
        for (size_t index = 0u; index < tasks.Count; ++index) {
            const ct_memory_task_info *task = &tasks.Items[index];
            if (task->Core == api->NoAffinity) {
                printf("  task=%" PRIu32 " name=%s state=%s priority=%" PRIu32
                    " affinity=any stack-min=%zu\n", (uint32_t)task->Number, task->Name,
                    task_state_name(task->State), (uint32_t)task->Priority, task->StackMinimumBytes);
            } else {
                printf("  task=%" PRIu32 " name=%s state=%s priority=%" PRIu32
                    " affinity=%" PRId32 " stack-min=%zu\n", (uint32_t)task->Number, task->Name,
                    task_state_name(task->State), (uint32_t)task->Priority,
                    (int32_t)task->Core, task->StackMinimumBytes);
            }
        }
    }

    printf("LittleFS\n");
    if (filesystem_result != ESP_OK) {
        printf("  error: %s\n", api->ErrorName(filesystem_result));
    } else {
        const size_t available = subtract_saturated(filesystem_total, filesystem_used);
        printf("  total=%zu used=%zu available=%zu used-percent=%u%%\n",
            filesystem_total, filesystem_used, available,
            percentage_size(filesystem_used, filesystem_total));
    }
    printf("Heap integrity\n  %s\n", heap_ok ? "ok" : "corrupt");

    free(processes);
    free(modules);
    if (tasks_ok) release_task_snapshot(&tasks);
}
