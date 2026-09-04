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
    [pscustomobject]@{ Name = "SD manager"; Directory = "Sd"; Artifact = "sd.ctm" },
    [pscustomobject]@{ Name = "Nano editor"; Directory = "Nano"; Artifact = "nano.ctm" },
    [pscustomobject]@{ Name = "Filesystem commands"; Directory = "FsCommands"; Artifact = "commands.fs.ctm" },
    [pscustomobject]@{ Name = "Network administration"; Directory = "Net"; Artifact = "net.ctm" },
    [pscustomobject]@{ Name = "Managed SSH library"; Directory = "SystemSsh"; Artifact = "system.ssh.ctm" },
    [pscustomobject]@{ Name = "SSH administration service"; Directory = "Sshd"; Artifact = "sshd.ctm" },
    [pscustomobject]@{ Name = "Managed overlay library"; Directory = "OverlayLibrary"; Artifact = "tests.overlay.library.ctm" },
    [pscustomobject]@{ Name = "Managed overlay acceptance"; Directory = "OverlayFixture"; Artifact = "tests.overlay.ctm" }
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
if ($shellSource -notmatch 'ListFilesAt\("/sd", "SD"\)' -or
    $shellSource -notmatch 'usage: ls \[path\]') {
    throw "ManagedShell ls must expose the SD root and accept an explicit directory path."
}
if ($shellSource -notmatch 'ExecuteFileSystemCommand' -or
    $shellSource -notmatch 'command == "mkdir"' -or $shellSource -notmatch 'command == "cat"') {
    throw "ManagedShell must route extensionless filesystem commands through commands.fs.ctm."
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
$networkApiHeaders = @(
    (Join-Path $PSScriptRoot "main\managed_network_host_api.h"),
    (Join-Path $PSScriptRoot "Modules\Net\main\managed_network_host_api.h")
)
if ((Get-Content -LiteralPath $networkApiHeaders[0] -Raw) -cne
    (Get-Content -LiteralPath $networkApiHeaders[1] -Raw)) {
    throw "Managed network host API headers must remain byte-identical."
}
$networkSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Modules\Net\Program.ct") -Raw
if ($networkSource -notmatch [regex]::Escape('/sd/wifi_profile/') -or
    $networkSource -notmatch [regex]::Escape('/storage/net/profiles/')) {
    throw "Managed network profiles must prefer persistent SD storage and retain the LittleFS fallback."
}
if ($networkSource -notmatch [regex]::Escape('[Overlay("network")]') -or
    $networkSource -match [regex]::Escape('File.ReadAllLines')) {
    throw "The network application must retain its compact overlay and bounded profile parser."
}
$sshApiHeaders = @(
    (Join-Path $PSScriptRoot "main\managed_ssh_host_api.h"),
    (Join-Path $PSScriptRoot "Modules\SystemSsh\main\managed_ssh_host_api.h")
)
if ((Get-Content -LiteralPath $sshApiHeaders[0] -Raw) -cne
    (Get-Content -LiteralPath $sshApiHeaders[1] -Raw)) {
    throw "Managed SSH host API headers must remain byte-identical."
}
$storageHostSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "main\managed_storage_host.c") -Raw
foreach ($requiredHostMarker in @('storage_control', 'submit_operation',
        'ct_managed_storage_host_v1', 'validate_layout_locked', 'ct_storage_fat_format',
        'ESP_ELFSYM_END')) {
    if ($storageHostSource -notmatch [regex]::Escape($requiredHostMarker)) {
        throw "Managed storage host is missing '$requiredHostMarker'."
    }
}
$storageRuntimePath = Join-Path $root "..\runtime\esp-idf\ctilde_storage\ctilde_storage.c"
$storageRuntimeSource = Get-Content -LiteralPath $storageRuntimePath -Raw
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
$nanoSources = Get-Content -LiteralPath @(
    (Join-Path $PSScriptRoot "Modules\Nano\NanoBuffer.ct"),
    (Join-Path $PSScriptRoot "Modules\Nano\NanoInput.ct"),
    (Join-Path $PSScriptRoot "Modules\Nano\Program.ct")
) -Raw
foreach ($forbiddenNanoHelper in @('String.Format', '.Split(', '.ToString(')) {
    if ($nanoSources -match [regex]::Escape($forbiddenNanoHelper)) {
        throw "Nano must not pull allocation-heavy helper '$forbiddenNanoHelper' into nano.ctm."
    }
}
foreach ($requiredNanoOverlay in @('[Overlay("buffer")]', '[Overlay("editor")]')) {
    if ($nanoSources -notmatch [regex]::Escape($requiredNanoOverlay)) {
        throw "Nano must retain the '$requiredNanoOverlay' executable partition."
    }
}
$managedRuntimePath = Join-Path $root "..\runtime\esp-idf\ctilde_managed_runtime\ctilde_managed_runtime.c"
$managedRuntimeSource = Get-Content -LiteralPath $managedRuntimePath -Raw
if ($managedRuntimeSource -notmatch '(?s)CT_RUNTIME_SERVICE_CONSOLE_READ.*?transfer->Count == 0u.*?vTaskDelay\(1u\)') {
    throw "Empty managed-console reads must yield to the FreeRTOS idle task."
}
$shellCMake = Get-Content -LiteralPath (Join-Path $PSScriptRoot "CMakeLists.txt") -Raw
if ($shellCMake -notmatch 'ctilde_storage_stage' -or
    $shellCMake -notmatch 'add_dependencies\(littlefs_storage_bin ctilde_storage_stage\)') {
    throw "ManagedShell must restage newly built modules before every LittleFS image build."
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
    if ($module.Artifact -in @("sd.ctm", "nano.ctm") -and (Get-Item -LiteralPath $moduleOutput).Length -gt 98304) {
        throw "$($module.Name) exceeds the 96 KiB ESP32 module-load budget. Avoid pulling allocation-heavy formatting or splitting helpers into $($module.Artifact)."
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $moduleStorage) | Out-Null
    Copy-Item -LiteralPath $moduleOutput -Destination $moduleStorage -Force
}

dotnet run --project $cli -- --project $shellProject --build --idf-path $IdfPath
if ($LASTEXITCODE -ne 0) { throw "Managed shell firmware build failed." }

if (-not $BuildOnly) {
    Write-Host "Managed shell image built. Flash with the ordinary ESP-IDF flash target when the board is connected."
}
