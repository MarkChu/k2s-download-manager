# K2S Download Manager

K2S 下載工具，提供 Web UI 介面，支援佇列管理、Proxy 自動輪換、Gemini 自動解 Captcha。

## 快速安裝（Docker）

### 方法一：docker run（最簡單，不需 Compose）

```bash
docker run -d \
  --name k2s-downloader \
  --restart unless-stopped \
  -p 5000:5000 \
  -v ~/k2s-data:/data \
  ghcr.io/markchu/k2s-download-manager:latest
```

開啟瀏覽器：`http://YOUR_IP:5000`

**更新到最新版：**

```bash
docker pull ghcr.io/markchu/k2s-download-manager:latest
docker rm -f k2s-downloader
docker run -d \
  --name k2s-downloader \
  --restart unless-stopped \
  -p 5000:5000 \
  -v ~/k2s-data:/data \
  ghcr.io/markchu/k2s-download-manager:latest
```

---

### 方法二：Docker Compose

```bash
# 1. 下載 compose 設定檔
curl -O https://raw.githubusercontent.com/MarkChu/k2s-download-manager/main/docker-compose.yml

# 2. 啟動（首次會自動拉取 image）
docker compose up -d

# 3. 開啟瀏覽器
# http://localhost:5000
```

**更新到最新版：**

```bash
docker compose pull && docker compose up -d
```

**停止服務：**

```bash
docker compose down
```

---

## 設定

首次啟動後，設定檔會自動建立在 `./data/settings.json`：

```json
{
  "GeminiApiKey": "",         // Gemini API Key（自動解 Captcha 用，可留空）
  "DownloadDirectory": "/data/downloads",
  "Threads": 20,
  "SplitSizeMb": 20,
  "ProxyRefreshIntervalMin": 5
}
```

修改後重啟：

```bash
docker compose restart
```

---

## 資料目錄

所有資料存放在 `./data/`：

| 路徑                   | 說明             |
| ---------------------- | ---------------- |
| `./data/settings.json` | 設定檔           |
| `./data/downloads/`    | 下載的檔案       |
| `./data/proxies.txt`   | Proxy 清單快取   |
| `./data/queue.json`    | 下載佇列         |

---

## Web UI 功能

- 新增 / 批次新增 K2S URL
- 即時進度條（速度、ETA、分段數）
- 下載佇列管理（重試、取消、刪除）
- Proxy 管理（手動貼上或自動刷新）
- Captcha 自動解（Gemini API）/ 手動輸入
- 即時 Log

---

## 從原始碼 Build

```bash
git clone https://github.com/MarkChu/k2s-download-manager.git
cd k2s-download-manager

# 使用本地 build（取代預建 image）
docker compose up -d --build
```
