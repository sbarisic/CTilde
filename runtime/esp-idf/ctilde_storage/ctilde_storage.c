#include "ctilde_storage.h"

#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "driver/sdspi_host.h"
#include "driver/spi_master.h"
#include "esp_blockdev.h"
#include "esp_blockdev/generic_partition.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_vfs_fat.h"
#include "esp_vfs.h"
#include "ff.h"
#include "diskio_impl.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "sdmmc_cmd.h"
#include "sd_protocol_defs.h"

#define CT_STORAGE_CARD_MAGIC UINT32_C(0x43545343)
#define CT_STORAGE_DEVICE_MAGIC UINT32_C(0x43545344)
#define CT_STORAGE_MOUNT_MAGIC UINT32_C(0x4354534d)
#define CT_STORAGE_MONITOR_MAGIC UINT32_C(0x43545352)
#define CT_STORAGE_MAX_MOUNTS 4

static const char *TAG = "ctilde.storage";

typedef struct ct_card ct_card;
typedef struct ct_device ct_device;

struct ct_card {
    uint32_t Magic;
    sdmmc_card_t Card;
    sdspi_dev_handle_t Device;
    spi_host_device_t Host;
    uint32_t References;
    bool HostInitialized;
    bool DeviceInitialized;
};

struct ct_device {
    uint32_t Magic;
    esp_blockdev_handle_t Block;
    ct_device *Parent;
    ct_card *Card;
    uint32_t References;
    uint32_t Mounts;
    uint32_t DirectMounts;
    bool OwnsBlock;
};

typedef struct ct_mount {
    uint32_t Magic;
    ct_device *Device;
    BYTE Drive;
    char DriveName[4];
    char Path[ESP_VFS_PATH_MAX + 1];
    FATFS *FileSystem;
    uint64_t Generation;
    bool Available;
} ct_mount;

typedef struct ct_monitor_mapping {
    int32_t PartitionIndex;
    int32_t MaximumOpenFiles;
    char Path[ESP_VFS_PATH_MAX + 1];
    ct_device *Device;
    ct_mount *Mount;
} ct_monitor_mapping;

typedef struct ct_monitor {
    uint32_t Magic;
    ct_storage_sdspi_configuration Configuration;
    ct_monitor_mapping Mappings[CT_STORAGE_MAX_MOUNTS];
    size_t MappingCount;
    ct_card *Card;
    ct_device *Root;
    ct_storage_sd_card_info CardInfo;
    bool CardInfoAvailable;
    TaskHandle_t Task;
    SemaphoreHandle_t Stopped;
    StaticSemaphore_t StoppedStorage;
    SemaphoreHandle_t AppendGate;
    StaticSemaphore_t AppendGateStorage;
    volatile bool StopRequested;
    volatile bool RemountRequested;
    volatile ct_storage_monitor_state_t State;
    volatile int32_t LastError;
    volatile uint64_t Generation;
} ct_monitor;

typedef struct ct_disk_slot {
    ct_device *Device;
} ct_disk_slot;

static ct_disk_slot s_disks[FF_VOLUMES];

__attribute__((weak)) void ctilde_managed_storage_invalidate_prefix(
    const char *prefix, uint64_t generation)
{
    (void)prefix;
    (void)generation;
}

__attribute__((weak)) bool ctilde_managed_storage_prefix_busy(const char *prefix)
{
    (void)prefix;
    return false;
}

static int32_t neg(esp_err_t error)
{
    return error == ESP_OK ? 0 : -(int32_t)error;
}

static ct_card *card_from(uintptr_t value)
{
    ct_card *card = (ct_card *)value;
    return card != NULL && card->Magic == CT_STORAGE_CARD_MAGIC ? card : NULL;
}

static ct_device *device_from(uintptr_t value)
{
    ct_device *device = (ct_device *)value;
    return device != NULL && device->Magic == CT_STORAGE_DEVICE_MAGIC ? device : NULL;
}

static ct_mount *mount_from(uintptr_t value)
{
    ct_mount *mount = (ct_mount *)value;
    return mount != NULL && mount->Magic == CT_STORAGE_MOUNT_MAGIC ? mount : NULL;
}

static ct_monitor *monitor_from(uintptr_t value)
{
    ct_monitor *monitor = (ct_monitor *)value;
    return monitor != NULL && monitor->Magic == CT_STORAGE_MONITOR_MAGIC ? monitor : NULL;
}

static void retain_card(ct_card *card)
{
    (void)__atomic_add_fetch(&card->References, 1u, __ATOMIC_ACQ_REL);
}

static void retain_device(ct_device *device)
{
    (void)__atomic_add_fetch(&device->References, 1u, __ATOMIC_ACQ_REL);
}

static bool device_has_mounted_ancestor(const ct_device *device)
{
    for (const ct_device *current = device->Parent; current != NULL; current = current->Parent) {
        if (__atomic_load_n(&current->DirectMounts, __ATOMIC_ACQUIRE) != 0) return true;
    }
    return false;
}

static bool device_raw_io_is_busy(const ct_device *device)
{
    return __atomic_load_n(&device->Mounts, __ATOMIC_ACQUIRE) != 0 ||
        device_has_mounted_ancestor(device);
}

static void adjust_mount_count(ct_device *device, bool add)
{
    for (ct_device *current = device; current != NULL; current = current->Parent) {
        if (add)
            (void)__atomic_add_fetch(&current->Mounts, 1u, __ATOMIC_ACQ_REL);
        else
            (void)__atomic_sub_fetch(&current->Mounts, 1u, __ATOMIC_ACQ_REL);
    }
}

static esp_err_t release_device_internal(ct_device *device)
{
    if (device == NULL || device->Magic != CT_STORAGE_DEVICE_MAGIC)
        return ESP_ERR_INVALID_ARG;
    if (__atomic_load_n(&device->DirectMounts, __ATOMIC_ACQUIRE) != 0)
        return ESP_ERR_INVALID_STATE;
    const uint32_t remaining = __atomic_sub_fetch(&device->References, 1u,
        __ATOMIC_ACQ_REL);
    if (remaining != 0) return ESP_OK;
    esp_err_t result = ESP_OK;
    if (device->OwnsBlock && device->Block != NULL && device->Block->ops->release != NULL)
        result = device->Block->ops->release(device->Block);
    ct_device *parent = device->Parent;
    ct_card *card = device->Card;
    device->Magic = 0;
    free(device);
    if (parent != NULL) {
        esp_err_t parent_result = release_device_internal(parent);
        if (result == ESP_OK) result = parent_result;
    }
    if (card != NULL)
        (void)__atomic_sub_fetch(&card->References, 1u, __ATOMIC_ACQ_REL);
    return result;
}

static bool valid_range(const ct_device *device, uint64_t offset, size_t length,
    size_t alignment)
{
    if (alignment == 0 || offset % alignment != 0 || length % alignment != 0)
        return false;
    return offset <= device->Block->geometry.disk_size &&
        length <= device->Block->geometry.disk_size - offset;
}

static bool valid_mount_path(const char *path)
{
    if (path == NULL || path[0] != '/') return false;
    const size_t length = strlen(path);
    if (length == 0 || length > ESP_VFS_PATH_MAX ||
        (length > 1 && path[length - 1] == '/')) return false;
    const char *part = path + 1;
    while (*part != '\0') {
        const char *slash = strchr(part, '/');
        const size_t count = slash == NULL ? strlen(part) : (size_t)(slash - part);
        if (count == 0 || (count == 1 && part[0] == '.') ||
            (count == 2 && part[0] == '.' && part[1] == '.')) return false;
        if (slash == NULL) break;
        part = slash + 1;
    }
    return true;
}

int32_t ct_storage_sdspi_open(ct_storage_sdspi_configuration configuration,
    uintptr_t *card_out, uintptr_t *device_out, ct_storage_sd_card_info *info)
{
    if (card_out == NULL || device_out == NULL || info == NULL) return -EINVAL;
    *card_out = 0; *device_out = 0; *info = (ct_storage_sd_card_info){0};
    if (configuration.Host != 2 || configuration.FrequencyKilohertz == 0 ||
        configuration.FrequencyKilohertz > 40000u) return -EINVAL;
    ct_card *card = calloc(1, sizeof(*card));
    ct_device *device = calloc(1, sizeof(*device));
    if (card == NULL || device == NULL) { free(card); free(device); return -ENOMEM; }
    card->Magic = CT_STORAGE_CARD_MAGIC;
    card->Host = SPI2_HOST;
    card->References = 1;
    spi_bus_config_t bus = {
        .mosi_io_num = configuration.Mosi,
        .miso_io_num = configuration.Miso,
        .sclk_io_num = configuration.Clock,
        .quadwp_io_num = -1,
        .quadhd_io_num = -1,
        .max_transfer_sz = 4096,
    };
    esp_err_t result = spi_bus_initialize(card->Host, &bus, SPI_DMA_CH_AUTO);
    if (result != ESP_OK) goto fail;
    card->HostInitialized = true;
    result = sdspi_host_init();
    if (result != ESP_OK) goto fail;
    sdspi_device_config_t slot = SDSPI_DEVICE_CONFIG_DEFAULT();
    slot.host_id = card->Host;
    slot.gpio_cs = configuration.ChipSelect;
    result = sdspi_host_init_device(&slot, &card->Device);
    if (result != ESP_OK) goto fail;
    card->DeviceInitialized = true;
    sdmmc_host_t host = SDSPI_HOST_DEFAULT();
    host.slot = card->Device;
    host.max_freq_khz = configuration.FrequencyKilohertz;
    result = sdmmc_card_init(&host, &card->Card);
    if (result != ESP_OK) goto fail;
    esp_blockdev_handle_t block = NULL;
    result = sdmmc_get_blockdev(&card->Card, &block);
    if (result != ESP_OK) goto fail;
    device->Magic = CT_STORAGE_DEVICE_MAGIC;
    device->Block = block;
    device->Card = card;
    device->References = 1;
    device->OwnsBlock = true;
    retain_card(card);
    int real_frequency = 0;
    (void)sdspi_host_get_real_freq(card->Device, &real_frequency);
    info->CapacityBytes = block->geometry.disk_size;
    info->SectorSize = (uint32_t)card->Card.csd.sector_size;
    info->FrequencyKilohertz = real_frequency > 0 ? (uint32_t)real_frequency : configuration.FrequencyKilohertz;
    info->ManufacturerId = card->Card.cid.mfg_id;
    info->OemId = card->Card.cid.oem_id;
    info->ProductRevision = card->Card.cid.revision;
    info->SerialNumber = (uint32_t)card->Card.cid.serial;
    info->ManufacturingDate = card->Card.cid.date;
    info->IsHighCapacity = (card->Card.ocr & SD_OCR_SDHC_CAP) != 0;
    *card_out = (uintptr_t)card;
    *device_out = (uintptr_t)device;
    return 0;
fail:
    if (card->DeviceInitialized) (void)sdspi_host_remove_device(card->Device);
    (void)sdspi_host_deinit();
    if (card->HostInitialized) (void)spi_bus_free(card->Host);
    free(device); free(card);
    return neg(result);
}

int32_t ct_storage_sdspi_status(uintptr_t handle)
{
    ct_card *card = card_from(handle);
    return card == NULL ? -EINVAL : neg(sdmmc_get_status(&card->Card));
}

int32_t ct_storage_sdspi_close(uintptr_t handle)
{
    ct_card *card = card_from(handle);
    if (card == NULL) return -EINVAL;
    if (__atomic_load_n(&card->References, __ATOMIC_ACQUIRE) != 1u)
        return -EBUSY;
    esp_err_t result = ESP_OK;
    if (card->DeviceInitialized) result = sdspi_host_remove_device(card->Device);
    esp_err_t deinit = sdspi_host_deinit();
    if (result == ESP_OK && deinit != ESP_OK) result = deinit;
    if (card->HostInitialized) {
        esp_err_t bus = spi_bus_free(card->Host);
        if (result == ESP_OK && bus != ESP_OK) result = bus;
    }
    card->Magic = 0;
    free(card);
    if (result != ESP_OK)
        ESP_LOGE(TAG, "card resources were released with cleanup error %s", esp_err_to_name(result));
    /* Once the handle is consumed there is nothing safe for the caller to
       retry. Report success and retain cleanup failures in the native log. */
    return 0;
}

int32_t ct_storage_block_info(uintptr_t handle, ct_storage_block_info_t *info)
{
    ct_device *device = device_from(handle);
    if (device == NULL || info == NULL) return -EINVAL;
    const esp_blockdev_geometry_t *g = &device->Block->geometry;
    *info = (ct_storage_block_info_t){
        .Length = g->disk_size, .ReadAlignment = g->read_size,
        .WriteAlignment = g->write_size, .EraseAlignment = g->erase_size,
        .PreferredReadSize = g->recommended_read_size,
        .PreferredWriteSize = g->recommended_write_size,
        .PreferredEraseSize = g->recommended_erase_size,
        .IsReadOnly = device->Block->device_flags.read_only,
    };
    return 0;
}

int32_t ct_storage_block_read(uintptr_t handle, uint64_t offset,
    uint8_t *destination, size_t length)
{
    ct_device *device = device_from(handle);
    if (device == NULL || (length != 0 && destination == NULL)) return -EINVAL;
    if (device_raw_io_is_busy(device)) return -EBUSY;
    if (!valid_range(device, offset, length, device->Block->geometry.read_size)) return -EINVAL;
    if (length == 0) return 0;
    return neg(device->Block->ops->read(device->Block, destination, length, offset, length));
}

int32_t ct_storage_block_write(uintptr_t handle, uint64_t offset,
    const uint8_t *source, size_t length)
{
    ct_device *device = device_from(handle);
    if (device == NULL || (length != 0 && source == NULL)) return -EINVAL;
    if (device->Block->device_flags.read_only) return -EROFS;
    if (device_raw_io_is_busy(device)) return -EBUSY;
    if (!valid_range(device, offset, length, device->Block->geometry.write_size)) return -EINVAL;
    if (length == 0) return 0;
    return neg(device->Block->ops->write(device->Block, source, offset, length));
}

int32_t ct_storage_block_erase(uintptr_t handle, uint64_t offset, size_t length)
{
    ct_device *device = device_from(handle);
    if (device == NULL) return -EINVAL;
    if (device->Block->device_flags.read_only) return -EROFS;
    if (device_raw_io_is_busy(device)) return -EBUSY;
    if (!valid_range(device, offset, length, device->Block->geometry.erase_size)) return -EINVAL;
    if (length == 0) return 0;
    return neg(device->Block->ops->erase(device->Block, offset, length));
}

int32_t ct_storage_block_flush(uintptr_t handle)
{
    ct_device *device = device_from(handle);
    if (device == NULL) return -EINVAL;
    if (device_raw_io_is_busy(device)) return -EBUSY;
    return device->Block->ops->sync == NULL ? 0 : neg(device->Block->ops->sync(device->Block));
}

int32_t ct_storage_block_slice(uintptr_t handle, uint64_t offset, uint64_t length,
    uintptr_t *result_out)
{
    ct_device *parent = device_from(handle);
    if (parent == NULL || result_out == NULL || offset > SIZE_MAX || length > SIZE_MAX)
        return -EINVAL;
    *result_out = 0;
    if (device_raw_io_is_busy(parent)) return -EBUSY;
    const size_t alignment = parent->Block->geometry.read_size;
    if (!valid_range(parent, offset, (size_t)length, alignment)) return -EINVAL;
    esp_blockdev_handle_t block = NULL;
    esp_err_t result = esp_blockdev_generic_partition_get(parent->Block,
        (size_t)offset, (size_t)length, &block);
    if (result != ESP_OK) return neg(result);
    ct_device *child = calloc(1, sizeof(*child));
    if (child == NULL) { (void)block->ops->release(block); return -ENOMEM; }
    child->Magic = CT_STORAGE_DEVICE_MAGIC;
    child->Block = block;
    child->Parent = parent;
    child->References = 1;
    child->OwnsBlock = true;
    retain_device(parent);
    *result_out = (uintptr_t)child;
    return 0;
}

int32_t ct_storage_block_release(uintptr_t handle)
{
    ct_device *device = device_from(handle);
    if (device == NULL) return -EINVAL;
    const uint32_t references = __atomic_load_n(&device->References, __ATOMIC_ACQUIRE);
    esp_err_t result = release_device_internal(device);
    if (result != ESP_OK && references == 1u) {
        ESP_LOGE(TAG, "block device was released with cleanup error %s", esp_err_to_name(result));
        return 0;
    }
    return neg(result);
}

static DSTATUS ct_bdl_disk_init(BYTE drive) { return drive < FF_VOLUMES && s_disks[drive].Device != NULL ? 0 : STA_NOINIT; }
static DSTATUS ct_bdl_disk_status(BYTE drive) { return ct_bdl_disk_init(drive); }

static DRESULT ct_bdl_disk_read(BYTE drive, BYTE *buffer, DWORD sector, UINT count)
{
    if (drive >= FF_VOLUMES || s_disks[drive].Device == NULL || buffer == NULL || count == 0) return RES_PARERR;
    ct_device *device = s_disks[drive].Device;
    const size_t size = device->Block->geometry.read_size;
    if ((uint64_t)sector * size > device->Block->geometry.disk_size ||
        (uint64_t)count * size > device->Block->geometry.disk_size - (uint64_t)sector * size) return RES_PARERR;
    return device->Block->ops->read(device->Block, buffer, (size_t)count * size,
        (uint64_t)sector * size, (size_t)count * size) == ESP_OK ? RES_OK : RES_ERROR;
}

static DRESULT ct_bdl_disk_write(BYTE drive, const BYTE *buffer, DWORD sector, UINT count)
{
    if (drive >= FF_VOLUMES || s_disks[drive].Device == NULL || buffer == NULL || count == 0) return RES_PARERR;
    ct_device *device = s_disks[drive].Device;
    if (device->Block->device_flags.read_only) return RES_WRPRT;
    const size_t size = device->Block->geometry.write_size;
    if ((uint64_t)sector * size > device->Block->geometry.disk_size ||
        (uint64_t)count * size > device->Block->geometry.disk_size - (uint64_t)sector * size) return RES_PARERR;
    return device->Block->ops->write(device->Block, buffer, (uint64_t)sector * size,
        (size_t)count * size) == ESP_OK ? RES_OK : RES_ERROR;
}

static DRESULT ct_bdl_disk_ioctl(BYTE drive, BYTE command, void *buffer)
{
    if (drive >= FF_VOLUMES || s_disks[drive].Device == NULL) return RES_PARERR;
    ct_device *device = s_disks[drive].Device;
    const esp_blockdev_geometry_t *g = &device->Block->geometry;
    if (command == CTRL_SYNC)
        return device->Block->ops->sync == NULL || device->Block->ops->sync(device->Block) == ESP_OK ? RES_OK : RES_ERROR;
    if (buffer == NULL) return RES_PARERR;
    if (command == GET_SECTOR_COUNT) { *(LBA_t *)buffer = (LBA_t)(g->disk_size / g->read_size); return RES_OK; }
    if (command == GET_SECTOR_SIZE) { *(WORD *)buffer = (WORD)g->read_size; return RES_OK; }
    if (command == GET_BLOCK_SIZE) { *(DWORD *)buffer = (DWORD)(g->erase_size / g->read_size); return RES_OK; }
    return RES_PARERR;
}

static const ff_diskio_impl_t s_disk_impl = {
    .init = ct_bdl_disk_init, .status = ct_bdl_disk_status, .read = ct_bdl_disk_read,
    .write = ct_bdl_disk_write, .ioctl = ct_bdl_disk_ioctl,
};

static int32_t attach_disk(ct_device *device, BYTE *drive, char name[4])
{
    esp_err_t result = ff_diskio_get_drive(drive);
    if (result != ESP_OK) return neg(result);
    if (*drive >= FF_VOLUMES || *drive > 9) return -ENOSPC;
    s_disks[*drive].Device = device;
    ff_diskio_register(*drive, &s_disk_impl);
    name[0] = (char)('0' + *drive); name[1] = ':'; name[2] = '\0'; name[3] = '\0';
    return 0;
}

static void detach_disk(BYTE drive)
{
    if (drive >= FF_VOLUMES) return;
    ff_diskio_unregister(drive);
    s_disks[drive].Device = NULL;
}

int32_t ct_storage_fat_mount(const char *path, uintptr_t device_handle,
    int32_t maximum_open_files, uintptr_t *mount_out)
{
    ct_device *device = device_from(device_handle);
    if (device == NULL || mount_out == NULL || maximum_open_files <= 0 ||
        !valid_mount_path(path)) return -EINVAL;
    *mount_out = 0;
    if (__atomic_load_n(&device->Mounts, __ATOMIC_ACQUIRE) != 0 ||
        device_has_mounted_ancestor(device)) return -EBUSY;
    ct_mount *mount = calloc(1, sizeof(*mount));
    if (mount == NULL) return -ENOMEM;
    mount->Drive = FF_DRV_NOT_USED;
    int32_t result = attach_disk(device, &mount->Drive, mount->DriveName);
    if (result != 0) { free(mount); return result; }
    esp_vfs_fat_conf_t config = {
        .base_path = path, .fat_drive = mount->DriveName,
        .max_files = (size_t)maximum_open_files,
    };
    esp_err_t esp_result = esp_vfs_fat_register(&config, &mount->FileSystem);
    if (esp_result != ESP_OK) { detach_disk(mount->Drive); free(mount); return neg(esp_result); }
    FRESULT fat_result = f_mount(mount->FileSystem, mount->DriveName, 1);
    if (fat_result != FR_OK) {
        /* f_mount registers the FATFS object even when immediate volume
           discovery fails. Remove it before esp_vfs_fat_unregister_path
           releases the object, otherwise FatFs retains a dangling pointer
           and the next mount attempts to free state through released memory. */
        (void)f_mount(NULL, mount->DriveName, 0);
        (void)esp_vfs_fat_unregister_path(path);
        detach_disk(mount->Drive); free(mount); return -(int32_t)(0x7000 + fat_result);
    }
    mount->Magic = CT_STORAGE_MOUNT_MAGIC;
    mount->Device = device;
    (void)snprintf(mount->Path, sizeof(mount->Path), "%s", path);
    mount->Generation = 1;
    mount->Available = true;
    retain_device(device);
    (void)__atomic_add_fetch(&device->DirectMounts, 1u, __ATOMIC_ACQ_REL);
    adjust_mount_count(device, true);
    *mount_out = (uintptr_t)mount;
    return 0;
}

int32_t ct_storage_fat_unmount(uintptr_t handle)
{
    ct_mount *mount = mount_from(handle);
    if (mount == NULL) return -EINVAL;
    mount->Available = false;
    FRESULT fat_result = f_mount(NULL, mount->DriveName, 0);
    esp_err_t vfs_result = esp_vfs_fat_unregister_path(mount->Path);
    detach_disk(mount->Drive);
    (void)__atomic_sub_fetch(&mount->Device->DirectMounts, 1u, __ATOMIC_ACQ_REL);
    adjust_mount_count(mount->Device, false);
    esp_err_t release_result = release_device_internal(mount->Device);
    mount->Magic = 0;
    free(mount);
    if (fat_result != FR_OK)
        ESP_LOGE(TAG, "FAT volume was released with unmount error %d", (int)fat_result);
    if (vfs_result != ESP_OK)
        ESP_LOGE(TAG, "VFS prefix was released with cleanup error %s", esp_err_to_name(vfs_result));
    if (release_result != ESP_OK)
        ESP_LOGE(TAG, "mounted device was released with cleanup error %s", esp_err_to_name(release_result));
    /* The mount handle is consumed even if a cleanup step reports an error;
       returning an error would invite an unsafe retry through freed storage. */
    return 0;
}

int32_t ct_storage_fat_volume_info(uintptr_t handle, ct_storage_fat_volume_info_t *info)
{
    ct_mount *mount = mount_from(handle);
    if (mount == NULL || info == NULL || !mount->Available) return -EINVAL;
    DWORD free_clusters = 0;
    FATFS *fs = NULL;
    FRESULT result = f_getfree(mount->DriveName, &free_clusters, &fs);
    if (result != FR_OK || fs == NULL) return -(int32_t)(0x7000 + result);
#if FF_MAX_SS == FF_MIN_SS
    const uint32_t sector_size = (uint32_t)FF_MAX_SS;
#else
    const uint32_t sector_size = (uint32_t)fs->ssize;
#endif
    const uint64_t cluster_bytes = (uint64_t)fs->csize * sector_size;
    *info = (ct_storage_fat_volume_info_t){
        .CapacityBytes = (uint64_t)(fs->n_fatent - 2u) * cluster_bytes,
        .FreeBytes = (uint64_t)free_clusters * cluster_bytes,
        .SectorSize = sector_size, .ClusterSize = (uint32_t)cluster_bytes,
    };
    return 0;
}

uint64_t ct_storage_fat_generation(uintptr_t handle)
{
    ct_mount *mount = mount_from(handle);
    return mount == NULL ? 0 : __atomic_load_n(&mount->Generation, __ATOMIC_ACQUIRE);
}

bool ct_storage_fat_is_available(uintptr_t handle)
{
    ct_mount *mount = mount_from(handle);
    return mount != NULL && __atomic_load_n(&mount->Available, __ATOMIC_ACQUIRE);
}

int32_t ct_storage_fat_format(uintptr_t device_handle, uint8_t kind,
    uint32_t allocation_unit_bytes, uint8_t fat_copies)
{
    ct_device *device = device_from(device_handle);
    if (device == NULL || device_raw_io_is_busy(device) ||
        fat_copies == 0 || fat_copies > 2 || kind > 3) return -EINVAL;
    BYTE drive = FF_DRV_NOT_USED;
    char name[4];
    int32_t result = attach_disk(device, &drive, name);
    if (result != 0) return result;
    MKFS_PARM options = {
        .fmt = kind == 3 ? FM_FAT32 : kind == 0 ? (FM_FAT | FM_FAT32) : FM_FAT,
        .n_fat = fat_copies, .align = 0, .n_root = 0,
        .au_size = allocation_unit_bytes,
    };
    const size_t work_size = 4096;
    void *work = malloc(work_size);
    if (work == NULL) { detach_disk(drive); return -ENOMEM; }
    FRESULT fat_result = f_mkfs(name, &options, work, work_size);
    free(work);
    if (fat_result == FR_OK && kind != 0) {
        FATFS probe = {0};
        fat_result = f_mount(&probe, name, 1);
        if (fat_result == FR_OK) {
            const BYTE expected = kind == 1 ? FS_FAT12 : kind == 2 ? FS_FAT16 : FS_FAT32;
            if (probe.fs_type != expected) fat_result = FR_INVALID_PARAMETER;
        }
        /* Registration happens before the optional immediate mount. Always
           unregister the stack probe, including failed verification. */
        FRESULT unmount_result = f_mount(NULL, name, 0);
        if (fat_result == FR_OK) fat_result = unmount_result;
    }
    detach_disk(drive);
    if (fat_result == FR_OK && device->Block->ops->sync != NULL)
        return neg(device->Block->ops->sync(device->Block));
    return fat_result == FR_OK ? 0 : -(int32_t)(0x7000 + fat_result);
}

static uint32_t read_le32(const uint8_t *value)
{
    return (uint32_t)value[0] | (uint32_t)value[1] << 8 |
        (uint32_t)value[2] << 16 | (uint32_t)value[3] << 24;
}

static int32_t read_mbr_partition(ct_device *device, int32_t selected,
    uint64_t *offset_out, uint64_t *length_out)
{
    uint8_t sector[512];
    esp_err_t read_result = device->Block->ops->read(device->Block, sector,
        sizeof(sector), 0, sizeof(sector));
    if (read_result != ESP_OK) return neg(read_result);
    if (sector[510] != 0x55 || sector[511] != 0xaa) return -ENOEXEC;
    uint32_t starts[4] = {0};
    uint32_t counts[4] = {0};
    uint32_t bootable = 0;
    const uint64_t device_sectors = device->Block->geometry.disk_size / 512u;
    for (int32_t index = 0; index < 4; ++index) {
        const int entry = 446 + index * 16;
        const uint8_t active = sector[entry];
        const uint8_t type = sector[entry + 4];
        starts[index] = read_le32(sector + entry + 8);
        counts[index] = read_le32(sector + entry + 12);
        if (active != 0 && active != 0x80) return -ENOEXEC;
        if (active == 0x80 && ++bootable > 1) return -ENOEXEC;
        if (type == 5 || type == 15 || type == 0xee) return -ENOTSUP;
        if (type == 0) {
            if (active != 0 || starts[index] != 0 || counts[index] != 0) return -ENOEXEC;
            continue;
        }
        const uint64_t end = (uint64_t)starts[index] + counts[index];
        if (starts[index] == 0 || counts[index] == 0 || end > device_sectors)
            return -ENOEXEC;
        for (int32_t prior = 0; prior < index; ++prior) {
            if (counts[prior] == 0) continue;
            const uint64_t prior_end = (uint64_t)starts[prior] + counts[prior];
            if ((uint64_t)starts[index] < prior_end &&
                (uint64_t)starts[prior] < end) return -ENOEXEC;
        }
    }
    if (counts[selected] == 0) return -ENOENT;
    *offset_out = (uint64_t)starts[selected] * 512u;
    *length_out = (uint64_t)counts[selected] * 512u;
    return 0;
}

static int32_t monitor_device_for_mapping(ct_monitor *monitor,
    ct_monitor_mapping *mapping)
{
    if (mapping->PartitionIndex < 0) {
        retain_device(monitor->Root);
        mapping->Device = monitor->Root;
        return 0;
    }
    uint64_t start = 0, length = 0;
    int32_t result = read_mbr_partition(monitor->Root, mapping->PartitionIndex,
        &start, &length);
    if (result != 0) return result;
    uintptr_t slice = 0;
    result = ct_storage_block_slice((uintptr_t)monitor->Root, start, length, &slice);
    if (result == 0) mapping->Device = device_from(slice);
    return result;
}

static void monitor_set_state(ct_monitor *monitor, ct_storage_monitor_state_t state,
    int32_t error)
{
    const ct_storage_monitor_state_t previous = __atomic_exchange_n(&monitor->State,
        state, __ATOMIC_ACQ_REL);
    __atomic_store_n(&monitor->LastError, error, __ATOMIC_RELEASE);
    if (previous != state)
        ESP_LOGI(TAG, "SD state %d -> %d (%" PRId32 ")", (int)previous, (int)state, error);
}

static void monitor_unmount(ct_monitor *monitor)
{
    monitor_set_state(monitor, CT_STORAGE_REMOVING, 0);
    xSemaphoreTake(monitor->AppendGate, portMAX_DELAY);
    const uint64_t generation = __atomic_add_fetch(&monitor->Generation, 1u,
        __ATOMIC_ACQ_REL);
    for (size_t index = 0; index < monitor->MappingCount; ++index)
        ctilde_managed_storage_invalidate_prefix(monitor->Mappings[index].Path, generation);
    for (size_t index = monitor->MappingCount; index > 0; --index) {
        ct_monitor_mapping *mapping = &monitor->Mappings[index - 1];
        if (mapping->Mount != NULL) {
            (void)ct_storage_fat_unmount((uintptr_t)mapping->Mount);
            mapping->Mount = NULL;
        }
        if (mapping->Device != NULL) {
            (void)release_device_internal(mapping->Device);
            mapping->Device = NULL;
        }
    }
    if (monitor->Root != NULL) {
        (void)release_device_internal(monitor->Root);
        monitor->Root = NULL;
    }
    if (monitor->Card != NULL) {
        (void)ct_storage_sdspi_close((uintptr_t)monitor->Card);
        monitor->Card = NULL;
    }
    __atomic_store_n(&monitor->CardInfoAvailable, false, __ATOMIC_RELEASE);
    xSemaphoreGive(monitor->AppendGate);
}

static int32_t monitor_mount(ct_monitor *monitor)
{
    monitor_set_state(monitor, CT_STORAGE_MOUNTING, 0);
    uintptr_t card = 0, device = 0;
    ct_storage_sd_card_info info;
    int32_t result = ct_storage_sdspi_open(monitor->Configuration, &card, &device, &info);
    if (result != 0) return result;
    monitor->Card = card_from(card);
    monitor->Root = device_from(device);
    monitor->CardInfo = info;
    __atomic_store_n(&monitor->CardInfoAvailable, true, __ATOMIC_RELEASE);
    for (size_t index = 0; index < monitor->MappingCount; ++index) {
        ct_monitor_mapping *mapping = &monitor->Mappings[index];
        result = monitor_device_for_mapping(monitor, mapping);
        if (result != 0) { monitor_unmount(monitor); return result; }
    }
    /* Materialize every slice before mounting any one of them. Once a child is
       mounted, raw access through the parent is deliberately blocked. */
    for (size_t index = 0; index < monitor->MappingCount; ++index) {
        ct_monitor_mapping *mapping = &monitor->Mappings[index];
        uintptr_t mount = 0;
        result = ct_storage_fat_mount(mapping->Path, (uintptr_t)mapping->Device,
            mapping->MaximumOpenFiles, &mount);
        if (result != 0) { monitor_unmount(monitor); return result; }
        mapping->Mount = mount_from(mount);
        mapping->Mount->Generation = __atomic_add_fetch(&monitor->Generation, 1u,
            __ATOMIC_ACQ_REL);
    }
    monitor_set_state(monitor, CT_STORAGE_MOUNTED, 0);
    return 0;
}

static void monitor_task(void *argument)
{
    ct_monitor *monitor = argument;
    while (!__atomic_load_n(&monitor->StopRequested, __ATOMIC_ACQUIRE)) {
        if (monitor->Card == NULL) {
            const bool remount = __atomic_exchange_n(&monitor->RemountRequested, false,
                __ATOMIC_ACQ_REL);
            if (!remount && __atomic_load_n(&monitor->State, __ATOMIC_ACQUIRE) ==
                CT_STORAGE_FAULTED) {
                vTaskDelay(pdMS_TO_TICKS(500));
                continue;
            }
            const int32_t result = monitor_mount(monitor);
            if (result != 0) monitor_set_state(monitor,
                result == -ESP_ERR_TIMEOUT || result == -ESP_ERR_NOT_FOUND ? CT_STORAGE_NOT_PRESENT : CT_STORAGE_FAULTED,
                result);
            vTaskDelay(pdMS_TO_TICKS(1000));
            continue;
        }
        const bool remount = __atomic_exchange_n(&monitor->RemountRequested, false,
            __ATOMIC_ACQ_REL);
        const int32_t status = ct_storage_sdspi_status((uintptr_t)monitor->Card);
        if (remount || status != 0) {
            monitor_unmount(monitor);
            monitor_set_state(monitor, CT_STORAGE_NOT_PRESENT, status);
            continue;
        }
        vTaskDelay(pdMS_TO_TICKS(500));
    }
    if (monitor->Card != NULL) monitor_unmount(monitor);
    monitor_set_state(monitor, CT_STORAGE_NOT_PRESENT, 0);
    xSemaphoreGive(monitor->Stopped);
    vTaskDelete(NULL);
}

int32_t ct_storage_monitor_create(ct_storage_sdspi_configuration configuration,
    uintptr_t *monitor_out)
{
    if (monitor_out == NULL) return -EINVAL;
    *monitor_out = 0;
    ct_monitor *monitor = calloc(1, sizeof(*monitor));
    if (monitor == NULL) return -ENOMEM;
    monitor->Magic = CT_STORAGE_MONITOR_MAGIC;
    monitor->Configuration = configuration;
    monitor->State = CT_STORAGE_NOT_PRESENT;
    monitor->Stopped = xSemaphoreCreateBinaryStatic(&monitor->StoppedStorage);
    monitor->AppendGate = xSemaphoreCreateMutexStatic(&monitor->AppendGateStorage);
    if (monitor->Stopped == NULL) { free(monitor); return -ENOMEM; }
    *monitor_out = (uintptr_t)monitor;
    return 0;
}

int32_t ct_storage_monitor_add_fat(uintptr_t handle, int32_t partition_index,
    const char *path, int32_t maximum_open_files)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL || monitor->Task != NULL || monitor->MappingCount >= CT_STORAGE_MAX_MOUNTS ||
        partition_index < -1 || partition_index > 3 || maximum_open_files <= 0 ||
        !valid_mount_path(path)) return -EINVAL;
    for (size_t index = 0; index < monitor->MappingCount; ++index)
        if (strcmp(monitor->Mappings[index].Path, path) == 0) return -EEXIST;
    ct_monitor_mapping *mapping = &monitor->Mappings[monitor->MappingCount++];
    mapping->PartitionIndex = partition_index;
    mapping->MaximumOpenFiles = maximum_open_files;
    (void)snprintf(mapping->Path, sizeof(mapping->Path), "%s", path);
    return 0;
}

int32_t ct_storage_monitor_start(uintptr_t handle)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL || monitor->Task != NULL || monitor->MappingCount == 0) return -EINVAL;
    return xTaskCreate(monitor_task, "ct_sd_monitor", 6144, monitor,
        tskIDLE_PRIORITY + 2, &monitor->Task) == pdPASS ? 0 : -ENOMEM;
}

ct_storage_monitor_state_t ct_storage_monitor_state(uintptr_t handle)
{
    ct_monitor *monitor = monitor_from(handle);
    return monitor == NULL ? CT_STORAGE_FAULTED : __atomic_load_n(&monitor->State, __ATOMIC_ACQUIRE);
}

int32_t ct_storage_monitor_append_run_log(uintptr_t handle, const char *data, size_t length)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL || data == NULL) return -EINVAL;
    xSemaphoreTake(monitor->AppendGate, portMAX_DELAY);
    int32_t result = -ENODEV;
    if (ct_storage_monitor_state(handle) == CT_STORAGE_MOUNTED) {
        bool sd_mounted = false;
        for (size_t index = 0; index < monitor->MappingCount; ++index)
            if (strcmp(monitor->Mappings[index].Path, "/sd") == 0) sd_mounted = true;
        const int fd = sd_mounted ? open("/sd/run.log", O_WRONLY | O_CREAT | O_APPEND, 0666) : -1;
        if (fd >= 0) {
            size_t written = 0;
            while (written < length) {
                const ssize_t count = write(fd, data + written, length - written);
                if (count < 0 && errno == EINTR) continue;
                if (count <= 0) break;
                written += (size_t)count;
            }
            result = written == length ? 0 : -EIO;
            if (close(fd) != 0) result = -EIO;
        }
    }
    xSemaphoreGive(monitor->AppendGate);
    return result;
}

uint64_t ct_storage_monitor_generation(uintptr_t handle)
{
    ct_monitor *monitor = monitor_from(handle);
    return monitor == NULL ? 0 : __atomic_load_n(&monitor->Generation, __ATOMIC_ACQUIRE);
}

int32_t ct_storage_monitor_last_error(uintptr_t handle)
{
    ct_monitor *monitor = monitor_from(handle);
    return monitor == NULL ? -EINVAL : __atomic_load_n(&monitor->LastError, __ATOMIC_ACQUIRE);
}

int32_t ct_storage_monitor_remount(uintptr_t handle)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL || monitor->Task == NULL) return -EINVAL;
    __atomic_store_n(&monitor->RemountRequested, true, __ATOMIC_RELEASE);
    return 0;
}

int32_t ct_storage_monitor_volume_info(uintptr_t handle, int32_t mapping_index,
    ct_storage_fat_volume_info_t *info)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL || info == NULL || mapping_index < 0 ||
        (size_t)mapping_index >= monitor->MappingCount) return -EINVAL;
    ct_mount *mount = monitor->Mappings[mapping_index].Mount;
    return mount == NULL ? -ENODEV : ct_storage_fat_volume_info((uintptr_t)mount, info);
}

int32_t ct_storage_monitor_card_info(uintptr_t handle,
    ct_storage_sd_card_info *info)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL || info == NULL) return -EINVAL;
    if (!__atomic_load_n(&monitor->CardInfoAvailable, __ATOMIC_ACQUIRE))
        return -ENODEV;
    *info = monitor->CardInfo;
    return 0;
}

int32_t ct_storage_monitor_release(uintptr_t handle)
{
    ct_monitor *monitor = monitor_from(handle);
    if (monitor == NULL) return -EINVAL;
    if (monitor->Task != NULL) {
        __atomic_store_n(&monitor->StopRequested, true, __ATOMIC_RELEASE);
        if (xSemaphoreTake(monitor->Stopped, pdMS_TO_TICKS(3000)) != pdTRUE) return -ETIMEDOUT;
    }
    vSemaphoreDelete(monitor->Stopped);
    vSemaphoreDelete(monitor->AppendGate);
    monitor->Magic = 0;
    free(monitor);
    return 0;
}
