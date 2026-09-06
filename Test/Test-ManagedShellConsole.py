#!/usr/bin/env python3
"""Verify native free output uses the process console bridge (Linux/WSL)."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
native = root / 'examples/ManagedShell/Modules/Shell/main'
harness = r'''
#include "diagnostics_host_api.h"
#include <assert.h>
#include <string.h>
static char captured[2048];
static size_t used;
static bool available = true;
static int read_stage;
size_t ct_runtime_console_read(uint8_t *data, size_t length, bool *eof) {
    assert(length == 1);
    *eof = read_stage == 2;
    if (read_stage++ == 1) { *data = 'X'; return 1; }
    return 0;
}
void ct_runtime_console_write(const uint8_t *data, size_t length) {
    assert(used + length < sizeof(captured));
    memcpy(captured + used, data, length);
    used += length;
    captured[used] = 0;
}
static size_t total(ct_diagnostics_heap_kind kind) {
    return kind == CT_DIAGNOSTICS_HEAP_SPIRAM ? 0 : 100000;
}
static void info(ct_diagnostics_heap_info *output, ct_diagnostics_heap_kind kind) {
    (void)kind;
    output->TotalFreeBytes = 25000;
    output->MinimumFreeBytes = 20000;
}
static const ct_managed_diagnostics_host_api_v1 api = {
    .Size = sizeof(api), .Version = CT_MANAGED_DIAGNOSTICS_HOST_API_VERSION,
    .HeapGetInfo = info, .HeapGetTotalSize = total
};
const ct_managed_diagnostics_host_api_v1 *ct_managed_diagnostics_host_v1(void) {
    return available ? &api : NULL;
}
extern void ct_shell_print_memory(void);
extern int32_t ct_shell_read_input(bool *eof);
int main(void) {
    ct_shell_print_memory();
    assert(strstr(captured, "free heap: 25000, minimum: 20000\n"));
    assert(strstr(captured, "25.0%"));
    assert(strstr(captured, "SPIRAM"));
    assert(strstr(captured, "not configured"));
    used = 0; available = false;
    ct_shell_print_memory();
    assert(strcmp(captured, "free: memory diagnostics unavailable\n") == 0);
    bool eof = true;
    assert(ct_shell_read_input(&eof) == -1 && !eof);
    assert(ct_shell_read_input(&eof) == 'X' && !eof);
    assert(ct_shell_read_input(&eof) == -1 && eof);
    return 0;
}
'''
with tempfile.TemporaryDirectory(prefix='ctilde-console-') as directory:
    work = Path(directory)
    (work / 'test.c').write_text(harness)
    subprocess.run(['cc', '-Wall', '-Wextra', '-Werror', '-ffunction-sections',
                    '-fdata-sections', '-Wl,--gc-sections', '-I', str(native),
                    str(native / 'shell_native.c'), str(work / 'test.c'),
                    '-o', str(work / 'test')], check=True)
    result = subprocess.run([str(work / 'test')], capture_output=True, check=True)
    assert not result.stdout and not result.stderr, 'Native diagnostics bypassed the process console'
print('SHELL_CONSOLE_OK: process output routing, empty input polls, received bytes, and EOF')
