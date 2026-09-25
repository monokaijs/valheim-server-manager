#!/usr/bin/env bash
set -euo pipefail

mkdir -p /data/server /data/worlds /data/manager /data/logs /opt/steamcmd
bepinex_pack_version="${BEPINEX_PACK_VERSION:-$(cat /app/bepinex-pack.version)}"
if [[ -z "${BEPINEX_PACK_VERSION:-}" ]]; then
  # Pick up pack-only Thunderstore releases without waiting for a new image.
  if pack_json="$(curl -fsSL --max-time 10 'https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/' 2>/dev/null)"; then
    latest_pack="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["latest"]["version_number"])' <<<"$pack_json" 2>/dev/null || true)"
    if [[ "$latest_pack" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && dpkg --compare-versions "$latest_pack" ge "$bepinex_pack_version"; then
      bepinex_pack_version="$latest_pack"
    fi
  fi
fi
if [[ ! "$bepinex_pack_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Invalid BepInExPack version: $bepinex_pack_version" >&2
  exit 1
fi

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

installed_pack_version=""
if [[ -f /data/server/BepInEx/core/BepInEx.dll && -f /data/server/BepInEx/.vsm-pack-version ]]; then
  installed_pack_version="$(cat /data/server/BepInEx/.vsm-pack-version)"
fi
if [[ "$installed_pack_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && dpkg --compare-versions "$installed_pack_version" ge "$bepinex_pack_version"; then
  bepinex_pack_version="$installed_pack_version"
else
  bepinex_work="$(mktemp -d)"
  trap 'rm -rf "$bepinex_work"' EXIT
  if curl -fsSL --retry 3 "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/$bepinex_pack_version/" -o "$bepinex_work/bepinex.zip"; then
    python3 /install-bepinex-pack.py "$bepinex_work/bepinex.zip" /data/server "$bepinex_pack_version"
  elif [[ -z "${BEPINEX_PACK_VERSION:-}" && "$installed_pack_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'BepInExPack download failed; continuing with installed version %s\n' "$installed_pack_version" >&2
    bepinex_pack_version="$installed_pack_version"
  else
    echo "BepInExPack could not be downloaded and no known installed version is available." >&2
    exit 1
  fi
  rm -rf "$bepinex_work"
  trap - EXIT
fi
export BEPINEX_PACK_VERSION="$bepinex_pack_version"
printf 'Using BepInExPack Valheim %s\n' "$bepinex_pack_version"

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

vsm_version="2.3.0"
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
