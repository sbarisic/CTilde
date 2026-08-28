# QEMU freestanding kernel

This example builds a 32-bit x86 Multiboot kernel and boots it directly with QEMU. Unlike the Linux-loaded [freestanding example](../Freestanding/README.md), this image uses no Linux process loader or syscall ABI. It has no libc, CRT, managed heap, console, filesystem, threads, exceptions, or operating-system services.

The C~ source owns the complete image: `[ConstInit]` emits the Multiboot header as immutable native data, assembly functions implement the debug ports, and a naked assembly function provides `_start`. There are no native assembly source files. Startup initializes the C~ runtime, calls `kernel_main`, shuts the runtime down, and writes the returned status to QEMU's `isa-debug-exit` device at port `0xF4`. The kernel writes its marker by looping over an immutable string literal, which is emitted directly into the image and performs no managed heap allocation.

## Prerequisites

The checked workflow uses GCC and QEMU under WSL:

```bash
sudo apt update
sudo apt install gcc-multilib qemu-system-x86
```

`gcc-multilib` is required because the generated freestanding C includes the compiler's standard integer headers while targeting 32-bit x86.

## Build and run

From the repository root in PowerShell, run the standalone smoke test:

```powershell
.\Test\Test-QemuFreestanding.ps1
```

The script checks the toolchain, builds the image, inspects its ELF class, architecture, required symbols, undefined symbols, Multiboot placement, and exact header bytes, then boots it headlessly under QEMU with a ten-second timeout. QEMU returns process status `1` for a successful guest status of zero because `isa-debug-exit` encodes the result as `(value << 1) | 1`; the script handles that convention.

To build without running QEMU:

```powershell
dotnet run --project .\CTilde.Cli -c Release --no-launch-profile -- --project .\examples\QemuFreestanding\ctilde.json --build
```

To run the resulting image manually:

```powershell
$image = (& wsl --exec wslpath -a -u (Resolve-Path .\examples\QemuFreestanding\build\kernel.elf)).Trim()
& wsl --exec qemu-system-x86_64 `
    -accel tcg -machine pc -m 16M -smp 1 -nodefaults -no-reboot `
    -display none -monitor none -serial none -parallel none `
    -kernel $image `
    -chardev stdio,id=debugcon,signal=off `
    -device isa-debugcon,iobase=0xe9,chardev=debugcon `
    -device isa-debug-exit,iobase=0xf4,iosize=0x4
```

The expected output is:

```text
CTILDE_QEMU_OK
```

This first kernel intentionally has one CPU and no VGA, UART, interrupts, paging, Multiboot-information parsing, allocator, GRUB ISO, or physical-hardware portability contract.
