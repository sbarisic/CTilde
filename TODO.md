# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and target milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.50 surface is in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements are in [C_ABI.md](C_ABI.md).

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

### Draft 0.51 lower-RAM programs

The current stage and measurements are in [the implementation report](examples/ManagedShell/DRAFT051_PROGRESS.md). Remaining release gates are:

- [ ] Generate service calls through the capability contract and remove the SSH module's native adapter.
- [ ] Share the remaining ARC, array, string, exception, and collection algorithms without changing ownership or exception behavior.
- [ ] Implement the mapped-section package and ESP32 flash cache. Keep partition migration separate from ordinary flashing.
- [ ] Add checked spans, scoped parameters, callable lifetime metadata, and buffer-taking library APIs.
- [ ] Reuse SSH packet storage, use 16 KiB channel/SFTP chunks, and preserve larger transport packet reception and backpressure.
- [ ] Reduce fixed process storage and select smaller stacks only after measured stack acceptance.
- [ ] Complete memory accounting, workload comparisons, mapping failure tests, and authenticated command, interactive, and SFTP acceptance.
- [ ] Add the memory optimization profile with bounded temporary promotion and stronger reachability analysis.

### Other compiler work

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
- [ ] Complete `sd.ctm` removable-card hardware acceptance: no-card boot, status/info, insertion, deliberate mount/unmount/remount, removal with open files, reinsertion, four-entry MBR read/write, multi-partition mounts, Unicode names, and destructive formatting on an explicitly disposable card.
- [ ] Complete `nano.ctm` hardware acceptance in a foreground Windows Terminal session: LittleFS and SD files, Unicode input, bracketed paste, terminal resize, normal/discard/cancel exits, cancellation, media removal during save, backup recovery, and terminal-state restoration.

## Managed modules

Draft 0.50 retains Runtime ABI 22, Module ABI 3, and schema-3 metadata. It adds the native size profile, inferred placement for private single-overlay helpers, final-package executable/data measurement, separated resident load segments, and example-local working-set budgets. Provider-owned resident stubs still make ordinary and exceptional managed calls cleanup-safe. Draft 0.48 redirected streams, stable process identities, exact dependency imports, and Draft 0.47 metadata references remain available. Remaining work is:

- [ ] Make the canonical descriptor returned by runtime registration authoritative in generated object, interface, delegate, cast, and dispatch metadata; validate its complete ABI shape rather than only name, size, alignment, and value/reference kind.
- [ ] Add runtime-sized unboxed `ct_type_ops` dictionaries for shared arrays, lists, dictionaries, equality, hashing, comparison, ARC copy/drop, and exceptions.
- [ ] Complete the supported managed-library surface for fields and richer descriptor-sharing scenarios. Constructors, properties, concrete classes, structures, interfaces, arrays, delegates, and exceptions use cleanup-safe provider stubs for the currently supported concrete non-generic surface.
- [ ] Move the complete non-generic standard library and generic implementations into the shared firmware component once canonical operations and callable imports are available. Managed ELF files currently still contain reachable private runtime and standard-library implementation code.
- [ ] Extend the resident native-resource ledger beyond the current socket, crypto, and managed-thread payload users as additional firmware accessors are added; retain the documented undefined behavior for unsafe state that escapes runtime accounting.
- [ ] Define whether a stuck normal static finalizer may be promoted back to forced cleanup after it has taken ownership from `Main`.
- [ ] Move more private runtime and standard-library implementation out of modules; Draft 0.50 infers helpers that belong to one overlay and audits the Xtensa overlay relocation subset, but helpers shared by resident and multiple overlay paths still require resident code or shared-runtime extraction.
- [ ] Add optional overlay compression, prefetching, pinning, more than one executable window, or RAM-backed source caching only after measured transition and memory evidence. Raw overlay-body breakpoints, disassembly-aware stepping, and hot replacement also remain deferred.
- [ ] Extend overlays beyond ESP32/Xtensa only with backend-specific executable-memory, relocation, and cache-coherency evidence. ESP32-C3/RISC-V, hosted, Cosmopolitan, freestanding, and resident firmware deliberately reject `[Overlay]`.
- [ ] Expand corrupt, wrong-architecture, stale-ABI, dependency-cycle, version-conflict, signature-mismatch, and cross-process reference rejection tests.
- [ ] Extend thread lifecycle stress to cancellation during native task creation and attachment. The current fixture observes both workers before requesting termination.
- [ ] Design a separate hosted Managed Module ABI host only if desktop shared-runtime modules are required. The current [HostedNativeImport example](examples/HostedNativeImport/README.md) intentionally exercises native C ABI loading and cannot substitute for managed descriptors, canonical types, process instances, or `.ctmeta.json` references.

## SSH and remote administration

Draft 0.49 implements the common `shell.ctm` environment, opaque resident socket/crypto tokens, Curve25519/P-256/AES-GCM transport, public-key authentication, rekey limits, redirected shell and exec channels, SFTP v3 rooted beneath `/sftp`, and an explicit secrets-aware packaging target. Draft 0.50 reduces their executable working set and adds size gates; it does not complete protocol acceptance. Remaining work is:

- [ ] Run connected ESP32 Wi-Fi, UART-shell supervision, remote shell/exec, resize, Nano-over-SSH, service-control, cancellation, and complete descendant cleanup scenarios.
- [ ] Run OpenSSH and libssh interoperability for transport, public-key authentication, PTY/exec, exit status, and SFTP operations.
- [ ] Add malformed-packet and parser fuzzing, interrupted/full-filesystem transfers, rekey and network-loss endurance, and 100-cycle connection/module load-unload soaks.
- [ ] Measure stable heap, tasks, socket/crypto/file handles, module registrations, resident segments, and overlay transitions across those acceptance runs.
- [ ] Obtain an independent protocol and cryptographic-composition security review before describing the server as production-ready.

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

- [ ] Compare compact-map index widths and occupancy encodings on x64 and ESP32, with separate operation timings and peak-memory measurements. The [benchmark-only entry-array prototype](Test/Fixtures/CompactMap/README.md) reduced allocations but increased payload storage, so production `Map` retains its eight-array layout.

- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
