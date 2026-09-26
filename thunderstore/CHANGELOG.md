# Changelog

## Unreleased

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
