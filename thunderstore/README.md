# Server Manager client

This package contains the Valheim Server Manager client plugin. The server agent is installed by the [Docker deployment](https://github.com/monokaijs/valheim-server-manager); it is not part of this Thunderstore package.

The client provides server-owned characters, privacy-controlled inventory inspection, and a read-only **Server mods (F8)** compatibility view. When a server provides a mod list, the view displays required and optional package versions and checks the package metadata already present in the active BepInEx profile.

The client does not download, install, update, or remove mods. No installer, preloader, or mod manager is included. Manage the profile through your external mod manager and restart Valheim after changing packages.

The BepInEx plugin ID is `dev.creaton.valheim-server-manager.client`.

## Privacy

Inventory inspection and detailed telemetry are disabled until the player opts in. Servers can require inventory inspection or server-owned characters for admission; the client does not silently change player privacy settings.
