[CmdletBinding()]
param(
    [string]$Compiler = $env:CTILDE_COSMOCC
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$example = Join-Path $repository 'examples\Cosmopolitan'
$manifest = Join-Path $example 'ctilde.json'
$image = Join-Path $example 'build\Cosmopolitan.com'
$carrier = "$image.dbg"

if ([string]::IsNullOrWhiteSpace($Compiler)) {
    throw 'Set CTILDE_COSMOCC to wsl:/path/to/x86_64-unknown-cosmo-cc or pass -Compiler.'
}
if (-not $Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Windows acceptance runner currently requires a WSL-hosted Cosmopolitan wrapper.'
}

$previousCompiler = $env:CTILDE_COSMOCC
try {
    $env:CTILDE_COSMOCC = $Compiler
    & dotnet run --project (Join-Path $repository 'CTilde.Cli') -c Release --no-launch-profile -- --project $manifest --build
    if ($LASTEXITCODE -ne 0) { throw "Cosmopolitan build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $image) -or -not (Test-Path -LiteralPath $carrier)) {
        throw 'The build did not produce both the APE image and ELF/DWARF carrier.'
    }

    Push-Location $example
    try {
        $windowsOutput = & $image
        if ($LASTEXITCODE -ne 0) { throw "The APE failed on Windows with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }

    $linuxImage = (& wsl --exec wslpath -a -u $image).Trim()
    $linuxCarrier = (& wsl --exec wslpath -a -u $carrier).Trim()
    $linuxExample = (& wsl --exec wslpath -a -u $example).Trim()
    $linuxOutput = & wsl --exec sh -lc "cd '$linuxExample' && '$linuxImage'"
    if ($LASTEXITCODE -ne 0) { throw "The APE failed under WSL with exit code $LASTEXITCODE." }
    $inspection = & wsl --exec sh -lc "readelf -h '$linuxCarrier'; nm -g '$linuxCarrier'"
    if ($LASTEXITCODE -ne 0) { throw 'ELF carrier inspection failed.' }
    $inspectionText = $inspection -join "`n"

    $expected = @('C~ is running as a Cosmopolitan APE.', 'Worker value: 42')
    foreach ($line in $expected) {
        if ($windowsOutput -notcontains $line -or $linuxOutput -notcontains $line) {
            throw "The Windows or WSL transcript omitted '$line'."
        }
    }
    if ($inspectionText -notmatch 'Machine:\s+Advanced Micro Devices X86-64' -or
        $inspectionText -notmatch '(?m)\sT main$' -or $inspectionText -notmatch '(?m)\sT ct_runtime_initialize$') {
        throw 'The retained carrier does not contain the expected x86-64 header and C~ runtime symbols.'
    }

    Write-Output 'Cosmopolitan Draft 0.24 acceptance passed on Windows and WSL.'
}
finally {
    $env:CTILDE_COSMOCC = $previousCompiler
}
