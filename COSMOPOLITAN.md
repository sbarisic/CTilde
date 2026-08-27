# Cosmopolitan target design

## Status

This document defines the staged C~ integration of [Cosmopolitan Libc](https://github.com/jart/cosmopolitan) and its [cosmocc toolchain](https://github.com/jart/cosmopolitan/blob/master/tool/cosmocc/README.md). Draft 0.24 implements Stage 1: explicit x86-64 semantics, project/CLI/schema/editor support, a dedicated WSL-aware driver, retained ELF/DWARF and APE artifacts, and a managed-runtime acceptance example executed from the same APE on WSL/Linux and Windows.

The implementation deliberately starts with a multi-operating-system x86-64 executable. AArch64 and a combined x86-64/AArch64 image follow only after each architecture has an independent semantic compilation. This avoids producing a nominally fat binary from C~ source whose `static if`, target queries, CPU intrinsics, or inline assembly were already resolved for one architecture.

## Why this is a distinct target

Cosmopolitan is not a conventional host compiler selection. It provides its own C library, startup objects, linker scripts, compiler wrappers, executable format, POSIX compatibility layer, and debugging artifacts. Its current toolchain can produce programs for Linux, macOS, Windows, FreeBSD, OpenBSD, and NetBSD on x86-64 and AArch64. The default `cosmocc` wrapper can combine both architectures into one Actually Portable Executable (APE).

C~ already has useful seams for this work:

- one deterministic GNU C23 backend;
- an explicit `CompilationTarget` and compile-time `Target.Profile`;
- hosted ARC, exceptions, console, file I/O, math, TLS, and pthread-based concurrency;
- source-owned modular C and content-addressed native object caches;
- a dedicated freestanding driver that demonstrates target-specific compiler discovery and architecture probes;
- stable public headers and symbol maps.

The Cosmopolitan target should reuse hosted language semantics and most hosted C emission. It must not reuse the ordinary hosted build driver as an undocumented compiler alias, because output shaping, toolchain probing, architecture selection, portable ABI rules, and debug artifacts differ.

## Toolchain facts that shape the design

The official toolchain contract has several consequences for C~:

1. `cosmocc` is the fat x86-64/AArch64 wrapper. The architecture-specific `x86_64-unknown-cosmo-cc` and `aarch64-unknown-cosmo-cc` wrappers produce multi-OS programs for one architecture.
2. The physical `*-linux-cosmo-*` compiler executables are internal implementation tools. C~ should invoke the supported `*-unknown-cosmo-*` wrappers and let them supply ABI flags, startup objects, linker scripts, `fixupobj`, and runtime libraries.
3. An architecture-specific wrapper first emits an ELF/DWARF carrier. `objcopy -SO binary` unwraps the compact APE image. The build should retain both artifacts: the portable image for distribution and the ELF carrier for symbols and native debugging.
4. AArch64 Cosmopolitan reserves registers and has special TLS requirements. C~ must not reconstruct those compiler flags itself.
5. APE does not currently provide a general shared-library ecosystem or position-independent executable model. Static linking is the supported default; independent DLL loading remains outside this target.
6. The toolchain is relocatable but requires a Unix shell. Windows support should initially use WSL unless the user supplies a working native POSIX environment explicitly.
7. APE files can also contain ZIP assets under `/zip`, but embedding assets requires Cosmopolitan's compatible ZIP tool and a separate deterministic asset contract.
8. Reproducible builds need controlled locale/time inputs and must not admit `__DATE__` or `__TIME__` into generated sources. C~ already emits deterministic source and should set `LC_ALL=C` and `SOURCE_DATE_EPOCH=0` for the native driver.

## Public target model

Add:

```csharp
public enum CompilationTarget
{
    Hosted,
    EspIdf,
    Freestanding,
    Cosmopolitan
}
```

and extend `System.Runtime.TargetProfile` with `Cosmopolitan`.

The target has hosted process semantics:

- `[EntryPoint]` is required and emits ordinary `main` startup.
- The runtime initializes and shuts down automatically.
- Managed objects, arrays, strings, ARC, exceptions, `defer`, console, environment, math, hosted file I/O, threads, mutexes, and TLS remain available.
- `[RuntimeImpl]`, `[Naked]`, `[TaskEntry]`, `[Interrupt]`, fixed-address registers, and MMIO are invalid because they belong to freestanding or MCU execution models.
- Public exports and synchronous native callbacks retain the hosted attachment and exception-barrier contract.

`Target.Profile` evaluates to `TargetProfile.Cosmopolitan`. `Target.PointerSize` is eight in every planned Cosmopolitan configuration. `Target.Architecture` is still a single constant and therefore requires one semantic architecture per compilation.

## Architecture stages

### Stage 1: x86-64 multi-OS APE

Require `architecture: "x64"`. Invoke `x86_64-unknown-cosmo-cc` and the matching `x86_64-linux-cosmo-objcopy`. This produces one executable that can run on supported x86-64 operating systems.

The source may use x64 target queries, x64 CPU intrinsics, and validated x64 GNU inline assembly. Those choices are honest because every native slice has the same architecture.

### Stage 2: AArch64 multi-OS APE

Require `architecture: "arm64"`. Invoke `aarch64-unknown-cosmo-cc` and matching tools. Add an independent acceptance matrix and keep all register/TLS policy in Cosmopolitan's wrapper.

### Stage 3: x86-64 plus AArch64 fat APE

Do not feed one already-bound C~ program to `cosmocc`. Instead:

1. create one x64 `Compilation` and one Arm64 `Compilation` from the same syntax trees;
2. bind and prune `static if` independently;
3. emit and compile two deterministic native bundles;
4. verify that public exports and runtime ABI signatures agree;
5. link each architecture through its supported wrapper;
6. combine the resulting images with `apelink`;
7. retain both architecture-specific debug carriers.

This stage needs a project-level architecture set rather than a synthetic `TargetArchitecture.Fat` value. A source-level query always observes the architecture of its current slice.

## Project and CLI shape

The first project form is intentionally small:

```json
{
  "target": "cosmopolitan",
  "architecture": "x64",
  "sources": ["src/**/*.ct"],
  "build": {
    "cLayout": "modules",
    "compiler": "auto",
    "configuration": "release",
    "executable": "build/tool.com",
    "lto": false
  },
  "cosmopolitan": {
    "mode": "default"
  }
}
```

`cosmopolitan.mode` accepts:

- `default`: normal Cosmopolitan runtime and tracing support;
- `tiny`: pass `-mtiny`, normally with `-Os`;
- `debug`: pass `-mdbg` and retain the full debug runtime.

The build configuration controls C~ optimization and debug information. `mode` independently controls which Cosmopolitan runtime library variant is linked and defaults to `default`.

Direct builds use `--target cosmopolitan --architecture x64 --native-output app.com`. `--compiler` accepts an exact wrapper path or `wsl:<command>`. `auto` checks `CTILDE_COSMOCC`, then the architecture-specific wrapper on `PATH`, then the same wrapper in WSL on Windows. The environment variable names the supported wrapper, not the physical `*-linux-cosmo-gcc` executable.

The initial target rejects:

- architecture `auto`, x86, Arm32, Xtensa, and RISC-V;
- ESP-IDF, freestanding linker, and panic-policy options;
- hosted debugger preparation until the ELF-carrier descriptor is implemented;
- response files and user options that replace compiler-owned output, compile mode, startup objects, linker script, or `objcopy` step.

Native `.c`, `.S`, `.s`, object, archive, and controlled option lists should be added only after the core generated-program path is stable. They must use Cosmopolitan-built objects and archives; mixing host ABI objects is invalid.

## Native build pipeline

For each generated C source:

1. probe the wrapper's predefined macros and require `__COSMOPOLITAN__`, `__COSMOCC__`, the declared architecture, and 64-bit pointers;
2. compile with `-std=gnu23`, configuration flags, `-Wall -Wextra -Werror`, section splitting, and optional LTO;
3. cache the object by Draft version, generated content, shared headers, compiler identity, target, architecture, mode, and effective flags;
4. link all generated objects through the supported `*-unknown-cosmo-cc` wrapper into `<image>.dbg`;
5. unwrap the APE with matching `objcopy -SO binary <image>.dbg <image>`;
6. retain the carrier and report both outputs. The acceptance runner inspects the carrier and executes the portable image on both supported test hosts.

The driver must not add `-nostdlib`, a C~ linker script, or custom CRT objects. Those are freestanding responsibilities and would bypass Cosmopolitan's supported ABI contract.

The initial implementation should use GCC mode. `-mclang` can become an explicit later option after the same acceptance suite passes with Cosmopolitan's bundled Clang.

## Runtime and standard-library mapping

The generated runtime should select its POSIX branch under Cosmopolitan. Cosmopolitan supplies POSIX behavior on Windows, so generated C must not compile a separate `_WIN32` branch merely because the resulting APE later runs on Windows.

Expected mappings are:

| C~ facility | Cosmopolitan mapping |
|---|---|
| ARC allocation | `calloc`/`free` and C11 atomics |
| exceptions | `setjmp`/`longjmp` within C~ frames |
| console | `stdin`, `stdout`, `stderr`, and UTF-8 bytes |
| file I/O | portable POSIX/C file operations supplied by Cosmo Libc |
| math | Cosmo Libc single-precision math |
| threads/TLS | Cosmopolitan pthreads and `_Thread_local` |
| process exit | `exit`/`abort` |
| CPU intrinsics | existing architecture-specific C~ lowering |

Acceptance must verify behavior on Linux and Windows before the target documentation promises both. macOS and BSD claims should remain inherited toolchain capability, not measured C~ support, until CI or recorded manual execution exists.

## Native interop boundaries

`[Extern]` remains a trusted native boundary. Portable C~ applications should link only functions and data available in Cosmopolitan or explicitly supplied Cosmopolitan-built archives. The compiler cannot prove that an arbitrary extern exists on every operating system.

Initial rules:

- ordinary exports and extern data retain the System V C-level signature mapping used by Cosmopolitan;
- `[Used]` must be verified through the final APE and its ELF carrier before claiming retention;
- custom `[Section]` output is accepted only after the APE linker script is shown to preserve the requested section safely;
- `[LinkerSymbol]` is rejected until supported Cosmopolitan linker symbols are documented and tested;
- no dynamic library import/export model is added;
- source-level operating-system branching is not added because one APE is intended to run unchanged across systems.

## Debugging and diagnostics

The portable image is the distribution artifact. The ELF carrier is the debug artifact. A later debug descriptor should record both paths and use Cosmopolitan's `cosmoaddr2line` or GDB against the carrier. C~ source maps and logical probes remain reusable, but Windows/macOS process launch and symbol relocation require measured adapter work.

Suggested diagnostics:

- `CT4118`: unsupported Cosmopolitan target architecture, missing/mismatched wrapper, or invalid toolchain macros;
- `CT4119`: unsupported target feature or incompatible native option;
- `CT4120`: link, unwrap, or APE validation failure.

Toolchain diagnostics should name the failed wrapper and preserve its native stderr. C~ diagnostics must be complete before the native toolchain runs.

## Reproducibility, distribution, and security

- Pin the tested Cosmopolitan release in acceptance documentation, not inside generated binaries.
- Set deterministic locale and source-date environment values.
- Keep generated objects outside the source tree and use the existing build lock.
- Include toolchain executable content/version in cache identity.
- Never download a toolchain implicitly during a normal build. Installation is an explicit user or test-harness action.
- Preserve Cosmopolitan's embedded license notices. Do not strip `.ident` or notices outside the supported wrapper flow.
- Do not auto-install a systemwide APE loader. It is optional and requires administrator/root policy.
- Do not promise antivirus acceptance, code signing, setuid behavior, or platform-native assimilation. `assimilate` remains an explicit post-build tool.

## Acceptance plan

The core x64 milestone is implemented. The full x64 audit remains complete only when all of the following pass:

1. Manifest, CLI, schema, and language-server target parsing.
2. `Target.Profile == Cosmopolitan`, x64 architecture, and pointer-size conformance.
3. Unity and modular generated C equivalence and deterministic repeated APE bytes under fixed build environment.
4. Managed objects, arrays, strings, ARC, exceptions, `defer`, static initialization/shutdown, console, math, hosted file I/O, threads, mutexes, exports, and callbacks.
5. Release and debug builds through an official pinned cosmocc toolchain.
6. ELF inspection for entry, exported symbols, runtime symbols, section layout, and absence of unresolved host-libc dependencies.
7. Portable image validation and execution on WSL/Linux x86-64.
8. Execution of the same image on Windows x86-64, including Unicode console and file paths.
9. Full existing MSVC, WSL GCC, WSL Clang, freestanding, ESP-IDF, editor, format, and diff regressions.

AArch64 and fat-image milestones repeat the semantic/runtime suite per slice. macOS and BSD become measured C~ support only when the exact APE is executed there.

## Dependency-ordered implementation plan

1. **Implemented in Draft 0.24:** add the target/profile enums, parser/schema/editor state, target queries, availability rules, and x64-only diagnostics.
2. **Implemented in Draft 0.24:** reuse the hosted standard-library and runtime surface through explicit target selection.
3. **Implemented in Draft 0.24:** add a dedicated x64 `CosmopolitanBuildDriver` with WSL-aware wrapper discovery, macro probing, deterministic environment, object caching, ELF-carrier linking, and APE unwrapping.
4. **Implemented in Draft 0.24:** add conformance and one substantial multi-function example covering managed runtime, exceptions, file I/O, and concurrency.
5. **Measured with official 4.0.2:** verify WSL/Linux plus Windows execution of the same image and inspect its x86-64 ELF carrier.
6. Audit and enable `[Used]`, `[Section]`, exports, callbacks, public headers, and source debugging one feature at a time.
7. Add the Arm64 single-architecture wrapper and acceptance.
8. Add dual semantic compilation, cross-slice ABI verification, and `apelink` for true fat output.
9. Add optional deterministic `/zip` assets, native inputs, supported runtime modes, Clang mode, and debugger integration.

## Alternatives rejected

- **Treat `cosmocc` as hosted `--compiler`:** this hides the target profile, gives incorrect target queries, lacks APE/debug artifact handling, and permits incompatible host objects.
- **Use the freestanding target:** this discards the managed hosted library and bypasses Cosmopolitan startup/runtime contracts.
- **Call the physical `*-linux-cosmo-gcc`:** this makes C~ responsible for undocumented ABI flags, startup objects, linker scripts, and fixups.
- **Use default fat `cosmocc` immediately:** one C~ semantic compilation cannot honestly represent two architectures when compile-time target selection exists.
- **Add compile-time OS queries:** one APE must retain runtime portability; compile-time OS pruning would defeat it.
- **Bundle or auto-download cosmocc in CTilde:** the 4.0.2 package is large, independently versioned, and has its own installation and notice obligations.
