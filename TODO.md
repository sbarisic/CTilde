# C~ TODO

## Hosted standard-library I/O

- [x] Add hosted `Console.Read` and UTF-8 `Console.ReadLine` while preserving common console output.
- [x] Add owned `System.IO.FileHandle` open, checked buffer read, complete buffer/string write, and consuming close operations.
- [x] Add portable UTF-8 path handling, catchable `IOException`, editor documentation, strict native tests, and the `examples/HostedIo` project.
- [ ] Add seeking, directories, metadata, deletion, higher-level streams, encoding-aware text files, and asynchronous I/O when applications require them.

## Language server and editor support

- [x] Add a shared `ctilde.json` project loader and CLI `--project` mode.
- [x] Add an immutable, target-aware language-service query snapshot.
- [x] Add LSP 3.17 incremental synchronization and compiler diagnostics.
- [x] Add completion, hover, signature help, definitions, and document/workspace symbols.
- [x] Expose embedded standard-library sources through a read-only URI.
- [x] Convert the VS Code package to a bundled TypeScript language client.
- [x] Package the framework-dependent .NET 10 server and project schema.
- [x] Add compiler, manifest, CLI, and end-to-end protocol checks.
- [x] Add full-document compiler-aware semantic highlighting with TextMate fallback.
- [x] Add C#-style `///` XML documentation, warning diagnostics, embedded API sidecars, and lazy completion documentation resolution.
- [x] Add target-aware native project builds, standalone CLI publishing, and VS Code Check/Build tasks.
- [ ] Add references, rename, formatting, code actions, and auto-import completion edits.
- [ ] Add semantic-token range requests, delta results, and result-ID caching if project sizes require them.
- [ ] Publish self-contained server binaries when release distribution requires clients without .NET 10.

## User-defined arithmetic operators

- [x] Add `operator` declarations for unary and binary `+` and `-`, and binary `*` and `/` on classes and structures.
- [x] Reuse implicit conversions and better-candidate selection across both operand type hierarchies.
- [x] Lower operator calls and `+=`, `-=`, `*=`, and `/=` through ARC-aware calls with single target evaluation.
- [x] Add dedicated `ct_op_*` names, `[NoAlloc]` effects, diagnostics, editor symbols, hover, definition, and keyword highlighting.
- [x] Rewrite HostedIo vector arithmetic to exercise both scalar orders, unary operators, division, and compound assignment.
- [ ] Add user-defined conversions and additional operator families only with a separate language-design revision.

## Standard-library vectors

- [x] Add lazily loaded `System.Vec2`, `System.Vec3`, and `System.Vec4` mutable value types.
- [x] Add allocation-free constants, component and scalar arithmetic, dot products, lengths, normalization, and `Vec3.Cross`.
- [x] Add documentation, editor navigation, hosted native tests, ESP cross-build coverage, and the HostedIo migration.
- [ ] Add interpolation, clamping, distance, swizzles, conversions, or SIMD-specific lowering only when applications require them.

## Compiler architecture completion

Draft 0.12 uses the release pipeline completed for draft 0.7:

- [x] Bind methods, accessors, constructors, and initializers into immutable bound nodes and semantic maps.
- [x] Record lookup, access, overload, conversion, constant, flow, extern-use, ARC ownership, and allocation-effect results during binding.
- [x] Replace line-classified instructions with typed operands, blocks, loads, stores, calls, branches, checks, throws, ownership operations, and cleanup actions.
- [x] Make `GetDiagnostics()` stop after declaration, binding, flow/effect analysis, and target validation without constructing a C emitter, C writer, typed IR, or C translation unit.
- [x] Make lazy C emission consume `TypedIrProgram` and remove the old `MethodLowerer` and line classifier.
- [x] Split non-generated C# implementation and conformance files below 900 physical lines.

The migration retained the original 74 conformance checks and deterministic hosted and ESP-IDF C baselines. Draft 0.10 deliberately changes every generated runtime for atomic ARC and attached-thread state.

Later exception work includes filters, inner exceptions, stack traces, specialized subclasses, and broader native-boundary propagation policy.

## ESP-IDF target support

### Goal

Add ESP-IDF as a target profile for the current GNU C23 backend.

The first release must build, flash, and run a C~ program on an ESP32. It must support console output, delays, and basic GPIO.

ESP-IDF must select the chip toolchain. The C~ compiler must not contain separate ESP32, ESP32-S3, or ESP32-C6 backends.

This scope covers the ESP32 family supported by ESP-IDF 6. It does not cover the legacy ESP8266 RTOS SDK.

### Current fit

C~ already emits one GNU C23 translation unit. ESP-IDF 6 also uses GNU C23 as its default C dialect.

The parser, binder, flow analysis, typed IR, and most C emission can stay shared. ESP support needs a platform runtime and an ESP-IDF project layer.

The hardware MVP has removed the entry-point, failure, process-exit, and symbol-retention assumptions from ESP output. `CompilationOptions` and the CLI now select `hosted` or `esp-idf`, and the checked example supplies the ESP-IDF project and fixed-width shim.

These limits remain:

- The CLI emits C but does not duplicate ESP-IDF linking, flashing, or monitoring.
- `[Extern]` supports synchronous scalar, by-reference, buffer, UTF-8, opaque-handle, and delegate/context calls. `[Export]` supports ABI-safe entry from attached native threads. Retained callbacks remain unsupported.
- Managed allocation uses atomic deterministic ARC; cycles leak.
- Exception, cleanup, and release-worklist state is isolated per attached native thread.
- `defer` provides deterministic block cleanup without heap registration.
- `[NoAlloc]` verifies allocation-free generated call paths and trusts annotated native boundaries.

The draft 0.10 runtime adds atomic ARC and explicit native-thread attachment to the draft 0.9 synchronous ABI. Preserve its descriptors, vtables, drop callbacks, ownership ABI, boxing behavior, per-thread handler semantics, and fatal-runtime-failure boundary when later ESP work changes runtime and emitter files.

### Design rules

- Keep one parser, semantic model, typed IR, and C emitter pipeline.
- Put platform decisions after semantic analysis.
- Keep hosted C as the default target.
- Preserve byte-identical hosted output unless an approved runtime change requires new output.
- Use one `esp-idf` target for all supported ESP32 chips.
- Let `idf.py set-target` select Xtensa or RISC-V.
- Keep the C~ compiler independent from the ESP-IDF compiler and linker.
- Use C shims for ESP-IDF APIs with complex native types.
- Do not expose unstable ESP-IDF typedef layouts through the C~ ABI.

### Phase 1: Add target options

- [x] Add a public `CompilationTarget` value with `Hosted` and `EspIdf` members.
- [x] Add immutable compilation options to `Compilation.Create`.
- [x] Include the target in cached analysis and emission state.
- [x] Add `--target hosted|esp-idf` to the CLI.
- [x] Show the selected target in trace output and the generated file banner.
- [x] Pass the target to `TargetValidator` and `CEmitter`.
- [x] Reserve `app_main` and all ESP runtime symbols for the ESP-IDF target.
- [x] Add a diagnostic for an unknown or unsupported target.
- [x] Keep all current hosted tests unchanged and passing.

Do not add one target value for each ESP32 chip. Generated C must stay independent from the selected ESP32 instruction set.

### Phase 2: Split the platform runtime

- [x] Separate common runtime emission from hosted runtime emission.
- [x] Add an ESP-IDF runtime profile.
- [x] Keep object layout, arrays, strings, arithmetic, and checks in the common runtime.
- [x] Move entry-point, console, allocation, failure, and termination policies into the runtime profile.
- [x] Emit `void app_main(void)` for ESP-IDF.
- [x] Call `ct_module_init()` once before the C~ entry method.
- [x] Permit `app_main` to return after the C~ entry method returns.
- [x] Add a 32-bit pointer-width assertion to the ESP-IDF output.

The ESP entry wrapper must have this logical form:

```c
void app_main(void)
{
    ct_module_init();
    ctilde_entry();
}
```

ESP-IDF starts FreeRTOS before it calls `app_main`. The C~ runtime must not start or stop the scheduler.

### Phase 3: Define embedded runtime behavior

#### Console

- [x] Keep `System.Console` on the ESP-IDF standard output stream for the first release.
- [x] Disable output buffering during runtime startup, or flush each public write operation.
- [ ] Verify UART and native USB console configurations.
- [ ] Keep exact UTF-8 byte output and current numeric formatting.

ESP-IDF maps `stdout` and `stderr` to its configured console. The target must also work when the console maps to `/dev/null`.

#### Runtime failures

- [x] Replace `exit(EXIT_FAILURE)` in the ESP runtime.
- [x] Print the C~ runtime code and compact source location.
- [x] Use `abort()` as the first default failure policy.
- [x] Document reset and panic behavior from the selected ESP-IDF configuration.
- [ ] Consider configurable `abort`, `restart`, and `halt` policies after the first release.
- [x] Mark failure functions as functions that do not return.

Absolute host paths consume flash when the emitter stores them in runtime checks. Add path mapping or compact file identifiers for ESP output.

#### Process APIs

- [x] Reject `System.Environment.Exit` for the ESP-IDF target.
- [x] Add an explicit ESP restart API instead of changing `Exit` semantics.
- [x] Define the behavior of a returned C~ entry method.

A microcontroller firmware image has no portable process exit code. The compiler must not silently convert `Exit` into a reset.

#### Allocation

- [x] Replace program-lifetime allocation with non-moving ARC and make reference counts atomic in draft 0.10.
- [x] Route allocation through one target hook.
- [x] Add target-aware deallocation.
- [x] Start with normal `calloc` or `heap_caps_calloc` using byte-addressable memory.
- [x] Move exception handlers, durable locals, pending actions, and defer captures to automatic method storage.
- [x] Add automatic ownership cleanup records and exception-handler cleanup boundaries.
- [x] Generate class, array, string, box, and structure retain/drop helpers.
- [x] Drain cascading releases iteratively without recursive C calls.
- [x] Add borrowed parameters, owned returns, `[Retained]`, and `[ReturnsBorrowed]`.
- [x] Add unsafe `System.Runtime.Memory.Retain` and `Release`.
- [x] Add `[NoAlloc]` fixed-point effect inference for bounded permanent-loop paths.
- [x] Add allocation-free LIFO `defer` lowering for explicit native resource cleanup.
- [ ] Add allocation-failure tests on hardware.
- [x] Document deterministic reclamation and the cycle-leak limitation.
- [x] Add `CT_MEMORY_DIAGNOSTICS`-guarded live allocation and object counters.
- [x] Compile and link the ARC heap-recovery source for both ESP architectures.
- [x] Flash the draft 0.7 image and verify the ARC heap-recovery marker on hardware.

Permanent loops can allocate temporary managed values when their ownership does not escape an iteration. Programs must still bound live ownership and avoid accumulating cycles. Boxing is an allocation operation.

#### Stack and watchdogs

- [x] Provide an ESP `sdkconfig.defaults` value for the main task stack.
- [x] Start with an 8 KiB main task stack and validate the value with measured data.
- [x] Measure the stack high-water mark in hardware tests.
- [x] Add a delay API that yields to FreeRTOS.
- [x] Document that a busy permanent loop can trigger a watchdog.

### Phase 4: Restore embedded dead-code removal

- [x] Do not call `ct_keep_symbols()` from `app_main`.
- [x] Mark intentionally unused internal definitions with a GCC-compatible attribute.
- [x] Verify that ESP-IDF removes unused function and data sections.
- [x] Record flash and DRAM size for each conformance firmware.
- [ ] Add whole-program reachability analysis if attributes do not give acceptable size.

`ct_keep_symbols()` currently takes the address of all generated functions and runtime helpers. A reachable call can prevent linker garbage collection.

Review the new `System.Object` metadata for embedded placement:

- [ ] Put immutable vtables and descriptors in read-only storage where possible.
- [ ] Put immutable string literal data in flash.
- [ ] Measure object headers, boxes, array headers, and string descriptors on a 32-bit target.
- [ ] Set a firmware-size and static-DRAM budget after measurement.

### Phase 5: Add an ESP-IDF project template

- [x] Add a template with a top-level `CMakeLists.txt`.
- [x] Add a `main/CMakeLists.txt` component file.
- [x] Add `sdkconfig.defaults` for the console and main task stack.
- [x] Put generated C in `main/generated/ctilde_program.c`.
- [x] Keep handwritten shims outside the generated directory.
- [x] Add generated files and ESP-IDF build output to `.gitignore`.
- [x] Add a build script that runs C~ emission before `idf.py build`.
- [x] Support all source files in one C~ compilation.
- [x] Reject or clearly diagnose project paths that contain spaces.

The first project layout should be:

```text
project/
  CMakeLists.txt
  sdkconfig.defaults
  main/
    CMakeLists.txt
    generated/
      ctilde_program.c
    ctilde_esp_shim.c
```

The normal workflow should be:

```powershell
ctilde --target esp-idf Program.ct -o main/generated/ctilde_program.c
idf.py set-target esp32
idf.py build
idf.py -p COM4 flash monitor
```

The C~ CLI must not duplicate ESP-IDF dependency resolution, partition generation, linking, flashing, or monitor behavior.

### Phase 6: Add the first ESP API surface

Use a handwritten C shim for APIs that use ESP-IDF types, macros, opaque handles, or component dependencies.

- [x] Add `DelayMilliseconds(uint)`.
- [x] Add an explicit restart method.
- [x] Add GPIO input and output direction methods.
- [x] Add GPIO read and write methods.
- [x] Add a 32-bit tick-count method.
- [x] Add basic heap diagnostics.
- [x] Add a singleton RMT-backed WS2812 API with fixed-width calls.
- [ ] Add simple ESP log-level methods if `System.Console` is insufficient.
- [x] Load ESP declarations only for the ESP-IDF target.
- [x] Declare each required ESP-IDF component in `main/CMakeLists.txt`.

Keep the first shim ABI limited to fixed-width scalars and opaque values. Do not pass ESP-IDF structures by value.

### Phase 7: Extend native interop

The first target can use synchronous APIs from the C~ entry task. Full ESP-IDF use needs more language and ABI work.

#### Phase 7a: Complete the synchronous C ABI

- [x] Add signed and unsigned 64-bit integers for ESP-IDF time and counter APIs.
- [x] Add native-sized signed and unsigned integers for `intptr_t`, `uintptr_t`, and `size_t`.
- [x] Add `ref`, `in`, and definitely assigned `out` parameters with exact pointer ABI mappings.
- [x] Add unsafe `void*`, stack allocation, and explicit pointer-plus-length native buffer views.
- [x] Add scoped native UTF-8 string views. Do not pass a managed C~ `string` as `const char*` implicitly.
- [x] Add distinct opaque native handle types instead of representing every handle as an integer or unrestricted pointer.
- [x] Add ownership metadata for borrowed, created, consumed, nullable, and retained handles or pointers.
- [x] Expose `esp_err_t` as a value that preserves its numeric code, success test, and native error name instead of reducing every failure to `bool`.
- [x] Keep `defer` as the first deterministic release mechanism and diagnose discarded owned handles.

#### Phase 7b: Generate source-compatible ESP-IDF bindings

- [ ] Add a binding manifest that names required ESP-IDF components and public headers.
- [ ] Generate C~ declarations and C adapters against the installed ESP-IDF headers.
- [ ] Generate adapters for configuration structures, designated initialization, default-initializer macros, static-inline functions, and function-like macros.
- [ ] Import public constants, typedefs, flags, and enum names without baking unstable native structure layouts or enum numbers into reusable C~ artifacts.
- [ ] Compile generated adapters as part of the owning ESP-IDF component and let ESP-IDF remain responsible for include paths, Kconfig, dependencies, and linking.
- [ ] Reject private, `esp_private`, example-helper, preview, and experimental APIs unless a manifest explicitly opts into their weaker compatibility contract.

ESP-IDF guarantees public API source compatibility but not binary layout compatibility between releases. Native configuration structures must therefore be initialized in generated or handwritten C compiled against the selected ESP-IDF headers. C~ must not treat a copied native layout as a stable managed ABI.

#### Phase 7c: Export methods and support callbacks

- [x] Add an `[Export("symbol")]` attribute for C-callable C~ methods.
- [x] Generate a native header for exported symbols and reachable unmanaged layouts.
- [x] Add unsafe unmanaged function-pointer types with exact calling convention, parameter, return, and nullability rules.
- [x] Add delegates as managed target-plus-method values. Delegates are not layout-compatible with unmanaged function pointers.
- [x] Add synchronous callback trampolines that pair a typed C entry point with an explicit `void*` user context.
- [ ] Distinguish synchronous callbacks from retained callbacks and require explicit registration, unregistration, and rooted-lifetime rules for retained delegates.
- [x] Permit direct unmanaged function pointers only in `unsafe` code and emit attached-thread static-method and delegate-to-C context trampolines.
- [x] Attach the generated entry task internally and reject unattached export or callback entry with `CTT0001`.
- [x] Add native ABI attachment for native-created tasks and synchronous cross-task callbacks.
- [ ] Add retained callback registration, unregistration, and rooted-lifetime rules.
- [x] Keep C~ exceptions from unwinding through native frames for the supported synchronous current-task callback profile by terminating with `CTE0003`.

#### Phase 7d: FreeRTOS and ISR safety

- [ ] Add source-level `volatile` and atomic operations with defined ordering and shared-state rules.
- [x] Make static initialization thread-safe and isolate exception, cleanup, and release state per attached C~ task.
- [x] Support multiple attached C~ tasks through the native attachment ABI and FreeRTOS task-local storage.
- [x] Make ARC counts atomic across attached tasks.
- [ ] Define which C~ operations are permitted in an interrupt service routine.
- [ ] Add compiler-enforced ISR profiles: no allocation, no throwing, and no blocking, with IRAM/DRAM-safe reachability checks.
- [ ] Add separate task-stack configuration for exported task entry methods.

Do not call general C~ allocation, console, or virtual dispatch from an interrupt until the runtime defines ISR-safe behavior.

### Validation

#### Compiler tests

- [x] Verify byte-identical repeated ESP-IDF emission.
- [x] Verify that hosted output stays unchanged except for the approved ESP GCC format fix.
- [x] Verify `app_main` emission and the absence of hosted `main`.
- [x] Verify the ESP runtime failure policy.
- [x] Verify `Environment.Exit` diagnostics.
- [x] Verify target-specific reserved-symbol diagnostics.
- [x] Verify target-specific standard-library loading.
- [x] Verify that unused code does not stay reachable through `ct_keep_symbols`.
- [x] Verify 64-bit literal, promotion, wrapping, formatting, boxing, enum, switch, and ABI behavior.
- [x] Verify named delegate selection, ARC receiver lifetime, virtual/base dispatch, identity, exceptions, and null invocation.
- [x] Verify unmanaged function-pointer signatures, unsafe enforcement, exact native calls, and the `CTE0003` callback boundary.
- [x] Verify native-sized promotions, portable constants, wrapping, formatting, mangling, and target-width shifts.
- [x] Verify `ref`/`in`/`out` addressability, readonly and definite-assignment flow, callable signatures, ARC replacement, and native pointer mappings.
- [x] Verify native-buffer element restrictions, construction, conversion, bounds, stack-count checks, loop and escape rejection, flattening, and `[NoAlloc]` use.
- [x] Verify opaque nominal typing, native ownership moves, cleanup reservations, scoped UTF-8 input, exact ESP errors, exports, deterministic headers, and synchronous delegate/context adapters.

#### Toolchain tests

- [x] Compile fresh generated C with the installed Xtensa compiler in GNU C23 mode.
- [x] Compile fresh generated C with the installed RISC-V compiler in GNU C23 mode.
- [x] Treat all compiler warnings as errors.
- [x] Build a complete ESP-IDF firmware for `esp32`.
- [x] Build a complete ESP-IDF firmware for one RISC-V target such as `esp32c3`.
- [x] Run `idf.py size` and record flash, IRAM, and DRAM use.
- [ ] Set regression limits after the first accepted measurements.

The stale `bin/hello.c` artifact currently fails both installed cross-compilers with `-Wmisleading-indentation`. Regenerate the file before the target gate evaluates current emitter output.

#### Hardware tests

- [x] Flash the detected ESP32-D0WDQ6-V3 on `COM4`.
- [x] Verify startup and exact console output.
- [x] Obtain human confirmation that the onboard GPIO4 WS2812 visibly follows the UART blink commands.
- [x] Verify that the delay API yields without a watchdog reset.
- [x] Verify one null or bounds failure and its reported C~ runtime code.
- [x] Verify the configured failure reset or halt behavior.
- [x] Record minimum free heap and main task stack high-water mark.
- [x] Run the object construction, virtual dispatch, boxing, and string conformance cases on the target.
- [x] Flash Draft 0.7 and confirm `timer64: ok`, `delegate: 42`, `function pointer: 42`, `arc heap recovery: True`, and `CTILDE_ESP_OK` on `COM4`.
- [x] Re-run the Draft 0.7 null-failure image, confirm `CTN0001`, `abort()`, and `SW_CPU_RESET`, then restore the self-test image.
- [x] Record fresh Draft 0.7 heap and stack readings and reconfirm the GPIO4 WS2812 cycle without watchdog resets.

The 2026-08-19 Draft 0.7 run reported 298,012 bytes of free heap, a 295,468-byte minimum, and 6,960 bytes of stack high-water headroom after configuring and clearing the RMT-backed WS2812 and returning from the managed self-tests. UART completed more than ten `ws2812: on/off` cycles on GPIO4 without a watchdog reset; the same path was previously confirmed by a person to blink the onboard LED green. The failure image printed `CTN0001`, called `abort()`, and restarted with `SW_CPU_RESET`. The self-test was reflashed and rechecked as the final board state. The earlier GPIO2 run remains command-level validation only because GPIO2 is microSD MISO, not a visible LED.

#### Draft 0.8 closure

- [x] Build and size the updated `esp32` and `esp32c3` native-buffer self-test firmware.
- [x] Flash Draft 0.8 on `COM4` and confirm `native buffer: 42` plus every existing ARC, delegate, function-pointer, timer, and success marker.
- [x] Observe the GPIO4 WS2812 cycles without watchdog resets and record fresh heap and stack readings after the managed self-tests return.
- [x] Flash `RuntimeFailure.ct`, confirm `CTN0001`, `abort()`, and `SW_CPU_RESET`, then restore and recheck the Draft 0.8 self-test as the final board state.

The 2026-08-19 Draft 0.8 run produced a 151,008-byte `esp32` binary and a 154,496-byte `esp32c3` binary. The T-CAN485 reported 297,964 bytes of free heap, a 295,420-byte minimum, and 6,960 bytes of stack high-water headroom. All Draft 0.8 markers passed, more than ten GPIO4 WS2812 cycles ran without a watchdog reset, the failure image produced the required abort/reset sequence, and the self-test was restored and rechecked as the final board state.

#### Draft 0.9 closure

- [x] Build and size the `esp32` and `esp32c3` native-resource and same-task-entry firmware with warnings as errors.
- [x] Flash Draft 0.9 on `COM4` and confirm `native utf8: ok`, `opaque defer: ok`, `esp error: ESP_OK`, `delegate context: 42`, and `export: 42` plus every existing marker.
- [x] Observe more than ten GPIO4 WS2812 cycles without watchdog resets and record fresh heap and stack readings after the managed self-tests return.
- [x] Flash `RuntimeFailure.ct`, confirm `CTN0001`, `abort()`, and `SW_CPU_RESET`, then restore and recheck the Draft 0.9 self-test as the final board state.

The 2026-08-19 Draft 0.9 builds produced 153,165-byte `esp32` and 156,744-byte `esp32c3` images. The T-CAN485 reported 297,700 bytes of free heap, a 295,112-byte minimum, and 6,552 bytes of stack high-water headroom. Every new marker passed, more than ten UART-confirmed GPIO4 WS2812 cycles ran without a watchdog reset, the failure image repeatedly produced the required abort/reset sequence, and the Draft 0.9 self-test was restored and rechecked as the final board state.

#### Draft 0.10 closure

- [x] Validate atomic ARC and attached exports, delegate callbacks, unmanaged function pointers, exceptions, cleanup, and attach/detach failures under optimized hosted native tests.
- [x] Compile fresh draft 0.10 output with both Xtensa and RISC-V cross-compilers using warnings as errors.
- [x] Complete-link the `esp32` and `esp32c3` attached-task firmware.
- [x] Flash the connected dual-core ESP32 and confirm `threading: ok`, ARC heap recovery, exception isolation, and the existing hardware markers.

The 2026-08-19 Draft 0.10 build produced a 155,360-byte `esp32` image. Both complete Xtensa and RISC-V firmware links passed. The connected ESP32-D0WDQ6-V3 reported `threading: ok`, `exception: caught on ESP32`, `arc heap recovery: True`, and `CTILDE_ESP_OK`, with 297,620 bytes of free heap, a 286,624-byte minimum, and 6,520 bytes of stack high-water headroom. More than ten GPIO4 WS2812 cycles continued without a watchdog reset, and the Draft 0.10 self-test remains flashed.

### First-release acceptance criteria

ESP-IDF support is ready for its first release only when all these conditions are true:

- [x] One C~ source compilation produces deterministic ESP-IDF-ready GNU C23.
- [x] The same generated C builds for one Xtensa and one RISC-V ESP32 target.
- [x] The firmware builds with warnings treated as errors.
- [x] A real ESP32 on `COM4` prints the expected C~ console output.
- [x] A real T-CAN485 runs the WS2812 and delay example without a watchdog reset, with the visible LED result confirmed by a person.
- [x] Runtime failures produce a C~ code before the configured abort or restart.
- [x] Hosted C output and the complete hosted conformance suite still pass.
- [x] Flash, static DRAM, heap, and stack measurements are recorded.
- [x] The documentation states the ARC cycle-leak and single-C~-task limits.

### References

- [ESP-IDF C support](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/c.html)
- [ESP-IDF application startup](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/startup.html)
- [ESP-IDF build system](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/build-system.html)
- [ESP-IDF standard I/O](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/stdio.html)
- [ESP-IDF API conventions and compatibility](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-reference/api-conventions.html)
- [ESP-IDF error handling](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/error-handling.html)
- [ESP timer handles and callbacks](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-reference/system/esp_timer.html)
- [ESP-IDF memory and IRAM rules](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/memory-types.html)
