# Hosted native module loading

This example loads an ordinary native plug-in through hosted C~ `[NativeImport]`. The compiler maps the logical name `ctilde_example_plugin` to `ctilde_example_plugin.dll` on Windows or `libctilde_example_plugin.so` on Linux, loads it during runtime startup, resolves the three typed C ABI symbols, and unloads it after C~ static finalization.

`Program.ct` verifies a version function, a stateless function, and state retained inside the loaded module. `native/plugin.c` is deliberately small so the complete ABI boundary is visible. The example runner builds both the plug-in and the C~ application, then executes the result:

```powershell
./examples/HostedNativeImport/Test-HostedNativeImport.ps1
```

The default matrix uses MSVC, WSL GCC, and WSL Clang. Use `-Compilers msvc` for the Windows-only path.

This is native C ABI loading, not shared-runtime managed C~ module loading. Managed Module ABI 4 currently targets ESP-IDF. The hosted target does not yet emit a C~ shared library, consume `.ctmeta.json` references, isolate mutable static state per process, or load managed C~ types through a shared runtime.
