param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

$project = Join-Path $PSScriptRoot 'CTilde.VisualStudio/CTilde.VisualStudio.csproj'
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$vsix = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "CTilde.VisualStudio/bin/$Configuration") -Filter '*.vsix' -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $vsix) { throw 'The Visual Studio build did not produce a VSIX.' }
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$destination = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'CTilde.VisualStudio-0.11.0.vsix'
Copy-Item -LiteralPath $vsix.FullName -Destination $destination -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($destination)
try {
    $requiredEntries = @(
        'CTilde.VisualStudio.dll',
        'CTilde.VisualStudio.Core.dll',
        'Microsoft.VisualStudio.LanguageServer.Client.dll',
        'System.Text.Encodings.Web.dll',
        'CTilde.VisualStudio.pkgdef',
        'Grammars/ctilde.tmLanguage.tmTheme'
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
    $inventory = $archive.Entries | Sort-Object FullName | ForEach-Object { '{0}`t{1}' -f $_.FullName, $_.Length }
}
finally { $archive.Dispose() }
$inventoryPath = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'CTilde.VisualStudio-0.11.0.inventory.txt'
[System.IO.File]::WriteAllLines($inventoryPath, $inventory, [System.Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata = @(
    "file=$([System.IO.Path]::GetFileName($destination))",
    "bytes=$((Get-Item -LiteralPath $destination).Length)",
    "sha256=$hash"
)
[System.IO.File]::WriteAllLines((Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'CTilde.VisualStudio-0.11.0.sha256.txt'), $metadata, [System.Text.UTF8Encoding]::new($false))
$metadata
