[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$example = Join-Path $repository 'examples\QemuFreestanding'
$manifest = Join-Path $example 'ctilde.json'
$image = Join-Path $example 'build\kernel.elf'

if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
    throw 'The QEMU freestanding smoke test requires WSL.'
}

$missingTool = & wsl --exec sh -lc 'for tool in gcc qemu-system-x86_64 readelf nm timeout; do command -v "$tool" >/dev/null 2>&1 || { printf "%s" "$tool"; exit 1; }; done'
if ($LASTEXITCODE -ne 0) {
    throw "Required WSL tool '$missingTool' was not found. Install qemu-system-x86 and gcc-multilib."
}

& wsl --exec sh -lc 'printf "#include <inttypes.h>\n#include <stdint.h>\nint probe(uintptr_t value) { return sizeof(void*) == 4 && value != 0; }\n" | gcc -m32 -std=gnu2x -ffreestanding -fno-builtin -fno-stack-protector -fno-pie -Wall -Wextra -Werror -fsyntax-only -x c -' 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'WSL GCC cannot compile 32-bit freestanding headers. Install gcc-multilib (which supplies the required i386 development headers).'
}

& dotnet run --project (Join-Path $repository 'CTilde.Cli') -c Release --no-launch-profile -- --project $manifest --build
if ($LASTEXITCODE -ne 0) {
    throw "The QEMU freestanding build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $image)) {
    throw "The QEMU freestanding build did not produce '$image'."
}

$linuxImage = (& wsl --exec wslpath -a -u $image).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($linuxImage)) {
    throw "Could not translate '$image' to a WSL path."
}

$header = & wsl --exec readelf -h $linuxImage
if ($LASTEXITCODE -ne 0) {
    throw 'readelf could not inspect the QEMU kernel image.'
}
$headerText = $header -join "`n"
if ($headerText -notmatch 'Class:\s+ELF32' -or $headerText -notmatch 'Machine:\s+Intel 80386') {
    throw 'The QEMU kernel is not an ELF32 Intel 80386 image.'
}

$symbols = & wsl --exec nm -g $linuxImage
if ($LASTEXITCODE -ne 0) {
    throw 'nm could not inspect the QEMU kernel symbols.'
}
$symbolsText = $symbols -join "`n"
foreach ($symbol in @('_start', 'ct_runtime_initialize', 'kernel_main', 'ct_runtime_shutdown')) {
    if ($symbolsText -notmatch "(?m)\s[Tt]\s+$([Regex]::Escape($symbol))\s*$") {
        throw "The QEMU kernel does not define required text symbol '$symbol'."
    }
}

$undefined = & wsl --exec nm -u $linuxImage
if ($LASTEXITCODE -ne 0) {
    throw 'nm could not inspect undefined QEMU kernel symbols.'
}
if (-not [string]::IsNullOrWhiteSpace($undefined -join "`n")) {
    throw "The QEMU kernel contains undefined symbols:`n$($undefined -join "`n")"
}

$qemuOutput = & wsl --exec timeout --foreground "$($TimeoutSeconds)s" qemu-system-x86_64 `
    -accel tcg `
    -machine pc `
    -m 16M `
    -smp 1 `
    -nodefaults `
    -no-reboot `
    -display none `
    -monitor none `
    -serial none `
    -parallel none `
    -kernel $linuxImage `
    -chardev stdio,id=debugcon,signal=off `
    -device isa-debugcon,iobase=0xe9,chardev=debugcon `
    -device isa-debug-exit,iobase=0xf4,iosize=0x4 2>&1
$qemuExitCode = $LASTEXITCODE
$qemuText = ($qemuOutput | ForEach-Object { $_.ToString() }) -join "`n"

if ($qemuExitCode -eq 124) {
    throw "QEMU did not terminate within $TimeoutSeconds seconds. Output:`n$qemuText"
}
if ($qemuExitCode -ne 1) {
    throw "QEMU returned exit code $qemuExitCode instead of the expected debug-exit status 1. Output:`n$qemuText"
}
if ($qemuText -notmatch '(?m)^CTILDE_QEMU_OK\r?$') {
    throw "QEMU output did not contain the exact CTILDE_QEMU_OK marker. Output:`n$qemuText"
}

Write-Output 'QEMU freestanding smoke test passed.'
