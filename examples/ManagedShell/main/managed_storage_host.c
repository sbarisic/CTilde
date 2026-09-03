#include "managed_storage_host.h"

#include <errno.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "ctilde_managed_runtime.h"
#include "ctilde_storage.h"
#include "esp_elf.h"
#include "esp_err.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "managed_storage_host_api.h"
#include "private/elf_symbol.h"

#define SD_MOUNT_PATH "/sd"
#define SD_MAXIMUM_OPEN_FILES 16
#define SD_POLL_STACK_BYTES 4096

typedef enum storage_operation {
    STORAGE_OPERATION_NONE = 0,
    STORAGE_OPERATION_MOUNT,
    STORAGE_OPERATION_UNMOUNT,
    STORAGE_OPERATION_REMOUNT,
    STORAGE_OPERATION_FORMAT,
    STORAGE_OPERATION_WRITE_MBR,
} storage_operation;

typedef struct storage_request {
    storage_operation Operation;
    int32_t Target;
    uint32_t Kind;
    uint32_t AllocationUnitBytes;
    uint32_t FatCopies;
    uint32_t TimeoutMilliseconds;
    ct_managed_mbr_layout Layout;
    int32_t Result;
    uint32_t TimedOut;
} storage_request;

static SemaphoreHandle_t s_gate;
static SemaphoreHandle_t s_operation_gate;
static SemaphoreHandle_t s_operation_complete;
static TaskHandle_t s_control_task;
static storage_request s_request;
static uintptr_t s_monitor;
static uintptr_t s_raw_card;
static uintptr_t s_raw_device;
static int32_t s_selected_target = -1;
static int32_t s_last_error;
static uint64_t s_generation;
static ct_managed_sd_state s_observed_state = CT_MANAGED_SD_NOT_PRESENT;
static bool s_timeout_fault;
static ct_storage_sd_card_info s_card_info;
static bool s_card_info_available;

static const ct_storage_sdspi_configuration s_configuration = {
    .Host = 2, .Miso = 2, .Mosi = 15, .Clock = 14, .ChipSelect = 13,
    .FrequencyKilohertz = 20000u,
};

static uint32_t read_le32(const uint8_t *value)
{
    return (uint32_t)value[0] | (uint32_t)value[1] << 8 |
        (uint32_t)value[2] << 16 | (uint32_t)value[3] << 24;
}

static void write_le32(uint8_t *value, uint32_t item)
{
    value[0] = (uint8_t)item;
    value[1] = (uint8_t)(item >> 8);
    value[2] = (uint8_t)(item >> 16);
    value[3] = (uint8_t)(item >> 24);
}

static void observe_state(ct_managed_sd_state state)
{
    if (s_observed_state != state) {
        s_observed_state = state;
        ++s_generation;
    }
}

static void close_raw_locked(void)
{
    if (s_raw_device != 0u) {
        (void)ct_storage_block_release(s_raw_device);
        s_raw_device = 0u;
    }
    if (s_raw_card != 0u) {
        (void)ct_storage_sdspi_close(s_raw_card);
        s_raw_card = 0u;
    }
}

static int32_t open_raw_locked(void)
{
    if (s_raw_card != 0u && ct_storage_sdspi_status(s_raw_card) == 0) return 0;
    close_raw_locked();
    ct_storage_sd_card_info info = {0};
    const int32_t result = ct_storage_sdspi_open(s_configuration, &s_raw_card,
        &s_raw_device, &info);
    if (result != 0) {
        close_raw_locked();
        s_card_info = (ct_storage_sd_card_info){0};
        s_card_info_available = false;
        s_last_error = result;
        observe_state(CT_MANAGED_SD_NOT_PRESENT);
        return result;
    }
    s_card_info = info;
    s_card_info_available = true;
    s_last_error = 0;
    observe_state(CT_MANAGED_SD_UNMOUNTED);
    return 0;
}

static int32_t stop_monitor_locked(void)
{
    if (s_monitor == 0u) return 0;
    const int32_t result = ct_storage_monitor_release(s_monitor);
    if (result == 0) s_monitor = 0u;
    else s_last_error = result;
    return result;
}

static int32_t start_monitor_locked(int32_t target)
{
    if (target < -1 || target > 3) return -EINVAL;
    int32_t result = stop_monitor_locked();
    if (result != 0) return result;
    close_raw_locked();
    result = ct_storage_monitor_create(s_configuration, &s_monitor);
    if (result == 0)
        result = ct_storage_monitor_add_fat(s_monitor, target, SD_MOUNT_PATH,
            SD_MAXIMUM_OPEN_FILES);
    if (result == 0) result = ct_storage_monitor_start(s_monitor);
    if (result != 0) {
        if (s_monitor != 0u) (void)ct_storage_monitor_release(s_monitor);
        s_monitor = 0u;
        s_last_error = result;
        observe_state(CT_MANAGED_SD_FAULTED);
        return result;
    }
    s_selected_target = target;
    s_timeout_fault = false;
    s_last_error = 0;
    observe_state(CT_MANAGED_SD_MOUNTING);
    return 0;
}

static ct_managed_sd_state monitor_state_locked(void)
{
    if (s_timeout_fault) return CT_MANAGED_SD_FAULTED;
    if (s_monitor == 0u) {
        if (s_raw_card == 0u) return CT_MANAGED_SD_NOT_PRESENT;
        if (ct_storage_sdspi_status(s_raw_card) == 0) return CT_MANAGED_SD_UNMOUNTED;
        close_raw_locked();
        s_last_error = -ENODEV;
        return CT_MANAGED_SD_NOT_PRESENT;
    }
    const ct_storage_monitor_state_t state = ct_storage_monitor_state(s_monitor);
    s_last_error = ct_storage_monitor_last_error(s_monitor);
    ct_storage_sd_card_info info;
    if (ct_storage_monitor_card_info(s_monitor, &info) == 0) {
        s_card_info = info;
        s_card_info_available = true;
    }
    if (state == CT_STORAGE_NOT_PRESENT) {
        s_card_info = (ct_storage_sd_card_info){0};
        s_card_info_available = false;
    }
    return (ct_managed_sd_state)state;
}

static int32_t wait_for_mount_locked(uint32_t timeout_milliseconds)
{
    const TickType_t started = xTaskGetTickCount();
    const TickType_t timeout = pdMS_TO_TICKS(timeout_milliseconds);
    for (;;) {
        const ct_managed_sd_state state = monitor_state_locked();
        observe_state(state);
        if (state == CT_MANAGED_SD_MOUNTED) return 0;
        if (state == CT_MANAGED_SD_FAULTED) return s_last_error == 0 ? -EIO : s_last_error;
        if (state == CT_MANAGED_SD_NOT_PRESENT &&
            xTaskGetTickCount() - started >= pdMS_TO_TICKS(1200))
            return s_last_error == 0 ? -ENODEV : s_last_error;
        if (xTaskGetTickCount() - started >= timeout) return -ETIMEDOUT;
        vTaskDelay(pdMS_TO_TICKS(50));
    }
}

static int32_t snapshot(ct_managed_sd_snapshot *output)
{
    if (output == NULL || s_gate == NULL) return -EINVAL;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    const ct_managed_sd_state state = monitor_state_locked();
    observe_state(state);
    ct_storage_fat_volume_info_t volume = {0};
    const bool volume_available = s_monitor != 0u && state == CT_MANAGED_SD_MOUNTED &&
        ct_storage_monitor_volume_info(s_monitor, 0, &volume) == 0;
    *output = (ct_managed_sd_snapshot){
        .Size = sizeof(*output), .Version = CT_MANAGED_STORAGE_HOST_API_VERSION,
        .State = (uint32_t)state, .SelectedTarget = s_selected_target,
        .LastError = s_last_error,
        .Present = s_card_info_available ? 1u : 0u,
        .Generation = s_generation,
        .CapacityBytes = s_card_info.CapacityBytes,
        .VolumeCapacityBytes = volume.CapacityBytes,
        .VolumeFreeBytes = volume.FreeBytes,
        .SectorSize = s_card_info.SectorSize,
        .FrequencyKilohertz = s_card_info.FrequencyKilohertz,
        .ManufacturerId = s_card_info.ManufacturerId,
        .OemId = s_card_info.OemId,
        .ProductRevision = s_card_info.ProductRevision,
        .SerialNumber = s_card_info.SerialNumber,
        .ManufacturingDate = s_card_info.ManufacturingDate,
        .IsHighCapacity = s_card_info.IsHighCapacity ? 1u : 0u,
        .VolumeSectorSize = volume.SectorSize,
        .VolumeClusterSize = volume.ClusterSize,
        .CardInfoAvailable = s_card_info_available ? 1u : 0u,
        .VolumeInfoAvailable = volume_available ? 1u : 0u,
    };
    xSemaphoreGive(s_gate);
    return 0;
}

static int32_t mount_card_now(int32_t target, uint32_t timeout_milliseconds)
{
    if (s_gate == NULL || timeout_milliseconds == 0u) return -EINVAL;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    ct_managed_sd_state state = monitor_state_locked();
    if (target < -1) target = s_selected_target;
    if (state == CT_MANAGED_SD_MOUNTED && target == s_selected_target) {
        xSemaphoreGive(s_gate);
        return 0;
    }
    int32_t result = start_monitor_locked(target);
    if (result == 0) result = wait_for_mount_locked(timeout_milliseconds);
    s_last_error = result;
    xSemaphoreGive(s_gate);
    return result;
}

static int32_t unmount_card_now(uint32_t timeout_milliseconds)
{
    (void)timeout_milliseconds;
    if (s_gate == NULL) return -EINVAL;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    if (ctilde_managed_storage_prefix_busy(SD_MOUNT_PATH)) {
        s_last_error = -EBUSY;
        xSemaphoreGive(s_gate);
        return -EBUSY;
    }
    int32_t result = stop_monitor_locked();
    if (result == 0) {
        s_timeout_fault = false;
        const int32_t open_result = open_raw_locked();
        if (open_result != 0 && open_result != -ESP_ERR_NOT_FOUND &&
            open_result != -ESP_ERR_TIMEOUT) result = open_result;
    }
    s_last_error = result;
    xSemaphoreGive(s_gate);
    return result;
}

static int32_t remount_card_now(uint32_t timeout_milliseconds)
{
    int32_t result = unmount_card_now(timeout_milliseconds);
    return result == 0 ? mount_card_now(s_selected_target, timeout_milliseconds) : result;
}

static int32_t read_sector_locked(uint8_t sector[512])
{
    const int32_t result = open_raw_locked();
    return result == 0 ? ct_storage_block_read(s_raw_device, 0u, sector, 512u) : result;
}

static int32_t decode_mbr_locked(ct_managed_mbr_layout *output, uint8_t sector[512])
{
    int32_t result = read_sector_locked(sector);
    if (result != 0) return result;
    if (sector[510] != 0x55u || sector[511] != 0xaau) return -ENOEXEC;
    ct_managed_mbr_entry *entries[4] = {
        &output->Entry0, &output->Entry1, &output->Entry2, &output->Entry3,
    };
    for (size_t index = 0; index < 4u; ++index) {
        const size_t offset = 446u + index * 16u;
        if (sector[offset] != 0u && sector[offset] != 0x80u) return -ENOEXEC;
        *entries[index] = (ct_managed_mbr_entry){
            .Bootable = sector[offset] == 0x80u ? 1u : 0u,
            .Type = sector[offset + 4u],
            .FirstSector = read_le32(sector + offset + 8u),
            .SectorCount = read_le32(sector + offset + 12u),
        };
    }
    return 0;
}

static bool fat_partition_type(uint32_t type)
{
    return type == 1u || type == 4u || type == 6u || type == 11u ||
        type == 12u || type == 14u;
}

static int32_t target_device_locked(int32_t target, uintptr_t *device, bool *owned)
{
    int32_t result = open_raw_locked();
    if (result != 0) return result;
    if (target == -1) {
        *device = s_raw_device;
        *owned = false;
        return 0;
    }
    if (target < 0 || target > 3) return -EINVAL;
    uint8_t sector[512];
    ct_managed_mbr_layout layout;
    result = decode_mbr_locked(&layout, sector);
    if (result != 0) return result;
    const ct_managed_mbr_entry *entries[4] = {
        &layout.Entry0, &layout.Entry1, &layout.Entry2, &layout.Entry3,
    };
    const ct_managed_mbr_entry *entry = entries[target];
    if (!fat_partition_type(entry->Type) || entry->SectorCount == 0u) return -EINVAL;
    result = ct_storage_block_slice(s_raw_device, (uint64_t)entry->FirstSector * 512u,
        (uint64_t)entry->SectorCount * 512u, device);
    *owned = result == 0;
    return result;
}

static int32_t format_card_now(int32_t target, uint32_t kind,
    uint32_t allocation_unit_bytes, uint32_t fat_copies, uint32_t timeout_milliseconds)
{
    if (s_gate == NULL || kind > 3u || fat_copies < 1u || fat_copies > 2u) return -EINVAL;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    if (s_monitor != 0u) {
        xSemaphoreGive(s_gate);
        return -EBUSY;
    }
    uintptr_t device = 0u;
    bool owned = false;
    const TickType_t started = xTaskGetTickCount();
    int32_t result = target_device_locked(target, &device, &owned);
    if (result == 0)
        result = ct_storage_fat_format(device, (uint8_t)kind, allocation_unit_bytes,
            (uint8_t)fat_copies);
    if (owned) (void)ct_storage_block_release(device);
    if (result == 0 && xTaskGetTickCount() - started > pdMS_TO_TICKS(timeout_milliseconds))
        result = -ETIMEDOUT;
    s_last_error = result;
    xSemaphoreGive(s_gate);
    return result;
}

static const ct_managed_mbr_entry *layout_entry(const ct_managed_mbr_layout *layout,
    size_t index)
{
    if (index == 0u) return &layout->Entry0;
    if (index == 1u) return &layout->Entry1;
    if (index == 2u) return &layout->Entry2;
    return &layout->Entry3;
}

static int32_t validate_layout_locked(const ct_managed_mbr_layout *layout)
{
    ct_storage_block_info_t info;
    int32_t result = ct_storage_block_info(s_raw_device, &info);
    if (result != 0) return result;
    const uint64_t sectors = info.Length / 512u;
    uint32_t bootable = 0u;
    for (size_t index = 0; index < 4u; ++index) {
        const ct_managed_mbr_entry *entry = layout_entry(layout, index);
        if (entry->Bootable > 1u || entry->Type > 255u) return -EINVAL;
        bootable += entry->Bootable;
        if (bootable > 1u) return -EINVAL;
        if (entry->Type == 0u) {
            if (entry->Bootable != 0u || entry->FirstSector != 0u ||
                entry->SectorCount != 0u) return -EINVAL;
            continue;
        }
        if (entry->Type == 5u || entry->Type == 15u || entry->Type == 0xeeu ||
            entry->FirstSector == 0u || entry->SectorCount == 0u) return -EINVAL;
        const uint64_t end = (uint64_t)entry->FirstSector + entry->SectorCount;
        if (end > sectors) return -ERANGE;
        for (size_t prior = 0; prior < index; ++prior) {
            const ct_managed_mbr_entry *other = layout_entry(layout, prior);
            const uint64_t other_end = (uint64_t)other->FirstSector + other->SectorCount;
            if (other->Type != 0u && (uint64_t)entry->FirstSector < other_end &&
                (uint64_t)other->FirstSector < end) return -EINVAL;
        }
    }
    return 0;
}

static int32_t read_mbr(ct_managed_mbr_layout *output)
{
    if (s_gate == NULL || s_operation_gate == NULL || output == NULL) return -EINVAL;
    if (xSemaphoreTake(s_operation_gate, pdMS_TO_TICKS(10000)) != pdTRUE)
        return -EBUSY;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    int32_t result = -EBUSY;
    if (s_monitor == 0u) {
        uint8_t sector[512];
        result = decode_mbr_locked(output, sector);
        if (result == 0) result = validate_layout_locked(output);
    }
    s_last_error = result;
    xSemaphoreGive(s_gate);
    xSemaphoreGive(s_operation_gate);
    return result;
}

static int32_t write_mbr_now(const ct_managed_mbr_layout *layout,
    uint32_t timeout_milliseconds)
{
    if (s_gate == NULL || layout == NULL) return -EINVAL;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    if (s_monitor != 0u) {
        xSemaphoreGive(s_gate);
        return -EBUSY;
    }
    uint8_t sector[512];
    const TickType_t started = xTaskGetTickCount();
    int32_t result = read_sector_locked(sector);
    if (result == 0) result = validate_layout_locked(layout);
    if (result == 0) {
        (void)memset(sector + 446u, 0, 64u);
        for (size_t index = 0; index < 4u; ++index) {
            const ct_managed_mbr_entry *entry = layout_entry(layout, index);
            const size_t offset = 446u + index * 16u;
            sector[offset] = entry->Bootable != 0u ? 0x80u : 0u;
            sector[offset + 4u] = (uint8_t)entry->Type;
            write_le32(sector + offset + 8u, entry->FirstSector);
            write_le32(sector + offset + 12u, entry->SectorCount);
        }
        sector[510] = 0x55u;
        sector[511] = 0xaau;
        result = ct_storage_block_write(s_raw_device, 0u, sector, sizeof(sector));
        if (result == 0) result = ct_storage_block_flush(s_raw_device);
        if (result == 0) {
            uint8_t verify[512];
            result = ct_storage_block_read(s_raw_device, 0u, verify, sizeof(verify));
            if (result == 0 && memcmp(sector, verify, sizeof(sector)) != 0) result = -EIO;
        }
    }
    if (result == 0 && xTaskGetTickCount() - started > pdMS_TO_TICKS(timeout_milliseconds))
        result = -ETIMEDOUT;
    s_last_error = result;
    xSemaphoreGive(s_gate);
    return result;
}

static const char *error_name(int32_t code)
{
    if (code == 0) return "OK";
    if (code <= -0x7000 && code > -0x7020) {
        static const char *const names[] = {
            "FR_OK", "FR_DISK_ERR", "FR_INT_ERR", "FR_NOT_READY", "FR_NO_FILE",
            "FR_NO_PATH", "FR_INVALID_NAME", "FR_DENIED", "FR_EXIST",
            "FR_INVALID_OBJECT", "FR_WRITE_PROTECTED", "FR_INVALID_DRIVE",
            "FR_NOT_ENABLED", "FR_NO_FILESYSTEM", "FR_MKFS_ABORTED", "FR_TIMEOUT",
            "FR_LOCKED", "FR_NOT_ENOUGH_CORE", "FR_TOO_MANY_OPEN_FILES",
            "FR_INVALID_PARAMETER",
        };
        const uint32_t index = (uint32_t)(-code - 0x7000);
        if (index < sizeof(names) / sizeof(names[0])) return names[index];
    }
    switch (-code) {
        case EINVAL: return "EINVAL";
        case EBUSY: return "EBUSY";
        case ENODEV: return "ENODEV";
        case ENOENT: return "ENOENT";
        case ENOEXEC: return "ENOEXEC";
        case ENOTSUP: return "ENOTSUP";
        case ERANGE: return "ERANGE";
        case ETIMEDOUT: return "ETIMEDOUT";
        case EIO: return "EIO";
        default: break;
    }
    return code < 0 ? esp_err_to_name((esp_err_t)-code) : "UNKNOWN";
}

static void storage_control(void *argument)
{
    (void)argument;
    xSemaphoreTake(s_gate, portMAX_DELAY);
    (void)open_raw_locked();
    (void)start_monitor_locked(-1);
    xSemaphoreGive(s_gate);
    for (;;) {
        const uint32_t notified = ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(500));
        if (notified != 0u) {
            int32_t result = -EINVAL;
            switch (s_request.Operation) {
                case STORAGE_OPERATION_MOUNT:
                    result = mount_card_now(s_request.Target,
                        s_request.TimeoutMilliseconds);
                    break;
                case STORAGE_OPERATION_UNMOUNT:
                    result = unmount_card_now(s_request.TimeoutMilliseconds);
                    break;
                case STORAGE_OPERATION_REMOUNT:
                    result = remount_card_now(s_request.TimeoutMilliseconds);
                    break;
                case STORAGE_OPERATION_FORMAT:
                    result = format_card_now(s_request.Target, s_request.Kind,
                        s_request.AllocationUnitBytes, s_request.FatCopies,
                        s_request.TimeoutMilliseconds);
                    break;
                case STORAGE_OPERATION_WRITE_MBR:
                    result = write_mbr_now(&s_request.Layout,
                        s_request.TimeoutMilliseconds);
                    break;
                default:
                    break;
            }
            s_request.Result = result;
            if (result == -ETIMEDOUT ||
                __atomic_load_n(&s_request.TimedOut, __ATOMIC_ACQUIRE) != 0u) {
                xSemaphoreTake(s_gate, portMAX_DELAY);
                s_timeout_fault = true;
                s_last_error = -ETIMEDOUT;
                observe_state(CT_MANAGED_SD_FAULTED);
                xSemaphoreGive(s_gate);
            }
            xSemaphoreGive(s_operation_complete);
            xSemaphoreGive(s_operation_gate);
            continue;
        }
        if (xSemaphoreTake(s_gate, 0) != pdTRUE) continue;
        if (s_monitor == 0u) (void)open_raw_locked();
        xSemaphoreGive(s_gate);
    }
}

static int32_t submit_operation(storage_operation operation, int32_t target,
    uint32_t kind, uint32_t allocation_unit_bytes, uint32_t fat_copies,
    const ct_managed_mbr_layout *layout, uint32_t timeout_milliseconds)
{
    if (s_operation_gate == NULL || s_operation_complete == NULL ||
        s_control_task == NULL || timeout_milliseconds == 0u) return -EINVAL;
    const TickType_t timeout = pdMS_TO_TICKS(timeout_milliseconds);
    if (xSemaphoreTake(s_operation_gate, 0) != pdTRUE) return -EBUSY;
    (void)xSemaphoreTake(s_operation_complete, 0);
    s_request = (storage_request){
        .Operation = operation, .Target = target, .Kind = kind,
        .AllocationUnitBytes = allocation_unit_bytes, .FatCopies = fat_copies,
        .TimeoutMilliseconds = timeout_milliseconds,
    };
    if (layout != NULL) s_request.Layout = *layout;
    xTaskNotifyGive(s_control_task);
    if (xSemaphoreTake(s_operation_complete, timeout) != pdTRUE) {
        __atomic_store_n(&s_request.TimedOut, 1u, __ATOMIC_RELEASE);
        return -ETIMEDOUT;
    }
    return s_request.Result;
}

static int32_t mount_card(int32_t target, uint32_t timeout_milliseconds)
{
    return submit_operation(STORAGE_OPERATION_MOUNT, target, 0u, 0u, 0u,
        NULL, timeout_milliseconds);
}

static int32_t unmount_card(uint32_t timeout_milliseconds)
{
    return submit_operation(STORAGE_OPERATION_UNMOUNT, 0, 0u, 0u, 0u,
        NULL, timeout_milliseconds);
}

static int32_t remount_card(uint32_t timeout_milliseconds)
{
    return submit_operation(STORAGE_OPERATION_REMOUNT, 0, 0u, 0u, 0u,
        NULL, timeout_milliseconds);
}

static int32_t format_card(int32_t target, uint32_t kind,
    uint32_t allocation_unit_bytes, uint32_t fat_copies,
    uint32_t timeout_milliseconds)
{
    return submit_operation(STORAGE_OPERATION_FORMAT, target, kind,
        allocation_unit_bytes, fat_copies, NULL, timeout_milliseconds);
}

static int32_t write_mbr(const ct_managed_mbr_layout *layout,
    uint32_t timeout_milliseconds)
{
    if (layout == NULL) return -EINVAL;
    return submit_operation(STORAGE_OPERATION_WRITE_MBR, 0, 0u, 0u, 0u,
        layout, timeout_milliseconds);
}

static const ct_managed_storage_host_api_v1 s_host_api = {
    .Size = sizeof(ct_managed_storage_host_api_v1),
    .Version = CT_MANAGED_STORAGE_HOST_API_VERSION,
    .Snapshot = snapshot, .Mount = mount_card, .Unmount = unmount_card,
    .Remount = remount_card, .Format = format_card, .ReadMbr = read_mbr,
    .WriteMbr = write_mbr, .ErrorName = error_name,
};

const ct_managed_storage_host_api_v1 *ct_managed_storage_host_v1(void)
{
    return &s_host_api;
}

static const struct esp_elfsym s_host_symbols[] = {
    ESP_ELFSYM_EXPORT(ct_managed_storage_host_v1),
};

int ct_managed_storage_host_initialize(void)
{
    if (s_gate != NULL) return 0;
    s_gate = xSemaphoreCreateMutex();
    s_operation_gate = xSemaphoreCreateBinary();
    s_operation_complete = xSemaphoreCreateBinary();
    if (s_gate == NULL || s_operation_gate == NULL ||
        s_operation_complete == NULL) return -ENOMEM;
    xSemaphoreGive(s_operation_gate);
    const esp_err_t symbols = esp_elf_register_symbol(
        (esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_host_symbols);
    if (symbols != ESP_OK) return -(int32_t)symbols;
    if (xTaskCreate(storage_control, "ct_sd_control", SD_POLL_STACK_BYTES, NULL,
        tskIDLE_PRIORITY + 1, &s_control_task) != pdPASS) return -ENOMEM;
    return 0;
}
