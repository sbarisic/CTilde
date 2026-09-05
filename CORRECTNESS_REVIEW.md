# Draft 0.50 correctness review

This pass uses the previously staged Draft 0.50 optimization work as its comparison baseline. The staged patch and HEAD identity are preserved under `artifacts/correctness-review`. The changes are not committed, pushed, or published. Language Draft 0.50, Runtime ABI 22, Managed Module ABI 3, and debug metadata version 3 remain unchanged.

## Finding dispositions

| Finding | Disposition |
| --- | --- |
| Private overlay delegate entries could disappear | Binding records exact delegate targets, and placement retains callable resident entries. Initializers, instance targets, lambdas, and constructed methods use the same target registry. |
| Inferred overlays could absorb unmanaged-address targets | Address targets and their ordinary call closure remain resident. Explicit-overlay address formation still reports the existing diagnostic. Address creation adds no false execution edge to effect analysis. |
| Allocation list and payload quota could race | A process-private lock serializes reservations, rollback, publication, and unlinking. Allocation, poisoning, and free occur outside the lock. The runtime-operation gate covers unfinished allocation and cleanup. |
| Native cache missed handwritten transitive headers | Cache format 2 uses compiler dependency scans before lookup and hashes resolved content, source/cwd identity, flags, include environment, target, and compiler identity. Unusable scans compile uncached; failed compiles cannot publish entries. |
| Language-server source membership stayed stale | Create/delete notifications re-expand project globs. Rename is handled as delete/create. Open buffers win and publication checks reject superseded snapshots. |
| File URI conversion discarded UNC servers | Conversion uses the URI's authority-aware local path and platform normalization. |
| Git children could block on full output pipes | Both streams drain concurrently. `RestoreAsync` accepts cancellation; the synchronous signature remains a wrapper. CLI restore/update connect Ctrl+C, await terminated children, and preserve the last valid lockfile. |
| Managed GDB MI parser did not decode octal escapes | Decode one to three octal digits and run the same ordinary, path, octal, and malformed-string corpus in managed and TypeScript tests. |
| StreamReader loaded the entire input | Use a 4096-byte read buffer plus only the requested result. Deliver complete lines before EOF and validate malformed later bytes when consumed. |
| Stable sorting scaled quadratically | Use insertion runs of at most 32 followed by iterative stable merge passes and one scratch array. Preserve source data, equal-key order, comparer failures, and ARC cleanup. |
| Unchanged source was repeatedly parsed | Cache immutable syntax by normalized path, content, and origin; rebuild semantic analysis and evict project-reset entries. |
| Compact-map storage experiment | Keep production storage unchanged. The prototype lowers allocation count but increases payload size; see the [measured recommendation](Test/Fixtures/CompactMap/README.md). |
| Additional: pattern foreach leaked owned return values | Transfer owned enumerator and Current results into cleanup slots without retaining them again. The map experiment checks ARC balance for integer and reference workloads. |
| Additional: property overlay emission lost placement decisions | Reuse the exact analyzed accessor symbols during emission, including their inferred placement and entry requirements. The full Nano build exposed this mismatch. |
| Additional: child-thread exception flag failed GCC compilation | Emit the flag as volatile across the setjmp/longjmp exception boundary. The threaded ESP-IDF fixture exposed the compiler warning. |
| Additional: constructed delegate initializers lacked method bodies | Analyze initializer and constructed-body discovery to a fixed point before emission. The regression instantiates a generic method from a static delegate initializer. |
| Additional: threaded modules imported unavailable native helpers | Export the firmware's atomic, shift, and FreeRTOS primitives used by generated threading code. The linked import audit checks all 13 modules against firmware export tables. No exported ABI structure changes. |
| Additional: ELF function pointers could map into data memory | Resolve defined GLOB_DAT symbols through their actual loaded section in both loader paths. The ESP32 allocator worker exposed this through an instruction-fetch fault. |
| Additional: managed scalar atomics bypassed resident CAS | Route byte, short, and 32-bit compare-exchange through the resident helper. Retry failed strong CAS when the value changes back to the comparand. The fixture also checks 64-bit native helper imports. |
| Additional: forced cleanup leaked native thread resources | Register payloads and completion semaphores with the process under runtime-operation gating. Resident callbacks reclaim them when managed destructors are skipped. Worker detachment and final task deletion finish in resident code. |
| Additional: quick process exit hid the actual result | If a foreground child has already exited, the shell waits for cleanup and reports its exit code. It no longer replaces fast quota failures with a terminal-assignment error. |

## Reproducible checks and reports

Run commands from the repository root:

```powershell
dotnet build CTilde.sln -c Release --nologo
dotnet Test/bin/Release/net10.0/CTilde.Tests.dll --filter "draft 0.50"
./Test/Test-ManagedAllocator.ps1
./Test/Test-Validation.ps1 -Tier Fast
./examples/ManagedShell/Test-ManagedShell.ps1 -BuildOnly
python Test/Test-ManagedModuleImports.py
python Test/Test-Documentation.py
```

The allocator harness extracts the production bookkeeping functions into a pthread host fixture. It exercises deterministic quota reservation, allocation failure rollback, concurrent allocation/free, list integrity, and arena cleanup. Native thread payload checks cover allocation, semaphore, and registration failure plus normal and forced release. Its report records the runtime-source hash. This host harness does not substitute for ESP32 acceptance.

The linked import audit requires `pyelftools`, available in the ESP-IDF Python environment. It reads actual module imports and firmware export tables, including the example's host interfaces.

Focused conformance reports, the native-cache `11 -> 22` reproduction, allocator JSON, compact-map CSV/JSON, complete validation logs, and documentation checks are written under `artifacts/correctness-review`. Module size budgets remain enforced by the existing ManagedShell runner; its report is `artifacts/managed-shell/managed-module-sizes.json`.

The final Fast tier passed in 1,687.41 seconds: all 283 MSVC cases, all 114 selected WSL GCC cases, all 114 selected WSL Clang cases, and the managed debugger, Visual Studio, and VS Code suites. The latest Release build also passed with zero warnings and errors, followed by five atomic regressions and the repository formatter regression. The last ESP-specific comparand-mask correction and shell quick-exit correction were checked in the final target build and physical run.

All 13 modules and the resident firmware built. The firmware is 1,046,384 bytes. The shell-plus-SSH executable working set is 76,508 of the allowed 124,928 bytes. The linked import audit resolves every strong import against 115 firmware exports. The unity and modular overlay packages were linked and inspected separately; their package sizes are 42,160 and 42,240 bytes.

The final non-destructive COM4 ESP32 run completed 100 allocator and overlay lifecycle cycles, 100 additional rapid quota exits, ten in-cycle quota failures, ten cooperative cancellations, and ten forced terminations. Heap integrity remained valid, every test process and module cleared, and FreeRTOS task count stayed at 17 during diagnostics. Idle heap ranged from 112,144 to 112,688 bytes. The medians for cycles 11–20 and 91–100 were 112,640 and 112,634 bytes, a 6-byte loss within the recorded 512-byte allowance. The report retains every sample, artifact hash, quota outcome, and cleanup result in `artifacts/correctness-review/device/lifecycle-100.json`.

The separately linked unity overlay package then passed three physical runs, including delegate targets, unmanaged addresses, nested transitions, and exception cleanup. Its evidence is `artifacts/correctness-review/device/overlay-unity.json`.

The original application, module storage, and NVS were restored after testing. NVS had changed during the test boots, so its post-test bytes were saved separately before restoring the original copy. A digest check over all 4 MiB matched the original backup before the original application restarted. Bootloader, partition-table, PHY, and SFTP contents were not rewritten. The preservation and restoration reports contain hashes and results; credential-bearing backups remain ignored local artifacts.

`artifacts/correctness-review/acceptance.json` combines the final gate results, counts, artifact identities, benchmark results, and evidence boundaries. No commit, push, or publication was performed.

The allocator fixture is non-overlay because overlay-enabled processes reject source-created threads. Thread creation/attachment cancellation remains a separate roadmap stress case; this runner observes both workers before termination. Historical dated results in implementation status and example guides remain historical.

## Documentation and website

The [documentation audit](DOCUMENTATION_AUDIT.md) records every repository-owned Markdown document, including reviewed unchanged documents and preserved upstream material. The check script records content hashes and local link/anchor results. XML API documentation describes incremental UTF-8 validation and stable sorting without promising comparer call order.

The website keeps its existing HTML, CSS, JavaScript, and visual design. Updated text covers incremental reading, stable sorting, dependency-aware caching, project refresh, current draft/ABI identities, and the verified 29-project solution inventory. Delivery is a local preview only; no hosting migration or publication is included.
