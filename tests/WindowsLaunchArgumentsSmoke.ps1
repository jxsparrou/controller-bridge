param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"),
    [string]$ProbeDirectory = (Join-Path $PSScriptRoot "sBridge.Tests\bin\Release\net10.0")
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "This check requires Windows." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) { throw "Run build.ps1 first." }
if (-not (Test-Path -LiteralPath (Join-Path $ProbeDirectory "sBridge.Tests.exe"))) {
    throw "Build sBridge.slnx in Release first to produce the test-only argument probe."
}

function Encode-TransportArgument([string]$value) {
    # Independently encoded incoming CLI, not the product forwarding under test.
    # Quote all values and use regex transforms rather than the production loop.
    $escaped = [regex]::Replace($value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge arguments " + [Guid]::NewGuid().ToString("N"))
$bridge = $null
$previousDataDirectory = $env:SBRIDGE_TEST_DATA_DIRECTORY
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    $gameDirectory = Join-Path $staging "Game With Spaces"
    New-Item -ItemType Directory -Path $gameDirectory | Out-Null
    Copy-Item -Path (Join-Path $ProbeDirectory "*") -Destination $gameDirectory -Recurse
    $game = Join-Path $gameDirectory "Argument Probe.exe"
    Copy-Item -LiteralPath (Join-Path $gameDirectory "sBridge.Tests.exe") -Destination $game
    $capture = Join-Path $staging "Captured Arguments.json"
    [IO.File]::WriteAllLines((Join-Path $staging "sBridge.cfg"), @("SisrEnabled=false", "SgdbApiKey=", "LogEnabled=true"))
    # Keep the script ASCII for Windows PowerShell 5; construct Unicode explicitly.
    $unicode = 'Caf' + [char]0x00E9 + ' ' + [char]0x904A + [char]0x6232 + ' ' + [char]::ConvertFromUtf32(0x1F3AE)
    $expected = @("Player One", "", 'quote"inside', 'C:\Trailing Space\', 'slashes\\"quote', "tab`tvalue", $unicode)
    $tokens = @($game, "--write-arguments", $capture) + $expected
    $incoming = ($tokens | ForEach-Object { Encode-TransportArgument $_ }) -join " "
    $bridge = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -ArgumentList $incoming -WorkingDirectory $staging -PassThru
    # The direct probe uses bounded exit grace; retain a generous outer timeout
    # for slow Windows startup/observation rather than assuming exact timing.
    if (-not $bridge.WaitForExit(130000)) { throw "Bridge did not complete its monitor within 130 seconds." }
    if ($bridge.ExitCode -ne 0) { throw "Bridge exited with $($bridge.ExitCode)." }
    # Windows PowerShell 5 emits a JSON array as one pipeline object. Assign it
    # directly rather than wrapping the pipeline in another array expression.
    $actual = [IO.File]::ReadAllText($capture) | ConvertFrom-Json
    if ($actual.Count -ne $expected.Count) { throw "Argument count changed: expected $($expected.Count), got $($actual.Count)." }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if ($actual[$i] -cne $expected[$i]) { throw "Argument $i changed. Expected '$($expected[$i])', got '$($actual[$i])'." }
    }
    $log = [IO.File]::ReadAllText((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"))
    if (-not $log.Contains("Bridge exiting successfully.") -or -not $log.Contains("SISR=False")) {
        throw "Successful SISR-disabled launch evidence was missing."
    }
    Write-Host "PASS: Main and Win32 shell launch preserve spaces, empty values, quotes, backslashes, tabs, and Unicode."
} finally {
    if ($null -ne $bridge) {
        if (-not $bridge.HasExited) { $bridge.Kill(); $bridge.WaitForExit() }
        $bridge.Dispose()
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousDataDirectory
}
