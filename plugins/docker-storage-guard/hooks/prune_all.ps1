Write-Host "=== [PRUNE ALL UNUSED DOCKER OBJECTS (PS)] ==="
& docker system prune -f
& docker system df
