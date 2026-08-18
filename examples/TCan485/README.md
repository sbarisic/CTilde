# T-CAN485 WS2812 hardware test

This ESP-IDF project compiles one C~ program into `main/generated/ctilde_program.c`, links the generated translation unit with a fixed-width ESP shim, and leaves chip selection, linking, flashing, and monitoring to ESP-IDF. It targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

`Program.ct` exercises construction, virtual dispatch, boxing, strings, exceptions, heap and stack diagnostics, FreeRTOS delay, and the onboard WS2812. The checked component manifest pins Espressif `led_strip` 3.0.3 and uses its non-DMA RMT backend. The permanent loop performs no C~ allocations.

To verify the fatal runtime boundary:

```powershell
.\Build.ps1 -Target esp32 -Port COM4 -Source RuntimeFailure.ct -Flash -Monitor
```

The monitor must show `CTILDE_ESP_FAILURE_TEST` followed by runtime code `CTN0001`. Reflash `Program.ct` afterward.

## Hardware evidence

An earlier 2026-08-18 run on an ESP32-D0WDQ6-V3 revision 3.1 at `COM4` established the managed-runtime, failure, heap, stack, and yielding-delay behavior. That firmware alternated ordinary GPIO2 commands, which did not constitute a visible blink test after the hardware was identified as T-CAN485: GPIO2 is the SD MISO signal and the onboard light is an addressable WS2812 on GPIO4.

The corrected self-test reports the same managed markers followed by live resource measurements and an alternating LED command:

```text
C~ ESP-IDF hardware test
virtual: 42
boxed: 7
exception: caught on ESP32
free heap: 298172
minimum free heap: 298172
stack high water: 7744
tick: 1
CTILDE_ESP_OK
ws2812: on
ws2812: off
```

The 2026-08-18 corrected run showed repeated 500 ms UART transitions without a watchdog reset, and the onboard RGB LED was confirmed to blink green in step with them. The failure image reported `C~ runtime error CTN0001 at RuntimeFailure.ct:17`, called `abort()`, and rebooted with `SW_CPU_RESET`; `Program.ct` was then reflashed and its UART output rechecked as the final board state.

The initial runtime uses program-lifetime allocation and one C~ execution task. The WS2812 API owns one native strip handle for firmware lifetime; identical configuration is idempotent and conflicting reconfiguration fails. Native callbacks, multiple C~ tasks or strips, interrupt execution, and automatic managed reclamation are not supported. Permanent loops must keep allocation bounded and call a yielding API such as `DelayMilliseconds`; a busy loop can trigger the ESP-IDF task watchdog.
