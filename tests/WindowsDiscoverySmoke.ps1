param(
    [string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app")
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires an interactive Windows desktop." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory "sBridge.exe"))) { throw "Run build.ps1 first." }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge discovery " + [Guid]::NewGuid().ToString("N"))
$previousData = $env:SBRIDGE_TEST_DATA_DIRECTORY
$gui = $null
function Find-Element($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    [IO.File]::WriteAllLines((Join-Path $staging "sBridge.cfg"), @("SisrEnabled=false", "SgdbApiKey=", "LogEnabled=true"))
    $gui = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() } while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Settings") { throw "Settings did not open." }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($gui.MainWindowHandle)
    $tab = Find-Element $root "Add Xbox / Store Games"
    if ($null -eq $tab) { throw "Packaged-game discovery tab was missing." }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $scan = Find-Element $root "Scan Packaged Games"
    if ($null -eq $scan) { throw "Scan button was missing." }
    $scan.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do { Start-Sleep -Milliseconds 50; $cancel = Find-Element $root "Cancel Scan" } while ($null -eq $cancel -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $cancel -or -not $gui.Responding) { throw "Scan did not expose a responsive cancel action." }
    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do { Start-Sleep -Milliseconds 50; $scan = Find-Element $root "Scan Packaged Games" } while ($null -eq $scan -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $scan) { throw "Cancelled scan did not restore the button." }
    Write-Host "PASS: packaged discovery keeps the UI responsive and supports cancel."
    $epicTab = Find-Element $root "Add Epic Games"
    if ($null -eq $epicTab) { throw "Epic discovery tab was missing." }
    $epicTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $epicScan = Find-Element $root "Scan Epic Games"
    if ($null -eq $epicScan) { throw "Epic scan button was missing." }
    $epicScan.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    $found = $false
    do {
        Start-Sleep -Milliseconds 100
        $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) { if ($element.Current.Name -match '^Found \d+ apps\. Select games to add\.') { $found = $true; break } }
    } while (-not $found -and [DateTime]::UtcNow -lt $deadline)
    if (-not $found -or -not $gui.Responding) { throw "Read-only Epic inventory scan did not complete in the responsive UI." }
    Write-Host "PASS: Epic discovery tab completes a read-only manifest inventory scan."
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $scan = Find-Element $root "Scan Packaged Games"
    $scan.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $gui.Refresh()
    if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) { throw "Closing during scan did not complete bounded cleanup." }
    $children = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $gui.Id -and $_.Name -eq "powershell.exe" })
    if ($children.Count -ne 0) { throw "Owned discovery command remained after closing settings." }
    $config = [IO.File]::ReadAllText((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json")) | ConvertFrom-Json
    if ($config.games.PSObject.Properties.Count -gt 0) { throw "Read-only discovery unexpectedly registered a game." }
    Write-Host "PASS: close-during-scan cleans the owned command and does not register games."
} finally {
    if ($null -ne $gui) {
        if (-not $gui.HasExited) { $gui.Kill(); $gui.WaitForExit() }
        $gui.Dispose()
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousData
}
