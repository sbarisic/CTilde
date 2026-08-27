#include <stddef.h>
#include <stdint.h>

static _Alignas(16) uint8_t arena[64 * 1024];
static size_t arena_used;

void* platform_allocate(uintptr_t size)
{
    const size_t aligned = (size_t)((size + (uintptr_t)15) & ~(uintptr_t)15);
    if (aligned > sizeof(arena) - arena_used)
        return NULL;
    void* result = arena + arena_used;
    arena_used += aligned;
    return result;
}

void platform_free(void* value)
{
    (void)value;
}
