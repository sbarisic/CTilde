# T-CAN485 WS2812 hardware test

This ESP-IDF project emits the draft 0.14 modular C bundle under `main/generated`: shared runtime headers, one runtime implementation, reachable namespace sources, the entry/module-lifecycle source, a symbol map, and a CMake source fragment. The component includes that fragment, and the CLI's native build stage invokes `idf.py build`. ESP-IDF still owns chip selection, component resolution, incremental compilation, linking, flashing, and monitoring. The native shim is compiled by the component CMake project. It targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

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

Set `ctilde.debugger.serialPort` to the board port. This example uses 460800 baud for both the ESP-IDF console and the debugger; any external serial monitor must use the same rate. Debug Launch validates this configuration, builds and flashes the firmware, interrupts the initialized application over UART, and starts the architecture-specific GDB from the active ESP-IDF toolchain. Debug Attach reuses matching ELF and C~ debug-map artifacts. The adapter keeps the serial port in a small ESP-IDF-Python bridge for the complete session. The runtime stub therefore consumes UART input during debugging, so do not run an interactive monitor on the same port at the same time.

## Hardware evidence

The draft 0.14 ABI 14 sources and inline-assembly fixture pass strict syntax checks and complete modular links for both ESP32/Xtensa and ESP32-C3/RISC-V with ESP-IDF 6.0.2. On 2026-08-21, a debug-configured ABI 14 image was flashed to the connected dual-core ESP32. The runtime UART stub accepted the architecture-specific GDB, reported five FreeRTOS tasks, hit a C~ source breakpoint, exposed C~ locals, stepped to the next C~ statement, stopped on a handled C~ exception, resumed, and detached cleanly. Continued UART output confirmed the WS2812 loop remained active after detach. This focused debugger run does not replace the complete ABI 14 runtime regression, shutdown/lifetime, and monitor acceptance gate. The draft 0.12 run below remains the latest complete physical-board acceptance.

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
