param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"),
    [string]$ProbeDirectory = (Join-Path $PSScriptRoot "sBridge.Tests\bin\Release\net10.0")
)
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires an interactive Windows desktop." }
if (-not (Test-Path -LiteralPath (Join-Path $ProbeDirectory "sBridge.Tests.exe"))) { throw "Build the Release solution first." }
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge managed " + [Guid]::NewGuid().ToString("N"))
$previousData = $env:SBRIDGE_TEST_DATA_DIRECTORY
$launch = $null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SBridgeManagedSmokeWindow {
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public static IntPtr Find(int pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint id; GetWindowThreadProcessId(window, out id);
            if (id == pid) { var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity); if (text.ToString() == title) { found = window; return false; } }
            return true;
        }, IntPtr.Zero); return found;
    }
    public static bool Close(IntPtr window) { return PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); }
}
'@
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $probe = Join-Path $staging "Probe"
    New-Item -ItemType Directory -Path $probe | Out-Null
    Copy-Item -Path (Join-Path $ProbeDirectory "*") -Destination $probe -Recurse
    $sisr = Join-Path $probe "ManagedSisrProbe.exe"
    $game = Join-Path $probe "ManagedGame.exe"
    Copy-Item -LiteralPath (Join-Path $probe "sBridge.Tests.exe") -Destination $sisr
    Copy-Item -LiteralPath (Join-Path $probe "sBridge.Tests.exe") -Destination $game
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    New-Item -ItemType Directory -Path $env:SBRIDGE_TEST_DATA_DIRECTORY | Out-Null
    $capture = Join-Path $staging "sisr-start.json"
    $argv = Join-Path $staging "game-args.json"
    $configPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json"
    $settings = @{ schemaVersion=1; sisrPath=$sisr; sisrArguments=('--sisr-api-probe "' + $capture + '"'); sisrEnabled=$true; managedSisrStartup=$true;
        logEnabled=$true; protectedSteamGridDbApiKey=$null; gameProfiles=@{}; games=@{}; selectedSteamAccountIds=@() }
    $settings.gameProfiles[$game] = @{ steamInput="enabled"; watchProcess=""; controller=@{ controllerType="dualSenseEdge";
        gyroPassthrough=$false; touchpadPassthrough=$false; backButtonPassthrough=$true } }
    [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $options = '"' + $game + '" --write-arguments "' + $argv + '" "Player One"'
    $launch = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -ArgumentList $options -WorkingDirectory $staging -PassThru
    if (-not $launch.WaitForExit(30000)) { throw "Managed launch did not finish." }
    if (-not (Test-Path -LiteralPath $argv)) { throw "Ready managed SISR did not permit game activation." }
    $received = [IO.File]::ReadAllText($argv) | ConvertFrom-Json
    if ($received.Count -ne 1 -or ($received -join "") -ne "Player One") { throw "Managed startup changed game argument forwarding." }
    $started = [IO.File]::ReadAllText($capture) | ConvertFrom-Json
    if (-not $started.cwd.StartsWith((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "sisr\sessions"), [StringComparison]::OrdinalIgnoreCase)) { throw "Managed SISR did not use its isolated session cwd." }
    if (-not (Test-Path -LiteralPath (Join-Path $started.cwd "startup.json"))) { throw "Owned startup configuration was missing." }
    $startup = [IO.File]::ReadAllText((Join-Path $started.cwd "startup.json")) | ConvertFrom-Json
    if ($startup.default_controller_type -ne "dualsenseedge" -or $startup.gyro_passthrough -ne $false -or $startup.touchpad_passthrough -ne $false -or $startup.back_button_passthrough -ne $true) { throw "Per-game profile was not applied to owned startup config." }
    $logPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"
    $log = [IO.File]::ReadAllText($logPath)
    if ($log.IndexOf("Owned SISR API ready:") -lt 0 -or $log.IndexOf("Owned SISR API ready:") -gt $log.IndexOf("Launching custom game:")) { throw "Readiness did not precede activation." }
    if (-not $log.Contains("Requested graceful quit") -or $log.Contains("Force-stopping")) { throw "Ready owned SISR was not shut down gracefully." }
    Write-Host "PASS: managed config/cwd and API readiness precede game activation; argument forwarding and graceful owned cleanup work."
    $launch.Dispose(); $launch = $null
    Remove-Item -LiteralPath $argv, $capture
    foreach ($failure in @("--unsupported-api", "--ignore-controller-profile", "legacy")) {
        $settings.managedSisrStartup = ($failure -ne "legacy")
        $settings.sisrArguments = '--sisr-api-probe "' + $capture + '"' + $(if ($failure -eq "legacy") { "" } else { " " + $failure })
        [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        $launch = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -ArgumentList $options -WorkingDirectory $staging -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do { Start-Sleep -Milliseconds 100; $dialog = [SBridgeManagedSmokeWindow]::Find($launch.Id, "sBridge Exception") } while ($dialog -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
        if ($dialog -eq [IntPtr]::Zero) { throw "SISR policy failure ($failure) did not report a launch error." }
        if (Test-Path -LiteralPath $argv) { throw "SISR policy failure ($failure) activated a game." }
        if ($failure -eq "legacy") {
            if (Test-Path -LiteralPath $capture) { throw "Structured legacy profile started SISR before rejection." }
        } else {
            $failed = [IO.File]::ReadAllText($capture) | ConvertFrom-Json
            $remaining = Get-Process -Id $failed.pid -ErrorAction SilentlyContinue
            if ($null -ne $remaining) { $remaining.Dispose(); throw "Owned SISR remained alive before the error dialog." }
            Remove-Item -LiteralPath $capture
        }
        if (-not [SBridgeManagedSmokeWindow]::Close($dialog) -or -not $launch.WaitForExit(10000)) { throw "Managed launch error did not close." }
        $launch.Dispose(); $launch=$null
        Write-Host "PASS: $failure blocks activation and leaves no owned SISR before the error dialog."
    }
} finally {
    if ($null -ne $launch) { if (-not $launch.HasExited) { $launch.Kill(); $launch.WaitForExit() }; $launch.Dispose() }
    # A failed harness may leave a recorded stand-in; verify its unique image path
    # before cleaning that test-owned root, never enumerate/stop installed SISR.
    if ($null -ne $capture -and (Test-Path -LiteralPath $capture)) {
        $record = [IO.File]::ReadAllText($capture) | ConvertFrom-Json
        $owned = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if ($null -ne $owned) { if ($owned.Path -eq $sisr) { $owned.Kill(); $owned.WaitForExit() }; $owned.Dispose() }
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousData
}
