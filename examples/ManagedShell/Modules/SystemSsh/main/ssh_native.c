#include "managed_ssh_host_api.h"
#include <errno.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

static volatile bool s_stop;
extern bool ct_managed_process_cancellation_requested(void);

static const ct_managed_ssh_host_api_v1 *host(void)
{
    const ct_managed_ssh_host_api_v1 *api = ct_managed_ssh_host_v1();
    return api != NULL && api->Version == CT_MANAGED_SSH_HOST_API_VERSION && api->Size >= sizeof(*api) ? api : NULL;
}

int32_t ct_managed_sshd_run(int32_t port, int32_t maximum_packet_bytes, int32_t maximum_channels,
    bool *listening, bool *connected, bool *authenticated, uint32_t *sessions_accepted,
    uint32_t *authentication_failures, int64_t *bytes_received, int64_t *bytes_sent)
{
    (void)maximum_packet_bytes; (void)maximum_channels;
    const ct_managed_ssh_host_api_v1 *api = host();
    if (api == NULL) return -ENOSYS;
    while (api->NetworkReady() == 0) {
        if (s_stop || ct_managed_process_cancellation_requested()) return -ECANCELED;
        api->Delay(250u);
    }
    int listener = api->Listen((uint16_t)port, 1);
    if (listener < 0) return listener;
    *listening = true; *connected = false; *authenticated = false;
    s_stop = false;
    int32_t result = 0;
    while (!s_stop && !ct_managed_process_cancellation_requested()) {
        int client = api->Accept(listener, 250u);
        if (client == -ETIMEDOUT) continue;
        if (client < 0) { result = client; break; }
        *connected = true;
        (*sessions_accepted)++;
        static const uint8_t banner[] = "SSH-2.0-CTilde_0.48\r\n";
        int sent = api->Send(client, banner, sizeof(banner) - 1u, 5000u);
        if (sent > 0) *bytes_sent += sent;
        uint8_t peer_banner[256];
        int received = api->Receive(client, peer_banner, sizeof(peer_banner), 5000u);
        if (received > 0) *bytes_received += received;
        if (received <= 0 || (size_t)received < 8u || memcmp(peer_banner, "SSH-2.0-", 8u) != 0)
            (*authentication_failures)++;
        (void)api->Close(client);
        *connected = false;
    }
    (void)api->Close(listener);
    *listening = false;
    return result;
}

void ct_managed_sshd_stop(void) { s_stop = true; }
