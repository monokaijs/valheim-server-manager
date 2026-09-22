# Client experience review

## Implemented

| Area | Finding | Change |
| --- | --- | --- |
| Joining | Every client with server characters enabled locally waited up to 20 seconds, even when server-owned characters were disabled. | Only an explicit server-character policy blocks spawn. Capability messages stop after acknowledgment or three attempts. Existing early profile responses remain compatible. |
| Character errors | Loading messages were no-ops. A failed profile write could be retried every frame and leave the player waiting indefinitely. | Profile errors clear pending work, block unsafe profile saves, disconnect, and show the next step. A bounded character handshake fails closed rather than falling back to a local character on a managed server. Duplicate profile responses cannot replace a live character. |
| Reconnecting | Routed RPC registration and biome state could carry across network sessions. | Registration follows the current router. Direct callbacks verify the current server RPC; reconnect clears transient state, inventory work, and icon caches. |
| Important notices | A center HUD message could disappear before the player reached the menu. | The bundled client notice panel keeps access and moderation messages visible until dismissed, with a copy-message action. |
| Routine notices | Welcomes competed with urgent notices and could be delivered while loading. | Welcome and broadcast messages wait for the player, use a lower-right toast, and receive 8–45 seconds according to message length. Important notices take priority. Duplicates are suppressed and the queue is bounded. |
| Layout | Long notices need readable layouts. | Scaled panels use wrapping and scroll areas. Menu-only input guards prevent clicks behind a modal. |
| Mod compatibility | Players need to know which package versions the server requires. | The client checks installed package metadata against the server allowlist. Missing required packages, unlisted packages, and wrong optional versions disconnect with a persistent notice. The server holds world data until a valid receipt arrives. There is no mod screen or install path. |
| Inventory inspection | Every snapshot repeated GPU readback and PNG encoding for all icons. | Up to 256 sprite encodings are cached per connection. At most two uncached icons are rendered per frame. Requests are rate limited, and consent and connection identity are checked again before the response is sent. |
| Telemetry | Opted-out clients still performed reflection and built event payloads. Biome checks ran each frame. | Event hooks return before collecting data when telemetry is off. Opted-in biome checks run once per second; localization reflection is cached. |
| Server overhead | Maintenance repeatedly allocated LINQ results every frame and scanned reflection metadata for RPC sends. | Maintenance runs four times per second; command draining is bounded per frame; RPC sends use the available typed API. Disconnect cleanup removes pending requests and character/death state. |
| Message authoring | Owners had no preview or local line-limit feedback; pasted Windows newlines were rejected. | The dashboard previews the sample client wording, shows character counts, validates the four-line limit, and supports direct Preview actions. The settings tabs and preview work at phone widths. Server validation normalizes line endings before counting. |

## Validation

- The control-plane tests and new plugin regression suite run with `dotnet test ValheimServerManager.slnx`. The plugin tests do not require Unity or a game installation.
- The client and server plugins compile against Valheim dedicated-server and BepInEx assemblies in the package workflow.
- The production dashboard builds with TypeScript checks.
- The built Messages interface was checked in an isolated browser fixture at desktop and 390-pixel phone widths. Checks cover wrapping, preview wording, editing, five-line rejection, and horizontal overflow. This fixture uses sample data and does not write to a real server.
- Compilation and browser previews do not establish the native notice panel's actual appearance or input behavior in Valheim.

No FPS improvement or load-time benchmark is claimed. The changes remove specific repeated work identified in the code; profiling inside the game is required to quantify the effect.

## Gameplay acceptance checks

Use a disposable mod-manager profile and a test server before publishing a release.

1. Join vanilla, VSM local-character, and VSM server-character servers. Confirm that only the last waits for a profile, and that imported inventory, skills, and spawn data match the server save.
2. Test a missing imported character, an invalid profile, a used character where a new one is required, and a delayed profile. Confirm one readable reason at the menu and no local-character fallback on a managed server.
3. Connect with the bundled client. Confirm no mod button or F8 screen appears. Verify whitelist notices render once.
4. Test a kick and ban during play and while loading. Confirm the reason remains visible after disconnecting. Dismiss with mouse and Escape; check controller navigation and cursor behavior separately.
5. Test one-line and maximum-length notices at 720p, 1080p, and 4K, including longer translated text. Check scrolling, contrast, and overlap with HUD elements.
6. Test exact, missing, unlisted, and wrong optional package versions. Confirm only allowed profiles acknowledge the server requirement; rejected profiles receive a readable disconnect notice, no world entry, and no file changes. Confirm a vanilla or modified client without a receipt times out before world data is released.
7. Reconnect to a different server and verify only the current server's required catalog is checked.
8. Inspect the same inventory repeatedly, then disconnect during a snapshot or disable inspection consent before it completes. Verify icon reuse, bounded frame work, and no response carrying a previous session's inventory.

## Further protocol work

Character checkpoints still use the existing fire-and-forget upload protocol. A separate durable-save acknowledgment and recovery design would let the client distinguish a transmitted checkpoint from one actually committed by the server, particularly at logout or on abrupt network loss. This deserves its own compatibility and recovery tests rather than being treated as an incidental UI change. First-join migration also warrants an end-to-end comparison of all native profile fields, beyond the current player-data adoption path.

The native overlay and the final logout checkpoint were not tested in a running multiplayer game during this review. Those checks remain release prerequisites.
