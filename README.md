# Valheim Server Manager

A self-hosted Valheim control plane with a web dashboard, live server agent, server-owned characters, package management, player moderation, outbound webhooks, and a separate, privacy-aware client plugin for read-only compatibility checks.

## Included

- One-container Linux deployment that installs the dedicated server with SteamCMD and supervises it without access to the Docker socket.
- React/shadcn dashboard with a live monitor for player count, process CPU and RAM, recent events, character inspection, access lists, Thunderstore/manual mods, webhooks, safe console commands, and auditing. Monitor charts retain one hour of five-second samples in manager memory.
- BepInEx server agent built at startup against the exact installed Valheim assemblies.
- Inventory inspection is a mandatory server admission rule. Server-owned characters remain independently optional; every player needs the Server Manager client runtime and inventory sharing enabled.
- SQLite persistence, Steam OpenID authentication with admin and moderator roles, secure cookies, CSRF protection, login throttling, SignalR updates, signed webhook delivery, and automatic mod rollback.

The server plugin identifier is `dev.creaton.valheim-server-manager`; the bundled client runtime uses `dev.creaton.valheim-server-manager.client`. Older `dev.monokai.*` configuration files are copied forward automatically on first load and retained as rollback copies.

## Start

Requirements: Docker Engine with Compose, an x86-64 Linux host, and roughly 5 GB of free space.

```bash
cp .env.example .env
# Optional: edit the server name, world, ports, and password in .env.
docker compose pull
docker compose up -d
```

That starts a private, passwordless server with inventory-sharing admission enabled. Players need the Server Manager runtime with inventory sharing enabled to stay connected. Open port `8080` for the dashboard and UDP `2456-2458` for Valheim. Set `VSM_PUBLIC_URL` before using Steam dashboard sign-in. Initially, a Steam64 ID in `adminlist.txt` (either `Steam_<id>` or the numeric ID) gets manager admin access. Put the dashboard behind HTTPS before exposing it to the internet.

## Dashboard roles

Admins can use every dashboard feature. Under **Settings → Roles**, an admin can assign another 17-digit Steam ID the **Admin** or **Moderator** role. Assigned roles are stored in the manager database and take effect on the next request, including for an existing session. An explicit moderator assignment overrides the default dashboard admin access granted by `adminlist.txt`; it does not change Valheim's in-game admin privileges.

Moderators can monitor the server, manage player admission and bans, inspect and edit inventories, use safe console commands, and edit mod configuration files. Server start, stop, and restart; applying pending mod or config changes; manager settings; role changes; webhook and API token management; and mod package installation, updating, enabling, disabling, and removal require an admin. The console `restart` command also requires an admin.

The first start takes several minutes because it downloads Valheim, installs BepInEx, and compiles both plugins. Game, world, manager, log, and BepInEx data live in named Docker volumes. Each container start checks Thunderstore for a newer BepInExPack and upgrades its managed core and launcher files on the persistent server volume before building the plugins; existing mod plugins and BepInEx config are preserved. Set `BEPINEX_PACK_VERSION` only if you need to pin a specific pack release.

## Password and whitelist access

Settings → Server manages the server name and world, password and public listing, Steam/crossplay backend, PlayFab instance ID, native save and backup cadence, player capacity, and every official world preset/modifier. The password is encrypted with the manager's persisted data-protection keys and is never returned by the API. Saving performs a world save and controlled Valheim restart. The current game build rejects an empty password for a public-listed server, so passwordless mode always starts with `-public 0` and players join by IP or crossplay join code.

World modifiers are opt-in for existing installations: until **Manage gameplay and exploration modifiers** is enabled, VSM omits `-preset`, `-modifier`, and `-setkey` so it does not overwrite settings already stored in the world. Once enabled, the selected preset is applied first, followed by combat, death-penalty, resource, raid, portal, no-build-cost, player-event, passive-mob, and no-map overrides on every start.

Valheim officially supports 10 concurrent players. Settings from 1–10 use the same server-side admission control; values from 11–100 additionally patch the BepInEx server agent's Steam/PlayFab capacity and are clearly marked as an unsupported, modded configuration. Clients do not need a player-limit mod. The game port remains deployment-owned because changing it also requires changing Docker's published UDP ports; edit `SERVER_PORT`, `SERVER_QUERY_PORT`, and `SERVER_EXTRA_PORT` together in `.env` and recreate the container.

Adding any entry to `permittedlist.txt` enables Valheim's vanilla whitelist; players not on that list are rejected. Each rejected, non-banned platform identity automatically creates its own pending request under **Players → Requests**. Repeated attempts update that request's player name, last-attempt time, and attempt count. Approval adds the exact identity to Valheim's live permitted list; denial closes only that request. The player reconnects after approval. Removing the final permitted entry disables the whitelist.

For Discord registration bots, create a scoped token under Settings → API tokens. The secret is displayed once and only its SHA-256 hash is retained. Requests use `Authorization: Bearer <token>`:

```http
GET /api/external/v1/whitelist

POST /api/external/v1/whitelist
Content-Type: application/json

{"platformId":"76561198000000000"}

DELETE /api/external/v1/whitelist/Steam_76561198000000000

GET /api/external/v1/join-requests?status=pending
POST /api/external/v1/join-requests/{requestId}/approve
POST /api/external/v1/join-requests/{requestId}/deny
```

The registration endpoint accepts a 17-digit Steam64 ID and normalizes it to Valheim's canonical `Steam_<id>` form. Tokens can be independently scoped to `whitelist.read`, `whitelist.write`, `join-requests.read`, and `join-requests.write`; they are rate-limited, revocable, and appear in the audit log as `api-token:<name>`.

## Server Manager client package

Docker installs the server agent. After the first successful container start, authenticated owners can give players the separate client package:

```text
/api/v1/downloads/plugin
```

The Server Manager client plugin checks installed package metadata in the active BepInEx profile against the server's allowlist. Required versions must be present; listed optional packages may be omitted, but any installed optional package must match the listed version. Other plugin packages block admission. A mismatch disconnects the client with a persistent notice naming missing or unapproved packages. The server holds world data until it receives a valid acknowledgment and disconnects clients without one after its grace period. The client cannot download, install, update, or remove mods. Players manage their profile through an external mod manager and restart Valheim after changing packages.

### Built-in proximity voice

The Server Manager client and server plugins include proximity voice chat. Players need the current Server Manager client package, a microphone, and the usual game connection; no separate voice mod or port is required. The default mode is push-to-talk: hold **Left Alt** to transmit. In this mode the microphone opens only while the key is held, and the client shows a transmitting indicator. The server relays audio only to opted-in players within the configured range. Voice is not stored by the manager.

Server owners can enable voice and set its range (5–100 world units) under **Settings → Server → Proximity voice chat**; changes apply to connected players immediately. Voice is enabled with a range of 40 by default. Players can press **F8 while in the game world** to open voice settings, choose a microphone (or the system default), click the talk key to rebind it, switch modes, and adjust playback volume, microphone gain, and voice activation threshold. Changes save to `BepInEx/config/dev.creaton.valheim-server-manager.client.cfg`; `VoiceChat.InputDevice` is empty when using the system default. Voice activation and open mic keep the microphone open during play; disabling voice locally stops both capture and playback. Voice uses 16 kHz mono μ-law frames over Valheim's existing authenticated peer connection; crowded or congested game connections may affect audio latency. This feature has not yet been verified in a live multiplayer game session.

### Discord activity

The server owner can create a Discord application in the [Discord Developer Portal](https://discord.com/developers/applications), copy its **Application ID**, and enter it under **Settings → Discord** in the dashboard. The application name appears as the game title on Discord. The dashboard also lets the owner set an optional HTTPS image URL and format the details and state lines with `{server}`, `{world}`, `{players}`, `{maxPlayers}`, `{freeSlots}`, and `{region}`. Changes are sent to connected players without restarting the server. `VSM_DISCORD_APPLICATION_ID` in `.env` is an optional initial default. No bot token, OAuth secret, or Discord account link is needed.

When the server owner configures an Application ID, the client automatically shows the server's activity while connected; there is no client opt-in setting. The client receives server and player counts from the agent, reads the current biome locally for `{region}`, and sends the rendered activity only to the local Discord desktop app. The activity clears on disconnect, when the server disables it, or on game exit. Discord desktop must be running, and Discord's own activity-sharing setting must be enabled. The web dashboard and dedicated server cannot publish to a player's Discord profile by themselves.

The client runtime is bundled with the Server Manager package and updated by the external mod manager. The server sends package identities and versions over the game RPC; it sends no download URLs or package archives. Manual ZIP uploads and protected infrastructure remain server-only. Older profiles that received bootstrap-installed packages should be recreated in an external mod manager because the read-only client does not remove those files.

Under **Settings → Server → Client compatibility**, inventory inspection is mandatory and server-owned characters are independently optional. Every player needs the Server Manager client runtime. Inventory inspection is enforced after the client handshake grace period. Server-owned characters and mandatory gameplay mods can add their own connection requirements. Other character settings remain configurable before first start:

```env
VSM_SERVER_CHARACTERS_ENABLED=false
VSM_SERVER_CHARACTERS_ACCEPT_FIRST_JOIN=true
VSM_SERVER_CHARACTERS_REJECT_USED=false
VSM_SERVER_CHARACTERS_BACKUPS=10
VSM_CLIENT_MOD_GRACE_SECONDS=20
```

New client installs enable inventory sharing by default. Players with an existing `AllowInventoryInspection = false` setting must enable it to join; the server rejects clients that decline after the grace period. Detailed telemetry remains independently optional:

```ini
[Privacy]
AllowInventoryInspection = true
AllowDetailedTelemetry = false

[ServerCharacters]
Enabled = true
```

The live inspection desk requests current stats, biome, skills, equipment, inventory placement, item metadata, durability, and game-rendered item icons while an administrator is watching. It shows sample freshness, slot changes, searchable skills, and item details; pausing, hiding the tab, or closing the view stops live sampling. For player-owned characters, the manager also saves a last known inventory snapshot about every 15 seconds while they are connected, without icons, so administrators can inspect and queue edits after they leave. These are client-reported observations, not cheat-proof inventory transactions. Inventory snapshots are visible only to authenticated administrators and are not sent to webhooks.

In the inventory tab, an authenticated administrator can search the connected client's item catalog (including modded items) and give an item with a chosen quality and quantity. The client checks the prefab and quality, places as many items as fit in the player's inventory, and reports the delivered count. Selecting an occupied slot also allows changing its stack, quality, and durability or removing it. The actions are audited; the catalog is fetched only for the current player.

When a player is offline and has a server-owned character, **Players → Details** opens the saved `.fch` inventory. Administrators can give items, edit a stack's quantity, quality, durability, or equipped state, and remove stacks. Changes are written to the native save for the next join, with a backup and revision check. Unsupported save versions fail closed. The offline catalog includes built-in prefabs; modded items can be entered by exact prefab name. Player-owned characters live on the player's device, so offline edits to those characters are queued and applied when the player next joins. The dashboard shows the last known inventory with its capture time and the status of each queued edit.

## Server-owned characters and migration

VSM implements its own server-character protocol in the protected server agent and client runtime; it does not depend on ServerCharacters or ServerSync. The server sends its authoritative native `.fch` before player spawn. The client runtime installs that profile into the active Valheim session and returns native checkpoints every 30 seconds, on Valheim profile saves, on server save requests, and during normal logout handling. Clients without a compatible Server Manager runtime are rejected when server characters are enabled.

While a server-owned character is online, the agent keeps five periodic full-profile backups. It takes the first at the next successful character checkpoint, then another after at least 30 minutes; the next checkpoint may add up to about 30 seconds. In **Players → Details → Character backups**, administrators can review those saves and restore one after the player disconnects. Recovery replaces the entire character, including inventory, skills, and progression, and retains the displaced save in `vsm-admin-backups`. Check world tombstones and stored items before restoring an older inventory to avoid duplication. A pending death save must be reconciled before recovery. Player-owned characters remain local to the player's device and have no server recovery saves.

With server-owned characters enabled, version 2.7.2 holds world admission until the client supports the death-save protocol. Before the client's death handler creates a tombstone, the server writes a `.fch.vsm-death-pending` fence next to that character's save. The client then uploads its post-death profile; only after that profile is committed does the server move the fence to `.fch.vsm-death-last` and confirm the save. A disconnect between those steps blocks the next join instead of loading a pre-death inventory alongside an existing tombstone. Ordinary checkpoints cannot overwrite a fenced character. Player-owned characters do not receive this protection, and a modified client can still lie about its character state.

An interrupted death needs administrator recovery. Stop the game server, inspect the character save and the world tombstone, and decide which item copy is valid. If the tombstone exists, ensure the `.fch` inventory no longer contains the items moved to it; if the tombstone never appeared, restore the appropriate character backup. Only then remove that character's `.fch.vsm-death-pending` file and restart the server. Never remove the fence without reconciling the inventory and grave. A server crash before the world persists a committed tombstone can still require restoration from the rolling character backup. Test death, immediate logout, network loss, and restart with a running game before deployment.

**Players** combines the roster, join requests, characters, and access lists. Select a player in any of these views to open a details dialog with Steam identity, server activity, access, and live inventory when sharing is enabled. The roster retains names and last-seen dates after players disconnect and includes owners of server-owned characters. Set `VSM_STEAM_API_KEY` in `.env` to show Steam display names and full-size avatars; the key stays on the server. Without a key, the dashboard uses known character names. Steam's [GetPlayerSummaries API](https://partner.steamgames.com/doc/webapi/ISteamUser#GetPlayerSummaries) provides profile details.

Administrators can use **Notify players** on the Players page to show a message in Valheim's HUD to everyone online, or use **Notify** from an online player's actions menu to address only that player. The manager records the target, message, and number of routed recipients in the audit log.

**Players → Characters** migrates existing native `.fch` saves after Valheim is stopped. Upload each save with its owning Steam64 ID. The manager writes the profile atomically to `characters_local` as `Steam_<Steam64>_<character>.fch`; replacements create a copy under `characters_local/vsm-import-backups/` before activation. Runtime checkpoints also retain configurable rolling backups in `characters_local/vsm-character-backups/`.

Imports are limited to 2 MiB, require a safe `.fch` filename, verify Valheim's embedded SHA-512 signature, are blocked while the game is running, and are audited with the resulting SHA-256 hash. The entire native character is migrated—not only inventory—so equipment, skills, and progression stay consistent.

## Safe console

The browser console intentionally exposes no shell and no arbitrary game terminal. Supported commands are:

```text
help
status
players
save
broadcast "message"
kick PLAYER_OR_PEER [reason]
ban PLATFORM_ID
unban PLATFORM_ID
whitelist list
whitelist add PLATFORM_ID
whitelist remove PLATFORM_ID
mods list
restart [seconds] [reason]
```

Kick and online-ban actions in the Players page accept an optional reason. Compatible clients see the rendered notice before the delayed disconnect, and the reason is included in audit and event records. Welcome, kick, ban, restart, whitelist-rejection, and client-runtime-required templates are editable under **Settings → Messages** with a fixed allowlist of placeholders.

Client notices adapt to their purpose. Welcome messages wait until the character is ready and appear briefly in the lower-right corner. Kick, ban, access, character, and missing-mod errors remain available at the menu until dismissed, with a copy-message action. The bundled client runtime displays these notices during connection and while playing. The Messages editor previews the wording and checks length and line limits before saving.

VSM no longer adds a character-negotiation delay when server-owned characters are disabled. A server that requires managed characters explicitly tells the client to wait; invalid or timed-out profiles stop the connection with an actionable notice. Inventory inspection caches icons and spreads new icon rendering across frames. See [the client experience review](docs/client-experience-review.md) for implementation details, verified behavior, and gameplay checks.

## Mods

Thunderstore installs pin exact versions and dependencies. The container-managed BepInEx pack and Server Manager package are treated as built-in infrastructure: they are hidden from catalog results, rejected as direct installs, and automatically satisfy compatible dependency declarations without overwriting live BepInEx files. The installer supports packages containing a root plugin DLL or asset tree, direct `plugins/`, `patchers/`, or `config/` directories, and wrapped `BepInEx/` layouts. Manual uploads must use the Thunderstore package layout with a root `manifest.json`; their declared Thunderstore dependencies are resolved too. Uploads reject traversal paths, symlinks, oversized archives, managed collisions, and unmanaged overwrites.

Each managed package has a structured **Configure** editor after it has loaded once. The server agent reports BepInEx plugin GUIDs, DLL locations, and primary config paths; the manager then exposes only `.cfg` files belonging to DLLs tracked by that package. The editor preserves comments and formatting, uses optimistic revision checks, masks password/token-like values, writes atomically, retains 20 backups per file, audits changes without recording values, and supports either saving for the next restart or an immediate save-and-restart. Protected manager infrastructure and unrelated config files remain inaccessible. The per-mod **Files** tab and **File manager** page add scoped folder trees and text-file creation, editing, rename, and deletion for CFG, JSON, YAML, TOML, INI, TXT, and XML. Raw content is revealed explicitly, writes are revision-checked and backed up, and links/traversal/executables are rejected. See [live inspection and mod policies](docs/live-inspection-and-mod-policies.md) for admission, file namespace, compatibility, and rollout details.

Enabled Thunderstore packages are client-required by default. Choose **Mandatory**, **Optional**, or **Server only** per package. Required dependencies remain mandatory even when their own policy is optional. The client checks package metadata already present in the active profile and never changes files. A missing required package, unlisted package, or installed optional package at a different version produces a disconnect notice. The allowlist is enforced even when there are no required gameplay mods, so the Server Manager client package is required to join. The active client list is published only by the authenticated loopback control channel and relayed over the joining peer's game RPC; clients do not need a dashboard URL, token, or per-server configuration.

Changes are staged. **Apply & restart** requests a world save, snapshots BepInEx, restarts the server, waits up to 120 seconds for the agent, and restores the snapshot if the agent does not reconnect. Mod DLLs are arbitrary native-equivalent server code; install only packages you trust.

The Mods page checks Thunderstore daily and supports reviewing updates individually or using **Stage all** before the same guarded apply/restart flow. Updates remain pinned to exact versions and are never applied merely because a newer package exists.

## Manager and dashboard updates

Settings → Updates checks stable `vX.Y.Z` releases of this repository. Manual **Save & update** and optional daily automatic updates cover the ASP.NET control plane, React dashboard, server agent, and bundled client plugin as one compatible release. Automatic application waits until no players are connected. The browser polls the running manager version and reloads its hashed UI assets after a successful container replacement.

The application container intentionally has no Docker socket. Install the narrowly scoped root host updater once from the deployment checkout:

```bash
sudo ./scripts/install-host-updater.sh /absolute/path/to/valheim-server-manager
```

The updater timer reads only versioned requests from the persistent manager volume, accepts strict semantic release versions, pulls the matching image from GitHub Container Registry, and waits for Docker health validation. It also downloads the tagged source to update the deployment's Compose file and host updater. A failed pull leaves the current deployment untouched; a failed startup restores the previous image. Update state and results remain visible in Settings → Updates and the audit log.

## Webhooks

Generic deliveries use JSON event envelopes and these headers:

```text
X-Valheim-Event
X-Valheim-Delivery
X-Valheim-Timestamp
X-Valheim-Signature-256: sha256=<HMAC(timestamp + "." + body)>
```

Subscriptions accept exact event names, `*`, or prefixes such as `player.*`. Discord is available as a formatting adapter. Chat subscriptions start absent, private whispers are never collected, and private/link-local destinations are blocked unless explicitly allowed in stored configuration.

## Development

The dashboard and control plane build independently of Valheim:

```bash
cd web && npm install && npm run build
docker build -t valheim-server-manager .
```

The plugin projects require `ValheimManaged` and `BepInExRoot` MSBuild properties. The container supplies both only after SteamCMD and BepInEx installation, which prevents committing or compiling against stale game binaries.

Run control-plane tests in a .NET 10 SDK environment:

```bash
dotnet test ValheimServerManager.slnx
```

Run the full Docker/SteamCMD/BepInEx handshake smoke test on an x86-64 Docker host:

```bash
./scripts/docker-smoke.sh
```

The smoke stack uses its own Compose project and volumes, waits for the plugin handshake, performs a controlled container restart, confirms a fresh handshake, then removes those test resources. Set `KEEP_SMOKE_STACK=true` to retain it for diagnosis.

## Publishing releases

The **Publish Manager Release** GitHub Actions workflow creates the Git tag and GitHub Release, publishes the matching Thunderstore package, and builds the x86-64 container image. Images are published to `ghcr.io/monokaijs/valheim-server-manager` with `X.Y.Z`, `vX.Y.Z`, and `latest` tags. The GHCR package must remain public so new installations and the host updater can pull it without registry credentials.

If Thunderstore publication succeeded but the container failed, use **Recover container publication** with the existing version and the full SHA of its Docker packaging fix. It checks that the release tag is an ancestor, the version matches, and application/mod source is unchanged. It publishes only the container and leaves tags and Thunderstore untouched. Enable `update_latest` only for the newest stable release. After verifying the image, complete the existing GitHub Release if it is missing; do not rerun the full publisher for that version.

Docker Hub mirroring is optional and disabled until the repository variable `DOCKERHUB_ENABLED` is set to `true`. After confirming the Docker Hub account and target repository, set `DOCKERHUB_IMAGE` to the approved `namespace/valheim-server-manager` and configure Actions secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`. Use a manually managed personal access token with Read & Write permission, no Delete permission, and an agreed expiry; enter it directly in GitHub's secret form, never in source or logs. Do not use a session-dependent Docker Desktop token. Enable mirroring only after credential setup has been approved.

Normal releases and container recovery can then mirror the successful GHCR image to Docker Hub without rebuilding. GHCR and GitHub Release publication do not depend on the Docker Hub job. To copy an existing image, use **Mirror existing container to Docker Hub** with its version and verified GHCR digest. It preserves the complete image index and digest, publishes `X.Y.Z` and `vX.Y.Z`, and updates `latest` only when requested and GHCR `latest` matches that exact digest. It never changes Git tags or publishes to Thunderstore. For 2.7.3, use the fixed GHCR digest rather than rebuilding the original release tag, which predates the Dockerfile correction.

The standalone **Publish to Thunderstore** workflow can also be manually triggered from the repository's Actions page. It downloads the current Valheim dedicated-server and BepInEx references, builds only the client plugin, packages it as **Server Manager** (`Creaton-Server_Manager` on Thunderstore), and publishes it to the Valheim community. Its BepInEx plugin ID is `dev.creaton.valheim-server-manager.client`.

For the first release, the calculation starts from `thunderstore.toml`; each successful release records a `vX.Y.Z` Git tag that becomes the base for the next increment. The workflow authenticates with the repository's `THUNDERSTORE_TOKEN` Actions secret.

## Operational notes

- `permittedlist.txt`, `bannedlist.txt`, and `adminlist.txt` are modified through Valheim's live synchronized lists when the agent is connected and through atomic files while stopped.
- An empty permitted list means the vanilla whitelist is disabled.
- Set `VSM_UPDATE_ON_START=false` to prevent SteamCMD validation on every container start.
- The manager token is generated once at `/data/manager/agent-token` unless `VSM_AGENT_TOKEN` is explicitly supplied.
- World backups and multi-server/RBAC support are intentionally outside v1; mod rollback snapshots do not replace an external world-backup policy.

This project is unofficial and is not affiliated with Iron Gate AB or Coffee Stain Publishing.

## Admin world map

The working-tree map implementation provides private live player positions, native terrain rendering and confirmed drag/coordinate teleport. See [runtime requirements, safety limits and validation](docs/admin-world-map.md). Teleport requires the updated companion client; this feature is not in the published 2.7.2 package.
