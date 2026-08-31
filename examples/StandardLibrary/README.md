# Standard-library tour

This hosted x64 example demonstrates the Draft 0.40 and Draft 0.41 library foundations in small, responsibility-based source files:

- ordinal UTF-8 string search, replacement, splitting, segments, joining, and ASCII helpers;
- `StringBuilder`, composite formatting, numeric format specifications, and custom `IFormattable` values;
- explicit managed/native UTF-8 copying, bounded pointer conversion, exact buffers with embedded NUL bytes, and borrowed C strings;
- `TimeSpan`, `Stopwatch`, deterministic `Random`, and the expanded scalar `Math` surface;
- `Thread`, `SpinWait`, and a non-copyable `SpinLock` protecting shared state.

Build and run it from this directory:

```powershell
dotnet ..\..\CTilde.Cli\bin\Release\net10.0\ctilde.dll --project .\ctilde.json --run
```

Indexes and lengths in the string examples are UTF-8 byte offsets. Native conversion remains explicit: `Utf8` copies validated bytes into managed storage, while `NativeUtf8String.Borrow` supplies a temporary zero-allocation C string for synchronous native calls.
