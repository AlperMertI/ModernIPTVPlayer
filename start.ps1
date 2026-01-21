Write-Host "Building project..." -ForegroundColor Cyan
dotnet build ModernIPTVPlayer.csproj -r win-x64

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful. Launching..." -ForegroundColor Green
    & ".\bin\x64\Debug\net8.0-windows10.0.22000.0\win-x64\ModernIPTVPlayer.exe"
} else {
    Write-Host "Build failed." -ForegroundColor Red
}
