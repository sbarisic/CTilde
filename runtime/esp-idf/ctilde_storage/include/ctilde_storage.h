#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct ct_storage_block_info_t {
    uint64_t Length;
    size_t ReadAlignment;
    size_t WriteAlignment;
    size_t EraseAlignment;
    size_t PreferredReadSize;
    size_t PreferredWriteSize;
    size_t PreferredEraseSize;
    bool IsReadOnly;
} ct_storage_block_info_t;

typedef struct ct_storage_sdspi_configuration {
    int32_t Host;
    int32_t Miso;
    int32_t Mosi;
    int32_t Clock;
    int32_t ChipSelect;
    uint32_t FrequencyKilohertz;
} ct_storage_sdspi_configuration;

typedef struct ct_storage_sd_card_info {
    uint64_t CapacityBytes;
    uint32_t SectorSize;
    uint32_t FrequencyKilohertz;
    int32_t ManufacturerId;
    int32_t OemId;
    int32_t ProductRevision;
    uint32_t SerialNumber;
    int32_t ManufacturingDate;
    bool IsHighCapacity;
} ct_storage_sd_card_info;

typedef struct ct_storage_fat_volume_info_t {
    uint64_t CapacityBytes;
    uint64_t FreeBytes;
    uint32_t SectorSize;
    uint32_t ClusterSize;
} ct_storage_fat_volume_info_t;

typedef enum ct_storage_monitor_state_t {
    CT_STORAGE_NOT_PRESENT = 0,
    CT_STORAGE_MOUNTING = 1,
    CT_STORAGE_MOUNTED = 2,
    CT_STORAGE_REMOVING = 3,
    CT_STORAGE_FAULTED = 4,
} ct_storage_monitor_state_t;

int32_t ct_storage_sdspi_open(ct_storage_sdspi_configuration configuration,
    uintptr_t *card, uintptr_t *device, ct_storage_sd_card_info *info);
int32_t ct_storage_sdspi_status(uintptr_t card);
int32_t ct_storage_sdspi_close(uintptr_t card);

int32_t ct_storage_block_info(uintptr_t handle, ct_storage_block_info_t *info);
int32_t ct_storage_block_read(uintptr_t handle, uint64_t offset,
    uint8_t *destination, size_t destination_length);
int32_t ct_storage_block_write(uintptr_t handle, uint64_t offset,
    const uint8_t *source, size_t source_length);
int32_t ct_storage_block_erase(uintptr_t handle, uint64_t offset, size_t length);
int32_t ct_storage_block_flush(uintptr_t handle);
int32_t ct_storage_block_slice(uintptr_t handle, uint64_t offset, uint64_t length,
    uintptr_t *result);
int32_t ct_storage_block_release(uintptr_t handle);

int32_t ct_storage_fat_mount(const char *path, uintptr_t device,
    int32_t maximum_open_files, uintptr_t *mount);
int32_t ct_storage_fat_unmount(uintptr_t mount);
int32_t ct_storage_fat_volume_info(uintptr_t mount,
    ct_storage_fat_volume_info_t *info);
uint64_t ct_storage_fat_generation(uintptr_t mount);
bool ct_storage_fat_is_available(uintptr_t mount);
int32_t ct_storage_fat_format(uintptr_t device, uint8_t kind,
    uint32_t allocation_unit_bytes, uint8_t fat_copies);

int32_t ct_storage_monitor_create(ct_storage_sdspi_configuration configuration,
    uintptr_t *monitor);
int32_t ct_storage_monitor_add_fat(uintptr_t monitor, int32_t partition_index,
    const char *path, int32_t maximum_open_files);
int32_t ct_storage_monitor_start(uintptr_t monitor);
ct_storage_monitor_state_t ct_storage_monitor_state(uintptr_t monitor);
uint64_t ct_storage_monitor_generation(uintptr_t monitor);
int32_t ct_storage_monitor_last_error(uintptr_t monitor);
int32_t ct_storage_monitor_remount(uintptr_t monitor);
int32_t ct_storage_monitor_volume_info(uintptr_t monitor, int32_t mapping_index,
    ct_storage_fat_volume_info_t *info);
int32_t ct_storage_monitor_card_info(uintptr_t monitor,
    ct_storage_sd_card_info *info);
int32_t ct_storage_monitor_release(uintptr_t monitor);

/* The managed runtime supplies this hook. It is intentionally weak so the
   storage component can also be used by ordinary ESP-IDF firmware. */
void ctilde_managed_storage_invalidate_prefix(const char *prefix, uint64_t generation);

#ifdef __cplusplus
}
#endif
