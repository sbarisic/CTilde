[CmdletBinding()]
param(
    [string]$IdfPath = "C:\esp\v6.0.2\esp-idf",
    [string]$ProjectDirectory = "",
    [string]$Port = "COM4",
    [ValidateRange(1, 4000000)]
    [int]$BaudRate = 460800,
    [switch]$AutomatedOnly,
    [switch]$AcceptMemoryBaseline,
    [string]$ExpectedUsbSerialId = "VID_1A86&PID_55D4"
)

$ErrorActionPreference = "Stop"
$repositoryDirectory = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProjectDirectory)) {
    $ProjectDirectory = Join-Path $repositoryDirectory "examples\TCan485"
}
$ProjectDirectory = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$projectManifest = Join-Path $ProjectDirectory "ctilde.json"
$programSource = Join-Path $ProjectDirectory "Program.ct"
$runtimeFailureSource = Join-Path $ProjectDirectory "RuntimeFailure.ct"
$memoryValidationSource = Join-Path $ProjectDirectory "MemoryValidation.ct"
$consoleValidationSource = Join-Path $ProjectDirectory "ConsoleValidation.ct"
$draft018ValidationSource = Join-Path $ProjectDirectory "Draft018Validation.ct"
$draft019ValidationSource = Join-Path $ProjectDirectory "Draft019Validation.ct"
$draft020ValidationSource = Join-Path $ProjectDirectory "Draft020Validation.ct"
$buildScript = Join-Path $ProjectDirectory "Build.ps1"
$artifactDirectory = Join-Path $repositoryDirectory "artifacts\esp32-hardware"
$timestamp = [DateTimeOffset]::Now.ToString("yyyyMMdd-HHmmss")
$reportPath = Join-Path $artifactDirectory "$timestamp.json"
$debugReportPath = Join-Path $artifactDirectory "$timestamp-debug.json"
$debugDescriptorPath = Join-Path $artifactDirectory "$timestamp-debug-target.json"
$adapterPath = Join-Path $repositoryDirectory "editors\vscode\out\debugAdapter.js"
$supportTest = Join-Path $PSScriptRoot "Esp32HardwareSupport.test.mjs"
$debugHarness = Join-Path $PSScriptRoot "Esp32DebugHarness.mjs"
$workDirectory = Join-Path $artifactDirectory "work-$timestamp"
$fatalBuildDirectory = Join-Path $workDirectory "fatal-build"
$fatalSdkconfig = Join-Path $workDirectory "sdkconfig.fatal"
$fatalDefaults = Join-Path $workDirectory "sdkconfig.fatal.defaults"
$restartBuildDirectory = Join-Path $workDirectory "restart-build"
$restartSdkconfig = Join-Path $workDirectory "sdkconfig.restart"
$restartDefaults = Join-Path $workDirectory "sdkconfig.restart.defaults"
$haltBuildDirectory = Join-Path $workDirectory "halt-build"
$haltSdkconfig = Join-Path $workDirectory "sdkconfig.halt"
$haltDefaults = Join-Path $workDirectory "sdkconfig.halt.defaults"
$memoryBaselinePath = Join-Path $PSScriptRoot "Baselines\esp-idf-memory.json"

$report = [ordered]@{
    version = 1
    startedAt = [DateTimeOffset]::Now.ToString("O")
    completedAt = $null
    passed = $false
    automatedPassed = $false
    automatedOnly = [bool]$AutomatedOnly
    visualLed = if ($AutomatedOnly) { "pending" } else { "unconfirmed" }
    repository = $repositoryDirectory
    commit = (& git -c "safe.directory=E:/Projects/CTilde" rev-parse HEAD).Trim()
    target = "esp32"
    port = $Port
    baudRate = $BaudRate
    tools = [ordered]@{}
    firmware = $null
    runtimeFailure = $null
    panicPolicies = [ordered]@{ restart = $null; halt = $null }
    memoryValidation = $null
    consoleValidation = $null
    draft018Validation = $null
    draft019Validation = $null
    draft020Validation = $null
    debugger = $null
    postDetach = $null
    startupTimeout = $null
    restore = [ordered]@{ attempted = $false; passed = $false; error = $null }
    error = $null
}

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkingDirectory = $repositoryDirectory) {
    Push-Location $WorkingDirectory
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Captured([string]$File, [string[]]$Arguments, [string]$WorkingDirectory = $repositoryDirectory) {
    Push-Location $WorkingDirectory
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 wraps redirected native stderr as
        # NativeCommandError records. With the script-wide Stop preference,
        # informational tools such as `idf.py size` would then terminate even
        # when the native process returned zero. Capture both streams and use
        # the native exit code as the authority.
        $ErrorActionPreference = "Continue"
        $output = & $File @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw "$File failed with exit code $exitCode.`n$output"
        }
        return $output
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }
}

function Remove-Ansi([string]$Text) {
    return [regex]::Replace($Text, "`e\[[0-9;?]*[ -/]*[@-~]", "")
}

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    # Windows PowerShell 5.1 does not recognize Set-Content's utf8NoBOM
    # encoding name. Use the framework API so reports and parser inputs have
    # identical UTF-8 bytes under both Windows PowerShell and PowerShell 7.
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Select-FirmwareTranscript([string]$Text) {
    # The ESP32 ROM writes its reset banner at 115200 baud before ESP-IDF
    # switches UART0 to the project baud rate. Discard that undecodable prefix,
    # but keep strict UTF-8 validation for the bootloader and application output.
    $starts = @("I (", "esp error:", "CTILDE_ESP_FAILURE_TEST") |
        ForEach-Object { $Text.IndexOf($_, [StringComparison]::Ordinal) } |
        Where-Object { $_ -ge 0 }
    if ($starts.Count -eq 0) { return $Text }
    return $Text.Substring(($starts | Measure-Object -Minimum).Minimum)
}

function Stop-ProjectMonitorProcesses {
    $escapedProject = [regex]::Escape($ProjectDirectory)
    $targets = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -match $escapedProject -and
            $_.Name -match '^(?:python|xtensa.*gdb).*' -and
            $_.CommandLine -match 'idf_monitor|target remote'
        } |
        Sort-Object ProcessId -Descending
    foreach ($target in $targets) {
        Stop-Process -Id $target.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($targets.Count -gt 0) { Start-Sleep -Milliseconds 500 }
}

function Invoke-IdfMonitor([string[]]$Arguments, [scriptblock]$Completed, [int]$TimeoutSeconds) {
    $python = Join-Path $env:IDF_PYTHON_ENV_PATH "Scripts\python.exe"
    $idf = Join-Path $env:IDF_PATH "tools\idf.py"
    if (-not (Test-Path -LiteralPath $python) -or -not (Test-Path -LiteralPath $idf)) {
        throw "The activated ESP-IDF Python environment is incomplete."
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $python
    $start.WorkingDirectory = $ProjectDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # esp-idf-monitor reads the Windows console directly. Let it inherit stdin;
    # the harness terminates the child once the required UART condition is met.
    $start.RedirectStandardInput = $false
    $start.CreateNoWindow = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.Environment["PYTHONUTF8"] = "1"
    $start.Environment["PYTHONIOENCODING"] = "utf-8"
    $start.ArgumentList.Add($idf)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $transcript = [Text.StringBuilder]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw "Could not start ESP-IDF monitor." }
        $stdoutTask = $process.StandardOutput.ReadLineAsync()
        $stderrTask = $process.StandardError.ReadLineAsync()
        $never = [Threading.Tasks.Task]::Delay([Threading.Timeout]::Infinite)
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $tasks = [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
            $completedIndex = [Threading.Tasks.Task]::WaitAny($tasks, 100)
            if ($completedIndex -ge 0) {
                $task = $tasks[$completedIndex]
                $line = $task.GetAwaiter().GetResult()
                if ($null -ne $line) {
                    [void]$transcript.AppendLine($line)
                    Write-Host $line
                }
                if ($completedIndex -eq 0) {
                    $stdoutTask = if ($null -eq $line) { $never } else { $process.StandardOutput.ReadLineAsync() }
                }
                else {
                    $stderrTask = if ($null -eq $line) { $never } else { $process.StandardError.ReadLineAsync() }
                }
            }
            $clean = Remove-Ansi $transcript.ToString()
            if (& $Completed $clean) {
                $process.Kill($true)
                $process.WaitForExit()
                return [pscustomobject]@{ Transcript = Remove-Ansi $transcript.ToString(); ElapsedSeconds = $watch.Elapsed.TotalSeconds }
            }
            if ($process.HasExited) {
                throw "ESP-IDF monitor exited with code $($process.ExitCode) before its acceptance condition was met.`n$clean"
            }
            Start-Sleep -Milliseconds 100
        }
        throw "Timed out after $TimeoutSeconds seconds waiting for ESP-IDF monitor output.`n$(Remove-Ansi $transcript.ToString())"
    }
    finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
        $process.Dispose()
        Stop-ProjectMonitorProcesses
    }
}

function Invoke-PassiveSerialCapture([scriptblock]$Completed, [int]$TimeoutSeconds) {
    $serial = [IO.Ports.SerialPort]::new()
    $serial.PortName = $Port
    $serial.BaudRate = $BaudRate
    $serial.Parity = [IO.Ports.Parity]::None
    $serial.DataBits = 8
    $serial.StopBits = [IO.Ports.StopBits]::One
    $serial.Handshake = [IO.Ports.Handshake]::None
    $serial.DtrEnable = $false
    $serial.RtsEnable = $false
    $serial.ReadTimeout = 100
    $serial.Encoding = [Text.UTF8Encoding]::new($false, $false)
    $transcript = [Text.StringBuilder]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $lastOpenError = $null
        while (-not $serial.IsOpen -and $watch.Elapsed.TotalSeconds -lt [Math]::Min(5, $TimeoutSeconds)) {
            try {
                $serial.Open()
                # Keep both modem-control lines inactive after the Windows CH343
                # driver has opened the handle. This observes a running target
                # without reproducing ESP-IDF monitor's reset pulse.
                $serial.DtrEnable = $false
                $serial.RtsEnable = $false
                # The CH343 retains bytes in its USB-side FIFO after the
                # previous flasher/debugger handle closes. Drain that backlog
                # before starting a new observation window. New firmware output
                # can be discarded during this bounded warm-up; acceptance
                # always waits for later markers.
                $drainUntil = [DateTime]::UtcNow.AddMilliseconds(750)
                while ([DateTime]::UtcNow -lt $drainUntil) {
                    $serial.DiscardInBuffer()
                    Start-Sleep -Milliseconds 25
                }
                $watch.Restart()
            }
            catch {
                $lastOpenError = $_.Exception
                Start-Sleep -Milliseconds 150
            }
        }
        if (-not $serial.IsOpen) {
            throw "Could not passively open $Port within five seconds: $($lastOpenError.Message)"
        }

        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $text = $serial.ReadExisting()
            if (-not [string]::IsNullOrEmpty($text)) {
                [void]$transcript.Append($text)
                Write-Host -NoNewline $text
            }
            $clean = Remove-Ansi $transcript.ToString()
            if (& $Completed $clean) {
                return [pscustomobject]@{ Transcript = $clean; ElapsedSeconds = $watch.Elapsed.TotalSeconds }
            }
            Start-Sleep -Milliseconds 50
        }
        throw "Timed out after $TimeoutSeconds seconds waiting for passive UART output.`n$(Remove-Ansi $transcript.ToString())"
    }
    finally {
        if ($serial.IsOpen) { $serial.Close() }
        $serial.Dispose()
    }
}

function Invoke-PassiveSerialByteCapture([scriptblock]$Completed, [int]$TimeoutSeconds) {
    $serial = [IO.Ports.SerialPort]::new($Port, $BaudRate, [IO.Ports.Parity]::None, 8, [IO.Ports.StopBits]::One)
    $serial.Handshake = [IO.Ports.Handshake]::None
    $serial.DtrEnable = $false
    $serial.RtsEnable = $false
    $serial.ReadTimeout = 100
    $bytes = [IO.MemoryStream]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $serial.Open()
        $serial.DtrEnable = $false
        $serial.RtsEnable = $false
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            try {
                $value = $serial.ReadByte()
                if ($value -ge 0) { $bytes.WriteByte([byte]$value) }
            }
            catch [TimeoutException] { }
            $current = $bytes.ToArray()
            if (& $Completed $current) {
                return [pscustomobject]@{ Bytes = $current; ElapsedSeconds = $watch.Elapsed.TotalSeconds }
            }
        }
        $captured = $bytes.ToArray()
        if ($captured.Length -eq 0) {
            $tailHex = "<empty>"
            $tailText = "<empty>"
        }
        else {
            $tailStart = [Math]::Max(0, $captured.Length - 96)
            $tail = $captured[$tailStart..($captured.Length - 1)]
            $tailHex = [BitConverter]::ToString($tail).Replace("-", "")
            $tailText = [Text.Encoding]::UTF8.GetString($tail).Replace("`r", "\\r").Replace("`n", "\\n")
        }
        throw "Timed out after $TimeoutSeconds seconds waiting for raw UART output ($($captured.Length) bytes). Tail text: $tailText; tail hex: $tailHex"
    }
    finally {
        if ($serial.IsOpen) { $serial.Close() }
        $serial.Dispose()
        $bytes.Dispose()
    }
}

function Invoke-NodeJson([string[]]$Arguments) {
    $output = Invoke-Captured "node" $Arguments
    return $output | ConvertFrom-Json
}

function Save-Report {
    $report.completedAt = [DateTimeOffset]::Now.ToString("O")
    Write-Utf8NoBom $reportPath (($report | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
}

function Restore-ReleaseFirmware {
    $report.restore.attempted = $true
    try {
        & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $programSource -Flash
        if ($LASTEXITCODE -ne 0) { throw "Release restore failed with exit code $LASTEXITCODE." }
        $report.restore.passed = $true
    }
    catch {
        $report.restore.error = $_.Exception.Message
        Write-Warning "Could not restore the ordinary Release firmware: $($_.Exception.Message)"
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $workDirectory | Out-Null
try {
    if (-not (Test-Path -LiteralPath $projectManifest) -or -not (Test-Path -LiteralPath $buildScript)) {
        throw "TCan485 project files were not found under $ProjectDirectory."
    }
    $portInfo = Get-CimInstance Win32_SerialPort | Where-Object DeviceID -eq $Port | Select-Object -First 1
    $portCandidate = if ($null -eq $portInfo) { "null" } else { ([ordered]@{ name = $portInfo.Name; deviceId = $portInfo.DeviceID; pnpDeviceId = $portInfo.PNPDeviceID } | ConvertTo-Json -Compress) }
    $validatePort = "import('node:url').then(u=>import(u.pathToFileURL(process.argv[1]).href)).then(m=>console.log(JSON.stringify(m.validateUsbSerialDevice(JSON.parse(process.argv[2]),process.argv[3],process.argv[4]))))"
    $validatedPort = Invoke-NodeJson @("-e", $validatePort, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $portCandidate, $Port, $ExpectedUsbSerialId)

    $resolvedIdfPath = (Resolve-Path -LiteralPath $IdfPath).Path
    $profile = Get-ChildItem -LiteralPath "C:\Espressif\tools" -Filter "Microsoft.*.PowerShell_profile.ps1" -File -ErrorAction SilentlyContinue |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match [regex]::Escape($resolvedIdfPath) } |
        Select-Object -First 1
    if ($null -ne $profile) {
        . $profile.FullName
    }
    else {
        $exportScript = Join-Path $resolvedIdfPath "export.ps1"
        if (-not (Test-Path -LiteralPath $exportScript)) { throw "ESP-IDF activation script was not found: $exportScript" }
        . $exportScript
    }
    if ((Resolve-Path -LiteralPath $env:IDF_PATH).Path -ne (Resolve-Path -LiteralPath $IdfPath).Path) {
        throw "Activated ESP-IDF path '$env:IDF_PATH' does not match '$IdfPath'."
    }

    $report.tools.idf = (Invoke-Captured "idf.py" @("--version") $ProjectDirectory).Trim()
    $report.tools.dotnet = (Invoke-Captured "dotnet" @("--version")).Trim()
    $report.tools.node = (Invoke-Captured "node" @("--version")).Trim()
    $report.tools.compiler = (Invoke-Captured "xtensa-esp32-elf-gcc" @("--version") $ProjectDirectory).Split("`n")[0].Trim()
    $report.tools.gdb = (Invoke-Captured "xtensa-esp32-elf-gdb" @("--version") $ProjectDirectory).Split("`n")[0].Trim()
    $report.tools.port = [ordered]@{ name = $validatedPort.name; pnpDeviceId = $validatedPort.pnpDeviceId }

    Invoke-Checked "dotnet" @("build", ".\Test\Test.csproj", "-c", "Release", "--nologo")
    Invoke-Checked "npm" @("run", "compile") (Join-Path $repositoryDirectory "editors\vscode")
    Invoke-Checked "node" @("--test", $supportTest)

    Write-Host "`n=== ABI 16 Release workload ==="
    & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $programSource -Clean
    if ($LASTEXITCODE -ne 0) { throw "Release firmware build failed with exit code $LASTEXITCODE." }
    $sizeOutput = Invoke-Captured "idf.py" @("size") $ProjectDirectory
    $sizeInput = Join-Path $artifactDirectory "$timestamp-size.txt"
    Write-Utf8NoBom $sizeInput $sizeOutput
    $parseSize = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.parseIdfSize(fs.readFileSync(process.argv[2],'utf8'))))))"
    $size = Invoke-NodeJson @("-e", $parseSize, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $sizeInput)
    $elfPath = Join-Path $ProjectDirectory "build\ctilde_tcan485.elf"
    $objectSymbolsOutput = Invoke-Captured "xtensa-esp32-elf-objdump" @("-t", $elfPath) $ProjectDirectory
    $objectSymbolsInput = Join-Path $artifactDirectory "$timestamp-symbols.txt"
    Write-Utf8NoBom $objectSymbolsInput $objectSymbolsOutput
    $parseSymbols = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.parseObjectSymbols(fs.readFileSync(process.argv[2],'utf8'))))))"
    $objectSymbols = Invoke-NodeJson @("-e", $parseSymbols, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $objectSymbolsInput)
    Invoke-Checked "idf.py" @("-p", $Port, "flash") $ProjectDirectory
    $firmwareCapture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        $hasTransitions = ([regex]::Matches($text, "(?m)^ws2812:\s*(?:on|off)\s*$")).Count -ge 25
        $hasNetworkResult = $text.Contains("wifi: not configured") -or
            $text.Contains("generated wifi/http bindings: ok") -or
            $text.Contains("wifi/http error:")
        $hasTransitions -and $hasNetworkResult
    } 90
    $firmwareTranscript = Select-FirmwareTranscript $firmwareCapture.Transcript
    if ($firmwareTranscript.Contains([char]0xfffd)) { throw "Firmware transcript contains malformed UTF-8 replacement characters." }
    $firmwareTranscriptPath = Join-Path $artifactDirectory "$timestamp-firmware.txt"
    Write-Utf8NoBom $firmwareTranscriptPath $firmwareTranscript
    $parseFirmware = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.parseFirmwareTranscript(fs.readFileSync(process.argv[2],'utf8'))))))"
    $firmware = Invoke-NodeJson @("-e", $parseFirmware, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $firmwareTranscriptPath)
    $report.firmware = [ordered]@{ measurements = $firmware; size = $size; immutableSymbols = $objectSymbols; elapsedSeconds = $firmwareCapture.ElapsedSeconds; transcript = $firmwareTranscriptPath }

    if (-not $AutomatedOnly) {
        $answer = Read-Host "Did the onboard T-CAN485 WS2812 visibly alternate during the 25 checked transitions? [y/N]"
        if ($answer -match '^(?i:y|yes)$') {
            $report.visualLed = "confirmed"
        }
        else {
            $report.visualLed = "not-confirmed"
            Write-Warning "Visible WS2812 confirmation was not provided. Automated acceptance will continue, but this run will not close the physical release gate."
        }
    }

    Write-Host "`n=== Draft 0.18 compile-time and native-system facilities ==="
    & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $draft018ValidationSource -Flash
    if ($LASTEXITCODE -ne 0) { throw "Draft 0.18 validation firmware failed to build and flash with exit code $LASTEXITCODE." }
    $draft018Capture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        $text.Contains("CTILDE_DRAFT_018_OK") -or ($text.Contains("CTILDE_DRAFT_018_") -and $text.Contains("FAILED"))
    } 45
    $draft018Transcript = Select-FirmwareTranscript $draft018Capture.Transcript
    if (-not $draft018Transcript.Contains("CTILDE_DRAFT_018_OK")) {
        throw "Draft 0.18 validation did not emit its success marker.`n$draft018Transcript"
    }
    if (-not $draft018Transcript.Contains("draft018 architecture: xtensa") -or
        -not $draft018Transcript.Contains("draft018 stack headroom:")) {
        throw "Draft 0.18 validation did not report architecture and task stack evidence.`n$draft018Transcript"
    }
    $draft018TranscriptPath = Join-Path $artifactDirectory "$timestamp-draft018.txt"
    Write-Utf8NoBom $draft018TranscriptPath $draft018Transcript
    $report.draft018Validation = [ordered]@{ elapsedSeconds = $draft018Capture.ElapsedSeconds; transcript = $draft018TranscriptPath }

    Write-Host "`n=== Draft 0.19 compile-time layout and low-level facilities ==="
    & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $draft019ValidationSource -Flash
    if ($LASTEXITCODE -ne 0) { throw "Draft 0.19 validation firmware failed to build and flash with exit code $LASTEXITCODE." }
    $draft019Capture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        $text.Contains("CTILDE_DRAFT_019_OK") -or $text.Contains("CTILDE_DRAFT_019_FAILED")
    } 45
    $draft019Transcript = Select-FirmwareTranscript $draft019Capture.Transcript
    if (-not $draft019Transcript.Contains("CTILDE_DRAFT_019_OK")) {
        throw "Draft 0.19 validation did not emit its success marker.`n$draft019Transcript"
    }
    $draft019TranscriptPath = Join-Path $artifactDirectory "$timestamp-draft019.txt"
    Write-Utf8NoBom $draft019TranscriptPath $draft019Transcript
    $report.draft019Validation = [ordered]@{ elapsedSeconds = $draft019Capture.ElapsedSeconds; transcript = $draft019TranscriptPath }

    Write-Host "`n=== Draft 0.20 protocol and native-integration facilities ==="
    & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $draft020ValidationSource -Flash
    if ($LASTEXITCODE -ne 0) { throw "Draft 0.20 validation firmware failed to build and flash with exit code $LASTEXITCODE." }
    $draft020Capture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        $text.Contains("CTILDE_DRAFT_020_OK") -or $text.Contains("CTILDE_DRAFT_020_FAILED")
    } 45
    $draft020Transcript = Select-FirmwareTranscript $draft020Capture.Transcript
    if (-not $draft020Transcript.Contains("CTILDE_DRAFT_020_OK")) {
        throw "Draft 0.20 validation did not emit its success marker.`n$draft020Transcript"
    }
    $draft020TranscriptPath = Join-Path $artifactDirectory "$timestamp-draft020.txt"
    Write-Utf8NoBom $draft020TranscriptPath $draft020Transcript
    $report.draft020Validation = [ordered]@{ elapsedSeconds = $draft020Capture.ElapsedSeconds; transcript = $draft020TranscriptPath }

    Write-Host "`n=== Managed layout and allocation failure ==="
    $previousMemoryBuild = $env:CTILDE_MEMORY_VALIDATION_BUILD
    try {
        $env:CTILDE_MEMORY_VALIDATION_BUILD = "1"
        & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $memoryValidationSource -Flash
        if ($LASTEXITCODE -ne 0) { throw "Memory-validation firmware failed to build and flash with exit code $LASTEXITCODE." }
    }
    finally {
        if ($null -eq $previousMemoryBuild) { Remove-Item Env:CTILDE_MEMORY_VALIDATION_BUILD -ErrorAction SilentlyContinue }
        else { $env:CTILDE_MEMORY_VALIDATION_BUILD = $previousMemoryBuild }
    }
    $memoryCapture = Invoke-IdfMonitor @("-p", $Port, "monitor") { param($text) $text.Contains("CTILDE_MEMORY_OK") } 45
    $memoryTranscriptPath = Join-Path $artifactDirectory "$timestamp-memory.txt"
    Write-Utf8NoBom $memoryTranscriptPath (Select-FirmwareTranscript $memoryCapture.Transcript)
    $parseMemory = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.parseMemoryValidationTranscript(fs.readFileSync(process.argv[2],'utf8'))))))"
    $memoryResult = Invoke-NodeJson @("-e", $parseMemory, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $memoryTranscriptPath)
    $report.memoryValidation = [ordered]@{ result = $memoryResult; elapsedSeconds = $memoryCapture.ElapsedSeconds; transcript = $memoryTranscriptPath }

    $actualBaselineInput = Join-Path $workDirectory "memory-actual.json"
    $targetMeasurements = [ordered]@{
        binaryBytes = [int64]$size.binaryBytes
        imageBytes = [int64]$size.imageBytes
        flashCode = [int64]$size.sections.flashCode
        flashData = [int64]$size.sections.flashData
        iram = [int64]$size.sections.iram
        dram = [int64]$size.sections.dram
    }
    $actualBaseline = [ordered]@{
        tools = [ordered]@{
            idf = $report.tools.idf
            compiler = (Invoke-Captured "xtensa-esp32-elf-gcc" @("-dumpfullversion") $ProjectDirectory).Trim()
        }
        targets = [ordered]@{ esp32 = $targetMeasurements }
        hardware = $firmware
        layout = $memoryResult.layout
    }
    Write-Utf8NoBom $actualBaselineInput (($actualBaseline | ConvertTo-Json -Depth 30) + [Environment]::NewLine)
    if ($AcceptMemoryBaseline) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $memoryBaselinePath) -Force | Out-Null
        $updateBaseline = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>{const old=fs.existsSync(process.argv[2])?JSON.parse(fs.readFileSync(process.argv[2],'utf8')):null;const actual=JSON.parse(fs.readFileSync(process.argv[3],'utf8'));process.stdout.write(m.serializeHardwareReport(m.updateMemoryBaseline(old,actual)));}))"
        $updatedBaseline = Invoke-Captured "node" @("-e", $updateBaseline, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $memoryBaselinePath, $actualBaselineInput)
        Write-Utf8NoBom $memoryBaselinePath $updatedBaseline
    }
    elseif (-not (Test-Path -LiteralPath $memoryBaselinePath)) {
        throw "ESP memory baseline is missing. Run this acceptance once with -AcceptMemoryBaseline after reviewing measurements."
    }
    else {
        $validateBaseline = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>m.validateMemoryBaseline(JSON.parse(fs.readFileSync(process.argv[2],'utf8')),JSON.parse(fs.readFileSync(process.argv[3],'utf8')))))"
        Invoke-Checked "node" @("-e", $validateBaseline, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $memoryBaselinePath, $actualBaselineInput)
    }

    Write-Host "`n=== Exact COM4 USB-to-UART console bytes ==="
    & $buildScript -IdfPath $IdfPath -Target esp32 -Port $Port -Source $consoleValidationSource -Flash
    if ($LASTEXITCODE -ne 0) { throw "Console-validation firmware failed to build and flash with exit code $LASTEXITCODE." }
    $consoleCapture = Invoke-PassiveSerialByteCapture {
        param($bytes)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $ascii.Contains("CTILDE_CONSOLE_OK`n") -or $ascii.Contains("CTILDE_CONSOLE_OK`r`n")
    } 20
    $consoleBytesPath = Join-Path $artifactDirectory "$timestamp-console.bin"
    [IO.File]::WriteAllBytes($consoleBytesPath, $consoleCapture.Bytes)
    $parseConsole = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.extractConsoleFixture(fs.readFileSync(process.argv[2]))))))"
    $consoleResult = Invoke-NodeJson @("-e", $parseConsole, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $consoleBytesPath)
    $report.consoleValidation = [ordered]@{ result = $consoleResult; elapsedSeconds = $consoleCapture.ElapsedSeconds; rawBytes = $consoleBytesPath; device = $report.tools.port }

    Write-Host "`n=== Fatal runtime boundary ==="
    Invoke-Checked "dotnet" @(
        "run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--",
        $runtimeFailureSource, "--c-layout", "modules", "--output-directory", (Join-Path $ProjectDirectory "main\generated"),
        "--header", (Join-Path $ProjectDirectory "main\generated\ctilde_exports.h"), "--target", "esp-idf", "--trace"
    )
    $fatalDefaultsText = Get-Content -LiteralPath (Join-Path $ProjectDirectory "sdkconfig.defaults") -Raw
    $fatalDefaultsText = $fatalDefaultsText.Replace("CONFIG_ESP_SYSTEM_GDBSTUB_RUNTIME=y", "# CONFIG_ESP_SYSTEM_GDBSTUB_RUNTIME is not set")
    $fatalDefaultsText = $fatalDefaultsText.Replace("CONFIG_ESP_GDBSTUB_SUPPORT_TASKS=y", "# CONFIG_ESP_GDBSTUB_SUPPORT_TASKS is not set")
    $fatalDefaultsText += "`n# CONFIG_ESP_GDBSTUB_ENABLED is not set`nCONFIG_ESP_SYSTEM_PANIC_PRINT_REBOOT=y`n"
    Write-Utf8NoBom $fatalDefaults $fatalDefaultsText
    $previousFatalBuild = $env:CTILDE_FATAL_RUNTIME_BUILD
    try {
        $env:CTILDE_FATAL_RUNTIME_BUILD = "1"
        Invoke-Checked "idf.py" @(
            "-B", $fatalBuildDirectory,
            "-D", "SDKCONFIG=$fatalSdkconfig",
            "-D", "SDKCONFIG_DEFAULTS=$fatalDefaults",
            "set-target", "esp32"
        ) $ProjectDirectory
        Invoke-Checked "idf.py" @("-B", $fatalBuildDirectory, "build") $ProjectDirectory
        Invoke-Checked "idf.py" @("-B", $fatalBuildDirectory, "-p", $Port, "flash") $ProjectDirectory
    }
    finally {
        if ($null -eq $previousFatalBuild) { Remove-Item Env:CTILDE_FATAL_RUNTIME_BUILD -ErrorAction SilentlyContinue }
        else { $env:CTILDE_FATAL_RUNTIME_BUILD = $previousFatalBuild }
    }
    $failureCapture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        ($text.Contains("SW_CPU_RESET") -or $text.Contains("Rebooting...")) -and $text.Contains("CTN0001")
    } 45
    $failureTranscriptPath = Join-Path $artifactDirectory "$timestamp-runtime-failure.txt"
    Write-Utf8NoBom $failureTranscriptPath (Select-FirmwareTranscript $failureCapture.Transcript)
    $parseFailure = "Promise.all([import('node:url'),import('node:fs')]).then(([u,fs])=>import(u.pathToFileURL(process.argv[1]).href).then(m=>console.log(JSON.stringify(m.parseRuntimeFailureTranscript(fs.readFileSync(process.argv[2],'utf8'))))))"
    $failure = Invoke-NodeJson @("-e", $parseFailure, (Join-Path $PSScriptRoot "Esp32HardwareSupport.mjs"), $failureTranscriptPath)
    $report.runtimeFailure = [ordered]@{ result = $failure; elapsedSeconds = $failureCapture.ElapsedSeconds; transcript = $failureTranscriptPath }

    Write-Host "`n=== ESP-IDF restart panic policy ==="
    Invoke-Checked "dotnet" @(
        "run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--",
        $runtimeFailureSource, "--c-layout", "modules", "--output-directory", (Join-Path $ProjectDirectory "main\generated"),
        "--header", (Join-Path $ProjectDirectory "main\generated\ctilde_exports.h"), "--target", "esp-idf",
        "--panic-policy", "restart", "--trace"
    )
    Write-Utf8NoBom $restartDefaults $fatalDefaultsText
    try {
        $env:CTILDE_FATAL_RUNTIME_BUILD = "1"
        Invoke-Checked "idf.py" @("-B", $restartBuildDirectory, "-D", "SDKCONFIG=$restartSdkconfig", "-D", "SDKCONFIG_DEFAULTS=$restartDefaults", "set-target", "esp32") $ProjectDirectory
        Invoke-Checked "idf.py" @("-B", $restartBuildDirectory, "build") $ProjectDirectory
        Invoke-Checked "idf.py" @("-B", $restartBuildDirectory, "-p", $Port, "flash") $ProjectDirectory
    }
    finally {
        if ($null -eq $previousFatalBuild) { Remove-Item Env:CTILDE_FATAL_RUNTIME_BUILD -ErrorAction SilentlyContinue }
        else { $env:CTILDE_FATAL_RUNTIME_BUILD = $previousFatalBuild }
    }
    $restartCapture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        $text.Contains("CTN0001") -and (
            $text.Contains("SW_CPU_RESET") -or
            $text.Contains("rst:") -or
            ([regex]::Matches($text, "CTILDE_ESP_FAILURE_TEST").Count -ge 2) -or
            ([regex]::Matches($text, "2nd stage bootloader").Count -ge 2))
    } 45
    $restartTranscript = Select-FirmwareTranscript $restartCapture.Transcript
    $restartObserved = $restartTranscript.Contains("SW_CPU_RESET") -or
        $restartTranscript.Contains("rst:") -or
        ([regex]::Matches($restartTranscript, "CTILDE_ESP_FAILURE_TEST").Count -ge 2) -or
        ([regex]::Matches($restartTranscript, "2nd stage bootloader").Count -ge 2)
    if (-not $restartTranscript.Contains("CTN0001") -or
        -not $restartObserved) {
        throw "The restart panic policy did not emit its diagnostic and reset the target.`n$restartTranscript"
    }
    $restartTranscriptPath = Join-Path $artifactDirectory "$timestamp-panic-restart.txt"
    Write-Utf8NoBom $restartTranscriptPath $restartTranscript
    $report.panicPolicies.restart = [ordered]@{ elapsedSeconds = $restartCapture.ElapsedSeconds; transcript = $restartTranscriptPath }

    Write-Host "`n=== ESP-IDF halt panic policy ==="
    Invoke-Checked "dotnet" @(
        "run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--",
        $runtimeFailureSource, "--c-layout", "modules", "--output-directory", (Join-Path $ProjectDirectory "main\generated"),
        "--header", (Join-Path $ProjectDirectory "main\generated\ctilde_exports.h"), "--target", "esp-idf",
        "--panic-policy", "halt", "--trace"
    )
    $haltDefaultsText = $fatalDefaultsText.Replace("CONFIG_ESP_SYSTEM_PANIC_PRINT_REBOOT=y", "CONFIG_ESP_SYSTEM_PANIC_PRINT_HALT=y")
    Write-Utf8NoBom $haltDefaults $haltDefaultsText
    try {
        $env:CTILDE_FATAL_RUNTIME_BUILD = "1"
        Invoke-Checked "idf.py" @("-B", $haltBuildDirectory, "-D", "SDKCONFIG=$haltSdkconfig", "-D", "SDKCONFIG_DEFAULTS=$haltDefaults", "set-target", "esp32") $ProjectDirectory
        Invoke-Checked "idf.py" @("-B", $haltBuildDirectory, "build") $ProjectDirectory
        Invoke-Checked "idf.py" @("-B", $haltBuildDirectory, "-p", $Port, "flash") $ProjectDirectory
    }
    finally {
        if ($null -eq $previousFatalBuild) { Remove-Item Env:CTILDE_FATAL_RUNTIME_BUILD -ErrorAction SilentlyContinue }
        else { $env:CTILDE_FATAL_RUNTIME_BUILD = $previousFatalBuild }
    }
    $haltCapture = Invoke-IdfMonitor @("-p", $Port, "monitor") {
        param($text)
        $text.Contains("CTN0001") -and ($text.Contains("CPU halted") -or $text.Contains("System halted"))
    } 45
    $haltTranscript = Select-FirmwareTranscript $haltCapture.Transcript
    if (-not $haltTranscript.Contains("CTN0001") -or
        (-not $haltTranscript.Contains("CPU halted") -and -not $haltTranscript.Contains("System halted")) -or
        $haltTranscript.Contains("Rebooting...")) {
        throw "The halt panic policy did not enter the configured ESP-IDF halt path.`n$haltTranscript"
    }
    $haltTranscriptPath = Join-Path $artifactDirectory "$timestamp-panic-halt.txt"
    Write-Utf8NoBom $haltTranscriptPath $haltTranscript
    $report.panicPolicies.halt = [ordered]@{ elapsedSeconds = $haltCapture.ElapsedSeconds; transcript = $haltTranscriptPath }

    Write-Host "`n=== Instrumented debugger v3 ==="
    Invoke-Checked "dotnet" @(
        "run", "--project", ".\CTilde.Cli", "-c", "Release", "--no-build", "--",
        "--project", $projectManifest, "--prepare-debug", "launch", "--debug-target", $debugDescriptorPath,
        "--debug-memory", "guarded", "--idf-path", $IdfPath, "--serial-port", $Port, "--baud-rate", "$BaudRate"
    )
    Invoke-Checked "node" @(
        $debugHarness, "--adapter", $adapterPath, "--descriptor", $debugDescriptorPath, "--source", $programSource,
        "--report", $debugReportPath, "--port", $Port, "--baud", "$BaudRate"
    )
    $debugReport = Get-Content -LiteralPath $debugReportPath -Raw | ConvertFrom-Json
    if (-not $debugReport.passed) { throw "Debugger-v3 hardware harness failed: $($debugReport.error)" }
    $report.debugger = $debugReport

    Write-Host "`n=== Post-detach continuation ==="
    $postDetachCapture = Invoke-PassiveSerialCapture {
        param($text)
        ([regex]::Matches($text, "(?m)^ws2812:\s*(?:on|off)\s*$")).Count -ge 4
    } 15
    if ($postDetachCapture.Transcript -match 'rst:|Guru Meditation|panic|CTILDE runtime error') {
        throw "Post-detach monitor observed a reset or runtime failure."
    }
    $report.postDetach = [ordered]@{ transitions = 4; elapsedSeconds = $postDetachCapture.ElapsedSeconds; resetObserved = $false }

    Write-Host "`n=== Instrumented startup timeout without debugger ==="
    Invoke-Checked "idf.py" @("-p", $Port, "flash") $ProjectDirectory
    $timeoutCapture = Invoke-PassiveSerialCapture {
        param($text)
        $text.Contains("esp error: ESP_OK")
    } 25
    if ($timeoutCapture.ElapsedSeconds -lt 12 -or $timeoutCapture.ElapsedSeconds -gt 22) {
        throw "No-debugger startup gate released after $([Math]::Round($timeoutCapture.ElapsedSeconds, 2)) seconds; expected 12-22 seconds."
    }
    $report.startupTimeout = [ordered]@{ elapsedSeconds = $timeoutCapture.ElapsedSeconds; expectedSeconds = 15; passed = $true }

    $report.automatedPassed = $true
    if ($AutomatedOnly -or $report.visualLed -ne "confirmed") {
        Write-Warning "All automated checks passed, but visible LED confirmation is pending. This run does not close the release gate."
    }
    else {
        $report.passed = $true
    }
}
catch {
    $report.error = $_.Exception.ToString()
    Write-Host $_.Exception.ToString() -ForegroundColor Red
}
finally {
    Stop-ProjectMonitorProcesses
    Restore-ReleaseFirmware
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $report.restore.passed) { $report.passed = $false }
    Save-Report
    Write-Host "Hardware report: $reportPath"
}

if (-not $report.automatedPassed -or -not $report.restore.passed) { exit 1 }
