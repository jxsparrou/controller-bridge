$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) {
    Write-Error "C# compiler csc.exe not found at $compiler"
    exit 1
}

Write-Host "Compiling Program.cs into uwphook-bridge.exe..."
& $compiler /target:winexe /out:uwphook-bridge.exe /r:System.Windows.Forms.dll /r:System.dll Program.cs

if ($LASTEXITCODE -eq 0) {
    Write-Host "Successfully compiled uwphook-bridge.exe!" -ForegroundColor Green
} else {
    Write-Error "Compilation failed!"
    exit 1
}
