# Hosted console and file I/O

This project reads one UTF-8 line, writes it to `ctilde-hosted-π.txt`, closes the owned file handle through `defer`, reopens the file, and prints its bytes. The generated program is an ordinary hosted console executable.

Build the native executable from the repository root:

```powershell
dotnet run --project .\CTilde.Cli -c Release --no-launch-profile -- --project .\examples\HostedIo\ctilde.json --build
.\examples\HostedIo\build\HostedIo.exe
```

Use `--configuration release` for optimized output or `--compiler clang`, `gcc`, or `msvc` to override discovery. Emit-only and manual native compilation remain available:

```powershell
dotnet run --project .\CTilde.Cli -c Release --no-launch-profile -- --project .\examples\HostedIo\ctilde.json -o .\examples\HostedIo\program.c
cl /nologo /std:clatest /O2 /W4 /WX /Fe:.\examples\HostedIo\program.exe .\examples\HostedIo\program.c
.\examples\HostedIo\program.exe
```

Expected interaction:

```text
Enter text: hello hosted
Saved and reloaded: hello hosted
HOSTED_IO_OK
```

`FileHandle` is move-only. Every successfully opened handle must be returned, transferred to native code, consumed by `File.Close`, or reserved immediately with `defer File.Close(file);`. File data is binary; the string overload writes exact UTF-8 bytes without adding a newline. `File.Read` uses a checked `NativeBuffer<byte>` and therefore requires an unsafe context.

The example leaves `ctilde-hosted-π.txt` in its working directory so its output can be inspected. Delete it after the run if it is no longer needed.
