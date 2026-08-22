# T-CAN485 WS2812 hardware test

This ESP-IDF project emits the draft 0.14 modular C bundle under `main/generated`: shared runtime headers, one runtime implementation, reachable namespace sources, the entry/module-lifecycle source, a symbol map, and a CMake source fragment. The component includes that fragment, and the CLI's native build stage invokes `idf.py build`. ESP-IDF still owns chip selection, component resolution, incremental compilation, linking, flashing, and monitoring. The native shim is compiled by the component CMake project. It targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

The repeatable physical acceptance runner uses the connected board defaults and restores the ordinary Release image on every exit path:

```powershell
.\Test\Test-Esp32Hardware.ps1
.\Test\Test-Esp32Hardware.ps1 -AutomatedOnly
```

Run it from the repository root. The normal command prompts once to confirm visible WS2812 activity. `-AutomatedOnly` performs all machine-verifiable checks but leaves that release gate pending. Each run writes an ignored JSON report and UART transcripts under `artifacts/esp32-hardware`.

`Program.ct` exercises scoped UTF-8 input, a move-only opaque resource released by `defer`, exact `EspError` naming, generated exports, an instance delegate through a callback/context adapter, two attached FreeRTOS worker tasks, per-task exceptions and cleanup, the earlier timer/function-pointer/native-buffer features, construction, boxing, strings, and ARC heap recovery. The checked component manifest pins Espressif `led_strip` 3.0.3 and uses its non-DMA RMT backend. All allocation-producing managed self-tests return before measurement and the permanent allocation-free loop.

To verify the fatal runtime boundary:

```powershell
.\Build.ps1 -Target esp32 -Port COM4 -Source RuntimeFailure.ct -Flash -Monitor
```

The monitor must show `CTILDE_ESP_FAILURE_TEST` followed by runtime code `CTN0001`. Reflash `Program.ct` afterward.

## UART GDB-stub debugging

The C~ VS Code debugger can launch or attach through ESP-IDF's runtime UART GDB stub. The example declares `esp_gdbstub` as a private component dependency so its options remain available with `MINIMAL_BUILD`. Create a separate debug `sdkconfig` with these values before using **C~: Debug Project**:

```text
CONFIG_ESP_SYSTEM_GDBSTUB_RUNTIME=y
CONFIG_ESP_GDBSTUB_SUPPORT_TASKS=y
CONFIG_COMPILER_OPTIMIZATION_DEBUG=y
```

Set `ctilde.debugger.serialPort` to the board port. This example uses 460800 baud for both the ESP-IDF console and the debugger; any external serial monitor must use the same rate. Debug Launch validates this configuration, builds and flashes a version-2 instrumented image, then connects during its 15-second pre-initialization gate. The checked-in Launch configuration uses guarded ARC diagnostics. Source, function, log, and exception breakpoints use logical probes and do not consume the ESP32's two instruction-breakpoint slots; hardware data watchpoints remain limited by the target. Debug Attach reuses matching ELF and debug-map artifacts. The adapter keeps the serial port in a small ESP-IDF-Python bridge for the complete session. The runtime stub therefore consumes UART input during debugging, so do not run an interactive monitor on the same port at the same time. C~ console writes made after attachment appear in VS Code's Debug Console. Pressing Stop clears logical and hardware debugger state and continues the current firmware; after the session ends, output returns to the ordinary UART console.

## Hardware evidence

The draft 0.14 ABI 14 sources and inline-assembly fixture pass strict syntax checks and complete modular links for both ESP32/Xtensa and ESP32-C3/RISC-V with ESP-IDF 6.0.2. On 2026-08-22, automated physical acceptance ran on the ESP32-D0WDQ6-V3 revision 3.1 T-CAN485 at `COM4` and 460800 baud using Xtensa GCC 15.2.0 and ESP-GDB 17.1. The ordinary 168,480-byte Release binary passed every runtime marker, ARC heap recovery, and 25 alternating WS2812 UART transitions. It reported 295,204 bytes free, a 284,740-byte minimum, and 6,744 bytes of main-task stack headroom. The isolated fatal image emitted `CTN0001`, called `abort()`, and rebooted. The acceptance runner restored the ordinary Release image afterward.

The same run exercised debugger metadata v2 with guarded memory diagnostics. Six logical breakpoints were active at once; startup and first-statement stops, C~ Step Over/Into/Out, five FreeRTOS tasks, caught-exception translation, lexical locals, live ARC objects, intact canaries, a reference-count hardware watchpoint, console forwarding, and zero live objects after the managed self-tests all passed. Disconnect cleared debugger state and continued the firmware without a reset, and the passive UART observer saw four subsequent alternating WS2812 messages. A separate no-debugger boot passed the 15-second startup gate after 14.16 seconds. The ignored machine-readable report is `artifacts/esp32-hardware/20260822-155832.json`.

After the acceptance runner restored the ordinary Release image, the operator confirmed that the onboard GPIO4 WS2812 visibly alternated. Draft 0.14 ABI 14 is therefore the latest complete physical-board acceptance, including both machine-verifiable results and the human-visible output check.

The Draft 0.12 acceptance image was flashed and monitored on 2026-08-20. Its stable output was:

```text
esp error: ESP_OK
C~ ESP-IDF hardware test
virtual: 42
delegate: 42
function pointer: 42
timer64: ok
native buffer: 42
native utf8: ok
opaque defer: ok
delegate context: 42
export: 42
threading: ok
boxed: 7
exception: caught on ESP32
arc heap recovery: True
free heap: 297692
minimum free heap: 286696
stack high water: 6704
tick: 19
CTILDE_ESP_OK
ws2812: on
ws2812: off
```

That image reported the heap and stack values above and completed more than 25 UART-confirmed 500 ms `ws2812: on/off` transitions without a watchdog reset. The same GPIO4 path was previously confirmed by a person to blink the onboard RGB LED green in step with the commands. The `esp32` build used a 154,640-byte binary and a 154,525-byte measured image: 65,222 bytes of flash code, 32,704 bytes of flash data, 45,003 bytes of IRAM, and 14,028 bytes of DRAM. The `esp32c3` build used a 159,728-byte binary and a 159,428-byte measured image: 81,834 bytes of flash code, 30,236 bytes of flash data, and 51,422 bytes of DRAM, including 40,102 bytes of executable text.

The separate failure image printed `C~ runtime error CTN0001 at RuntimeFailure.ct:23`, called `abort()`, and rebooted with `rst:0xc (SW_CPU_RESET)`. `Program.ct` was then rebuilt, reflashed, and rechecked through every marker and additional WS2812 cycles; it is the final board state from the physical-board validation.

Draft 0.14 uses atomic ARC and per-task exception, cleanup, diagnostic-origin, and release state; cycles leak, and cleanup is not promised after a panic, fatal failure, or reset. The example reserves FreeRTOS application TLS slot 0 through `sdkconfig.defaults`. Native buffers and UTF-8 views remain scoped synchronous values and cannot be retained by the shim. Opaque ownership is lexical; long-lived resource fields are not supported. The WS2812 API owns one native strip handle for firmware lifetime. Native-created tasks must call `ct_thread_attach` and `ct_thread_detach`; retained callbacks and interrupt entry remain unsupported. Permanent loops must keep live ownership bounded and call a yielding API such as `DelayMilliseconds`; a busy loop can trigger the ESP-IDF task watchdog.
