param(
    [string]$InputDirectory = "C:\Users\WDAGUtilityAccount\Desktop\sBridge Input",
    [string]$ResultsDirectory = "C:\Users\WDAGUtilityAccount\Desktop\sBridge Results"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $InputDirectory) -or -not (Test-Path -LiteralPath $ResultsDirectory)) {
    throw "Run this verifier inside the prepared Sandbox with its mapped input/results folders."
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$resultPath = Join-Path $ResultsDirectory "result.json"
$result = [ordered]@{ schemaVersion=1; succeeded=$false; testedUtc=[DateTimeOffset]::UtcNow.ToString("O"); os=""; dotnetRuntimes=@(); package=""; sha256=""; error=$null }
Start-Transcript -LiteralPath (Join-Path $ResultsDirectory "transcript.txt") -Force | Out-Null
try {
    $result.os = (Get-CimInstance Win32_OperatingSystem).Caption
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -ne $dotnet) {
        $result.dotnetRuntimes = @(& $dotnet.Source --list-runtimes)
        if ($LASTEXITCODE -ne 0) { throw "Cannot establish installed .NET runtimes." }
    }
    # Also inspect standard runtime roots; absence from PATH alone is insufficient.
    foreach ($root in @((Join-Path $env:ProgramFiles "dotnet\shared"), (Join-Path ${env:ProgramFiles(x86)} "dotnet\shared"))) {
        if (Test-Path -LiteralPath $root) {
            foreach ($framework in @(Get-ChildItem -LiteralPath $root -Directory)) {
                foreach ($version in @(Get-ChildItem -LiteralPath $framework.FullName -Directory)) {
                    $result.dotnetRuntimes += "$($framework.Name) $($version.Name)"
                }
            }
        }
    }
    if (@($result.dotnetRuntimes | Where-Object { $_ -match '\s10\.' }).Count -gt 0) {
        throw "This machine already contains .NET 10; it cannot establish a clean-machine result."
    }
    $package = @(Get-ChildItem -LiteralPath $InputDirectory -Filter "*-self-contained.zip" -File)
    if ($package.Count -ne 1) { throw "Expected one self-contained package in the isolated input folder." }
    $result.package = $package[0].Name
    $result.sha256 = (Get-FileHash -LiteralPath $package[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "No installed .NET 10 found. Testing the bundled-runtime package..." -ForegroundColor Cyan
    & (Join-Path $InputDirectory "WindowsPackageSmoke.ps1") -Package $package[0].FullName -DesktopChecks
    $result.succeeded = $true
    Write-Host "PASS: clean-machine package and desktop checks completed." -ForegroundColor Green
} catch {
    $result.error = $_.Exception.Message
    Write-Host "FAIL: $($result.error)" -ForegroundColor Red
} finally {
    [IO.File]::WriteAllText($resultPath, ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Stop-Transcript | Out-Null
}
Write-Host "Results are in $ResultsDirectory. Keep this window open until the host has collected them."
Read-Host "Press Enter when finished" | Out-Null
