[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root "..\CTilde.Cli\CTilde.Cli.csproj"
$moduleProject = Join-Path $PSScriptRoot "Modules\Hello\ctilde.json"
$shellProject = Join-Path $PSScriptRoot "ctilde.json"
$moduleOutput = Join-Path $PSScriptRoot "Modules\Hello\build\managed-modules\examples.hello.ctm"
$moduleStorage = Join-Path $PSScriptRoot "storage\modules\examples.hello.ctm"

dotnet run --project $cli -- --project $moduleProject --build
if ($LASTEXITCODE -ne 0) { throw "Managed Hello module build failed." }

New-Item -ItemType Directory -Force (Split-Path -Parent $moduleStorage) | Out-Null
Copy-Item -LiteralPath $moduleOutput -Destination $moduleStorage -Force

dotnet run --project $cli -- --project $shellProject --build
if ($LASTEXITCODE -ne 0) { throw "Managed shell firmware build failed." }

if (-not $BuildOnly) {
    Write-Host "Managed shell image built. Flash with the ordinary ESP-IDF flash target when the board is connected."
}
