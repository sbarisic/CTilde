#!/usr/bin/env python3
"""Exercise production graph and argument storage with allocation failure injection (Linux/WSL)."""
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[1]
source = (root / 'runtime/esp-idf/ctilde_managed_runtime/ctilde_managed_runtime.c').read_text()
out = root / 'artifacts/ssh-xip/process-storage'
out.mkdir(parents=True, exist_ok=True)

def function(name):
    match = re.search(r'^static (?:int|QueueHandle_t) ' + name + r'\([^;]*?\)\s*\{', source, re.M)
    if not match:
        raise RuntimeError('Missing production function: ' + name)
    start = source.index('{', match.start())
    end, depth = start + 1, 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[match.start():end]

start = source.index('static uintptr_t start_process_core(')
end = source.index('    char path_buffer[CT_MODULE_PATH_MAX];', start)
validation = source[start:end].replace('start_process_core(', 'validate_start(') + '    return 1;\n}\n'
contract = (root / 'runtime/esp-idf/ctilde_managed_runtime/include/ct_runtime_contract.h').read_text()
options = re.search(r'typedef struct ct_process_start_options_v1 \{.*?\} ct_process_start_options_v1;', contract, re.S).group()

prefix = r"""
#include <assert.h>
#include <errno.h>
#include <pthread.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#define CT_MAX_PROCESS_MODULES 32
#define CT_MAILBOX_DEPTH 16
#define CT_MAX_ARGUMENTS 32
#define CT_PROCESS_PIPE_BYTES 8192
static bool s_initialized=true;
#define CT_RUNTIME_GATE_STOPPED UINT32_C(0x80000000)
typedef void *QueueHandle_t;
typedef struct ct_message { size_t Length; } ct_message;
#define portMAX_DELAY 0
static pthread_mutex_t registry = PTHREAD_MUTEX_INITIALIZER;
#define s_registry (&registry)
#define xSemaphoreTake(p,t) pthread_mutex_lock(p)
#define xSemaphoreGive(p) pthread_mutex_unlock(p)
typedef struct ct_module ct_module;
typedef struct { size_t StaticStateSize; } descriptor;
struct ct_module {
    ct_module *Dependencies[32]; uint32_t DependencyCount;
    ct_module **ProcessTopology; uint32_t ProcessTopologyCount;
    descriptor *Descriptor;
};
typedef struct { void *State; unsigned char Initialized; } ct_module_instance;
typedef struct {
    ct_module_instance *Instances; uint32_t InstanceCount;
    bool Cleaned; uint32_t RuntimeGate; QueueHandle_t Mailbox;
    void *ArgumentStorage; char **Arguments; size_t *ArgumentLengths; int32_t ArgumentCount;
} ct_process;
typedef struct { const uint8_t *Data; size_t Length; } ct_process_utf8_v1;
static int allocation, fail_at, live;
static void *tracked_malloc(size_t size) {
    if (++allocation == fail_at) return NULL;
    void *p = malloc(size); if (p) ++live; return p;
}
static void *tracked_calloc(size_t n, size_t size) {
    void *p = tracked_malloc(n * size); if (p) memset(p, 0, n * size); return p;
}
static void tracked_free(void *p) { if(p) { --live; free(p); } }
#define malloc tracked_malloc
#define calloc tracked_calloc
#define free tracked_free
static QueueHandle_t xQueueCreate(size_t n, size_t size) { return calloc(n, size); }
"""
suffix = r"""
static void *mailbox_worker(void *argument) {
    ct_process *p = argument;
    for(int i=0;i<1000;++i) {
        pthread_mutex_lock(&registry);
        assert(ensure_process_mailbox(p) != NULL);
        pthread_mutex_unlock(&registry);
    }
    return NULL;
}
static void cleanup(ct_process *p) {
    for (uint32_t i=0; i<p->InstanceCount; ++i) free(p->Instances[i].State);
    free(p->Instances); free(p->ArgumentStorage); memset(p,0,sizeof(*p));
}
int main(void) {
    ct_process_utf8_v1 path={(const uint8_t *)"shell.ctm",9};
    ct_process_start_options_v1 options={sizeof(options),0,0,0,0};
    assert(validate_start(&path,NULL,0,&options)==1);
    assert(validate_start(&path,NULL,0,NULL)==0);
    options.Size=sizeof(options)-1; assert(validate_start(&path,NULL,0,&options)==0);
    options.Size=sizeof(options); options.Flags=8; assert(validate_start(&path,NULL,0,&options)==0);
    options.Flags=1;
    uint32_t capacities[]={0,255,256,1024,8192,8193,UINT32_MAX};
    for(unsigned i=0;i<sizeof(capacities)/sizeof(capacities[0]);++i) {
        options.InputBufferBytes=capacities[i];
        assert(validate_start(&path,NULL,0,&options)==(capacities[i]>=256 && capacities[i]<=8192));
    }
    options.Flags=0;
    assert(validate_start(&path,NULL,1,&options)==0);
    ct_process_utf8_v1 overflow={(const uint8_t *)"x",SIZE_MAX};
    assert(validate_start(&path,&overflow,1,&options)==0);
    path.Length=0; assert(validate_start(&path,NULL,0,&options)==0);

    descriptor d = {7};
    ct_module leaf={.Descriptor=&d}, left={.Descriptor=&d}, right={.Descriptor=&d}, root={.Descriptor=&d};
    left.Dependencies[0]=right.Dependencies[0]=&leaf;
    left.DependencyCount=right.DependencyCount=1;
    root.Dependencies[0]=&left; root.Dependencies[1]=&right; root.DependencyCount=2;
    for(int failure=1; failure<=6; ++failure) {
        allocation=0; fail_at=failure; ct_process p={0};
        assert(add_instance_graph(&p,&root)==-ENOMEM);
        cleanup(&p); free(root.ProcessTopology); root.ProcessTopology=NULL;
        assert(live==0);
    }
    fail_at=0; allocation=0;
    ct_process a={0}, b={0};
    assert(add_instance_graph(&a,&root)==0 && a.InstanceCount==4);
    assert(root.ProcessTopology[0]==&leaf && root.ProcessTopology[3]==&root);
    ct_module **topology=root.ProcessTopology;
    assert(add_instance_graph(&b,&root)==0 && root.ProcessTopology==topology);
    for(int i=0;i<4;++i) assert(a.Instances[i].State != b.Instances[i].State);
    cleanup(&a); cleanup(&b); free(root.ProcessTopology); assert(live==0);
    uint8_t first[]={'a',0,'b'}, second[]="test";
    ct_process_utf8_v1 args[]={{first,3},{second,4}};
    size_t bytes=2*(sizeof(char*)+sizeof(size_t))+9;
    allocation=0; fail_at=1;
    assert(copy_process_arguments(&a,args,2,bytes)==-ENOMEM); cleanup(&a); assert(live==0);
    fail_at=0;
    assert(copy_process_arguments(&a,args,2,bytes)==0);
    memset(first,0xff,sizeof(first)); memset(second,0xff,sizeof(second));
    assert(a.ArgumentCount==2 && a.ArgumentLengths[0]==3 && a.ArgumentLengths[1]==4);
    assert(memcmp(a.Arguments[0],"a\0b\0",4)==0 && memcmp(a.Arguments[1],"test\0",5)==0);
    cleanup(&a); assert(live==0);
    assert(copy_process_arguments(&a,NULL,0,0)==0 && a.ArgumentStorage==NULL);
    cleanup(&a);
    a.Cleaned=true; assert(ensure_process_mailbox(&a)==NULL);
    a.Cleaned=false; a.RuntimeGate=CT_RUNTIME_GATE_STOPPED; assert(ensure_process_mailbox(&a)==NULL);
    a.RuntimeGate=0; allocation=0; fail_at=1;
    assert(ensure_process_mailbox(&a)==NULL && a.Mailbox==NULL);
    fail_at=0; allocation=0;
    pthread_t threads[8];
    for(int i=0;i<8;++i) assert(pthread_create(&threads[i],NULL,mailbox_worker,&a)==0);
    for(int i=0;i<8;++i) pthread_join(threads[i],NULL);
    assert(allocation==1 && live==1); free(a.Mailbox); a.Mailbox=NULL; assert(live==0);
    puts("PROCESS_STORAGE_OK: lazy mailbox races and failure,  diamond graph, shared topology, independent state, six allocation failures, synchronous UTF-8 copying, empty arguments");
}
"""
fixture = out / 'process-storage.c'
fixture.write_text(prefix + options + validation + '\n'.join(function(n) for n in ['append_process_topology','add_instance_graph','copy_process_arguments','ensure_process_mailbox']) + suffix)
executable = out / 'process-storage'
subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-fsanitize=address,undefined','-g','-pthread',str(fixture),'-o',str(executable)],check=True)
subprocess.run([str(executable)],check=True)
