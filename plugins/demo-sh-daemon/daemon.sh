#!/bin/bash
count=0
while true; do
  count=$((count + 1))
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] [Shell-Daemon] Tick #$count - Plugin: ${LWT_PLUGIN_ID:-demo-sh-daemon}"
  sleep 30
done
