#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi

script_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
deployment_root="${1:-$script_root}"
install -m 0755 "$script_root/scripts/vsm-host-updater" /usr/local/sbin/vsm-host-updater

cat >/etc/systemd/system/vsm-host-updater.service <<EOF
[Unit]
Description=Valheim Server Manager host updater
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
Environment="VSM_DEPLOYMENT_ROOT=$deployment_root"
ExecStart=/usr/local/sbin/vsm-host-updater
EOF

cat >/etc/systemd/system/vsm-host-updater.timer <<'EOF'
[Unit]
Description=Poll for approved Valheim Server Manager updates

[Timer]
OnBootSec=30s
OnUnitActiveSec=60s
AccuracySec=5s
Persistent=true

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now vsm-host-updater.timer
systemctl start vsm-host-updater.service
echo "Host updater installed for $deployment_root"
