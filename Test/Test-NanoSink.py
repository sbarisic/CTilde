#!/usr/bin/env python3
"""Check the production Nano output sink with bounded storage and failed writes (Linux/WSL)."""
from pathlib import Path
import subprocess

root=Path(__file__).resolve().parents[1]
out=root/'artifacts/draft051/nano-sink-test'
out.mkdir(parents=True,exist_ok=True)
source=root/'examples/ManagedShell/Modules/Nano/main/nano_sink.c'
test=out/'sink-test.c'
test.write_text('#include "'+source.as_posix()+'"\n'+r'''
#include <stdlib.h>
#include <assert.h>
#include <stdio.h>
struct ct_managed_module_descriptor_v4 { int unused; };
const ct_managed_module_descriptor_v4 ct_managed_module_v4={0};
static unsigned char output[100000];
static size_t written, largest_allocation;
static int live, fail_write, fail_allocate;
static void *allocate(size_t n,const ct_managed_module_descriptor_v4 *owner) {
    assert(owner==&ct_managed_module_v4);
    if(fail_allocate)return NULL;
    if(n>largest_allocation)largest_allocation=n;
    ++live;return calloc(1,n);
}
static void release(void *p) { if(p){--live;free(p);} }
static int32_t service(uint32_t id,void *data,size_t size) {
    if(id==18)return 0;
    assert(id==16 && size==sizeof(ct_console_transfer_v19));
    if(fail_write)return -5;
    ct_console_transfer_v19 *transfer=data;
    assert(written+transfer->Length<=sizeof(output));
    for(size_t i=0;i<transfer->Length;++i)output[written++]=transfer->Data[i];
    transfer->Count=transfer->Length;return 0;
}
static const ct_runtime_api_v23 api={.Allocate=allocate,.Free=release,.Service=service};
const ct_runtime_api_v23 *ct_runtime_api=&api;
int main(void) {
    uintptr_t sink=ct_nano_sink_create(1024);assert(sink);
    for(size_t i=0;i<80000;++i)assert(ct_nano_sink_append(sink,(uint8_t)(i%251))==0);
    assert(ct_nano_sink_flush(sink)==0 && written==80000);
    for(size_t i=0;i<written;++i)assert(output[i]==(uint8_t)(i%251));
    assert(largest_allocation<=1100);
    assert(ct_nano_sink_flush(sink)==0 && written==80000);
    fail_write=1;
    for(size_t i=0;i<1024;++i)assert(ct_nano_sink_append(sink,1)==0);
    assert(ct_nano_sink_append(sink,2)==-5);
    ct_nano_sink_reset(sink);fail_write=0;
    assert(ct_nano_sink_append(sink,3)==0 && ct_nano_sink_flush(sink)==0);
    assert(written==80001 && output[80000]==3);
    ct_nano_sink_destroy(sink);assert(live==0);
    fail_allocate=1;assert(ct_nano_sink_create(1024)==0 && live==0);
    puts("NANO_SINK_OK: 80000 output bytes, bounded allocation, write failure, reset, cleanup");
}
''')
binary=out/'sink-test'
subprocess.run(['cc','-Wall','-Wextra','-Werror','-fsanitize=address,undefined',str(test),'-o',str(binary)],check=True)
subprocess.run([str(binary)],check=True)
