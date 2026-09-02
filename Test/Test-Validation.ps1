[CmdletBinding()]
param(
    [ValidateSet('Fast', 'Release')]
    [string]$Tier = 'Fast'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
$conformanceDll = Join-Path $repositoryRoot 'Test/bin/Release/net10.0/CTilde.Tests.dll'
$debugAdapterTestsDll = Join-Path $repositoryRoot 'CTilde.DebugAdapter.Tests/bin/Release/net10.0/CTilde.DebugAdapter.Tests.dll'
$visualStudioTestsProject = Join-Path $repositoryRoot 'editors/visualstudio/CTilde.VisualStudio.Tests/CTilde.VisualStudio.Tests.csproj'
$visualStudioTestsDll = Join-Path $repositoryRoot 'editors/visualstudio/CTilde.VisualStudio.Tests/bin/Release/net10.0/CTilde.VisualStudio.Tests.dll'
$vscodeRoot = Join-Path $repositoryRoot 'editors/vscode'
$total = [Diagnostics.Stopwatch]::StartNew()

function Invoke-Phase([string]$Name, [scriptblock]$Action) {
    Write-Host "`n=== $Name ==="
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $stopwatch.Stop()
        Write-Host ("PASS {0} ({1:N2}s)" -f $Name, $stopwatch.Elapsed.TotalSeconds)
    }
    catch {
        $stopwatch.Stop()
        Write-Host ("FAIL {0} ({1:N2}s)" -f $Name, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Red
        throw
    }
}

function Invoke-Checked([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repositoryRoot) {
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required validation command '$Name' was not found."
    }
}

function Invoke-Conformance([string]$Compiler, [switch]$CrossToolchainOnly) {
    $previousCompiler = $env:CTILDE_CC
    try {
        if ([string]::IsNullOrEmpty($Compiler)) {
            Remove-Item Env:CTILDE_CC -ErrorAction SilentlyContinue
        }
        else {
            $env:CTILDE_CC = $Compiler
        }
        $arguments = @($conformanceDll)
        if ($CrossToolchainOnly) { $arguments += '--cross-toolchain-only' }
        Invoke-Checked 'dotnet' $arguments
    }
    finally {
        if ($null -eq $previousCompiler) { Remove-Item Env:CTILDE_CC -ErrorAction SilentlyContinue }
        else { $env:CTILDE_CC = $previousCompiler }
    }
}

foreach ($command in @('dotnet', 'node', 'npm', 'wsl')) { Require-Command $command }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw "MSVC discovery tool was not found: $vswhere" }
$visualStudio = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if ([string]::IsNullOrWhiteSpace($visualStudio)) { throw 'Visual Studio C++ x64 tools were not found.' }
foreach ($compiler in @('gcc', 'clang')) {
    & wsl --exec $compiler --version *> $null
    if ($LASTEXITCODE -ne 0) { throw "Required WSL compiler '$compiler' was not found." }
}

Invoke-Phase 'Release managed build' {
    Invoke-Checked 'dotnet' @('build', '.\CTilde.sln', '-c', 'Release', '--nologo')
}
foreach ($path in @($compilerDll, $conformanceDll, $debugAdapterTestsDll)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "The managed build omitted required output '$path'." }
}

Invoke-Phase 'Full conformance (MSVC)' { Invoke-Conformance '' }
Invoke-Phase 'Cross-toolchain conformance (WSL GCC)' { Invoke-Conformance 'wsl:gcc' -CrossToolchainOnly }
Invoke-Phase 'Cross-toolchain conformance (WSL Clang)' { Invoke-Conformance 'wsl:clang' -CrossToolchainOnly }
Invoke-Phase 'Debug Adapter managed tests' { Invoke-Checked 'dotnet' @($debugAdapterTestsDll) }
Invoke-Phase 'Visual Studio core tests' {
    Invoke-Checked 'dotnet' @('build', $visualStudioTestsProject, '-c', 'Release', '--nologo')
    Invoke-Checked 'dotnet' @($visualStudioTestsDll)
}
Invoke-Phase 'VS Code prepared tests' { Invoke-Checked 'npm' @('run', 'test:no-build') $vscodeRoot }

if ($Tier -eq 'Release') {
    Invoke-Phase 'Example catalog smoke' {
        & (Join-Path $PSScriptRoot 'Test-ExampleCatalog.ps1') -CompilerDll $compilerDll
        if ($LASTEXITCODE -ne 0) { throw 'The example catalog smoke failed.' }
    }
    Invoke-Phase 'Native-import release matrix' {
        & (Join-Path $PSScriptRoot 'Test-NativeImportMatrix.ps1') -CompilerDll $compilerDll
        if ($LASTEXITCODE -ne 0) { throw 'The native-import release matrix failed.' }
    }
    Invoke-Phase 'HostedIo SIMD release matrix' {
        & (Join-Path $PSScriptRoot 'Test-HostedSimdMatrix.ps1') -CompilerDll $compilerDll
        if ($LASTEXITCODE -ne 0) { throw 'The HostedIo SIMD release matrix failed.' }
    }
    Invoke-Phase 'VS Code bundled extension host' { Invoke-Checked 'npm' @('run', 'test:extension:no-build') $vscodeRoot }
    Invoke-Phase 'VS Code minimum extension host' { Invoke-Checked 'npm' @('run', 'test:extension:minimum:no-build') $vscodeRoot }
    Invoke-Phase 'Conformance filter interfaces' {
        Invoke-Checked 'dotnet' @($conformanceDll, '--filter', 'deterministic C emission')
        $previousFilter = $env:CTILDE_TEST_FILTER
        try {
            $env:CTILDE_TEST_FILTER = 'deterministic C emission'
            Invoke-Checked 'dotnet' @($conformanceDll)
        }
        finally {
            if ($null -eq $previousFilter) { Remove-Item Env:CTILDE_TEST_FILTER -ErrorAction SilentlyContinue }
            else { $env:CTILDE_TEST_FILTER = $previousFilter }
        }
    }
    Invoke-Phase 'Formatting and diff checks' {
        Invoke-Checked 'dotnet' @('format', '.\CTilde.sln', '--verify-no-changes', '--no-restore')
        Invoke-Checked 'git' @('-c', 'safe.directory=E:/Projects/CTilde', 'diff', '--check')
    }
}

$total.Stop()
Write-Host ("`nValidation tier {0} passed in {1:N2}s." -f $Tier, $total.Elapsed.TotalSeconds)
