# Valheim Server Manager

A self-hosted Valheim control plane with a web dashboard, live server agent, server-owned characters, package management, player moderation, outbound webhooks, and a privacy-aware client runtime delivered by the same Server Manager package.

## Included

- One-container Linux deployment that installs the dedicated server with SteamCMD and supervises it without access to the Docker socket.
- React/shadcn dashboard based on the `dashboard-01` shell for status, online players, live character inspection, access lists, Thunderstore/manual mods, webhooks, safe console commands, and auditing.
- BepInEx server agent built at startup against the exact installed Valheim assemblies.
- Vanilla-compatible by default. Server-owned characters and managed-client enforcement are a single optional switch; dashboard inventory inspection and detailed telemetry remain independently opt-in.
- SQLite persistence, Steam OpenID authentication restricted to `adminlist.txt`, secure cookies, CSRF protection, login throttling, SignalR updates, signed webhook delivery, and automatic mod rollback.

The server plugin identifier is `dev.creaton.valheim-server-manager`; the automatically managed client runtime uses `dev.creaton.valheim-server-manager.client`. Older `dev.monokai.*` configuration files are copied forward automatically on first load and retained as rollback copies.

## Start

Requirements: Docker Engine with Compose, an x86-64 Linux host, and roughly 5 GB of free space.

```bash
cp .env.example .env
# Optional: edit the server name, world, ports, and password in .env.
docker compose pull
docker compose up -d
```

That is enough to start a private, passwordless, vanilla-compatible server. Open port `8080` for the dashboard and UDP `2456-2458` for Valheim. Set `VSM_PUBLIC_URL` before using Steam dashboard sign-in; the authenticated Steam64 ID must appear in `adminlist.txt` as either `Steam_<id>` or the numeric ID. Put the dashboard behind HTTPS before exposing it to the internet.

The first start takes several minutes because it downloads Valheim, installs BepInEx, and compiles both plugins. Game, world, manager, log, and BepInEx data live in named Docker volumes.

## Password and whitelist access

Settings → Server manages the server name and world, password and public listing, Steam/crossplay backend, PlayFab instance ID, native save and backup cadence, player capacity, and every official world preset/modifier. The password is encrypted with the manager's persisted data-protection keys and is never returned by the API. Saving performs a world save and controlled Valheim restart. The current game build rejects an empty password for a public-listed server, so passwordless mode always starts with `-public 0` and players join by IP or crossplay join code.

World modifiers are opt-in for existing installations: until **Manage gameplay and exploration modifiers** is enabled, VSM omits `-preset`, `-modifier`, and `-setkey` so it does not overwrite settings already stored in the world. Once enabled, the selected preset is applied first, followed by combat, death-penalty, resource, raid, portal, no-build-cost, player-event, passive-mob, and no-map overrides on every start.

Valheim officially supports 10 concurrent players. Settings from 1–10 use the same server-side admission control; values from 11–100 additionally patch the BepInEx server agent's Steam/PlayFab capacity and are clearly marked as an unsupported, modded configuration. Clients do not need a player-limit mod. The game port remains deployment-owned because changing it also requires changing Docker's published UDP ports; edit `SERVER_PORT`, `SERVER_QUERY_PORT`, and `SERVER_EXTRA_PORT` together in `.env` and recreate the container.

Adding any entry to `permittedlist.txt` enables Valheim's vanilla whitelist; players not on that list are rejected. Each rejected, non-banned platform identity automatically creates its own pending request under **Join requests**. Repeated attempts update that request's player name, last-attempt time, and attempt count. Approval adds the exact identity to Valheim's live permitted list; denial closes only that request. The player reconnects after approval. Removing the final permitted entry disables the whitelist.

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

## One Server Manager package

After the first successful container start, authenticated owners give players this one package:

```text
/api/v1/downloads/plugin
```

Install the Server Manager ZIP through r2modman/Thunderstore Mod Manager or copy its `BepInEx` directory into Valheim. The same package is safe on dedicated servers and player clients: server-only code is disabled in the client process, while the client update receiver is disabled in the dedicated-server process. On connection, server agent 2.1.2 relays a deterministic manifest containing the size- and SHA-256-verified VSM client runtime, its runtime updater, and every enabled Thunderstore mod marked **Required** in the Mods page. An older client stages the server's exact VSM version through the same pipeline as other required mods, disconnects safely, and asks for one Valheim restart. Before BepInEx loads plugins on the next launch, the stable preloader promotes the new runtime updater and atomically activates the complete managed set. There is no second VSM mod for players to install or publish.

Server Manager synchronization owns `BepInEx/plugins/ValheimServerManagerManaged` and its single allowlisted updater DLL at `BepInEx/plugins/ValheimServerManager/ValheimServerManagerRuntimeUpdater.dll`; personal plugins are left alone. Manual ZIP uploads and other protected infrastructure are server-only because the manager has no stable Thunderstore source for them. The client-targeted Server Manager runtime is embedded in the relayed manifest with an exact size and SHA-256, so the dashboard does not need to be publicly reachable.

Players without mods can join by default. Under **Settings → Server → Client compatibility**, enable managed-client mode only when server-owned characters are wanted. The same defaults are configurable before first start:

With server-owned characters disabled, VSM leaves Valheim's peer-info and world-data connection path untouched. Optional notices, telemetry, and manifest discovery never delay admission; only Valheim's native authentication/version rules and explicitly required managed mods can block a connection.

```env
VSM_SERVER_CHARACTERS_ENABLED=false
VSM_SERVER_CHARACTERS_ACCEPT_FIRST_JOIN=true
VSM_SERVER_CHARACTERS_REJECT_USED=false
VSM_SERVER_CHARACTERS_BACKUPS=10
VSM_CLIENT_MOD_GRACE_SECONDS=20
```

When server-owned characters are enabled, the player can separately opt into dashboard inspection and telemetry through:

```ini
[Privacy]
AllowInventoryInspection = true
AllowDetailedTelemetry = false

[ServerCharacters]
Enabled = true
```

Character snapshots are requested live and include current stats, biome, skills, equipment, inventory placement, item metadata, durability, and game-rendered item icons. They are returned only to the authenticated dashboard and are not saved or sent to webhooks.

## Server-owned characters and migration

VSM implements its own server-character protocol in the protected server agent and client runtime; it does not depend on ServerCharacters or ServerSync. The server sends its authoritative native `.fch` before player spawn. The client runtime installs that profile into the active Valheim session and returns native checkpoints every 30 seconds, on Valheim profile saves, on server save requests, and during normal logout handling. Clients without a compatible Server Manager runtime are rejected when server characters are enabled.

The Characters page migrates existing native `.fch` saves after Valheim is stopped. Upload each save with its owning Steam64 ID. The manager writes the profile atomically to `characters_local` as `Steam_<Steam64>_<character>.fch`; replacements create a copy under `characters_local/vsm-import-backups/` before activation. Runtime checkpoints also retain configurable rolling backups in `characters_local/vsm-character-backups/`.

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

Client notices adapt to their purpose. Welcome messages wait until the character is ready and appear briefly in the lower-right corner. Kick, ban, access, and character errors remain available at the menu until dismissed, with a copy-message action. The update receiver can display these notices even before the full client runtime is installed. Mod downloads show progress; a successful update offers **Quit Valheim** or **Later**, while a failed update explains how to retry without forcing the game to quit. The Messages editor previews the wording and checks length and line limits before saving.

VSM no longer adds a character-negotiation delay to vanilla connections. A server that requires managed characters explicitly tells the client to wait; invalid or timed-out profiles stop the connection with an actionable notice. Inventory inspection caches icons and spreads new icon rendering across frames. See [the client experience review](docs/client-experience-review.md) for implementation details, verified behavior, and gameplay checks.

## Mods

Thunderstore installs pin exact versions and dependencies. The container-managed BepInEx pack and Server Manager package are treated as built-in infrastructure: they are hidden from catalog results, rejected as direct installs, and automatically satisfy compatible dependency declarations without overwriting live BepInEx files. The installer supports packages containing a root plugin DLL or asset tree, direct `plugins/`, `patchers/`, or `config/` directories, and wrapped `BepInEx/` layouts. Manual uploads must use the Thunderstore package layout with a root `manifest.json`; their declared Thunderstore dependencies are resolved too. Uploads reject traversal paths, symlinks, oversized archives, managed collisions, and unmanaged overwrites.

Each managed package has a structured **Configure** editor after it has loaded once. The server agent reports BepInEx plugin GUIDs, DLL locations, and primary config paths; the manager then exposes only `.cfg` files belonging to DLLs tracked by that package. The editor preserves comments and formatting, uses optimistic revision checks, masks password/token-like values, writes atomically, retains 20 backups per file, audits changes without recording values, and supports either saving for the next restart or an immediate save-and-restart. Protected manager infrastructure and unmanaged config files remain inaccessible.

Enabled Thunderstore packages are client-required by default. Use the **Clients** switch to mark genuinely server-only packages. Gameplay-mod manifests contain only the selected Thunderstore packages; the embedded VSM client runtime is included only when server-owned characters require it. The active client manifest is published only by the authenticated loopback control channel and is relayed over the joining peer's game RPC; clients do not need a dashboard URL, token, or per-server configuration.

Changes are staged. **Apply & restart** requests a world save, snapshots BepInEx, restarts the server, waits up to 120 seconds for the agent, and restores the snapshot if the agent does not reconnect. Mod DLLs are arbitrary native-equivalent server code; install only packages you trust.

The Mods page checks Thunderstore daily and supports reviewing updates individually or using **Stage all** before the same guarded apply/restart flow. Updates remain pinned to exact versions and are never applied merely because a newer package exists.

## Manager and dashboard updates

Settings → Updates checks stable `vX.Y.Z` releases of this repository. Manual **Save & update** and optional daily automatic updates cover the ASP.NET control plane, React dashboard, server agent, and generated client artifact as one compatible release. Automatic application waits until no players are connected. The browser polls the running manager version and reloads its hashed UI assets after a successful container replacement.

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

The standalone **Publish to Thunderstore** workflow can also be manually triggered from the repository's Actions page. It downloads the current Valheim dedicated-server and BepInEx references, builds the server agent with the calculated version, packages it as **Server Manager** (`Creaton-Server_Manager` on Thunderstore), and publishes it to the Valheim community. Its BepInEx plugin ID is `dev.creaton.valheim-server-manager`.

For the first release, the calculation starts from `thunderstore.toml`; each successful release records a `vX.Y.Z` Git tag that becomes the base for the next increment. The workflow authenticates with the repository's `THUNDERSTORE_TOKEN` Actions secret.

## Operational notes

- `permittedlist.txt`, `bannedlist.txt`, and `adminlist.txt` are modified through Valheim's live synchronized lists when the agent is connected and through atomic files while stopped.
- An empty permitted list means the vanilla whitelist is disabled.
- Set `VSM_UPDATE_ON_START=false` to prevent SteamCMD validation on every container start.
- The manager token is generated once at `/data/manager/agent-token` unless `VSM_AGENT_TOKEN` is explicitly supplied.
- World backups and multi-server/RBAC support are intentionally outside v1; mod rollback snapshots do not replace an external world-backup policy.

This project is unofficial and is not affiliated with Iron Gate AB or Coffee Stain Publishing.
