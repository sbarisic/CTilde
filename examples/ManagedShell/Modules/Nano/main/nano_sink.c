#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct ct_managed_module_descriptor_v4 ct_managed_module_descriptor_v4;
typedef struct ct_managed_call_target_v4 ct_managed_call_target_v4;
typedef struct ct_managed_call_frame_v23 ct_managed_call_frame_v23;

typedef struct ct_type_descriptor ct_type_descriptor;
typedef struct ct_process_context ct_process_context;
typedef struct ct_runtime_api_v23 ct_runtime_api_v23;
#include "../../../../../runtime/esp-idf/ctilde_managed_runtime/include/ct_runtime_contract.h"

typedef struct ct_console_transfer_v19 {
    uint8_t *Data;
    size_t Length;
    size_t Count;
    bool Eof;
} ct_console_transfer_v19;

typedef struct nano_sink {
    uint8_t *Data;
    size_t Length;
    size_t Capacity;
} nano_sink;

extern const ct_runtime_api_v23 *ct_runtime_api;
extern const ct_managed_module_descriptor_v4 ct_managed_module_v4;

int32_t ct_nano_sink_flush(uintptr_t handle);

uintptr_t ct_nano_sink_create(uint32_t capacity)
{
    if (capacity == 0 || capacity > 32768 || ct_runtime_api == NULL) return 0;
    nano_sink *sink = (nano_sink *)ct_runtime_api->Allocate(sizeof(*sink) + capacity,
        &ct_managed_module_v4);
    if (sink == NULL) return 0;
    sink->Data = (uint8_t *)(sink + 1);
    sink->Length = 0;
    sink->Capacity = capacity;
    return (uintptr_t)sink;
}

void ct_nano_sink_destroy(uintptr_t handle)
{
    nano_sink *sink = (nano_sink *)handle;
    if (sink == NULL) return;
    ct_runtime_api->Free(sink);
}

void ct_nano_sink_reset(uintptr_t handle)
{
    nano_sink *sink = (nano_sink *)handle;
    if (sink != NULL) sink->Length = 0;
}

int32_t ct_nano_sink_append(uintptr_t handle, uint8_t value)
{
    nano_sink *sink = (nano_sink *)handle;
    if (sink == NULL) return -1;
    if (sink->Length == sink->Capacity) {
        const int32_t result = ct_nano_sink_flush(handle);
        if (result != 0) return result;
    }
    sink->Data[sink->Length++] = value;
    return 0;
}

int32_t ct_nano_sink_flush(uintptr_t handle)
{
    nano_sink *sink = (nano_sink *)handle;
    if (sink == NULL || ct_runtime_api == NULL) return -1;
    ct_console_transfer_v19 transfer = {
        .Data = sink->Data, .Length = sink->Length, .Count = 0, .Eof = false,
    };
    const int32_t result = ct_runtime_api->Service(UINT32_C(16), &transfer,
        sizeof(transfer));
    if (result != 0 || transfer.Count != transfer.Length)
        return result == 0 ? -1 : result;
    sink->Length = 0;
    return ct_runtime_api->Service(UINT32_C(18), NULL, 0u);
}

int32_t ct_nano_query_size(void)
{
    static const uint8_t request[] = { 27u, '[', '1', '8', 't' };
    if (ct_runtime_api == NULL) return -1;
    ct_console_transfer_v19 transfer = {
        .Data = (uint8_t *)(uintptr_t)(const void *)request,
        .Length = sizeof(request), .Count = 0, .Eof = false,
    };
    const int32_t result = ct_runtime_api->Service(UINT32_C(16), &transfer,
        sizeof(transfer));
    if (result != 0 || transfer.Count != transfer.Length)
        return result == 0 ? -1 : result;
    return ct_runtime_api->Service(UINT32_C(18), NULL, 0u);
}
