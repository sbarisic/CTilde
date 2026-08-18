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
        Invoke-Checked "dotnet" @("build", ".\CTilde.sln", "--nologo")

        $hello = Join-Path $temporaryDirectory "hello.c"
        $exceptions = Join-Path $temporaryDirectory "exceptions.c"
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "--no-build", "--", ".\examples\Hello.ct", "-o", $hello, "--target", "esp-idf")
        Invoke-Checked "dotnet" @("run", "--project", ".\CTilde.Cli", "--no-build", "--", ".\examples\Exceptions.ct", "-o", $exceptions, "--target", "esp-idf")

        $xtensa = Find-Compiler (Join-Path $ToolsPath "xtensa-esp-elf") "xtensa-esp32-elf-gcc.exe"
        $riscv = Find-Compiler (Join-Path $ToolsPath "riscv32-esp-elf") "riscv32-esp-elf-gcc.exe"
        foreach ($compiler in @($xtensa, $riscv)) {
            foreach ($source in @($hello, $exceptions)) {
                Invoke-Checked $compiler @("-std=gnu23", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", "-I", (Join-Path $exampleDirectory "main"), $source)
                Write-Host "PASS $([IO.Path]::GetFileName($compiler)) $([IO.Path]::GetFileName($source))"
            }
        }

        if (-not $SkipFirmwareBuild) {
            $buildScript = Join-Path $exampleDirectory "Build.ps1"
            & $buildScript -IdfPath $IdfPath -Target esp32
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for esp32 with exit code $LASTEXITCODE." }
            & $buildScript -IdfPath $IdfPath -Target esp32c3
            if ($LASTEXITCODE -ne 0) { throw "$buildScript failed for esp32c3 with exit code $LASTEXITCODE." }
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
