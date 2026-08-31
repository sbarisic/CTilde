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
$fixtureRoot = Join-Path $PSScriptRoot 'Fixtures/NativeImport'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('ctilde-native-import-matrix-' + [guid]::NewGuid().ToString('N'))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ([IO.Path]::GetFullPath($workRoot).StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Matrix work directory escaped the system temporary directory: $workRoot"
}

function Get-WslPath([string]$Path) {
    $converted = & wsl --exec wslpath -a $Path
    if ($LASTEXITCODE -ne 0) { throw "Could not convert '$Path' to a WSL path." }
    return $converted.Trim()
}

function Build-MsvcFixture([string]$Root) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($installation)) { throw 'Visual Studio C tools were not found.' }
    $vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
    $commandFile = Join-Path $Root 'build-fixture.cmd'
    $source = Join-Path $Root 'fixture.c'
    $object = Join-Path $Root 'fixture.obj'
    $library = Join-Path $Root 'ctilde_native_import_fixture.dll'
    [IO.File]::WriteAllText($commandFile,
        "@echo off`r`ncall `"$vcvars`" >nul`r`ncl /nologo /std:clatest /LD /W4 /WX /Fo:`"$object`" /Fe:`"$library`" `"$source`"`r`n",
        [Text.Encoding]::ASCII)
    & cmd.exe /d /c $commandFile
    if ($LASTEXITCODE -ne 0) { throw 'MSVC native-import fixture build failed.' }
}

function Build-WslFixture([string]$Root, [string]$Compiler) {
    $library = Join-Path $Root 'libctilde_native_import_fixture.so'
    & wsl --exec $Compiler -std=gnu23 -shared -fPIC -Wall -Wextra -Werror -o (Get-WslPath $library) (Get-WslPath (Join-Path $Root 'fixture.c'))
    if ($LASTEXITCODE -ne 0) {
        & wsl --exec $Compiler -std=gnu2x -shared -fPIC -Wall -Wextra -Werror -o (Get-WslPath $library) (Get-WslPath (Join-Path $Root 'fixture.c'))
    }
    if ($LASTEXITCODE -ne 0) { throw "$Compiler native-import fixture build failed." }
}

try {
    foreach ($compiler in $Compilers) {
        foreach ($profile in @(
            @{ Name = 'release-unity'; Configuration = 'release'; Layout = 'unity'; Lto = $false },
            @{ Name = 'release-modules-lto'; Configuration = 'release'; Layout = 'modules'; Lto = $true })) {
            $label = ($compiler -replace ':', '-') + '-' + $profile.Name
            $root = Join-Path $workRoot $label
            [IO.Directory]::CreateDirectory($root) | Out-Null
            Copy-Item -LiteralPath (Join-Path $fixtureRoot 'Program.ct') -Destination $root
            Copy-Item -LiteralPath (Join-Path $fixtureRoot 'fixture.c') -Destination $root
            if ($compiler -eq 'msvc') { Build-MsvcFixture $root }
            else { Build-WslFixture $root $compiler.Substring(4) }

            $manifest = [ordered]@{
                target = 'hosted'
                architecture = 'x64'
                sources = @('Program.ct')
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
            [IO.File]::WriteAllText((Join-Path $root 'ctilde.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
            & dotnet $CompilerDll --project (Join-Path $root 'ctilde.json') --build
            if ($LASTEXITCODE -ne 0) { throw "Native-import matrix build failed: $label" }

            $executable = Join-Path $root "build/$label.exe"
            if ($compiler -eq 'msvc') {
                Copy-Item -LiteralPath (Join-Path $root 'ctilde_native_import_fixture.dll') -Destination (Split-Path -Parent $executable)
            }
            $output = if ($compiler.StartsWith('wsl:', [StringComparison]::OrdinalIgnoreCase)) {
                $linuxRoot = Get-WslPath $root
                & wsl --cd $linuxRoot --exec env "LD_LIBRARY_PATH=$linuxRoot" (Get-WslPath $executable)
            }
            else {
                & $executable
            }
            if ($LASTEXITCODE -ne 0 -or ($output -join "`n").Trim() -ne '42') {
                throw "Native-import matrix execution failed: $label ($($output -join ' | '))"
            }
            Write-Host "PASS $label"
        }
    }
}
finally {
    if (Test-Path $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
