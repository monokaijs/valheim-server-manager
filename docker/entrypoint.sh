#!/usr/bin/env bash
set -euo pipefail

mkdir -p /data/server /data/worlds /data/manager /data/logs /opt/steamcmd
bepinex_pack_version="${BEPINEX_PACK_VERSION:-5.4.2350}"

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

# Valheim's current dedicated-server build mutates a shared HttpClient timeout
# after its first public-IP request. Resolve the address once per container so
# the server plugin can bypass that broken retry path.
if [[ -z "${VSM_PUBLIC_IP:-}" ]]; then
  for public_ip_url in \
    https://api.ipify.org \
    https://ipv4.icanhazip.com \
    https://checkip.amazonaws.com; do
    if public_ip_candidate="$(curl -4fsS --max-time 5 "$public_ip_url" 2>/dev/null)"; then
      public_ip_candidate="${public_ip_candidate//$'\r'/}"
      public_ip_candidate="${public_ip_candidate//$'\n'/}"
      if [[ -n "$public_ip_candidate" ]]; then
        export VSM_PUBLIC_IP="$public_ip_candidate"
        break
      fi
    fi
  done
fi

if [[ ! -x /opt/steamcmd/steamcmd.sh ]]; then
  curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar -xz -C /opt/steamcmd
fi

if [[ "${VSM_UPDATE_ON_START:-true}" == "true" || ! -x /data/server/valheim_server.x86_64 ]]; then
  /opt/steamcmd/steamcmd.sh +force_install_dir /data/server +login anonymous +app_update 896660 +quit
fi

if [[ ! -f /data/server/BepInEx/core/BepInEx.dll ]]; then
  work="$(mktemp -d)"
  curl -fsSL "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/$bepinex_pack_version/" -o "$work/bepinex.zip"
  unzip -q "$work/bepinex.zip" -d "$work/unpacked"
  cp -a "$work/unpacked/BepInExPack_Valheim/." /data/server/
  rm -rf "$work"
fi

# Releases before 2.1 installed server and client components together under
# plugins/ServerManager. Quarantine that bundle so its old runtime updater
# cannot patch dedicated-server connections alongside the current agent.
legacy_plugin_dir="/data/server/BepInEx/plugins/ServerManager"
if [[ -f "$legacy_plugin_dir/ValheimServerManager.Server.dll" ||
      -f "$legacy_plugin_dir/ValheimServerManagerRuntimeUpdater.dll" ]]; then
  legacy_backup_root="/data/manager/legacy-plugin-backups"
  mkdir -p "$legacy_backup_root"
  legacy_backup="$legacy_backup_root/ServerManager-$(date -u +%Y%m%dT%H%M%SZ)-$$"
  mv "$legacy_plugin_dir" "$legacy_backup"
  printf 'Quarantined legacy Server Manager bundle at %s\n' "$legacy_backup"
fi

vsm_version="2.2.2"
mkdir -p /data/server/BepInEx/plugins/ValheimServerManager /data/manager/downloads
dotnet build /app/plugins/ValheimServerManager.Server/ValheimServerManager.Server.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /data/server/BepInEx/plugins/ValheimServerManager >/data/logs/server-plugin-build.log

dotnet build /app/plugins/ValheimServerManager.Client/ValheimServerManager.Client.csproj -c Release \
  -p:ValheimManaged=/data/server/valheim_server_Data/Managed -p:BepInExRoot=/data/server/BepInEx \
  -o /tmp/vsm-client >/data/logs/client-plugin-build.log

package="$(mktemp -d)"
mkdir -p "$package/BepInEx/plugins/ValheimServerManager"
cp /tmp/vsm-client/ValheimServerManager.Client.dll "$package/BepInEx/plugins/ValheimServerManager/"
cp /tmp/vsm-client/Newtonsoft.Json.dll "$package/BepInEx/plugins/ValheimServerManager/"
printf '%s\n' "{\"name\":\"Server_Manager\",\"version_number\":\"$vsm_version\",\"website_url\":\"https://github.com/monokaijs/valheim-server-manager\",\"description\":\"Read-only client mod compatibility and server-owned characters.\",\"dependencies\":[\"denikson-BepInExPack_Valheim-$bepinex_pack_version\"]}" >"$package/manifest.json"
printf '%s\n' '# Valheim Server Manager client' '' 'The Docker deployment installs the server agent. Install this client package through your external mod manager. The client checks this profile against the server mod allowlist and disconnects with a notice if required packages are missing or unlisted packages are present; it never downloads or installs mods.' >"$package/README.md"
python3 /app/plugins/make_icon.py "$package/icon.png"
rm -f /data/manager/downloads/ValheimServerManagerClient-*.zip /data/manager/downloads/ValheimServerManagerServer-*.zip /data/manager/downloads/XomNghien-ServerModBootstrap-*.zip
(cd "$package" && zip -qr "/data/manager/downloads/ValheimServerManager-$vsm_version.zip" .)
rm -rf "$package"

rm -rf /tmp/vsm-client

chmod +x /data/server/valheim_server.x86_64 /data/server/start_server_bepinex.sh 2>/dev/null || true
exec dotnet /app/ValheimServerManager.dll
