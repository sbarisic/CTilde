[CmdletBinding()]
param(
    [string]$IdfPath = $env:IDF_PATH,
    [ValidateSet("esp32", "esp32c3")]
    [string]$Target = "esp32",
    [string]$Port = "COM4",
    [string]$Source = "Program.ct",
    [switch]$Flash,
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
$generatedPath = Join-Path $generatedDirectory "ctilde_program.c"
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

Push-Location $projectDirectory
try {
    & dotnet run --project (Join-Path $repositoryDirectory "CTilde.Cli") -- $sourcePath -o $generatedPath --target esp-idf --trace
    if ($LASTEXITCODE -ne 0) { throw "C~ compilation failed with exit code $LASTEXITCODE." }

    & idf.py set-target $Target
    if ($LASTEXITCODE -ne 0) { throw "idf.py set-target failed with exit code $LASTEXITCODE." }

    & idf.py build
    if ($LASTEXITCODE -ne 0) { throw "idf.py build failed with exit code $LASTEXITCODE." }

    if ($Flash) {
        & idf.py -p $Port flash
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
