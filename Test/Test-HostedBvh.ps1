param(
    [ValidateSet('auto', 'msvc', 'wsl:gcc', 'wsl:clang')] [string]$Compiler = 'auto',
    [ValidateRange(1, 25)] [int]$Iterations = 9,
    [ValidateRange(8, 1200)] [int]$ImageWidth = 320,
    [ValidateRange(1, 500)] [int]$SamplesPerPixel = 16,
    [ValidateRange(1, 50)] [int]$MaxDepth = 16,
    [ValidateRange(0, 10)] [int]$WarmupCount = 2,
    [switch]$KeepWork,
    [string]$CompilerDll = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$exampleRoot = Join-Path $repositoryRoot 'examples/HostedIo'
$reportRoot = Join-Path $repositoryRoot 'artifacts/hosted-bvh'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('ctilde-hosted-bvh-' + [guid]::NewGuid().ToString('N'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($workRoot).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Benchmark work directory escaped the system temporary directory: $workRoot"
}

function Get-WslPath([string]$Path) {
    $result = & wsl --exec wslpath -a -u $Path
    if ($LASTEXITCODE -ne 0) { throw "Could not translate '$Path' to WSL." }
    return $result.Trim()
}

function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

function Write-Manifest([string]$Root) {
    $manifest = [ordered]@{
        target = 'hosted'; architecture = 'x64'; simdOptimizations = $true; sources = @('*.ct')
        build = [ordered]@{
            cLayout = 'modules'; generatedDirectory = 'build/generated/modules'
            generatedHeader = 'build/generated/ctilde_exports.h'; symbolMap = 'build/generated/ctilde_symbols.json'
            configuration = 'release'; compiler = $Compiler; executable = 'build/HostedBvhBenchmark.exe'
            lto = $true; optimization = 'speed'; cpuTarget = 'baseline'; floatingPoint = 'precise'
            pgo = [ordered]@{ mode = 'off'; directory = 'build/pgo' }
        }
    }
    [IO.File]::WriteAllText((Join-Path $Root 'ctilde.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

function Write-Program([string]$Root, [string]$Builder, [bool]$Census) {
    $body = if ($Census) {
@"
        long traced = camera.CountPacketPathSegments(world, RandomGenerator.DefaultRenderSeed);
        Console.WriteLine("TRACED_SEGMENTS:" + traced.ToString());
"@
    } else {
@"
        Console.WriteLine("RENDER_BEGIN");
        Stopwatch renderWatch = Stopwatch.StartNew();
        ParallelRenderSession session = new ParallelRenderSession(camera, world, RandomGenerator.DefaultRenderSeed);
        session.Start();
        session.Join();
        renderWatch.Stop();
        Console.WriteLine("RENDER_NANOSECONDS:" + renderWatch.ElapsedNanoseconds.ToString());
        Console.WriteLine("PIXEL_CHECKSUM:" + session.PixelChecksum().ToString());
"@
    }
    $stats = if ($Builder -eq 'BuildSahBvh') {
@"
        FlattenedSahBvh flat = (FlattenedSahBvh)world;
        Console.WriteLine("BVH_STATS:" + flat.NodeCount.ToString() + ":" + flat.LeafCount.ToString() + ":" + flat.MaximumDepth.ToString() + ":" + flat.PrimitiveCount.ToString());
"@
    } else { '        Console.WriteLine("BVH_STATS:-1:-1:-1:" + objects.Count.ToString());' }
    $program = @"
using System;
using System.Diagnostics;
namespace HostedIoExample;
public static class NativeBvhBenchmarkProgram
{
    [EntryPoint] public static void Main()
    {
        HittableList objects = Scene.CreateFinal(RandomGenerator.DefaultSceneSeed);
        Stopwatch buildWatch = Stopwatch.StartNew();
        Hittable world = objects.$Builder();
        buildWatch.Stop();
        Console.WriteLine("BUILD_NANOSECONDS:" + buildWatch.ElapsedNanoseconds.ToString());
$stats
        Camera camera = Scene.CreateBookCamera();
        camera.ImageWidth = $ImageWidth;
        camera.SamplesPerPixel = $SamplesPerPixel;
        camera.MaxDepth = $MaxDepth;
        camera.ProgressRows = 0;
$body
    }
}
"@
    [IO.File]::WriteAllText((Join-Path $Root 'Program.ct'), $program, [Text.UTF8Encoding]::new($false))
}

function Invoke-CtildeBuild([string]$Root) {
    Push-Location $Root
    try {
        $output = & dotnet $CompilerDll --project (Join-Path $Root 'ctilde.json') --build --trace 2>&1
        if ($LASTEXITCODE -ne 0) { throw "C~ build failed in '$Root':`n$($output -join "`n")" }
        return ($output -join "`n")
    } finally { Pop-Location }
}

function Invoke-Program([string]$Executable, [string]$Root) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.WorkingDirectory = $Root; $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        $start.FileName = 'wsl'; $start.ArgumentList.Add('--cd'); $start.ArgumentList.Add((Get-WslPath $Root))
        $start.ArgumentList.Add('--exec'); $start.ArgumentList.Add((Get-WslPath $Executable))
    } else { $start.FileName = $Executable }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $wall = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw "Could not start '$Executable'." }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $stdoutBuilder = [Text.StringBuilder]::new()
    $renderWallStart = $null; $renderCpuStart = $null
    while (($line = $process.StandardOutput.ReadLine()) -ne $null) {
        [void]$stdoutBuilder.AppendLine($line)
        if ($line -eq 'RENDER_BEGIN') {
            $renderWallStart = $wall.Elapsed.TotalMilliseconds
            $renderCpuStart = $process.TotalProcessorTime.TotalMilliseconds
        }
    }
    $process.WaitForExit(); $wall.Stop()
    $stdout = $stdoutBuilder.ToString(); $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Benchmark failed: $stdout`n$stderr" }
    $totalCpu = $process.TotalProcessorTime.TotalMilliseconds
    return [ordered]@{
        output = $stdout; wallMilliseconds = $wall.Elapsed.TotalMilliseconds; cpuMilliseconds = $totalCpu
        renderWallMilliseconds = if ($null -eq $renderWallStart) { $null } else { $wall.Elapsed.TotalMilliseconds - $renderWallStart }
        renderCpuMilliseconds = if ($null -eq $renderCpuStart) { $null } else { $totalCpu - $renderCpuStart }
    }
}

function New-Variant([hashtable]$Definition) {
    $root = Join-Path $workRoot $Definition.Name
    New-Item -ItemType Directory -Path $root | Out-Null
    Get-ChildItem $exampleRoot -Filter '*.ct' | Where-Object Name -ne 'Program.ct' | Copy-Item -Destination $root
    Write-Program $root $Definition.Builder $false; Write-Manifest $root
    $trace = Invoke-CtildeBuild $root
    $censusRoot = $root + '-census'; New-Item -ItemType Directory -Path $censusRoot | Out-Null
    Get-ChildItem $exampleRoot -Filter '*.ct' | Where-Object Name -ne 'Program.ct' | Copy-Item -Destination $censusRoot
    Write-Program $censusRoot $Definition.Builder $true; Write-Manifest $censusRoot
    $null = Invoke-CtildeBuild $censusRoot
    $census = Invoke-Program (Join-Path $censusRoot 'build/HostedBvhBenchmark.exe') $censusRoot
    if ($census.output -notmatch 'TRACED_SEGMENTS:(\d+)') { throw "Missing path census for $($Definition.Name)." }
    return [ordered]@{
        name = $Definition.Name; accelerator = $Definition.Accelerator; root = $root
        executable = (Join-Path $root 'build/HostedBvhBenchmark.exe'); tracedPathSegments = [long]$Matches[1]
        samples = [Collections.Generic.List[object]]::new()
        compileFlags = @([regex]::Matches($trace, 'trace: native compile flags (.+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Last 1)
        linkFlags = @([regex]::Matches($trace, 'trace: native link flags (.+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Last 1)
    }
}

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    if ([string]::IsNullOrWhiteSpace($CompilerDll)) {
        & dotnet build (Join-Path $repositoryRoot 'CTilde.Cli/CTilde.Cli.csproj') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Could not build the C~ CLI.' }
        $CompilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
    }
    $CompilerDll = [IO.Path]::GetFullPath($CompilerDll)
    $definitions = @(
        @{ Name = 'object-midpoint-bvh'; Accelerator = 'object-midpoint-bvh'; Builder = 'BuildMidpointBvh' },
        @{ Name = 'flattened-sah-bvh'; Accelerator = 'flattened-sah-bvh'; Builder = 'BuildSahBvh' }
    )
    $variants = @($definitions | ForEach-Object { New-Variant $_ })
    for ($warmup = 0; $warmup -lt $WarmupCount; $warmup++) {
        foreach ($variant in $(if (($warmup % 2) -eq 0) { $variants } else { @($variants[1], $variants[0]) })) {
            $null = Invoke-Program $variant.executable $variant.root
        }
    }
    for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
        foreach ($variant in $(if (($iteration % 2) -eq 0) { $variants } else { @($variants[1], $variants[0]) })) {
            $sample = Invoke-Program $variant.executable $variant.root
            if ($sample.output -notmatch 'BUILD_NANOSECONDS:(\d+)') { throw "Missing build timing for $($variant.name)." }
            $buildNs = [long]$Matches[1]
            if ($sample.output -notmatch 'RENDER_NANOSECONDS:(\d+)') { throw "Missing render timing for $($variant.name)." }
            $renderNs = [long]$Matches[1]
            if ($sample.output -notmatch 'PIXEL_CHECKSUM:(\d+)') { throw "Missing checksum for $($variant.name)." }
            $checksum = [uint64]$Matches[1]
            if ($sample.output -notmatch 'BVH_STATS:(-?\d+):(-?\d+):(-?\d+):(\d+)') { throw "Missing BVH statistics for $($variant.name)." }
            $variant.samples.Add([ordered]@{ iteration = $iteration; buildNanoseconds = $buildNs; renderNanoseconds = $renderNs; renderWallMilliseconds = $sample.renderWallMilliseconds; renderCpuMilliseconds = $sample.renderCpuMilliseconds; processWallMilliseconds = $sample.wallMilliseconds; processCpuMilliseconds = $sample.cpuMilliseconds; checksum = $checksum })
            $variant.pixelChecksum = $checksum
            $variant.statistics = [ordered]@{ nodeCount = [int]$Matches[1]; leafCount = [int]$Matches[2]; maximumDepth = [int]$Matches[3]; primitiveCount = [int]$Matches[4] }
        }
    }
    foreach ($variant in $variants) {
        $variant.medianBuildNanoseconds = Get-Median @($variant.samples | ForEach-Object { [double]$_.buildNanoseconds })
        $variant.medianRenderNanoseconds = Get-Median @($variant.samples | ForEach-Object { [double]$_.renderNanoseconds })
        $variant.medianRenderWallMilliseconds = Get-Median @($variant.samples | ForEach-Object { [double]$_.renderWallMilliseconds })
        $variant.medianRenderCpuMilliseconds = Get-Median @($variant.samples | ForEach-Object { [double]$_.renderCpuMilliseconds })
    }
    if ($variants[0].pixelChecksum -ne $variants[1].pixelChecksum) { throw 'BVH accelerators produced different pixel checksums.' }
    if ($variants[0].tracedPathSegments -ne $variants[1].tracedPathSegments) { throw 'BVH accelerators produced different path censuses.' }
    $report = [ordered]@{
        schemaVersion = 1; timestampUtc = [DateTime]::UtcNow.ToString('O'); draft = '0.44'; compiler = $Compiler
        profile = [ordered]@{ cLayout = 'modules'; configuration = 'release'; lto = $true; optimization = 'speed'; cpuTarget = 'baseline'; floatingPoint = 'precise'; pgo = 'off'; workers = 12 }
        imageWidth = $ImageWidth; samplesPerPixel = $SamplesPerPixel; maxDepth = $MaxDepth; warmupCount = $WarmupCount; iterations = $Iterations
        variants = $variants; speedups = [ordered]@{
            sahBuildOverMidpoint = $variants[0].medianBuildNanoseconds / $variants[1].medianBuildNanoseconds
            sahRenderOverMidpoint = $variants[0].medianRenderNanoseconds / $variants[1].medianRenderNanoseconds
        }
    }
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $reportPath = Join-Path $reportRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '.json')
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "Hosted BVH report: $reportPath"
    Write-Host ("SAH build: {0:N2}x; SAH render: {1:N2}x" -f $report.speedups.sahBuildOverMidpoint, $report.speedups.sahRenderOverMidpoint)
} finally {
    if ($KeepWork) { Write-Host "Benchmark work retained: $workRoot" }
    elseif (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
