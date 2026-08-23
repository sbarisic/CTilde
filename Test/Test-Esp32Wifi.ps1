[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [string]$ToolsPath = "C:\Espressif\tools",
    [string]$ProjectDirectory = "",
    [string]$Port = "COM4",
    [ValidateRange(1, 4000000)]
    [int]$BaudRate = 460800,
    [string]$Ssid = $env:CTILDE_TEST_WIFI_SSID,
    [string]$Password = $env:CTILDE_TEST_WIFI_PASSWORD,
    [string]$Url = "https://example.com/"
)

$ErrorActionPreference = "Stop"
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProjectDirectory)) {
    $ProjectDirectory = Join-Path $repositoryDirectory "examples\TCan485"
}
$ProjectDirectory = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$settingsPath = Join-Path $ProjectDirectory "WifiSettings.ct"
$buildScript = Join-Path $ProjectDirectory "Build.ps1"
$originalSettings = [IO.File]::ReadAllBytes($settingsPath)
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ctilde-wifi-test-" + [guid]::NewGuid().ToString("N"))
$settingsChanged = $false

function ConvertTo-CtildeString([string]$Value, [string]$Name) {
    if ($null -eq $Value) { return "" }
    if ($Value.IndexOfAny([char[]]@("`0", "`r", "`n")) -ge 0) {
        throw "$Name must be a single C~ string without NUL or newline characters."
    }
    return $Value.Replace("\", "\\").Replace('"', '\"')
}

function Find-ByteSequence([byte[]]$Buffer, [byte[]]$Pattern) {
    if ($Pattern.Length -eq 0) { return 0 }
    for ($start = 0; $start -le $Buffer.Length - $Pattern.Length; $start++) {
        $matches = $true
        for ($offset = 0; $offset -lt $Pattern.Length; $offset++) {
            if ($Buffer[$start + $offset] -ne $Pattern[$offset]) {
                $matches = $false
                break
            }
        }
        if ($matches) { return $start }
    }
    return -1
}

function Invoke-ProjectBuild([switch]$Flash) {
    $buildParameters = @{
        IdfPath = $IdfPath
        Target = "esp32"
        Port = $Port
        Flash = [bool]$Flash
    }
    & $buildScript @buildParameters
    if ($LASTEXITCODE -ne 0) {
        throw "$buildScript failed with exit code $LASTEXITCODE."
    }
}

if ([string]::IsNullOrWhiteSpace($Ssid)) {
    throw "Set CTILDE_TEST_WIFI_SSID or pass -Ssid to run the opt-in live-network test."
}
if (-not (Test-Path -LiteralPath $IdfPath)) { throw "ESP-IDF was not found: $IdfPath" }

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    $settings = [Text.Encoding]::UTF8.GetString($originalSettings)
    $settings = $settings.Replace('public const string Ssid = "";', 'public const string Ssid = "' + (ConvertTo-CtildeString $Ssid "SSID") + '";')
    $settings = $settings.Replace('public const string Password = "";', 'public const string Password = "' + (ConvertTo-CtildeString $Password "password") + '";')
    $settings = $settings.Replace('public const string Url = "https://example.com/";', 'public const string Url = "' + (ConvertTo-CtildeString $Url "URL") + '";')
    $settingsChanged = $settings -cne [Text.Encoding]::UTF8.GetString($originalSettings)
    [IO.File]::WriteAllText($settingsPath, $settings, [Text.UTF8Encoding]::new($false))

    Invoke-ProjectBuild -Flash

    $python = Get-ChildItem -LiteralPath (Join-Path $ToolsPath "python") -Recurse -Filter "python.exe" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'v6\.0\.2[\\/]venv' } |
        Select-Object -First 1
    if ($null -eq $python) { throw "ESP-IDF Python was not found under $ToolsPath." }
    $captureScript = Join-Path $temporaryDirectory "capture.py"
    $capturePath = Join-Path $temporaryDirectory "wifi-uart.bin"
    $captureSource = @'
import serial, sys, time
port, baud, output = sys.argv[1], int(sys.argv[2]), sys.argv[3]
deadline = time.monotonic() + 75.0
data = bytearray()
stream = serial.Serial(port=None, baudrate=baud, timeout=0.2, dsrdtr=False, rtscts=False)
stream.dtr = False
stream.rts = False
stream.port = port
stream.open()
try:
    # Restart after the capture owns the port so a fast association cannot
    # finish between idf.py flash and opening the observer.
    stream.dtr = False
    stream.rts = True
    time.sleep(0.1)
    stream.rts = False
    while time.monotonic() < deadline:
        chunk = stream.read(4096)
        if chunk:
            data.extend(chunk)
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
            if (b"generated wifi/http bindings: ok" in data or
                    b"wifi/http error:" in data or
                    b"Guru Meditation Error" in data or
                    b"Entering gdb stub" in data):
                break
finally:
    stream.dtr = False
    stream.rts = False
    stream.close()
with open(output, "wb") as target:
    target.write(data)
'@
    [IO.File]::WriteAllText($captureScript, $captureSource, [Text.UTF8Encoding]::new($false))
    & $python.FullName $captureScript $Port $BaudRate $capturePath
    if ($LASTEXITCODE -ne 0) { throw "UART capture failed with exit code $LASTEXITCODE." }

    $bytes = [IO.File]::ReadAllBytes($capturePath)
    # The ROM can emit a few bytes at its reset-time baud before the application
    # configures the requested console rate. Validate UTF-8 strictly from the
    # first stable C~ application marker onward.
    $applicationMarker = [Text.Encoding]::ASCII.GetBytes("esp error: ESP_OK")
    $applicationStart = Find-ByteSequence $bytes $applicationMarker
    if ($applicationStart -lt 0) {
        throw "The C~ application marker was not received. See the streamed UART output above."
    }
    $applicationBytes = [byte[]]::new($bytes.Length - $applicationStart)
    [Array]::Copy($bytes, $applicationStart, $applicationBytes, 0, $applicationBytes.Length)
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $transcript = $utf8.GetString($applicationBytes)
    if ($transcript -match 'Guru Meditation Error') { throw "The ESP32 panicked during the HTTPS test. See the streamed UART backtrace above." }
    if ($transcript -notmatch 'generated wifi/http bindings: ok') { throw "The HTTPS success marker was not received. See the streamed UART output above." }
    if ($transcript -notmatch 'http status: (2\d\d)') { throw "A 2xx HTTP status was not received. See the streamed UART output above." }
    $statusCode = [int]$Matches[1]
    if ($transcript -notmatch 'downloaded bytes: ([1-9][0-9]*)') { throw "The HTTPS response contained no bytes. See the streamed UART output above." }
    $downloadedBytes = [uint64]$Matches[1]
    if ($transcript -notmatch 'body hash: ([1-9][0-9]*)') { throw "The response hash was missing or zero. See the streamed UART output above." }
    if ($transcript -notmatch 'wifi free heap before: ([0-9]+)') { throw "The pre-fetch heap measurement was missing. See the streamed UART output above." }
    $freeBefore = [uint64]$Matches[1]
    if ($transcript -notmatch 'wifi free heap after: ([0-9]+)') { throw "The post-fetch heap measurement was missing. See the streamed UART output above." }
    $freeAfter = [uint64]$Matches[1]
    # ESP-IDF retains a small amount of first-use Wi-Fi/TLS state after its
    # documented cleanup sequence. Keep that bounded while still detecting
    # an accumulating native-resource leak.
    if ($freeAfter + 8192 -lt $freeBefore) { throw "Native Wi-Fi/TLS cleanup lost more than 8 KiB: before=$freeBefore after=$freeAfter." }
    Write-Host "PASS ESP32 Wi-Fi HTTPS fetch: status=$statusCode bytes=$downloadedBytes and heap recovered within tolerance."
}
finally {
    [IO.File]::WriteAllBytes($settingsPath, $originalSettings)
    if ($settingsChanged) {
        try {
            Invoke-ProjectBuild -Flash
            Write-Host "Restored the original TCan485 firmware."
        }
        catch {
            Write-Warning "Could not restore and flash the ordinary firmware: $($_.Exception.Message)"
        }
    }
    else {
        Write-Host "The tested settings were already original; the validated firmware remains flashed."
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
