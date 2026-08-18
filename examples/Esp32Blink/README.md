# ESP32 blink hardware test

This ESP-IDF project compiles one C~ program into `main/generated/ctilde_program.c`, links the generated translation unit with a fixed-width ESP shim, and leaves chip selection, linking, flashing, and monitoring to ESP-IDF.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

`Program.ct` exercises construction, virtual dispatch, boxing, strings, exceptions, heap and stack diagnostics, FreeRTOS delay, and GPIO2. Change `BlinkPin` when the board LED uses another GPIO. The permanent loop performs no C~ allocations.

To verify the fatal runtime boundary:

```powershell
.\Build.ps1 -Target esp32 -Port COM4 -Source RuntimeFailure.ct -Flash -Monitor
```

The monitor must show `CTILDE_ESP_FAILURE_TEST` followed by runtime code `CTN0001`. Reflash `Program.ct` afterward.

The initial runtime uses program-lifetime allocation and one C~ execution task. Native callbacks, multiple C~ tasks, interrupt execution, and automatic reclamation are not supported.
