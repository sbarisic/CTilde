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
    [pscustomobject]@{ Name = "Task manager"; Directory = "TaskManager"; Artifact = "taskmgr.ctm" },
    [pscustomobject]@{ Name = "SD manager"; Directory = "Sd"; Artifact = "sd.ctm" }
)

$shellSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Program.ct") -Raw
if ($shellSource -match 'command\s*==\s*"(?:memory|taskmgr|storage|remount|exec)"' -or
    $shellSource -match 'ShellPlatform\.(?:PrintMemory|PrintTaskManager)') {
    throw "Managed applications and SD control must remain outside ManagedShell built-ins."
}
if ($shellSource -notmatch 'command\.EndsWith\("\.ctm"\)' -or
    $shellSource -match 'command\s*==\s*"exec"') {
    throw "ManagedShell must dispatch only explicit lowercase .ctm application names."
}
$parserSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "ShellCommandLine.ct") -Raw
foreach ($requiredParserMarker in @('TryEscape', 'wasQuoted', 'background', 'inQuotes')) {
    if ($parserSource -notmatch [regex]::Escape($requiredParserMarker)) {
        throw "ManagedShell command-line parser is missing '$requiredParserMarker'."
    }
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
$storageApiHeaders = @(
    (Join-Path $PSScriptRoot "main\managed_storage_host_api.h"),
    (Join-Path $PSScriptRoot "Modules\Sd\main\managed_storage_host_api.h")
)
if ((Get-Content -LiteralPath $storageApiHeaders[0] -Raw) -cne
    (Get-Content -LiteralPath $storageApiHeaders[1] -Raw)) {
    throw "Managed storage host API headers must remain byte-identical."
}
$storageHostSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "main\managed_storage_host.c") -Raw
foreach ($requiredHostMarker in @('storage_control', 'submit_operation',
        'ct_managed_storage_host_v1', 'validate_layout_locked', 'ct_storage_fat_format')) {
    if ($storageHostSource -notmatch [regex]::Escape($requiredHostMarker)) {
        throw "Managed storage host is missing '$requiredHostMarker'."
    }
}
$storageRuntimeSource = Get-Content -LiteralPath (Join-Path $root
    "..\runtime\esp-idf\ctilde_storage\ctilde_storage.c") -Raw
if ($storageRuntimeSource -notmatch '(?s)if \(fat_result != FR_OK\).*?f_mount\(NULL, mount->DriveName, 0\).*?esp_vfs_fat_unregister_path\(path\)' -or
    $storageRuntimeSource -notmatch '(?s)fat_result = f_mount\(&probe, name, 1\).*?FRESULT unmount_result = f_mount\(NULL, name, 0\)') {
    throw "Failed FAT mounts must unregister their FATFS objects before releasing VFS or stack storage."
}
$sdSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Modules\Sd\Program.ct") -Raw
foreach ($requiredSdCommand in @('sd.ctm status', 'sd.ctm info', 'sd.ctm mount',
        'sd.ctm unmount', 'sd.ctm remount', 'sd.ctm format --yes',
        'sd.ctm mbr show', 'sd.ctm mbr write --yes')) {
    if ($sdSource -notmatch [regex]::Escape($requiredSdCommand)) {
        throw "SD application is missing '$requiredSdCommand'."
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
    if ($module.Artifact -eq "sd.ctm" -and (Get-Item -LiteralPath $moduleOutput).Length -gt 98304) {
        throw "SD manager exceeds the 96 KiB ESP32 module-load budget. Avoid pulling allocation-heavy formatting or splitting helpers into sd.ctm."
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $moduleStorage) | Out-Null
    Copy-Item -LiteralPath $moduleOutput -Destination $moduleStorage -Force
}

dotnet run --project $cli -- --project $shellProject --build --idf-path $IdfPath
if ($LASTEXITCODE -ne 0) { throw "Managed shell firmware build failed." }

if (-not $BuildOnly) {
    Write-Host "Managed shell image built. Flash with the ordinary ESP-IDF flash target when the board is connected."
}
