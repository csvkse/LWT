#!/bin/bash
set -e
echo "=== [PRUNE BUILDKIT CACHE (KEEP 3GB)] ==="
docker builder prune -f --keep-storage=3GB
echo "=== [PRUNE FINISHED] ==="
docker system df
