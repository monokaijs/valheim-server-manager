# Client experience review

## Implemented

| Area | Finding | Change |
| --- | --- | --- |
| Joining | Every client with server characters enabled locally waited up to 20 seconds, even on vanilla-compatible servers. | Only an explicit server-character policy blocks spawn. Capability messages stop after acknowledgment or three attempts. Existing early profile responses remain compatible. |
| Character errors | Loading messages were no-ops. A failed profile write could be retried every frame and leave the player waiting indefinitely. | Profile errors clear pending work, block unsafe profile saves, disconnect, and show the next step. A bounded character handshake fails closed rather than falling back to a local character on a managed server. Duplicate profile responses cannot replace a live character. |
| Reconnecting | Routed RPC registration and biome state could carry across network sessions. | Registration follows the current router. Direct callbacks verify the current server RPC; reconnect clears transient state, inventory work, and icon caches. |
| Important notices | A center HUD message could disappear before the player reached the menu; clients without the full runtime had no receiver. | A shared notice panel is owned by the bundled installer, with a fallback in the full client. Access and moderation notices survive scene changes until dismissed. There is a copy-message action and Escape dismisses a menu notice. |
| Routine notices | Welcomes competed with urgent notices and could be delivered while loading. | Welcome and broadcast messages wait for the player, use a lower-right toast, and receive 8–45 seconds according to message length. Important notices take priority. Duplicates are suppressed and the queue is bounded. |
| Layout | The update prompt used a fixed 520 × 220 rectangle, default label wrapping, and only a quit button. | Scaled panels use explicit wrapping, plain text, a scroll area for long messages, and separate titles/actions. Errors offer dismissal; successful staging offers Quit Valheim and Later. No automatic quit is scheduled on a player client. Menu-only input guards prevent clicks and menu shortcuts from reaching controls behind a modal; in-world toasts do not enable these guards. |
| Mod installation | Receiving a server manifest immediately began downloading packages, and the optional picker hid package details. | The F8 picker lists required package versions and each optional group with dependencies. Optional groups start unchecked. Receiving a list only verifies local state; downloads start after **Install selected**. The screen explains that bootstrap installs are not registered in r2modman's Installed list. |
| Updates | Both relay RPC names could schedule the same work. Old server work could complete during a new connection. No download progress was visible. | Duplicate payloads are ignored, new connections cancel old work, and stale task results cannot disconnect a new session. Package and byte progress is displayed only for work that takes time. Download inactivity is bounded, while active slow transfers may continue. |
| Update application | A changed manifest with unchanged package coordinates could replace plugin files while their assemblies were loaded. | New gameplay packages are staged for startup after player approval. Installed packages and managed configs cannot be replaced or removed by the installer. The VSM client runtime ships in the Thunderstore bundle and is updated through an external mod manager. Returning to the installed server clears a superseded pending install. |
| Inventory inspection | Every snapshot repeated GPU readback and PNG encoding for all icons. | Up to 256 sprite encodings are cached per connection. At most two uncached icons are rendered per frame. Requests are rate limited, and consent and connection identity are checked again before the response is sent. |
| Telemetry | Opted-out clients still performed reflection and built event payloads. Biome checks ran each frame. | Event hooks return before collecting data when telemetry is off. Opted-in biome checks run once per second; localization reflection is cached. |
| Server overhead | Maintenance repeatedly allocated LINQ results every frame and scanned reflection metadata for RPC sends. | Maintenance runs four times per second; command draining is bounded per frame; RPC sends use the available typed API. Disconnect cleanup removes pending requests and character/death state. |
| Message authoring | Owners had no preview or local line-limit feedback; pasted Windows newlines were rejected. | The dashboard previews the sample client wording, shows character counts, validates the four-line limit, and supports direct Preview actions. The settings tabs and preview work at phone widths. Server validation normalizes line endings before counting. |

## Validation

- The control-plane tests and new plugin regression suite run with `dotnet test ValheimServerManager.slnx`. The plugin tests do not require Unity or a game installation.
- Client, server, bootstrap, and runtime updater compile against the locally available Valheim dedicated-server and BepInEx assemblies.
- The production dashboard builds with TypeScript checks.
- The built Messages interface was checked in an isolated browser fixture at desktop and 390-pixel phone widths. Checks cover wrapping, preview wording, editing, five-line rejection, and horizontal overflow. This fixture uses sample data and does not write to a real server.
- The native overlay follows Unity's [IMGUI scroll-view contract](https://docs.unity3d.com/ScriptReference/GUI.BeginScrollView.html), but compilation and browser previews do not establish its actual appearance or input behavior in Valheim.

No FPS improvement or load-time benchmark is claimed. The changes remove specific repeated work identified in the code; profiling inside the game is required to quantify the effect.

## Gameplay acceptance checks

Use a disposable mod-manager profile and a test server before publishing a release.

1. Join vanilla, VSM local-character, and VSM server-character servers. Confirm that only the last waits for a profile, and that imported inventory, skills, and spawn data match the server save.
2. Test a missing imported character, an invalid profile, a used character where a new one is required, and a delayed profile. Confirm one readable reason at the menu and no local-character fallback on a managed server.
3. Connect with the bundled client and installer installed. Check whitelist rejection and confirm no download begins before **Install selected**. Repeat with the full client and verify a notice is rendered once.
4. Test a kick and ban during play and while loading. Confirm the reason remains visible after disconnecting. Dismiss with mouse and Escape; check controller navigation and cursor behavior separately.
5. Test one-line and maximum-length notices at 720p, 1080p, and 4K, including longer translated text. Check scrolling, contrast, and overlap with HUD elements.
6. Test an unchanged manifest, a new mod version, a rebuild under the same version, and a configuration-only repair. Confirm each change is listed without downloading; after **Install selected**, changed files should remain staged until restart. Later should leave the menu usable; a reconnect to the same server should still require restart.
7. Interrupt a download, reconnect to a different server, and test a stalled transfer. An old result must not disconnect the new session. Verify that failed staging preserves the installed mods.
8. Inspect the same inventory repeatedly, then disconnect during a snapshot or disable inspection consent before it completes. Verify icon reuse, bounded frame work, and no response carrying a previous session's inventory.

## Further protocol work

Character checkpoints still use the existing fire-and-forget upload protocol. A separate durable-save acknowledgment and recovery design would let the client distinguish a transmitted checkpoint from one actually committed by the server, particularly at logout or on abrupt network loss. This deserves its own compatibility and recovery tests rather than being treated as an incidental UI change. First-join migration also warrants an end-to-end comparison of all native profile fields, beyond the current player-data adoption path.

The native overlay and the final logout checkpoint were not tested in a running multiplayer game during this review. Those checks remain release prerequisites.
