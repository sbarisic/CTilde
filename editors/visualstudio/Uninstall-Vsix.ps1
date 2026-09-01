[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0)]
    [string]$VsixPath,

    [string]$ExtensionId,

    [string[]]$InstanceId,

    [switch]$ShutdownProcesses,

    [string]$LogDirectory = (Join-Path $env:TEMP 'CTilde-VsixInstaller')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Resolve-VsixFile {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'artifacts') -Filter 'CTilde.VisualStudio-*.vsix' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'VSIX not found. Pass -VsixPath, pass -ExtensionId, or run Build-Vsix.ps1 first.'
    }

    (Get-Item -LiteralPath $Path).FullName
}

function Get-VsixIdentity {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.Entries | Where-Object {
            $_.FullName -eq 'extension.vsixmanifest' -or $_.FullName -eq 'source.extension.vsixmanifest'
        } | Select-Object -First 1
        if ($null -eq $manifestEntry) { throw "The VSIX does not contain an extension manifest: $Path" }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose() }

        $identity = $manifest.SelectSingleNode("/*[local-name()='PackageManifest']/*[local-name()='Metadata']/*[local-name()='Identity']")
        if ($null -eq $identity -or [string]::IsNullOrWhiteSpace($identity.Id)) {
            throw "The VSIX manifest does not declare an extension ID: $Path"
        }
        [string]$identity.Id
    }
    finally { $archive.Dispose() }
}

function Get-VisualStudioInstance {
    param([string[]]$RequestedInstanceId)

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere.exe was not found at '$vswhere'. Install Visual Studio 2022 or newer."
    }

    $json = & $vswhere -all -products '*' -prerelease -version '[17.14,)' -format json -utf8
    if ($LASTEXITCODE -ne 0) { throw "vswhere.exe failed with exit code $LASTEXITCODE." }

    $instances = @($json | ConvertFrom-Json) | Where-Object {
        $_.isComplete -and $_.isLaunchable -and
        (Test-Path -LiteralPath (Join-Path $_.installationPath 'Common7\IDE\VSIXInstaller.exe') -PathType Leaf)
    }

    if ($null -ne $RequestedInstanceId -and @($RequestedInstanceId).Count -ne 0) {
        $missing = @($RequestedInstanceId | Where-Object { $_ -notin $instances.instanceId })
        if ($missing.Count -ne 0) { throw "Visual Studio instance ID not found: $($missing -join ', ')" }
        $instances = @($instances | Where-Object instanceId -in $RequestedInstanceId)
    }

    if ($instances.Count -eq 0) { throw 'No supported Visual Studio instance was found.' }
    $instances
}

if ([string]::IsNullOrWhiteSpace($ExtensionId)) {
    $resolvedVsix = Resolve-VsixFile -Path $VsixPath
    $ExtensionId = Get-VsixIdentity -Path $resolvedVsix
}

$instances = @(Get-VisualStudioInstance -RequestedInstanceId $InstanceId)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($LogDirectory)) | Out-Null

foreach ($instance in $instances) {
    $installer = Join-Path $instance.installationPath 'Common7\IDE\VSIXInstaller.exe'
    $logPath = Join-Path ([System.IO.Path]::GetFullPath($LogDirectory)) "uninstall-$($instance.instanceId).log"
    $arguments = @('/quiet', "/instanceIds:$($instance.instanceId)", "/logFile:$logPath", "/uninstall:$ExtensionId")
    if ($ShutdownProcesses) { $arguments += '/shutdownprocesses' }

    if (-not $PSCmdlet.ShouldProcess("$($instance.displayName) [$($instance.instanceId)]", "Uninstall '$ExtensionId' silently")) {
        continue
    }

    & $installer @arguments *> $null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "VSIX uninstallation failed for $($instance.displayName) with exit code $exitCode. Log: $logPath"
    }

    Write-Host "Uninstalled '$ExtensionId' from $($instance.displayName). Log: $logPath"
}
