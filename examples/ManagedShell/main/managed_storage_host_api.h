#pragma once

#include <stdint.h>

#define CT_MANAGED_STORAGE_HOST_API_VERSION 1u

typedef enum ct_managed_sd_state {
    CT_MANAGED_SD_NOT_PRESENT = 0,
    CT_MANAGED_SD_MOUNTING = 1,
    CT_MANAGED_SD_MOUNTED = 2,
    CT_MANAGED_SD_REMOVING = 3,
    CT_MANAGED_SD_FAULTED = 4,
    CT_MANAGED_SD_UNMOUNTED = 5,
} ct_managed_sd_state;

typedef struct ct_managed_sd_snapshot {
    uint32_t Size;
    uint32_t Version;
    uint32_t State;
    int32_t SelectedTarget;
    int32_t LastError;
    uint32_t Present;
    uint64_t Generation;
    uint64_t CapacityBytes;
    uint64_t VolumeCapacityBytes;
    uint64_t VolumeFreeBytes;
    uint32_t SectorSize;
    uint32_t FrequencyKilohertz;
    int32_t ManufacturerId;
    int32_t OemId;
    int32_t ProductRevision;
    uint32_t SerialNumber;
    int32_t ManufacturingDate;
    uint32_t IsHighCapacity;
    uint32_t VolumeSectorSize;
    uint32_t VolumeClusterSize;
    uint32_t CardInfoAvailable;
    uint32_t VolumeInfoAvailable;
} ct_managed_sd_snapshot;

typedef struct ct_managed_mbr_entry {
    uint32_t Bootable;
    uint32_t Type;
    uint32_t FirstSector;
    uint32_t SectorCount;
} ct_managed_mbr_entry;

typedef struct ct_managed_mbr_layout {
    ct_managed_mbr_entry Entry0;
    ct_managed_mbr_entry Entry1;
    ct_managed_mbr_entry Entry2;
    ct_managed_mbr_entry Entry3;
} ct_managed_mbr_layout;

typedef struct ct_managed_storage_host_api_v1 {
    uint32_t Size;
    uint32_t Version;
    int32_t (*Snapshot)(ct_managed_sd_snapshot *output);
    int32_t (*Mount)(int32_t target, uint32_t timeout_milliseconds);
    int32_t (*Unmount)(uint32_t timeout_milliseconds);
    int32_t (*Remount)(uint32_t timeout_milliseconds);
    int32_t (*Format)(int32_t target, uint32_t kind, uint32_t allocation_unit_bytes,
        uint32_t fat_copies, uint32_t timeout_milliseconds);
    int32_t (*ReadMbr)(ct_managed_mbr_layout *output);
    int32_t (*WriteMbr)(const ct_managed_mbr_layout *layout, uint32_t timeout_milliseconds);
    const char *(*ErrorName)(int32_t code);
} ct_managed_storage_host_api_v1;

const ct_managed_storage_host_api_v1 *ct_managed_storage_host_v1(void);
