# ── Stage 1: Build .NET app ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the files needed to build K2sDownloaderWeb
# (Core files are linked from WinForms, so we need both trees)
COPY K2sDownloaderWinForms/Core/       K2sDownloaderWinForms/Core/
COPY K2sDownloaderWinForms/settings.json.default K2sDownloaderWinForms/
COPY K2sDownloaderWeb/                 K2sDownloaderWeb/

WORKDIR /src/K2sDownloaderWeb
RUN dotnet publish -c Release -r linux-x64 --self-contained -o /app

# Copy default settings as a template
RUN cp /src/K2sDownloaderWinForms/settings.json.default /app/settings.json.default

# ── Stage 2: Download Playwright Chromium browser ─────────────────────────────
# Uses the same Ubuntu 22.04 base as the runtime stage to ensure binary compatibility.
FROM ubuntu:22.04 AS browser
RUN apt-get update && apt-get install -y --no-install-recommends \
    nodejs npm ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# PLAYWRIGHT_BROWSERS_PATH controls where the browser is stored.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN npx --yes playwright@1.51.0 install chromium

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
# runtime-deps:8.0-jammy = Ubuntu 22.04 — matches the browser download stage.
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy

# ffmpeg for media integrity checks; Chromium system dependencies for Playwright
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    libnss3 libnspr4 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
    libdbus-1-3 libxkbcommon0 libx11-6 libx11-xcb1 libxext6 libxfixes3 \
    libxrandr2 libgbm1 libxdamage1 libpango-1.0-0 libcairo2 libasound2 \
    libxcb1 libxrender1 libxcomposite1 libxtst6 libxshmfence1 \
    libglib2.0-0 fonts-liberation ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app .
COPY --from=browser /ms-playwright /ms-playwright
COPY scripts/docker-entrypoint.sh /entrypoint.sh
RUN chmod +x /app/k2s-web /entrypoint.sh

# Tell Playwright where to find the pre-installed browser
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# /data holds queue.json, proxies.txt, settings.json and downloaded files
RUN mkdir -p /data
VOLUME /data

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["/entrypoint.sh"]
