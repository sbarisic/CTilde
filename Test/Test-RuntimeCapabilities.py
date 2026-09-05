#!/usr/bin/env python3
"""Run the production capability registry against host threads (Linux/WSL)."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/runtime-capability-test'
(out / 'freertos').mkdir(parents=True, exist_ok=True)
(out / 'freertos/FreeRTOS.h').write_text('''#pragma once
#include <pthread.h>
typedef pthread_mutex_t portMUX_TYPE;
#define portMUX_INITIALIZER_UNLOCKED PTHREAD_MUTEX_INITIALIZER
#define portENTER_CRITICAL(p) pthread_mutex_lock(p)
#define portEXIT_CRITICAL(p) pthread_mutex_unlock(p)
''')
runtime = root / 'runtime/esp-idf/ctilde_managed_runtime'
test = out / 'capability-test.c'
test.write_text('#include "' + (runtime/'ctilde_runtime_capabilities.c').as_posix() + '"\n'
               + '#include "' + (runtime/'ctilde_runtime_buffer.c').as_posix() + '"\n' + r'''
#include <assert.h>
#include <stdio.h>
#include <inttypes.h>
#include <string.h>
static const ct_capability_header table = { sizeof(table), 1 };
static const ct_capability_header other = { sizeof(other), 1 };
static const ct_capability_header invalid = { 4, 1 };
static void *lookup(void *unused) {
    (void)unused;
    for (int i=0; i<10000; ++i) assert(ctilde_managed_get_capability(CT_CAP_CORE,1,sizeof(table))==&table);
    return NULL;
}
int main(void) {
    const uint64_t unsigned_values[]={0,1,9,10,99,100,UINT32_MAX,UINT64_MAX};
    const int64_t signed_values[]={0,1,-1,INT32_MIN,INT32_MAX,INT64_MIN,INT64_MAX};
    char expected[24], actual[24];
    for (size_t i=0; i<sizeof(unsigned_values)/sizeof(unsigned_values[0]); ++i) {
        memset(actual,0xa5,sizeof(actual));
        int length=snprintf(expected,sizeof(expected),"%" PRIu64,unsigned_values[i]);
        assert(ctilde_buffer_format_unsigned(unsigned_values[i],false,actual)==length);
        assert(memcmp(actual,expected,(size_t)length)==0 && (unsigned char)actual[length]==0xa5);
    }
    for (size_t i=0; i<sizeof(signed_values)/sizeof(signed_values[0]); ++i) {
        memset(actual,0xa5,sizeof(actual));
        int length=snprintf(expected,sizeof(expected),"%" PRId64,signed_values[i]);
        assert(ctilde_buffer_format_signed(signed_values[i],actual)==length);
        assert(memcmp(actual,expected,(size_t)length)==0 && (unsigned char)actual[length]==0xa5);
    }
    assert(ctilde_buffer_format_unsigned(0,true,actual)==2 && memcmp(actual,"-0",2)==0);
    assert(ctilde_buffer_hash_bytes(NULL,0)==UINT32_C(2166136261));
    assert(ctilde_buffer_hash_bytes("hello",5)==UINT32_C(0x4f9f2cab));
    assert(ctilde_buffer_validate_utf8(NULL,0));
    for (uint32_t scalar=0; scalar<=0x10ffff; ++scalar) {
        if (scalar>=0xd800 && scalar<=0xdfff) continue;
        uint8_t bytes[4];
        int32_t count=ctilde_buffer_encode_rune(scalar,bytes);
        assert(count>=1 && count<=4);
        assert(ctilde_buffer_validate_utf8(bytes,(size_t)count));
        if (count>1) assert(!ctilde_buffer_validate_utf8(bytes,(size_t)count-1));
        uint32_t decoded=bytes[0] & (count==1 ? 0x7f : count==2 ? 0x1f : count==3 ? 0xf : 7);
        for (int32_t i=1; i<count; ++i) decoded=(decoded<<6)|(bytes[i]&0x3f);
        assert(decoded==scalar);
    }
    const uint8_t malformed[][4]={{0xc0,0x80},{0xed,0xa0,0x80},{0xf4,0x90,0x80,0x80},{0x80},{0xe2,0x28,0xa1},{0xff}};
    const size_t lengths[]={2,3,4,1,3,1};
    for (size_t i=0; i<sizeof(lengths)/sizeof(lengths[0]); ++i)
        assert(!ctilde_buffer_validate_utf8(malformed[i],lengths[i]));
    assert(ctilde_managed_register_capability(0,&table)==-EINVAL);
    assert(ctilde_managed_register_capability(1,NULL)==-EINVAL);
    assert(ctilde_managed_register_capability(1,&invalid)==-EINVAL);
    assert(ctilde_managed_register_capability(CT_CAP_CORE,&table)==0);
    assert(ctilde_managed_register_capability(CT_CAP_CORE,&table)==0);
    assert(ctilde_managed_register_capability(CT_CAP_CORE,&other)==-EEXIST);
    assert(ctilde_managed_get_capability(CT_CAP_CORE,2,sizeof(table))==NULL);
    assert(ctilde_managed_get_capability(CT_CAP_CORE,1,sizeof(table)+1)==NULL);
    assert(ctilde_managed_get_capability(999,1,sizeof(table))==NULL);
    assert(ctilde_managed_register_capability(CT_CAP_BUFFER,&table)==-EPERM);
    assert(ctilde_managed_register_capability(CT_CAP_CORE,&table)==0);
    pthread_t tasks[4];
    for (int i=0; i<4; ++i) assert(pthread_create(&tasks[i],NULL,lookup,NULL)==0);
    for (int i=0; i<4; ++i) pthread_join(tasks[i],NULL);
    puts("RUNTIME_CAPABILITIES_OK: validation, missing/version/size rejection, frozen registration, 40000 concurrent reads, hash vectors, all Unicode scalars and malformed UTF-8");
}
''')
binary = out / 'capability-test'
subprocess.run(['cc', '-I'+str(out), '-I'+str(runtime/'include'), '-Wall', '-Wextra', '-Werror',
                '-pthread', '-fsanitize=address,undefined', str(test), '-o', str(binary)], check=True)
subprocess.run([str(binary)], check=True)

