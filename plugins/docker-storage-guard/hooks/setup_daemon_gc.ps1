# PowerShell setup daemon hook
Write-Host "=== [SETUP DOCKER DAEMON GC (PS)] ==="
Write-Host "Docker daemon GC is typically managed via Docker Desktop UI or daemon.json on Windows."
& docker builder prune -f --keep-storage=3GB 2>$null
