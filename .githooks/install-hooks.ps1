# LinuxWebTool (LWT) Install Git Hooks
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    git config core.hooksPath .githooks
    Write-Host "[Git Hooks] Successfully configured core.hooksPath = .githooks" -ForegroundColor Green
    Write-Host "            Pre-commit security gate is now active!" -ForegroundColor Cyan
} finally {
    Pop-Location
}
