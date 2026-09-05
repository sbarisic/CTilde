#!/usr/bin/env python3
"""Check production SD log append and unmount exclusion using host OS files (Linux/WSL)."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/managed-log-storage-test'
out.mkdir(parents=True, exist_ok=True)
source = (root / 'runtime/esp-idf/ctilde_storage/ctilde_storage.c').read_text()
def function(signature):
    start = source.index(signature)
    brace = source.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]

prefix = r'''
#include <assert.h>
#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>
enum { CT_STORAGE_MOUNTED=2, CT_STORAGE_REMOVING=3 };
#define portMAX_DELAY 0
typedef struct { const char *Path; void *Mount; void *Device; } ct_monitor_mapping;
typedef struct { int State; pthread_mutex_t *AppendGate; uint64_t Generation;
    size_t MappingCount; ct_monitor_mapping Mappings[1]; void *Root; void *Card;
    bool CardInfoAvailable; } ct_monitor;
static pthread_mutex_t gate=PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t schedule=PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t changed=PTHREAD_COND_INITIALIZER;
static bool hold_write, writing, removing, invalidated, release_write, fail_write, short_write;
static char filename[80];
static ct_monitor *monitor_from(uintptr_t h) { return (ct_monitor *)h; }
static void xSemaphoreTake(pthread_mutex_t *m, int unused) { (void)unused; pthread_mutex_lock(m); }
static void xSemaphoreGive(pthread_mutex_t *m) { pthread_mutex_unlock(m); }
static int ct_storage_monitor_state(uintptr_t h) { return __atomic_load_n(&((ct_monitor *)h)->State,__ATOMIC_ACQUIRE); }
static void monitor_set_state(ct_monitor *m, int state, int error) {
    (void)error; __atomic_store_n(&m->State,state,__ATOMIC_RELEASE);
    pthread_mutex_lock(&schedule); removing=true; pthread_cond_broadcast(&changed); pthread_mutex_unlock(&schedule);
}
static void ctilde_managed_storage_invalidate_prefix(const char *p, uint64_t g) {
    (void)p; (void)g; pthread_mutex_lock(&schedule); assert(!writing); invalidated=true; pthread_mutex_unlock(&schedule);
}
static int ct_storage_fat_unmount(uintptr_t h) { (void)h; return 0; }
static int release_device_internal(void *p) { (void)p; return 0; }
static int ct_storage_sdspi_close(uintptr_t h) { (void)h; return 0; }
static int test_open(const char *path, int flags, int mode) {
    assert(strcmp(path,"/sd/run.log")==0); assert(flags & O_APPEND); assert(!(flags & O_TRUNC));
    return open(filename,flags,mode);
}
static ssize_t test_write(int fd, const void *data, size_t count) {
    pthread_mutex_lock(&schedule);
    if (hold_write) { writing=true; pthread_cond_broadcast(&changed);
        while(!release_write) { pthread_cond_wait(&changed,&schedule); }
        writing=false; }
    pthread_mutex_unlock(&schedule);
    if(fail_write) { errno=ENOSPC; return -1; }
    return write(fd,data,short_write && count>1 ? 1 : count);
}
#define open test_open
#define write test_write
'''
suffix = r'''
#undef open
#undef write
static void *append_thread(void *m) { assert(ct_storage_monitor_append_run_log((uintptr_t)m,"second\n",7)==0); return NULL; }
static void *remove_thread(void *m) { monitor_unmount(m); return NULL; }
int main(void) {
    strcpy(filename,"/tmp/ctilde-run-log-XXXXXX"); int fd=mkstemp(filename); assert(fd>=0); close(fd);
    ct_monitor m={.State=CT_STORAGE_MOUNTED,.AppendGate=&gate,.MappingCount=1,
        .Mappings={{.Path="/sd",.Mount=(void *)1}},.CardInfoAvailable=true};
    short_write=true;
    assert(ct_storage_monitor_append_run_log((uintptr_t)&m,"first\n",6)==0);
    hold_write=true; pthread_t producer,remover;
    pthread_create(&producer,NULL,append_thread,&m);
    pthread_mutex_lock(&schedule); while(!writing) pthread_cond_wait(&changed,&schedule); pthread_mutex_unlock(&schedule);
    pthread_create(&remover,NULL,remove_thread,&m);
    pthread_mutex_lock(&schedule); while(!removing) pthread_cond_wait(&changed,&schedule);
    assert(!invalidated); release_write=true; pthread_cond_broadcast(&changed); pthread_mutex_unlock(&schedule);
    pthread_join(producer,NULL); pthread_join(remover,NULL); assert(invalidated);
    assert(ct_storage_monitor_append_run_log((uintptr_t)&m,"no",2)==-ENODEV);
    __atomic_store_n(&m.State,CT_STORAGE_MOUNTED,__ATOMIC_RELEASE); hold_write=false; fail_write=true;
    assert(ct_storage_monitor_append_run_log((uintptr_t)&m,"full",4)==-EIO);
    fd=open(filename,O_RDONLY); char data[20]={0}; assert(read(fd,data,sizeof(data))==13); close(fd);
    assert(strcmp(data,"first\nsecond\n")==0); unlink(filename);
    puts("MANAGED_LOG_STORAGE_OK: append, short writes, unmount exclusion, unavailable and full storage");
}
'''
test = out / 'storage-test.c'
test.write_text(prefix + function('static void monitor_unmount(') + '\n' +
                function('int32_t ct_storage_monitor_append_run_log(') + suffix)
binary = out / 'storage-test'
subprocess.run(['cc', '-Wall', '-Wextra', '-Werror', '-pthread', '-fsanitize=address,undefined',
                str(test), '-o', str(binary)], check=True)
subprocess.run([str(binary)], check=True)
