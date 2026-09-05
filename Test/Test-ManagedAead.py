from pathlib import Path
import argparse
import subprocess
parser = argparse.ArgumentParser(description="Test the production SSH AEAD helper against host PSA crypto.")
parser.add_argument("--psa-source", type=Path, required=True)
parser.add_argument("--output", type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = (args.output or root / "artifacts/draft051/aead-host-test").resolve()
output.mkdir(parents=True, exist_ok=True)
psa = args.psa_source.resolve()
config = output / "crypto-config.h"
config.write_text("\n".join("#define " + name for name in [
    "PSA_WANT_ALG_GCM", "PSA_WANT_KEY_TYPE_AES", "PSA_WANT_ALG_SHA_256",
    "MBEDTLS_PSA_CRYPTO_C", "MBEDTLS_PLATFORM_C", "MBEDTLS_CTR_DRBG_C",
    "MBEDTLS_PSA_BUILTIN_GET_ENTROPY"]) + "\n")
source = (root/'examples/ManagedShell/main/managed_ssh_host.c').read_text()
helper = source[source.index('static psa_status_t aead_packet('):source.index('static int32_t aes128_gcm_seal(')]
harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <psa/crypto.h>
#include <mbedtls/platform_util.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Failure line %d\n", __LINE__); return 1; } } while (0)
'''+helper+r'''
int main(void) {
    CHECK(psa_crypto_init() == PSA_SUCCESS);
    uint8_t key[16] = {0}, nonce[12] = {0}, aad[4] = {1,2,3,4};
    psa_key_attributes_t attrs = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&attrs, PSA_KEY_TYPE_AES);
    psa_set_key_bits(&attrs, 128);
    psa_set_key_usage_flags(&attrs, PSA_KEY_USAGE_ENCRYPT | PSA_KEY_USAGE_DECRYPT);
    psa_set_key_algorithm(&attrs, PSA_ALG_GCM);
    psa_key_id_t id;
    CHECK(psa_import_key(&attrs, key, sizeof(key), &id) == PSA_SUCCESS);
    const size_t sizes[] = {0,1,15,16,17,255,256,257,35000};
    for (size_t s=0;s<sizeof(sizes)/sizeof(sizes[0]);s++) {
        size_t n=sizes[s], expected_length=0;
        uint8_t *plain=malloc(n+16), *cipher=malloc(n+16), *expected=malloc(n+16), *decoded=malloc(n+16), tag[16];
        CHECK(plain && cipher && expected && decoded);
        for (size_t i=0;i<n;i++) plain[i]=(uint8_t)(i*17u);
        CHECK(psa_aead_encrypt(id, PSA_ALG_GCM, nonce,12,aad,4,plain,n,expected,n+16,&expected_length)==PSA_SUCCESS);
        CHECK(aead_packet(true,id,nonce,aad,4,plain,n,cipher,tag,NULL)==PSA_SUCCESS);
        CHECK(expected_length==n+16 && memcmp(expected,cipher,n)==0 && memcmp(expected+n,tag,16)==0);
        CHECK(aead_packet(false,id,nonce,aad,4,cipher,n,decoded,NULL,tag)==PSA_SUCCESS);
        CHECK(memcmp(plain,decoded,n)==0);
        memcpy(decoded,plain,n);
        CHECK(aead_packet(true,id,nonce,aad,4,decoded,n,decoded,tag,NULL)==PSA_SUCCESS);
        CHECK(memcmp(expected,decoded,n)==0 && memcmp(expected+n,tag,16)==0);
        CHECK(aead_packet(false,id,nonce,aad,4,decoded,n,decoded,NULL,tag)==PSA_SUCCESS);
        CHECK(memcmp(plain,decoded,n)==0);
        if (n > 1) {
            CHECK(aead_packet(true,id,nonce,aad,4,plain,n,plain+1,tag,NULL)==PSA_ERROR_INVALID_ARGUMENT);
            CHECK(aead_packet(true,id,nonce,aad,4,plain+1,n,plain,tag,NULL)==PSA_ERROR_INVALID_ARGUMENT);
        }
        tag[0]^=1;
        memset(decoded,0xa5,n);
        CHECK(aead_packet(false,id,nonce,aad,4,cipher,n,decoded,NULL,tag)==PSA_ERROR_INVALID_SIGNATURE);
        for(size_t i=0;i<n;i++) CHECK(decoded[i]==0);
        memcpy(decoded,cipher,n);
        CHECK(aead_packet(false,id,nonce,aad,4,decoded,n,decoded,NULL,tag)==PSA_ERROR_INVALID_SIGNATURE);
        for(size_t i=0;i<n;i++) CHECK(decoded[i]==0);
        free(plain);free(cipher);free(expected);free(decoded);
    }
    CHECK(psa_destroy_key(id)==PSA_SUCCESS);
    puts("AEAD_OK: 9 lengths, one-shot parity, separate/in-place round trips, overlap rejection, invalid-tag zeroization");
    return 0;
}
'''
source_path = output / "aead-test.c"
source_path.write_text('#define TF_PSA_CRYPTO_CONFIG_FILE "' + config.as_posix() + '"\n' + harness)
build = output / "build"
subprocess.run(["cmake", "-S", str(psa), "-B", str(build), "-DENABLE_TESTING=OFF",
    "-DENABLE_PROGRAMS=OFF", "-DCMAKE_BUILD_TYPE=Release", "-DTF_PSA_CRYPTO_CONFIG_FILE=" + str(config)], check=True)
subprocess.run(["cmake", "--build", str(build), "--parallel", "4"], check=True)
executable = output / "aead-test"
subprocess.run(["cc", "-O2", "-I" + str(psa / "include"),
    "-I" + str(psa / "drivers/builtin/include"), "-I" + str(build / "include"), str(source_path),
    "-Wl,--start-group", str(build / "core/libtfpsacrypto.a"), str(build / "drivers/builtin/libbuiltin.a"),
    str(build / "platform/libplatform.a"), str(build / "utilities/libutilities.a"), "-Wl,--end-group",
    "-o", str(executable)], check=True)
subprocess.run([str(executable)], check=True)
