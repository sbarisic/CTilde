# Freestanding kernel image

This example builds a GNU/ELF x64 image without a C runtime or hosted startup. The default manifest supplies `_start` as a narrow naked C~ export. The second manifest supplies the same startup sequence in native assembly. Both initialize the managed runtime explicitly, call an exported managed routine, shut the runtime down, and exit through a Linux syscall.

From this directory on Windows with WSL:

```powershell
dotnet run --project ..\..\CTilde.Cli -- --project .\ctilde.json --build
wsl ./build/kernel.elf
$LASTEXITCODE

dotnet run --project ..\..\CTilde.Cli -- --project .\ctilde-native-start.json --build
wsl ./build/kernel-native-start.elf
$LASTEXITCODE
```

The expected exit code is `0`. The fixed arena is an example allocator, not a production allocator.
