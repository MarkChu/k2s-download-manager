#!/bin/sh
set -e

DATA_DIR="/data"

# First run: copy default settings.json into the data volume
if [ ! -f "$DATA_DIR/settings.json" ]; then
    cp /app/settings.json.default "$DATA_DIR/settings.json"
    echo "[entrypoint] Created default $DATA_DIR/settings.json"
    echo "[entrypoint] !! Edit $DATA_DIR/settings.json to set GeminiApiKey and DownloadDirectory !!"
fi

# Create downloads folder if DownloadDirectory is set to /data/downloads
mkdir -p "$DATA_DIR/downloads"

# Run the web app with /data as working directory so queue.json / proxies.txt land there
cd "$DATA_DIR"
exec /app/k2s-web
