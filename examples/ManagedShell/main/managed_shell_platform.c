#include <inttypes.h>
#include <stdio.h>

#include "ctilde_managed_runtime.h"
#include "ctilde_esp_shim.h"
#include "esp_heap_caps.h"
#include "esp_littlefs.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "managed_diagnostics_host.h"
#include "managed_network_host.h"
#include "managed_shell_host.h"
#include "managed_ssh_host.h"
#include "managed_storage_host.h"

#define CTILDE_SHELL_TLS_INDEX 1

extern void ct_managed_shell_input_activity(void);

static_assert(CTILDE_SHELL_TLS_INDEX < CONFIG_FREERTOS_THREAD_LOCAL_STORAGE_POINTERS,
    "The managed shell requires a FreeRTOS TLS slot for firmware C~ state");

void *ct_esp_thread_state_get(void)
{
    return pvTaskGetThreadLocalStoragePointer(NULL, CTILDE_SHELL_TLS_INDEX);
}

void ct_esp_thread_state_set(void *state, ct_esp_thread_state_delete_fn delete_callback)
{
    vTaskSetThreadLocalStoragePointerAndDelCallback(NULL, CTILDE_SHELL_TLS_INDEX, state,
        state == NULL ? NULL : delete_callback);
}

int32_t ct_managed_shell_initialize(void)
{
    esp_log_level_set("ELF", ESP_LOG_WARN);
    esp_log_level_set("DLMOD", ESP_LOG_WARN);
    const esp_vfs_littlefs_conf_t configuration = {
        .base_path = "/storage",
        .partition_label = "storage",
        .format_if_mount_failed = true,
        .dont_mount = false,
    };
    const esp_err_t mount_result = esp_vfs_littlefs_register(&configuration);
    if (mount_result != ESP_OK) {
        printf("LittleFS mount failed: %s\n", esp_err_to_name(mount_result));
        return (int32_t)mount_result;
    }
    const esp_vfs_littlefs_conf_t sftp_configuration = {
        .base_path = "/sftp",
        .partition_label = "sftp",
        .format_if_mount_failed = true,
        .dont_mount = false,
    };
    const esp_err_t sftp_mount_result = esp_vfs_littlefs_register(&sftp_configuration);
    if (sftp_mount_result != ESP_OK) {
        printf("SFTP LittleFS mount failed: %s\n", esp_err_to_name(sftp_mount_result));
        return (int32_t)sftp_mount_result;
    }
    const int runtime_result = ctilde_managed_runtime_initialize();
    if (runtime_result != 0) {
        printf("Managed runtime initialization failed: %d\n", runtime_result);
        return runtime_result;
    }
    const int diagnostics_result = ct_managed_diagnostics_host_initialize();
    if (diagnostics_result != 0) {
        printf("Managed diagnostics initialization failed: %d\n", diagnostics_result);
        return diagnostics_result;
    }
    const int shell_host_result = ct_managed_shell_host_initialize();
    if (shell_host_result != 0) {
        printf("Managed shell host initialization failed: %d\n", shell_host_result);
        return shell_host_result;
    }
    ctilde_managed_console_set_uart_activity_hook(ct_managed_shell_input_activity);
    const int storage_result = ct_managed_storage_host_initialize();
    if (storage_result != 0) {
        printf("Managed storage initialization failed: %d\n", storage_result);
        return storage_result;
    }
    const int network_result = ct_managed_network_host_initialize();
    if (network_result != 0)
        printf("Managed network initialization deferred: %d\n", network_result);
    const int ssh_result = ct_managed_ssh_host_initialize();
    if (ssh_result != 0) {
        printf("Managed SSH host initialization failed: %d\n", ssh_result);
        return ssh_result;
    }
    size_t total = 0u;
    size_t used = 0u;
    if (esp_littlefs_info("storage", &total, &used) == ESP_OK)
        printf("LittleFS mounted at %s (%u/%u bytes used)\n", CTILDE_MANAGED_MODULE_ROOT,
            (unsigned)used, (unsigned)total);
    return 0;
}
