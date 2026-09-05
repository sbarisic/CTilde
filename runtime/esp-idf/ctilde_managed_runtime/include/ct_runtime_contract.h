#ifndef CT_RUNTIME_CONTRACT_H
#define CT_RUNTIME_CONTRACT_H

/* Shared source for firmware declarations and compiler-generated managed C. */
#define CT_CAP_CORE UINT32_C(1)
#define CT_CAP_BUFFER UINT32_C(2)
#define CT_CAP_NETWORK UINT32_C(3)
#define CT_CAP_CRYPTO UINT32_C(4)
#define CT_CAP_FILESYSTEM UINT32_C(5)
#define CT_CAP_PROCESS UINT32_C(6)

typedef struct ct_capability_header {
    uint32_t Size;
    uint32_t MajorVersion;
} ct_capability_header;

typedef struct ct_capability_requirement {
    uint32_t Id;
    uint32_t MajorVersion;
    uint32_t MinimumSize;
} ct_capability_requirement;

typedef struct ct_core_api_v1 {
    uint32_t Size;
    uint32_t MajorVersion;
    void *(*Allocate)(size_t size, const ct_managed_module_descriptor_v4 *module);
    void (*Free)(void *value);
    void (*FinalRelease)(void *value);
    void (*RuntimeFault)(const char *code, const char *file, int32_t line);
} ct_core_api_v1;

typedef struct ct_buffer_api_v1 {
    uint32_t Size;
    uint32_t MajorVersion;
    void *(*Copy)(void *destination, const void *source, size_t length);
    void *(*Move)(void *destination, const void *source, size_t length);
    void *(*Fill)(void *destination, int value, size_t length);
    int (*Compare)(const void *left, const void *right, size_t length);
    void *(*Find)(const void *data, int value, size_t length);
    uint32_t (*HashBytes)(const void *data, size_t length);
    bool (*ValidateUtf8)(const uint8_t *data, size_t length);
    int32_t (*EncodeRune)(uint32_t scalar, uint8_t buffer[4]);
    int32_t (*FormatUnsigned)(uint64_t value, bool negative, char *output);
    int32_t (*FormatSigned)(int64_t value, char *output);
} ct_buffer_api_v1;

typedef struct ct_network_api_v1 {
    uint32_t Size;
    uint32_t MajorVersion;
    int32_t (*Ready)(void);
    int32_t (*Listen)(uint16_t port, int32_t backlog, uint32_t *handle);
    int32_t (*Accept)(uint32_t listener, uint32_t timeout, uint32_t *handle);
    int32_t (*Wait)(uint32_t handle, uint32_t events, uint32_t timeout, uint32_t *ready);
    int32_t (*Receive)(uint32_t handle, uint8_t *data, size_t length, uint32_t timeout);
    int32_t (*Send)(uint32_t handle, const uint8_t *data, size_t length, uint32_t timeout);
    int32_t (*Shutdown)(uint32_t handle, uint32_t directions);
    int32_t (*Close)(uint32_t handle);
} ct_network_api_v1;

typedef struct ct_crypto_api_v1 {
    uint32_t Size;
    uint32_t MajorVersion;
    int32_t (*Random)(uint8_t *data, size_t length);
    int32_t (*ConstantTimeEqual)(const uint8_t *left, const uint8_t *right, size_t length);
    int32_t (*Sha256Create)(uint32_t *handle);
    int32_t (*Sha256Update)(uint32_t handle, const uint8_t *data, size_t length);
    int32_t (*Sha256Finish)(uint32_t handle, uint8_t output[32]);
    int32_t (*X25519Create)(uint32_t *handle, uint8_t public_key[32]);
    int32_t (*X25519Shared)(uint32_t handle, const uint8_t public_key[32], uint8_t secret[32]);
    int32_t (*P256PrivateImport)(const uint8_t *pem, size_t length, uint32_t *handle);
    int32_t (*P256Public)(uint32_t handle, uint8_t public_key[65]);
    int32_t (*P256Sign)(uint32_t handle, const uint8_t hash[32], uint8_t signature[64]);
    int32_t (*P256PublicImport)(const uint8_t public_key[65], uint32_t *handle);
    int32_t (*P256Verify)(uint32_t handle, const uint8_t hash[32], const uint8_t signature[64]);
    int32_t (*Aes128GcmCreate)(const uint8_t key[16], uint32_t *handle);
    int32_t (*Aes128GcmSeal)(uint32_t handle, const uint8_t nonce[12],
        const uint8_t *additional, size_t additional_length, const uint8_t *plain,
        size_t length, uint8_t *cipher, uint8_t tag[16]);
    int32_t (*Aes128GcmOpen)(uint32_t handle, const uint8_t nonce[12],
        const uint8_t *additional, size_t additional_length, const uint8_t *cipher,
        size_t length, const uint8_t tag[16], uint8_t *plain);
    void (*Zeroize)(void *data, size_t length);
    int32_t (*Close)(uint32_t handle);
} ct_crypto_api_v1;

typedef struct ct_filesystem_api_v1 {
    uint32_t Size;
    uint32_t MajorVersion;
    int32_t (*Open)(const uint8_t *path, size_t length, uint8_t mode, uint8_t access, uintptr_t *handle);
    int32_t (*Read)(uintptr_t handle, uint8_t *data, size_t length, size_t *count, bool *eof);
    int32_t (*Write)(uintptr_t handle, const uint8_t *data, size_t length, size_t *count);
    int32_t (*Seek)(uintptr_t handle, int64_t offset, uint8_t origin, int64_t *position);
    int32_t (*Length)(uintptr_t handle, int64_t *length);
    int32_t (*Flush)(uintptr_t handle);
    int32_t (*Close)(uintptr_t handle);
} ct_filesystem_api_v1;

typedef struct ct_process_api_v1 {
    uint32_t Size;
    uint32_t MajorVersion;
    uintptr_t (*Current)(void);
    uint32_t (*Id)(uintptr_t handle);
    bool (*CancellationRequested)(void);
    void (*Delay)(uint32_t milliseconds);
    uint64_t (*MonotonicMilliseconds)(void);
    void (*TerminateDescendants)(uint32_t id, uint32_t grace_milliseconds);
} ct_process_api_v1;

struct ct_runtime_api_v23 {
    uint32_t Size;
    uint32_t AbiVersion;
    void *(*Allocate)(size_t size, const ct_managed_module_descriptor_v4 *module);
    void (*Free)(void *value);
    void (*FinalRelease)(void *value);
    void (*Raise)(void *exception);
    void (*RuntimeFault)(const char *code, const char *file, int32_t line);
    const ct_type_descriptor *(*RegisterType)(const void *descriptor);
    void (*UnregisterTypes)(const ct_managed_module_descriptor_v4 *module);
    ct_process_context *(*CurrentProcess)(void);
    void *(*CurrentModuleState)(const ct_managed_module_descriptor_v4 *module);
    void *(*CurrentThreadState)(void);
    void (*SetThreadState)(void *state);
    bool (*CancellationRequested)(void);
    uintptr_t (*EnterManagedCall)(const ct_managed_module_descriptor_v4 *module,
        const ct_managed_call_target_v4 *target, ct_managed_call_frame_v23 *frame);
    void (*LeaveManagedCall)(ct_managed_call_frame_v23 *frame);
    int32_t (*Service)(uint32_t service, void *payload, size_t size);
    const void *(*GetCapability)(uint32_t id, uint32_t major_version, uint32_t minimum_size);
};

#endif
