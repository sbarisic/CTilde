#pragma once

#include <stddef.h>
#include <stdint.h>

#define CT_MANAGED_SSH_HOST_API_VERSION 1u

typedef struct ct_managed_ssh_host_api_v1 {
    uint32_t Size;
    uint32_t Version;
    int32_t (*NetworkReady)(void);
    void (*Delay)(uint32_t milliseconds);
    int32_t (*Listen)(uint16_t port, int32_t backlog);
    int32_t (*Accept)(int32_t listener, uint32_t timeout_milliseconds);
    int32_t (*Receive)(int32_t socket, uint8_t *data, size_t length, uint32_t timeout_milliseconds);
    int32_t (*Send)(int32_t socket, const uint8_t *data, size_t length, uint32_t timeout_milliseconds);
    int32_t (*Close)(int32_t socket);
    int32_t (*Random)(uint8_t *data, size_t length);
    int32_t (*Sha256)(const uint8_t *data, size_t length, uint8_t output[32]);
    int32_t (*ConstantTimeEqual)(const uint8_t *left, const uint8_t *right, size_t length);
    void (*Zeroize)(void *data, size_t length);
    const char *(*ErrorName)(int32_t code);
} ct_managed_ssh_host_api_v1;

const ct_managed_ssh_host_api_v1 *ct_managed_ssh_host_v1(void);
