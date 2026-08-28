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
$nativeAssembly = Join-Path $example 'native\start.S'

if (Test-Path -LiteralPath $nativeAssembly) {
    throw "The QEMU example must not contain the native assembly source '$nativeAssembly'."
}
$manifestText = Get-Content -LiteralPath $manifest -Raw
if ($manifestText -match '"nativeSources"') {
    throw 'The QEMU manifest must not declare native assembly sources.'
}

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

$sections = & wsl --exec readelf -SW $linuxImage
if ($LASTEXITCODE -ne 0) {
    throw 'readelf could not inspect the QEMU kernel sections.'
}
$sectionText = $sections -join "`n"
$multibootMatch = [Regex]::Match($sectionText, '(?m)^\s*\[\s*\d+\]\s+\.multiboot\s+PROGBITS\s+[0-9a-fA-F]+\s+([0-9a-fA-F]+)\s+([0-9a-fA-F]+)')
if (-not $multibootMatch.Success) {
    throw 'The QEMU kernel does not contain a PROGBITS .multiboot section.'
}
$multibootOffset = [Convert]::ToUInt64($multibootMatch.Groups[1].Value, 16)
if ($multibootOffset -ge 0x2000) {
    throw "The .multiboot section begins at file offset 0x$($multibootOffset.ToString('X')), outside the first 8 KiB."
}
$multibootHex = & wsl --exec readelf -x .multiboot $linuxImage
if ($LASTEXITCODE -ne 0) {
    throw 'readelf could not inspect the Multiboot header bytes.'
}
$multibootHexText = (($multibootHex -join ' ') -replace '\s+', '').ToLowerInvariant()
if ($multibootHexText -notmatch '02b0ad1b03000000fb4f52e4') {
    throw "The .multiboot section does not contain the expected magic, flags, and checksum bytes.`n$($multibootHex -join "`n")"
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
if ($qemuText.Trim() -ne 'CTILDE_QEMU_OK') {
    throw "QEMU output was not exactly the CTILDE_QEMU_OK marker. Output:`n$qemuText"
}

Write-Output 'QEMU freestanding smoke test passed.'
