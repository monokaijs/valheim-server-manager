# Live inspection, client admission, and configuration workspaces

## Admission and rollout

Inventory inspection is mandatory for every player. Older saved settings and the former `VSM_REQUIRE_INVENTORY_INSPECTION` environment variable no longer disable it. This rule is independent of server-owned character saves. Vanilla clients cannot join.

Players need the Server Manager package and `Privacy > AllowInventoryInspection=true` in the client runtime. New installs default this setting to true; existing false settings remain an explicit refusal. Players receive a disclosure of live, read-only administrator access. The server rejects missing runtimes and refused sharing after the configured grace period; it does **not** silently rewrite a player's privacy preference.

A client can lie about reported state: these controls are admission and operational inspection, not tamper-proof anti-cheat or authoritative inventory accounting.

## Live inspection

Open **Players → Inspect character** or **Characters → Inspect live character**. The inspection desk includes a player roster, health/stamina/eitr meters, armor and carry values, spatial inventory/hotbar slots, equipment, searchable skills, selected-item metadata, and a bounded session-only slot-change feed. It uses actual client-provided PNG icons, not fabricated item artwork.

The authenticated SignalR `WatchPlayer` stream samples approximately every 1.25 seconds, after the previous request finishes. Multiple viewers of the same player share a single in-flight game request and short-lived sample. Up to four views per administrator and 64 views overall are allowed. A 64-bit string `peerKey` avoids JavaScript precision loss when targeting players.

The hub rechecks the Steam administrator list throughout a stream. Pause, tab hiding, player switching, dialog closure and disconnect cancel the subscription; an already-started shared game request may finish, but no further samples are scheduled for that closed view. Data is sent only to the subscribed administrator, never broadcast to the general live hub, persisted to the database, or forwarded to webhooks. Only watch start/stop metadata is audited. Transient samples are dropped when the last viewer leaves.

Freshness uses manager reception and browser receipt times rather than trusting the client's clock. The UI stops saying **Live** after five seconds without a fresh successful sample. Paused, stale, unavailable, offline and disconnected views clearly identify retained data as last-received information. These are sampled state changes, not a complete inventory transaction history.

## Required, optional, and server-only mods

**Mods** has separate policy filters and a three-way selector per managed Thunderstore gameplay package:

- **Required:** checked against every client profile. Its dependencies are also mandatory, regardless of their requested policy. The dashboard identifies which required package promoted a dependency.
- **Optional:** may be omitted; if installed, the listed version is allowed. Other versions block admission.
- **Server only:** not checked on clients unless required as a dependency of another package.

Legacy `true` / `false` settings retain their required / server-only meaning. The client runtime is bundled with the Server Manager package and updated through the external mod manager. The client only reads package metadata in the active BepInEx profile. It cannot download or change installed packages.

The server holds world data until it receives an acknowledgment of the current allowlist revision from the client compatibility checker. It disconnects clients without a valid acknowledgment after the configured grace period. Required packages must match exactly; optional packages may be omitted but must match if installed; unlisted plugin packages block admission. The allowlist is enforced even with no required gameplay packages. Optional package changes alter the revision. This acknowledgment reports the client's observed package metadata and is **not** cryptographic attestation of an untampered game process. Dependencies must exist, be enabled and meet the declared minimum version before they can be offered. Administrators should mark a mod optional only when that mod actually supports clients omitting it; the manager cannot make an inherently mandatory gameplay mod optional.

## Configuration files

Use **Files** to select a package, or **Mods → Configure → Files**. The existing structured **Settings** editor continues to mask sensitive values. The raw editor requires an explicit reveal action because full configuration files can contain credentials.

The file tree is restricted to the package's tracked configuration files and its plugin-GUID/package namespaces under `BepInEx/config`. It is intentionally **not** an unrestricted host filesystem browser. Protected infrastructure, DLLs and scripts are excluded. Supported text extensions: `.cfg`, `.json`, `.yaml`, `.yml`, `.toml`, `.ini`, `.txt`, `.xml`.

Create nested folders/files, read/edit text, rename files, and delete files or empty folders. Mutations use the same lock as the structured editor, atomic replacement, optimistic SHA-256 revisions, and backups for replaced/renamed/deleted files. Content is limited to 2 MiB, trees to 1,000 entries and paths to 16 segments. Traversal, absolute paths, symbolic links, binary content, invalid JSON and XML DTD/entity declarations are refused. Neither content nor secrets are written to audit records. A plugin may still write its own files; a detected concurrent write requires reloading, not overwriting the newer revision.

Backups remain under the persistent manager `config-backups` directory. The workspace marks changes pending but does not restart the server automatically. Use **Mods → Apply & restart** when ready. A newly created filename must be one that the target mod actually reads; this feature does not teach mods arbitrary new configuration layouts or make raw files part of client mod synchronization.

## Password restart regression

Saved `server.password.enabled=false` takes precedence over a populated `SERVER_PASSWORD` environment variable. The launch argument builder now always emits `-password`, using an **explicit empty argument** when disabled, rather than omitting the option and leaving the game to choose a startup default. Passwordless launches also remain unlisted (`-public 0`).

A cold-start regression test recreates the service provider against the same SQLite database and persisted Data Protection keys, with a changed non-empty environment password. It checks the saved disabled flag, explicit empty launch argument, and preservation of the previously stored password for later re-enabling. Keep `/data/manager` mounted persistently; losing that directory necessarily loses saved settings and encryption keys. A deployment where the dashboard switch itself reverts still needs its volume/configuration checked.

## Validation boundaries

Automated tests cover persisted policy/password behavior, exact peer IDs, viewer limits, path and symbolic-link refusal, revision conflicts, backups, cross-package ownership, dependency closure, legacy policy migration, optional opt-in/opt-out and server-scoped preferences. Runtime compilation verifies current game API references separately. Real game-client acceptance must also exercise live combat stats, movement/item changes, missing-mod disconnect notices, disconnect/reconnect and policy enforcement on a running server.
