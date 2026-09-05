#include <errno.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "diagnostics_host_api.h"

#define CT_TASK_SNAPSHOT_ATTEMPTS 4u
#define CT_TASK_SNAPSHOT_SLACK 4u
#define CT_TASKMGR_SAMPLE_MILLISECONDS 250u

typedef struct ct_taskmgr_task_info {
    uintptr_t Handle;
    uint32_t ProcessId;
    uint32_t Number;
    uint64_t RunTime;
    size_t StackMinimumBytes;
} ct_taskmgr_task_info;

typedef struct ct_taskmgr_task_snapshot {
    ct_taskmgr_task_info *Items;
    size_t Count;
    uint64_t TotalRunTime;
} ct_taskmgr_task_snapshot;

static const ct_managed_diagnostics_host_api_v1 *host_api(void)
{
    const ct_managed_diagnostics_host_api_v1 *api = ct_managed_diagnostics_host_v1();
    if (api == NULL || api->Version != CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION ||
        api->Size < sizeof(ct_managed_diagnostics_host_api_v1) || api->CoreCount == 0u) return NULL;
    return api;
}

static uint64_t subtract_u64_saturated(uint64_t value, uint64_t amount)
{
    return value > amount ? value - amount : 0u;
}

static uint64_t add_u64_saturated(uint64_t left, uint64_t right)
{
    return left > UINT64_MAX - right ? UINT64_MAX : left + right;
}

static uint64_t cpu_tenths(uint64_t run_time, uint64_t wall_time, uint64_t maximum)
{
    if (wall_time == 0u) return maximum;
    const uint64_t whole = run_time / wall_time;
    const uint64_t remainder = run_time % wall_time;
    if (whole >= maximum / 1000u + 1u) return maximum;
    const uint64_t value = whole * 1000u + (remainder * 1000u + wall_time / 2u) / wall_time;
    return value > maximum ? maximum : value;
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

static bool process_is_active(ct_diagnostics_process_state state)
{
    return state == CT_DIAGNOSTICS_PROCESS_STARTING ||
        state == CT_DIAGNOSTICS_PROCESS_RUNNING ||
        state == CT_DIAGNOSTICS_PROCESS_CANCELLING;
}

static bool task_after(const ct_taskmgr_task_info *left, const ct_taskmgr_task_info *right)
{
    return left->Number > right->Number ||
        (left->Number == right->Number && left->Handle > right->Handle);
}

static void sort_tasks(ct_taskmgr_task_info *items, size_t count)
{
    for (size_t index = 1u; index < count; ++index) {
        const ct_taskmgr_task_info value = items[index];
        size_t destination = index;
        while (destination > 0u && task_after(&items[destination - 1u], &value)) {
            items[destination] = items[destination - 1u];
            destination--;
        }
        items[destination] = value;
    }
}

static void release_task_snapshot(ct_taskmgr_task_snapshot *snapshot)
{
    free(snapshot->Items);
    snapshot->Items = NULL;
    snapshot->Count = 0u;
    snapshot->TotalRunTime = 0u;
}

static bool capture_task_snapshot(
    const ct_managed_diagnostics_host_api_v1 *api,
    ct_taskmgr_task_snapshot *snapshot)
{
    (void)memset(snapshot, 0, sizeof(*snapshot));
    for (unsigned attempt = 0u; attempt < CT_TASK_SNAPSHOT_ATTEMPTS; ++attempt) {
        const uint32_t before = api->TaskGetCount();
        const size_t capacity = (size_t)before + CT_TASK_SNAPSHOT_SLACK;
        ct_diagnostics_task_info *raw =
            (ct_diagnostics_task_info *)calloc(capacity, sizeof(*raw));
        ct_taskmgr_task_info *items = (ct_taskmgr_task_info *)calloc(capacity, sizeof(*items));
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
            ct_taskmgr_task_info *item = &items[index];
            item->Handle = raw[index].Handle;
            item->ProcessId = api->ProcessForTask(item->Handle);
            item->Number = raw[index].Number;
            item->RunTime = raw[index].RunTime;
            item->StackMinimumBytes = raw[index].StackMinimumBytes;
        }
        free(raw);
        sort_tasks(items, captured);
        snapshot->Items = items;
        snapshot->Count = captured;
        snapshot->TotalRunTime = (uint64_t)total_run_time;
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

static const ct_taskmgr_task_info *find_task(
    const ct_taskmgr_task_snapshot *snapshot,
    const ct_taskmgr_task_info *task)
{
    for (size_t index = 0u; index < snapshot->Count; ++index) {
        const ct_taskmgr_task_info *candidate = &snapshot->Items[index];
        if (candidate->Number == task->Number && candidate->Handle == task->Handle &&
            candidate->ProcessId == task->ProcessId) return candidate;
    }
    return NULL;
}

void ct_managed_diagnostics_print_task_manager(void)
{
    const ct_managed_diagnostics_host_api_v1 *api = host_api();
    if (api == NULL) {
        puts("Task manager\n  error: diagnostics host API 1 is unavailable");
        return;
    }
    ct_taskmgr_task_snapshot first;
    ct_taskmgr_task_snapshot second;
    if (!capture_task_snapshot(api, &first)) {
        puts("Task manager\n  error: unable to capture initial task snapshot");
        return;
    }
    api->TaskDelayMilliseconds(CT_TASKMGR_SAMPLE_MILLISECONDS);
    if (!capture_task_snapshot(api, &second)) {
        release_task_snapshot(&first);
        puts("Task manager\n  error: unable to capture final task snapshot");
        return;
    }
    ct_diagnostics_process_info *processes = NULL;
    size_t process_count = 0u;
    if (!capture_processes(api, &processes, &process_count)) {
        release_task_snapshot(&first);
        release_task_snapshot(&second);
        puts("Task manager\n  error: unable to allocate process snapshot");
        return;
    }

    const uint64_t wall_time = subtract_u64_saturated(second.TotalRunTime, first.TotalRunTime);
    const uint64_t maximum_cpu = (uint64_t)api->CoreCount * 1000u;
    uint64_t idle_run_time = 0u;
    unsigned stable_idle_tasks = 0u;
    for (int32_t core = 0; core < (int32_t)api->CoreCount; ++core) {
        const uintptr_t idle_handle = api->TaskGetIdleHandleForCore(core);
        for (size_t index = 0u; index < second.Count; ++index) {
            const ct_taskmgr_task_info *current = &second.Items[index];
            if (current->Handle != idle_handle) continue;
            const ct_taskmgr_task_info *prior = find_task(&first, current);
            if (prior != NULL) {
                idle_run_time = add_u64_saturated(idle_run_time,
                    subtract_u64_saturated(current->RunTime, prior->RunTime));
                stable_idle_tasks++;
            }
            break;
        }
    }
    const uint64_t capacity = wall_time > UINT64_MAX / (uint64_t)api->CoreCount
        ? UINT64_MAX : wall_time * (uint64_t)api->CoreCount;
    const uint64_t busy_run_time = subtract_u64_saturated(capacity, idle_run_time);
    const bool system_cpu_available = wall_time != 0u && stable_idle_tasks == api->CoreCount;
    const uint64_t system_cpu = cpu_tenths(busy_run_time, wall_time, maximum_cpu);
    size_t active_count = 0u;
    for (size_t index = 0u; index < process_count; ++index)
        if (process_is_active(processes[index].State)) active_count++;

    printf("Task manager\n");
    printf("  sample-ms=%u cpu-scale=per-core cores=%" PRIu32 " maximum=%" PRIu32 ".0%%\n",
        CT_TASKMGR_SAMPLE_MILLISECONDS, api->CoreCount, api->CoreCount * 100u);
    if (!system_cpu_available) {
        printf("  system-cpu=n/a freertos-tasks=%zu active-processes=%zu\n",
            second.Count, active_count);
    } else {
        printf("  system-cpu=%" PRIu64 ".%" PRIu64 "%% freertos-tasks=%zu active-processes=%zu\n",
            system_cpu / 10u, system_cpu % 10u, second.Count, active_count);
    }
    const size_t total_ram = api->HeapGetTotalSize(CT_DIAGNOSTICS_HEAP_8BIT);
    printf("  memory-basis=managed-payload/total-8bit-ram total-ram=%zu (excludes shared code and stacks)\n", total_ram);
    printf("  PID STATE MODULE THREADS HEAP LIMIT MEM%% CPU STACK-MIN\n");
    for (size_t process_index = 0u; process_index < process_count; ++process_index) {
        const ct_diagnostics_process_info *process = &processes[process_index];
        if (!process_is_active(process->State)) continue;
        size_t mapped_count = 0u;
        size_t stable_count = 0u;
        size_t stack_minimum = SIZE_MAX;
        uint64_t process_run_time = 0u;
        for (size_t task_index = 0u; task_index < second.Count; ++task_index) {
            const ct_taskmgr_task_info *current = &second.Items[task_index];
            if (current->ProcessId != process->Id) continue;
            mapped_count++;
            if (current->StackMinimumBytes < stack_minimum) stack_minimum = current->StackMinimumBytes;
            const ct_taskmgr_task_info *prior = find_task(&first, current);
            if (prior == NULL) continue;
            stable_count++;
            process_run_time = add_u64_saturated(process_run_time,
                subtract_u64_saturated(current->RunTime, prior->RunTime));
        }
        const bool complete = wall_time != 0u && mapped_count == process->TaskCount &&
            stable_count == mapped_count && mapped_count != 0u;
        printf("  pid=%" PRIu32 " state=%s module=%s threads=%" PRIu32 " heap=%zu ",
            process->Id, process_state_name(process->State), process->ModuleName,
            process->TaskCount, process->HeapBytes);
        if (process->HeapLimit == 0u) printf("limit=unlimited ");
        else printf("limit=%zu ", process->HeapLimit);
        if (total_ram == 0u) printf("mem=n/a ");
        else {
            const uint64_t memory = (uint64_t)process->HeapBytes * 1000u / total_ram;
            printf("mem=%" PRIu64 ".%" PRIu64 "%% ", memory / 10u, memory % 10u);
        }
        if (!complete) {
            printf("cpu=n/a stack-min=n/a\n");
        } else {
            const uint64_t process_maximum = (uint64_t)mapped_count * 1000u < maximum_cpu
                ? (uint64_t)mapped_count * 1000u : maximum_cpu;
            const uint64_t process_cpu = cpu_tenths(process_run_time, wall_time, process_maximum);
            printf("cpu=%" PRIu64 ".%" PRIu64 "%% stack-min=%zu\n",
                process_cpu / 10u, process_cpu % 10u, stack_minimum);
        }
    }

    free(processes);
    release_task_snapshot(&first);
    release_task_snapshot(&second);
}

int32_t ct_managed_diagnostics_kill(uint32_t process_id)
{
    const ct_managed_diagnostics_host_api_v1 *api = host_api();
    if (api == NULL || process_id == 0u || api->ProcessHasExited((uintptr_t)process_id)) return -ESRCH;
    api->ProcessTerminate((uintptr_t)process_id, 1000u);
    return 0;
}
