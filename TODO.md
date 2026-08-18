# C~ TODO

## Language server and editor support

- [x] Add a shared `ctilde.json` project loader and CLI `--project` mode.
- [x] Add an immutable, target-aware language-service query snapshot.
- [x] Add LSP 3.17 incremental synchronization and compiler diagnostics.
- [x] Add completion, hover, signature help, definitions, and document/workspace symbols.
- [x] Expose embedded standard-library sources through a read-only URI.
- [x] Convert the VS Code package to a bundled TypeScript language client.
- [x] Package the framework-dependent .NET 10 server and project schema.
- [x] Add compiler, manifest, CLI, and end-to-end protocol checks.
- [ ] Add references, rename, semantic highlighting, formatting, code actions, and auto-import completion edits.
- [ ] Publish self-contained server binaries when release distribution requires clients without .NET 10.

## Compiler architecture completion

Draft 0.5 exception behavior is implemented, but the body pipeline remains transitional. Complete these tasks before a release:

1. Bind method, accessor, constructor, and initializer bodies into immutable bound nodes.
2. Move lookup, access checks, overload resolution, conversions, constants, and flow diagnostics out of `MethodLowerer`.
3. Replace line-classified instructions with typed three-address operands, blocks, loads, stores, calls, branches, checks, throws, and cleanup actions.
4. Make `GetDiagnostics()` stop after declaration, binding, flow, and target validation. It must not construct rendered C fragments.
5. Make the C emitter consume structured IR only.

Retain the 59 conformance checks and byte-identical C output while this work proceeds.

Later exception work includes filters, inner exceptions, stack traces, specialized subclasses, thread-local handler state, and a defined native-boundary policy.

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
- `[Extern]` supports simple C ABI calls but not callbacks or exported C~ methods.
- Managed allocation has program lifetime.
- Exception-handler state supports one C~ execution task.
- `defer` provides deterministic block cleanup without heap registration.
- `[NoAlloc]` verifies allocation-free generated call paths and trusts annotated native boundaries.

The draft 0.5 object and exception runtime is complete at the language-behavior level. Preserve its managed header, descriptors, vtables, boxing behavior, handler semantics, and fatal-runtime-failure boundary when ESP work changes the runtime and emitter files.

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

- [x] Keep zeroed program-lifetime allocation for the first release.
- [x] Route allocation through one target hook.
- [x] Start with normal `calloc` or `heap_caps_calloc` using byte-addressable memory.
- [x] Move exception handlers, durable locals, pending actions, and defer captures to automatic method storage.
- [x] Add `[NoAlloc]` fixed-point effect inference for bounded permanent-loop paths.
- [x] Add allocation-free LIFO `defer` lowering for explicit native resource cleanup.
- [ ] Add allocation-failure tests on hardware.
- [x] Document that allocated C~ objects do not return memory to the heap.
- [ ] Add optional heap counters for development builds.

Programs must not allocate strings, arrays, boxes, or objects without a bound inside permanent loops. Boxing is an allocation operation.

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

- [ ] Add an `[Export("symbol")]` attribute for C-callable C~ methods.
- [ ] Generate a native header for exported symbols and shared runtime layouts.
- [ ] Add callback trampolines with defined lifetime rules.
- [ ] Add function or delegate types if trampolines are not sufficient.
- [ ] Add opaque native handle types.
- [ ] Add 64-bit integer types for ESP-IDF time and counter APIs.
- [ ] Add `volatile` and atomic access rules.
- [ ] Define thread safety for static initialization and object identity hashes.
- [ ] Define which C~ operations are permitted in an interrupt service routine.
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

The corrected 2026-08-18 T-CAN485 run reported 298,172 bytes of free and minimum free heap and 7,744 bytes of stack high-water headroom after configuring and clearing the RMT-backed WS2812. UART alternated `ws2812: on/off` on GPIO4 for several cycles without a watchdog reset, and the onboard LED was confirmed to blink green in step with it. The null-failure image printed `CTN0001`, aborted, and restarted with `SW_CPU_RESET`; the WS2812 image was restored afterward. The earlier GPIO2 run was command-level validation only because GPIO2 is microSD MISO, not a visible LED.

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
- [x] The documentation states the permanent-allocation and single-C~-task limits.

### References

- [ESP-IDF C support](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/c.html)
- [ESP-IDF application startup](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/startup.html)
- [ESP-IDF build system](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/build-system.html)
- [ESP-IDF standard I/O](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/stdio.html)
