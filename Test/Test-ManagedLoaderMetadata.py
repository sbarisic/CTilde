#!/usr/bin/env python3
"""Exercise production ELF lookup-table cleanup under AddressSanitizer (Linux/WSL)."""
import argparse
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=Path, default=Path("artifacts/draft051/loader-metadata-test"))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
source = (root / "examples/ManagedShell/components/elf_loader/src/esp_elf.c").read_text()
helper = source[source.index("void esp_elf_discard_symbols("):source.index("int esp_elf_relocate_file(")]
harness = r'''
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <stdio.h>
#define CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT 1
typedef struct { char *name; void *addr; } symbol;
typedef struct { uint16_t num; symbol *symtab; char *symtab_names; void *image; } esp_elf_t;
static int live;
static void *allocate(size_t n) { void *p=calloc(1,n); assert(p); ++live; return p; }
static void esp_elf_free(void *p) { if(p) { --live; free(p); } }
''' + helper + r'''
int main(void) {
    esp_elf_discard_symbols(NULL);
    for (int layout=0; layout<4; ++layout) {
        int sentinel=42;
        esp_elf_t elf={ .num=3, .image=&sentinel };
        elf.symtab=allocate(3*sizeof(symbol));
        if (layout==0) {
            elf.symtab_names=allocate(24);
            for(int i=0;i<3;++i) elf.symtab[i].name=elf.symtab_names+i*8;
        } else if (layout==1) {
            for(int i=0;i<3;++i) elf.symtab[i].name=allocate(8);
        } else if (layout==2) {
            elf.symtab[0].name=allocate(8);
        } else {
            elf.num=0;
        }
        esp_elf_discard_symbols(&elf);
        assert(live==0 && elf.num==0 && !elf.symtab && !elf.symtab_names);
        assert(elf.image==&sentinel && *(int*)elf.image==42);
        esp_elf_discard_symbols(&elf);
        assert(live==0);
    }
    puts("LOADER_METADATA_OK: contiguous, separate, partial, empty, repeated cleanup; image retained");
    return 0;
}
'''
test = output / "metadata.c"
test.write_text(harness)
executable = output / "metadata-test"
subprocess.run(["cc", "-Wall", "-Wextra", "-Werror", "-g", "-fsanitize=address,undefined",
                str(test), "-o", str(executable)], check=True)
subprocess.run([str(executable)], check=True)
