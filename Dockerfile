FROM node:22-bookworm-slim AS web-build
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS app-build
WORKDIR /src
COPY Directory.Build.props ValheimServerManager.slnx ./
COPY src/ ./src/
COPY --from=web-build /src/src/ValheimServerManager/wwwroot ./src/ValheimServerManager/wwwroot/
RUN dotnet publish src/ValheimServerManager/ValheimServerManager.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble
ENV DEBIAN_FRONTEND=noninteractive \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    VSM_DATA_PATH=/data/manager \
    VSM_SAVE_PATH=/data/worlds \
    VSM_BEPINEX_PATH=/data/server/BepInEx \
    VSM_LOG_PATH=/data/logs \
    VSM_SERVER_EXECUTABLE=/data/server/valheim_server.x86_64 \
    VSM_SERVER_WORKDIR=/data/server \
    VSM_AGENT_URL=ws://127.0.0.1:8080/internal/agent \
    BEPINEX_PACK_VERSION=5.4.2350
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates curl zip unzip tar libatomic1 libpulse0 libpulse-dev libc6-i386 lib32gcc-s1 lib32stdc++6 python3 procps \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=app-build /out ./
COPY plugins/ ./plugins/
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 8080/tcp 2456-2457/udp
VOLUME ["/data/server", "/data/worlds", "/data/manager", "/data/logs"]
ENTRYPOINT ["/entrypoint.sh"]
