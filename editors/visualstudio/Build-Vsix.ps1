param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'CTilde.VisualStudio/CTilde.VisualStudio.csproj'
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$vsix = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "CTilde.VisualStudio/bin/$Configuration") -Filter '*.vsix' -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $vsix) { throw 'The Visual Studio build did not produce a VSIX.' }
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$destination = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'CTilde.VisualStudio-0.14.0.vsix'
Copy-Item -LiteralPath $vsix.FullName -Destination $destination -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($destination)
try {
    $requiredEntries = @(
        'CTilde.VisualStudio.dll',
        'CTilde.VisualStudio.Core.dll',
        'Microsoft.VisualStudio.LanguageServer.Client.dll',
        'Microsoft.Bcl.AsyncInterfaces.dll',
        'System.Buffers.dll',
        'System.IO.Pipelines.dll',
        'System.Memory.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll',
        'System.Text.Encodings.Web.dll',
        'System.Text.Json.dll',
        'System.Threading.Tasks.Extensions.dll',
        'CTilde.VisualStudio.pkgdef',
        'Grammars/ctilde.tmLanguage.tmTheme',
        'debug-adapter.pkgdef',
        'Tools/DebugAdapter/CTilde.DebugAdapter.exe',
        'Tools/DebugAdapter/CTilde.DebugAdapter.dll',
        'Tools/DebugAdapter/CTilde.DebugAdapter.runtimeconfig.json',
        'Tools/DebugAdapter/Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll'
    )
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $missingEntries = @($requiredEntries | Where-Object { $_ -notin $entryNames })
    if ($missingEntries.Count -ne 0) {
        throw "The VSIX is missing required private payloads: $($missingEntries -join ', ')."
    }
    $pkgdefEntry = $archive.Entries | Where-Object FullName -eq 'CTilde.VisualStudio.pkgdef' | Select-Object -First 1
    $reader = [System.IO.StreamReader]::new($pkgdefEntry.Open())
    try { $pkgdef = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($pkgdef -notmatch [regex]::Escape('"CodeBase"="$PackageFolder$\CTilde.VisualStudio.dll"')) {
        throw 'The generated package registration does not bind CTilde.VisualStudio.dll through $PackageFolder$.'
    }
    if (-not ($archive.Entries | Where-Object FullName -eq 'ProjectSystem/CTildeDebugger.xaml')) {
        throw 'The packaged extension does not contain the CPS C~ debugger rule required to enable F5 and Ctrl+F5.'
    }
    $adapterRegistrationEntry = $archive.Entries | Where-Object FullName -eq 'debug-adapter.pkgdef' | Select-Object -First 1
    $reader = [System.IO.StreamReader]::new($adapterRegistrationEntry.Open())
    try { $adapterRegistration = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($adapterRegistration -notmatch 'A8D3FECE-E5AE-4BB9-9483-23B1951FD115' -or
        $adapterRegistration -notmatch '0CF710B9-7DB1-473B-8CEB-1F981ABA01E2' -or
        $adapterRegistration -notmatch [regex]::Escape('Tools\DebugAdapter\CTilde.DebugAdapter.exe')) {
        throw 'The packaged Debug Adapter Host registration does not contain the C~ engine, exception category, and adapter path.'
    }
    if ($adapterRegistration -notmatch [regex]::Escape("C~ thrown exceptions]`r`n`"State`"=dword:00010000") -and
        $adapterRegistration -notmatch [regex]::Escape("C~ thrown exceptions]`n`"State`"=dword:00010000")) {
        throw 'The packaged Debug Adapter Host registration enables caught C~ exceptions by default.'
    }
    if (@($archive.Entries | Where-Object { $_.FullName -like 'Tools/DebugAdapter/*/CTilde.DebugAdapter.exe' }).Count -ne 0) {
        throw 'The packaged debug adapter contains duplicated culture-directory executables.'
    }
    $inventory = $archive.Entries | Sort-Object FullName | ForEach-Object { '{0}`t{1}' -f $_.FullName, $_.Length }
}
finally { $archive.Dispose() }
$inventoryPath = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'CTilde.VisualStudio-0.14.0.inventory.txt'
[System.IO.File]::WriteAllLines($inventoryPath, $inventory, [System.Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata = @(
    "file=$([System.IO.Path]::GetFileName($destination))",
    "bytes=$((Get-Item -LiteralPath $destination).Length)",
    "sha256=$hash"
)
[System.IO.File]::WriteAllLines((Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'CTilde.VisualStudio-0.14.0.sha256.txt'), $metadata, [System.Text.UTF8Encoding]::new($false))
$metadata
