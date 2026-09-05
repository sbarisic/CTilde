#!/usr/bin/env python3
"""Exercise the production firmware log queue with host threads (Linux/WSL)."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/managed-log-test'
(out / 'freertos').mkdir(parents=True, exist_ok=True)
(out / 'freertos/FreeRTOS.h').write_text('''#pragma once
#include <stdbool.h>
#include <pthread.h>
typedef pthread_mutex_t portMUX_TYPE;
#define portMUX_INITIALIZER_UNLOCKED PTHREAD_MUTEX_INITIALIZER
#define portENTER_CRITICAL(lock) pthread_mutex_lock(lock)
#define portEXIT_CRITICAL(lock) pthread_mutex_unlock(lock)
''')
(out / 'freertos/task.h').write_text('''#pragma once
#include <stdint.h>
#include <pthread.h>
typedef void *TaskHandle_t;
static inline TaskHandle_t xTaskGetCurrentTaskHandle(void) { return (void *)(uintptr_t)pthread_self(); }
''')
(out / 'esp_log.h').write_text('''#pragma once
#include <stdarg.h>
typedef int (*vprintf_like_t)(const char *, va_list);
vprintf_like_t esp_log_set_vprintf(vprintf_like_t);
''')
(out / 'ctilde_storage.h').write_text('''#pragma once
#include <stdint.h>
#include <stddef.h>
int32_t ct_storage_monitor_append_run_log(uintptr_t, const char *, size_t);
''')
source = root / 'examples/ManagedShell/main/managed_log.c'
test = out / 'log-test.c'
test.write_text('#include "' + source.as_posix() + '"\n' + r'''
#include <assert.h>
#include <stdlib.h>
static char disk[16384];
static size_t disk_count;
static unsigned console_calls, writes;
static bool available = true, recursive;
static int capture(const char *format, va_list arguments) {
    char buffer[1024]; console_calls++; return vsnprintf(buffer, sizeof(buffer), format, arguments);
}
vprintf_like_t esp_log_set_vprintf(vprintf_like_t callback) { assert(callback); return capture; }
static int emit(const char *format, ...) {
    va_list args; va_start(args, format); int count = log_output(format, args); va_end(args); return count;
}
int32_t ct_storage_monitor_append_run_log(uintptr_t monitor, const char *bytes, size_t count) {
    assert(monitor == 1); writes++;
    if (recursive) { recursive = false; emit("storage write failure\n"); }
    if (!available) return -1;
    assert(disk_count + count <= sizeof(disk));
    memcpy(disk + disk_count, bytes, count); disk_count += count; return 0;
}
static void *producer(void *arg) {
    for (int i=0; i<25; i++) emit("%c\n", (int)(uintptr_t)arg);
    return NULL;
}
int main(void) {
    ct_managed_log_initialize();
    emit("I (1) wifi: connected\n"); assert(writes == 0 && console_calls == 0);
    ct_managed_log_drain(1); assert(disk_count == strlen("I (1) wifi: connected\n") &&
        memcmp(disk, "I (1) wifi: connected\n", disk_count) == 0);
    size_t before = disk_count; ct_managed_log_drain(1); assert(disk_count == before);
    pthread_t threads[4];
    for (uintptr_t i=0; i<4; i++) assert(pthread_create(&threads[i], NULL, producer, (void *)('A'+i)) == 0);
    for (int i=0; i<4; i++) pthread_join(threads[i], NULL);
    ct_managed_log_drain(1); assert(disk_count == before + 200 && console_calls == 0);
    unsigned counts[4]={0};
    for (size_t i=before; i<disk_count; i+=2) {
        assert(disk[i]>='A' && disk[i]<='D' && disk[i+1]=='\n'); counts[disk[i]-'A']++;
    }
    for (size_t i=0; i<4; i++) assert(counts[i]==25);
    for (int i=0; i<2100; i++) emit("x");
    assert(console_calls == 52); ct_managed_log_drain(1); assert(s_count == 0);
    char long_line[600]; memset(long_line, 'z', 599); long_line[599]=0;
    emit("%s", long_line); assert(console_calls == 53 && s_count == 0);
    FILE *fallback = tmpfile(); assert(fallback);
    int saved = dup(fileno(stdout)); assert(saved >= 0);
    fflush(stdout); dup2(fileno(fallback), fileno(stdout));
    available = false; recursive = true; before = disk_count;
    emit("unavailable\n"); ct_managed_log_drain(1); fflush(stdout);
    assert(s_count == 0 && disk_count == before && console_calls == 54);
    rewind(fallback); char bytes[20] = {0}; assert(fread(bytes,1,12,fallback)==12);
    assert(strcmp(bytes,"unavailable\n")==0);
    dup2(saved,fileno(stdout)); close(saved); fclose(fallback);
    available = true; emit("remounted\n"); ct_managed_log_drain(1);
    assert(disk_count == before + 10);
    puts("MANAGED_LOG_OK: append, concurrency, overflow, unavailable storage, recursion, remount");
}
'''.replace('#include <assert.h>', '#include <assert.h>\n#include <unistd.h>'))
binary = out / 'log-test'
subprocess.run(['cc', '-I'+str(out), '-Wall', '-Wextra', '-Werror', '-pthread',
                '-fsanitize=address,undefined', str(test), '-o', str(binary)], check=True)
subprocess.run([str(binary)], check=True)
