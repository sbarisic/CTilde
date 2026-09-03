#include "managed_ssh_host.h"
#include "managed_ssh_host_api.h"
#include "managed_network_host_api.h"

#include <errno.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "esp_err.h"
#include "esp_elf.h"
#include "esp_random.h"
#include "lwip/inet.h"
#include "lwip/sockets.h"
#include "mbedtls/constant_time.h"
#include "mbedtls/platform_util.h"
#include "psa/crypto.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static int32_t network_ready(void)
{
    ct_managed_network_status status = {0};
    const ct_managed_network_host_api_v1 *network = ct_managed_network_host_v1();
    if (network == NULL || network->Status(&status) != 0) return 0;
    return status.AddressReady != 0u;
}

static void task_delay(uint32_t milliseconds) { vTaskDelay(pdMS_TO_TICKS(milliseconds)); }

static int32_t listen_socket(uint16_t port, int32_t backlog)
{
    int descriptor = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (descriptor < 0) return -errno;
    int enabled = 1;
    (void)setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons(port), .sin_addr.s_addr = htonl(INADDR_ANY) };
    if (bind(descriptor, (const struct sockaddr *)&address, sizeof(address)) != 0 || listen(descriptor, backlog) != 0) {
        const int result = -errno; (void)close(descriptor); return result;
    }
    return descriptor;
}

static int wait_descriptor(int descriptor, bool write, uint32_t timeout_milliseconds)
{
    fd_set set; FD_ZERO(&set); FD_SET(descriptor, &set);
    struct timeval timeout = { .tv_sec = (time_t)(timeout_milliseconds / 1000u), .tv_usec = (suseconds_t)((timeout_milliseconds % 1000u) * 1000u) };
    const int result = select(descriptor + 1, write ? NULL : &set, write ? &set : NULL, NULL, &timeout);
    return result > 0 ? 0 : result == 0 ? -ETIMEDOUT : -errno;
}

static int32_t accept_socket(int32_t listener, uint32_t timeout_milliseconds)
{
    const int ready = wait_descriptor(listener, false, timeout_milliseconds);
    if (ready != 0) return ready;
    int result = accept(listener, NULL, NULL);
    return result < 0 ? -errno : result;
}

static int32_t receive_socket(int32_t descriptor, uint8_t *data, size_t length, uint32_t timeout_milliseconds)
{
    if (data == NULL || length == 0u) return -EINVAL;
    const int ready = wait_descriptor(descriptor, false, timeout_milliseconds);
    if (ready != 0) return ready;
    const ssize_t result = recv(descriptor, data, length, 0);
    return result < 0 ? -errno : (int32_t)result;
}

static int32_t send_socket(int32_t descriptor, const uint8_t *data, size_t length, uint32_t timeout_milliseconds)
{
    if (data == NULL && length != 0u) return -EINVAL;
    size_t offset = 0u;
    while (offset < length) {
        const int ready = wait_descriptor(descriptor, true, timeout_milliseconds);
        if (ready != 0) return ready;
        const ssize_t count = send(descriptor, data + offset, length - offset, 0);
        if (count <= 0) return count == 0 ? -EPIPE : -errno;
        offset += (size_t)count;
    }
    return (int32_t)offset;
}

static int32_t close_socket(int32_t descriptor) { return descriptor < 0 || close(descriptor) == 0 ? 0 : -errno; }
static int32_t random_bytes(uint8_t *data, size_t length)
{
    if (data == NULL && length != 0u) return -EINVAL;
    esp_fill_random(data, length); return 0;
}
static int32_t sha256_bytes(const uint8_t *data, size_t length, uint8_t output[32])
{
    if ((data == NULL && length != 0u) || output == NULL) return -EINVAL;
    size_t output_length = 0u;
    const psa_status_t status = psa_hash_compute(PSA_ALG_SHA_256, data, length, output, 32u, &output_length);
    return status == PSA_SUCCESS && output_length == 32u ? 0 : -EIO;
}
static int32_t constant_time_equal(const uint8_t *left, const uint8_t *right, size_t length)
{
    if ((left == NULL || right == NULL) && length != 0u) return 0;
    return mbedtls_ct_memcmp(left, right, length) == 0;
}
static void zeroize(void *data, size_t length) { if (data != NULL) mbedtls_platform_zeroize(data, length); }
static const char *error_name(int32_t code) { return esp_err_to_name(code < 0 ? (esp_err_t)-code : (esp_err_t)code); }

static const ct_managed_ssh_host_api_v1 s_api = {
    .Size = sizeof(s_api), .Version = CT_MANAGED_SSH_HOST_API_VERSION,
    .NetworkReady = network_ready, .Delay = task_delay, .Listen = listen_socket, .Accept = accept_socket,
    .Receive = receive_socket, .Send = send_socket, .Close = close_socket,
    .Random = random_bytes, .Sha256 = sha256_bytes, .ConstantTimeEqual = constant_time_equal,
    .Zeroize = zeroize, .ErrorName = error_name,
};
const ct_managed_ssh_host_api_v1 *ct_managed_ssh_host_v1(void) { return &s_api; }
static const struct esp_elfsym s_symbols[] = { ESP_ELFSYM_EXPORT(ct_managed_ssh_host_v1), ESP_ELFSYM_END };
int ct_managed_ssh_host_initialize(void)
{
    return esp_elf_register_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols);
}
