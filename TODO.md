# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and ESP-IDF milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.15 surface remains in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements remain in [C_ABI.md](C_ABI.md).

## Language and standard library

- [ ] Design user-defined conversions and any additional operator families as an explicit language revision. Candidate families include equality, comparison, bitwise, logical, remainder, increment, and decrement.
- [ ] Extend hosted I/O when applications require seeking, directories, metadata, deletion, higher-level streams, or encoding-aware text files.
- [ ] Extend vectors only with application-backed requirements such as interpolation, clamping, distance, swizzles, conversions, or SIMD-aware lowering.
- [ ] Add ARC-safe managed-reference atomics only after defining a reclamation protocol that makes atomic loads safe.
- [ ] Define safe long-lived native-resource storage before permitting owned opaque handles in fields.

## Editor tooling

- [ ] Add references, rename, formatting, code actions, and auto-import completion edits.
- [ ] Add semantic-token range requests, delta results, and result-ID caching if project sizes demonstrate a need.
- [ ] Publish self-contained language-server packages when distribution must support machines without an installed .NET 10 runtime.
- [ ] Add documentation-tag completion, navigable documentation links, and other XML-documentation authoring features when prioritized.

## ESP-IDF and native interop

- [ ] Verify native USB CDC or USB Serial/JTAG console output on suitable ESP32-C3, ESP32-S2, or ESP32-S3 hardware. The accepted T-CAN485 validates its onboard USB-to-UART bridge only.
- [ ] Consider configurable panic policies such as abort, restart, and halt after the initial ABI 14 hardware release.
- [ ] Add ESP log-level APIs only if `System.Console` proves insufficient.
- [ ] Define a binding manifest that selects required ESP-IDF components and public headers.
- [ ] Generate editor-visible C~ declarations and source-compatible C adapters against the installed ESP-IDF headers.
- [ ] Generate adapters for configuration structures, designated initialization, default macros, static-inline functions, and function-like macros without copying unstable layouts or enum values into durable C~ metadata.
- [ ] Compile generated adapters in the owning ESP-IDF component and reject private, `esp_private`, example-helper, preview, and experimental APIs unless a manifest explicitly opts in.
- [ ] Define retained callback registration, unregistration, rooting, and cross-thread lifetime rules separately from the existing synchronous callback profile.
- [ ] Define permitted ISR operations, then add compiler-enforced no-allocation, no-throw, no-block, IRAM-safe, and DRAM-safe reachability profiles.
- [ ] Add separate task-stack configuration for exported task entry methods.

## Deferred research

- [ ] Design independent DLL loading, dynamic runtime registration, unloading safety, and module-lifetime tracking only as a future ABI revision.
- [ ] Add parallel path-tracer workers only if the existing per-sample deterministic output gate remains byte-identical across schedules.
- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
