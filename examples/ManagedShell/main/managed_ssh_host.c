#include "managed_ssh_host.h"
#include "managed_ssh_host_api.h"
#include "managed_network_host_api.h"
#include "ctilde_managed_runtime.h"

#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "esp_err.h"
#include "esp_elf.h"
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "lwip/inet.h"
#include "lwip/sockets.h"
#include "mbedtls/constant_time.h"
#include "mbedtls/md.h"
#include "mbedtls/pk.h"
#include "mbedtls/platform_util.h"
#include "psa/crypto.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#define CT_SSH_RESOURCE_CAPACITY 24u

static const char *TAG = "ctilde.ssh";

typedef enum ct_ssh_resource_kind {
    CT_SSH_RESOURCE_NONE,
    CT_SSH_RESOURCE_SOCKET,
    CT_SSH_RESOURCE_SHA256,
    CT_SSH_RESOURCE_PSA_KEY,
    CT_SSH_RESOURCE_PK
} ct_ssh_resource_kind;

typedef struct ct_ssh_resource {
    uint32_t Generation;
    ct_ssh_resource_kind Kind;
    uintptr_t LedgerToken;
    union {
        int Socket;
        psa_hash_operation_t Hash;
        psa_key_id_t Key;
        mbedtls_pk_context *Pk;
    } Value;
} ct_ssh_resource;

static ct_ssh_resource s_resources[CT_SSH_RESOURCE_CAPACITY];
static StaticSemaphore_t s_resource_lock_storage;
static SemaphoreHandle_t s_resource_lock;

static uint32_t encode_token(size_t slot, uint32_t generation)
{
    return (generation << 8) | (uint32_t)(slot + 1u);
}

static bool decode_token(uint32_t token, size_t *slot, uint32_t *generation)
{
    const uint32_t encoded_slot = token & UINT32_C(0xff);
    if (encoded_slot == 0u || encoded_slot > CT_SSH_RESOURCE_CAPACITY) return false;
    *slot = (size_t)(encoded_slot - 1u);
    *generation = token >> 8;
    return *generation != 0u;
}

static void destroy_value(ct_ssh_resource *resource)
{
    switch (resource->Kind) {
        case CT_SSH_RESOURCE_SOCKET:
            if (resource->Value.Socket >= 0) (void)close(resource->Value.Socket);
            break;
        case CT_SSH_RESOURCE_SHA256:
            (void)psa_hash_abort(&resource->Value.Hash);
            break;
        case CT_SSH_RESOURCE_PSA_KEY:
            (void)psa_destroy_key(resource->Value.Key);
            break;
        case CT_SSH_RESOURCE_PK:
            if (resource->Value.Pk != NULL) {
                mbedtls_pk_free(resource->Value.Pk);
                free(resource->Value.Pk);
            }
            break;
        default:
            break;
    }
    resource->Kind = CT_SSH_RESOURCE_NONE;
    resource->LedgerToken = 0u;
    (void)memset(&resource->Value, 0, sizeof(resource->Value));
}

static void cleanup_token(uintptr_t value)
{
    const uint32_t token = (uint32_t)value;
    size_t slot = 0u;
    uint32_t generation = 0u;
    if (!decode_token(token, &slot, &generation) || s_resource_lock == NULL) return;
    xSemaphoreTake(s_resource_lock, portMAX_DELAY);
    ct_ssh_resource *resource = &s_resources[slot];
    if (resource->Generation == generation && resource->Kind != CT_SSH_RESOURCE_NONE)
        destroy_value(resource);
    xSemaphoreGive(s_resource_lock);
}

static int32_t register_resource(ct_ssh_resource_kind kind, const void *value, uint32_t *output)
{
    if (output == NULL || s_resource_lock == NULL) return -EINVAL;
    xSemaphoreTake(s_resource_lock, portMAX_DELAY);
    size_t slot = CT_SSH_RESOURCE_CAPACITY;
    for (size_t index = 0u; index < CT_SSH_RESOURCE_CAPACITY; ++index) {
        if (s_resources[index].Kind == CT_SSH_RESOURCE_NONE) { slot = index; break; }
    }
    if (slot == CT_SSH_RESOURCE_CAPACITY) {
        xSemaphoreGive(s_resource_lock);
        return -EMFILE;
    }
    ct_ssh_resource *resource = &s_resources[slot];
    uint32_t generation = resource->Generation + 1u;
    if (generation == 0u || generation > UINT32_C(0x00ffffff)) generation = 1u;
    resource->Generation = generation;
    resource->Kind = kind;
    if (kind == CT_SSH_RESOURCE_SOCKET) resource->Value.Socket = *(const int *)value;
    else if (kind == CT_SSH_RESOURCE_SHA256) resource->Value.Hash = *(const psa_hash_operation_t *)value;
    else if (kind == CT_SSH_RESOURCE_PSA_KEY) resource->Value.Key = *(const psa_key_id_t *)value;
    else if (kind == CT_SSH_RESOURCE_PK) resource->Value.Pk = *(mbedtls_pk_context *const *)value;
    const uint32_t token = encode_token(slot, generation);
    xSemaphoreGive(s_resource_lock);

    const uintptr_t ledger = ctilde_managed_native_resource_register((uintptr_t)token, cleanup_token);
    if (ledger == 0u) {
        cleanup_token((uintptr_t)token);
        return -ENOMEM;
    }
    xSemaphoreTake(s_resource_lock, portMAX_DELAY);
    if (resource->Generation != generation || resource->Kind == CT_SSH_RESOURCE_NONE) {
        xSemaphoreGive(s_resource_lock);
        (void)ctilde_managed_native_resource_release(ledger);
        return -EIO;
    }
    resource->LedgerToken = ledger;
    xSemaphoreGive(s_resource_lock);
    *output = token;
    return 0;
}

static ct_ssh_resource *find_resource(uint32_t token, ct_ssh_resource_kind kind)
{
    size_t slot = 0u;
    uint32_t generation = 0u;
    if (!decode_token(token, &slot, &generation)) return NULL;
    ct_ssh_resource *resource = &s_resources[slot];
    return resource->Generation == generation && resource->Kind == kind ? resource : NULL;
}

static int32_t resource_close(uint32_t token)
{
    size_t slot = 0u;
    uint32_t generation = 0u;
    if (!decode_token(token, &slot, &generation)) return -EBADF;
    xSemaphoreTake(s_resource_lock, portMAX_DELAY);
    ct_ssh_resource *resource = &s_resources[slot];
    const uintptr_t ledger = resource->Generation == generation ? resource->LedgerToken : 0u;
    xSemaphoreGive(s_resource_lock);
    return ledger != 0u && ctilde_managed_native_resource_release(ledger) ? 0 : -EBADF;
}

static int32_t network_ready(void)
{
    ct_managed_network_status status = {0};
    const ct_managed_network_host_api_v1 *network = ct_managed_network_host_v1();
    if (network == NULL || network->Status(&status) != 0) return 0;
    return status.AddressReady != 0u;
}

static void task_delay(uint32_t milliseconds) { vTaskDelay(pdMS_TO_TICKS(milliseconds)); }

static uint64_t monotonic_milliseconds(void)
{
    return (uint64_t)esp_timer_get_time() / UINT64_C(1000);
}

static int wait_descriptor(int descriptor, uint32_t events, uint32_t timeout_milliseconds,
    uint32_t *ready_events)
{
    fd_set reads;
    fd_set writes;
    FD_ZERO(&reads);
    FD_ZERO(&writes);
    if ((events & CT_MANAGED_SSH_WAIT_READ) != 0u) FD_SET(descriptor, &reads);
    if ((events & CT_MANAGED_SSH_WAIT_WRITE) != 0u) FD_SET(descriptor, &writes);
    struct timeval timeout = {
        .tv_sec = (time_t)(timeout_milliseconds / 1000u),
        .tv_usec = (suseconds_t)((timeout_milliseconds % 1000u) * 1000u)
    };
    const int result = select(descriptor + 1, &reads, &writes, NULL, &timeout);
    if (result < 0) return -errno;
    uint32_t ready = 0u;
    if (result > 0 && FD_ISSET(descriptor, &reads)) ready |= CT_MANAGED_SSH_WAIT_READ;
    if (result > 0 && FD_ISSET(descriptor, &writes)) ready |= CT_MANAGED_SSH_WAIT_WRITE;
    if (ready_events != NULL) *ready_events = ready;
    return result == 0 ? -ETIMEDOUT : 0;
}

static int32_t socket_listen(uint16_t port, int32_t backlog, uint32_t *token)
{
    if (token == NULL || backlog < 1) return -EINVAL;
    int descriptor = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (descriptor < 0) return -errno;
    int enabled = 1;
    (void)setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    struct sockaddr_in address = {
        .sin_family = AF_INET,
        .sin_port = htons(port),
        .sin_addr.s_addr = htonl(INADDR_ANY)
    };
    if (bind(descriptor, (const struct sockaddr *)&address, sizeof(address)) != 0 ||
        listen(descriptor, backlog) != 0) {
        const int32_t result = -errno;
        (void)close(descriptor);
        return result;
    }
    const int flags = fcntl(descriptor, F_GETFL, 0);
    if (flags >= 0) (void)fcntl(descriptor, F_SETFL, flags | O_NONBLOCK);
    return register_resource(CT_SSH_RESOURCE_SOCKET, &descriptor, token);
}

static int32_t socket_accept(uint32_t listener_token, uint32_t timeout_milliseconds,
    uint32_t *token)
{
    ct_ssh_resource *listener = find_resource(listener_token, CT_SSH_RESOURCE_SOCKET);
    if (listener == NULL || token == NULL) return -EBADF;
    const int ready = wait_descriptor(listener->Value.Socket, CT_MANAGED_SSH_WAIT_READ,
        timeout_milliseconds, NULL);
    if (ready != 0) return ready;
    int descriptor = accept(listener->Value.Socket, NULL, NULL);
    if (descriptor < 0) return -errno;
    const int flags = fcntl(descriptor, F_GETFL, 0);
    if (flags >= 0) (void)fcntl(descriptor, F_SETFL, flags | O_NONBLOCK);
    return register_resource(CT_SSH_RESOURCE_SOCKET, &descriptor, token);
}

static int32_t socket_wait(uint32_t token, uint32_t events, uint32_t timeout_milliseconds,
    uint32_t *ready_events)
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_SOCKET);
    if (resource == NULL || ready_events == NULL ||
        (events & ~(CT_MANAGED_SSH_WAIT_READ | CT_MANAGED_SSH_WAIT_WRITE)) != 0u) return -EINVAL;
    return wait_descriptor(resource->Value.Socket, events, timeout_milliseconds, ready_events);
}

static int32_t socket_receive(uint32_t token, uint8_t *data, size_t length,
    uint32_t timeout_milliseconds)
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_SOCKET);
    if (resource == NULL || (data == NULL && length != 0u)) return -EINVAL;
    const int ready = wait_descriptor(resource->Value.Socket, CT_MANAGED_SSH_WAIT_READ,
        timeout_milliseconds, NULL);
    if (ready != 0) return ready;
    const ssize_t result = recv(resource->Value.Socket, data, length, 0);
    return result < 0 ? -errno : (int32_t)result;
}

static int32_t socket_send(uint32_t token, const uint8_t *data, size_t length,
    uint32_t timeout_milliseconds)
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_SOCKET);
    if (resource == NULL || (data == NULL && length != 0u)) return -EINVAL;
    size_t offset = 0u;
    while (offset < length) {
        const int ready = wait_descriptor(resource->Value.Socket, CT_MANAGED_SSH_WAIT_WRITE,
            timeout_milliseconds, NULL);
        if (ready != 0) return ready;
        const ssize_t count = send(resource->Value.Socket, data + offset, length - offset, 0);
        if (count <= 0) return count == 0 ? -EPIPE : -errno;
        offset += (size_t)count;
    }
    return (int32_t)offset;
}

static int32_t socket_shutdown(uint32_t token, uint32_t directions)
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_SOCKET);
    if (resource == NULL) return -EBADF;
    int how = SHUT_RDWR;
    if (directions == CT_MANAGED_SSH_SHUTDOWN_READ) how = SHUT_RD;
    else if (directions == CT_MANAGED_SSH_SHUTDOWN_WRITE) how = SHUT_WR;
    else if (directions != (CT_MANAGED_SSH_SHUTDOWN_READ | CT_MANAGED_SSH_SHUTDOWN_WRITE))
        return -EINVAL;
    return shutdown(resource->Value.Socket, how) == 0 ? 0 : -errno;
}

static int32_t random_bytes(uint8_t *data, size_t length)
{
    if (data == NULL && length != 0u) return -EINVAL;
    esp_fill_random(data, length);
    return 0;
}

static int32_t constant_time_equal(const uint8_t *left, const uint8_t *right, size_t length)
{
    if ((left == NULL || right == NULL) && length != 0u) return 0;
    return mbedtls_ct_memcmp(left, right, length) == 0 ? 1 : 0;
}

static int32_t sha256_create(uint32_t *token)
{
    psa_hash_operation_t operation = PSA_HASH_OPERATION_INIT;
    const psa_status_t status = psa_hash_setup(&operation, PSA_ALG_SHA_256);
    if (status != PSA_SUCCESS) return -EIO;
    return register_resource(CT_SSH_RESOURCE_SHA256, &operation, token);
}

static int32_t sha256_update(uint32_t token, const uint8_t *data, size_t length)
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_SHA256);
    if (resource == NULL || (data == NULL && length != 0u)) return -EINVAL;
    return psa_hash_update(&resource->Value.Hash, data, length) == PSA_SUCCESS ? 0 : -EIO;
}

static int32_t sha256_finish(uint32_t token, uint8_t output[32])
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_SHA256);
    if (resource == NULL || output == NULL) return -EINVAL;
    size_t output_length = 0u;
    const psa_status_t status = psa_hash_finish(&resource->Value.Hash, output, 32u, &output_length);
    if (status != PSA_SUCCESS || output_length != 32u) return -EIO;
    (void)memset(&resource->Value.Hash, 0, sizeof(resource->Value.Hash));
    return 0;
}

static int32_t x25519_create(uint32_t *token, uint8_t public_key[32])
{
    if (token == NULL || public_key == NULL) return -EINVAL;
    psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&attributes, PSA_KEY_TYPE_ECC_KEY_PAIR(PSA_ECC_FAMILY_MONTGOMERY));
    psa_set_key_bits(&attributes, 255u);
    psa_set_key_usage_flags(&attributes, PSA_KEY_USAGE_DERIVE | PSA_KEY_USAGE_EXPORT);
    psa_set_key_algorithm(&attributes, PSA_ALG_ECDH);
    psa_key_id_t key = 0;
    psa_status_t status = psa_generate_key(&attributes, &key);
    psa_reset_key_attributes(&attributes);
    if (status != PSA_SUCCESS) return -EIO;
    size_t length = 0u;
    status = psa_export_public_key(key, public_key, 32u, &length);
    if (status != PSA_SUCCESS || length != 32u) {
        (void)psa_destroy_key(key);
        return -EIO;
    }
    return register_resource(CT_SSH_RESOURCE_PSA_KEY, &key, token);
}

static int32_t x25519_shared(uint32_t token, const uint8_t peer_public_key[32],
    uint8_t shared_secret[32])
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_PSA_KEY);
    if (resource == NULL || peer_public_key == NULL || shared_secret == NULL) return -EINVAL;
    uint8_t aggregate = 0u;
    for (size_t index = 0u; index < 32u; ++index) aggregate |= peer_public_key[index];
    if (aggregate == 0u) return -EINVAL;
    size_t length = 0u;
    const psa_status_t status = psa_raw_key_agreement(PSA_ALG_ECDH, resource->Value.Key,
        peer_public_key, 32u, shared_secret, 32u, &length);
    if (status != PSA_SUCCESS || length != 32u) {
        mbedtls_platform_zeroize(shared_secret, 32u);
        return -EIO;
    }
    aggregate = 0u;
    for (size_t index = 0u; index < 32u; ++index) aggregate |= shared_secret[index];
    if (aggregate == 0u) {
        mbedtls_platform_zeroize(shared_secret, 32u);
        return -EINVAL;
    }
    return 0;
}

static bool p256_public_from_der(const uint8_t *der, size_t length, uint8_t output[65])
{
    static const uint8_t marker[] = { 0x03u, 0x42u, 0x00u, 0x04u };
    for (size_t index = 0u; index + sizeof(marker) + 64u <= length; ++index) {
        if (memcmp(der + index, marker, sizeof(marker)) == 0) {
            (void)memcpy(output, der + index + 3u, 65u);
            return true;
        }
    }
    return false;
}

static int32_t p256_private_import(const uint8_t *pem, size_t length, uint32_t *token)
{
    static const char begin[] = "-----BEGIN PRIVATE KEY-----";
    static const char end[] = "-----END PRIVATE KEY-----";
    if (pem == NULL || length == 0u || token == NULL || length > 16384u) return -EINVAL;
    if (length < sizeof(begin) - 1u + sizeof(end) - 1u ||
        memcmp(pem, begin, sizeof(begin) - 1u) != 0) return -EINVAL;
    bool found_end = false;
    for (size_t index = sizeof(begin) - 1u; index + sizeof(end) - 1u <= length; ++index) {
        if (memcmp(pem + index, end, sizeof(end) - 1u) == 0) {
            found_end = true;
            for (size_t trailing = index + sizeof(end) - 1u; trailing < length; ++trailing)
                if (pem[trailing] != '\r' && pem[trailing] != '\n') return -EINVAL;
            break;
        }
    }
    if (!found_end) return -EINVAL;
    uint8_t *terminated = malloc(length + 1u);
    mbedtls_pk_context *pk = malloc(sizeof(*pk));
    if (terminated == NULL || pk == NULL) {
        free(terminated);
        free(pk);
        return -ENOMEM;
    }
    (void)memcpy(terminated, pem, length);
    terminated[length] = 0u;
    mbedtls_pk_init(pk);
    ctilde_managed_memory_sample("ssh_key_parse_begin", 0u);
    const int result = mbedtls_pk_parse_key(pk, terminated, length + 1u, NULL, 0u);
    ctilde_managed_memory_sample("ssh_key_parse_complete", 0u);
    mbedtls_platform_zeroize(terminated, length + 1u);
    free(terminated);
    const int can_sign = result == 0 ? mbedtls_pk_can_do_psa(pk,
        MBEDTLS_PK_ALG_ECDSA(PSA_ALG_SHA_256), PSA_KEY_USAGE_SIGN_HASH) : 0;
    if (result != 0 || !can_sign) {
        ESP_LOGE(TAG, "P-256 host-key import rejected: parse=%d, bits=%u, sign=%d",
            result, (unsigned)(result == 0 ? mbedtls_pk_get_bitlen(pk) : 0u), can_sign);
        mbedtls_pk_free(pk);
        free(pk);
        return -EINVAL;
    }
    uint8_t der[160];
    const int der_length = mbedtls_pk_write_pubkey_der(pk, der, sizeof(der));
    uint8_t point[65];
    if (der_length <= 0 || !p256_public_from_der(der + sizeof(der) - (size_t)der_length,
        (size_t)der_length, point)) {
        mbedtls_pk_free(pk);
        free(pk);
        return -EINVAL;
    }
    return register_resource(CT_SSH_RESOURCE_PK, &pk, token);
}

static int32_t p256_public(uint32_t token, uint8_t public_key[65])
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_PK);
    if (resource == NULL || public_key == NULL) return -EINVAL;
    uint8_t der[160];
    const int length = mbedtls_pk_write_pubkey_der(resource->Value.Pk, der, sizeof(der));
    return length > 0 && p256_public_from_der(der + sizeof(der) - (size_t)length,
        (size_t)length, public_key) ? 0 : -EIO;
}

static bool der_integer(const uint8_t *der, size_t length, size_t *offset, uint8_t output[32])
{
    if (*offset + 2u > length || der[(*offset)++] != 0x02u) return false;
    size_t count = der[(*offset)++];
    if (count == 0u || count > 33u || *offset + count > length) return false;
    if (count == 33u) {
        if (der[*offset] != 0u) return false;
        (*offset)++;
        count--;
    }
    (void)memset(output, 0, 32u);
    (void)memcpy(output + 32u - count, der + *offset, count);
    *offset += count;
    return true;
}

static int32_t p256_sign(uint32_t token, const uint8_t hash[32], uint8_t signature[64])
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_PK);
    if (resource == NULL || hash == NULL || signature == NULL) return -EINVAL;
    uint8_t der[MBEDTLS_PK_SIGNATURE_MAX_SIZE];
    size_t length = 0u;
    if (mbedtls_pk_sign(resource->Value.Pk, MBEDTLS_MD_SHA256, hash, 32u,
        der, sizeof(der), &length) != 0 || length < 6u || der[0] != 0x30u) return -EIO;
    size_t offset = 2u;
    if (der[1] >= 0x80u) return -EIO;
    return der_integer(der, length, &offset, signature) &&
        der_integer(der, length, &offset, signature + 32u) && offset == length ? 0 : -EIO;
}

static int32_t p256_public_import(const uint8_t public_key[65], uint32_t *token)
{
    if (public_key == NULL || token == NULL || public_key[0] != 0x04u) return -EINVAL;
    psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&attributes, PSA_KEY_TYPE_ECC_PUBLIC_KEY(PSA_ECC_FAMILY_SECP_R1));
    psa_set_key_bits(&attributes, 256u);
    psa_set_key_usage_flags(&attributes, PSA_KEY_USAGE_VERIFY_HASH);
    psa_set_key_algorithm(&attributes, PSA_ALG_ECDSA(PSA_ALG_SHA_256));
    psa_key_id_t key = 0;
    const psa_status_t status = psa_import_key(&attributes, public_key, 65u, &key);
    psa_reset_key_attributes(&attributes);
    if (status != PSA_SUCCESS) {
        ESP_LOGE(TAG, "P-256 authorized-key import rejected: psa=%d", (int)status);
        return -EINVAL;
    }
    return register_resource(CT_SSH_RESOURCE_PSA_KEY, &key, token);
}

static int32_t p256_verify(uint32_t token, const uint8_t hash[32],
    const uint8_t signature[64])
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_PSA_KEY);
    if (resource == NULL || hash == NULL || signature == NULL) return -EINVAL;
    /* PSA ECDSA consumes fixed-width r || s, unlike mbedtls_pk_verify's DER input. */
    const psa_status_t status = psa_verify_hash(resource->Value.Key,
        PSA_ALG_ECDSA(PSA_ALG_SHA_256), hash, 32u, signature, 64u);
    return status == PSA_SUCCESS ? 1 : status == PSA_ERROR_INVALID_SIGNATURE ? 0 : -EIO;
}

static int32_t aes128_gcm_create(const uint8_t key[16], uint32_t *token)
{
    if (key == NULL || token == NULL) return -EINVAL;
    psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&attributes, PSA_KEY_TYPE_AES);
    psa_set_key_bits(&attributes, 128u);
    psa_set_key_usage_flags(&attributes, PSA_KEY_USAGE_ENCRYPT | PSA_KEY_USAGE_DECRYPT);
    psa_set_key_algorithm(&attributes, PSA_ALG_GCM);
    psa_key_id_t key_id = 0;
    const psa_status_t status = psa_import_key(&attributes, key, 16u, &key_id);
    psa_reset_key_attributes(&attributes);
    if (status != PSA_SUCCESS) return -EIO;
    return register_resource(CT_SSH_RESOURCE_PSA_KEY, &key_id, token);
}

/* Bounded scratch replaces the previous packet-sized ciphertext/tag copy. */
static psa_status_t aead_packet(bool encrypt, psa_key_id_t key, const uint8_t nonce[12],
    const uint8_t *additional, size_t additional_length, const uint8_t *input,
    size_t length, uint8_t *output, uint8_t *generated_tag, const uint8_t *expected_tag)
{
    /* Only disjoint buffers or exact in-place use are supported. A forward
       overlap could overwrite input that a later update has not consumed. */
    if (input != output && length != 0u) {
        const uintptr_t source = (uintptr_t)input, destination = (uintptr_t)output;
        const uintptr_t distance = source > destination ? source - destination : destination - source;
        if (distance < length) return PSA_ERROR_INVALID_ARGUMENT;
    }
    psa_aead_operation_t operation = PSA_AEAD_OPERATION_INIT;
    uint8_t scratch[PSA_AEAD_UPDATE_OUTPUT_SIZE(PSA_KEY_TYPE_AES, PSA_ALG_GCM, 256u)];
    size_t written = 0u;
    psa_status_t status = encrypt ? psa_aead_encrypt_setup(&operation, key, PSA_ALG_GCM) :
        psa_aead_decrypt_setup(&operation, key, PSA_ALG_GCM);
    if (status == PSA_SUCCESS) status = psa_aead_set_lengths(&operation, additional_length, length);
    if (status == PSA_SUCCESS) status = psa_aead_set_nonce(&operation, nonce, 12u);
    if (status == PSA_SUCCESS && additional_length != 0u)
        status = psa_aead_update_ad(&operation, additional, additional_length);
    for (size_t offset = 0u; status == PSA_SUCCESS && offset < length;) {
        const size_t count = length - offset > 256u ? 256u : length - offset;
        size_t produced = 0u;
        status = psa_aead_update(&operation, input + offset, count, scratch, sizeof(scratch), &produced);
        if (status == PSA_SUCCESS) {
            if (produced > offset + count - written) { status = PSA_ERROR_BUFFER_TOO_SMALL; break; }
            if (produced != 0u) memcpy(output + written, scratch, produced);
            written += produced;
            offset += count;
        }
    }
    if (status == PSA_SUCCESS) {
        size_t produced = 0u;
        size_t tag_length = 0u;
        status = encrypt ? psa_aead_finish(&operation, scratch, sizeof(scratch), &produced,
            generated_tag, 16u, &tag_length) :
            psa_aead_verify(&operation, scratch, sizeof(scratch), &produced, expected_tag, 16u);
        if (status == PSA_SUCCESS) {
            if (produced > length - written || (encrypt && tag_length != 16u))
                status = PSA_ERROR_CORRUPTION_DETECTED;
            else {
                if (produced != 0u) memcpy(output + written, scratch, produced);
                written += produced;
                if (written != length) status = PSA_ERROR_CORRUPTION_DETECTED;
            }
        }
    }
    (void)psa_aead_abort(&operation);
    mbedtls_platform_zeroize(scratch, sizeof(scratch));
    if (status != PSA_SUCCESS) {
        if (length != 0u) mbedtls_platform_zeroize(output, length);
        if (generated_tag != NULL) mbedtls_platform_zeroize(generated_tag, 16u);
    }
    return status;
}

static int32_t aes128_gcm_seal(uint32_t token, const uint8_t nonce[12],
    const uint8_t *additional, size_t additional_length, const uint8_t *plain,
    size_t plain_length, uint8_t *cipher, uint8_t tag[16])
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_PSA_KEY);
    if (resource == NULL || nonce == NULL || (plain == NULL && plain_length != 0u) ||
        (cipher == NULL && plain_length != 0u) || tag == NULL ||
        (additional == NULL && additional_length != 0u)) return -EINVAL;
    const psa_status_t status = aead_packet(true, resource->Value.Key, nonce,
        additional, additional_length, plain, plain_length, cipher, tag, NULL);
    return status == PSA_SUCCESS ? 0 : -EIO;
}

static int32_t aes128_gcm_open(uint32_t token, const uint8_t nonce[12],
    const uint8_t *additional, size_t additional_length, const uint8_t *cipher,
    size_t cipher_length, const uint8_t tag[16], uint8_t *plain)
{
    ct_ssh_resource *resource = find_resource(token, CT_SSH_RESOURCE_PSA_KEY);
    if (resource == NULL || nonce == NULL || (cipher == NULL && cipher_length != 0u) ||
        (plain == NULL && cipher_length != 0u) || tag == NULL ||
        (additional == NULL && additional_length != 0u)) return -EINVAL;
    const psa_status_t status = aead_packet(false, resource->Value.Key, nonce,
        additional, additional_length, cipher, cipher_length, plain, NULL, tag);
    return status == PSA_SUCCESS ? 0 :
        status == PSA_ERROR_INVALID_SIGNATURE ? -EBADMSG : -EIO;
}

static void zeroize(void *data, size_t length)
{
    if (data != NULL) mbedtls_platform_zeroize(data, length);
}

static const char *error_name(int32_t code)
{
    if (code < 0 && code >= -4095) return strerror(-code);
    return esp_err_to_name(code < 0 ? (esp_err_t)-code : (esp_err_t)code);
}

static const ct_managed_ssh_host_api_v2 s_api = {
    .Size = sizeof(s_api),
    .Version = CT_MANAGED_SSH_HOST_API_VERSION,
    .NetworkReady = network_ready,
    .Delay = task_delay,
    .MonotonicMilliseconds = monotonic_milliseconds,
    .TerminateDescendants = ctilde_managed_process_terminate_descendants,
    .SocketListen = socket_listen,
    .SocketAccept = socket_accept,
    .SocketWait = socket_wait,
    .SocketReceive = socket_receive,
    .SocketSend = socket_send,
    .SocketShutdown = socket_shutdown,
    .ResourceClose = resource_close,
    .Random = random_bytes,
    .ConstantTimeEqual = constant_time_equal,
    .Sha256Create = sha256_create,
    .Sha256Update = sha256_update,
    .Sha256Finish = sha256_finish,
    .X25519Create = x25519_create,
    .X25519Shared = x25519_shared,
    .P256PrivateImport = p256_private_import,
    .P256Public = p256_public,
    .P256Sign = p256_sign,
    .P256PublicImport = p256_public_import,
    .P256Verify = p256_verify,
    .Aes128GcmCreate = aes128_gcm_create,
    .Aes128GcmSeal = aes128_gcm_seal,
    .Aes128GcmOpen = aes128_gcm_open,
    .Zeroize = zeroize,
    .ErrorName = error_name
};

const ct_managed_ssh_host_api_v2 *ct_managed_ssh_host_v2(void) { return &s_api; }

static const struct esp_elfsym s_symbols[] = {
    ESP_ELFSYM_EXPORT(ct_managed_ssh_host_v2),
    ESP_ELFSYM_END
};

int ct_managed_ssh_host_initialize(void)
{
    static const ct_network_api_v1 network = {
        sizeof(ct_network_api_v1), 1u, network_ready, socket_listen, socket_accept,
        socket_wait, socket_receive, socket_send, socket_shutdown, resource_close
    };
    static const ct_crypto_api_v1 crypto = {
        sizeof(ct_crypto_api_v1), 1u, random_bytes, constant_time_equal,
        sha256_create, sha256_update, sha256_finish, x25519_create, x25519_shared,
        p256_private_import, p256_public, p256_sign, p256_public_import, p256_verify,
        aes128_gcm_create, aes128_gcm_seal, aes128_gcm_open, zeroize, resource_close
    };
    s_resource_lock = xSemaphoreCreateMutexStatic(&s_resource_lock_storage);
    if (s_resource_lock == NULL) return -ENOMEM;
    int result = ctilde_managed_register_capability(CT_CAP_NETWORK, &network);
    if (result != 0) return result;
    result = ctilde_managed_register_capability(CT_CAP_CRYPTO, &crypto);
    if (result != 0) return result;
    return esp_elf_register_symbol((esp_elf_symbol_table_t *)(uintptr_t)(const void *)s_symbols);
}
