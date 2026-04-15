$Source = "C:\Users\osiny\.claude\projects"
$BackupRoot = Join-Path $env:USERPROFILE "OneDrive\Documents\claude memory backup"
$Timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$BackupFile = Join-Path $BackupRoot "claude_memory_$Timestamp.zip"

Write-Host ""
Write-Host "============================================"
Write-Host "  Claude Memory Backup"
Write-Host "============================================"
Write-Host ""
Write-Host "  Source:  $Source"
Write-Host "  Backup:  $BackupFile"
Write-Host ""

if (-not (Test-Path $BackupRoot)) {
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
}

try {
    Compress-Archive -Path $Source -DestinationPath $BackupFile -Force
    $Size = [math]::Round((Get-Item $BackupFile).Length / 1024)
    Write-Host "  Done! Size: ${Size} KB" -ForegroundColor Green
    Write-Host ""

    # Keep only latest 10 backups
    $Files = Get-ChildItem -Path $BackupRoot -Filter "claude_memory_*.zip" | Sort-Object Name -Descending
    if ($Files.Count -gt 10) {
        $Files | Select-Object -Skip 10 | ForEach-Object {
            Remove-Item $_.FullName -Force
            Write-Host "  Removed old: $($_.Name)"
        }
    }
}
catch {
    Write-Host "  Backup failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "============================================"
Read-Host "Press Enter to close"
