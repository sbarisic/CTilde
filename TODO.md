# C~ roadmap

This document tracks outstanding work only. Completed language, compiler, runtime, editor, and ESP-IDF milestones are recorded in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) and the Git history. The normative Draft 0.17 surface remains in [LANGUAGE.md](LANGUAGE.md), and native compatibility requirements remain in [C_ABI.md](C_ABI.md).

## Language and standard library

- [ ] Design user-defined conversions and any additional operator families as an explicit language revision. Candidate families include equality, comparison, bitwise, logical, remainder, increment, and decrement.
- [ ] Extend hosted I/O when applications require seeking, directories, metadata, deletion, higher-level streams, or encoding-aware text files.
- [ ] Extend vectors only with application-backed requirements such as interpolation, clamping, distance, swizzles, conversions, or SIMD-aware lowering.
- [ ] Add ARC-safe managed-reference atomics only after defining a reclamation protocol that makes atomic loads safe.
- [ ] Define safe long-lived native-resource storage before permitting owned opaque handles in fields.
- [ ] Add compile-time `static assert(condition)` declarations, including assertions over `sizeof`, `alignof`, and `offsetof` results.
- [ ] Add compile-time target queries and `static if` branching over properties such as `Target.Architecture`, without exposing preprocessor-style conditional compilation.
- [ ] Add constant generic parameters for compile-time integral values, with syntax such as `RingBuffer<T, const int Capacity>`. Require compile-time arguments, include each value in monomorphized type identity, and permit their use in fixed layouts and compile-time expressions.
- [ ] Add inline compile-time-sized arrays with `T[N]` syntax, distinct from managed ARC arrays written as `T[]`. Store exactly `N` elements inside the containing value, and accept constant generic arguments for `N`. Define initialization, indexing, bounds checks, copying, ownership, layout, alignment, native ABI use, and zero-length behavior without adding a `fixed` keyword.
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
- [ ] Generalize the transitive `[NoAlloc]` analysis into an effect system. Define and infer effects such as `[NoThrow]`, `[NoBlock]`, and possibly `[NoRuntime]`, and report complete call paths when a restricted method reaches a forbidden operation.

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

## Freestanding targets and runtime

- [ ] Add a `freestanding` target profile with no required `main`, hosted C library, pthreads, filesystem, console, host TLS, default allocator, or process termination. Define the minimum compiler and runtime contract for startup, thread state, allocation, failure, and shutdown.
- [ ] Design one extensible attribute for user-supplied runtime implementations instead of separate attributes such as `[RuntimeAllocate]` and `[RuntimeFree]`. A candidate form is `[RuntimeImpl(Runtime.Allocate)]`. Define stable roles for allocation, deallocation, panic, and other required services, with exact signatures, uniqueness, target requirements, reachability, and diagnostics for missing or conflicting implementations.
- [ ] Separate `[EntryPoint]` from freestanding native startup. A freestanding build must not emit `main`, `app_main`, runtime initialization, or runtime shutdown unless the program requests them. Permit exported, naked, and section-placed startup methods such as `_start`, and let the program select and initialize only the required runtime services.
- [ ] Add validated freestanding native-build settings to `ctilde.json`. Cover linker scripts, entry symbols, object files, libraries, assembly sources, architecture flags, and separate compile and link options. Map these settings to toolchain features such as freestanding mode, omitted standard libraries, and omitted startup files while preserving deterministic command construction.

## Native interop

- [ ] Add explicit MMIO operations without reusing C~ `volatile` fields or a `[Volatile]` attribute. Define exact-width observable loads and stores, compiler ordering, device ordering, supported element types, alignment, and target-specific barriers. Provide primitives such as `Mmio.Read<T>` and `Mmio.Write<T>`, or an equivalent typed view, then lower `[Register]` onto them.
- [ ] Add general alignment control through `[Align(n)]` for types and eligible static, local, and field storage. Validate target limits and propagate alignment through `alignof`, containing layouts, `sizeof`, stack allocation, static storage, and generated C alignment declarations. Keep this separate from function-stack controls such as `[StackAlign(n)]`.
- [ ] Add native linker symbol declarations, weak imports and definitions, and explicit external reachability roots. Design an attribute such as `[Used]` so reachability preserves symbols referenced by linker scripts, hardware, interrupt vectors, boot ROM, startup assembly, or external loaders.
- [ ] Add typed extern declarations for mutable, readonly, and C `volatile` native data symbols. Define symbol naming, mutability, type and layout checks, address-taking, unsafe access, headers, linkage, and ownership restrictions. Keep native C volatility separate from C~ acquire/release `volatile` fields and explicit MMIO operations.
- [ ] Add zero-cost nominal `newtype` declarations over eligible underlying value types, with syntax such as `public newtype PhysicalAddress : nuint;`. Preserve the underlying representation and ABI, but reject implicit conversion between distinct newtypes. Define construction, explicit conversion, operators, constants, generic use, and native interop. Build `PhysicalAddress`, `VirtualAddress`, and `IoAddress` on this facility, and require explicit mapping between address domains.
- [ ] Add compile-time register definitions such as `[Register(0x60004000)]`, with `[Bit(n)]` and `[Bits(first, last)]` members lowered to deterministic mask-and-shift operations rather than C bitfields.
- [ ] Add deterministic compiler-defined bitfields such as `[BitField(typeof(uint))]`, `[Bit(n)]`, and `[Bits(first, last)]` for peripheral registers, protocol headers, page-table entries, and CPU control registers.
- [ ] Add portable CPU intrinsics for interrupt control, memory barriers, pause/halt, byte swapping, population count, and leading-zero count, plus target-specific namespaces for operations such as x86 CPUID/control registers, RISC-V CSRs, and Xtensa memory barriers.
- [ ] Add naked functions and first-class interrupt handlers.
- [ ] Add naked assembly functions: like naked functions, except the body is one complete `asm` block.
- [ ] Define retained callback registration, unregistration, rooting, and cross-thread lifetime rules separately from the existing synchronous callback profile.
- [ ] Define `[Interrupt]` as a target-specific effect profile over the general effect system. Enforce no-allocation, no-throw, no-block, IRAM-safe, DRAM-safe, and other target-specific ISR restrictions through transitive reachability.

## Deferred research

- [ ] Design independent DLL loading, dynamic runtime registration, unloading safety, and module-lifetime tracking only as a future ABI revision.
- [ ] Add parallel path-tracer workers only if the existing per-sample deterministic output gate remains byte-identical across schedules.
- [ ] Record a comparable pre-optimization renderer timing only if a representative historical build can be reconstructed; current elapsed-time measurements remain non-gating.
- [ ] Add architecture-specific inline-assembly validation only if C~ gains a native backend. The GNU C backend intentionally delegates instruction validation to the assembler.
