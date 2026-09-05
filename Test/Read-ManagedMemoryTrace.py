#!/usr/bin/env python3
"""Extract versioned managed memory samples from a captured firmware log."""

import argparse
import json
from pathlib import Path


def summarize(text):
    samples = []
    for number, line in enumerate(text.splitlines(), 1):
        marker = "CT_MEMORY "
        if marker not in line:
            continue
        try:
            sample, _ = json.JSONDecoder().raw_decode(line.split(marker, 1)[1])
            if sample["schemaVersion"] != 1 or sample["scope"] != "global":
                raise ValueError("unsupported memory trace schema")
            for name in ("freeBytes", "allocatedBytes", "largestBlockBytes", "minimumFreeBytes", "allocatedBlocks"):
                value = sample["byteAddressable"][name]
                if type(value) is not int or value < 0:
                    raise ValueError(f"invalid {name}")
            samples.append(sample)
        except (ValueError, KeyError, TypeError) as error:
            raise ValueError(f"Invalid CT_MEMORY record on line {number}: {error}") from error
    if not samples:
        raise ValueError("No CT_MEMORY samples found; enable CONFIG_CTILDE_MANAGED_MEMORY_TRACE")
    heaps = [sample["byteAddressable"] for sample in samples]
    return {
        "schemaVersion": 1,
        "scope": "global",
        "sampleCount": len(samples),
        "sampledPeakAllocatedBytes": max(heap["allocatedBytes"] for heap in heaps),
        "sampledPeakAllocatedBlocks": max(heap["allocatedBlocks"] for heap in heaps),
        "sampledMinimumFreeBytes": min(heap["freeBytes"] for heap in heaps),
        "sampledMinimumLargestBlockBytes": min(heap["largestBlockBytes"] for heap in heaps),
        "bootMinimumFreeBytes": min(heap["minimumFreeBytes"] for heap in heaps),
        "limitations": [
            "Boundary samples can miss allocation peaks between records.",
            "Global samples include other processes, Wi-Fi, and native platform allocations.",
            "Allocated block count is live blocks, not cumulative allocation count.",
            "Boot minimum includes work before this capture; logging affects timing.",
        ],
        "samples": samples,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = summarize(args.capture.read_text(encoding="utf-8", errors="replace"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Extracted {report['sampleCount']} samples to {args.output}")


if __name__ == "__main__":
    main()
