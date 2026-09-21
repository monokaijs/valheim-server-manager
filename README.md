# Valheim Server Manager

A self-hosted Valheim control plane with a web dashboard, live server agent, server-owned characters, package management, player moderation, outbound webhooks, and a privacy-aware client companion.

## Included

- One-container Linux deployment that installs the dedicated server with SteamCMD and supervises it without access to the Docker socket.
- React/shadcn dashboard based on the `dashboard-01` shell for status, online players, live character inspection, access lists, Thunderstore/manual mods, webhooks, safe console commands, and auditing.
- BepInEx server agent built at startup against the exact installed Valheim assemblies.
- Client companion required when server-owned characters are enabled. Dashboard inventory inspection and detailed telemetry remain independently disabled until the player opts in.
- SQLite persistence, Steam OpenID authentication restricted to `adminlist.txt`, secure cookies, CSRF protection, login throttling, SignalR updates, signed webhook delivery, and automatic mod rollback.

The mandatory server plugin identifier is `dev.creaton.valheim-server-manager`; the companion uses `dev.creaton.valheim-server-manager.client`. Older `dev.monokai.*` configuration files are copied forward automatically on first load and retained as rollback copies.

## Start

Requirements: Docker Engine with Compose, an x86-64 Linux host, and roughly 5 GB of free space.

```bash
cp .env.example .env
# Edit .env. The server password must be at least five characters and must not appear in the server name.
docker compose up --build -d
docker compose logs -f valheim-manager
```

Set `VSM_PUBLIC_URL` to the externally visible dashboard origin, then open that URL. Sign-in is handled by Steam OpenID; the authenticated Steam64 ID must appear in `adminlist.txt` as either `Steam_<id>` (the normal Valheim form) or the numeric ID. Authorization is rechecked against the file on every authenticated request, so removing an administrator also invalidates their dashboard session. Put the dashboard behind an HTTPS reverse proxy before exposing it to the internet.

The first start takes several minutes because it downloads Valheim, installs BepInEx, and compiles both plugins. Game, world, manager, log, and BepInEx data live in named Docker volumes.

## Password and whitelist access

Settings → Server switches between password-protected and passwordless hosting. The password is encrypted with the manager's persisted data-protection keys and is never returned by the API. Saving performs a world save and controlled Valheim restart. The current game build rejects an empty password for a public-listed server, so passwordless mode always starts with `-public 0` and players join by IP. Password mode follows the configured `SERVER_PUBLIC` value.

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

## One-package client bootstrap

After the first successful container start, authenticated owners give players this one package:

```text
/api/v1/downloads/client-bootstrap
```

Install its ZIP through r2modman/Thunderstore Mod Manager or copy its `BepInEx` directory into the player's Valheim directory. On the first connection, server agent 1.5 relays a deterministic manifest containing VSM companion 1.3 and every enabled Thunderstore mod marked **Required** in the Mods page. The bootstrap validates package identity, exact size, and SHA-256 where supplied, stages the packages, disconnects safely, and asks for one Valheim restart. The next connection runs the server's exact client mod set.

The bootstrap owns only `BepInEx/plugins/XomNghienManaged`; personal plugins are left alone. Manual ZIP uploads and protected infrastructure are server-only because the manager has no stable Thunderstore source for them. The companion ZIP is embedded in the relayed manifest with an exact size and SHA-256, so the dashboard does not need to be publicly reachable.

Server-owned characters are enabled by default. The player can separately opt into dashboard inspection and telemetry through:

```ini
[Privacy]
AllowInventoryInspection = true
AllowDetailedTelemetry = false

[ServerCharacters]
Enabled = true
```

Character snapshots are requested live and include current stats, biome, skills, equipment, inventory placement, item metadata, durability, and game-rendered item icons. They are returned only to the authenticated dashboard and are not saved or sent to webhooks.

## Server-owned characters and migration

VSM implements its own server-character protocol in the protected server agent and client companion; it does not depend on ServerCharacters or ServerSync. The server sends its authoritative native `.fch` before player spawn. The companion installs that profile into the active Valheim session and returns native checkpoints every 30 seconds, on Valheim profile saves, on server save requests, and during normal logout handling. Clients without companion 1.2.0 or newer are rejected when server characters are enabled.

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
kick PLAYER_OR_PEER
ban PLATFORM_ID
unban PLATFORM_ID
whitelist list
whitelist add PLATFORM_ID
whitelist remove PLATFORM_ID
mods list
restart [seconds]
```

## Mods

Thunderstore installs pin exact versions and dependencies. Manual uploads must use the Thunderstore package layout with a root `manifest.json` and files under `BepInEx/`. Uploads reject traversal paths, symlinks, oversized archives, managed collisions, and unmanaged overwrites.

Each managed package has a structured **Configure** editor after it has loaded once. The server agent reports BepInEx plugin GUIDs, DLL locations, and primary config paths; the manager then exposes only `.cfg` files belonging to DLLs tracked by that package. The editor preserves comments and formatting, uses optimistic revision checks, masks password/token-like values, writes atomically, retains 20 backups per file, audits changes without recording values, and supports either saving for the next restart or an immediate save-and-restart. Protected manager infrastructure and unmanaged config files remain inaccessible.

Enabled Thunderstore packages are client-required by default. Use the **Clients** switch to mark genuinely server-only packages. The active client manifest is published only by the authenticated loopback control channel and is relayed over the joining peer's game RPC; clients do not need a dashboard URL, token, or per-server configuration.

Changes are staged. **Apply & restart** requests a world save, snapshots BepInEx, restarts the server, waits up to 120 seconds for the agent, and restores the snapshot if the agent does not reconnect. Mod DLLs are arbitrary native-equivalent server code; install only packages you trust.

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

## Publish the Thunderstore package

The **Publish to Thunderstore** GitHub Actions workflow is manually triggered from the repository's Actions page. Choose whether to increment the latest published version's patch, minor, or major component. The workflow downloads the current Valheim dedicated-server and BepInEx references, builds the server agent with the calculated version, packages it as **Server Manager** (`Creaton-Server_Manager` on Thunderstore), and publishes it to the Valheim community. Its BepInEx plugin ID is `dev.creaton.valheim-server-manager`.

For the first release, the calculation starts from `thunderstore.toml`; each successful release records a `vX.Y.Z` Git tag that becomes the base for the next increment. The workflow authenticates with the repository's `THUNDERSTORE_TOKEN` Actions secret.

## Operational notes

- `permittedlist.txt`, `bannedlist.txt`, and `adminlist.txt` are modified through Valheim's live synchronized lists when the agent is connected and through atomic files while stopped.
- An empty permitted list means the vanilla whitelist is disabled.
- Set `VSM_UPDATE_ON_START=false` to prevent SteamCMD validation on every container start.
- The manager token is generated once at `/data/manager/agent-token` unless `VSM_AGENT_TOKEN` is explicitly supplied.
- World backups and multi-server/RBAC support are intentionally outside v1; mod rollback snapshots do not replace an external world-backup policy.

This project is unofficial and is not affiliated with Iron Gate AB or Coffee Stain Publishing.
