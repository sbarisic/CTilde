#include "managed_storage_host_api.h"

#include <stddef.h>
#include <stdint.h>

static const ct_managed_storage_host_api_v1 *host_api(void)
{
    const ct_managed_storage_host_api_v1 *api = ct_managed_storage_host_v1();
    return api != NULL && api->Version == CT_MANAGED_STORAGE_HOST_API_VERSION &&
        api->Size >= sizeof(ct_managed_storage_host_api_v1) ? api : NULL;
}

int32_t ct_managed_sd_get_snapshot(ct_managed_sd_snapshot *output)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->Snapshot(output);
}

int32_t ct_managed_sd_mount(int32_t target, uint32_t timeout_milliseconds)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->Mount(target, timeout_milliseconds);
}

int32_t ct_managed_sd_unmount(uint32_t timeout_milliseconds)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->Unmount(timeout_milliseconds);
}

int32_t ct_managed_sd_remount(uint32_t timeout_milliseconds)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->Remount(timeout_milliseconds);
}

int32_t ct_managed_sd_format(int32_t target, uint32_t kind,
    uint32_t allocation_unit_bytes, uint32_t fat_copies,
    uint32_t timeout_milliseconds)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->Format(target, kind, allocation_unit_bytes,
        fat_copies, timeout_milliseconds);
}

int32_t ct_managed_sd_read_mbr(ct_managed_mbr_layout *output)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->ReadMbr(output);
}

int32_t ct_managed_sd_write_mbr(const ct_managed_mbr_layout *layout,
    uint32_t timeout_milliseconds)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return api == NULL ? -1 : api->WriteMbr(layout, timeout_milliseconds);
}

const uint8_t *ct_managed_sd_error_name(int32_t code)
{
    const ct_managed_storage_host_api_v1 *api = host_api();
    return (const uint8_t *)(api == NULL ? "HOST_API_UNAVAILABLE" : api->ErrorName(code));
}
