#include "managed_network_host_api.h"
#include <stddef.h>

static ct_managed_network_status s_status;
static const ct_managed_network_host_api_v1 *api(void)
{
    const ct_managed_network_host_api_v1 *value = ct_managed_network_host_v1();
    return value != NULL && value->Version == CT_MANAGED_NETWORK_HOST_API_VERSION &&
        value->Size >= sizeof(*value) ? value : NULL;
}

int32_t ct_net_scan(void) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? -1 : v->Scan(); }
uint32_t ct_net_scan_count(void) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? 0u : v->ScanCount(); }
const uint8_t *ct_net_scan_ssid(uint32_t i) { const ct_managed_network_host_api_v1 *v = api(); return (const uint8_t *)(v == NULL ? NULL : v->ScanSsid(i)); }
int32_t ct_net_scan_rssi(uint32_t i) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? 0 : v->ScanRssi(i); }
uint32_t ct_net_scan_channel(uint32_t i) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? 0u : v->ScanChannel(i); }
int32_t ct_net_connect(const char *s, const char *p, const char *h) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? -1 : v->Connect(s, p, h); }
int32_t ct_net_disconnect(void) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? -1 : v->Disconnect(); }
int32_t ct_net_refresh_status(void) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? -1 : v->Status(&s_status); }
uint32_t ct_net_initialized(void) { return s_status.Initialized; }
uint32_t ct_net_connected(void) { return s_status.Connected; }
uint32_t ct_net_address_ready(void) { return s_status.AddressReady; }
int32_t ct_net_rssi(void) { return s_status.Rssi; }
int32_t ct_net_last_error(void) { return s_status.LastError; }
const uint8_t *ct_net_ssid(void) { return (const uint8_t *)s_status.Ssid; }
const uint8_t *ct_net_address(void) { return (const uint8_t *)s_status.Address; }
const uint8_t *ct_net_gateway(void) { return (const uint8_t *)s_status.Gateway; }
const uint8_t *ct_net_dns(void) { return (const uint8_t *)s_status.Dns; }
int32_t ct_net_wait(uint32_t t) { const ct_managed_network_host_api_v1 *v = api(); return v == NULL ? -1 : v->Wait(t); }
const uint8_t *ct_net_error_name(int32_t c) { const ct_managed_network_host_api_v1 *v = api(); return (const uint8_t *)(v == NULL ? "HOST_API_UNAVAILABLE" : v->ErrorName(c)); }
