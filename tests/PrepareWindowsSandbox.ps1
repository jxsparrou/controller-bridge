param(
    [string]$Package = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\packages\sBridge-0.0.0-dev-win-x64-self-contained.zip")
)
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Prepare this bundle on Windows." }
$Package = (Resolve-Path -LiteralPath $Package).ProviderPath
if (-not (Test-Path -LiteralPath ($Package + ".sha256"))) { throw "The package checksum sidecar is required." }
$root = Join-Path ([IO.Path]::GetTempPath()) ("sBridge-sandbox-proof-" + [Guid]::NewGuid().ToString("N"))
$input = Join-Path $root "Input"
$results = Join-Path $root "Results"
New-Item -ItemType Directory -Path $input, $results | Out-Null
try {
    Copy-Item -LiteralPath $Package, ($Package + ".sha256") -Destination $input
    foreach ($script in @("WindowsPackageSmoke.ps1", "WindowsSmoke.ps1", "WindowsSandboxVerify.ps1")) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $input
    }
    $inputXml = [Security.SecurityElement]::Escape($input)
    $resultsXml = [Security.SecurityElement]::Escape($results)
    $sandbox = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$inputXml</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\sBridge Input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$resultsXml</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\sBridge Results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File &quot;C:\Users\WDAGUtilityAccount\Desktop\sBridge Input\WindowsSandboxVerify.ps1&quot;</Command>
  </LogonCommand>
</Configuration>
"@
    $config = Join-Path $root "sBridge-clean-machine.wsb"
    [IO.File]::WriteAllText($config, $sandbox, [Text.UTF8Encoding]::new($false))
    $record = [ordered]@{ schemaVersion=1; sandboxConfiguration=$config; inputDirectory=$input; resultsDirectory=$results;
        package=[IO.Path]::GetFileName($Package); sha256=(Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText((Join-Path $root "bundle.json"), ($record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "Prepared Sandbox configuration: $config" -ForegroundColor Green
    Write-Host "Host results folder: $results"
    Write-Host "Enable Windows Sandbox/reboot if needed, then double-click the .wsb file."
    Write-Host "This preparation does not enable features or launch Sandbox."
    $record
} catch {
    Remove-Item -LiteralPath $root -Recurse -Force
    throw
}
