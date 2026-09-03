#pragma once

#include <stdint.h>

#define CT_MANAGED_NETWORK_HOST_API_VERSION 1u
#define CT_MANAGED_NETWORK_MAX_SCAN_RESULTS 20u

typedef struct ct_managed_network_status {
    uint32_t Size;
    uint32_t Version;
    uint32_t Initialized;
    uint32_t Connected;
    uint32_t AddressReady;
    int32_t Rssi;
    int32_t LastError;
    char Ssid[33];
    char Address[16];
    char Gateway[16];
    char Dns[16];
} ct_managed_network_status;

typedef struct ct_managed_network_host_api_v1 {
    uint32_t Size;
    uint32_t Version;
    int32_t (*Scan)(void);
    uint32_t (*ScanCount)(void);
    const char *(*ScanSsid)(uint32_t index);
    int32_t (*ScanRssi)(uint32_t index);
    uint32_t (*ScanChannel)(uint32_t index);
    int32_t (*Connect)(const char *ssid, const char *password, const char *hostname);
    int32_t (*Disconnect)(void);
    int32_t (*Status)(ct_managed_network_status *output);
    int32_t (*Wait)(uint32_t timeout_milliseconds);
    const char *(*ErrorName)(int32_t code);
} ct_managed_network_host_api_v1;

const ct_managed_network_host_api_v1 *ct_managed_network_host_v1(void);
