#include <stddef.h>
#include <stdint.h>
#include <string.h>

extern int32_t ctilde_add(int32_t left, int32_t right);

uint32_t ct_native_buffer_sum(const uint8_t* data, size_t length)
{
    uint32_t result = 0;
    for (size_t index = 0; index < length; index++) result += data[index];
    return result;
}

uint32_t ct_native_utf8_length(const char* value)
{
    return value == NULL ? 0u : (uint32_t)strlen(value);
}

int32_t ct_native_resource_create(uintptr_t* resource)
{
    *resource = (uintptr_t)42;
    return 0;
}

int32_t ct_native_resource_value(uintptr_t resource)
{
    return (int32_t)resource;
}

void ct_native_resource_release(uintptr_t resource)
{
    (void)resource;
}

int32_t ct_native_invoke_delegate(int32_t (*callback)(int32_t, void*), void* context, int32_t value)
{
    return callback(value, context);
}

int32_t ct_native_call_export(int32_t left, int32_t right)
{
    return ctilde_add(left, right);
}
