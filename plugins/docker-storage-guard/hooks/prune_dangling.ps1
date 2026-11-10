Write-Host "=== [PRUNE DANGLING IMAGES (PS)] ==="
& docker image prune -f
& docker system df
