[CmdletBinding()]
param(
    [string]$IdfPath = $env:IDF_PATH,
    [ValidateSet("esp32", "esp32c3")]
    [string]$Target = "esp32",
    [string]$Port = "COM4",
    [string]$Source = "Program.ct",
    [switch]$Clean,
    [switch]$Flash,
    [ValidateRange(115200, 2000000)]
    [int]$FlashBaud = 921600,
    [switch]$Monitor
)

$ErrorActionPreference = "Stop"
$projectDirectory = $PSScriptRoot
$repositoryDirectory = Split-Path -Parent (Split-Path -Parent $projectDirectory)

if ($projectDirectory -match '\s') {
    throw "The ESP-IDF example path cannot contain spaces: $projectDirectory"
}

if ([string]::IsNullOrWhiteSpace($IdfPath)) {
    $installedIdf = "C:\esp\v6.0.2\esp-idf"
    if (Test-Path -LiteralPath $installedIdf) {
        $IdfPath = $installedIdf
    } else {
        throw "Set IDF_PATH or pass -IdfPath with an ESP-IDF installation."
    }
}

$sourcePath = if ([IO.Path]::IsPathRooted($Source)) { $Source } else { Join-Path $projectDirectory $Source }
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "C~ source file was not found: $sourcePath"
}

$generatedDirectory = Join-Path $projectDirectory "main\generated"
$generatedHeaderPath = Join-Path $generatedDirectory "ctilde_exports.h"
New-Item -ItemType Directory -Force -Path $generatedDirectory | Out-Null

$resolvedIdfPath = (Resolve-Path -LiteralPath $IdfPath).Path
$activeIdfPath = if ([string]::IsNullOrWhiteSpace($env:IDF_PATH)) { $null } else { (Resolve-Path -LiteralPath $env:IDF_PATH -ErrorAction SilentlyContinue).Path }
if ($activeIdfPath -ne $resolvedIdfPath -or $null -eq (Get-Command idf.py -ErrorAction SilentlyContinue)) {
    $profileRoots = @($env:IDF_TOOLS_PATH, "C:\Espressif\tools") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    $eimProfile = $profileRoots | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Filter "Microsoft.*.PowerShell_profile.ps1" -File -ErrorAction SilentlyContinue
    } | Where-Object {
        (Get-Content -LiteralPath $_.FullName -Raw) -match [regex]::Escape($resolvedIdfPath)
    } | Select-Object -First 1

    if ($null -ne $eimProfile) {
        . $eimProfile.FullName
    } else {
        $exportScript = Join-Path $resolvedIdfPath "export.ps1"
        if (-not (Test-Path -LiteralPath $exportScript)) {
            throw "ESP-IDF activation script was not found for: $resolvedIdfPath"
        }
        . $exportScript
    }
}

function Get-ConfiguredTarget {
    $descriptionTarget = $null
    $descriptionPath = Join-Path $projectDirectory "build\project_description.json"
    if (Test-Path -LiteralPath $descriptionPath) {
        try {
            $description = Get-Content -LiteralPath $descriptionPath -Raw | ConvertFrom-Json
            $descriptionTarget = [string]$description.target
        } catch {
            Write-Verbose "Could not read ESP-IDF project description: $($_.Exception.Message)"
        }
    }

    $sdkTarget = $null
    $sdkconfigPath = Join-Path $projectDirectory "sdkconfig"
    if (Test-Path -LiteralPath $sdkconfigPath) {
        $targetMatch = Select-String -LiteralPath $sdkconfigPath -Pattern '^CONFIG_IDF_TARGET="([^"]+)"$' | Select-Object -First 1
        if ($null -ne $targetMatch) {
            $sdkTarget = $targetMatch.Matches[0].Groups[1].Value
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($descriptionTarget) -and
        -not [string]::IsNullOrWhiteSpace($sdkTarget) -and
        $descriptionTarget -ne $sdkTarget) {
        Write-Warning "ESP-IDF target metadata disagrees ($descriptionTarget versus $sdkTarget); reinitializing the target."
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($descriptionTarget)) { return $descriptionTarget }
    if (-not [string]::IsNullOrWhiteSpace($sdkTarget)) { return $sdkTarget }
    return $null
}

function Find-CurrentCompilerDll {
    $sourceFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryDirectory "CTilde.Cli") -Recurse -File -Include *.cs,*.csproj |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        Get-ChildItem -LiteralPath (Join-Path $repositoryDirectory "CTilde") -Recurse -File -Include *.cs,*.csproj |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    )
    $newestSource = ($sourceFiles | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
    $candidates = @(
        Join-Path $repositoryDirectory "CTilde.Cli\bin\Release\net10.0\ctilde.dll"
        Join-Path $repositoryDirectory "CTilde.Cli\bin\Debug\net10.0\ctilde.dll"
    )

    return $candidates |
        Where-Object { (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTimeUtc -ge $newestSource } |
        Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
        Select-Object -First 1
}

function Invoke-Ctilde([string[]]$CompilerArguments) {
    $compilerDll = Find-CurrentCompilerDll
    if (-not [string]::IsNullOrWhiteSpace($compilerDll)) {
        Write-Host "Using compiler: $compilerDll"
        & dotnet $compilerDll @CompilerArguments
    } else {
        Write-Host "Compiler output is missing or stale; building through dotnet run."
        & dotnet run --project (Join-Path $repositoryDirectory "CTilde.Cli") -c Release --no-launch-profile -- @CompilerArguments
    }
    $script:ctildeExitCode = $LASTEXITCODE
}

function Get-BuildEnvironmentSignature {
    return ([ordered]@{
        fatalRuntime = [string]$env:CTILDE_FATAL_RUNTIME_BUILD
        memoryValidation = [string]$env:CTILDE_MEMORY_VALIDATION_BUILD
    } | ConvertTo-Json -Compress)
}

function Write-BuildEnvironmentSignature([string]$Path, [string]$Value) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if ((Test-Path -LiteralPath $Path) -and [IO.File]::ReadAllText($Path) -ceq $Value) { return }
    $temporary = Join-Path $directory ("." + [IO.Path]::GetFileName($Path) + "." + [guid]::NewGuid().ToString("N") + ".tmp")
    try {
        [IO.File]::WriteAllText($temporary, $Value, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Push-Location $projectDirectory
try {
    $configuredTarget = Get-ConfiguredTarget
    $targetReinitialized = $Clean -or $configuredTarget -ne $Target
    if ($targetReinitialized) {
        $reason = if ($Clean) { "a clean build was requested" } elseif ([string]::IsNullOrWhiteSpace($configuredTarget)) { "no target is initialized" } else { "the configured target is $configuredTarget" }
        Write-Host "Initializing ESP-IDF target '$Target' because $reason."
        & idf.py set-target $Target
        if ($LASTEXITCODE -ne 0) { throw "idf.py set-target failed with exit code $LASTEXITCODE." }
    } else {
        Write-Host "Using initialized ESP-IDF target '$configuredTarget'; preserving the build directory."
    }

    $environmentStatePath = Join-Path $projectDirectory "build\.ctilde\wrapper-environment.json"
    $environmentSignature = Get-BuildEnvironmentSignature
    $environmentStateMissing = -not (Test-Path -LiteralPath $environmentStatePath)
    $environmentStateChanged = -not $environmentStateMissing -and
        [IO.File]::ReadAllText($environmentStatePath) -cne $environmentSignature
    if (-not $targetReinitialized -and ($environmentStateMissing -or $environmentStateChanged)) {
        $environmentReason = if ($environmentStateMissing) { "has not been recorded" } else { "changed" }
        Write-Host "Relevant build environment $environmentReason; reconfiguring without a full clean."
        & idf.py reconfigure
        if ($LASTEXITCODE -ne 0) { throw "idf.py reconfigure failed with exit code $LASTEXITCODE." }
    }

    $defaultSourcePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory "Program.ct"))
    if ([IO.Path]::GetFullPath($sourcePath) -eq $defaultSourcePath) {
        $compilerArguments = @("--project", (Join-Path $projectDirectory "ctilde.json"), "--build", "--trace")
    } else {
        $compilerArguments = @(
            $sourcePath,
            "--c-layout", "modules",
            "--output-directory", $generatedDirectory,
            "--header", $generatedHeaderPath,
            "--target", "esp-idf",
            "--build",
            "--idf-project", $projectDirectory,
            "--trace")
    }

    $script:ctildeExitCode = 0
    Invoke-Ctilde $compilerArguments
    if ($script:ctildeExitCode -ne 0) { throw "C~ native build failed with exit code $script:ctildeExitCode." }
    Write-BuildEnvironmentSignature $environmentStatePath $environmentSignature

    if ($Flash) {
        & idf.py -p $Port -b $FlashBaud flash
        if ($LASTEXITCODE -ne 0 -and $FlashBaud -ne 460800) {
            Write-Warning "Flashing at $FlashBaud baud failed; retrying once at 460800 baud."
            & idf.py -p $Port -b 460800 flash
        }
        if ($LASTEXITCODE -ne 0) { throw "idf.py flash failed with exit code $LASTEXITCODE." }
    }

    if ($Monitor) {
        & idf.py -p $Port monitor
        if ($LASTEXITCODE -ne 0) { throw "idf.py monitor failed with exit code $LASTEXITCODE." }
    }
}
finally {
    Pop-Location
}
