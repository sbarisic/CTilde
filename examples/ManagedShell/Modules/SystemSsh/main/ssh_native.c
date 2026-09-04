#include "managed_ssh_host_api.h"

#include <errno.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct ct_ssh_managed_bytes {
    void *Type;
    uint32_t IdentityHash;
    uint32_t RefCount;
    void *ReleaseNext;
    int32_t Length;
    uint8_t Data[];
} ct_ssh_managed_bytes;

static bool array_slice(ct_ssh_managed_bytes *array, int32_t offset, int32_t count,
    uint8_t **data)
{
    if (array == NULL || offset < 0 || count < 0 || offset > array->Length - count) return false;
    *data = array->Data + offset;
    return true;
}

static const ct_managed_ssh_host_api_v2 *host(void)
{
    const ct_managed_ssh_host_api_v2 *api = ct_managed_ssh_host_v2();
    return api != NULL && api->Version == CT_MANAGED_SSH_HOST_API_VERSION &&
        api->Size >= sizeof(*api) ? api : NULL;
}

int32_t ct_ssh_network_ready(void)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? 0 : api->NetworkReady();
}

void ct_ssh_delay(uint32_t milliseconds)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api != NULL) api->Delay(milliseconds);
}

int64_t ct_ssh_monotonic_milliseconds(void)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? 0 : (int64_t)api->MonotonicMilliseconds();
}

void ct_ssh_terminate_descendants(uint32_t process_id, uint32_t grace_milliseconds)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api != NULL) api->TerminateDescendants(process_id, grace_milliseconds);
}

int32_t ct_ssh_listen(int32_t port, int32_t backlog, uint32_t *token)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    if (port < 1 || port > 65535 || backlog < 1 || token == NULL) return -EINVAL;
    return api->SocketListen((uint16_t)port, backlog, token);
}

int32_t ct_ssh_accept(uint32_t listener, uint32_t timeout, uint32_t *token)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->SocketAccept(listener, timeout, token);
}

int32_t ct_ssh_wait(uint32_t token, uint32_t events, uint32_t timeout, uint32_t *ready)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->SocketWait(token, events, timeout, ready);
}

int32_t ct_ssh_receive(uint32_t token, uint8_t *data, size_t length, uint32_t timeout)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->SocketReceive(token, data, length, timeout);
}

int32_t ct_ssh_array_receive(uint32_t token, ct_ssh_managed_bytes *array,
    int32_t offset, int32_t count, uint32_t timeout)
{
    uint8_t *data = NULL;
    if (!array_slice(array, offset, count, &data)) return -EINVAL;
    return ct_ssh_receive(token, data, (size_t)count, timeout);
}

int32_t ct_ssh_send(uint32_t token, const uint8_t *data, size_t length, uint32_t timeout)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->SocketSend(token, data, length, timeout);
}

int32_t ct_ssh_array_send(uint32_t token, ct_ssh_managed_bytes *array,
    int32_t offset, int32_t count, uint32_t timeout)
{
    uint8_t *data = NULL;
    if (!array_slice(array, offset, count, &data)) return -EINVAL;
    return ct_ssh_send(token, data, (size_t)count, timeout);
}

int32_t ct_ssh_shutdown(uint32_t token, uint32_t directions)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->SocketShutdown(token, directions);
}

int32_t ct_ssh_close(uint32_t token)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->ResourceClose(token);
}

int32_t ct_ssh_random(uint8_t *data, size_t length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->Random(data, length);
}

int32_t ct_ssh_sha256(const uint8_t *data, size_t length, uint8_t *output, size_t output_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    if (output == NULL || output_length != 32u) return -EINVAL;
    uint32_t token = 0u;
    int32_t result = api->Sha256Create(&token);
    if (result == 0) result = api->Sha256Update(token, data, length);
    if (result == 0) result = api->Sha256Finish(token, output);
    if (token != 0u) (void)api->ResourceClose(token);
    return result;
}

int32_t ct_ssh_array_sha256(ct_ssh_managed_bytes *input, int32_t offset, int32_t count,
    ct_ssh_managed_bytes *output)
{
    uint8_t *data = NULL;
    if (!array_slice(input, offset, count, &data) || output == NULL || output->Length != 32)
        return -EINVAL;
    return ct_ssh_sha256(data, (size_t)count, output->Data, 32u);
}

void ct_ssh_array_zeroize(ct_ssh_managed_bytes *data)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api != NULL && data != NULL) api->Zeroize(data->Data, (size_t)data->Length);
}

int32_t ct_ssh_x25519_create(uint32_t *token, uint8_t *public_key, size_t public_key_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return token == NULL || public_key == NULL || public_key_length != 32u ? -EINVAL :
        api->X25519Create(token, public_key);
}

int32_t ct_ssh_array_x25519_create(uint32_t *token, ct_ssh_managed_bytes *public_key)
{
    return public_key == NULL ? -EINVAL :
        ct_ssh_x25519_create(token, public_key->Data, (size_t)public_key->Length);
}

int32_t ct_ssh_x25519_shared(uint32_t token, const uint8_t *peer, size_t peer_length,
    uint8_t *secret, size_t secret_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return peer == NULL || peer_length != 32u || secret == NULL || secret_length != 32u ?
        -EINVAL : api->X25519Shared(token, peer, secret);
}

int32_t ct_ssh_array_x25519_shared(uint32_t token, ct_ssh_managed_bytes *peer,
    ct_ssh_managed_bytes *secret)
{
    return peer == NULL || secret == NULL ? -EINVAL : ct_ssh_x25519_shared(token,
        peer->Data, (size_t)peer->Length, secret->Data, (size_t)secret->Length);
}

int32_t ct_ssh_p256_private_import(const uint8_t *pem, size_t length, uint32_t *token)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    return api == NULL ? -ENOSYS : api->P256PrivateImport(pem, length, token);
}

int32_t ct_ssh_array_p256_private_import(ct_ssh_managed_bytes *pem, uint32_t *token)
{
    return pem == NULL ? -EINVAL :
        ct_ssh_p256_private_import(pem->Data, (size_t)pem->Length, token);
}

int32_t ct_ssh_p256_public(uint32_t token, uint8_t *key, size_t key_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return key == NULL || key_length != 65u ? -EINVAL : api->P256Public(token, key);
}

int32_t ct_ssh_array_p256_public(uint32_t token, ct_ssh_managed_bytes *key)
{
    return key == NULL ? -EINVAL : ct_ssh_p256_public(token, key->Data, (size_t)key->Length);
}

int32_t ct_ssh_p256_sign(uint32_t token, const uint8_t *hash, size_t hash_length,
    uint8_t *signature, size_t signature_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return hash == NULL || hash_length != 32u || signature == NULL || signature_length != 64u ?
        -EINVAL : api->P256Sign(token, hash, signature);
}

int32_t ct_ssh_array_p256_sign(uint32_t token, ct_ssh_managed_bytes *hash,
    ct_ssh_managed_bytes *signature)
{
    return hash == NULL || signature == NULL ? -EINVAL : ct_ssh_p256_sign(token,
        hash->Data, (size_t)hash->Length, signature->Data, (size_t)signature->Length);
}

int32_t ct_ssh_p256_public_import(const uint8_t *key, size_t key_length, uint32_t *token)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return key == NULL || key_length != 65u ? -EINVAL : api->P256PublicImport(key, token);
}

int32_t ct_ssh_array_p256_public_import(ct_ssh_managed_bytes *key, uint32_t *token)
{
    return key == NULL ? -EINVAL :
        ct_ssh_p256_public_import(key->Data, (size_t)key->Length, token);
}

int32_t ct_ssh_p256_verify(uint32_t token, const uint8_t *hash, size_t hash_length,
    const uint8_t *signature, size_t signature_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return hash == NULL || hash_length != 32u || signature == NULL || signature_length != 64u ?
        -EINVAL : api->P256Verify(token, hash, signature);
}

int32_t ct_ssh_array_p256_verify(uint32_t token, ct_ssh_managed_bytes *hash,
    ct_ssh_managed_bytes *signature)
{
    return hash == NULL || signature == NULL ? -EINVAL : ct_ssh_p256_verify(token,
        hash->Data, (size_t)hash->Length, signature->Data, (size_t)signature->Length);
}

int32_t ct_ssh_aes_create(const uint8_t *key, size_t key_length, uint32_t *token)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    return key == NULL || key_length != 16u ? -EINVAL : api->Aes128GcmCreate(key, token);
}

int32_t ct_ssh_array_aes_create(ct_ssh_managed_bytes *key, uint32_t *token)
{
    return key == NULL ? -EINVAL : ct_ssh_aes_create(key->Data, (size_t)key->Length, token);
}

int32_t ct_ssh_aes_seal(uint32_t token, const uint8_t *nonce, size_t nonce_length,
    const uint8_t *additional, size_t additional_length, const uint8_t *plain,
    size_t plain_length, uint8_t *cipher, size_t cipher_length, uint8_t *tag,
    size_t tag_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    if (nonce == NULL || nonce_length != 12u || cipher_length != plain_length ||
        tag == NULL || tag_length != 16u) return -EINVAL;
    return api->Aes128GcmSeal(token, nonce, additional, additional_length, plain,
        plain_length, cipher, tag);
}

int32_t ct_ssh_aes_open(uint32_t token, const uint8_t *nonce, size_t nonce_length,
    const uint8_t *additional, size_t additional_length, const uint8_t *cipher,
    size_t cipher_length, const uint8_t *tag, size_t tag_length, uint8_t *plain,
    size_t plain_length)
{
    const ct_managed_ssh_host_api_v2 *api = host();
    if (api == NULL) return -ENOSYS;
    if (nonce == NULL || nonce_length != 12u || plain_length != cipher_length ||
        tag == NULL || tag_length != 16u) return -EINVAL;
    return api->Aes128GcmOpen(token, nonce, additional, additional_length, cipher,
        cipher_length, tag, plain);
}

int32_t ct_ssh_array_aes_seal(uint32_t token, ct_ssh_managed_bytes *nonce,
    ct_ssh_managed_bytes *additional, ct_ssh_managed_bytes *plain,
    ct_ssh_managed_bytes *cipher, ct_ssh_managed_bytes *tag)
{
    if (nonce == NULL || additional == NULL || plain == NULL || cipher == NULL || tag == NULL)
        return -EINVAL;
    return ct_ssh_aes_seal(token, nonce->Data, (size_t)nonce->Length,
        additional->Data, (size_t)additional->Length, plain->Data, (size_t)plain->Length,
        cipher->Data, (size_t)cipher->Length, tag->Data, (size_t)tag->Length);
}

int32_t ct_ssh_array_aes_open(uint32_t token, ct_ssh_managed_bytes *nonce,
    ct_ssh_managed_bytes *additional, ct_ssh_managed_bytes *cipher,
    ct_ssh_managed_bytes *tag, ct_ssh_managed_bytes *plain)
{
    if (nonce == NULL || additional == NULL || cipher == NULL || tag == NULL || plain == NULL)
        return -EINVAL;
    return ct_ssh_aes_open(token, nonce->Data, (size_t)nonce->Length,
        additional->Data, (size_t)additional->Length, cipher->Data, (size_t)cipher->Length,
        tag->Data, (size_t)tag->Length, plain->Data, (size_t)plain->Length);
}
