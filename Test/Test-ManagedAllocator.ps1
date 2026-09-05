[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts/correctness-review/allocator' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$runtime = Join-Path $repository 'runtime/esp-idf/ctilde_managed_runtime/ctilde_managed_runtime.c'
$source = [IO.File]::ReadAllText($runtime)
$functions = foreach ($name in @('begin_runtime_operation', 'end_runtime_operation', 'api_allocate', 'api_free', 'release_arena', 'ctilde_managed_atomic_compare_exchange_u32', 'release_thread_payload', 'ctilde_managed_thread_payload_allocate', 'ctilde_managed_thread_payload_free')) {
    $match = [regex]::Match($source, '(?m)^(?:static [^\r\n]*\b|bool __attribute__\(\(noinline\)\) |void \*|void )' + $name + '\([^;]*?\)\s*\{')
    if (!$match.Success) { throw "Missing production function: $name" }
    $opening = $source.IndexOf('{', $match.Index)
    $depth = 1
    $ending = $opening + 1
    while ($depth -gt 0 -and $ending -lt $source.Length) {
        if ($source[$ending] -eq '{') { $depth++ }
        if ($source[$ending] -eq '}') { $depth-- }
        $ending++
    }
    if ($depth -ne 0) { throw "Unbalanced production function: $name" }
    $source.Substring($match.Index, $ending - $match.Index)
}
$owner = [regex]::Match($source, 'typedef struct ct_thread_payload_owner \{[^}]*\} ct_thread_payload_owner;').Value
if (!$owner) { throw 'Missing production thread payload owner.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'allocator_under_test.inc'), $owner + "`n" + ($functions -join "`n`n"))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fixtures/ManagedAllocator/harness.c') -Destination $OutputDirectory
$wslDirectory = (& wsl --exec wslpath -a -u $OutputDirectory).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the allocator test directory in WSL.' }
& wsl --exec gcc -std=c11 -O2 -Wall -Wextra -Werror -pthread "$wslDirectory/harness.c" -o "$wslDirectory/allocator-test"
if ($LASTEXITCODE -ne 0) { throw 'Allocator harness compilation failed.' }
$output = & wsl --exec "$wslDirectory/allocator-test"
if ($LASTEXITCODE -ne 0) { throw 'Allocator harness failed.' }
$report = $output | ConvertFrom-Json
$report | Add-Member sourceSha256 (Get-FileHash -LiteralPath $runtime).Hash
$report | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'report.json')
$output
