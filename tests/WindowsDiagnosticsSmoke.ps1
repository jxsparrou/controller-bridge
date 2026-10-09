param([string]$PublishedDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\app"))
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires an interactive Windows desktop." }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge diagnostics " + [Guid]::NewGuid().ToString("N"))
$previousData = $env:SBRIDGE_TEST_DATA_DIRECTORY
$previousSteam = $env:SBRIDGE_TEST_STEAM_DIRECTORY
$gui = $null
function Element($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Report-Element($root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "DiagnosticReport")
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item -Path (Join-Path $PublishedDirectory "*") -Destination $staging -Recurse
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    $env:SBRIDGE_TEST_STEAM_DIRECTORY = Join-Path $staging "Synthetic Steam"
    New-Item -ItemType Directory -Path (Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\12345") -Force | Out-Null
    [IO.File]::WriteAllLines((Join-Path $staging "sBridge.cfg"), @("SisrEnabled=false", "SisrPath=C:\Private User\Secret\SISR.exe", "SgdbApiKey=diagnostic-secret-key", "LogEnabled=true"))
    $gui = Start-Process -FilePath (Join-Path $staging "sBridge.exe") -WorkingDirectory $staging -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $gui.Refresh() } while (-not $gui.HasExited -and $gui.MainWindowTitle -ne "sBridge Settings" -and [DateTime]::UtcNow -lt $deadline)
    if ($gui.HasExited -or $gui.MainWindowTitle -ne "sBridge Settings") { throw "Settings did not open." }
    $config = Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json"
    $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($config))
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($gui.MainWindowHandle)
    (Element $root "Diagnostics").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Element $root "Refresh Diagnostics").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $report = (Report-Element $root).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    } while (-not $report.StartsWith("sBridge diagnostics") -and [DateTime]::UtcNow -lt $deadline)
    if (-not $report.StartsWith("sBridge diagnostics") -or -not $gui.Responding) { throw "Read-only diagnostics did not complete in the responsive UI." }
    foreach ($private in @("diagnostic-secret-key", "Private User", "12345", $staging)) { if ($report.Contains($private)) { throw "Copied diagnostic summary included private data." } }
    if (-not (Element $root "Copy Diagnostics").Current.IsEnabled) { throw "Copy action was not enabled after collection." }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($config)) -ne $before) { throw "Read-only refresh changed settings." }
    if (Test-Path -LiteralPath (Join-Path $env:SBRIDGE_TEST_STEAM_DIRECTORY "userdata\12345\config")) { throw "Diagnostics created Steam resources." }
    if (Test-Path -LiteralPath (Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "sisr")) { throw "Diagnostics started or fabricated SISR state." }
    $gui.Refresh()
    if (-not $gui.CloseMainWindow() -or -not $gui.WaitForExit(10000)) { throw "Diagnostics settings window did not close." }
    Write-Host "PASS: responsive read-only diagnostics, safe copy payload, enabled copy action, and unchanged settings/Steam/SISR resources."
} finally {
    if ($null -ne $gui) { if (-not $gui.HasExited) { $gui.Kill(); $gui.WaitForExit() }; $gui.Dispose() }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousData
    $env:SBRIDGE_TEST_STEAM_DIRECTORY = $previousSteam
}
