# T-CAN485 WS2812 hardware test

This ESP-IDF project compiles one C~ program into `main/generated/ctilde_program.c`, links the generated translation unit with a fixed-width ESP shim, and leaves chip selection, linking, flashing, and monitoring to ESP-IDF. It targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

`Program.ct` exercises 64-bit monotonic time, an instance delegate with virtual dispatch, a synchronous unmanaged callback, a stack-backed byte buffer flattened through the native shim, construction, boxing, strings, exceptions, ARC heap recovery, heap and stack diagnostics, FreeRTOS delay, and the onboard WS2812. The ARC test repeatedly creates and releases mixed linked objects, reference-bearing structures, arrays, boxes, and dynamic strings. Free heap must return to its baseline within 512 bytes. The checked component manifest pins Espressif `led_strip` 3.0.3 and uses its non-DMA RMT backend. All allocation-producing managed self-tests return before measurement and the permanent allocation-free loop.

To verify the fatal runtime boundary:

```powershell
.\Build.ps1 -Target esp32 -Port COM4 -Source RuntimeFailure.ct -Flash -Monitor
```

The monitor must show `CTILDE_ESP_FAILURE_TEST` followed by runtime code `CTN0001`. Reflash `Program.ct` afterward.

## Hardware evidence

An earlier 2026-08-18 run on an ESP32-D0WDQ6-V3 revision 3.1 at `COM4` established the managed-runtime, failure, heap, stack, and yielding-delay behavior. That firmware alternated ordinary GPIO2 commands, which did not constitute a visible blink test after the hardware was identified as T-CAN485: GPIO2 is the SD MISO signal and the onboard light is an addressable WS2812 on GPIO4.

The Draft 0.8 acceptance image was flashed and monitored on 2026-08-19. It printed:

```text
C~ ESP-IDF hardware test
virtual: 42
delegate: 42
function pointer: 42
timer64: ok
native buffer: 42
boxed: 7
exception: caught on ESP32
arc heap recovery: True
free heap: 297964
minimum free heap: 295420
stack high water: 6960
tick: 13
CTILDE_ESP_OK
ws2812: on
ws2812: off
```

The monitor observed more than ten 500 ms `ws2812: on/off` cycles without a watchdog reset. The same GPIO4 WS2812 path was previously confirmed by a person to blink the onboard RGB LED green in step with these commands. The Draft 0.8 failure image reported `C~ runtime error CTN0001 at RuntimeFailure.ct:17`, called `abort()`, and rebooted with `rst:0xc (SW_CPU_RESET)`.

Fresh Draft 0.8 firmware builds measure 151,008 bytes for `esp32` and 154,496 bytes for `esp32c3`. The `esp32` image uses 62,254 bytes of flash code, 32,304 bytes of flash data, 45,003 bytes of IRAM, and 13,756 bytes of DRAM. The `esp32c3` image uses 77,604 bytes of flash code, 29,636 bytes of flash data, and 51,024 bytes of DRAM, including 39,976 bytes of executable text. After the failure test, `Program.ct` was rebuilt, reflashed, and rechecked through every marker and several additional WS2812 cycles. It is the final board state.

Draft 0.8 uses single-threaded ARC; cycles leak and cleanup is not promised after fatal failures or reset. Native buffers are scoped synchronous pointer-plus-length views and cannot be retained by the shim. The WS2812 API owns one native strip handle for firmware lifetime; identical configuration is idempotent and conflicting reconfiguration fails. Only static-method function pointers invoked synchronously on the current C~ task are supported. Retained, cross-task, instance/delegate-to-C, and interrupt callbacks remain unsupported, as do multiple C~ tasks or strips. Permanent loops must keep live ownership bounded and call a yielding API such as `DelayMilliseconds`; a busy loop can trigger the ESP-IDF task watchdog.
