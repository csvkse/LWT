# Native Tool Examples Packaging Script
param(
    [string]$OutputDir = "artifacts/native-tool-services"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$targetDir = Join-Path $repoRoot $OutputDir

if (!(Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

Write-Host "== Packaging Native Tool Examples to $targetDir ==" -ForegroundColor Cyan

# 1. Package script extensions
$extensions = @("sample-healthcheck", "ssh-access-guard", "caddy-server-manager", "native-command-demo")
foreach ($ext in $extensions) {
    $srcPath = Join-Path $repoRoot "extensions/$ext"
    if (Test-Path $srcPath) {
        $zipDest = Join-Path $targetDir "extension-$ext.zip"
        if (Test-Path $zipDest) { Remove-Item $zipDest -Force }
        Compress-Archive -Path "$srcPath/*" -DestinationPath $zipDest -Force
        Write-Host "  [OK] Packaged extension: $ext -> $zipDest" -ForegroundColor Green
    }
}

# 2. Package worker examples
$workers = @("native-resident", "native-managed")
foreach ($w in $workers) {
    $srcPath = Join-Path $repoRoot "workers/examples/$w"
    if (Test-Path $srcPath) {
        $zipDest = Join-Path $targetDir "worker-$w.zip"
        if (Test-Path $zipDest) { Remove-Item $zipDest -Force }
        Compress-Archive -Path "$srcPath/*" -DestinationPath $zipDest -Force
        Write-Host "  [OK] Packaged worker: $w -> $zipDest" -ForegroundColor Green
    }
}

Write-Host "`nPackaging completed successfully." -ForegroundColor Green
