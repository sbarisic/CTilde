[CmdletBinding()]
param(
    [string]$CompilerDll = '',
    [switch]$IncludeEspIdfBuild,
    [string]$IdfPath = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($CompilerDll)) {
    & dotnet build (Join-Path $repositoryRoot 'CTilde.Cli/CTilde.Cli.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The Release compiler build failed.' }
    $CompilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
}
$CompilerDll = [IO.Path]::GetFullPath($CompilerDll)
if (-not (Test-Path -LiteralPath $CompilerDll)) { throw "The compiler DLL was not found: $CompilerDll" }

function Invoke-Ctilde([string]$Manifest, [string[]]$Arguments) {
    $resolvedManifest = Join-Path $repositoryRoot $Manifest
    $output = & dotnet $CompilerDll --project $resolvedManifest @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "C~ failed for '$Manifest' with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }
    return @($output | ForEach-Object { $_.ToString().TrimEnd() })
}

function Invoke-HostedExample([string]$Manifest, [string[]]$ExpectedLines) {
    $output = Invoke-Ctilde $Manifest @('--run', '--configuration', 'release', '--verbosity', 'quiet')
    $expectedText = $ExpectedLines -join "`n"
    $actualText = $output -join "`n"
    if ($actualText -ne $expectedText) {
        throw "Hosted example '$Manifest' output did not match.`nExpected:`n$expectedText`nActual:`n$actualText"
    }
    Write-Host "PASS $Manifest"
}

Invoke-HostedExample 'examples/LanguageTour/ctilde.json' @(
    'Language and data-layout tour',
    'embedded bytes: 18',
    'embedded prefix: C~',
    'rune: λ',
    'utf8 roundtrip: True',
    'decoded bytes: 2',
    'encoded bytes: 2',
    'captureless lambda: 42',
    'captured lambda: 42',
    'operator sum: 42',
    'operator equality: True',
    'operator ordering: True',
    'interface name: temperature',
    'abstract value: 42',
    'type test: True',
    'newtype value: 42',
    'packed size: 6',
    'packed alignment: 2',
    'packed field offset: 2',
    'explicit low: 1',
    'explicit high: 2',
    'union float: 1',
    'do/no-recursion: 4',
    'x64 pointer width'
)
Invoke-HostedExample 'examples/CollectionsAndGeometry/ctilde.json' @(
    'generic values: True',
    'array algorithms: True',
    'collections: True',
    'version guard: True',
    'geometry: True'
)

foreach ($manifest in @(
    'examples/ManagedShell/ctilde.json',
    'examples/ManagedShell/Modules/Hello/ctilde.json',
    'examples/ManagedShell/Modules/Memory/ctilde.json',
    'examples/ManagedShell/Modules/TaskManager/ctilde.json',
    'examples/ManagedShell/Modules/Sd/ctilde.json'
)) {
    $null = Invoke-Ctilde $manifest @('--check', '--verbosity', 'quiet')
    Write-Host "PASS $manifest"
}

$buildEspIdf = $IncludeEspIdfBuild -or $env:CTILDE_EXAMPLE_ESP_IDF_BUILD -eq '1'
if ($buildEspIdf) {
    $resolvedIdfPath = if (-not [string]::IsNullOrWhiteSpace($IdfPath)) {
        $IdfPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:IDF_PATH)) {
        $env:IDF_PATH
    }
    else {
        'C:\esp\v6.0.2\esp-idf'
    }
    & (Join-Path $repositoryRoot 'examples/ManagedShell/Test-ManagedShell.ps1') `
        -BuildOnly -IdfPath $resolvedIdfPath
    if ($LASTEXITCODE -ne 0) { throw 'The ESP-IDF managed-module example build failed.' }
    foreach ($artifact in @(
        'examples/ManagedShell/Modules/Hello/build/managed-modules/examples.hello.ctm',
        'examples/ManagedShell/Modules/Hello/build/managed-modules/examples.hello.ctmeta.json',
        'examples/ManagedShell/Modules/Memory/build/managed-modules/memory.ctm',
        'examples/ManagedShell/Modules/Memory/build/managed-modules/memory.ctmeta.json',
        'examples/ManagedShell/Modules/TaskManager/build/managed-modules/taskmgr.ctm',
        'examples/ManagedShell/Modules/TaskManager/build/managed-modules/taskmgr.ctmeta.json',
        'examples/ManagedShell/Modules/Sd/build/managed-modules/sd.ctm',
        'examples/ManagedShell/Modules/Sd/build/managed-modules/sd.ctmeta.json',
        'examples/ManagedShell/build/ctilde_managed_shell.bin'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $artifact) -PathType Leaf)) {
            throw "The ESP-IDF managed-module build omitted '$artifact'."
        }
    }
    Write-Host 'PASS ESP-IDF managed module and shell packaging'
}

& (Join-Path $repositoryRoot 'examples/HostedNativeImport/Test-HostedNativeImport.ps1') `
    -Compilers msvc -CompilerDll $CompilerDll
if ($LASTEXITCODE -ne 0) { throw 'The hosted native-import example failed.' }

Write-Host 'Example catalog smoke passed.'
