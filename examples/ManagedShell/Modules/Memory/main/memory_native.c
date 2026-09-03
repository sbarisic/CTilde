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
    uint32_t Number;
    uint32_t Priority;
    ct_diagnostics_task_state State;
    int32_t Core;
    size_t StackMinimumBytes;
    char Name[CT_DIAGNOSTICS_TASK_NAME_CAPACITY];
} ct_memory_task_info;

typedef struct ct_memory_task_snapshot {
    ct_memory_task_info *Items;
    size_t Count;
} ct_memory_task_snapshot;

typedef struct ct_memory_pool_info {
    const char *Name;
    ct_diagnostics_heap_kind Kind;
    size_t Total;
    ct_diagnostics_heap_info Heap;
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

static const char *task_state_name(ct_diagnostics_task_state state)
{
    switch (state) {
        case CT_DIAGNOSTICS_TASK_RUNNING: return "running";
        case CT_DIAGNOSTICS_TASK_READY: return "ready";
        case CT_DIAGNOSTICS_TASK_BLOCKED: return "blocked";
        case CT_DIAGNOSTICS_TASK_SUSPENDED: return "suspended";
        case CT_DIAGNOSTICS_TASK_DELETED: return "deleted";
        case CT_DIAGNOSTICS_TASK_INVALID: return "invalid";
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
        const uint32_t before = api->TaskGetCount();
        const size_t capacity = (size_t)before + CT_TASK_SNAPSHOT_SLACK;
        ct_diagnostics_task_info *raw =
            (ct_diagnostics_task_info *)calloc(capacity, sizeof(*raw));
        ct_memory_task_info *items = (ct_memory_task_info *)calloc(capacity, sizeof(*items));
        if (raw == NULL || items == NULL) {
            free(raw);
            free(items);
            return false;
        }
        uint64_t total_run_time = 0;
        const uint32_t captured = api->Tasks(raw, (uint32_t)capacity, &total_run_time);
        const uint32_t after = api->TaskGetCount();
        if (captured == 0u || captured != after) {
            free(raw);
            free(items);
            continue;
        }
        for (uint32_t index = 0u; index < captured; ++index) {
            ct_memory_task_info *item = &items[index];
            item->Handle = raw[index].Handle;
            item->Number = raw[index].Number;
            item->Priority = raw[index].Priority;
            item->State = raw[index].State;
            item->Core = raw[index].Core;
            item->StackMinimumBytes = raw[index].StackMinimumBytes;
            (void)snprintf(item->Name, sizeof(item->Name), "%s", raw[index].Name);
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
    if (pool->Kind == CT_DIAGNOSTICS_HEAP_SPIRAM && pool->Total == 0u) {
        printf("  %s: not configured\n", pool->Name);
        return;
    }
    const size_t used = subtract_saturated(pool->Total, pool->Heap.TotalFreeBytes);
    printf("  %s: total=%zu used=%zu available=%zu largest=%zu minimum-free=%zu\n",
        pool->Name, pool->Total, used, pool->Heap.TotalFreeBytes,
        pool->Heap.LargestFreeBlock, pool->Heap.MinimumFreeBytes);
}

void ct_managed_diagnostics_print_memory(void)
{
    const ct_managed_diagnostics_host_api_v1 *api = host_api();
    if (api == NULL) {
        puts("Memory diagnostics\n  error: diagnostics host API 1 is unavailable");
        return;
    }
    ct_memory_pool_info pools[] = {
        { "default", CT_DIAGNOSTICS_HEAP_DEFAULT, 0u, { 0 } },
        { "8-bit", CT_DIAGNOSTICS_HEAP_8BIT, 0u, { 0 } },
        { "32-bit", CT_DIAGNOSTICS_HEAP_32BIT, 0u, { 0 } },
        { "internal", CT_DIAGNOSTICS_HEAP_INTERNAL, 0u, { 0 } },
        { "DMA", CT_DIAGNOSTICS_HEAP_DMA, 0u, { 0 } },
        { "executable", CT_DIAGNOSTICS_HEAP_EXECUTABLE, 0u, { 0 } },
        { "SPIRAM", CT_DIAGNOSTICS_HEAP_SPIRAM, 0u, { 0 } },
    };
    for (size_t index = 0u; index < sizeof(pools) / sizeof(pools[0]); ++index) {
        pools[index].Total = api->HeapGetTotalSize(pools[index].Kind);
        api->HeapGetInfo(&pools[index].Heap, pools[index].Kind);
    }
    const ct_memory_pool_info *primary = &pools[0];
    const size_t used = subtract_saturated(primary->Total, primary->Heap.TotalFreeBytes);
    const size_t overhead = subtract_saturated(used, primary->Heap.TotalAllocatedBytes);
    const size_t peak_used = subtract_saturated(primary->Total, primary->Heap.MinimumFreeBytes);
    const size_t fragmented = subtract_saturated(primary->Heap.TotalFreeBytes,
        primary->Heap.LargestFreeBlock);
    const unsigned fragmentation = percentage_size(fragmented, primary->Heap.TotalFreeBytes);

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
    const int32_t filesystem_result =
        api->LittleFsInfo("storage", &filesystem_total, &filesystem_used);
    const bool heap_ok = api->HeapCheckIntegrityAll(false);

    printf("RAM summary\n");
    printf("  total=%zu used=%zu available=%zu used-percent=%u%%\n",
        primary->Total, used, primary->Heap.TotalFreeBytes, percentage_size(used, primary->Total));
    printf("  allocated-payload=%zu allocator-overhead=%zu\n",
        primary->Heap.TotalAllocatedBytes, overhead);
    printf("  minimum-free=%zu peak-used=%zu largest-free-block=%zu fragmentation=%u%%\n",
        primary->Heap.MinimumFreeBytes, peak_used, primary->Heap.LargestFreeBlock, fragmentation);
    printf("  allocated-blocks=%zu free-blocks=%zu total-blocks=%zu\n",
        primary->Heap.AllocatedBlocks, primary->Heap.FreeBlocks, primary->Heap.TotalBlocks);

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
    if (filesystem_result != 0) {
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
