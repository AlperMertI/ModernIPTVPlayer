$folders = @("Models\Iptv", "Models\Common", "Models\Tmdb", "Models\Stremio")
foreach ($folder in $folders) {
    $nsSuffix = $folder.Replace("\", ".")
    $files = Get-ChildItem -Path $folder -Filter *.cs
    foreach ($f in $files) {
        $content = Get-Content $f.FullName
        $newContent = $content -replace 'namespace ModernIPTVPlayer$', "namespace ModernIPTVPlayer.$nsSuffix"
        $newContent = $newContent -replace 'namespace ModernIPTVPlayer\r\n{', "namespace ModernIPTVPlayer.$nsSuffix`r`n{"
        # Also fix standard namespace declaration if it's already partial but in root
        $newContent = $newContent -replace 'namespace ModernIPTVPlayer;', "namespace ModernIPTVPlayer.$nsSuffix;"
        
        Set-Content -Path $f.FullName -Value $newContent -Encoding utf8
        Write-Host "Updated $($f.FullName) to namespace ModernIPTVPlayer.$nsSuffix"
    }
}
