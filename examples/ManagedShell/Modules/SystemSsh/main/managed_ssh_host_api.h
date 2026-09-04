#pragma once

#include <stddef.h>
#include <stdint.h>

#define CT_MANAGED_SSH_HOST_API_VERSION 2u
#define CT_MANAGED_SSH_WAIT_READ 1u
#define CT_MANAGED_SSH_WAIT_WRITE 2u
#define CT_MANAGED_SSH_SHUTDOWN_READ 1u
#define CT_MANAGED_SSH_SHUTDOWN_WRITE 2u

typedef struct ct_managed_ssh_host_api_v2 {
    uint32_t Size;
    uint32_t Version;
    int32_t (*NetworkReady)(void);
    void (*Delay)(uint32_t milliseconds);
    uint64_t (*MonotonicMilliseconds)(void);
    void (*TerminateDescendants)(uint32_t process_id, uint32_t grace_milliseconds);
    int32_t (*SocketListen)(uint16_t port, int32_t backlog, uint32_t *token);
    int32_t (*SocketAccept)(uint32_t listener, uint32_t timeout_milliseconds, uint32_t *token);
    int32_t (*SocketWait)(uint32_t token, uint32_t events, uint32_t timeout_milliseconds,
        uint32_t *ready_events);
    int32_t (*SocketReceive)(uint32_t token, uint8_t *data, size_t length,
        uint32_t timeout_milliseconds);
    int32_t (*SocketSend)(uint32_t token, const uint8_t *data, size_t length,
        uint32_t timeout_milliseconds);
    int32_t (*SocketShutdown)(uint32_t token, uint32_t directions);
    int32_t (*ResourceClose)(uint32_t token);
    int32_t (*Random)(uint8_t *data, size_t length);
    int32_t (*ConstantTimeEqual)(const uint8_t *left, const uint8_t *right, size_t length);
    int32_t (*Sha256Create)(uint32_t *token);
    int32_t (*Sha256Update)(uint32_t token, const uint8_t *data, size_t length);
    int32_t (*Sha256Finish)(uint32_t token, uint8_t output[32]);
    int32_t (*X25519Create)(uint32_t *token, uint8_t public_key[32]);
    int32_t (*X25519Shared)(uint32_t token, const uint8_t peer_public_key[32],
        uint8_t shared_secret[32]);
    int32_t (*P256PrivateImport)(const uint8_t *pem, size_t length, uint32_t *token);
    int32_t (*P256Public)(uint32_t token, uint8_t public_key[65]);
    int32_t (*P256Sign)(uint32_t token, const uint8_t hash[32], uint8_t signature[64]);
    int32_t (*P256PublicImport)(const uint8_t public_key[65], uint32_t *token);
    int32_t (*P256Verify)(uint32_t token, const uint8_t hash[32],
        const uint8_t signature[64]);
    int32_t (*Aes128GcmCreate)(const uint8_t key[16], uint32_t *token);
    int32_t (*Aes128GcmSeal)(uint32_t token, const uint8_t nonce[12],
        const uint8_t *additional, size_t additional_length, const uint8_t *plain,
        size_t plain_length, uint8_t *cipher, uint8_t tag[16]);
    int32_t (*Aes128GcmOpen)(uint32_t token, const uint8_t nonce[12],
        const uint8_t *additional, size_t additional_length, const uint8_t *cipher,
        size_t cipher_length, const uint8_t tag[16], uint8_t *plain);
    void (*Zeroize)(void *data, size_t length);
    const char *(*ErrorName)(int32_t code);
} ct_managed_ssh_host_api_v2;

const ct_managed_ssh_host_api_v2 *ct_managed_ssh_host_v2(void);
