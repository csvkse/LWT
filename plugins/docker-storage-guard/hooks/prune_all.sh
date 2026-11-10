#!/bin/bash
set -e
echo "=== [PRUNE ALL UNUSED DOCKER OBJECTS] ==="
docker system prune -f
echo "=== [PRUNE FINISHED] ==="
docker system df
