# Native Tool & Plugin Sub-features Packaging Script
param(
    [string]$OutputDir = "artifacts/native-tool-services"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$targetDir = Join-Path $repoRoot $OutputDir

if (!(Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

Write-Host "== Packaging Native Tool & Builtin Plugins to $targetDir ==" -ForegroundColor Cyan

# Package builtin plugins from plugins/
$plugins = @("sample-healthcheck", "ssh-access-guard", "caddy-server-manager", "native-command-demo", "native-resident", "native-managed")
foreach ($p in $plugins) {
    $srcPath = Join-Path $repoRoot "plugins/$p"
    if (Test-Path $srcPath) {
        $zipDest = Join-Path $targetDir "plugin-$p.zip"
        if (Test-Path $zipDest) { Remove-Item $zipDest -Force }
        Compress-Archive -Path "$srcPath/*" -DestinationPath $zipDest -Force
        Write-Host "  [OK] Packaged plugin: $p -> $zipDest" -ForegroundColor Green
    }
}

Write-Host "`nPackaging completed successfully." -ForegroundColor Green
