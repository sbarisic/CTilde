# T-CAN485 WS2812 hardware test

This ESP-IDF project compiles one C~ program into `main/generated/ctilde_program.c`, emits `main/generated/ctilde_exports.h`, links both through a native ESP shim, and leaves chip selection, linking, flashing, and monitoring to ESP-IDF. It targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

`Program.ct` exercises scoped UTF-8 input, a move-only opaque resource released by `defer`, exact `EspError` naming, a generated export, an instance delegate through a callback/context adapter, the earlier timer/function-pointer/native-buffer features, construction, boxing, strings, exceptions, and ARC heap recovery. The checked component manifest pins Espressif `led_strip` 3.0.3 and uses its non-DMA RMT backend. All allocation-producing managed self-tests return before measurement and the permanent allocation-free loop.

To verify the fatal runtime boundary:

```powershell
.\Build.ps1 -Target esp32 -Port COM4 -Source RuntimeFailure.ct -Flash -Monitor
```

The monitor must show `CTILDE_ESP_FAILURE_TEST` followed by runtime code `CTN0001`. Reflash `Program.ct` afterward.

## Hardware evidence

An earlier 2026-08-18 run on an ESP32-D0WDQ6-V3 revision 3.1 at `COM4` established the managed-runtime, failure, heap, stack, and yielding-delay behavior. That firmware alternated ordinary GPIO2 commands, which did not constitute a visible blink test after the hardware was identified as T-CAN485: GPIO2 is the SD MISO signal and the onboard light is an addressable WS2812 on GPIO4.

The Draft 0.9 acceptance image was flashed and monitored on 2026-08-19. It printed:

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
boxed: 7
exception: caught on ESP32
arc heap recovery: True
free heap: 297700
minimum free heap: 295112
stack high water: 6552
tick: 14
CTILDE_ESP_OK
ws2812: on
ws2812: off
```

The monitor observed more than ten 500 ms `ws2812: on/off` cycles without a watchdog reset. The same GPIO4 WS2812 path was previously confirmed by a person to blink the onboard RGB LED green in step with these commands. The Draft 0.9 failure image reported `C~ runtime error CTN0001 at RuntimeFailure.ct:23`, called `abort()`, and rebooted with `rst:0xc (SW_CPU_RESET)`.

Fresh Draft 0.9 firmware uses 153,165 bytes for the `esp32` image: 64,022 bytes of flash code, 32,560 bytes of flash data, 45,003 bytes of IRAM, and 14,020 bytes of DRAM. The `esp32c3` image uses 156,744 bytes: 79,600 bytes of flash code, 29,900 bytes of flash data, and 51,316 bytes of DRAM, including 40,012 bytes of executable text. After the failure test, `Program.ct` was rebuilt, reflashed, and rechecked through every marker and more than ten additional WS2812 cycles. It is the final board state.

Draft 0.9 uses single-threaded ARC; cycles leak and cleanup is not promised after fatal failures or reset. Native buffers and UTF-8 views are scoped synchronous values and cannot be retained by the shim. Opaque ownership is lexical; long-lived resource fields are not supported. The WS2812 API owns one native strip handle for firmware lifetime. Exports and delegate/context callbacks require the internally attached `app_main` task. Retained, cross-task, native-created-task, and interrupt callbacks remain unsupported, as do multiple C~ tasks or strips. Permanent loops must keep live ownership bounded and call a yielding API such as `DelayMilliseconds`; a busy loop can trigger the ESP-IDF task watchdog.
