# Changelog

## Unreleased

- Prevent ordinary voice RPC batches from losing frames while retaining the 25-frame/s sustained limit and a bounded burst.
- Catch up microphone frames at low rendering rates, buffer received voice for jitter, and re-prime/fade after starvation without repeating old speech.
- Bypass environmental reverb and audio effects for clearer positional speech; preserve gradual distance fade and live server-range updates.
- Rebuild F8 voice sliders with centered thumbs, aligned tracks, larger hit rows, clear percentage/gain/RMS units and fine keyboard/controller steps.
- Pause capture while rebinding the talk key, retain remote playback, and expose safe callback/rebuffer/trim diagnostics. Synthetic checks pass; native listening and UI acceptance remain pending.

## 2.7.6

- Use transparent PNG voice icons; show the HUD only during speech, with waves following input loudness.
- Redesign F8 voice settings with a regular body font, heading-only Valheim styling, input meter, clipping warnings and capture/transport/output status.
- Count locally accepted voice sends accurately, honor consent/participation capture gates, and recover failed playback streams.
- Add anonymous, rate-limited diagnostics across capture, relay and playback to investigate silent audio without recording or logging voice contents.
- Start the companion requirement grace period after native authentication, preventing premature disconnects during connection setup.

## 2.7.2

- Keep five periodic server-owned character restore points, sampled 30 minutes apart while the player is online.
- Let administrators review and restore a player's saved character from the dashboard after the player disconnects.
- Persist a death fence before creating a tombstone for a server-owned character, and require a committed post-death profile before clearing it.
- Hold world admission until a client with the death-safe character protocol connects. Interrupted deaths require administrator recovery instead of restoring a stale inventory.

## 2.7.0

- Added built-in proximity voice chat with push-to-talk, voice activation, open mic, spatial playback, microphone gain, client opt-out, and server range controls.
- Added an in-game F8 voice settings panel for microphone selection and talk-key rebinding.

## 2.6.0

- Added optional Discord Rich Presence for players, showing the server, online count, and current biome.
- Added dashboard controls for Discord application ID, HTTPS activity artwork, and customizable details and state templates.
- Added template variables for server and world names, online players, capacity, free slots, and each player's current biome.
- Improved monitor trend charts with current and peak readings and clearer scaling.

## 2.4.0

- Added a dashboard notification tool for one online player or everyone on the server.
- Added administrator item giving from the inventory page with item search, quality, quantity, and delivered-count feedback.
- Replaced the generated package icon with dedicated Server Manager artwork.

## 2.3.0

- Removed the Server Manager notice panel from active gameplay. Welcome messages, broadcasts, and moderation reasons use Valheim's built-in HUD; persistent notices remain available at the menu.
- Updated the BepInExPack Valheim dependency to 5.4.2351 so the client package works with current mod requirements.
- The Docker manager now checks for newer BepInExPack releases at startup and upgrades existing server volumes while preserving installed mods and configuration. Release checks detect stale package dependencies.

## 2.2.3

- Required the Server Manager client runtime and inventory sharing for every server player. New client installs enable sharing by default; existing disabled settings still refuse sharing and are rejected after the grace period.
- Removed the in-game F8 mod screen and its on-screen button. Missing required mods and unlisted installed mods now disconnect the bundled client with a persistent notice. Listed optional mods may be omitted, but installed versions must match. The server holds world data until it receives a valid allowlist acknowledgment.

## 2.2.2

- Bundled the client runtime in the Server Manager package for updates through an external mod manager.
- Replaced the in-game installer and bootstrap with a read-only F8 compatibility view. It checks required and optional package versions in the active profile and never downloads or changes mods.
- The Thunderstore ZIP now contains only the client plugin; Docker installs the server agent separately.

## 1.6.6

- Unified server installation and the client update receiver into one Server Manager package and version.
- Removed the separately branded bootstrap and companion downloads.
- Kept the automatically relayed client runtime internal to Server Manager.

## 1.6.0

- Added reasoned kick/ban actions, editable server message templates, and client-visible administrative notices.
- Added explicit whitelist status and clearer companion-required rejection messaging.

## 1.5.0

- Added the server-side agent for live administration, access lists, safe console commands, and event delivery.
- Added managed Thunderstore packages, configuration editing, staged restarts, and rollback support.
- Added privacy-aware client coordination and authoritative server-owned character support.
- Added the **Server Manager** plugin identity, `dev.creaton.valheim-server-manager`, with migration from the legacy identifier.
