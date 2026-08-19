[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [string]$ToolsPath = "C:\Espressif\tools",
    [switch]$SkipFirmwareBuild
)

$ErrorActionPreference = "Stop"
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$exampleDirectory = Join-Path $repositoryDirectory "examples\TCan485"
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ctilde-esp-tests-" + [guid]::NewGuid().ToString("N"))

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

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    Push-Location $repositoryDirectory
    try {
        # Build the compiler, CLI, and conformance project needed by this gate. The
        # editor may have the language-server output loaded while this script runs.
        Invoke-Checked "dotnet" @("build", ".\Test\Test.csproj", "-c", "Release", "--nologo")

        $hello = Join-Path $temporaryDirectory "hello.c"
        $exceptions = Join-Path $temporaryDirectory "exceptions.c"
        $arcHeap = Join-Path $temporaryDirectory "arc-heap.c"
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
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", ".\examples\TCan485\Program.ct", "-o", $arcHeap, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $mathSource, "-o", $math, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $operatorsSource, "-o", $operators, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $vectorsSource, "-o", $vectors, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", $assemblySource, "-o", $assembly, "--target", "esp-idf")

        $xtensa = Find-Compiler (Join-Path $ToolsPath "xtensa-esp-elf") "xtensa-esp32-elf-gcc.exe"
        $riscv = Find-Compiler (Join-Path $ToolsPath "riscv32-esp-elf") "riscv32-esp-elf-gcc.exe"
        foreach ($compiler in @($xtensa, $riscv)) {
            foreach ($source in @($hello, $exceptions, $arcHeap, $math, $operators, $vectors, $assembly)) {
                Invoke-Checked $compiler @(
                    "-std=gnu23", "-O2", "-Wall", "-Wextra", "-Werror", "-fsyntax-only",
                    "-I", (Join-Path $exampleDirectory "main"),
                    $source)
                Write-Host "PASS $([IO.Path]::GetFileName($compiler)) $([IO.Path]::GetFileName($source))"
            }
        }

        if (-not $SkipFirmwareBuild) {
            $buildScript = Join-Path $exampleDirectory "Build.ps1"
            & $buildScript -IdfPath $IdfPath -Target esp32
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for esp32 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32c3
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for esp32c3 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32 -Source $assemblySource
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for inline assembly on esp32 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32c3 -Source $assemblySource
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for inline assembly on esp32c3 with exit code $LASTEXITCODE." }
            Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--", "--project", ".\examples\TCan485\ctilde.json")
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
