[CmdletBinding()]
param(
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$extensionRoot = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $extensionRoot "package.json") -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $extensionRoot ("{0}-{1}.vsix" -f $manifest.name, $manifest.version)
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $extensionRoot $OutputPath
}

function Invoke-ReleaseCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,
        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Release command failed with exit code ${LASTEXITCODE}: $Executable $($Arguments -join ' ')"
    }
}

Push-Location $extensionRoot
try {
    Invoke-ReleaseCommand npm ci
    Invoke-ReleaseCommand npm test
    Invoke-ReleaseCommand npm run test:extension
    Invoke-ReleaseCommand npm run test:extension:minimum
    Invoke-ReleaseCommand npm audit --omit=dev
    Invoke-ReleaseCommand npx vsce package --no-dependencies --out $OutputPath
}
finally {
    Pop-Location
}

$artifact = Get-Item -LiteralPath $OutputPath
$hash = Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
[pscustomobject]@{
    Path = $artifact.FullName
    Bytes = $artifact.Length
    SHA256 = $hash.Hash
}
