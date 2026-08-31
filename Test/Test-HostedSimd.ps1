param(
    [ValidateSet('auto', 'msvc', 'wsl:gcc', 'wsl:clang')]
    [string]$Compiler = 'auto',
    [ValidateRange(1, 25)]
    [int]$Iterations = 9,
    [ValidateRange(8, 1200)]
    [int]$ImageWidth = 320,
    [ValidateRange(1, 500)]
    [int]$SamplesPerPixel = 16,
    [ValidateRange(1, 50)]
    [int]$MaxDepth = 16,
    [ValidateRange(0, 10)]
    [int]$WarmupCount = 2,
    [switch]$ReportOnly
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
    $start = @{
        FilePath = $FileName
        WorkingDirectory = $WorkingDirectory
        Wait = $true
        PassThru = $true
        NoNewWindow = $true
    }
    if ($Arguments.Count -ne 0) { $start.ArgumentList = $Arguments }
    $process = Start-Process @start
    if ($process.ExitCode -ne 0) {
        throw "Command failed with exit code $($process.ExitCode): $FileName $($Arguments -join ' ')"
    }
}

function Get-WslPath([string]$Path) {
    $converted = & wsl --exec wslpath -a $Path
    if ($LASTEXITCODE -ne 0) { throw "Could not convert '$Path' to a WSL path." }
    return $converted.Trim()
}

function Get-MsvcTool([string]$Name) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    $installation = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($installation)) { return $null }
    return Get-ChildItem (Join-Path $installation 'VC\Tools\MSVC') -Filter ($Name + '.exe') -Recurse |
        Where-Object FullName -Match 'Hostx64\\x64' | Sort-Object FullName -Descending |
        Select-Object -First 1
}

function Get-AssemblyEvidence([string]$Executable) {
    if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        $disassembly = (& wsl --exec objdump -d (Get-WslPath $Executable)) -join "`n"
    }
    else {
        $dumpbin = Get-MsvcTool 'dumpbin'
        $disassembly = if ($null -eq $dumpbin) { '' } else { (& $dumpbin.FullName /nologo /disasm $Executable) -join "`n" }
    }
    return [ordered]@{
        addPackedFloat = $disassembly.IndexOf('addps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        multiplyPackedFloat = $disassembly.IndexOf('mulps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        unsignedMultiply = $disassembly.IndexOf('pmuludq', [StringComparison]::OrdinalIgnoreCase) -ge 0
        maskMove = $disassembly.IndexOf('movmskps', [StringComparison]::OrdinalIgnoreCase) -ge 0
        intToFloat = $disassembly.IndexOf('cvtdq2ps', [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
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

function New-Variant([string]$Name, [bool]$Enabled, [bool]$PacketRenderer) {
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
        camera.$(if ($PacketRenderer) { 'Render' } else { 'RenderScalar' })(image, world, RandomGenerator.DefaultRenderSeed);
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
    foreach ($variant in @(
        @{ Name = 'scalar'; Enabled = $false; Packet = $false },
        @{ Name = 'packet'; Enabled = $true; Packet = $true })) {
        $root = New-Variant $variant.Name $variant.Enabled $variant.Packet
        Invoke-Checked 'dotnet' @($compilerDll, '--project', (Join-Path $root 'ctilde.json'), '--build') $root
        $executable = Join-Path $root 'build/HostedIoBenchmark.exe'
        $image = Join-Path $root 'image.ppm'
        $generated = Get-ChildItem (Join-Path $root 'build/generated/modules') -Filter '*.c'
        $generatedText = ($generated | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
        $symbolMapText = [IO.File]::ReadAllText((Join-Path $root 'build/generated/ctilde_symbols.json'))
        $variants += [ordered]@{
            name = $variant.Name
            simdOptimizations = $variant.Enabled
            packetRenderer = $variant.Packet
            root = $root
            executable = $executable
            milliseconds = [Collections.Generic.List[double]]::new()
            imageSha256 = $null
            executableBytes = (Get-Item $executable).Length
            generatedCBytes = ($generated | Measure-Object Length -Sum).Sum
            instructionEvidence = [ordered]@{
                addPs = $generatedText.IndexOf('_mm_add_ps', [StringComparison]::Ordinal) -ge 0
                multiplyPs = $generatedText.IndexOf('_mm_mul_ps', [StringComparison]::Ordinal) -ge 0
                unalignedLoad = $generatedText.IndexOf('_mm_loadu_ps', [StringComparison]::Ordinal) -ge 0
                safePackedLoad = $generatedText.IndexOf('_mm_setr_ps', [StringComparison]::Ordinal) -ge 0
                unsignedMultiply = $generatedText.IndexOf('_mm_mul_epu32', [StringComparison]::Ordinal) -ge 0
                packedCompare = $generatedText.IndexOf('_mm_cmp', [StringComparison]::Ordinal) -ge 0
                unsignedToFloat = $generatedText.IndexOf('_mm_cvtepi32_ps', [StringComparison]::Ordinal) -ge 0
                packetType = $symbolMapText.IndexOf('System.Simd.Vec3x4', [StringComparison]::Ordinal) -ge 0
            }
            assemblyEvidence = Get-AssemblyEvidence $executable
        }
    }

    foreach ($variant in $variants) {
        for ($warmup = 0; $warmup -lt $WarmupCount; $warmup++) {
            Invoke-BenchmarkProgram $variant.executable $variant.root
        }
    }
    $optimizedWins = 0
    for ($index = 0; $index -lt $Iterations; $index++) {
        $order = if (($index % 2) -eq 0) { @(0, 1) } else { @(1, 0) }
        $pair = @{}
        foreach ($variantIndex in $order) {
            $variant = $variants[$variantIndex]
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            Invoke-BenchmarkProgram $variant.executable $variant.root
            $stopwatch.Stop()
            $variant.milliseconds.Add($stopwatch.Elapsed.TotalMilliseconds)
            $pair[$variant.name] = $stopwatch.Elapsed.TotalMilliseconds
        }
        if ($pair.packet -lt $pair.scalar) { $optimizedWins++ }
    }
    foreach ($variant in $variants) {
        $variant.medianMilliseconds = Get-Median $variant.milliseconds.ToArray()
        $variant.imageSha256 = (Get-FileHash (Join-Path $variant.root 'image.ppm') -Algorithm SHA256).Hash
        $variant.Remove('root')
        $variant.Remove('executable')
    }
    $toolVersion = if ($Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
        (& wsl --exec $Compiler.Substring(4) --version | Select-Object -First 1)
    } else {
        $cl = Get-MsvcTool 'cl'
        if ($null -eq $cl) { "MSVC selected through CTilde compiler discovery ($Compiler)" }
        else { "MSVC cl.exe $($cl.VersionInfo.FileVersion)" }
    }
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
        warmupCount = $WarmupCount
        variants = $variants
        speedup = $variants[0].medianMilliseconds / $variants[1].medianMilliseconds
        optimizedWins = $optimizedWins
        requiredWins = [Math]::Min(7, $Iterations)
    }
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $reportPath = Join-Path $reportRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + ".json")
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host "Hosted SIMD report: $reportPath"
    Write-Host ("Scalar median: {0:N2} ms; packet median: {1:N2} ms; speedup: {2:N2}x" -f
        $variants[0].medianMilliseconds, $variants[1].medianMilliseconds, $report.speedup)
    Write-Host ("Optimized wins: {0}/{1}" -f $optimizedWins, $Iterations)
    if (-not $ReportOnly -and -not $Compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase) -and
        ($report.speedup -le 1.0 -or $optimizedWins -lt [Math]::Min(7, $Iterations))) {
        throw ("Hosted SIMD performance gate failed: {0:N2}x speedup and {1}/{2} optimized wins." -f
            $report.speedup, $optimizedWins, $Iterations)
    }
}
finally {
    if (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
