#!/usr/bin/env python3
"""Compare retained baseline CTM packages with rebuilt modules (requires pyelftools)."""
import argparse
import hashlib
import json
from pathlib import Path
from elftools.elf.elffile import ELFFile


def inspect(path):
    with path.open("rb") as stream:
        elf = ELFFile(stream)
        symbols = elf.get_section_by_name(".dynsym")
        exports = [symbol for symbol in symbols.iter_symbols()
                   if symbol["st_info"]["bind"] == "STB_GLOBAL" and symbol["st_info"]["type"] == "STT_FUNC"]
        names = sum(len(symbol.name.encode("utf-8")) + 1 for symbol in exports)
        sections = {section.name: section["sh_size"] for section in elf.iter_sections()
                    if section.name in (".text", ".data", ".rodata", ".data.rel.ro", ".bss")}
        relocation = max((section["sh_size"] for section in elf.iter_sections()
                          if section["sh_type"] == "SHT_RELA"), default=0)
    return {
        "packageBytes": path.stat().st_size,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "sectionBytes": sections,
        "lookupNamesBytes": names,
        "lookupRecordBytes": len(exports) * 8,
        "lookupNameCount": len(exports),
        "largestRelocationTableBytes": relocation,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--modules", type=Path, default=Path("examples/ManagedShell/Modules"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    workloads = []
    for previous in sorted(args.baseline.glob("*/*.ctm")):
        current = args.modules / previous.parent.name / "build/managed-modules" / previous.name
        before, after = inspect(previous), inspect(current)
        workloads.append({
            "module": previous.stem, "before": before, "after": after,
            "packageDeltaBytes": after["packageBytes"] - before["packageBytes"],
            "baselineRetainedLookupPayloadBytes": before["lookupNamesBytes"] + before["lookupRecordBytes"],
            "currentRetainedLookupPayloadBytesAfterBinding": 0,
            "baselineRelocationHeapBufferBytes": before["largestRelocationTableBytes"],
            "currentRelocationStackBufferBytes": 384,
        })
    if len(workloads) != 13:
        raise ValueError(f"Expected 13 baseline modules, found {len(workloads)}")
    report = {
        "schemaVersion": 1, "measurementKind": "linked artifact inspection",
        "limitations": ["Lookup payload excludes allocator overhead and other loader metadata.",
                        "Relocation staging moves from variable heap allocation to 384 stack bytes.",
                        "These are not device peak RAM, timing, or SSH acceptance measurements."],
        "workloads": workloads,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Compared {len(workloads)} module packages")


if __name__ == "__main__":
    main()
