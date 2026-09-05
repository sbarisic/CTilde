"""Audit linked ManagedShell module imports against firmware ELF export tables. Requires pyelftools."""
import json
import hashlib
from pathlib import Path
import struct
from elftools.elf.elffile import ELFFile

root = Path(__file__).resolve().parents[1]
firmware = root / 'examples/ManagedShell/build/ctilde_managed_shell.elf'
with firmware.open('rb') as stream:
    elf = ELFFile(stream)
    sections = list(elf.iter_sections())
    def data(address, size=None):
        section = next(s for s in sections if s['sh_type'] != 'SHT_NOBITS' and s['sh_addr'] <= address < s['sh_addr'] + s['sh_size'])
        offset = address - section['sh_addr']
        return section.data()[offset:offset + size if size else None]
    exports = {}
    for symbol in elf.get_section_by_name('.symtab').iter_symbols():
        if symbol.name in ('s_symbols', 's_host_symbols') or symbol.name.endswith('_elfsyms'):
            for offset in range(0, symbol['st_size'], 8):
                name, address = struct.unpack('<II', data(symbol['st_value'] + offset, 8))
                if not name:
                    break
                exports[data(name).split(b'\0')[0].decode()] = hex(address)
results = []
for path in sorted((root / 'examples/ManagedShell/storage/modules').glob('*.ctm')):
    with path.open('rb') as stream:
        table = ELFFile(stream).get_section_by_name('.dynsym')
        imports = sorted({s.name for s in table.iter_symbols() if s['st_shndx'] == 'SHN_UNDEF' and s.name and s['st_info']['bind'] != 'STB_WEAK'})
        results.append(dict(module=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                            imports=imports, unresolved=[s for s in imports if s not in exports]))
report = dict(firmware=str(firmware), firmwareSha256=hashlib.sha256(firmware.read_bytes()).hexdigest(),
              exports=exports, modules=results, passed=bool(results) and all(not r['unresolved'] for r in results))
output = root / 'artifacts/correctness-review/device/import-audit.json'
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'exportCount':len(exports), 'modules':len(results), 'unresolved':[{ 'module':r['module'], 'symbols':r['unresolved']} for r in results if r['unresolved']]}))
assert report['passed']
