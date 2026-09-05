# Compact-map experiment

`CompactMap.ct` is a benchmark-only variant of `System.Collections.Map`. It preserves the bucket, free-list, insertion-order, callback, version, and growth algorithms. Seven parallel entry arrays become one array of `CompactEntry` values; the bucket array stays separate. Production `Map` is unchanged.

Run from the repository root after building the Release test project:

```powershell
dotnet Test/bin/Release/net10.0/CTilde.Tests.dll --filter "compact map experiment"
```

The test writes CSV samples and JSON medians under `artifacts/correctness-review`. It uses two warm-ups and seven interleaved samples per case, with 2,048 entries. Each sample covers growth, insertion, lookup, half-removal, ordered iteration, clearing, and ARC cleanup. Integer and managed-reference pairs each run with ordinary hashes and four collision buckets. Live allocations must return to the pre-call baseline after every sample. Allocation counts include reference-key probes.

The initial Windows x64 MSVC `/O2` run on 2026-09-05 measured:

| Case | Eight-array median | Compact median | Allocations, old / compact | Array payload, old / compact |
| --- | ---: | ---: | ---: | ---: |
| Integer pairs | 0.406 ms | 0.358 ms | 91 / 25 | 118,784 / 131,072 bytes |
| Integer pairs, collisions | 24.327 ms | 25.781 ms | 92 / 26 | 118,784 / 131,072 bytes |
| Reference pairs | 1.365 ms | 1.167 ms | 7,259 / 7,193 | 151,552 / 180,224 bytes |
| Reference pairs, collisions | 27.502 ms | 25.646 ms | 7,260 / 7,194 | 151,552 / 180,224 bytes |

A repeat during integrated Fast validation on the same day measured old/compact medians of 0.367/0.352 ms for integer pairs, 23.991/23.536 ms for colliding integer pairs, 1.138/1.067 ms for reference pairs, and 23.909/24.227 ms for colliding reference pairs. Allocation and payload counts were unchanged. The current CSV and JSON contain this repeat. The variation reinforces the need for target-specific measurements before treating small timing differences as a production benefit.

Payload counts exclude array/object headers, allocator bookkeeping, and key/value objects. Integer entry size comes from `sizeof`; reference entry size is calculated from the emitted natural pointer alignment. These are storage-layout measurements, not peak process-memory measurements. Timings cover the combined workload and do not establish isolated-operation latency or ESP32 performance.

Recommendation: retain the production layout. The compact candidate removes 66 growth allocations in this workload but increases array payload by about 10% for integers and 19% for references. Collision timing is mixed. Test smaller index fields, occupancy encoding, and separate operation timings on the intended targets before considering a production change.

This experiment also exposed excess retains of owned `GetEnumerator` and `Current` results in pattern `foreach` lowering. The compiler fix is covered by the per-sample ARC balance checks for both layouts.
