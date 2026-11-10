#!/bin/bash
set -e
echo "=== [PRUNE DANGLING IMAGES] ==="
docker image prune -f
echo "=== [PRUNE FINISHED] ==="
docker system df
