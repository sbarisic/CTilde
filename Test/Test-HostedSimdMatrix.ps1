param(
    [string[]]$Compilers = @('msvc', 'wsl:gcc', 'wsl:clang')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
$exampleRoot = Join-Path $repositoryRoot 'examples/HostedIo'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('ctilde-hosted-simd-matrix-' + [guid]::NewGuid().ToString('N'))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($workRoot).StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Matrix work directory escaped the system temporary directory: $workRoot"
}

try {
    if (-not (Test-Path $compilerDll)) {
        & dotnet build (Join-Path $repositoryRoot 'CTilde.sln') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'The compiler build failed.' }
    }
    foreach ($compiler in $Compilers) {
        foreach ($profile in @(
            @{ Name = 'debug-unity'; Configuration = 'debug'; Layout = 'unity'; Lto = $false },
            @{ Name = 'debug-modules'; Configuration = 'debug'; Layout = 'modules'; Lto = $false },
            @{ Name = 'release-unity'; Configuration = 'release'; Layout = 'unity'; Lto = $false },
            @{ Name = 'release-modules'; Configuration = 'release'; Layout = 'modules'; Lto = $false },
            @{ Name = 'release-modules-lto'; Configuration = 'release'; Layout = 'modules'; Lto = $true })) {
            $label = ($compiler -replace ':', '-') + '-' + $profile.Name
            $root = Join-Path $workRoot $label
            New-Item -ItemType Directory -Path $root | Out-Null
            Get-ChildItem $exampleRoot -Filter '*.ct' | Copy-Item -Destination $root
            $manifest = [ordered]@{
                target = 'hosted'
                architecture = 'x64'
                simdOptimizations = $true
                sources = @('*.ct')
                build = [ordered]@{
                    cLayout = $profile.Layout
                    generatedDirectory = 'build/generated'
                    generatedHeader = 'build/generated/ctilde_exports.h'
                    symbolMap = 'build/generated/ctilde_symbols.json'
                    configuration = $profile.Configuration
                    lto = $profile.Lto
                    compiler = $compiler
                    executable = "build/$label.exe"
                }
            }
            [IO.File]::WriteAllText((Join-Path $root 'ctilde.json'),
                ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
            & dotnet $compilerDll --project (Join-Path $root 'ctilde.json') --build
            if ($LASTEXITCODE -ne 0) { throw "Hosted SIMD matrix build failed: $label" }
            Write-Host "PASS $label"
        }
    }
}
finally {
    if (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
