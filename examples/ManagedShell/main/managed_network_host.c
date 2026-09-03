#include "managed_network_host.h"
#include "managed_network_host_api.h"

#include <errno.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "esp_event.h"
#include "esp_elf.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "lwip/inet.h"
#include "nvs_flash.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

static SemaphoreHandle_t s_gate;
static esp_netif_t *s_station;
static bool s_initialized;
static bool s_connected;
static bool s_address_ready;
static int32_t s_last_error;
static wifi_ap_record_t s_scan[CT_MANAGED_NETWORK_MAX_SCAN_RESULTS];
static uint16_t s_scan_count;
static ct_managed_network_status s_status;

static void copy_text(char *destination, size_t capacity, const char *source)
{
    if (capacity == 0u) return;
    if (source == NULL) source = "";
    const size_t length = strnlen(source, capacity - 1u);
    memcpy(destination, source, length);
    destination[length] = '\0';
}

static void refresh_addresses_locked(void)
{
    esp_netif_ip_info_t address = {0};
    if (s_station != NULL && esp_netif_get_ip_info(s_station, &address) == ESP_OK && address.ip.addr != 0u) {
        (void)esp_ip4addr_ntoa(&address.ip, s_status.Address, sizeof(s_status.Address));
        (void)esp_ip4addr_ntoa(&address.gw, s_status.Gateway, sizeof(s_status.Gateway));
        esp_netif_dns_info_t dns = {0};
        if (esp_netif_get_dns_info(s_station, ESP_NETIF_DNS_MAIN, &dns) == ESP_OK)
            (void)esp_ip4addr_ntoa(&dns.ip.u_addr.ip4, s_status.Dns, sizeof(s_status.Dns));
    }
}

static void network_event(void *argument, esp_event_base_t base, int32_t id, void *data)
{
    (void)argument;
    if (s_gate == NULL || xSemaphoreTake(s_gate, portMAX_DELAY) != pdTRUE) return;
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_CONNECTED) s_connected = true;
    else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        s_connected = false;
        s_address_ready = false;
        s_status.Address[0] = '\0';
        s_status.Gateway[0] = '\0';
        s_status.Dns[0] = '\0';
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        (void)data;
        s_connected = true;
        s_address_ready = true;
        refresh_addresses_locked();
    } else if (base == IP_EVENT && id == IP_EVENT_STA_LOST_IP) {
        s_address_ready = false;
        s_status.Address[0] = '\0';
    }
    xSemaphoreGive(s_gate);
}

static int32_t scan_networks(void)
{
    if (!s_initialized) return -ENODEV;
    wifi_scan_config_t configuration = {0};
    esp_err_t result = esp_wifi_scan_start(&configuration, true);
    if (result != ESP_OK) return -(int32_t)result;
    uint16_t count = CT_MANAGED_NETWORK_MAX_SCAN_RESULTS;
    memset(s_scan, 0, sizeof(s_scan));
    result = esp_wifi_scan_get_ap_records(&count, s_scan);
    if (result != ESP_OK) return -(int32_t)result;
    s_scan_count = count;
    return 0;
}

static uint32_t scan_count(void) { return s_scan_count; }
static const char *scan_ssid(uint32_t index)
{
    return index < s_scan_count ? (const char *)s_scan[index].ssid : NULL;
}
static int32_t scan_rssi(uint32_t index) { return index < s_scan_count ? s_scan[index].rssi : INT32_MIN; }
static uint32_t scan_channel(uint32_t index) { return index < s_scan_count ? s_scan[index].primary : 0u; }

static int32_t connect_station(const char *ssid, const char *password, const char *hostname)
{
    if (!s_initialized || ssid == NULL || password == NULL) return -EINVAL;
    const size_t ssid_length = strlen(ssid);
    const size_t password_length = strlen(password);
    if (ssid_length == 0u || ssid_length > 32u || password_length > 63u) return -EINVAL;
    if (hostname != NULL && hostname[0] != '\0') {
        const esp_err_t hostname_result = esp_netif_set_hostname(s_station, hostname);
        if (hostname_result != ESP_OK) return -(int32_t)hostname_result;
    }
    wifi_config_t configuration = {0};
    memcpy(configuration.sta.ssid, ssid, ssid_length);
    memcpy(configuration.sta.password, password, password_length);
    configuration.sta.threshold.authmode = password_length == 0u ? WIFI_AUTH_OPEN : WIFI_AUTH_WPA2_PSK;
    configuration.sta.pmf_cfg.capable = true;
    configuration.sta.pmf_cfg.required = false;
    esp_err_t result = esp_wifi_set_config(WIFI_IF_STA, &configuration);
    if (result == ESP_OK) result = esp_wifi_connect();
    if (result != ESP_OK) s_last_error = -(int32_t)result;
    copy_text(s_status.Ssid, sizeof(s_status.Ssid), ssid);
    return result == ESP_OK ? 0 : -(int32_t)result;
}

static int32_t disconnect_station(void)
{
    if (!s_initialized) return -ENODEV;
    const esp_err_t result = esp_wifi_disconnect();
    return result == ESP_OK || result == ESP_ERR_WIFI_NOT_CONNECT ? 0 : -(int32_t)result;
}

static int32_t status_snapshot(ct_managed_network_status *output)
{
    if (output == NULL) return -EINVAL;
    if (xSemaphoreTake(s_gate, pdMS_TO_TICKS(1000u)) != pdTRUE) return -ETIMEDOUT;
    s_status.Size = sizeof(s_status);
    s_status.Version = CT_MANAGED_NETWORK_HOST_API_VERSION;
    s_status.Initialized = s_initialized;
    s_status.Connected = s_connected;
    s_status.AddressReady = s_address_ready;
    s_status.LastError = s_last_error;
    wifi_ap_record_t access_point = {0};
    s_status.Rssi = esp_wifi_sta_get_ap_info(&access_point) == ESP_OK ? access_point.rssi : 0;
    *output = s_status;
    xSemaphoreGive(s_gate);
    return 0;
}

static int32_t wait_for_address(uint32_t timeout_milliseconds)
{
    const TickType_t start = xTaskGetTickCount();
    const TickType_t timeout = pdMS_TO_TICKS(timeout_milliseconds);
    do {
        if (s_address_ready) return 0;
        vTaskDelay(pdMS_TO_TICKS(25u));
    } while (xTaskGetTickCount() - start < timeout);
    return -ETIMEDOUT;
}

static const char *error_name(int32_t code)
{
    return code < 0 ? esp_err_to_name((esp_err_t)-code) : esp_err_to_name((esp_err_t)code);
}

static const ct_managed_network_host_api_v1 s_api = {
    .Size = sizeof(s_api), .Version = CT_MANAGED_NETWORK_HOST_API_VERSION,
    .Scan = scan_networks, .ScanCount = scan_count, .ScanSsid = scan_ssid,
    .ScanRssi = scan_rssi, .ScanChannel = scan_channel, .Connect = connect_station,
    .Disconnect = disconnect_station, .Status = status_snapshot, .Wait = wait_for_address,
    .ErrorName = error_name,
};

const ct_managed_network_host_api_v1 *ct_managed_network_host_v1(void) { return &s_api; }

static const struct esp_elfsym s_symbols[] = {
    ESP_ELFSYM_EXPORT(ct_managed_network_host_v1),
    ESP_ELFSYM_END
};

int ct_managed_network_host_initialize(void)
{
    if (s_initialized) return 0;
    s_gate = xSemaphoreCreateMutex();
    if (s_gate == NULL) return -ENOMEM;
    esp_err_t result = nvs_flash_init();
    if (result == ESP_ERR_NVS_NO_FREE_PAGES || result == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        result = nvs_flash_erase();
        if (result == ESP_OK) result = nvs_flash_init();
    }
    if (result == ESP_OK) result = esp_netif_init();
    if (result == ESP_OK) {
        result = esp_event_loop_create_default();
        if (result == ESP_ERR_INVALID_STATE) result = ESP_OK;
    }
    if (result == ESP_OK) s_station = esp_netif_create_default_wifi_sta();
    if (result == ESP_OK && s_station == NULL) result = ESP_FAIL;
    wifi_init_config_t initialization = WIFI_INIT_CONFIG_DEFAULT();
    if (result == ESP_OK) result = esp_wifi_init(&initialization);
    if (result == ESP_OK) result = esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, network_event, NULL);
    if (result == ESP_OK) result = esp_event_handler_register(IP_EVENT, ESP_EVENT_ANY_ID, network_event, NULL);
    if (result == ESP_OK) result = esp_wifi_set_mode(WIFI_MODE_STA);
    if (result == ESP_OK) result = esp_wifi_start();
    if (result == ESP_OK) result = esp_elf_register_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols);
    s_initialized = result == ESP_OK;
    s_last_error = result == ESP_OK ? 0 : -(int32_t)result;
    return s_last_error;
}
