# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and ESP-IDF milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.21 surface remains in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements remain in [C_ABI.md](C_ABI.md).

## Language and standard library

- [ ] Design user-defined conversions and any additional operator families as an explicit language revision. Candidate families include equality, comparison, bitwise, logical, remainder, increment, and decrement.
- [ ] Extend hosted I/O when applications require seeking, directories, metadata, deletion, higher-level streams, or encoding-aware text files.
- [ ] Extend vectors only with application-backed requirements such as interpolation, clamping, distance, swizzles, conversions, or SIMD-aware lowering.
- [ ] Add ARC-safe managed-reference atomics only after defining a reclamation protocol that makes atomic loads safe.
- [ ] Define safe long-lived native-resource storage before permitting owned opaque handles in fields.

## Compiler optimization

The first typed-IR size tranche now removes cleanup boundaries with no live records, coalesces fresh owned moves, propagates conservative non-null and fixed-range facts, and simplifies constant loops and stack allocations. Measured results are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md). Remaining work is:

- [ ] Split the broad generated internal header into narrow dependency headers without duplicating declarations or changing public ABI output.
- [ ] Add an optional readable-C mode with source-oriented names and annotations while preserving compact deterministic Release output by default.
- [ ] Investigate aggregating compatible `defer` capture records when it reduces durable state without changing immediate capture, LIFO order, or exception replacement.

## Compiler analysis and low-level code generation

- [ ] Add explicit stack controls such as `[NoStackProbe]` and `[StackAlign(n)]`; support `[StackUsage(n)]` when the compiler can calculate or verify the bound.
- [ ] Add compile-time stack-usage analysis with per-call-path costs and a worst-case static stack report, especially for MCU builds.
- [ ] Generalize the transitive `[NoAlloc]` analysis into an effect system. Define and infer effects such as `[NoThrow]`, `[NoBlock]`, and possibly `[NoRuntime]`, and report complete call paths when a restricted method reaches a forbidden operation.

## Editor tooling

- [ ] Add references, rename, formatting, code actions, and auto-import completion edits.
- [ ] Add semantic-token range requests, delta results, and result-ID caching if project sizes demonstrate a need.
- [ ] Publish self-contained language-server packages when distribution must support machines without an installed .NET 10 runtime.
- [ ] Add documentation-tag completion, navigable documentation links, and other XML-documentation authoring features when prioritized.

## ESP-IDF

- [ ] Verify native USB CDC or USB Serial/JTAG console output on suitable ESP32-C3, ESP32-S2, or ESP32-S3 hardware. The accepted T-CAN485 validates its onboard USB-to-UART bridge only.
- [ ] Add ESP log-level APIs only if `System.Console` proves insufficient.

## Native interop

- [ ] Add weak imports and definitions with explicit target semantics. Do not emulate weak linkage on MSVC.
- [ ] Add privileged CPU intrinsics for interrupt control and halt only after defining target-specific safety and execution-context contracts. Keep CPUID/control registers, RISC-V CSRs, and similar operations in explicit target namespaces.
- [ ] Generalize the narrow freestanding naked-startup form to target-specific naked functions with parameters, compiler-bound assembly operands, and first-class interrupt handlers.
- [ ] Define retained callback registration, unregistration, rooting, and cross-thread lifetime rules separately from the existing synchronous callback profile.
- [ ] Define `[Interrupt]` as a target-specific effect profile over the general effect system. Enforce no-allocation, no-throw, no-block, IRAM-safe, DRAM-safe, and other target-specific ISR restrictions through transitive reachability.

## Deferred research

- [ ] Design independent DLL loading, dynamic runtime registration, unloading safety, and module-lifetime tracking only as a future ABI revision.
- [ ] Add parallel path-tracer workers only if the existing per-sample deterministic output gate remains byte-identical across schedules.
- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
