[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [string]$ToolsPath = "C:\Espressif\tools",
    [switch]$SkipFirmwareBuild,
    [switch]$AcceptMemoryBaseline
)

$ErrorActionPreference = "Stop"
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$exampleDirectory = Join-Path $repositoryDirectory "examples\TCan485"
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ctilde-esp-tests-" + [guid]::NewGuid().ToString("N"))
$memoryBaselinePath = Join-Path $PSScriptRoot "Baselines\esp-idf-memory.json"
$memorySupportPath = Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"

function Find-Compiler([string]$root, [string]$name) {
    $compiler = Get-ChildItem -LiteralPath $root -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $compiler) {
        throw "Compiler $name was not found under $root."
    }
    return $compiler.FullName
}

function Invoke-Checked([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$file failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Captured([string]$file, [string[]]$arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $file @arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "$file failed with exit code $LASTEXITCODE.`n$output" }
        return $output
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Write-Utf8NoBom([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}

function Test-MemoryBudget([string]$target, [string]$compiler) {
    Push-Location $exampleDirectory
    try {
        $sizeOutput = Invoke-Captured "idf.py" @("size")
    }
    finally {
        Pop-Location
    }
    $sizePath = Join-Path $temporaryDirectory "size-$target.txt"
    Write-Utf8NoBom $sizePath $sizeOutput
    $parseSize = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.parseIdfSize(fs.readFileSync(process.argv[2],'utf8'))))))"
    $sizeJson = Invoke-Captured "node" @("-e", $parseSize, $memorySupportPath, $sizePath)
    $size = $sizeJson | ConvertFrom-Json
    $actual = [ordered]@{
        tools = [ordered]@{
            idf = (Invoke-Captured "idf.py" @("--version")).Trim()
            compiler = (Invoke-Captured $compiler @("-dumpfullversion")).Trim()
        }
        targets = [ordered]@{
            $target = [ordered]@{
                binaryBytes = [int64]$size.binaryBytes
                imageBytes = [int64]$size.imageBytes
                flashCode = [int64]$size.sections.flashCode
                flashData = [int64]$size.sections.flashData
                iram = [int64]$size.sections.iram
                dram = [int64]$size.sections.dram
            }
        }
    }
    $actualPath = Join-Path $temporaryDirectory "memory-$target.json"
    Write-Utf8NoBom $actualPath (($actual | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
    if ($AcceptMemoryBaseline) {
        if (-not (Test-Path -LiteralPath $memoryBaselinePath)) {
            throw "Physical ESP32 measurements must establish $memoryBaselinePath before cross-target rebaselining."
        }
        $update = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>{const old=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));const actual=JSON.parse(fs.readFileSync(process.argv[3],'utf8'));process.stdout.write(m.serializeHardwareReport(m.updateMemoryBaseline(old,actual)));}))"
        $updated = Invoke-Captured "node" @("-e", $update, $memorySupportPath, $memoryBaselinePath, $actualPath)
        Write-Utf8NoBom $memoryBaselinePath $updated
    }
    else {
        if (-not (Test-Path -LiteralPath $memoryBaselinePath)) {
            throw "ESP memory baseline is missing; run connected acceptance with -AcceptMemoryBaseline first."
        }
        $validate = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>m.validateMemoryBaseline(JSON.parse(fs.readFileSync(process.argv[2],'utf8')),JSON.parse(fs.readFileSync(process.argv[3],'utf8')))))"
        Invoke-Checked "node" @("-e", $validate, $memorySupportPath, $memoryBaselinePath, $actualPath)
    }
    Write-Host "PASS $target memory budget"
}

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    Push-Location $repositoryDirectory
    try {
        # Build the compiler, CLI, and conformance project needed by this gate. The
        # editor may have the language-server output loaded while this script runs.
        Invoke-Checked "dotnet" @("build", ".\Test\Test.csproj", "-c", "Release", "--nologo")

        $hello = Join-Path $temporaryDirectory "hello.c"
        $exceptions = Join-Path $temporaryDirectory "exceptions.c"
        $mathSource = Join-Path $temporaryDirectory "math.ct"
        $math = Join-Path $temporaryDirectory "math.c"
        $operatorsSource = Join-Path $temporaryDirectory "operators.ct"
        $operators = Join-Path $temporaryDirectory "operators.c"
        $vectorsSource = Join-Path $temporaryDirectory "vectors.ct"
        $vectors = Join-Path $temporaryDirectory "vectors.c"
        $assemblySource = Join-Path $temporaryDirectory "inline-assembly.ct"
        $assembly = Join-Path $temporaryDirectory "inline-assembly.c"
        [IO.File]::WriteAllText($mathSource, 'public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(Math.Sqrt(9.0f) + Math.Abs(-1.0f) + Math.Tan(0.0f) + Math.Min(1.0f, 2.0f) + Math.Max(1.0f, 2.0f) + Math.Sin(0.0f) + Math.Cos(0.0f) + Math.Floor(1.5f) + Math.Ceiling(1.5f) + Math.Pi); } }')
        [IO.File]::WriteAllText($operatorsSource, 'public struct Vector2 { public float X; public float Y; public Vector2(float x, float y) { X = x; Y = y; } public static Vector2 operator +(Vector2 left, Vector2 right) { return new Vector2(left.X + right.X, left.Y + right.Y); } public static Vector2 operator *(Vector2 value, float scale) { return new Vector2(value.X * scale, value.Y * scale); } } public static class Program { [EntryPoint] public static void Main() { Vector2 value = new Vector2(1.0f, 2.0f); value += new Vector2(2.0f, 3.0f); Console.WriteLine((value * 2.0f).X); } }')
        [IO.File]::WriteAllText($vectorsSource, 'public static class Program { [EntryPoint] public static void Main() { Vec2 two = Vec2.UnitX + Vec2.UnitY; Vec3 three = Vec3.UnitX.Cross(Vec3.UnitY).Normalize(); Vec4 four = Vec4.One * new Vec4(1.0f, 2.0f, 3.0f, 4.0f); Console.WriteLine(two.Dot(two) + three.Z + four.W); } }')
        [IO.File]::WriteAllText($assemblySource, 'public static class Program { [Export("ctilde_add")] public static int Add(int left, int right) { return left + right; } [EntryPoint] public static unsafe void Main() { int value = 1; [NoAlloc] asm (ref value) { } [NoAlloc] asm { nop } Console.WriteLine(value); } }')
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", ".\examples\Hello.ct", "-o", $hello, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", ".\examples\Exceptions.ct", "-o", $exceptions, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $mathSource, "-o", $math, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $operatorsSource, "-o", $operators, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $vectorsSource, "-o", $vectors, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $assemblySource, "-o", $assembly, "--target", "esp-idf")

        $xtensa = Find-Compiler (Join-Path $ToolsPath "xtensa-esp-elf") "xtensa-esp32-elf-gcc.exe"
        $riscv = Find-Compiler (Join-Path $ToolsPath "riscv32-esp-elf") "riscv32-esp-elf-gcc.exe"
        foreach ($compiler in @($xtensa, $riscv)) {
            # These standalone checks intentionally use only the portable shim include
            # directory. Runtime-backed Thread/Mutex code requires ESP-IDF's FreeRTOS
            # headers and configuration and is therefore validated by the complete
            # firmware builds below instead of this context-free syntax pass.
            foreach ($source in @($hello, $exceptions, $math, $operators, $vectors, $assembly)) {
                Invoke-Checked $compiler @(
                    "-std=gnu23", "-O2", "-Wall", "-Wextra", "-Werror", "-fsyntax-only",
                    "-I", (Join-Path $exampleDirectory "main"),
                    $source)
                Write-Host "PASS $([IO.Path]::GetFileName($compiler)) $([IO.Path]::GetFileName($source))"
            }
        }

        if (-not $SkipFirmwareBuild) {
            $buildScript = Join-Path $exampleDirectory "Build.ps1"
            & $buildScript -IdfPath $IdfPath -Target esp32 -Clean
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for esp32 with exit code $LASTEXITCODE." }
            Test-MemoryBudget "esp32" $xtensa
            & $buildScript -IdfPath $IdfPath -Target esp32c3
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for esp32c3 with exit code $LASTEXITCODE." }
            Test-MemoryBudget "esp32c3" $riscv
            & $buildScript -IdfPath $IdfPath -Target esp32 -Source $assemblySource
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for inline assembly on esp32 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32c3 -Source $assemblySource
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for inline assembly on esp32c3 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32 -Source (Join-Path $exampleDirectory "Draft023Validation.ct")
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for Draft 0.23 interrupts on esp32 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32c3 -Source (Join-Path $exampleDirectory "Draft023Validation.ct")
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for Draft 0.23 interrupts on esp32c3 with exit code $LASTEXITCODE." }
            Push-Location $exampleDirectory
            try {
                Invoke-Checked "idf.py" @("set-target", "esp32")
            }
            finally {
                Pop-Location
            }
            Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", "--project", ".\examples\TCan485\ctilde.json", "--generate-bindings")
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
