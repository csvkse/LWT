Write-Host "=== [PRUNE BUILDKIT CACHE (KEEP 3GB) (PS)] ==="
& docker builder prune -f --keep-storage=3GB
& docker system df
