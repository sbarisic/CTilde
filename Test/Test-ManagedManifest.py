#!/usr/bin/env python3
"""Run the production firmware manifest reader on a compiled Xtensa module (Linux/WSL)."""
import argparse
from pathlib import Path
import re
import struct
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--module', type=Path, required=True)
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
out = (args.output or root/'artifacts/managed-manifest-test').resolve()
out.mkdir(parents=True, exist_ok=True)
runtime = root/'runtime/esp-idf/ctilde_managed_runtime'
source = (runtime/'ctilde_managed_runtime.c').read_text()


def function(name):
    match = re.search(r'^static [^\n]*\b'+name+r'\([^;]*?\)\s*\{', source, re.M)
    if not match:
        raise ValueError('Missing production function '+name)
    start = source.index('{', match.start())
    depth, end = 1, start+1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[match.start():end]


names = ['contained_string', 'ascii_letter', 'ascii_digit', 'ascii_alphanumeric',
         'canonical_module_name', 'exact_module_version', 'read_u16_le', 'read_u32_le',
         'byte_range', 'read_file_range', 'read_manifest']
structures = source[source.index('typedef struct ct_binary_dependency_v1 {'):
                    source.index('} ct_binary_manifest_v1;')+len('} ct_binary_manifest_v1;')]
harness = r'''
#include "ctilde_managed_runtime.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <limits.h>
#include <assert.h>
#define CT_MODULE_NAME_MAX CTILDE_MANAGED_MODULE_NAME_CAPACITY
#define CT_MODULE_VERSION_MAX CTILDE_MANAGED_MODULE_VERSION_CAPACITY
#define CT_MAX_DEPENDENCIES 16
#define CONFIG_IDF_TARGET_ARCH_XTENSA 1
#define ESP_LOGE(...) ((void)0)
''' + structures + '\n' + '\n\n'.join(function(name) for name in names) + r'''
int main(int argc, char **argv) {
    assert(argc==3);
    ct_binary_manifest_v1 *manifest=NULL;
    uint8_t *owned=NULL;
    int result=read_manifest(argv[1],&manifest,&owned,NULL);
    assert(result==atoi(argv[2]));
    if (result==0) { assert(manifest && owned); free(owned); }
    else assert(manifest==NULL && owned==NULL);
    return 0;
}
'''
(out/'reader.c').write_text(harness)
binary = out/'reader'
subprocess.run(['cc','-std=c11','-Wall','-Wextra','-Werror','-fsanitize=address,undefined',
                '-I'+str(runtime/'include'),str(out/'reader.c'),'-o',str(binary)],check=True)
data = args.module.read_bytes()
section_offset = struct.unpack_from('<I',data,32)[0]
stride,count,names_index = struct.unpack_from('<HHH',data,46)
sections = [struct.unpack_from('<10I',data,section_offset+i*stride) for i in range(count)]
strings = sections[names_index]
table = data[strings[4]:strings[4]+strings[5]]
manifest = next(s for s in sections if table[s[0]:].split(b'\0',1)[0]==b'.ctilde.manifest')
offset = manifest[4]
cases = [('current',data,0)]
old = bytearray(data)
old[offset+5] = 3
struct.pack_into('<II',old,offset+16,22,3)
# Linux EPROTONOSUPPORT is 93. Firmware returns the platform's matching errno.
cases.append(('old-abi',old,-93))
bad = bytearray(data)
bad[offset+5] ^= 0xff
cases.append(('inconsistent-magic',bad,-8))
bad = bytearray(data)
struct.pack_into('<I',bad,offset+32,0xffffffff)
cases.append(('dependency-overflow',bad,-8))
cases.append(('truncated',data[:80],-8))
for name,contents,result in cases:
    path = out/(name+'.ctm')
    path.write_bytes(contents)
    subprocess.run([str(binary),str(path),str(result)],check=True)
print('MANIFEST_OK: current ABI, explicit old-ABI rejection, magic mismatch, dependency bounds, truncation')
