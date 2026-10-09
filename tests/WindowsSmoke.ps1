param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app")
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") {
    throw "This smoke check requires Windows and an interactive desktop."
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) {
    throw "Build with build.ps1 first, or supply -PublishedDirectory."
}

# Only an isolated copy is configured. SISR and artwork stay disabled, and the
# GUI is closed without importing/removing shortcuts or invoking Steam actions.
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge smoke " + [Guid]::NewGuid().ToString("N"))
$gui = $null
$launch = $null
$heldConfig = $null
$previousDataDirectory = $env:SBRIDGE_TEST_DATA_DIRECTORY
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SBridgeSettingsSmokeWindow {
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public static IntPtr Find(int pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint id; GetWindowThreadProcessId(window, out id);
            if (id == pid) {
                var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity);
                if (text.ToString() == title) { found = window; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Close(IntPtr window) { return PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); }
}
'@
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    $exe = Join-Path $staging "sBridge.exe"
    $legacyPath = Join-Path $staging "sBridge.cfg"
    $configPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json"
    $logPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"
    $gamePath = Join-Path $staging "Game With Spaces.exe"
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot "System32\cmd.exe") -Destination $gamePath
    [IO.File]::WriteAllLines($legacyPath, @(
        "SisrPath=C:\Not Installed\SISR.exe",
        "SisrArguments=--smoke-sentinel",
        "SisrEnabled=false",
        "SgdbApiKey=",
        "LogEnabled=true",
        "Sisr_Example_123!Game=false",
        "Watch_Example_123!Game=Game.exe",
        "# retained custom comment",
        "FutureSetting=Keep=Value"
    ))
    $legacyBytes = [IO.File]::ReadAllBytes($legacyPath)

    $gui = Start-Process -FilePath $exe -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 200
        $gui.Refresh()
        if ($gui.HasExited) { throw "Settings exited before opening a window." }
    } while ($gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.MainWindowTitle -ne "sBridge Settings") {
        throw "Expected settings window; got '$($gui.MainWindowTitle)'."
    }
    if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) {
        throw "Settings did not close normally."
    }
    if ($gui.ExitCode -ne 0) { throw "Settings exited with $($gui.ExitCode)." }

    $saved = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
    if ($saved.schemaVersion -ne 1 -or $saved.sisrPath -cne "C:\Not Installed\SISR.exe" -or
        $saved.sisrArguments -cne "--smoke-sentinel" -or $saved.sisrEnabled -ne $false -or $saved.logEnabled -ne $true -or
        $saved.gameProfiles.'Example_123!Game'.steamInput -cne "disabled" -or
        $saved.gameProfiles.'Example_123!Game'.watchProcess -cne "Game.exe") { throw "JSON migration did not preserve settings/profile choices." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($legacyPath)) -cne [Convert]::ToBase64String($legacyBytes)) {
        throw "Legacy source/comments/unknown keys were modified during migration."
    }
    Write-Host "PASS: JSON migration/startup/save preserve settings and leave the legacy source unchanged."

    # A direct matched game uses bounded exit grace. Allow extra time for Windows
    # startup/scan delays; session semantics have their own handoff checks.
    $launch = Start-Process -FilePath $exe -ArgumentList ('"' + $gamePath + '" /c exit 0') -WorkingDirectory $staging -PassThru
    if (-not $launch.WaitForExit(130000)) { throw "Legacy custom launch did not complete within 130 seconds." }
    if ($launch.ExitCode -ne 0) { throw "Custom launch exited with $($launch.ExitCode)." }
    $log = [IO.File]::ReadAllText($logPath)
    if (-not $log.Contains("Launching custom game: Path=$gamePath, Args=/c exit 0") -or
        -not $log.Contains("Custom game launched with PID:") -or
        -not $log.Contains("CustomGame=True, SISR=False") -or
        -not $log.Contains("Bridge exiting successfully.")) {
        throw "Expected successful SISR-disabled custom launch evidence was missing."
    }
    if ($log.Contains("Launching SISR:") -or $log.Contains("Launching owned SISR:") -or $log.Contains("Resetting forced Steam") -or $log.Contains("Killing running")) {
        throw "Disabled SISR launch unexpectedly attempted controller cleanup/startup."
    }
    Write-Host "PASS: SISR-disabled custom launch with spaces in the executable path completes."

    $launch.Dispose(); $launch = $null
    $validBytes = [IO.File]::ReadAllBytes($configPath)
    $invalidBytes = [Text.Encoding]::UTF8.GetBytes('{"schemaVersion":99}')
    [IO.File]::WriteAllBytes($configPath, $invalidBytes)
    $gui.Dispose(); $gui = $null
    $gui = Start-Process -FilePath $exe -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() }
    while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Exception" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Exception") { throw "Unsupported JSON settings did not produce a startup error." }
    if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) { throw "Malformed-settings error did not close." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -cne [Convert]::ToBase64String($invalidBytes)) {
        throw "Unsupported JSON settings were overwritten instead of rejected."
    }
    Write-Host "PASS: unsupported JSON schema is rejected without falling back to valid legacy settings."

    $gui.Dispose(); $gui = $null
    [IO.File]::WriteAllBytes($configPath, $validBytes)
    # Permit loading/validation but deny replacement through real Windows sharing
    # semantics. An owned modal dialog is not necessarily Process.MainWindowTitle.
    $heldConfig = New-Object IO.FileStream($configPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $gui = Start-Process -FilePath $exe -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() }
    while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Settings") { throw "Locked settings did not open." }
    if (-not $gui.CloseMainWindow()) { throw "Could not request closing locked settings." }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 100; $gui.Refresh()
        $errorWindow = [SBridgeSettingsSmokeWindow]::Find($gui.Id, "Error Saving Settings")
    } while (-not $gui.HasExited -and $errorWindow -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $errorWindow -eq [IntPtr]::Zero) { throw "Save failure did not produce the expected error notification." }
    if (-not [SBridgeSettingsSmokeWindow]::Close($errorWindow) -or -not $gui.WaitForExit(10000)) { throw "Locked save error did not close normally (possible modal reentry)." }
    $heldConfig.Dispose(); $heldConfig = $null
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -cne [Convert]::ToBase64String($validBytes)) {
        throw "Locked original settings were changed."
    }
    Write-Host "PASS: locked save error is visible, closes without reentry, and leaves the original intact."
} finally {
    if ($null -ne $heldConfig) { $heldConfig.Dispose() }
    foreach ($process in @($gui, $launch)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousDataDirectory
}
