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

vsm_version="2.1.1"
mkdir -p /data/server/BepInEx/plugins/ValheimServerManager /data/manager/downloads /data/manager/runtime
dotnet build /app/plugins/ValheimServerManager.Server/ValheimServerManager.Server.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /data/server/BepInEx/plugins/ValheimServerManager >/data/logs/server-plugin-build.log

dotnet build /app/plugins/ValheimServerManager.Client/ValheimServerManager.Client.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /tmp/vsm-client >/data/logs/client-plugin-build.log

dotnet build /app/plugins/ValheimServerManager.Bootstrap/ValheimServerManager.Bootstrap.csproj -c Release -f net472 \
  -o /tmp/vsm-bootstrap >/data/logs/bootstrap-build.log

dotnet build /app/plugins/ValheimServerManager.RuntimeUpdater/ValheimServerManager.RuntimeUpdater.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /tmp/vsm-runtime >/data/logs/runtime-updater-build.log

package="$(mktemp -d)"
mkdir -p "$package/BepInEx/patchers" "$package/BepInEx/plugins/ValheimServerManager" "$package/BepInEx/plugins/ValheimServerManager.Server"
cp /tmp/vsm-bootstrap/ValheimServerManagerBootstrap.dll "$package/BepInEx/patchers/"
cp /tmp/vsm-runtime/ValheimServerManagerRuntimeUpdater.dll "$package/BepInEx/plugins/ValheimServerManager/"
cp /data/server/BepInEx/plugins/ValheimServerManager/ValheimServerManager.Server.dll \
  /data/server/BepInEx/plugins/ValheimServerManager/Newtonsoft.Json.dll \
  "$package/BepInEx/plugins/ValheimServerManager.Server/"
printf '%s\n' "{\"name\":\"Server_Manager\",\"version_number\":\"$vsm_version\",\"website_url\":\"https://github.com/monokaijs/valheim-server-manager\",\"description\":\"One Server Manager package for dedicated servers and players, with automatic server-specific client synchronization.\",\"dependencies\":[\"denikson-BepInExPack_Valheim-5.4.2350\"]}" >"$package/manifest.json"
printf '%s\n' '# Valheim Server Manager' '' 'Install this single package. On a dedicated server it runs the management agent; on a player client it receives and safely stages the exact client runtime and required mods selected by that server.' >"$package/README.md"
python3 /app/plugins/make_icon.py "$package/icon.png"
rm -f /data/manager/downloads/ValheimServerManagerClient-*.zip /data/manager/downloads/ValheimServerManagerServer-*.zip /data/manager/downloads/XomNghien-ServerModBootstrap-*.zip
(cd "$package" && zip -qr "/data/manager/downloads/ValheimServerManager-$vsm_version.zip" .)
rm -rf "$package"

client_runtime="$(mktemp -d)"
mkdir -p "$client_runtime/BepInEx/plugins/ValheimServerManager"
cp /tmp/vsm-client/ValheimServerManager.Client.dll /tmp/vsm-client/Newtonsoft.Json.dll \
  /tmp/vsm-runtime/ValheimServerManagerRuntimeUpdater.dll \
  "$client_runtime/BepInEx/plugins/ValheimServerManager/"
printf '%s\n' "{\"name\":\"Server_Manager\",\"version_number\":\"$vsm_version\",\"website_url\":\"https://github.com/monokaijs/valheim-server-manager\",\"description\":\"Valheim Server Manager client runtime.\",\"dependencies\":[\"denikson-BepInExPack_Valheim-5.4.2350\"]}" >"$client_runtime/manifest.json"
printf '%s\n' '# Valheim Server Manager runtime' '' 'This client-targeted runtime is managed automatically by the installed Server Manager package.' >"$client_runtime/README.md"
python3 /app/plugins/make_icon.py "$client_runtime/icon.png"
rm -f /data/manager/runtime/ValheimServerManager-*-client.zip
(cd "$client_runtime" && zip -qr "/data/manager/runtime/ValheimServerManager-$vsm_version-client.zip" .)
rm -rf "$client_runtime" /tmp/vsm-client /tmp/vsm-bootstrap /tmp/vsm-runtime

chmod +x /data/server/valheim_server.x86_64 /data/server/start_server_bepinex.sh 2>/dev/null || true
exec dotnet /app/ValheimServerManager.dll
