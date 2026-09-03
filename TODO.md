# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and target milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.46 surface remains in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements remain in [C_ABI.md](C_ABI.md).

## Language and standard library

Draft 0.42 adds invariant scalar and enum parsing, attached-console UTF-8 handling, strict UTF-8 encoding objects, and synchronous hosted/Cosmopolitan streams, directories, paths, and metadata. Remaining follow-ups are:

- [ ] Add Unicode escape forms without changing byte-based `char` and string indexing. The UTF-8 rune helpers are implemented; escape syntax is not.
- [ ] Add checked numeric-to-numeric conversions, culture-aware parsing/formatting, Unicode casing, normalization, collation and grapheme segmentation, and regular expressions only after their portability and allocation contracts are specified.
- [ ] Add dependency-source navigation plus an explicit design for module-local `internal` access and semantic import aliases. Current manifest aliases name module placements; they do not alter namespaces.
- [ ] Extend closure source-debug metadata beyond the current generated-method mapping and add dedicated lambda editor-service fixtures.
- [ ] Permit iterator suspension across cleanup regions after defining state-machine ownership for `try`, `catch`, `finally`, `lock`, and `defer`.

- [ ] Design user-defined conversions and any additional operator families as an explicit language revision. Candidate families include bitwise, logical, remainder, increment, and decrement; arithmetic, equality, and ordering are implemented.
- [ ] Add wall-clock time, calendars, time zones, timeout and cancellation abstractions only after defining their runtime and portability contracts. Draft 0.40 provides monotonic elapsed time only.
- [ ] Add asynchronous I/O, explicit sharing controls, filesystem watchers, memory-mapped files, globbing, and lazy directory enumeration only after their ownership and cross-platform contracts are specified.
- [ ] Add ARC-safe managed-reference atomics only after defining a reclamation protocol that makes atomic loads safe.
- [ ] Define safe long-lived native-resource storage before permitting owned opaque handles in fields.

## Fixed-width SIMD

Keep `Vec2`, `Vec3`, and `Vec4` as scalar geometry types. The four fixed 16-byte lane types, conversions, loads/stores, reductions, semantic operations, explicit x86/Arm `simd128` lowering, hosted x64 automatic geometry optimization, and `Vec3x4` packet workload are implemented. Remaining measured stages are:

- [ ] Extend automatic geometry optimization beyond hosted x64 only after separate architecture-specific semantic and performance evidence. Draft 0.38 intentionally excludes ESP-IDF, Cosmopolitan, freestanding, x86, and Arm.
- [ ] Consider double-precision matrices, decomposition, dynamic matrices, AVX-width vectors, and scalable vectors as separate revisions.

## Compiler optimization

The first typed-IR size tranche removes cleanup boundaries with no live records, coalesces fresh owned moves, propagates conservative non-null and fixed-range facts, and simplifies constant loops and stack allocations. The following low-risk generated-C tranche moves reachable atomic, wrapping-arithmetic, ARC, null, bounds, and stack-size common paths into the modular internal header, assigns default object hashes lazily, and devirtualizes sealed receivers and sealed overrides. Public ABI 16 layouts and native ownership entry points remain unchanged. Measured results are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md). Remaining work is:

- [ ] Add an optional readable-C mode with source-oriented names and annotations while preserving compact deterministic Release output by default.
- [ ] Investigate aggregating compatible `defer` capture records when it reduces durable state without changing immediate capture, LIFO order, or exception replacement.

## Compiler analysis and low-level code generation

- [ ] Add explicit stack controls such as `[NoStackProbe]` and `[StackAlign(n)]`; verified `[StackUsage(n)]` contracts are implemented.

## Editor tooling

- [ ] Add rename, editor formatting, code actions, and auto-import completion edits. Exact cross-project references are implemented.
- [ ] Add semantic-token range requests, delta results, and result-ID caching if project sizes demonstrate a need.
- [ ] Publish self-contained language-server packages when distribution must support machines without an installed .NET 10 runtime.
- [ ] Add documentation-tag completion, navigable documentation links, and other XML-documentation authoring features when prioritized.

## ESP-IDF

- [ ] Verify native USB CDC or USB Serial/JTAG console output on suitable ESP32-C3, ESP32-S2, or ESP32-S3 hardware. The accepted T-CAN485 validates its onboard USB-to-UART bridge only.
- [ ] Add ESP log-level APIs only if `System.Console` proves insufficient.
- [ ] Extend `System.Storage` beyond the Draft 0.46 T-CAN485 SDSPI path only after separate hardware evidence: native SDMMC, shared SPI-bus arbitration, card-detect GPIOs, and other boards are deferred.
- [ ] Add GPT and extended/logical MBR partitions only with explicit on-media compatibility tests. Draft 0.46 supports whole devices and four primary MBR entries.
- [ ] Add bind mounts or a general overlay VFS only when applications need namespace composition. ManagedShell currently implements module lookup precedence directly.
- [ ] Complete removable-card hardware acceptance: no-card boot, insertion, removal with open files, reinsertion, multi-partition mounts, Unicode names, and destructive formatting on an explicitly disposable card.

## Managed modules

Draft 0.46 advances managed applications to Runtime ABI 19 while retaining Managed Module ABI 1. The compiler emits deterministic public metadata, a fixed ELF manifest, per-process static schemas, runtime/API binding accessors, and hidden module-local definitions. The ESP-IDF runtime preflights, loads, executes, waits for, and immediately unloads trusted application modules; it now also owns process filesystem handles and current directories. A manual Draft 0.45 ManagedShell serial session completed 100 load/run/unload cycles with stable current free heap and no stale module registrations. Remaining work is:

- [ ] Move the complete non-generic standard library and generic implementations into the shared firmware component. Managed ELF files currently still contain reachable private runtime and standard-library implementation code.
- [ ] Implement canonical type fingerprints, descriptor registration, and runtime-sized unboxed `ct_type_ops` dictionaries for shared arrays, lists, dictionaries, equality, hashing, comparison, ARC copy/drop, and exceptions.
- [ ] Compile `.ctmeta.json` references as semantic dependencies without adding provider source, enforce binary-module-local `internal`, and generate checked managed import slots and concrete cross-module calls.
- [ ] Turn the structural dependency loader into a supported managed-library surface, then exercise concrete classes, structures, interfaces, arrays, and delegates across a module boundary. Public managed APIs remain non-generic.
- [ ] Make source-created child threads inherit process ownership, finish the native-resource ledger, and translate an unhandled managed exception into process failure instead of firmware panic.
- [ ] Extend the implemented main-task cancellation/forced-termination and reaper cleanup to source-created child tasks and the complete native-resource ledger; retain the documented undefined behavior for unsafe state that escapes runtime accounting.
- [ ] Make managed console input cancellable without deleting a task inside a blocking VFS/newlib read, and define whether a stuck normal static finalizer may be promoted back to forced cleanup after it has taken ownership from `Main`.
- [ ] Replace the fixed lifetime process-handle table with reclaimable generation-checked handles so repeated starts are not bounded by one boot-time slot count.
- [ ] Audit every dynamic symbol and relocation against the loader allowlist, then prove modules contain no private runtime or standard-library implementation.
- [ ] Add corrupt, wrong-architecture, stale-ABI, dependency-cycle, version-conflict, signature-mismatch, heap-quota, child-thread, exception, cancellation, forced-termination, and cross-process reference rejection tests.
- [ ] Extend the ManagedShell hardware runner to execute the lifecycle loop and persist a machine-readable report so the manual 2026-09-02 measurements become a replayable acceptance gate.
- [ ] Design a separate hosted Managed Module ABI host only if desktop shared-runtime modules are required. The current [HostedNativeImport example](examples/HostedNativeImport/README.md) intentionally exercises native C ABI loading and cannot substitute for managed descriptors, canonical types, process instances, or `.ctmeta.json` references.

## Example coverage

- [ ] Add a full-service freestanding backend that demonstrates file, directory, clock, math, thread, mutex, and runtime-TLS provider groups. The current freestanding examples intentionally implement only the services reached by their kernels.
- [ ] Add `[Register]` and `[LinkerSymbol]` to a runnable target example after selecting hardware addresses and linker symbols that are safe to read or write on that target.
- [ ] Add an offline repository-source-module example only when its exact Git object store can be checked in or generated locally without making `Examples.sln` access the network.

## Cosmopolitan target

The staged target contract and rejected shortcuts are documented in [COSMOPOLITAN.md](COSMOPOLITAN.md). Implement it in this order:

- [ ] Complete the x64 hosted-library audit beyond the measured managed-runtime example: math, environment/process behavior, Unicode console and paths, exports, callbacks, TLS stress, LTO, `tiny`/`debug` modes, and deterministic repeated APE bytes.
- [ ] Inspect final x64 APE retention and custom sections before extending Cosmopolitan-specific `[Used]` and `[Section]` guarantees; also validate public headers, callback metadata, and source-debug maps against the retained carrier.
- [ ] Add controlled Cosmopolitan-built `.c`, `.S`, object, and archive inputs without permitting ordinary host-ABI objects or compiler-owned link settings to be replaced.
- [ ] Add the AArch64 single-architecture target through `aarch64-unknown-cosmo-cc`, retaining Cosmopolitan-owned ABI/TLS flags.
- [ ] Implement true x64/AArch64 fat output as two independent C~ semantic compilations with cross-slice public-ABI verification followed by `apelink`.
- [ ] Add optional deterministic `/zip` assets, bundled Clang mode, and ELF-carrier debugger integration only after the core target passes.

## Native interop

- [ ] Add macOS `libfoo.dylib` mapping after a hosted macOS build and runtime acceptance environment is available.
- [ ] Add versioned `.so` and project-specific logical-name mappings without allowing source-level paths or platform extensions.
- [ ] Add weak imports and definitions with explicit target semantics. Do not emulate weak linkage on MSVC.
- [ ] Add privileged CPU intrinsics for interrupt control and halt only after defining target-specific safety and execution-context contracts. Keep CPUID/control registers, RISC-V CSRs, and similar operations in explicit target namespaces.
- [ ] Generalize freestanding naked startup beyond parameterless complete raw bodies to target-specific naked parameters, compiler-bound operands, and additional interrupt signatures.
- [ ] Define retained callback registration, unregistration, rooting, and cross-thread lifetime rules separately from the existing synchronous callback profile.

## Deferred research

- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
