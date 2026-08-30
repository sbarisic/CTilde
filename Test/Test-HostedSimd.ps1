param(
    [ValidateSet('auto', 'msvc', 'wsl:gcc', 'wsl:clang')]
    [string]$Compiler = 'auto',
    [ValidateRange(1, 25)]
    [int]$Iterations = 5,
    [ValidateRange(8, 1200)]
    [int]$ImageWidth = 160,
    [ValidateRange(1, 500)]
    [int]$SamplesPerPixel = 4,
    [ValidateRange(1, 50)]
    [int]$MaxDepth = 8
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
$exampleRoot = Join-Path $repositoryRoot 'examples/HostedIo'
$reportRoot = Join-Path $repositoryRoot 'artifacts/hosted-simd'
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ctilde-hosted-simd-" + [guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($workRoot).StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Benchmark work directory escaped the system temporary directory: $workRoot"
}

function Invoke-Checked([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory) {
    $process = Start-Process -FilePath $FileName -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "Command failed with exit code $($process.ExitCode): $FileName $($Arguments -join ' ')"
    }
}

function Get-WslPath([string]$Path) {
    $converted = & wsl --exec wslpath -a $Path
    if ($LASTEXITCODE -ne 0) { throw "Could not convert '$Path' to a WSL path." }
    return $converted.Trim()
}

function Invoke-BenchmarkProgram([string]$Executable, [string]$WorkingDirectory) {
    if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        Invoke-Checked 'wsl' @('--cd', (Get-WslPath $WorkingDirectory), '--exec', (Get-WslPath $Executable)) $WorkingDirectory
    }
    else {
        Invoke-Checked $Executable @() $WorkingDirectory
    }
}

function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

function New-Variant([string]$Name, [bool]$Enabled) {
    $root = Join-Path $workRoot $Name
    New-Item -ItemType Directory -Path $root | Out-Null
    Get-ChildItem $exampleRoot -Filter '*.ct' | Where-Object Name -ne 'Program.ct' | Copy-Item -Destination $root
    $program = @"
using System;
using System.IO;
namespace HostedIoExample;
public static class SimdBenchmarkProgram
{
    [EntryPoint] public static void Main()
    {
        FileHandle image = File.Open("image.ppm", FileMode.Create, FileAccess.Write);
        defer File.Close(image);
        HittableList objects = Scene.CreateFinal(RandomGenerator.DefaultSceneSeed);
        Hittable world = objects.BuildBvh();
        Camera camera = Scene.CreateBookCamera();
        camera.ImageWidth = $ImageWidth;
        camera.SamplesPerPixel = $SamplesPerPixel;
        camera.MaxDepth = $MaxDepth;
        camera.ProgressRows = 0;
        camera.Render(image, world, RandomGenerator.DefaultRenderSeed);
    }
}
"@
    [IO.File]::WriteAllText((Join-Path $root 'Program.ct'), $program, [Text.UTF8Encoding]::new($false))
    $manifest = [ordered]@{
        target = 'hosted'
        architecture = 'x64'
        simdOptimizations = $Enabled
        sources = @('*.ct')
        build = [ordered]@{
            cLayout = 'modules'
            generatedDirectory = 'build/generated/modules'
            generatedHeader = 'build/generated/ctilde_exports.h'
            symbolMap = 'build/generated/ctilde_symbols.json'
            configuration = 'release'
            lto = $true
            compiler = $Compiler
            executable = 'build/HostedIoBenchmark.exe'
        }
    }
    [IO.File]::WriteAllText((Join-Path $root 'ctilde.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    return $root
}

try {
    if (-not (Test-Path $compilerDll)) {
        Invoke-Checked 'dotnet' @('build', (Join-Path $repositoryRoot 'CTilde.sln'), '-c', 'Release') $repositoryRoot
    }
    $variants = @()
    foreach ($variant in @(@{ Name = 'disabled'; Enabled = $false }, @{ Name = 'enabled'; Enabled = $true })) {
        $root = New-Variant $variant.Name $variant.Enabled
        Invoke-Checked 'dotnet' @($compilerDll, '--project', (Join-Path $root 'ctilde.json'), '--build') $root
        $executable = Join-Path $root 'build/HostedIoBenchmark.exe'
        Invoke-BenchmarkProgram $executable $root
        $times = [Collections.Generic.List[double]]::new()
        for ($index = 0; $index -lt $Iterations; $index++) {
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            Invoke-BenchmarkProgram $executable $root
            $stopwatch.Stop()
            $times.Add($stopwatch.Elapsed.TotalMilliseconds)
        }
        $image = Join-Path $root 'image.ppm'
        $generated = Get-ChildItem (Join-Path $root 'build/generated/modules') -Filter '*.c'
        $generatedText = ($generated | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
        $symbolMapText = [IO.File]::ReadAllText((Join-Path $root 'build/generated/ctilde_symbols.json'))
        $variants += [ordered]@{
            name = $variant.Name
            simdOptimizations = $variant.Enabled
            milliseconds = @($times)
            medianMilliseconds = Get-Median $times.ToArray()
            imageSha256 = (Get-FileHash $image -Algorithm SHA256).Hash
            executableBytes = (Get-Item $executable).Length
            generatedCBytes = ($generated | Measure-Object Length -Sum).Sum
            instructionEvidence = [ordered]@{
                addPs = $generatedText.Contains('_mm_add_ps', [StringComparison]::Ordinal)
                multiplyPs = $generatedText.Contains('_mm_mul_ps', [StringComparison]::Ordinal)
                unalignedLoad = $generatedText.Contains('_mm_loadu_ps', [StringComparison]::Ordinal)
                safePackedLoad = $generatedText.Contains('_mm_setr_ps', [StringComparison]::Ordinal)
                packetType = $symbolMapText.Contains('System.Simd.Vec3x4', [StringComparison]::Ordinal)
            }
        }
    }
    if ($variants[0].imageSha256 -ne $variants[1].imageSha256) {
        throw "Disabled and enabled builds produced different seeded images."
    }
    $toolVersion = if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        (& wsl --exec $Compiler.Substring(4) --version | Select-Object -First 1)
    } else { "MSVC selected through CTilde compiler discovery ($Compiler)" }
    $report = [ordered]@{
        timestampUtc = [DateTime]::UtcNow.ToString('O')
        draft = '0.38'
        machine = $env:COMPUTERNAME
        compiler = $Compiler
        compilerVersion = $toolVersion
        iterations = $Iterations
        imageWidth = $ImageWidth
        samplesPerPixel = $SamplesPerPixel
        maxDepth = $MaxDepth
        variants = $variants
        speedup = $variants[0].medianMilliseconds / $variants[1].medianMilliseconds
    }
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $reportPath = Join-Path $reportRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + ".json")
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host "Hosted SIMD report: $reportPath"
    Write-Host ("Disabled median: {0:N2} ms; enabled median: {1:N2} ms; speedup: {2:N2}x" -f
        $variants[0].medianMilliseconds, $variants[1].medianMilliseconds, $report.speedup)
}
finally {
    if (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
