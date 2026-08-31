# Cosmopolitan APE example

This project uses the target introduced in Draft 0.24 to build one x86-64 Actually Portable Executable with the Cosmopolitan Libc toolchain. The same `Cosmopolitan.com` payload is intended to run on supported x86-64 Linux, macOS, Windows, FreeBSD, OpenBSD, and NetBSD systems.

The example exercises the ordinary C~ hosted runtime rather than the freestanding profile. It uses managed strings and arrays, ARC, a worker thread and mutex, exceptions, console output, file output, and the exact Draft 0.40 deterministic `Random` sequence.

Install the official Cosmopolitan toolchain outside this repository. On Windows, point C~ at its single-architecture wrapper through WSL:

```powershell
$env:CTILDE_COSMOCC = 'wsl:/path/to/cosmocc/bin/x86_64-unknown-cosmo-cc'
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --build
.\build\Cosmopolitan.com
```

Use `--run` instead of `--build` to rebuild and launch the APE through the manifest-driven runner.

The archive contains symbolic links and must be extracted by a ZIP tool that preserves them. Cosmopolitan's compiler subprocesses are APE programs themselves. On WSL/Linux, follow the upstream toolchain instructions to install `bin/ape-x86_64.elf` as `/usr/bin/ape` and register the APE binfmt handlers, or provide an equivalent working APE execution environment. C~ validates the wrapper but does not download the toolchain or change system loader policy.

The build retains `build/Cosmopolitan.com.dbg`, the ELF/DWARF carrier used for symbol inspection and native debugging. `build/Cosmopolitan.com` is the unwrapped APE payload. Successful execution prints the worker result and creates `cosmopolitan-output.txt` in the current directory.

Draft 0.40 accepts only explicit `architecture: "x64"` and the `x86_64-unknown-cosmo-cc` wrapper. Arm64 and fat x64+Arm64 APEs require later compiler work described in the repository Cosmopolitan design document.
