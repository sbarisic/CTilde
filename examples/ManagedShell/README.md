# ManagedShell

ManagedShell is the Draft 0.45 ESP-IDF example for Runtime ABI 18 and Managed Module ABI 1. The firmware mounts LittleFS at `/storage`, initializes the shared C~ runtime component, and accepts `ls`, `modules`, `ps`, `exec`, `kill`, `wait`, `send`, `free`, and `help` commands. Modules are resolved below `/storage/modules`; absolute paths outside that root and traversal segments are rejected.

`Modules/Hello` builds `examples.hello.ctm` plus deterministic `examples.hello.ctmeta.json`. It demonstrates a managed application entry point, copied arguments, process-local mutable statics, shared-runtime console output, heap accounting, automatic exit, reverse finalization, unloading, and reload.

Build both images with:

```powershell
.\examples\ManagedShell\Test-ManagedShell.ps1 -BuildOnly
```

Flash and monitor with an active ESP-IDF 6 environment:

```powershell
idf.py -C examples/ManagedShell -p COM4 flash monitor
```

The connected ESP32-D0WDQ6-V3 acceptance on 2026-09-02 completed a 16-cycle warm-up followed by 100 load/run/exit/unload cycles. Every application returned its argument count, every reload observed independent static state, the module registry returned to zero, task counts returned to zero, and free heap remained exactly 167,000 bytes across the measured 100-cycle interval.

Managed modules are trusted native code. There is no MMU boundary: unsafe pointers, ESP-IDF calls, MMIO, callbacks, interrupts, or native resources that escape runtime accounting can corrupt the firmware and make forced reclamation unsafe.
