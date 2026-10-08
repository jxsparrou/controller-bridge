param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Push-Location -LiteralPath $PSScriptRoot
try {
    # Publish the complete framework-dependent app, not just its apphost EXE.
    & dotnet.exe publish .\sBridge.csproj -c $Configuration -r win-x64 --self-contained false -o .\artifacts\app
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Write-Host "Built sBridge in artifacts\app. Keep all files in that folder together." -ForegroundColor Green
} finally {
    Pop-Location
}
