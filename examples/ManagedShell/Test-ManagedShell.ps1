[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root "..\CTilde.Cli\CTilde.Cli.csproj"
$shellProject = Join-Path $PSScriptRoot "ctilde.json"
$diagnosticsTest = Join-Path $root "..\Test\ManagedShellDiagnostics.test.mjs"
$modules = @(
    [pscustomobject]@{ Name = "Managed Hello"; Directory = "Hello"; Artifact = "examples.hello.ctm" },
    [pscustomobject]@{ Name = "Memory diagnostics"; Directory = "Memory"; Artifact = "memory.ctm" },
    [pscustomobject]@{ Name = "Task manager"; Directory = "TaskManager"; Artifact = "taskmgr.ctm" }
)

$shellSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Program.ct") -Raw
if ($shellSource -match 'command\s*==\s*"(?:memory|taskmgr)"' -or
    $shellSource -match 'ShellPlatform\.(?:PrintMemory|PrintTaskManager)') {
    throw "memory and taskmgr must remain separate managed applications, not ManagedShell built-ins."
}
$hostApiHeaders = @(
    (Join-Path $PSScriptRoot "main\managed_diagnostics_host_api.h"),
    (Join-Path $PSScriptRoot "Modules\Memory\main\diagnostics_host_api.h"),
    (Join-Path $PSScriptRoot "Modules\TaskManager\main\diagnostics_host_api.h")
)
$hostApiReference = Get-Content -LiteralPath $hostApiHeaders[0] -Raw
foreach ($header in $hostApiHeaders | Select-Object -Skip 1) {
    if ((Get-Content -LiteralPath $header -Raw) -cne $hostApiReference) {
        throw "Managed diagnostics host API headers must remain byte-identical."
    }
}

node --test $diagnosticsTest
if ($LASTEXITCODE -ne 0) { throw "Managed shell diagnostics parser tests failed." }

foreach ($module in $modules) {
    $moduleRoot = Join-Path $PSScriptRoot ("Modules\" + $module.Directory)
    $moduleProject = Join-Path $moduleRoot "ctilde.json"
    $moduleOutput = Join-Path $moduleRoot ("build\managed-modules\" + $module.Artifact)
    $moduleStorage = Join-Path $PSScriptRoot ("storage\modules\" + $module.Artifact)
    dotnet run --project $cli -- --project $moduleProject --build --idf-path $IdfPath
    if ($LASTEXITCODE -ne 0) { throw "$($module.Name) module build failed." }
    New-Item -ItemType Directory -Force (Split-Path -Parent $moduleStorage) | Out-Null
    Copy-Item -LiteralPath $moduleOutput -Destination $moduleStorage -Force
}

dotnet run --project $cli -- --project $shellProject --build --idf-path $IdfPath
if ($LASTEXITCODE -ne 0) { throw "Managed shell firmware build failed." }

if (-not $BuildOnly) {
    Write-Host "Managed shell image built. Flash with the ordinary ESP-IDF flash target when the board is connected."
}
