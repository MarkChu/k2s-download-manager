#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# K2S Downloader Web — direct install script (no Docker)
#
# Usage:
#   1. On your dev machine (Windows), publish first:
#        dotnet publish K2sDownloaderWeb -r linux-x64 -c Release --self-contained -o publish/web
#
#   2. Copy the publish/web folder and this script to your Linux server:
#        scp -r publish/web user@server:~/k2s-install
#        scp scripts/install.sh user@server:~/k2s-install/
#
#   3. On the Linux server:
#        cd ~/k2s-install
#        sudo bash install.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/k2s-downloader}"
DATA_DIR="${DATA_DIR:-/var/lib/k2s-downloader}"
SERVICE_USER="${SERVICE_USER:-k2s}"
PORT="${PORT:-5000}"
SERVICE_NAME="k2s-web"

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'

log()  { echo -e "${GREEN}[install]${NC} $*"; }
warn() { echo -e "${YELLOW}[warn]${NC} $*"; }

# ── Checks ────────────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    echo "Run as root: sudo bash install.sh"
    exit 1
fi

if [[ ! -f "./k2s-web" ]]; then
    echo "k2s-web binary not found in current directory."
    echo "Publish first: dotnet publish K2sDownloaderWeb -r linux-x64 -c Release --self-contained -o publish/web"
    exit 1
fi

# ── Service user ──────────────────────────────────────────────────────────────
if ! id "$SERVICE_USER" &>/dev/null; then
    useradd -r -s /bin/false -d "$DATA_DIR" "$SERVICE_USER"
    log "Created service user: $SERVICE_USER"
fi

# ── Install binary ────────────────────────────────────────────────────────────
log "Installing to $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR"
cp -r . "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/k2s-web"

# ── Data directory ────────────────────────────────────────────────────────────
mkdir -p "$DATA_DIR/downloads"

if [[ ! -f "$DATA_DIR/settings.json" ]]; then
    if [[ -f "$INSTALL_DIR/settings.json" ]]; then
        cp "$INSTALL_DIR/settings.json" "$DATA_DIR/settings.json"
        log "Created $DATA_DIR/settings.json from template"
    fi
fi

chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"

# ── Systemd service ───────────────────────────────────────────────────────────
cat > "/etc/systemd/system/$SERVICE_NAME.service" << EOF
[Unit]
Description=K2S Downloader Web
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
WorkingDirectory=$DATA_DIR
ExecStart=$INSTALL_DIR/k2s-web
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_URLS=http://+:$PORT

# Harden the service
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

# ── Done ──────────────────────────────────────────────────────────────────────
IP=$(hostname -I | awk '{print $1}')
echo ""
log "Installation complete!"
echo ""
echo "  Service : systemctl status $SERVICE_NAME"
echo "  Web UI  : http://$IP:$PORT"
echo "  Config  : $DATA_DIR/settings.json   (set GeminiApiKey and DownloadDirectory)"
echo "  Logs    : journalctl -u $SERVICE_NAME -f"
echo "  Data    : $DATA_DIR"
echo ""
warn "Edit $DATA_DIR/settings.json and set DownloadDirectory to a path the '$SERVICE_USER' user can write to."
warn "Then restart: systemctl restart $SERVICE_NAME"
