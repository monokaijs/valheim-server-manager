# Server Manager client

This package contains the Valheim Server Manager client plugin. The server agent is installed by the [Docker deployment](https://github.com/monokaijs/valheim-server-manager); it is not part of this Thunderstore package.

The client provides server-owned characters, privacy-controlled inventory inspection, and a read-only mod allowlist check. It checks package metadata already present in the active BepInEx profile. Missing required packages, unlisted packages, or installed optional packages at unlisted versions disconnect the client with a persistent notice.

The client does not download, install, update, or remove mods. No installer, preloader, or mod manager is included. Manage the profile through your external mod manager and restart Valheim after changing packages.

The BepInEx plugin ID is `dev.creaton.valheim-server-manager.client`.

## Privacy

Inventory inspection and detailed telemetry are disabled until the player opts in. Servers can require inventory inspection or server-owned characters for admission; the client does not silently change player privacy settings.
