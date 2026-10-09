param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"),
    [string]$ProbeDirectory = (Join-Path $PSScriptRoot "sBridge.Tests\bin\Release\net10.0")
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires Windows." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) { throw "Run build.ps1 first." }
if (-not (Test-Path -LiteralPath (Join-Path $ProbeDirectory "sBridge.Tests.exe"))) { throw "Build the Release solution first." }
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge handoff " + [Guid]::NewGuid().ToString("N"))
$bridge = $null
$created = [DateTime]::UtcNow
$events = Join-Path $staging "Processes.txt"
$previousDataDirectory = $env:SBRIDGE_TEST_DATA_DIRECTORY
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    $gameDirectory = Join-Path $staging "Controlled Game"
    New-Item -ItemType Directory -Path $gameDirectory | Out-Null
    Copy-Item -Path (Join-Path $ProbeDirectory "*") -Destination $gameDirectory -Recurse
    foreach ($name in @("Launcher", "Bootstrap", "Game")) {
        Copy-Item -LiteralPath (Join-Path $gameDirectory "sBridge.Tests.exe") -Destination (Join-Path $gameDirectory ($name + ".exe"))
    }
    $launcher = Join-Path $gameDirectory "Launcher.exe"
    $bootstrap = Join-Path $gameDirectory "Bootstrap.exe"
    $game = Join-Path $gameDirectory "Game.exe"
    [IO.File]::WriteAllLines((Join-Path $staging "sBridge.cfg"), @(
        "SisrEnabled=false", "SgdbApiKey=", "LogEnabled=true", "Watch_$launcher=$game"
    ))
    $arguments = '"' + $launcher + '" --session-chain "' + $events + '" 1500 1500 2500 "' + $bootstrap + '" "' + $game + '"'
    $bridge = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -ArgumentList $arguments -WorkingDirectory $staging -PassThru
    if (-not $bridge.WaitForExit(30000)) { throw "Controlled handoff session did not finish within 30 seconds." }
    $ids = [IO.File]::ReadAllLines($events)
    if ($ids.Count -ne 3) { throw "Expected three controlled process stages." }
    $log = [IO.File]::ReadAllText((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"))
    if (-not $log.Contains("Matched PID=$($ids[1])") -or -not $log.Contains("Matched PID=$($ids[2])") -or
        -not $log.Contains("provisional bootstrap") -or -not $log.Contains("Bridge exiting successfully.")) {
        throw ("Expected two handoffs and successful completion were missing from the session log. Expected PIDs: " + ($ids -join ", ") + "`n" + $log)
    }
    if ($log.Contains("Launching owned SISR:")) { throw "Disabled integration unexpectedly started SISR." }
    Write-Host "PASS: published Main follows launcher -> bootstrap -> game and exits after the actual game."
} finally {
    if ($null -ne $bridge) {
        if (-not $bridge.HasExited) { $bridge.Kill(); $bridge.WaitForExit() }
        $bridge.Dispose()
    }
    if (Test-Path -LiteralPath $events) {
        foreach ($id in [IO.File]::ReadAllLines($events)) {
            $process = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                try {
                    if ($process.StartTime.ToUniversalTime() -ge $created -and
                        $process.Path.StartsWith((Join-Path $staging "Controlled Game") + "\", [StringComparison]::OrdinalIgnoreCase)) {
                        $process.Kill(); $process.WaitForExit()
                    }
                } finally { $process.Dispose() }
            }
        }
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousDataDirectory
}
