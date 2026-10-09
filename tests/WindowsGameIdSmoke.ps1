param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"),
    [string]$ProbeDirectory = (Join-Path $PSScriptRoot "sBridge.Tests\bin\Release\net10.0")
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires Windows and an interactive desktop." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) { throw "Run build.ps1 first." }
if (-not (Test-Path -LiteralPath (Join-Path $ProbeDirectory "sBridge.Tests.exe"))) { throw "Build the Release solution first." }
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge ID smoke " + [Guid]::NewGuid().ToString("N"))
$previousDataDirectory = $env:SBRIDGE_TEST_DATA_DIRECTORY
$bridge = $null
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    New-Item -ItemType Directory -Path $env:SBRIDGE_TEST_DATA_DIRECTORY | Out-Null
    $gameDirectory = Join-Path $staging "Registered Game"
    New-Item -ItemType Directory -Path $gameDirectory | Out-Null
    Copy-Item -Path (Join-Path $ProbeDirectory "*") -Destination $gameDirectory -Recurse
    $gamePath = Join-Path $gameDirectory "Registered Game.exe"
    Copy-Item -LiteralPath (Join-Path $gameDirectory "sBridge.Tests.exe") -Destination $gamePath
    $capture = Join-Path $staging "Received Arguments.json"
    $id = [Guid]::NewGuid().ToString("D")
    $expected = @("Player One", "", 'quote"inside', 'C:\Trailing Space\', "tab`tvalue")
    $profiles = @{}; $profiles[$id] = @{steamInput="disabled"; watchProcess=""}
    # A conflicting legacy target choice verifies ID-scoped preference resolution.
    $profiles[$gamePath] = @{steamInput="enabled"; watchProcess=""}
    $games = @{}; $games[$id] = @{name="Registered Probe"; provider="win32"; providerId=$gamePath;
        launchKind="executable"; target=$gamePath; arguments=(@("--write-arguments", $capture) + $expected);
        processHint=$gamePath; installDirectory=$gameDirectory}
    $settings = @{schemaVersion=1; sisrPath="C:\Missing\SISR.exe"; sisrArguments=""; sisrEnabled=$true;
        logEnabled=$true; protectedSteamGridDbApiKey=$null; gameProfiles=$profiles; games=$games}
    $config = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json"
    [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    $original = [IO.File]::ReadAllBytes($config)
    $exe = Join-Path $staging "sBridge.exe"
    $bridge = Start-Process -FilePath $exe -ArgumentList ("launch " + $id) -WorkingDirectory $staging -PassThru
    if (-not $bridge.WaitForExit(25000)) { throw "Registered launch did not finish within 25 seconds." }
    if ($bridge.ExitCode -ne 0) { throw "Registered launch exited with $($bridge.ExitCode)." }
    $actual = [IO.File]::ReadAllText($capture) | ConvertFrom-Json
    if ($actual.Count -ne $expected.Count) { throw "Stored argument count was changed." }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if ($actual[$index] -cne $expected[$index]) { throw "Stored argument $index was changed." }
    }
    $logPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"
    $log = [IO.File]::ReadAllText($logPath)
    if (-not $log.Contains("Resolved game ID=$id") -or -not $log.Contains("SISR=False") -or
        -not $log.Contains("Bridge exiting successfully.") -or $log.Contains("Launching owned SISR:")) {
        throw "ID resolution/profile/session evidence was missing or wrong."
    }
    Write-Host "PASS: launch <UUID> resolves stored arguments and independent ID-scoped integration choice."
    $bridge.Dispose(); $bridge = $null
    [IO.File]::WriteAllText($logPath, "")
    $bridge = Start-Process -FilePath $exe -ArgumentList ("launch " + [Guid]::NewGuid().ToString("D")) -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 100; $bridge.Refresh() }
    while (-not $bridge.HasExited -and $bridge.MainWindowTitle -ne "sBridge Exception" -and [DateTime]::UtcNow -lt $deadline)
    if ($bridge.HasExited -or $bridge.MainWindowTitle -ne "sBridge Exception") { throw "Missing game ID did not produce the expected launch error." }
    if (-not $bridge.CloseMainWindow() -or -not $bridge.WaitForExit(10000)) { throw "Missing-ID error did not close." }
    $log = [IO.File]::ReadAllText($logPath)
    if (-not $log.Contains("game ID is not registered") -or $log.Contains("Launching owned SISR:") -or
        $log.Contains("Launching UWP app via COM") -or $log.Contains("Launching custom game:")) {
        throw "Missing-ID request fell through to a launch/integration action."
    }
    if ([Convert]::ToBase64String($original) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes($config))) {
        throw "ID launch unexpectedly rewrote the registry/settings document."
    }
    Write-Host "PASS: unknown ID fails before activation/integration and leaves the registry intact."
} finally {
    if ($null -ne $bridge) {
        if (-not $bridge.HasExited) { $bridge.Kill(); $bridge.WaitForExit() }
        $bridge.Dispose()
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousDataDirectory
}
