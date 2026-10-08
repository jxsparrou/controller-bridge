param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.0.0-dev",
    [ValidatePattern('^(?:[0-9a-fA-F]{40})?$')]
    [string]$Revision = "",
    [ValidateSet("both", "framework-dependent", "self-contained")]
    [string]$Mode = "both"
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Distribution packages must be built with the Windows .NET 10 SDK." }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$output = Join-Path $PSScriptRoot "artifacts\packages"
$staging = Join-Path $PSScriptRoot ("artifacts\staging\" + [Guid]::NewGuid().ToString("N"))
Push-Location -LiteralPath $PSScriptRoot
try {
    New-Item -ItemType Directory -Path $output, $staging -Force | Out-Null
    $sdk = (& dotnet.exe --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw "Requires a stable .NET 10 SDK selected by global.json." }
    $modes = if ($Mode -eq "both") { @("framework-dependent", "self-contained") } else { @($Mode) }
    foreach ($kind in $modes) {
        $app = Join-Path $staging $kind
        $selfContained = if ($kind -eq "self-contained") { "true" } else { "false" }
        $information = if ($Revision.Length -eq 0) { $Version } else { "$Version+$Revision" }
        & dotnet.exe publish .\sBridge.csproj -c Release -r win-x64 --self-contained $selfContained -o $app `
            "-p:Version=$Version" "-p:InformationalVersion=$information" -p:IncludeSourceRevisionInInformationalVersion=false `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $kind ($LASTEXITCODE)." }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "LICENSE") -Destination (Join-Path $app "LICENSE-sBridge.txt")
        $runtime = [IO.File]::ReadAllText((Join-Path $app "sBridge.runtimeconfig.json")) | ConvertFrom-Json
        $frameworks = if ($kind -eq "self-contained") { @($runtime.runtimeOptions.includedFrameworks) } else { @($runtime.runtimeOptions.frameworks) }
        if ($kind -eq "self-contained") {
            # Preserve the notices from the exact runtime packs selected by this SDK.
            $assets = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "obj\project.assets.json")) | ConvertFrom-Json
            foreach ($framework in $frameworks) {
                $packageId = ($framework.name + ".Runtime.win-x64").ToLowerInvariant()
                $pack = $null
                foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                    $candidate = Join-Path $folder "$packageId\$($framework.version)"
                    if (Test-Path -LiteralPath $candidate) { $pack = $candidate; break }
                }
                if ($null -eq $pack) { throw "Cannot locate bundled runtime pack notices for $($framework.name)." }
                $license = @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -ieq "LICENSE" -or $_.Name -ieq "LICENSE.TXT" })
                if ($license.Count -eq 0) { throw "Runtime pack license was missing for $($framework.name)." }
                $notices = Join-Path $app ("licenses\" + $framework.name)
                New-Item -ItemType Directory -Path $notices -Force | Out-Null
                foreach ($file in @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(?i:LICENSE(?:\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })) {
                    Copy-Item -LiteralPath $file.FullName -Destination $notices
                }
            }
        }
        $runtimeNote = if ($kind -eq "self-contained") { "Includes the .NET 10 Desktop runtime. No separate .NET install is required." } else { "Requires the .NET 10 Desktop Runtime (x64)." }
        $notes = @(
            "sBridge $Version - Windows x64 - $kind", $runtimeNote,
            "Extract every file into one permanent folder. Do not copy only sBridge.exe.",
            "No arguments opens settings. Settings/logs are per-user under LocalAppData\sBridge.",
            "SISR remains external and optional. Close Steam before shortcut edits.",
            "Select Steam Accounts explicitly before importing. Existing shortcuts/AppIDs are retained.",
            "Unsigned development packages can show a Windows SmartScreen prompt.",
            "Source: https://github.com/jxsparrou/controller-bridge", "License: GPL-3.0 (LICENSE-sBridge.txt)",
            "Credits: SISR by Alia5; UWPHook design by BrianLima; Luke1505 PR #1 concepts.",
            "package-manifest.json records runtime mode, version, SDK, revision and SHA-256 of payload files."
        )
        [IO.File]::WriteAllLines((Join-Path $app "PACKAGE-README.txt"), $notes, [Text.UTF8Encoding]::new($false))
        $files = @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($app.Length + 1).Replace('\', '/')
            if ($relative -match '(?i)(?:^|/)(?:config\.json|sBridge\.cfg|sBridge\.log|sBridge\.Tests.*)$' -or $_.Extension -eq '.pdb') {
                throw "Unexpected state/test/debug file in package: $relative"
            }
            [ordered]@{ path=$relative; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        $manifest = [ordered]@{ schemaVersion=1; application="sBridge"; packageVersion=$Version; sourceRevision=$Revision; sdkVersion=$sdk;
            runtimeIdentifier="win-x64"; selfContained=($kind -eq "self-contained"); frameworks=$frameworks; files=$files }
        [IO.File]::WriteAllText((Join-Path $app "package-manifest.json"), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        $name = "sBridge-$Version-win-x64-$kind.zip"
        $zip = Join-Path $output $name
        $temporary = Join-Path $staging ($name + ".tmp")
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName)) {
                $relative = $file.FullName.Substring($app.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                # Stable ZIP metadata, independent of publish/copy timestamps.
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($file.FullName); $target = $entry.Open()
                try { $input.CopyTo($target) } finally { $input.Dispose(); $target.Dispose() }
            }
        } finally { $archive.Dispose(); $stream.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $zip -Force
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText(($zip + ".sha256"), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
        Write-Host "Packaged $zip" -ForegroundColor Green
    }
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    Pop-Location
}
