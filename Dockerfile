# ── Stage 1: Build ────────────────────────────────────────────────────────────
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

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
# runtime-deps is the minimal base for self-contained .NET apps (no SDK/runtime needed)
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app

COPY --from=build /app .
COPY scripts/docker-entrypoint.sh /entrypoint.sh
RUN chmod +x /app/k2s-web /entrypoint.sh

# /data holds queue.json, proxies.txt, settings.json and downloaded files
RUN mkdir -p /data
VOLUME /data

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["/entrypoint.sh"]
