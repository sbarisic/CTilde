#!/usr/bin/env python3
"""Verify managed-module header dependencies with the production CMake wrapper (Linux/WSL)."""
from pathlib import Path
import json
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
upstream = (root / 'examples/ManagedShell/components/elf_loader/elf_loader.cmake').read_text()
start = upstream.index('        # Compile each C file to .o file')
end = upstream.index('        # Link all .o files to .so file', start)
compile_loop = upstream[start:end]
wrapper = root / 'examples/ManagedShell/cmake/ctilde_project_so.cmake'

def run(*args, cwd):
    return subprocess.run(args, cwd=cwd, check=True, text=True, capture_output=True).stdout

with tempfile.TemporaryDirectory(prefix='ctilde header dependencies ') as directory:
    work = Path(directory)
    # Exercise the upstream compile loop, replacing only IDF discovery and linking.
    (work / 'elf_loader.cmake').write_text('''macro(project_so project_name)
set(so_compile_flags -c)
set(so_link_flags -shared)
set(so_c_sources "${CMAKE_SOURCE_DIR}/constructor.c")
''' + compile_loop + '''add_custom_target(so ALL DEPENDS ${so_obj_files})
endmacro()
''')
    (work / 'CMakeLists.txt').write_text(f'''cmake_minimum_required(VERSION 3.20)
project(header_dependencies C)
set(ELF_LOADER_CMAKE_DIR "${{CMAKE_SOURCE_DIR}}")
include("{wrapper.as_posix()}")
ctilde_project_so(header_dependencies)
''')
    (work / 'constructor.c').write_text('#include "types.h"\nint allocation_size(void) { return sizeof(struct channel); }\n')
    (work / 'types.h').write_text('#include "fields.h"\nstruct channel { char bytes[CHANNEL_BYTES]; };\n')
    (work / 'fields.h').write_text('#define CHANNEL_BYTES 56\n')
    (work / 'check.c').write_text('int allocation_size(void); int main(void) { return allocation_size(); }\n')
    run('cmake', '-G', 'Ninja', '-S', '.', '-B', 'build', cwd=work)

    def size_after_build():
        run('cmake', '--build', 'build', cwd=work)
        run('cc', 'check.c', 'build/so_objs/constructor.o', '-o', 'check', cwd=work)
        return subprocess.run([str(work / 'check')], cwd=work).returncode

    assert size_after_build() == 56
    obj = work / 'build/so_objs/constructor.o'
    previous = obj.stat().st_mtime_ns
    assert size_after_build() == 56
    assert obj.stat().st_mtime_ns == previous, 'Unchanged build recompiled the object'
    (work / 'fields.h').write_text('#define CHANNEL_BYTES 76\n')
    assert size_after_build() == 76, 'Transitive header change reused an incompatible constructor'
    assert obj.stat().st_mtime_ns != previous
    (work / 'fields.h').unlink()
    failed = subprocess.run(['cmake', '--build', 'build'], cwd=work, capture_output=True)
    assert failed.returncode != 0, 'Deleted dependency reused a stale object'

print(json.dumps({'schemaVersion': 1, 'passed': True,
                  'checks': ['initial layout', 'unchanged reuse', 'transitive layout change',
                             'deleted header rejection', 'paths with spaces']}))
