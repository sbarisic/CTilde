# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and target milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.41 surface remains in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements remain in [C_ABI.md](C_ABI.md).

## Language and standard library

Draft 0.41 adds controlled native optimization profiles, x64 AVX2 targeting, precise/fast floating point, hosted PGO, schema-2 parallel HostedIo benchmarking, and the compiler-backed string, splitting, builder, invariant-formatting, ASCII, and checked native UTF-8 foundations. Remaining follow-ups are:

- [ ] Add a flattened, cache-oriented SAH BVH and compare it as a new benchmark accelerator against `object-midpoint-bvh`.

- [ ] Add Unicode escape forms without changing byte-based `char` and string indexing. The UTF-8 rune helpers are implemented; escape syntax is not.
- [ ] Add parsing and general conversion APIs, culture-aware formatting, Unicode casing, normalization, collation and grapheme segmentation, and regular expressions only after their portability and allocation contracts are specified.
- [ ] Add dependency-source navigation plus an explicit design for module-local `internal` access and semantic import aliases. Current manifest aliases name module placements; they do not alter namespaces.
- [ ] Extend closure source-debug metadata beyond the current generated-method mapping and add dedicated lambda editor-service fixtures.
- [ ] Permit iterator suspension across cleanup regions after defining state-machine ownership for `try`, `catch`, `finally`, `lock`, and `defer`.

- [ ] Design user-defined conversions and any additional operator families as an explicit language revision. Candidate families include bitwise, logical, remainder, increment, and decrement; arithmetic, equality, and ordering are implemented.
- [ ] Add wall-clock time, calendars, time zones, timeout and cancellation abstractions only after defining their runtime and portability contracts. Draft 0.40 provides monotonic elapsed time only.
- [ ] Extend hosted I/O when applications require seeking, directories, metadata, deletion, higher-level streams, or encoding-aware text files.
- [ ] Add ARC-safe managed-reference atomics only after defining a reclamation protocol that makes atomic loads safe.
- [ ] Define safe long-lived native-resource storage before permitting owned opaque handles in fields.

## Fixed-width SIMD

Keep `Vec2`, `Vec3`, and `Vec4` as scalar geometry types. The four fixed 16-byte lane types, conversions, loads/stores, reductions, semantic operations, explicit x86/Arm `simd128` lowering, hosted x64 automatic geometry optimization, and `Vec3x4` packet workload are implemented. Remaining measured stages are:

- [ ] Extend automatic geometry optimization beyond hosted x64 only after separate architecture-specific semantic and performance evidence. Draft 0.38 intentionally excludes ESP-IDF, Cosmopolitan, freestanding, x86, and Arm.
- [ ] Consider double-precision matrices, decomposition, dynamic matrices, AVX-width vectors, and scalable vectors as separate revisions.

## Compiler optimization

The first typed-IR size tranche removes cleanup boundaries with no live records, coalesces fresh owned moves, propagates conservative non-null and fixed-range facts, and simplifies constant loops and stack allocations. The following low-risk generated-C tranche moves reachable atomic, wrapping-arithmetic, ARC, null, bounds, and stack-size common paths into the modular internal header, assigns default object hashes lazily, and devirtualizes sealed receivers and sealed overrides. Public ABI 16 layouts and native ownership entry points remain unchanged. Measured results are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md). Remaining work is:

- [ ] Split the broad generated internal header into narrow dependency headers without duplicating declarations or changing public ABI output.
- [ ] Add an optional readable-C mode with source-oriented names and annotations while preserving compact deterministic Release output by default.
- [ ] Investigate aggregating compatible `defer` capture records when it reduces durable state without changing immediate capture, LIFO order, or exception replacement.

## Compiler analysis and low-level code generation

- [ ] Add explicit stack controls such as `[NoStackProbe]` and `[StackAlign(n)]`; support `[StackUsage(n)]` when the compiler can calculate or verify the bound.
- [ ] Add compile-time stack-usage analysis with per-call-path costs and a worst-case static stack report, especially for MCU builds.

## Editor tooling

- [ ] Add rename, editor formatting, code actions, and auto-import completion edits. Exact cross-project references are implemented.
- [ ] Add semantic-token range requests, delta results, and result-ID caching if project sizes demonstrate a need.
- [ ] Publish self-contained language-server packages when distribution must support machines without an installed .NET 10 runtime.
- [ ] Add documentation-tag completion, navigable documentation links, and other XML-documentation authoring features when prioritized.

## ESP-IDF

- [ ] Verify native USB CDC or USB Serial/JTAG console output on suitable ESP32-C3, ESP32-S2, or ESP32-S3 hardware. The accepted T-CAN485 validates its onboard USB-to-UART bridge only.
- [ ] Add ESP log-level APIs only if `System.Console` proves insufficient.

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

- [ ] Design dynamic C~-module descriptor registration, dependency ordering, and module-lifetime tracking as a future runtime ABI revision. Draft 0.39 native imports load ordinary C ABI libraries only.
- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
