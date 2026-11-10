#!/bin/bash
set -e

echo "=== [SETUP DOCKER DAEMON BUILDKIT GC] ==="

TARGET_CONF="/etc/docker/daemon.json"
BACKUP_CONF="/etc/docker/daemon.json.bak.$(date +%s)"

if [ -f "$TARGET_CONF" ]; then
    cp "$TARGET_CONF" "$BACKUP_CONF"
    echo "Existing daemon.json backed up to $BACKUP_CONF"
fi

TMP_CONF="/tmp/daemon-gc-apply-$$.json"

# 使用 Python 或 jq 安全合并配置
python3 - << 'EOF'
import json, os

conf_path = '/etc/docker/daemon.json'
data = {}
if os.path.exists(conf_path):
    try:
        with open(conf_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception as e:
        print(f"Warning: Failed to parse existing daemon.json ({e}), initializing fresh")

# 注入 builder.gc 策略
data['builder'] = {
    'gc': {
        'enabled': True,
        'defaultKeepStorage': '5GB',
        'policy': [
            {
                'keepStorage': '3GB',
                'filter': ['unused-for=48h']
            },
            {
                'keepStorage': '5GB'
            }
        ]
    }
}

# 注入日志轮转策略
data['log-driver'] = 'json-file'
data['log-opts'] = {
    'max-size': '50m',
    'max-file': '3'
}

with open('/tmp/daemon-gc-applied.json', 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2)
print("Merged daemon.json generated successfully")
EOF

# 校验有效性
if dockerd --validate --config-file /tmp/daemon-gc-applied.json >/dev/null 2>&1; then
    mv /tmp/daemon-gc-applied.json "$TARGET_CONF"
    echo "[OK] Configuration applied to $TARGET_CONF"
    systemctl reload docker 2>/dev/null || systemctl restart docker 2>/dev/null || true
    echo "[OK] Docker daemon reloaded successfully"
    cat "$TARGET_CONF"
else
    echo "[ERROR] dockerd validation failed! Configuration was not applied."
    rm -f /tmp/daemon-gc-applied.json
    exit 1
fi
