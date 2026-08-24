# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and ESP-IDF milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.16 surface remains in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements remain in [C_ABI.md](C_ABI.md).

## Language and standard library

- [ ] Design user-defined conversions and any additional operator families as an explicit language revision. Candidate families include equality, comparison, bitwise, logical, remainder, increment, and decrement.
- [ ] Extend hosted I/O when applications require seeking, directories, metadata, deletion, higher-level streams, or encoding-aware text files.
- [ ] Extend vectors only with application-backed requirements such as interpolation, clamping, distance, swizzles, conversions, or SIMD-aware lowering.
- [ ] Add ARC-safe managed-reference atomics only after defining a reclamation protocol that makes atomic loads safe.
- [ ] Define safe long-lived native-resource storage before permitting owned opaque handles in fields.
- [ ] Add compile-time `static assert(condition)` declarations, including assertions over `sizeof`, `alignof`, and `offsetof` results.
- [ ] Add compile-time target queries and `static if` branching over properties such as `Target.Architecture`, without exposing preprocessor-style conditional compilation.
- [ ] Add zero-cost explicit-endianness integer types such as `be16`, `be32`, `le16`, and `le32`, with deterministic conversion to and from native-endian values for protocols and hardware registers.

## Compiler optimization

The first typed-IR size tranche now removes cleanup boundaries with no live records, coalesces fresh owned moves, propagates conservative non-null and fixed-range facts, and simplifies constant loops and stack allocations. Measured results are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md). Remaining work is:

- [ ] Replace namespace-only modular partitioning with stable per-source or finer dependency buckets so one edited source recompiles less native C.
- [ ] Split the broad generated internal header into narrow dependency headers without duplicating declarations or changing public ABI output.
- [ ] Add an optional readable-C mode with source-oriented names and annotations while preserving compact deterministic Release output by default.
- [ ] Investigate aggregating compatible `defer` capture records when it reduces durable state without changing immediate capture, LIFO order, or exception replacement.

## Compiler analysis and low-level code generation

- [ ] Add explicit stack controls such as `[NoStackProbe]` and `[StackAlign(n)]`; support `[StackUsage(n)]` when the compiler can calculate or verify the bound.
- [ ] Add compile-time stack-usage analysis with per-call-path costs and a worst-case static stack report, especially for MCU builds.
- [ ] Add a no-recursion effect through `[NoRecursion]` and a project-wide option, enforcing it wherever bounded static stack analysis is required.

## Editor tooling

- [ ] Add references, rename, formatting, code actions, and auto-import completion edits.
- [ ] Add semantic-token range requests, delta results, and result-ID caching if project sizes demonstrate a need.
- [ ] Publish self-contained language-server packages when distribution must support machines without an installed .NET 10 runtime.
- [ ] Add documentation-tag completion, navigable documentation links, and other XML-documentation authoring features when prioritized.

## ESP-IDF

- [ ] Verify native USB CDC or USB Serial/JTAG console output on suitable ESP32-C3, ESP32-S2, or ESP32-S3 hardware. The accepted T-CAN485 validates its onboard USB-to-UART bridge only.
- [ ] Consider configurable panic policies such as abort, restart, and halt after the initial ABI 14 hardware release.
- [ ] Add ESP log-level APIs only if `System.Console` proves insufficient.
- [ ] Add separate task-stack configuration for exported task entry methods.

## Native interop

- [ ] Add volatile memory using `[Volatile]`.
- [ ] Add section placement using `[Section("section")]`.
- [ ] Add zero-cost typed address newtypes such as `PhysicalAddress`, `VirtualAddress`, and `IoAddress`; reject implicit cross-domain conversions and require explicit mapping.
- [ ] Add compile-time register definitions such as `[Register(0x60004000)]`, with `[Bit(n)]` and `[Bits(first, last)]` members lowered to deterministic mask-and-shift operations rather than C bitfields.
- [ ] Add deterministic compiler-defined bitfields such as `[BitField(typeof(uint))]`, `[Bit(n)]`, and `[Bits(first, last)]` for peripheral registers, protocol headers, page-table entries, and CPU control registers.
- [ ] Add portable CPU intrinsics for interrupt control, memory barriers, pause/halt, byte swapping, population count, and leading-zero count, plus target-specific namespaces for operations such as x86 CPUID/control registers, RISC-V CSRs, and Xtensa memory barriers.
- [ ] Add naked functions and first-class interrupt handlers.
- [ ] Add naked assembly functions: like naked functions, except the body is one complete `asm` block.
- [ ] Define retained callback registration, unregistration, rooting, and cross-thread lifetime rules separately from the existing synchronous callback profile.
- [ ] Define permitted ISR operations, then add compiler-enforced no-allocation, no-throw, no-block, IRAM-safe, and DRAM-safe reachability profiles.

## Deferred research

- [ ] Design independent DLL loading, dynamic runtime registration, unloading safety, and module-lifetime tracking only as a future ABI revision.
- [ ] Add parallel path-tracer workers only if the existing per-sample deterministic output gate remains byte-identical across schedules.
- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
