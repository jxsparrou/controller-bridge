$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) {
    Write-Error "C# compiler csc.exe not found at $compiler"
    exit 1
}

Write-Host "Compiling all files into sBridge.exe..."
& $compiler /target:winexe /out:sBridge.exe /win32icon:sBridge.ico /r:System.Windows.Forms.dll /r:System.dll /r:System.Drawing.dll /r:System.Core.dll Program.cs VdfParser.cs SteamShortcuts.cs SettingsForm.cs AppManager.cs

if ($LASTEXITCODE -eq 0) {
    Write-Host "Successfully compiled sBridge.exe!" -ForegroundColor Green
} else {
    Write-Error "Compilation failed!"
    exit 1
}
