# Standard-library tour

This hosted x64 project runs on the current Draft 0.45 compiler and demonstrates the library foundations introduced from Draft 0.40 through Draft 0.42 in small, responsibility-based source files:

- ordinal UTF-8 string search, replacement, splitting, segments, joining, and ASCII helpers;
- `StringBuilder`, composite formatting, numeric format specifications, and custom `IFormattable` values;
- explicit managed/native UTF-8 copying, bounded pointer conversion, exact buffers with embedded NUL bytes, and borrowed C strings;
- `TimeSpan`, `Stopwatch`, deterministic `Random`, and the expanded scalar `Math` surface;
- `Thread`, `SpinWait`, and a non-copyable `SpinLock` protecting shared state.
- compiler-backed Boolean, integer, floating-point, and enum `Parse`/`TryParse` APIs;
- Unicode console output, path construction, recursive directories, strict UTF-8 text round-tripping, seeking, and file metadata.

Build and run it from this directory:

```powershell
dotnet ..\..\CTilde.Cli\bin\Release\net10.0\ctilde.dll --project .\ctilde.json --run
```

Indexes and lengths in the string examples are UTF-8 byte offsets. Native conversion remains explicit: `Utf8` copies validated bytes into managed storage, while `NativeUtf8String.Borrow` supplies a temporary zero-allocation C string for synchronous native calls.

The I/O tour creates `ctilde-stdlib-tour` beneath the current directory, reads its file through a seekable `FileStream`, prints metadata, then removes the directory before returning.

Generic value helpers, mutable collections, versioned enumerators, vectors, matrices, and quaternions have their own focused [CollectionsAndGeometry example](../CollectionsAndGeometry/README.md), keeping this I/O-oriented tour readable.
