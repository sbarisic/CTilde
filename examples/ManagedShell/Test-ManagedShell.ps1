[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [string]$CompilerDll = '',
    [switch]$BuildOnly,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root "..\CTilde.Cli\CTilde.Cli.csproj"
$shellProject = Join-Path $PSScriptRoot "ctilde.json"
$diagnosticsTest = Join-Path $root "..\Test\ManagedShellDiagnostics.test.mjs"
$sshTest = Join-Path $root "..\Test\ManagedShellSsh.test.mjs"
function Invoke-ModuleCompiler([string]$Project) {
    if ($CompilerDll) {
        dotnet $CompilerDll --project $Project --build --idf-path $IdfPath
    } else {
        dotnet run --project $cli -- --project $Project --build --idf-path $IdfPath
    }
    if ($LASTEXITCODE -ne 0) { throw "Compiler build failed for $Project." }
}
$modules = @(
    [pscustomobject]@{ Name = "Managed Hello"; Directory = "Hello"; Artifact = "examples.hello.ctm" },
    [pscustomobject]@{ Name = "Allocator acceptance"; Directory = "AllocatorFixture"; Artifact = "tests.allocator.ctm" },
    [pscustomobject]@{ Name = "Memory diagnostics"; Directory = "Memory"; Artifact = "memory.ctm" },
    [pscustomobject]@{ Name = "Task manager"; Directory = "TaskManager"; Artifact = "taskmgr.ctm" },
    [pscustomobject]@{ Name = "SD manager"; Directory = "Sd"; Artifact = "sd.ctm" },
    [pscustomobject]@{ Name = "Nano editor"; Directory = "Nano"; Artifact = "nano.ctm" },
    [pscustomobject]@{ Name = "Filesystem commands"; Directory = "FsCommands"; Artifact = "commands.fs.ctm" },
    [pscustomobject]@{ Name = "Shared shell"; Directory = "Shell"; Artifact = "shell.ctm" },
    [pscustomobject]@{ Name = "Network administration"; Directory = "Net"; Artifact = "net.ctm" },
    [pscustomobject]@{ Name = "Managed SSH library"; Directory = "SystemSsh"; Artifact = "system.ssh.ctm" },
    [pscustomobject]@{ Name = "SSH administration service"; Directory = "Sshd"; Artifact = "sshd.ctm" },
    [pscustomobject]@{ Name = "Managed overlay library"; Directory = "OverlayLibrary"; Artifact = "tests.overlay.library.ctm" },
    [pscustomobject]@{ Name = "Managed overlay acceptance"; Directory = "OverlayFixture"; Artifact = "tests.overlay.ctm" }
)

function Get-ManagedModuleSize {
    param([Parameter(Mandatory)][string]$ModulePath)

    $bytes = [IO.File]::ReadAllBytes($ModulePath)
    if ($bytes.Length -lt 52 -or $bytes[0] -ne 0x7f -or $bytes[1] -ne 0x45 -or
        $bytes[2] -ne 0x4c -or $bytes[3] -ne 0x46 -or $bytes[4] -ne 1 -or $bytes[5] -ne 1) {
        throw "Managed module '$ModulePath' is not a little-endian ELF32 package."
    }
    $sectionHeaderOffset = [BitConverter]::ToUInt32($bytes, 32)
    $sectionHeaderSize = [BitConverter]::ToUInt16($bytes, 46)
    $sectionHeaderCount = [BitConverter]::ToUInt16($bytes, 48)
    if ($sectionHeaderSize -ne 40) {
        throw "Managed module '$ModulePath' has an unsupported ELF32 section-header size."
    }
    $residentExecutable = 0L
    $residentData = 0L
    for ($index = 0; $index -lt $sectionHeaderCount; $index++) {
        $offset = [int64]$sectionHeaderOffset + [int64]$index * $sectionHeaderSize
        if ($offset -lt 0 -or $offset + 40 -gt $bytes.Length) {
            throw "Managed module '$ModulePath' has an invalid section-header table."
        }
        if ([BitConverter]::ToUInt32($bytes, [int]$offset + 4) -ne 1) { continue }
        $flags = [BitConverter]::ToUInt32($bytes, [int]$offset + 8)
        $memoryBytes = [BitConverter]::ToUInt32($bytes, [int]$offset + 20)
        if (($flags -band 6) -eq 6) { $residentExecutable += $memoryBytes }
        if (($flags -band 3) -eq 3) { $residentData += $memoryBytes }
    }
    $metadataPath = [IO.Path]::ChangeExtension($ModulePath, ".ctmeta.json")
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{
        module = $metadata.name
        packageBytes = [int64](Get-Item -LiteralPath $ModulePath).Length
        residentExecutableBytes = $residentExecutable
        residentDataBytes = $residentData
        maximumOverlayBytes = [int64]$metadata.maximumOverlayBytes
    }
}

$sizeBudgets = @{
    "shell.ctm" = [pscustomobject]@{ Resident = 28KB; Overlay = 20KB }
    "system.ssh.ctm" = [pscustomobject]@{ Resident = 36KB; Overlay = 28KB }
    "sshd.ctm" = [pscustomobject]@{ Resident = 10KB; Overlay = [int64]::MaxValue }
}
$sizeMeasurements = @{}

$firmwareSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Program.ct") -Raw
$shellSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Modules\Shell\Program.ct") -Raw
if ($firmwareSource -notmatch [regex]::Escape('/storage/modules/shell.ctm') -or
    $firmwareSource -notmatch [regex]::Escape('"--uart"')) {
    throw "ManagedShell firmware must supervise the internal shell.ctm application."
}
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
if ($shellSource -notmatch 'FileSystemCommand' -or
    $shellSource -notmatch 'command == "mkdir"' -or $shellSource -notmatch 'command == "cat"') {
    throw "ManagedShell must route extensionless filesystem commands through commands.fs.ctm."
}
$shellEditorSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Modules\Shell\ShellEditor.ct") -Raw
foreach ($requiredShellMarker in @('args[0] == "--exec"', 'args[0] != "--uart"',
        'args[0] != "--ssh"', 'bracketedPaste', 'HistoryCapacity = 32', 'SetForeground',
        '[Overlay("help")]', '[Overlay("filesystem")]', '[Overlay("process-admin")]')) {
    if (($shellSource + $shellEditorSource) -notmatch [regex]::Escape($requiredShellMarker)) {
        throw "Shared shell is missing '$requiredShellMarker'."
    }
}
$parserSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Modules\Shell\ShellCommandLine.ct") -Raw
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
$sshSources = [string]::Join("`n", (Get-Content -LiteralPath @(
    (Join-Path $PSScriptRoot "Modules\SystemSsh\Protocol.ct"),
    (Join-Path $PSScriptRoot "Modules\SystemSsh\Transport.ct"),
    (Join-Path $PSScriptRoot "Modules\SystemSsh\Configuration.ct"),
    (Join-Path $PSScriptRoot "Modules\SystemSsh\Server.ct"),
    (Join-Path $PSScriptRoot "Modules\SystemSsh\Sftp.ct"),
    (Join-Path $PSScriptRoot "Modules\Sshd\Program.ct")
) -Raw))
foreach ($requiredSshMarker in @('curve25519-sha256', 'kex-strict-s-v00@openssh.com',
        'aes128-gcm@openssh.com', 'ssh-userauth', 'publickey', 'ExchangeKeys(payload)',
        '/storage/modules/shell.ctm', '[Overlay("configuration")]', '[Overlay("handshake")]',
        '[Overlay("authentication")]', '[Overlay("sftp-core")]',
        '[Overlay("sftp-files")]', '[Overlay("sftp-directories")]',
        'SftpSession', 'MaximumTransferBytes = 32768')) {
    if ($sshSources -notmatch [regex]::Escape($requiredSshMarker)) {
        throw "Managed SSH implementation is missing '$requiredSshMarker'."
    }
}
$sshNativeSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Modules\SystemSsh\main\ssh_native.c") -Raw
if ($sshNativeSource -match 'SSH-2\.0-' -or $sshNativeSource -match 'static.*stop') {
    throw "The unloadable SSH native wrapper must not own protocol or service state."
}
$shellApiHeaders = @(
    (Join-Path $PSScriptRoot "main\managed_shell_host_api.h"),
    (Join-Path $PSScriptRoot "Modules\Shell\main\managed_shell_host_api.h")
)
if ((Get-Content -LiteralPath $shellApiHeaders[0] -Raw) -cne
    (Get-Content -LiteralPath $shellApiHeaders[1] -Raw)) {
    throw "Managed shell host API headers must remain byte-identical."
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
$nanoSources = [string]::Join("`n", (Get-Content -LiteralPath @(
    (Join-Path $PSScriptRoot "Modules\Nano\NanoBuffer.ct"),
    (Join-Path $PSScriptRoot "Modules\Nano\NanoInput.ct"),
    (Join-Path $PSScriptRoot "Modules\Nano\Program.ct")
) -Raw))
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
foreach ($requiredOverlayHardening in @('volatile uint32_t *OverlayWindow', 'uint32_t staging[CT_OVERLAY_STAGING_WORDS]',
        'psa_hash_update', 'LoadedOverlayModule', 'LogicalOverlayModule', 'CONFIG_CTILDE_MANAGED_MAX_CALL_DEPTH')) {
    if ($managedRuntimeSource -notmatch [regex]::Escape($requiredOverlayHardening)) {
        throw "Managed overlay runtime is missing hardening marker '$requiredOverlayHardening'."
    }
}
foreach ($requiredProcessOwnership in @('ct_runtime_thread_attach_v23', 'ct_child_task',
        'terminate_child_tasks', 'NativeResources', 'ForegroundProcess')) {
    if ($managedRuntimeSource -notmatch [regex]::Escape($requiredProcessOwnership)) {
        throw "Managed process ownership is missing '$requiredProcessOwnership'."
    }
}
if ($managedRuntimeSource -notmatch '(?s)ct_managed_process_pipe_close.*?OwnsParentStream\[stream\] = false;.*?ParentClosed.*?ChildrenClosed.*?Streams\[stream\] = NULL') {
    throw "Closing a redirected parent pipe must preserve the child endpoint until child cleanup."
}
foreach ($forbiddenOverlayWindowAccess in @('fread(process->OverlayWindow', 'memset(process->OverlayWindow',
        'psa_hash_compute(PSA_ALG_SHA_256, process->OverlayWindow', 'memcpy(process->OverlayWindow')) {
    if ($managedRuntimeSource -match [regex]::Escape($forbiddenOverlayWindowAccess)) {
        throw "Managed executable overlay windows must not use byte-oriented access '$forbiddenOverlayWindowAccess'."
    }
}
$shellCMake = Get-Content -LiteralPath (Join-Path $PSScriptRoot "CMakeLists.txt") -Raw
if ($shellCMake -notmatch 'ctilde_storage_stage' -or
    $shellCMake -notmatch 'add_dependencies\(littlefs_storage_bin ctilde_storage_stage\)' -or
    $shellCMake -notmatch 'ctilde_ssh_package') {
    throw "ManagedShell must restage newly built modules before every LittleFS image build."
}

node --test $diagnosticsTest
if ($LASTEXITCODE -ne 0) { throw "Managed shell diagnostics parser tests failed." }
node --test $sshTest
if ($LASTEXITCODE -ne 0) { throw "Managed shell SSH protocol fixture tests failed." }
if ($ValidateOnly) {
    Write-Host "ManagedShell focused source and transcript validation passed."
    return
}

foreach ($module in $modules) {
    $moduleRoot = Join-Path $PSScriptRoot ("Modules\" + $module.Directory)
    $moduleProject = Join-Path $moduleRoot "ctilde.json"
    $moduleOutput = Join-Path $moduleRoot ("build\managed-modules\" + $module.Artifact)
    $moduleStorage = Join-Path $PSScriptRoot ("storage\modules\" + $module.Artifact)
    Invoke-ModuleCompiler $moduleProject
    if ($LASTEXITCODE -ne 0) { throw "$($module.Name) module build failed." }
    if ($sizeBudgets.ContainsKey($module.Artifact)) {
        $measurement = Get-ManagedModuleSize -ModulePath $moduleOutput
        $budget = $sizeBudgets[$module.Artifact]
        if ($measurement.residentExecutableBytes -gt $budget.Resident) {
            throw "$($module.Name) resident executable size $($measurement.residentExecutableBytes) exceeds $($budget.Resident) bytes."
        }
        if ($measurement.maximumOverlayBytes -gt $budget.Overlay) {
            throw "$($module.Name) overlay window $($measurement.maximumOverlayBytes) exceeds $($budget.Overlay) bytes."
        }
        $sizeMeasurements[$module.Artifact] = $measurement
        Write-Host ("{0}: package={1}, resident-executable={2}, resident-data={3}, overlay-window={4}" -f `
            $measurement.module, $measurement.packageBytes, $measurement.residentExecutableBytes,
            $measurement.residentDataBytes, $measurement.maximumOverlayBytes)
    }
    if ($module.Artifact -eq "sd.ctm" -and (Get-Item -LiteralPath $moduleOutput).Length -gt 98304) {
        throw "$($module.Name) exceeds the 96 KiB ESP32 module-load budget."
    }
    if ($module.Artifact -eq "nano.ctm") {
        $moduleMetadata = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($moduleOutput, ".ctmeta.json")) -Raw | ConvertFrom-Json
        if (-not $moduleMetadata.hasOverlays -or $moduleMetadata.maximumOverlayBytes -gt 98304) {
            throw "Nano's largest streamed executable partition exceeds the 96 KiB ESP32 module-load budget."
        }
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $moduleStorage) | Out-Null
    Copy-Item -LiteralPath $moduleOutput -Destination $moduleStorage -Force
}

$concurrentExecutableBytes =
    $sizeMeasurements["shell.ctm"].residentExecutableBytes +
    $sizeMeasurements["shell.ctm"].maximumOverlayBytes +
    $sizeMeasurements["system.ssh.ctm"].residentExecutableBytes +
    $sizeMeasurements["system.ssh.ctm"].maximumOverlayBytes +
    $sizeMeasurements["sshd.ctm"].residentExecutableBytes +
    $sizeMeasurements["sshd.ctm"].maximumOverlayBytes
$concurrentBudgetBytes = 122KB
if ($concurrentExecutableBytes -gt $concurrentBudgetBytes) {
    throw "Concurrent UART shell and SSH daemon executable working set $concurrentExecutableBytes exceeds $concurrentBudgetBytes bytes."
}
$sizeReport = [ordered]@{
    schemaVersion = 1
    draftVersion = "0.51"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    modules = @($sizeMeasurements.Values | Sort-Object module)
    concurrentProcessGraphExecutableBytes = $concurrentExecutableBytes
    concurrentProcessGraphBudgetBytes = $concurrentBudgetBytes
}
$sizeReportPath = Join-Path $root "..\artifacts\managed-shell\managed-module-sizes.json"
New-Item -ItemType Directory -Force (Split-Path -Parent $sizeReportPath) | Out-Null
$sizeReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $sizeReportPath -Encoding utf8
Write-Host "Concurrent shell plus SSH executable working set: $concurrentExecutableBytes / $concurrentBudgetBytes bytes."

Invoke-ModuleCompiler $shellProject
if ($LASTEXITCODE -ne 0) { throw "Managed shell firmware build failed." }

if (-not $BuildOnly) {
    Write-Host "Managed shell image built. Flash with the ordinary ESP-IDF flash target when the board is connected."
}
