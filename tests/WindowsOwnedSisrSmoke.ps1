param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app")
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires Windows and an interactive desktop." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) { throw "Run build.ps1 first." }
if (Get-Process -Name SISR -ErrorAction SilentlyContinue) {
    throw "An existing SISR instance is running; leave it untouched and run this isolated check after it exits."
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge owned smoke " + [Guid]::NewGuid().ToString("N"))
$foreign = $null
$bridge = $null
$previousDataDirectory = $env:SBRIDGE_TEST_DATA_DIRECTORY
function Close-ErrorDialog([Diagnostics.Process]$process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "Bridge exited before its expected error dialog." }
    } while ($process.MainWindowTitle -ne "sBridge Exception" -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowTitle -ne "sBridge Exception") { throw "Expected launch error dialog did not appear." }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) { throw "Error dialog did not close." }
}

try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    $exe = Join-Path $staging "sBridge.exe"
    $standIn = Join-Path $staging "SISR.exe"
    $logPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"
    $missingGame = Join-Path $staging "Missing Game.exe"
    # Renamed apphost still loads sBridge.dll. With no arguments it is just an
    # isolated settings window: a controlled named-process stand-in, not SISR,
    # Steam, VIIPER, an API simulator, or controller emulation.
    Copy-Item -LiteralPath $exe -Destination $standIn
    [IO.File]::WriteAllLines((Join-Path $staging "sBridge.cfg"), @(
        "SisrPath=$standIn", "SisrArguments=", "SisrEnabled=true", "SgdbApiKey=", "LogEnabled=true"
    ))

    $foreign = Start-Process -FilePath $standIn -WorkingDirectory $staging -PassThru
    $bridge = Start-Process -FilePath $exe -ArgumentList ('"' + $missingGame + '"') -WorkingDirectory $staging -PassThru
    Close-ErrorDialog $bridge
    $foreign.Refresh()
    if ($foreign.HasExited) { throw "Unowned stand-in was terminated." }
    $log = [IO.File]::ReadAllText($logPath)
    if (-not $log.Contains("SISR is already running") -or $log.Contains("Started owned SISR PID=")) {
        throw ("Expected external-instance refusal without startup was missing.`n" + $log)
    }
    Write-Host "PASS: external named process is left untouched and no game is launched."
    $bridge.Dispose(); $bridge = $null

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($foreign.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100; $foreign.Refresh()
    }
    if (-not $foreign.CloseMainWindow() -or -not $foreign.WaitForExit(10000)) { throw "Stand-in settings window did not close." }
    $foreign.Dispose(); $foreign = $null

    [IO.File]::WriteAllText($logPath, "")
    $bridge = Start-Process -FilePath $exe -ArgumentList ('"' + $missingGame + '"') -WorkingDirectory $staging -PassThru
    # Main must clean up the started stand-in in finally BEFORE showing the
    # blocking exception dialog for the missing custom game.
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do { Start-Sleep -Milliseconds 100; $bridge.Refresh() }
    while ($bridge.MainWindowTitle -ne "sBridge Exception" -and [DateTime]::UtcNow -lt $deadline)
    if ($bridge.MainWindowTitle -ne "sBridge Exception") { throw "Missing-game error dialog did not appear." }
    $log = [IO.File]::ReadAllText($logPath)
    if (-not $log.Contains("Started owned SISR PID=") -or -not $log.Contains("Failed to launch custom game")) {
        throw "Owned startup and game launch failure evidence was missing."
    }
    foreach ($process in @(Get-Process -Name SISR -ErrorAction SilentlyContinue)) {
        try { if ($process.Path -eq $standIn) { throw "Owned stand-in is still alive while the launch error dialog is displayed." } }
        finally { $process.Dispose() }
    }
    Close-ErrorDialog $bridge
    Write-Host "PASS: launch failure cleans owned process before the error dialog."
} finally {
    foreach ($process in @($foreign, $bridge)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    # On assertion failure, only a stand-in at this test's unique executable path
    # may be stopped. Never stop a user's installed SISR by name.
    foreach ($process in @(Get-Process -Name SISR -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -eq (Join-Path $staging "SISR.exe")) { $process.Kill(); $process.WaitForExit() }
        } finally { $process.Dispose() }
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousDataDirectory
}
