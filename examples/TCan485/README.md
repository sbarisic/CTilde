# T-CAN485 WS2812 hardware test

This ESP-IDF project emits the Draft 0.15 ABI 15 modular C bundle under `main/generated`: shared runtime headers, one runtime implementation, reachable namespace sources, the entry/module-lifecycle source, a symbol map, and CMake source fragments. `Bindings/esp-idf.bindings.json` also generates tracked declarations and adapters for the native timer, hardware RNG, GPIO, Wi-Fi station, network-interface, event-loop, HTTPS client, and certificate-bundle APIs. The component includes both fragments, and the CLI's native build stage invokes `idf.py build`. ESP-IDF still owns chip selection, component resolution, incremental compilation, linking, flashing, and monitoring. The native shim remains intact. The project targets the classic ESP32 T-CAN485: GPIO4 carries the onboard WS2812 data signal, while GPIO2 is reserved for the microSD MISO signal and is never driven by this test.

From PowerShell:

```powershell
.\Build.ps1 -Target esp32
.\Build.ps1 -Target esp32 -Port COM4 -Flash -Monitor
```

The repeatable physical acceptance runner uses the connected board defaults and restores the ordinary Release image on every exit path:

```powershell
.\Test\Test-Esp32Hardware.ps1
.\Test\Test-Esp32Hardware.ps1 -AutomatedOnly
.\Test\Test-Esp32Hardware.ps1 -AutomatedOnly -AcceptMemoryBaseline
```

Run it from the repository root. The normal command prompts once to confirm visible WS2812 activity. `-AutomatedOnly` performs all machine-verifiable checks but leaves that release gate pending. `-AcceptMemoryBaseline` is an explicit reviewer-controlled update; ordinary runs only validate the tracked versioned baseline and fail when the ESP-IDF or compiler version differs. Each run writes an ignored JSON report, raw UART bytes, and transcripts under `artifacts/esp32-hardware`.

`Program.ct` exercises generic interface dispatch, scalar atomics, volatile publication, source-created threads, recursive `lock`, scoped UTF-8 input, a move-only opaque resource released by `defer`, exact `EspError` naming, generated exports, an instance delegate through a callback/context adapter, attached native FreeRTOS tasks, per-task exceptions and cleanup, generated timer/random/buffer bindings, construction, boxing, strings, and ARC heap recovery. It prints `generated bindings: ok` after the generated native calls succeed. The checked component manifest pins Espressif `led_strip` 3.0.3 and uses its non-DMA RMT backend. All allocation-producing managed self-tests return before measurement and the permanent allocation-free loop.

## Default Wi-Fi HTTPS fetch

The normal firmware calls the Wi-Fi/HTTPS demo after its managed self-tests. Configure `Ssid` and `Password` in `WifiSettings.ct` and optionally change the HTTPS URL. A clean checkout uses empty credential placeholders; an empty SSID prints `wifi: not configured`, skips native network initialization, and continues to the permanent WS2812 loop. Do not commit credentials or a locally edited settings file.

When enabled, `WifiDemo.ct` starts an `IWebsiteFetcher` implementation on a C~ `Thread`. The main task keeps the WS2812 animated while the worker polls for an IPv4 address and downloads the response. The worker publishes its report through a `Mutex`, a volatile completion flag, and atomic state/counter values. It uses generated NVS, owned native handles, and `defer` to close and destroy HTTP, Wi-Fi, event-loop, network, and storage resources on success and failure. The response is limited to 64 KiB; only its first 256 bytes are retained, non-printable bytes are displayed as `.`, and the complete body receives a deterministic FNV-1a hash.

A successful fetch prints the HTTP status, declared and received lengths, hash, elapsed microseconds, sanitized preview, and `generated wifi/http bindings: ok`. Connection, TLS, HTTP, response-limit, and C~ failures print `wifi/http error: ...`, briefly set the LED red, and then return to the normal firmware loop. The generated declarations are `[NoAlloc]` only with respect to the C~ heap; ESP-IDF Wi-Fi and TLS use native heap internally. HTTPS and the full certificate bundle intentionally increase flash and peak heap requirements.

The project uses one `0x3f0000`-byte factory application slot, consuming the remainder of the board's 4 MiB flash after the partition table and the required NVS and PHY data partitions. There are no OTA application slots or filesystem partitions. This maximizes firmware space but means updates must be installed over the serial flashing connection rather than OTA.

The opt-in hardware check takes credentials from the environment, temporarily edits the settings, validates a 2xx response and bounds retained first-use native state to 8 KiB, then restores and flashes the original settings when it changed them. If the source already contains the requested settings, the validated firmware remains flashed without a redundant rebuild:

```powershell
$env:CTILDE_TEST_WIFI_SSID = "your-network"
$env:CTILDE_TEST_WIFI_PASSWORD = "your-password"
.\Test\Test-Esp32Wifi.ps1
```

Run that command from the repository root. The test does not write credentials to its artifacts, but command-line `-Ssid` and `-Password` values can be visible to local process-inspection tools; environment variables avoid that exposure.

Refresh or verify the tracked binding outputs with the matching installed ESP-IDF and Espressif Clang:

```powershell
ctilde --project .\ctilde.json --generate-bindings --idf-path C:\esp\v6.0.2\esp-idf --esp-clang C:\Espressif\tools\esp-clang\esp-20.1.1_20250829\esp-clang\bin\clang.exe
ctilde --project .\ctilde.json --verify-bindings --idf-path C:\esp\v6.0.2\esp-idf --esp-clang C:\Espressif\tools\esp-clang\esp-20.1.1_20250829\esp-clang\bin\clang.exe
```

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

Set `ctilde.debugger.serialPort` to the board port. This example uses 460800 baud for both the ESP-IDF console and the debugger; any external serial monitor must use the same rate. Debug Launch validates this configuration, builds and flashes a version-3 instrumented image, then connects during its 15-second pre-initialization gate. The checked-in Launch configuration uses guarded ARC diagnostics. Source, function, log, and exception breakpoints use logical probes and do not consume the ESP32's two instruction-breakpoint slots; hardware data watchpoints remain limited by the target. Debug Attach reuses matching ELF and debug-map artifacts. The adapter keeps the serial port in a small ESP-IDF-Python bridge for the complete session. The runtime stub therefore consumes UART input during debugging, so do not run an interactive monitor on the same port at the same time. C~ console writes made after attachment appear in VS Code's Debug Console. Pressing Stop clears logical and hardware debugger state and continues the current firmware; after the session ends, output returns to the ordinary UART console.

## Hardware evidence

Draft 0.15 ABI 15 completed the full physical acceptance on 2026-08-23 using the ESP32-D0WDQ6-V3 revision 3.1 board on COM4 at 460800 baud, ESP-IDF 6.0.2, Xtensa GCC 15.2.0, and ESP-GDB 17.1. The ordinary Release image was 171,136 bytes and measured 171,013 image bytes, 78,554 bytes flash code, 36,608 bytes flash data, 45,391 bytes IRAM, and 14,652 bytes static DRAM. It reported 297,036 bytes free heap, a 284,304-byte minimum, and 6,736 bytes of main-task stack headroom. Every ABI marker passed, including `draft15 concurrency: ok`, and 25 alternating WS2812 transitions completed. The operator confirmed that the onboard GPIO4 LED visibly alternated.

That physical result predates the Wi-Fi/HTTPS worker. The normal firmware now includes and invokes that worker, while the clean-checkout image skips native network initialization because its tracked SSID is empty. ESP-IDF Wi-Fi, TLS, the HTTP client, and the full certificate bundle remain linked in both cases. The deliberately rebaselined ESP32 cross-build is 191,616 binary bytes and 191,497 image bytes: 87,174 bytes flash code, 44,776 bytes flash data, 48,463 bytes IRAM, and 15,444 bytes static DRAM. The ESP32-C3 cross-build is 208,192 binary bytes and 207,820 image bytes: 113,112 bytes flash code, 43,860 bytes flash data, and 56,824 bytes static DRAM. These are the tracked ESP-IDF 6.0.2/GCC 15.2.0 limits; a live-network acceptance run still requires local credentials because credentials are never tracked.

An automated connected-board pass of that new network-disabled image completed on 2026-08-23. It printed `wifi: not configured`, completed all ABI markers and 25 alternating WS2812 transitions, and measured 291,696 bytes free heap, a 278,964-byte minimum, and 6,708 bytes of main-task stack headroom. The allocation-failure, exact-console, fatal-reset, debugger-v3, detach, and startup-timeout checks also passed, and the runner restored the 191,616-byte Release image. The ignored report is `artifacts/esp32-hardware/20260823-211534.json`. Because the run used `-AutomatedOnly`, the previous visual LED confirmation remains the latest human observation; no live HTTPS claim is made without local credentials.

A configured live-network run then passed on the same hardware. The current 1,010,160-byte configured default image associated, obtained an IPv4 address, validated the TLS certificate, returned HTTP 200 from `https://example.com/`, read 559 bytes, and produced FNV-1a hash `1710764169`. ESP-IDF retained 7,280 bytes of bounded first-use Wi-Fi/TLS state after its cleanup sequence; ARC recovery and `CTILDE_ESP_OK` still passed and the LED loop continued. Credentials are intentionally omitted from this evidence and must not be committed.

The ABI 15 layout fixture measured 16-byte objects, 20-byte string and array headers, 40-byte descriptors, and 12-byte probe vtables with totals of 720 descriptor bytes, 204 vtable bytes, and 496 literal-object bytes. Injected class, array, box, and dynamic-string allocation failures were caught, later allocations succeeded, and live ownership returned to zero. The exact 154-byte COM4 CRLF/UTF-8 frame passed. The isolated fatal image emitted `CTN0001`, aborted, and rebooted.

Guarded debugger metadata v3 passed six simultaneous logical breakpoints, startup and first-statement stops, C~ Step Over/Into/Out, five FreeRTOS tasks, caught-exception translation, lexical locals, live ARC/canary inspection, a reference-count hardware watchpoint, and console forwarding. After the managed tests, 3,364 allocations matched 3,364 final releases with zero live objects. Disconnect cleared debugger state and continued four LED transitions without a reset. A separate boot without a debugger passed the startup timeout after 14.23 seconds. The runner restored the ordinary Release firmware. The ignored report is `artifacts/esp32-hardware/20260823-030442.json`.

The following paragraphs retain older measurements for historical comparison.

The draft 0.14 ABI 14 sources and inline-assembly fixture pass strict syntax checks and complete modular links for both ESP32/Xtensa and ESP32-C3/RISC-V with ESP-IDF 6.0.2. On 2026-08-22, automated physical acceptance ran on the ESP32-D0WDQ6-V3 revision 3.1 T-CAN485 at `COM4` and 460800 baud using Xtensa GCC 15.2.0 and ESP-GDB 17.1. The ordinary 168,480-byte Release binary passed every runtime marker, ARC heap recovery, and 25 alternating WS2812 UART transitions. It reported 295,204 bytes free, a 284,740-byte minimum, and 6,744 bytes of main-task stack headroom. The isolated fatal image emitted `CTN0001`, called `abort()`, and rebooted. The acceptance runner restored the ordinary Release image afterward.

The same run exercised debugger metadata v2 with guarded memory diagnostics. Six logical breakpoints were active at once; startup and first-statement stops, C~ Step Over/Into/Out, five FreeRTOS tasks, caught-exception translation, lexical locals, live ARC objects, intact canaries, a reference-count hardware watchpoint, console forwarding, and zero live objects after the managed self-tests all passed. Disconnect cleared debugger state and continued the firmware without a reset, and the passive UART observer saw four subsequent alternating WS2812 messages. A separate no-debugger boot passed the 15-second startup gate after 14.16 seconds. The ignored machine-readable report is `artifacts/esp32-hardware/20260822-155832.json`.

After that acceptance runner restored the ordinary Release image, the operator confirmed that the onboard GPIO4 WS2812 visibly alternated. This remains the complete historical Draft 0.14 ABI 14 result that preceded the Draft 0.15 run above.

The immediate memory acceptance then measured the pruned ordinary ESP32 Release image at 164,816 binary bytes and 164,693 image bytes: 73,854 bytes of flash code, 35,184 bytes of flash data, 45,211 bytes of IRAM, and 14,580 bytes of static DRAM. It reported 297,108 bytes free, a 288,536-byte minimum, and 6,744 bytes of main-task stack headroom. The ESP32-C3 cross-build measured 170,784 binary bytes and 170,484 image bytes: 91,654 bytes of flash code, 32,684 bytes of flash data, and 51,746 bytes of static DRAM. The tracked balanced budgets allow at most the greater of 2 percent or 1 KiB growth for binary/image/flash sections, the greater of 2 percent or 512 bytes for IRAM/DRAM, at most 4 KiB heap regression, and at most 512 bytes stack regression with a hard 1 KiB floor.

The same historical connected-board fixture caught injected allocation failure for class, array, box, and dynamic-string allocation, disabled injection, allocated successfully afterward, and returned ARC live-object/allocation counts to their starting values. ELF inspection confirmed retained descriptors, vtables, and literal objects reside in read-only flash-backed sections. Its ignored automated report is `artifacts/esp32-hardware/20260822-234147.json`.

COM4 was verified as the T-CAN485 onboard `VID_1A86&PID_55D4` USB-to-UART bridge rather than Bluetooth or an unrelated serial device. A raw 460800-baud capture matched the complete CRLF wire frame byte-for-byte, including `čćž €`, signed and unsigned integers, `1.5`, and `True False`, under strict UTF-8 decoding. This validates UART output through the board's direct USB cable. It does not validate native USB CDC or ESP32-C3 USB Serial/JTAG; that work remains deferred until suitable hardware is available.

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

Draft 0.15 uses atomic ARC and per-task exception, cleanup, diagnostic-origin, and release state; cycles leak, and cleanup is not promised after a panic, fatal failure, or reset. The example reserves FreeRTOS application TLS slot 0 through `sdkconfig.defaults`. Native buffers and UTF-8 views remain scoped synchronous values and cannot be retained by the shim. Opaque ownership is lexical; long-lived resource fields are not supported. The WS2812 API owns one native strip handle for firmware lifetime. Native-created tasks must call `ct_thread_attach` and `ct_thread_detach`; source-created `Thread` workers attach automatically. Retained callbacks and interrupt entry remain unsupported. Permanent loops must keep live ownership bounded and call a yielding API such as `DelayMilliseconds`; a busy loop can trigger the ESP-IDF task watchdog.
