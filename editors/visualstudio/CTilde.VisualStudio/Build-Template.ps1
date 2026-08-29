param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Icon,
    [Parameter(Mandatory = $true)][string]$Destination
)

$sourceRoot = (Resolve-Path -LiteralPath $Source).Path
$iconPath = (Resolve-Path -LiteralPath $Icon).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destinationPath)) | Out-Null

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [System.IO.File]::Open($destinationPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $files = Get-ChildItem -LiteralPath $sourceRoot -File | Sort-Object Name
        foreach ($file in $files) {
            $entry = $archive.CreateEntry($file.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [System.IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
        $iconEntry = $archive.CreateEntry('ctilde-icon.png', [System.IO.Compression.CompressionLevel]::Optimal)
        $iconEntry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $iconInput = [System.IO.File]::OpenRead($iconPath)
        $iconOutput = $iconEntry.Open()
        try { $iconInput.CopyTo($iconOutput) } finally { $iconOutput.Dispose(); $iconInput.Dispose() }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }
