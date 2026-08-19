# T-CAN485 WS2812 hardware test

This ESP-IDF project compiles one C~ program into `main/generated/ctilde_program.c`, emits `main/generated/ctilde_exports.h`, and uses the CLI's native build stage to invoke `idf.py build`. ESP-IDF still owns chip selection, component resolution, linking, flashing, and monitoring. The native shim is compiled by the component CMake project. It targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

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

## Hardware evidence

An earlier 2026-08-18 run on an ESP32-D0WDQ6-V3 revision 3.1 at `COM4` established the managed-runtime, failure, heap, stack, and yielding-delay behavior. That firmware alternated ordinary GPIO2 commands, which did not constitute a visible blink test after the hardware was identified as T-CAN485: GPIO2 is the SD MISO signal and the onboard light is an addressable WS2812 on GPIO4.

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

The monitor observed more than 25 500 ms `ws2812: on/off` transitions without a watchdog reset. The same GPIO4 WS2812 path was previously confirmed by a person to blink the onboard RGB LED green in step with these commands.

The Draft 0.10 threading image was flashed and monitored on the same board on 2026-08-19. Both attached FreeRTOS workers completed their export, callback, ARC, exception, and defer checks and produced `threading: ok`. The complete run also printed `exception: caught on ESP32`, `arc heap recovery: True`, and `CTILDE_ESP_OK`; it reported 297,620 bytes of free heap, a 286,624-byte minimum, and 6,520 bytes of stack high-water headroom.

The optimized Draft 0.12 image was flashed and monitored on 2026-08-20. It printed every marker above plus `threading: ok`, reported 297,692 bytes of free heap, a 286,696-byte minimum, and 6,704 bytes of stack high-water headroom, and completed more than 25 UART-confirmed WS2812 transitions without a watchdog reset. The `esp32` build uses a 154,640-byte binary and a 154,525-byte measured image: 65,222 bytes of flash code, 32,704 bytes of flash data, 45,003 bytes of IRAM, and 14,028 bytes of DRAM. The `esp32c3` build uses a 159,728-byte binary and a 159,428-byte measured image: 81,834 bytes of flash code, 30,236 bytes of flash data, and 51,422 bytes of DRAM, including 40,102 bytes of executable text. The failure image printed `C~ runtime error CTN0001 at RuntimeFailure.ct:23`, called `abort()`, and rebooted with `rst:0xc (SW_CPU_RESET)`. `Program.ct` was then rebuilt, reflashed, and rechecked through every marker and additional WS2812 cycles; it is the final board state.

Draft 0.10 uses atomic ARC and per-task exception, cleanup, and release state; cycles leak and cleanup is not promised after fatal failures or reset. The example reserves FreeRTOS application TLS slot 0 through `sdkconfig.defaults`. Native buffers and UTF-8 views remain scoped synchronous values and cannot be retained by the shim. Opaque ownership is lexical; long-lived resource fields are not supported. The WS2812 API owns one native strip handle for firmware lifetime. Native-created tasks must call `ct_thread_attach` and `ct_thread_detach`; retained callbacks and interrupt entry remain unsupported. Permanent loops must keep live ownership bounded and call a yielding API such as `DelayMilliseconds`; a busy loop can trigger the ESP-IDF task watchdog.
