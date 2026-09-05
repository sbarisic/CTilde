#!/usr/bin/env python3
"""Compare saved ESP32 module memory reports and firmware ELF files.

Each directory must contain one firmware ELF and unique *.memory.json reports.
Dynamic costs require device measurements and are not inferred from ELF sizes.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def firmware(path):
    data = path.read_bytes()
    if data[:6] != b'\x7fELF\x01\x01' or len(data) < 52:
        raise ValueError(f'{path}: expected a little-endian ELF32 firmware')
    if struct.unpack_from('<H', data, 18)[0] != 94:
        raise ValueError(f'{path}: expected Xtensa firmware')
    offset = struct.unpack_from('<I', data, 32)[0]
    stride, count = struct.unpack_from('<HH', data, 46)
    if stride < 40 or offset + stride * count > len(data):
        raise ValueError(f'{path}: invalid section table')
    ram, flash = 0, 0
    for index in range(count):
        _, _, flags, address, _, size = struct.unpack_from('<6I', data, offset + index * stride)
        if not flags & 2:
            continue
        # Original ESP32 IROM and DROM virtual address ranges.
        mapped = 0x3f400000 <= address < 0x3f800000 or 0x400d0000 <= address < 0x40400000
        if mapped:
            flash += size
        else:
            ram += size
    return dict(staticRamBytes=ram, mappedSectionBytes=flash,
                elfSha256=hashlib.sha256(data).hexdigest())


def snapshot(directory):
    modules = {}
    for path in sorted(directory.rglob('*.memory.json')):
        value = json.loads(path.read_text(encoding='utf-8-sig'))
        name = value['module']
        if value['schemaVersion'] != 1 or name in modules:
            raise ValueError(f'{path}: unsupported schema or duplicate module {name}')
        modules[name] = dict(shared=value['sharedModule'], perProcess=value['perProcess'],
                             unknownCosts=value['unknownCosts'])
    if not modules:
        raise ValueError(f'{directory}: no module memory reports')
    return dict(firmware=firmware(directory/'ctilde_managed_shell.elf'), modules=modules)


def compare(before, after):
    if before['modules'].keys() != after['modules'].keys():
        raise ValueError('Workload module sets differ; compare identical inputs')
    changes = {}
    for name, old in before['modules'].items():
        new = after['modules'][name]
        changes[name] = {
            group: {key: new[group][key] - value
                    for key, value in old[group].items()
                    if type(value) is int and type(new[group].get(key)) is int}
            for group in ('shared', 'perProcess')}
    return dict(schemaVersion=1, target='esp32', before=before, after=after,
                deltas=dict(firmwareStaticRamBytes=after['firmware']['staticRamBytes']-before['firmware']['staticRamBytes'],
                            firmwareMappedSectionBytes=after['firmware']['mappedSectionBytes']-before['firmware']['mappedSectionBytes'],
                            modules=changes),
                accounting='Shared costs are counted once per loaded module. Do not sum all modules unless all are loaded.',
                devicePeaks=None, netWorkloadRamReduction=None,
                pending=['device allocation and native scratch peaks', 'simultaneously loaded graph',
                         'startup and execution latency', 'authenticated SSH and SFTP'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    result = compare(snapshot(args.before), snapshot(args.after))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print('Firmware static RAM delta:', result['deltas']['firmwareStaticRamBytes'])
    for name, value in result['deltas']['modules'].items():
        print(f"{name}: shared linked RAM delta {value['shared']['linkedResidentBytes']:+d} bytes")
    print('Net workload RAM and device peaks require separate device measurements.')


if __name__ == '__main__':
    main()
