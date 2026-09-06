#!/usr/bin/env python3
"""Exercise production pipe reads at a closed SSH window (Linux/WSL)."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
source = (root / 'runtime/esp-idf/ctilde_managed_runtime/ctilde_managed_runtime.c').read_text()
start = source.index('bool ct_managed_process_pipe_read(')
end = source.index('\nbool ct_managed_process_pipe_write(', start)
read = source[start:end]
prefix = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#define CT_CONSOLE_ENDPOINT_PIPE 1
#define pdMS_TO_TICKS(x) (x)
typedef struct { uint8_t data[8]; size_t size; } queue;
typedef struct { queue *Buffer; bool ChildrenClosed; int Kind; } ct_console_endpoint;
typedef struct { ct_console_endpoint *Streams[3]; bool OwnsParentStream[3]; } ct_process;
typedef struct { uint8_t Data[8]; } ct_managed_array;
static ct_process process;
static unsigned last_wait;
static ct_process *process_from_handle(uintptr_t h) { return h == 1 ? &process : NULL; }
static bool valid_pipe_buffer(ct_managed_array *b, int offset, int count) {
    return b && offset >= 0 && count >= 0 && offset <= 8 - count;
}
static int64_t esp_timer_get_time(void) { return 0; }
static bool ct_managed_process_cancellation_requested(void) { return false; }
static size_t xStreamBufferReceive(queue *q, void *target, size_t count, unsigned wait) {
    last_wait = wait;
    if (count > q->size) count = q->size;
    memcpy(target, q->data, count);
    memmove(q->data, q->data + count, q->size - count);
    q->size -= count;
    return count;
}
static size_t xStreamBufferBytesAvailable(queue *q) { return q->size; }
'''
test = r'''
int main(void) {
    queue q = {{1,2,3,4,5}, 5};
    ct_console_endpoint endpoint = {&q, false, CT_CONSOLE_ENDPOINT_PIPE};
    process.Streams[1] = &endpoint;
    process.OwnsParentStream[1] = true;
    ct_managed_array output = {0};
    int32_t n = -1; bool eof = true;
    assert(!ct_managed_process_pipe_read(1, 1, &output, 0, 0, 0, &n, &eof));
    assert(n == 0 && !eof && q.size == 5 && last_wait == 0);
    endpoint.ChildrenClosed = true;
    assert(!ct_managed_process_pipe_read(1, 1, &output, 0, 0, UINT32_MAX, &n, &eof));
    assert(n == 0 && !eof && q.size == 5);
    assert(ct_managed_process_pipe_read(1, 1, &output, 0, 3, 0, &n, &eof));
    assert(n == 3 && !eof && q.size == 2 && last_wait == 0);
    assert(!ct_managed_process_pipe_read(1, 1, &output, 0, 0, 0, &n, &eof));
    assert(!eof && q.size == 2);
    assert(ct_managed_process_pipe_read(1, 1, &output, 3, 5, 0, &n, &eof));
    assert(n == 2 && !eof && q.size == 0);
    assert(memcmp(output.Data, "\1\2\3\4\5", 5) == 0);
    assert(ct_managed_process_pipe_read(1, 1, &output, 0, 0, 0, &n, &eof));
    assert(n == 0 && eof);
    return 0;
}
'''
with tempfile.TemporaryDirectory(prefix='ctilde-pipe-eof-') as directory:
    work = Path(directory)
    (work / 'test.c').write_text(prefix + read + test)
    subprocess.run(['cc', '-Wall', '-Wextra', '-Werror', '-fsanitize=address,undefined',
                    '-g', str(work / 'test.c'), '-o', str(work / 'test')], check=True)
    subprocess.run([str(work / 'test')], check=True)
print('PIPE_EOF_OK: zero-window probes, buffered close, partial drain, EOF, nonblocking reads')
