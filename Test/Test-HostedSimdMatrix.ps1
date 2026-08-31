param(
    [string[]]$Compilers = @('msvc', 'wsl:gcc', 'wsl:clang'),
    [string]$CompilerDll = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CompilerDll)) {
    & dotnet build (Join-Path $repositoryRoot 'CTilde.Cli/CTilde.Cli.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The Release compiler build failed.' }
    $CompilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
}
$CompilerDll = [IO.Path]::GetFullPath($CompilerDll)
if (-not (Test-Path -LiteralPath $CompilerDll)) { throw "The compiler DLL was not found: $CompilerDll" }
$exampleRoot = Join-Path $repositoryRoot 'examples/HostedIo'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('ctilde-hosted-simd-matrix-' + [guid]::NewGuid().ToString('N'))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($workRoot).StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Matrix work directory escaped the system temporary directory: $workRoot"
}

try {
    foreach ($compiler in $Compilers) {
        $profiles = foreach ($optimization in @('speed', 'aggressive')) {
            foreach ($cpu in @('baseline', 'avx2')) {
                foreach ($floatingPoint in @('precise', 'fast')) {
                    @{ Name = "$optimization-$cpu-$floatingPoint"; Configuration = 'release'; Layout = 'unity'; Lto = $true
                        Optimization = $optimization; Cpu = $cpu; FloatingPoint = $floatingPoint }
                }
            }
        }
        foreach ($profile in $profiles) {
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
                    generatedC = 'build/generated/ctilde_program.c'
                    generatedHeader = 'build/generated/ctilde_exports.h'
                    symbolMap = 'build/generated/ctilde_symbols.json'
                    configuration = $profile.Configuration
                    lto = $profile.Lto
                    compiler = $compiler
                    executable = "build/$label.exe"
                    optimization = $profile.Optimization
                    cpuTarget = $profile.Cpu
                    floatingPoint = $profile.FloatingPoint
                    pgo = [ordered]@{ mode = 'off'; directory = 'build/pgo' }
                }
            }
            [IO.File]::WriteAllText((Join-Path $root 'ctilde.json'),
                ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
            & dotnet $CompilerDll --project (Join-Path $root 'ctilde.json') --build
            if ($LASTEXITCODE -ne 0) { throw "Hosted SIMD matrix build failed: $label" }
            Write-Host "PASS $label"
        }
    }
}
finally {
    if (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
