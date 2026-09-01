param(
    [ValidateSet('auto', 'msvc', 'wsl:gcc', 'wsl:clang')]
    [string]$Compiler = 'auto',
    [ValidateRange(1, 25)] [int]$Iterations = 9,
    [ValidateRange(8, 1200)] [int]$ImageWidth = 320,
    [ValidateRange(1, 500)] [int]$SamplesPerPixel = 16,
    [ValidateRange(1, 50)] [int]$MaxDepth = 16,
    [ValidateRange(0, 10)] [int]$WarmupCount = 2,
    [ValidateRange(1, 10)] [int]$PgoTrainingRuns = 2,
    [switch]$ReportOnly,
    [switch]$KeepWork,
    [string]$CompilerDll = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$exampleRoot = Join-Path $repositoryRoot 'examples/HostedIo'
$reportRoot = Join-Path $repositoryRoot 'artifacts/hosted-simd'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("ctilde-hosted-simd-" + [guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($workRoot).StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Benchmark work directory escaped the system temporary directory: $workRoot"
}

function Get-WslPath([string]$Path) {
    $converted = & wsl --exec wslpath -a -u $Path
    if ($LASTEXITCODE -ne 0) { throw "Could not convert '$Path' to a WSL path." }
    return $converted.Trim()
}

function Get-MsvcTool([string]$Name) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    $installation = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($installation)) { return $null }
    return Get-ChildItem (Join-Path $installation 'VC\Tools\MSVC') -Filter ($Name + '.exe') -Recurse |
        Where-Object FullName -Match 'Hostx64\\x64' | Sort-Object FullName -Descending | Select-Object -First 1
}

function Invoke-CtildeBuild([string]$Root) {
    Push-Location $Root
    try {
        $output = & dotnet $CompilerDll --project (Join-Path $Root 'ctilde.json') --build --trace 2>&1
        if ($LASTEXITCODE -ne 0) { throw "CTilde native build failed in '$Root':`n$($output -join "`n")" }
        return ($output -join "`n")
    }
    finally { Pop-Location }
}

function Invoke-BenchmarkProgram([string]$Executable, [string]$WorkingDirectory) {
    $isWsl = $Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if (-not $isWsl) {
        $trainingEnvironment = Get-ChildItem (Join-Path $WorkingDirectory 'build/pgo') -Filter 'training-environment.txt' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $trainingEnvironment) {
            $profileDirectory = Split-Path -Parent $trainingEnvironment.FullName
            $start.Environment['VCPROFILE_PATH'] = $profileDirectory
        }
    }
    if ($isWsl) {
        $start.FileName = 'wsl'
        foreach ($argument in @('--cd', (Get-WslPath $WorkingDirectory), '--exec', '/usr/bin/time', '-f', 'CTILDE_TIME:%U:%S', (Get-WslPath $Executable))) {
            $start.ArgumentList.Add($argument)
        }
    }
    else { $start.FileName = $Executable }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $wall = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw "Could not start benchmark executable '$Executable'." }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $wall.Stop()
    if ($process.ExitCode -ne 0) { throw "Benchmark program failed with exit code $($process.ExitCode):`n$stdout`n$stderr" }
    $cpuMilliseconds = if ($isWsl) {
        if ($stderr -notmatch 'CTILDE_TIME:([0-9.]+):([0-9.]+)') { throw "WSL /usr/bin/time did not report process CPU time: $stderr" }
        1000.0 * ([double]$Matches[1] + [double]$Matches[2])
    } else { $process.TotalProcessorTime.TotalMilliseconds }
    return [ordered]@{ output = ($stdout + "`n" + $stderr); wallMilliseconds = $wall.Elapsed.TotalMilliseconds; cpuMilliseconds = $cpuMilliseconds }
}

function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

function Get-AssemblyEvidence([string]$Executable) {
    if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        $disassembly = (& wsl --exec objdump -d (Get-WslPath $Executable)) -join "`n"
    } else {
        $dumpbin = Get-MsvcTool 'dumpbin'
        $disassembly = if ($null -eq $dumpbin) { '' } else { (& $dumpbin.FullName /nologo /disasm $Executable) -join "`n" }
    }
    return [ordered]@{
        addPackedFloat = $disassembly.IndexOf('addps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        multiplyPackedFloat = $disassembly.IndexOf('mulps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        maskMove = $disassembly.IndexOf('movmskps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        intToFloat = $disassembly.IndexOf('cvtdq2ps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        avx = $disassembly.IndexOf('vaddps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        fma = $disassembly.IndexOf('vfmadd', [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
}

function Write-Program([string]$Root, [string]$Mode, [bool]$Census) {
    $body = if ($Census) {
        $method = if ($Mode -eq 'scalar') { 'CountScalarPathSegments' } else { 'CountPacketPathSegments' }
@"
        long traced = camera.$method(world, RandomGenerator.DefaultRenderSeed);
        Console.WriteLine("IMAGE_HEIGHT:" + camera.ImageHeight.ToString());
        Console.WriteLine("TRACED_SEGMENTS:" + traced.ToString());
"@
    } elseif ($Mode -eq 'parallel') {
@"
        ParallelRenderSession session = new ParallelRenderSession(camera, world, RandomGenerator.DefaultRenderSeed);
        session.Start();
        session.Join();
        Console.WriteLine("IMAGE_HEIGHT:" + camera.ImageHeight.ToString());
        Console.WriteLine("PIXEL_CHECKSUM:" + session.PixelChecksum().ToString());
        int worker = 0;
        while (worker < session.RenderWorkerCount)
        {
            Console.WriteLine("WORKER:" + worker.ToString() + ":" + session.WorkerElapsedNanoseconds(worker).ToString() + ":" + session.WorkerPixelCount(worker).ToString());
            worker++;
        }
"@
    } else {
        $method = if ($Mode -eq 'scalar') { 'RenderScalar' } else { 'Render' }
@"
        Rgba32[] pixels = new Rgba32[camera.ImageWidth * camera.ImageHeight];
        camera.$method(pixels, world, RandomGenerator.DefaultRenderSeed);
        Console.WriteLine("IMAGE_HEIGHT:" + camera.ImageHeight.ToString());
        Console.WriteLine("PIXEL_CHECKSUM:" + PixelBuffer.Checksum(pixels).ToString());
"@
    }
    $program = @"
using System;
namespace HostedIoExample;
public static class NativeBenchmarkProgram
{
    [EntryPoint] public static void Main()
    {
        HittableList objects = Scene.CreateFinal(RandomGenerator.DefaultSceneSeed);
        Hittable world = objects.BuildBvh();
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

function Write-Manifest([string]$Root, [string]$Cpu, [string]$FloatingPoint, [string]$PgoMode) {
    $manifest = [ordered]@{
        target = 'hosted'; architecture = 'x64'; simdOptimizations = $true; sources = @('*.ct')
        build = [ordered]@{
            cLayout = 'unity'; generatedC = 'build/generated/ctilde_program.c'; generatedHeader = 'build/generated/ctilde_exports.h'
            symbolMap = 'build/generated/ctilde_symbols.json'; configuration = 'release'; lto = $true; compiler = $Compiler
            executable = 'build/HostedIoBenchmark.exe'; optimization = 'speed'; cpuTarget = $Cpu; floatingPoint = $FloatingPoint
            pgo = [ordered]@{ mode = $PgoMode; directory = 'build/pgo' }
        }
    }
    [IO.File]::WriteAllText((Join-Path $Root 'ctilde.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

function New-Variant([hashtable]$Definition) {
    $root = Join-Path $workRoot $Definition.Name
    New-Item -ItemType Directory -Path $root | Out-Null
    Get-ChildItem $exampleRoot -Filter '*.ct' | Where-Object Name -ne 'Program.ct' | Copy-Item -Destination $root
    Write-Program $root $Definition.Mode $false
    Write-Manifest $root $Definition.Cpu $Definition.Fp $(if ($Definition.Pgo) { 'generate' } else { 'off' })
    $buildTrace = Invoke-CtildeBuild $root
    $executable = Join-Path $root 'build/HostedIoBenchmark.exe'
    if ($Definition.Pgo) {
        for ($training = 0; $training -lt $PgoTrainingRuns; $training++) { $null = Invoke-BenchmarkProgram $executable $root }
        Write-Manifest $root $Definition.Cpu $Definition.Fp 'use'
        $buildTrace = Invoke-CtildeBuild $root
    }

    $censusRoot = Join-Path $workRoot ($Definition.Name + '-census')
    New-Item -ItemType Directory -Path $censusRoot | Out-Null
    Get-ChildItem $exampleRoot -Filter '*.ct' | Where-Object Name -ne 'Program.ct' | Copy-Item -Destination $censusRoot
    Write-Program $censusRoot $Definition.Mode $true
    Write-Manifest $censusRoot $Definition.Cpu $Definition.Fp 'off'
    $null = Invoke-CtildeBuild $censusRoot
    $censusResult = Invoke-BenchmarkProgram (Join-Path $censusRoot 'build/HostedIoBenchmark.exe') $censusRoot
    if ($censusResult.output -notmatch 'TRACED_SEGMENTS:(\d+)') { throw "Census did not report traced segments: $($censusResult.output)" }
    $tracedSegments = [long]$Matches[1]

    $generated = @(Get-Item (Join-Path $root 'build/generated/ctilde_program.c'))
    $generatedText = ($generated | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    return [ordered]@{
        name = $Definition.Name; renderMode = $Definition.Mode; optimization = 'speed'; cpuTarget = $Definition.Cpu
        floatingPoint = $Definition.Fp; pgo = $(if ($Definition.Pgo) { 'use' } else { 'off' })
        workerCount = $(if ($Definition.Mode -eq 'parallel') { 12 } else { 1 }); accelerator = 'flattened-sah-bvh'
        root = $root; executable = $executable; tracedPathSegments = $tracedSegments; samples = [Collections.Generic.List[object]]::new()
        pixelChecksum = $null; executableBytes = (Get-Item $executable).Length; generatedCBytes = ($generated | Measure-Object Length -Sum).Sum
        resolvedCompileFlags = @([regex]::Matches($buildTrace, 'trace: native compile flags (.+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Last 1)
        resolvedLinkFlags = @([regex]::Matches($buildTrace, 'trace: native link flags (.+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Last 1)
        generatedEvidence = [ordered]@{
            addPs = $generatedText.Contains('_mm_add_ps', [StringComparison]::Ordinal)
            multiplyPs = $generatedText.Contains('_mm_mul_ps', [StringComparison]::Ordinal)
            fmaGuard = $generatedText.Contains('defined(_MSC_VER) && defined(__AVX2__)', [StringComparison]::Ordinal)
            packetType = $generatedText.Contains('ct_simd', [StringComparison]::Ordinal)
        }
        assemblyEvidence = Get-AssemblyEvidence $executable
    }
}

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    if ([string]::IsNullOrWhiteSpace($CompilerDll)) {
        & dotnet build (Join-Path $repositoryRoot 'CTilde.Cli/CTilde.Cli.csproj') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Could not build the CTilde CLI.' }
        $CompilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
    }
    $CompilerDll = [IO.Path]::GetFullPath($CompilerDll)
    if (-not (Test-Path -LiteralPath $CompilerDll)) { throw "The compiler DLL was not found: $CompilerDll" }

    $definitions = @(
        @{ Name = 'scalar-baseline-precise'; Mode = 'scalar'; Cpu = 'baseline'; Fp = 'precise'; Pgo = $false },
        @{ Name = 'packet-baseline-precise'; Mode = 'packet'; Cpu = 'baseline'; Fp = 'precise'; Pgo = $false },
        @{ Name = 'parallel-baseline-precise'; Mode = 'parallel'; Cpu = 'baseline'; Fp = 'precise'; Pgo = $false },
        @{ Name = 'parallel-avx2-precise'; Mode = 'parallel'; Cpu = 'avx2'; Fp = 'precise'; Pgo = $false },
        @{ Name = 'parallel-avx2-fast'; Mode = 'parallel'; Cpu = 'avx2'; Fp = 'fast'; Pgo = $false },
        @{ Name = 'parallel-avx2-precise-pgo'; Mode = 'parallel'; Cpu = 'avx2'; Fp = 'precise'; Pgo = $true },
        @{ Name = 'parallel-avx2-fast-pgo'; Mode = 'parallel'; Cpu = 'avx2'; Fp = 'fast'; Pgo = $true }
    )
    $variants = @($definitions | ForEach-Object { New-Variant $_ })
    $logicalProcessors = if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) { [int]((& wsl --exec nproc).Trim()) } else { [Environment]::ProcessorCount }

    foreach ($variant in $variants) {
        for ($warmup = 0; $warmup -lt $WarmupCount; $warmup++) { $null = Invoke-BenchmarkProgram $variant.executable $variant.root }
    }
    for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
        $shifted = @(0..($variants.Count - 1) | ForEach-Object { ($_ + $iteration) % $variants.Count })
        $order = if (($iteration % 2) -eq 0) { $shifted } else { @($shifted[($shifted.Count - 1)..0]) }
        foreach ($variantIndex in $order) {
            $variant = $variants[$variantIndex]
            $result = Invoke-BenchmarkProgram $variant.executable $variant.root
            if ($result.output -notmatch 'PIXEL_CHECKSUM:(\d+)') { throw "Benchmark did not report an RGBA checksum: $($result.output)" }
            $checksum = $Matches[1]
            if ($null -ne $variant.pixelChecksum -and $variant.pixelChecksum -ne $checksum) { throw "Variant '$($variant.name)' produced a non-deterministic checksum." }
            $variant.pixelChecksum = $checksum
            if ($result.output -notmatch 'IMAGE_HEIGHT:(\d+)') { throw 'Benchmark did not report image height.' }
            $imageHeight = [int]$Matches[1]
            $workers = @([regex]::Matches($result.output, 'WORKER:(\d+):(\d+):(\d+)') | ForEach-Object {
                [ordered]@{ index = [int]$_.Groups[1].Value; elapsedNanoseconds = [long]$_.Groups[2].Value; pixelCount = [int]$_.Groups[3].Value }
            })
            $primaryRays = [long]$ImageWidth * [long]$imageHeight * [long]$SamplesPerPixel
            $wallSeconds = $result.wallMilliseconds / 1000.0
            $variant.samples.Add([ordered]@{
                iteration = $iteration; wallMilliseconds = $result.wallMilliseconds; cpuMilliseconds = $result.cpuMilliseconds
                normalizedCpuUtilizationPercent = if ($result.wallMilliseconds -eq 0) { 0.0 } else { 100.0 * $result.cpuMilliseconds / ($result.wallMilliseconds * $logicalProcessors) }
                primaryRaysPerSecond = $primaryRays / $wallSeconds; tracedRaysPerSecond = $variant.tracedPathSegments / $wallSeconds; workers = $workers
            })
        }
    }

    foreach ($variant in $variants) {
        $variant.primaryRays = [long]$ImageWidth * [long]$imageHeight * [long]$SamplesPerPixel
        $variant.medianWallMilliseconds = Get-Median @($variant.samples | ForEach-Object { $_.wallMilliseconds })
        $variant.medianCpuMilliseconds = Get-Median @($variant.samples | ForEach-Object { $_.cpuMilliseconds })
        $variant.medianNormalizedCpuUtilizationPercent = Get-Median @($variant.samples | ForEach-Object { $_.normalizedCpuUtilizationPercent })
        $variant.medianPrimaryRaysPerSecond = Get-Median @($variant.samples | ForEach-Object { $_.primaryRaysPerSecond })
        $variant.medianTracedRaysPerSecond = Get-Median @($variant.samples | ForEach-Object { $_.tracedRaysPerSecond })
        $workerSamples = @($variant.samples | Where-Object { $_.workers.Count -gt 0 } | ForEach-Object { ,@($_.workers) })
        if ($workerSamples.Count -gt 0) {
            $workerMedians = @()
            for ($worker = 0; $worker -lt $variant.workerCount; $worker++) {
                $workerMedians += [ordered]@{ index = $worker; medianElapsedNanoseconds = Get-Median @($workerSamples | ForEach-Object { [double]$_[$worker].elapsedNanoseconds }); pixelCount = $workerSamples[0][$worker].pixelCount }
            }
            $elapsed = @($workerMedians | ForEach-Object { $_.medianElapsedNanoseconds })
            $variant.workerMedians = $workerMedians
            $variant.workerLoadImbalance = [ordered]@{
                maxToMin = ($elapsed | Measure-Object -Maximum).Maximum / ($elapsed | Measure-Object -Minimum).Minimum
                maxToMean = ($elapsed | Measure-Object -Maximum).Maximum / (($elapsed | Measure-Object -Average).Average)
            }
        }
        $null = $variant.Remove('root'); $null = $variant.Remove('executable')
    }

    $byName = @{}; foreach ($variant in $variants) { $byName[$variant.name] = $variant }
    foreach ($pair in @(@('parallel-avx2-precise', 'parallel-avx2-precise-pgo'), @('parallel-avx2-fast', 'parallel-avx2-fast-pgo'))) {
        if ($byName[$pair[0]].pixelChecksum -ne $byName[$pair[1]].pixelChecksum) { throw "PGO changed the checksum between '$($pair[0])' and '$($pair[1])'." }
    }
    $toolVersion = if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        (& wsl --exec $Compiler.Substring(4) --version | Select-Object -First 1)
    } else {
        $cl = Get-MsvcTool 'cl'; if ($null -eq $cl) { "MSVC selected through CTilde discovery ($Compiler)" } else { "MSVC cl.exe $($cl.VersionInfo.FileVersion)" }
    }
    $report = [ordered]@{
        schemaVersion = 2; timestampUtc = [DateTime]::UtcNow.ToString('O'); draft = '0.44'; machine = $env:COMPUTERNAME
        compiler = $Compiler; compilerVersion = $toolVersion; logicalProcessors = $logicalProcessors; iterations = $Iterations
        imageWidth = $ImageWidth; imageHeight = $imageHeight; samplesPerPixel = $SamplesPerPixel; maxDepth = $MaxDepth
        warmupCount = $WarmupCount; pgoTrainingRuns = $PgoTrainingRuns; accelerator = 'flattened-sah-bvh'
        comparisonAccelerators = @('object-midpoint-bvh'); variants = $variants
        speedups = [ordered]@{
            packetOverScalar = $byName['scalar-baseline-precise'].medianWallMilliseconds / $byName['packet-baseline-precise'].medianWallMilliseconds
            parallelOverPacket = $byName['packet-baseline-precise'].medianWallMilliseconds / $byName['parallel-baseline-precise'].medianWallMilliseconds
            avx2OverBaselineParallel = $byName['parallel-baseline-precise'].medianWallMilliseconds / $byName['parallel-avx2-precise'].medianWallMilliseconds
            precisePgo = $byName['parallel-avx2-precise'].medianWallMilliseconds / $byName['parallel-avx2-precise-pgo'].medianWallMilliseconds
            fastPgo = $byName['parallel-avx2-fast'].medianWallMilliseconds / $byName['parallel-avx2-fast-pgo'].medianWallMilliseconds
        }
    }
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $reportPath = Join-Path $reportRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '.json')
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "Hosted SIMD report: $reportPath"
    Write-Host ("Packet/scalar: {0:N2}x; parallel/packet: {1:N2}x; AVX2/baseline parallel: {2:N2}x" -f $report.speedups.packetOverScalar, $report.speedups.parallelOverPacket, $report.speedups.avx2OverBaselineParallel)
    if (-not $ReportOnly -and $report.speedups.packetOverScalar -le 1.0) { throw ("Hosted SIMD performance gate failed: packet/scalar speedup was {0:N2}x." -f $report.speedups.packetOverScalar) }
}
finally {
    if ($KeepWork) { Write-Host "Hosted SIMD work directory retained: $workRoot" }
    elseif (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
