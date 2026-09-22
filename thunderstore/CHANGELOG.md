# Changelog

## Unreleased

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
