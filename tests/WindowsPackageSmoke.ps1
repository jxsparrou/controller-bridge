param([Parameter(Mandatory=$true)][string]$Package, [switch]$DesktopChecks)
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Requires Windows." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Package = (Resolve-Path -LiteralPath $Package).ProviderPath
$staging = Join-Path ([IO.Path]::GetTempPath()) ("sBridge package " + [Guid]::NewGuid().ToString("N"))
$previousData = $env:SBRIDGE_TEST_DATA_DIRECTORY
$previousRoot = $env:DOTNET_ROOT
$previousRootX64 = $env:DOTNET_ROOT_X64
$previousLookup = $env:DOTNET_MULTILEVEL_LOOKUP
$launch = $null
try {
    if (Test-Path -LiteralPath ($Package + ".sha256")) {
        $expectedHash = [IO.File]::ReadAllText($Package + ".sha256").Split(' ')[0]
        if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -ine $expectedHash) { throw "ZIP checksum mismatch." }
    }
    New-Item -ItemType Directory -Path $staging | Out-Null
    $app = Join-Path $staging "App"
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        foreach ($entry in $zip.Entries) {
            if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Replace('\', '/').Split('/') -contains '..') { throw "Unsafe ZIP entry path." }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $app)
    $manifest = [IO.File]::ReadAllText((Join-Path $app "package-manifest.json")) | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.application -ne "sBridge" -or $manifest.runtimeIdentifier -ne "win-x64") { throw "Unexpected package manifest." }
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ([IO.Path]::IsPathRooted($file.path) -or $file.path.Replace('\', '/').Split('/') -contains '..') { throw "Unsafe manifest path." }
        if (-not $listed.Add($file.path)) { throw "Duplicate package manifest path." }
        $path = Join-Path $app $file.path
        if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { throw "Payload hash/length mismatch: $($file.path)" }
    }
    if (@(Get-ChildItem -LiteralPath $app -File -Recurse).Count -ne $manifest.files.Count + 1) { throw "Unlisted package files." }
    foreach ($required in @("sBridge.exe", "sBridge.dll", "sBridge.runtimeconfig.json", "sBridge.deps.json", "sBridge.ico", "LICENSE-sBridge.txt", "PACKAGE-README.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "Missing package file $required" }
    }
    $runtime = [IO.File]::ReadAllText((Join-Path $app "sBridge.runtimeconfig.json")) | ConvertFrom-Json
    if ($manifest.selfContained) {
        foreach ($required in @("licenses\Microsoft.NETCore.App\LICENSE.TXT", "licenses\Microsoft.NETCore.App\THIRD-PARTY-NOTICES.TXT", "licenses\Microsoft.WindowsDesktop.App\LICENSE")) {
            if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "Missing bundled runtime notice $required" }
        }
        foreach ($required in @("hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll", "System.Windows.Forms.dll", "System.Drawing.Common.dll")) {
            if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "Missing bundled runtime file $required" }
        }
        if (@($runtime.runtimeOptions.includedFrameworks).Count -lt 2) { throw "Self-contained runtime metadata was missing." }
        # Exercise app-local hosting with invalid explicit runtime roots. Registered
        # global fallback still exists on SDK machines; this is not clean-VM proof.
        $env:DOTNET_ROOT = Join-Path $staging "No Runtime"
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $env:DOTNET_MULTILEVEL_LOOKUP = "0"
    } elseif (@($runtime.runtimeOptions.frameworks).Count -lt 2) { throw "Framework-dependent runtime metadata was missing." }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = Join-Path $staging "Data"
    New-Item -ItemType Directory -Path $env:SBRIDGE_TEST_DATA_DIRECTORY | Out-Null
    $settings = @{ schemaVersion=1; sisrPath=""; sisrArguments=""; sisrEnabled=$false; managedSisrStartup=$false; logEnabled=$true;
        protectedSteamGridDbApiKey=$null; gameProfiles=@{}; games=@{}; selectedSteamAccountIds=@() }
    [IO.File]::WriteAllText((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "config.json"), ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $game = Join-Path $staging "Package Game.exe"
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot "System32\cmd.exe") -Destination $game
    $launch = Start-Process -FilePath (Join-Path $app "sBridge.exe") -ArgumentList ('"' + $game + '" /c exit 0') -WorkingDirectory $staging -PassThru
    if (-not $launch.WaitForExit(30000)) { throw "Packaged SISR-disabled launch did not complete." }
    if ($launch.ExitCode -ne 0) { throw "Packaged launch exited with $($launch.ExitCode)." }
    $log = [IO.File]::ReadAllText((Join-Path $env:SBRIDGE_TEST_DATA_DIRECTORY "logs\sBridge.log"))
    if (-not $log.Contains("CustomGame=True, SISR=False") -or -not $log.Contains("Bridge exiting successfully.")) { throw "Packaged launch success evidence was missing." }
    foreach ($unexpected in @("config.json", "sBridge.cfg", "sBridge.log")) { if (Test-Path -LiteralPath (Join-Path $app $unexpected)) { throw "Package wrote beside the executable." } }
    Write-Host "PASS: $([IO.Path]::GetFileName($Package)) hashes/runtime payload and isolated SISR-disabled launch."
    if ($DesktopChecks) {
        & (Join-Path $PSScriptRoot "WindowsSmoke.ps1") -PublishedDirectory $app
    }
} finally {
    if ($null -ne $launch) { if (-not $launch.HasExited) { $launch.Kill(); $launch.WaitForExit() }; $launch.Dispose() }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $env:SBRIDGE_TEST_DATA_DIRECTORY = $previousData
    $env:DOTNET_ROOT = $previousRoot
    $env:DOTNET_ROOT_X64 = $previousRootX64
    $env:DOTNET_MULTILEVEL_LOOKUP = $previousLookup
}
