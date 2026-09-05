#include "ctilde_managed_runtime.h"
#include <errno.h>
#include "freertos/FreeRTOS.h"

#define CT_CAPABILITY_SLOTS 16u
typedef struct ct_capability_entry {
    uint32_t Id;
    const ct_capability_header *Table;
} ct_capability_entry;
static ct_capability_entry s_capabilities[CT_CAPABILITY_SLOTS];
static portMUX_TYPE s_capability_lock = portMUX_INITIALIZER_UNLOCKED;
static bool s_capabilities_sealed;

int32_t ctilde_managed_register_capability(uint32_t id, const void *table)
{
    const ct_capability_header *header = table;
    if (id == 0 || header == NULL || header->Size < sizeof(*header) || header->MajorVersion == 0)
        return -EINVAL;
    int32_t result = -ENOSPC;
    portENTER_CRITICAL(&s_capability_lock);
    for (size_t index = 0; index < CT_CAPABILITY_SLOTS; ++index) {
        if (s_capabilities[index].Id == id) {
            result = s_capabilities[index].Table == table ? 0 : -EEXIST;
            goto done;
        }
    }
    if (s_capabilities_sealed) { result = -EPERM; goto done; }
    for (size_t index = 0; index < CT_CAPABILITY_SLOTS; ++index) {
        if (s_capabilities[index].Table == NULL) {
            s_capabilities[index] = (ct_capability_entry){ id, header };
            result = 0;
            break;
        }
    }
done:
    portEXIT_CRITICAL(&s_capability_lock);
    return result;
}

const void *ctilde_managed_get_capability(uint32_t id, uint32_t major_version, uint32_t minimum_size)
{
    const void *result = NULL;
    portENTER_CRITICAL(&s_capability_lock);
    s_capabilities_sealed = true;
    for (size_t index = 0; index < CT_CAPABILITY_SLOTS; ++index) {
        const ct_capability_header *table = s_capabilities[index].Table;
        if (s_capabilities[index].Id == id && table != NULL && table->MajorVersion == major_version &&
            table->Size >= minimum_size) {
            result = table;
            break;
        }
    }
    portEXIT_CRITICAL(&s_capability_lock);
    return result;
}
