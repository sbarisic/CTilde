[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.10.0",
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\compiler")
)

$ErrorActionPreference = "Stop"
if ($Runtime -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $Runtime.Contains('..')) {
    throw "Runtime must be a single .NET runtime identifier without path traversal."
}
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $Version.Contains('..')) {
    throw "Version must contain only letters, numbers, periods, and hyphens."
}
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $resolvedOutputRoot $Runtime))
$archivePath = Join-Path $resolvedOutputRoot "ctilde-$Version-$Runtime.zip"
if (-not $publishDirectory.StartsWith($resolvedOutputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish directory must stay inside the selected output root."
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& dotnet publish (Join-Path $PSScriptRoot "CTilde.Cli.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "C~ compiler publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "DISTRIBUTION.md") -Destination (Join-Path $publishDirectory "README.md")
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath
Write-Output "Published standalone C~ compiler: $archivePath"
