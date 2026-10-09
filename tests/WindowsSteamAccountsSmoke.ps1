param([string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"))
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires an interactive Windows desktop." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) { throw "Run build.ps1 first." }
if (@(Get-Process -Name steam -ErrorAction SilentlyContinue).Count -gt 0) { throw "Close Steam before running the account-write smoke check." }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SBridgeAccountSmokeWindow {
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public static bool Click(IntPtr window) { return PostMessage(window, 0xF5, IntPtr.Zero, IntPtr.Zero); }
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
}
'@
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge accounts " + [Guid]::NewGuid().ToString("N"))
$previousData = $env:SBRIDGE_TEST_DATA_DIRECTORY
$previousSteam = $env:SBRIDGE_TEST_STEAM_DIRECTORY
$gui = $null
function Element($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Set-Value($root, [string]$id, [string]$value) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Missing control $id" }
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Invoke-Import($root) {
    $button = Element $root "Add Custom Game to Steam"
    if (-not [SBridgeAccountSmokeWindow]::Click([IntPtr]$button.Current.NativeWindowHandle)) { throw "Could not click import." }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $dialog = $null
        foreach ($title in @("Success", "Steam Account Selection", "Shortcut Save Results", "Validation Error", "Steam is Running", "Error Saving Settings", "sBridge Exception")) {
            $handle = [SBridgeAccountSmokeWindow]::Find($gui.Id, $title)
            if ($handle -ne [IntPtr]::Zero) { $dialog = [System.Windows.Automation.AutomationElement]::FromHandle($handle); break }
        }
    } while ($null -eq $dialog -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $dialog) { throw "Import result dialog did not appear." }
    $title = $dialog.Current.Name
    $ok = Element $dialog "OK"
    if (-not [SBridgeAccountSmokeWindow]::Click([IntPtr]$ok.Current.NativeWindowHandle)) { throw "Could not dismiss the result dialog." }
    Start-Sleep -Milliseconds 100
    return $title
}
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    $env:SBRIDGE_TEST_STEAM_DIRECTORY = Join-Path $staging "Synthetic Steam"
    foreach ($id in @("1", "2", "3")) { New-Item -ItemType Directory -Path (Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\$id") -Force | Out-Null }
    $first = Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\1\config\shortcuts.vdf"
    $second = Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\2\config\shortcuts.vdf"
    $third = Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\3\config\shortcuts.vdf"
    New-Item -ItemType Directory -Path (Split-Path $first), (Split-Path $third) | Out-Null
    [byte[]]$empty = @(0) + [Text.Encoding]::UTF8.GetBytes("shortcuts") + @(0,8,8)
    [IO.File]::WriteAllBytes($first, $empty)
    [byte[]]$unselected = @(0,1,2)
    [IO.File]::WriteAllBytes($third, $unselected)
    [IO.File]::WriteAllLines((Join-Path $staging "sBridge.cfg"), @("SisrEnabled=false", "SgdbApiKey=", "LogEnabled=true"))
    $gui = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() } while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Settings") { throw "Settings did not open." }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($gui.MainWindowHandle)
    (Element $root "Add Custom Game").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Set-Value $root "CustomGameName" "Account scope game"
    Set-Value $root "CustomGamePath" (Join-Path $staging "sBridge.exe")
    if ((Invoke-Import $root) -ne "Steam Account Selection") { throw "An empty account selection did not block import." }
    if (Test-Path -LiteralPath $second) { throw "Empty selection created a shortcut file." }
    (Element $root "Steam Accounts").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    foreach ($id in @("1", "2")) { (Element $root "Account $id").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    (Element $root "Add Custom Game").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ((Invoke-Import $root) -ne "Success") { throw "Selected-account import failed." }
    if (-not (Test-Path -LiteralPath $second) -or (Test-Path -LiteralPath ($second + ".bak"))) { throw "First-file creation/backup semantics failed." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($first + ".bak")) -ne [Convert]::ToBase64String($empty)) { throw "Existing account backup did not preserve its original." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($third)) -ne [Convert]::ToBase64String($unselected)) { throw "Unselected malformed account changed." }
    $config = [IO.File]::ReadAllText((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json")) | ConvertFrom-Json
    if (($config.selectedSteamAccountIds -join ",") -ne "1,2") { throw "Explicit account selection was not persisted." }
    $firstBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($first))
    $secondBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($second))
    Set-Value $root "CustomGameName" "Account scope game"
    Set-Value $root "CustomGamePath" (Join-Path $staging "sBridge.exe")
    if ((Invoke-Import $root) -ne "Success") { throw "Stable-ID retry failed." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($first)) -ne $firstBytes -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($second)) -ne $secondBytes) { throw "Retry duplicated or rewrote shortcuts." }
    $gui.Refresh()
    if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) { throw "Settings did not close." }
    $gui.Dispose(); $gui = $null
    $gui = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() } while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Settings") { throw "Settings did not reopen." }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($gui.MainWindowHandle)
    (Element $root "Steam Accounts").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    foreach ($id in @("1", "2")) {
        if ((Element $root "Account $id").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw "Account $id was not checked after restart." }
    }
    $gui.Refresh()
    if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) { throw "Reopened settings did not close." }
    Write-Host "PASS: empty selection blocks imports; only selected accounts receive shortcuts; first-file creation, backup, persisted choices, and deduplicated retry work."
} finally {
    if ($null -ne $gui) { if (-not $gui.HasExited) { $gui.Kill(); $gui.WaitForExit() }; $gui.Dispose() }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousData
    $env:SBRIDGE_TEST_STEAM_DIRECTORY = $previousSteam
}
