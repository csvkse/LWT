#!/bin/bash
set -e

echo "=== [DOCKER STORAGE SAFE OPTIMIZE] ==="

echo "1/3. Pruning dangling untagged images (<none>:<none>)..."
docker image prune -f || true

echo "2/3. Pruning BuildKit cache while preserving 3GB hot layers..."
docker builder prune -f --keep-storage=3GB || true

echo "3/3. Verifying /etc/docker/daemon.json BuildKit GC configuration..."
if [ -f /etc/docker/daemon.json ]; then
    if ! grep -q "builder" /etc/docker/daemon.json 2>/dev/null; then
        echo "Configuring missing builder.gc in daemon.json..."
        /bin/bash "$(dirname "$0")/setup_daemon_gc.sh" || true
    else
        echo "daemon.json already has builder.gc configured."
    fi
fi

echo "=== [OPTIMIZATION FINISHED] ==="
echo "Current Root Partition:"
df -h /
echo "Current Docker System Usage:"
docker system df || true
