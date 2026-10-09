param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"),
    [string]$ProbeDirectory = (Join-Path $PSScriptRoot "sBridge.Tests\bin\Release\net10.0")
)
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires an interactive Windows desktop." }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SBridgeLibrarySmokeWindow {
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public static bool Click(IntPtr window) { return PostMessage(window, 0xF5, IntPtr.Zero, IntPtr.Zero); }
    public static void SelectCombo(IntPtr window, int index) { SendMessage(window, 0x14E, new IntPtr(index), IntPtr.Zero); }
    public static IntPtr Find(int pid, string title) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((window, parameter) => { uint id; GetWindowThreadProcessId(window, out id); if (id==pid) {
            var text=new StringBuilder(256); GetWindowText(window,text,text.Capacity); if(text.ToString()==title){found=window;return false;} } return true;
        },IntPtr.Zero); return found;
    }
    public static bool Close(IntPtr window) { return PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero); }
}
'@
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge library " + [Guid]::NewGuid().ToString("N"))
$previousData = $env:SBRIDGE_TEST_DATA_DIRECTORY
$previousSteam = $env:SBRIDGE_TEST_STEAM_DIRECTORY
$gui = $null; $launch = $null; $held = $null
function Element($root, [string]$value, [switch]$Id) {
    $property = if ($Id) { [System.Windows.Automation.AutomationElement]::AutomationIdProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $condition = New-Object System.Windows.Automation.PropertyCondition($property, $value)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Set-Value($root, [string]$id, [string]$value) { (Element $root $id -Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Read-Settings { return [IO.File]::ReadAllText($configPath) | ConvertFrom-Json }
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $probe = Join-Path $staging "Probe"; New-Item -ItemType Directory -Path $probe | Out-Null
    Copy-Item -Path (Join-Path $ProbeDirectory "*") -Destination $probe -Recurse
    $game = Join-Path $probe "Edited Game.exe"; Copy-Item -LiteralPath (Join-Path $probe "sBridge.Tests.exe") -Destination $game
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"; New-Item -ItemType Directory -Path $env:SBRIDGE_TEST_DATA_DIRECTORY | Out-Null
    $env:SBRIDGE_TEST_STEAM_DIRECTORY = Join-Path $staging "Synthetic Steam"
    $vdf = Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\1\config\shortcuts.vdf"
    New-Item -ItemType Directory -Path (Split-Path $vdf) -Force | Out-Null
    [byte[]]$untouched = @(0,1,2); [IO.File]::WriteAllBytes($vdf, $untouched)
    $id = [Guid]::NewGuid().ToString("D"); $other = [Guid]::NewGuid().ToString("D")
    $games = @{}
    $games[$id] = @{ name="Library Original"; provider="win32"; providerId="C:\Old\Game.exe"; launchKind="executable"; target="C:\Old\Game.exe";
        arguments=@(); processHint="C:\Old\Game.exe"; installDirectory="C:\Old"; futureGame="retained" }
    $games[$other] = @{ name="Other Game"; provider="win32"; providerId="C:\Other.exe"; launchKind="executable"; target="C:\Other.exe";
        arguments=@(); processHint=""; installDirectory=$null }
    $settings = @{ schemaVersion=1; sisrPath="C:\Missing\SISR.exe"; sisrArguments=""; sisrEnabled=$true; managedSisrStartup=$false; logEnabled=$true;
        protectedSteamGridDbApiKey=$null; gameProfiles=@{}; games=$games; selectedSteamAccountIds=@() }
    $settings.gameProfiles[$id] = @{ steamInput="automatic"; watchProcess=""; controller=@{ controllerType="dualShock4"; gyroPassthrough=$true;
        touchpadPassthrough=$false; backButtonPassthrough=$false; futureController="retained controller" } }
    $configPath = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json"
    [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $gui = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() } while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Settings") { throw "Settings did not open." }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($gui.MainWindowHandle)
    (Element $root "Registered Games").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $row = Element $root ("Library Original [win32; " + $id.Replace("-", "").Substring(0,8) + "]")
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $capture = Join-Path $staging "Edited Arguments.json"
    $unicode = [string][char]0x904A + [char]0x6232
    Set-Value $root "LibraryName" "Library Renamed"
    Set-Value $root "LibraryTarget" $game
    Set-Value $root "LibraryHint" $game
    Set-Value $root "LibraryInstall" $probe
    Set-Value $root "LibraryArguments" ('--write-arguments "' + $capture + '" "" "Player One" "' + $unicode + '"')
    [SBridgeLibrarySmokeWindow]::SelectCombo([IntPtr](Element $root "LibrarySisr" -Id).Current.NativeWindowHandle, 2)
    [SBridgeLibrarySmokeWindow]::Click([IntPtr](Element $root "SaveLibraryGame" -Id).Current.NativeWindowHandle) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 100; $saved = Read-Settings } while ($saved.games.$id.name -ne "Library Renamed" -and [DateTime]::UtcNow -lt $deadline)
    if ($saved.games.$id.name -ne "Library Renamed" -or $saved.games.$id.target -ne $game -or $saved.gameProfiles.$id.steamInput -ne "disabled") { throw "Registry/profile edits were not committed together." }
    if (@($saved.games.PSObject.Properties).Count -ne 2 -or $saved.games.$id.futureGame -ne "retained" -or $saved.games.$other.name -ne "Other Game") { throw "UUID/other game/extension preservation failed." }
    if ($saved.gameProfiles.$id.controller.controllerType -ne "dualShock4") { throw "Library editing lost the controller override." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($vdf)) -ne [Convert]::ToBase64String($untouched)) { throw "Local library editing changed Steam data." }
    (Element $root "Controller Profiles").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Element $root ("Library Original [win32; " + $id.Replace("-", "").Substring(0,8) + "]")).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    [SBridgeLibrarySmokeWindow]::SelectCombo([IntPtr](Element $root "ControllerType" -Id).Current.NativeWindowHandle, 3)
    (Element $root "ControllerGyro" -Id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Element $root "ControllerBack" -Id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    [SBridgeLibrarySmokeWindow]::Click([IntPtr](Element $root "SaveControllerProfile" -Id).Current.NativeWindowHandle) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 100; $saved = Read-Settings } while ($saved.gameProfiles.$id.controller.controllerType -ne "dualSenseEdge" -and [DateTime]::UtcNow -lt $deadline)
    if ($saved.gameProfiles.$id.controller.controllerType -ne "dualSenseEdge" -or $saved.gameProfiles.$id.controller.gyroPassthrough -ne $false -or
        $saved.gameProfiles.$id.controller.backButtonPassthrough -ne $true -or $saved.gameProfiles.$id.controller.futureController -ne "retained controller") { throw "Controller draft/extension settings were not saved." }
    (Element $root "Registered Games").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Element $root "Reload Games").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 100
    (Element $root ("Library Renamed [win32; " + $id.Replace("-", "").Substring(0,8) + "]")).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    # Force a real atomic-save sharing violation and verify the draft is rolled back.
    [byte[]]$before = [IO.File]::ReadAllBytes($configPath)
    $held = [IO.FileStream]::new($configPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    Set-Value $root "LibraryName" "Unsaved rename"
    [SBridgeLibrarySmokeWindow]::Click([IntPtr](Element $root "SaveLibraryGame" -Id).Current.NativeWindowHandle) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 100; $errorWindow = [SBridgeLibrarySmokeWindow]::Find($gui.Id,"Error Saving Settings") } while ($errorWindow -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($errorWindow -eq [IntPtr]::Zero) { throw "Locked library save did not show an error." }
    [SBridgeLibrarySmokeWindow]::Close($errorWindow) | Out-Null
    Start-Sleep -Milliseconds 200; $held.Dispose(); $held=$null
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -ne [Convert]::ToBase64String($before)) { throw "Failed edit overwrote config." }
    (Element $root "Reload Games").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 100
    (Element $root ("Library Renamed [win32; " + $id.Replace("-", "").Substring(0,8) + "]")).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ((Element $root "LibraryName" -Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne "Library Renamed") { throw "Failed edit leaked into the in-memory registry." }
    $gui.Refresh(); if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) { throw "Library window did not close." }
    $launch = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -ArgumentList ("launch " + $id) -WorkingDirectory $staging -PassThru
    if (-not $launch.WaitForExit(30000)) { throw "Edited UUID launch did not finish." }
    $received = [IO.File]::ReadAllText($capture) | ConvertFrom-Json
    if ($received.Count -ne 3 -or $received[0] -ne "" -or $received[1] -ne "Player One" -or $received[2] -ne $unicode) { throw "Edited argument tokens were not preserved." }
    Write-Host "PASS: library UUID/profile/extension edits, untouched Steam data, failed-save rollback, and edited ID argument launch."
} finally {
    if ($null -ne $held) { $held.Dispose() }
    foreach ($process in @($gui,$launch)) { if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() } }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY=$previousData; $env:SBRIDGE_TEST_STEAM_DIRECTORY=$previousSteam
}
