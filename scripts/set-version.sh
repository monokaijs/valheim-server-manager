#!/usr/bin/env bash
set -euo pipefail

release_version="${1:-}"
if [[ ! "$release_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Usage: $0 <major.minor.patch>" >&2
  exit 1
fi
export RELEASE_VERSION="$release_version"

perl -0pi -e 's|<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>|<Version>$ENV{RELEASE_VERSION}</Version>|g; s|<InformationalVersion>[0-9]+\.[0-9]+\.[0-9]+</InformationalVersion>|<InformationalVersion>$ENV{RELEASE_VERSION}</InformationalVersion>|g' \
  Directory.Build.props

perl -0pi -e 's/^versionNumber = "[0-9]+\.[0-9]+\.[0-9]+"$/versionNumber = "$ENV{RELEASE_VERSION}"/m' \
  thunderstore.toml

perl -0pi -e 's/(PluginVersion = ")[0-9]+\.[0-9]+\.[0-9]+(";)/$1$ENV{RELEASE_VERSION}$2/g' \
  plugins/ValheimServerManager.Server/ServerPlugin.cs \
  plugins/ValheimServerManager.Client/ClientPlugin.cs

perl -0pi -e 's/(vsm_version=")[0-9]+\.[0-9]+\.[0-9]+(";?)/$1$ENV{RELEASE_VERSION}$2/g' \
  docker/entrypoint.sh

perl -0pi -e 's|(ValheimServerManager-)[0-9]+\.[0-9]+\.[0-9]+(\.zip)|$1$ENV{RELEASE_VERSION}$2|g' \
  src/ValheimServerManager/Program.cs

perl -0pi -e 's/(Valheim Server Manager )[0-9]+\.[0-9]+\.[0-9]+/$1$ENV{RELEASE_VERSION}/g' \
  web/src/main.tsx

perl -0pi -e 's/(server agent )[0-9]+\.[0-9]+\.[0-9]+/$1$ENV{RELEASE_VERSION}/g' \
  README.md

echo "Set Server Manager version to $release_version"
