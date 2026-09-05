[CmdletBinding()]
param(
    [string]$Port = 'COM4',
    [string]$IdfProfile = 'C:\Espressif\tools\Microsoft.v6.0.2.PowerShell_profile.ps1',
    [string]$EspPython = "$env:USERPROFILE\.espressif\python_env\idf6.0_py3.14_env\Scripts\python.exe",
    [switch]$NoMonitor,
    [switch]$FullFlash,
    [switch]$RomBackup,
    [switch]$UseStub,
    [switch]$PreserveStorage
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Invoke-EspPython {
    & $EspPython @args
    if ($LASTEXITCODE -ne 0) { throw "Python command failed with exit code $LASTEXITCODE. Flashing stopped." }
}

if (-not (Test-Path -LiteralPath $IdfProfile)) { throw "ESP-IDF profile not found: $IdfProfile" }
if (-not (Test-Path -LiteralPath $EspPython)) { throw "ESP Python not found: $EspPython" }
if ($RomBackup -and $UseStub) { throw 'Use either -RomBackup or -UseStub, not both.' }

# Use the ROM transport for both operations unless the stub is explicitly requested.
# RomBackup remains accepted for compatibility with earlier script invocations.
$transportArguments = @('-m', 'esptool', '--chip', 'esp32', '--port', $Port, '--baud', '115200')
if (-not $UseStub) { $transportArguments += '--no-stub' }

Push-Location $repoRoot
try {
    . $IdfProfile
    Invoke-EspPython -c 'import esptool, serial'
    if ($PreserveStorage -and -not $FullFlash) {
        $flashHelp = & $EspPython -m esptool write-flash --help | Out-String
        if ($LASTEXITCODE -ne 0 -or $flashHelp -notmatch '--diff-with') {
            throw 'This esptool does not support --diff-with. Update esptool or use -FullFlash.'
        }
    }

    # Probe ownership without resetting the device before starting the build.
    Invoke-EspPython -c 'import serial, sys; p = serial.Serial(); p.port = sys.argv[1]; p.dtr = False; p.rts = False; p.open(); p.close()' $Port

    if ($PreserveStorage) {
    $toolsDirectory = Join-Path $repoRoot 'artifacts\managed-shell\flash-tools'
    New-Item -ItemType Directory -Force $toolsDirectory | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDirectory 'littlefs_python-0.19.0.dist-info'))) {
        Invoke-EspPython -m pip install --disable-pip-version-check --target $toolsDirectory 'littlefs-python==0.19.0'
    }
    }

    & (Join-Path $PSScriptRoot 'Test-ManagedShell.ps1') -BuildOnly -IdfPath $env:IDF_PATH

    $firmware = Join-Path $PSScriptRoot 'build\ctilde_managed_shell.bin'
    $storage = Join-Path $PSScriptRoot 'build\storage.bin'
    if ($PreserveStorage) {
    $runDirectory = Join-Path $repoRoot ('artifacts\managed-shell\flash-' +
        [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $runDirectory | Out-Null
    $backup = Join-Path $runDirectory 'flash-before.bin'
    $storage = Join-Path $runDirectory 'storage-merged.bin'
    $firmware = Join-Path $PSScriptRoot 'build\ctilde_managed_shell.bin'

    Write-Host "Backing up the current device to $backup"
    $backupArguments = @($transportArguments)
    if (-not $UseStub) {
        Write-Warning 'ROM backup is much slower than the flasher stub. The full 4 MiB read can take tens of minutes.'
        $backupArguments += @('--after', 'no-reset')
    } else {
        # Keep the stub active: exiting a failed stub can hide the original read error.
        $backupArguments += @('--after', 'no-reset-stub')
    }
    $backupArguments += @('read-flash', '--flash-size', '4MB', '0', '0x400000', $backup)
    $backupSucceeded = $false
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        & $EspPython @backupArguments
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $backup) -and
            (Get-Item -LiteralPath $backup).Length -eq 0x400000) {
            $backupSucceeded = $true
            break
        }
        if ($attempt -lt 2) { Write-Warning 'Backup failed. Reconnecting and retrying once; no flash has been written.' }
    }
    if (-not $backupSucceeded) {
        throw 'Backup failed; no flash was written. Reconnect USB and retry. The default transport uses ROM for both backup and flashing.'
    }

    # Mount the backup without formatting. Replace only the rebuilt module files.
    $mergeProgram = @'
import hashlib
import json
from pathlib import Path
import struct
import sys

root, output, deps = map(Path, sys.argv[1:])
sys.path.insert(0, str(deps))
from littlefs import LittleFS, UserContext

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

def digest(data):
    return hashlib.sha256(data).hexdigest()

backup = (output / "flash-before.bin").read_bytes()
require(len(backup) == 0x400000, "A complete 4 MiB flash backup is required")
project = root / "examples/ManagedShell"
table = (project / "build/partition_table/partition-table.bin").read_bytes()
require(backup[0x8000:0x8000 + len(table)] == table, "Device partition table differs from the build; no flash written")
parts = {}
for pos in range(0x8000, 0x8C00, 32):
    magic, kind, subtype, offset, size, name, flags = struct.unpack_from("<HBBII16sI", backup, pos)
    if magic != 0x50AA:
        break
    require(flags == 0, "Encrypted or read-only partitions are not supported")
    require(offset + size <= len(backup), "Partition exceeds backup")
    parts[name.split(b"\0")[0].decode()] = (offset, size)
require(parts.get("factory") == (0x10000, 0x200000), "Unexpected firmware partition")
require(parts.get("storage") == (0x210000, 0xF0000), "Unexpected storage partition")
require(parts.get("sftp") == (0x300000, 0x100000), "Unexpected SFTP partition")

def mount(data):
    context = UserContext(buffer=bytearray(data))
    fs = LittleFS(context=context, mount=False, block_size=4096,
        block_count=len(data) // 4096, read_size=128, prog_size=128,
        cache_size=512, lookahead_size=128, name_max=64)
    fs.mount()
    return fs, context

def contents(fs):
    result = {}
    for directory, _, files in fs.walk("/"):
        for name in files:
            path = directory.rstrip("/") + "/" + name
            with fs.open(path, "rb") as stream:
                result[path] = digest(stream.read())
    return result

fs, context = mount(backup[0x210000:0x300000])
before = contents(fs)
modules = sorted((project / "storage/modules").glob("*.ctm"))
require(len(modules) == 13, "Expected all 13 rebuilt modules")
updates = {"/modules/" + path.name: path.read_bytes() for path in modules}
changed_modules = {}
for path, data in updates.items():
    if before.get(path) == digest(data):
        continue
    with fs.open(path, "wb") as stream:
        stream.write(data)
    changed_modules[path] = digest(data)
fs.unmount()
merged = bytes(context.buffer)
require(len(merged) == 0xF0000, "Merged storage size changed")
fs, _ = mount(merged)
after = contents(fs)
fs.unmount()
require({p: h for p, h in before.items() if p not in updates} ==
        {p: h for p, h in after.items() if p not in updates}, "Existing non-module files changed")
require(all(after.get(p) == digest(data) for p, data in updates.items()), "Module verification failed")
firmware = (project / "build/ctilde_managed_shell.bin").read_bytes()
require(0 < len(firmware) <= 0x200000, "Firmware exceeds its partition")
(output / "storage-merged.bin").write_bytes(merged)
# Include the final erase sector so its current contents participate in the diff.
# Esptool pads the new firmware's final sector with 0xFF when rewriting it.
firmware_before = backup[0x10000:0x10000 + ((len(firmware) + 4095) & ~4095)]
storage_before = backup[0x210000:0x300000]
(output / "firmware-before.bin").write_bytes(firmware_before)
(output / "storage-before.bin").write_bytes(storage_before)
report = dict(backupSha256=digest(backup), firmwareSha256=digest(firmware),
    storageSha256=digest(merged), preservedFiles=sum(p not in updates for p in before),
    firmwareBeforeSha256=digest(firmware_before), storageBeforeSha256=digest(storage_before),
    changedModules=changed_modules,
    updatedModules={p: digest(data) for p, data in updates.items()})
(output / "preservation.json").write_text(json.dumps(report, indent=2) + "\n")
print(f"Prepared {len(updates)} modules; preserved {report['preservedFiles']} other files.")
print(f"Changed modules: {len(changed_modules)}; unchanged modules were not rewritten in LittleFS.")
'@
    $mergePath = Join-Path $runDirectory 'merge-storage.py'
    Set-Content -LiteralPath $mergePath -Value $mergeProgram -Encoding utf8
    Invoke-EspPython $mergePath $repoRoot $runDirectory $toolsDirectory
    } else {
        Write-Host 'Flashing full firmware and built storage images without a backup or differential comparison.'
        Write-Warning 'The storage partition will be replaced by the build image. Device-only files in that partition will be lost. NVS and SFTP partitions are not written.'
    }

    $flashArguments = @($transportArguments) + @('write-flash', '--flash-size', '4MB',
        '0x10000', $firmware, '0x210000', $storage)
    if ($PreserveStorage -and -not $FullFlash) {
        $flashArguments += @('--diff-with', (Join-Path $runDirectory 'firmware-before.bin'),
            (Join-Path $runDirectory 'storage-before.bin'))
        Write-Host 'Flashing changed sectors with esptool verification enabled.'
    }
    Invoke-EspPython @flashArguments
    Write-Host 'Flash complete.'
    if ($PreserveStorage) { Write-Host "Backup and preservation report: $runDirectory" }
    if (-not $NoMonitor) {
        Write-Host 'Press Enter for ct>. Exit the monitor with Ctrl+].'
        Invoke-EspPython -m serial.tools.miniterm --raw --eol CR $Port 115200
    }
}
finally {
    Pop-Location
}
