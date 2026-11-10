# PowerShell optimize hook
$ErrorActionPreference = 'SilentlyContinue'

Write-Host "=== [DOCKER STORAGE SAFE OPTIMIZE (PS)] ==="
Write-Host "1/2. Pruning dangling untagged images..."
& docker image prune -f 2>$null

Write-Host "2/2. Pruning BuildKit cache while keeping 3GB hot layers..."
& docker builder prune -f --keep-storage=3GB 2>$null

Write-Host "=== [OPTIMIZATION FINISHED] ==="
& docker system df 2>$null
