#!/usr/bin/env bash
set -euo pipefail

mkdir -p /data/server /data/worlds /data/manager /data/logs /opt/steamcmd

if [[ -z "${VSM_AGENT_TOKEN:-}" ]]; then
  if [[ -f /data/manager/agent-token ]]; then
    VSM_AGENT_TOKEN="$(tr -d '\r\n' </data/manager/agent-token)"
  else
    VSM_AGENT_TOKEN="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
    printf '%s' "$VSM_AGENT_TOKEN" >/data/manager/agent-token
    chmod 600 /data/manager/agent-token
  fi
  export VSM_AGENT_TOKEN
fi

if [[ ! -x /opt/steamcmd/steamcmd.sh ]]; then
  curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar -xz -C /opt/steamcmd
fi

if [[ "${VSM_UPDATE_ON_START:-true}" == "true" || ! -x /data/server/valheim_server.x86_64 ]]; then
  /opt/steamcmd/steamcmd.sh +force_install_dir /data/server +login anonymous +app_update 896660 validate +quit
fi

if [[ ! -f /data/server/BepInEx/core/BepInEx.dll ]]; then
  work="$(mktemp -d)"
  curl -fsSL "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/${BEPINEX_PACK_VERSION:-5.4.2350}/" -o "$work/bepinex.zip"
  unzip -q "$work/bepinex.zip" -d "$work/unpacked"
  cp -a "$work/unpacked/BepInExPack_Valheim/." /data/server/
  rm -rf "$work"
fi

mkdir -p /data/server/BepInEx/plugins/ValheimServerManager /data/manager/downloads
dotnet build /app/plugins/ValheimServerManager.Server/ValheimServerManager.Server.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /data/server/BepInEx/plugins/ValheimServerManager >/data/logs/server-plugin-build.log

dotnet build /app/plugins/ValheimServerManager.Client/ValheimServerManager.Client.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /tmp/vsm-client >/data/logs/client-plugin-build.log

server_package="$(mktemp -d)"
mkdir -p "$server_package/BepInEx/plugins/ValheimServerManager.Server"
cp /data/server/BepInEx/plugins/ValheimServerManager/ValheimServerManager.Server.dll \
  /data/server/BepInEx/plugins/ValheimServerManager/Newtonsoft.Json.dll \
  "$server_package/BepInEx/plugins/ValheimServerManager.Server/"
printf '%s\n' '{"name":"Server_Manager","version_number":"1.6.5","website_url":"https://github.com/monokaijs/valheim-server-manager","description":"Mandatory VSM administration, customizable moderation notices, admission requests, client-mod relay, and server-owned character agent.","dependencies":["denikson-BepInExPack_Valheim-5.4.2350"]}' >"$server_package/manifest.json"
printf '%s\n' '# Server Manager' '' 'Mandatory server-side administration, event, and authoritative native character bridge. Configure its token through the manager container.' >"$server_package/README.md"
python3 /app/plugins/make_icon.py "$server_package/icon.png"
(cd "$server_package" && zip -qr /data/manager/downloads/ValheimServerManagerServer-1.6.5.zip .)
rm -rf "$server_package"

package="$(mktemp -d)"
mkdir -p "$package/BepInEx/plugins/ValheimServerManager.Client"
cp /tmp/vsm-client/ValheimServerManager.Client.dll /tmp/vsm-client/Newtonsoft.Json.dll "$package/BepInEx/plugins/ValheimServerManager.Client/"
printf '%s\n' '{"name":"ValheimServerManagerClient","version_number":"1.4.9","website_url":"","description":"Automatically managed client half of VSM server-owned characters, customizable server notices, consent-based inventory, and telemetry.","dependencies":["denikson-BepInExPack_Valheim-5.4.2350"]}' >"$package/manifest.json"
printf '%s\n' '# Valheim Server Manager Client' '' 'Required for VSM server-owned characters. Live dashboard inspection and detailed telemetry retain their separate opt-in privacy settings.' >"$package/README.md"
python3 /app/plugins/make_icon.py "$package/icon.png"
(cd "$package" && zip -qr /data/manager/downloads/ValheimServerManagerClient-1.4.9.zip .)
rm -rf "$package" /tmp/vsm-client

if [[ -f /app/plugins/artifacts/XomNghien-ServerModBootstrap-2.2.0.zip ]]; then
  cp /app/plugins/artifacts/XomNghien-ServerModBootstrap-2.2.0.zip /data/manager/downloads/
fi

chmod +x /data/server/valheim_server.x86_64 /data/server/start_server_bepinex.sh 2>/dev/null || true
exec dotnet /app/ValheimServerManager.dll
