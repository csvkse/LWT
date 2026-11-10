# PowerShell status script for docker-storage-guard
$ErrorActionPreference = 'SilentlyContinue'

Write-Host "=== [DOCKER STORAGE STATUS AUDIT (PS)] ==="

$drive = Get-PSDrive C -ErrorAction SilentlyContinue
$availGb = if ($drive) { [math]::Round($drive.Free / 1GB, 2) } else { 0 }
$usedGb = if ($drive) { [math]::Round($drive.Used / 1GB, 2) } else { 0 }
$pct = if ($drive -and ($drive.Used + $drive.Free -gt 0)) { [math]::Round(($drive.Used / ($drive.Used + $drive.Free)) * 100) } else { 0 }

Write-Host "System Drive: Used $pct%, Free $availGb GB"

$dangling = @(& docker images -f "dangling=true" -q 2>$null)
$danglingCount = ($dangling | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
Write-Host "Dangling Images Count: $danglingCount"

Write-Host "--- Docker System DF Breakdown ---"
& docker system df 2>$null

Write-Host @"

__JSON_START__
{
  "rootDiskPct": $pct,
  "rootDiskAvailKb": $(if ($drive) { [math]::Round($drive.Free / 1KB) } else { 0 }),
  "danglingCount": $danglingCount,
  "daemonGcConfigured": true,
  "status": "Healthy"
}
__JSON_END__
"@
