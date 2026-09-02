param(
    [ValidateSet('msvc', 'wsl:gcc', 'wsl:clang')]
    [string[]]$Compilers = @('msvc', 'wsl:gcc', 'wsl:clang'),
    [string]$CompilerDll = ''
)

$ErrorActionPreference = 'Stop'
$exampleRoot = $PSScriptRoot
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $exampleRoot)
$manifest = Join-Path $exampleRoot 'ctilde.json'
$pluginSource = Join-Path $exampleRoot 'native/plugin.c'
$buildRoot = Join-Path $exampleRoot 'build/native-import'

if ([string]::IsNullOrWhiteSpace($CompilerDll)) {
    & dotnet build (Join-Path $repositoryRoot 'CTilde.Cli/CTilde.Cli.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The Release compiler build failed.' }
    $CompilerDll = Join-Path $repositoryRoot 'CTilde.Cli/bin/Release/net10.0/ctilde.dll'
}
$CompilerDll = [IO.Path]::GetFullPath($CompilerDll)
if (-not (Test-Path -LiteralPath $CompilerDll)) { throw "The compiler DLL was not found: $CompilerDll" }

function Get-WslPath([string]$Path) {
    $converted = & wsl --exec wslpath -a ([IO.Path]::GetFullPath($Path))
    if ($LASTEXITCODE -ne 0) { throw "Could not convert '$Path' to a WSL path." }
    return $converted.Trim()
}

function Build-MsvcPlugin([string]$OutputDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe was not found.' }
    $installation = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($installation)) { throw 'Visual Studio C tools were not found.' }

    $vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
    $commandFile = Join-Path $OutputDirectory 'build-plugin.cmd'
    $object = Join-Path $OutputDirectory 'plugin.obj'
    $library = Join-Path $OutputDirectory 'ctilde_example_plugin.dll'
    [IO.File]::WriteAllText($commandFile,
        "@echo off`r`ncall `"$vcvars`" >nul`r`ncl /nologo /std:clatest /LD /O2 /W4 /WX /Fo:`"$object`" /Fe:`"$library`" `"$pluginSource`"`r`n",
        [Text.Encoding]::ASCII)
    & cmd.exe /d /c $commandFile
    if ($LASTEXITCODE -ne 0) { throw 'The MSVC plug-in build failed.' }
}

function Build-WslPlugin([string]$OutputDirectory, [string]$Compiler) {
    $library = Join-Path $OutputDirectory 'libctilde_example_plugin.so'
    & wsl --exec $Compiler -std=c17 -shared -fPIC -O2 -fvisibility=hidden -Wall -Wextra -Werror -o (Get-WslPath $library) (Get-WslPath $pluginSource)
    if ($LASTEXITCODE -ne 0) { throw "The WSL $Compiler plug-in build failed." }
}

$expected = @(
    'native module api: 1',
    'native module sum: 42',
    'native module state: 1',
    'native module state: 2'
) -join "`n"

foreach ($compiler in $Compilers) {
    $label = $compiler.Replace(':', '-')
    $outputDirectory = Join-Path $buildRoot $label
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $executable = Join-Path $outputDirectory 'HostedNativeImport.exe'

    if ($compiler -eq 'msvc') {
        Build-MsvcPlugin $outputDirectory
    }
    else {
        Build-WslPlugin $outputDirectory $compiler.Substring(4)
    }

    & dotnet $CompilerDll --project $manifest --build --compiler $compiler --configuration release --native-output $executable
    if ($LASTEXITCODE -ne 0) { throw "The C~ $compiler build failed." }

    $output = if ($compiler -eq 'msvc') {
        & $executable
    }
    else {
        $wslOutputDirectory = Get-WslPath $outputDirectory
        & wsl --cd $wslOutputDirectory --exec env "LD_LIBRARY_PATH=$wslOutputDirectory" (Get-WslPath $executable)
    }
    if ($LASTEXITCODE -ne 0) { throw "The $compiler example exited with code $LASTEXITCODE." }

    $actual = ($output | ForEach-Object { $_.ToString().TrimEnd() }) -join "`n"
    if ($actual.Trim() -ne $expected) {
        throw "The $compiler example output did not match.`nExpected:`n$expected`nActual:`n$actual"
    }
    Write-Host "PASS $compiler hosted native module loading"
}
