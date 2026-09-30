# Companion voice UI

The companion HUD now uses a microphone icon rather than a transmitting text box.

| Icon | Meaning |
| --- | --- |
| Microphone with slash | Voice muted locally or disabled by the server |
| Plain microphone | Voice ready; push-to-talk may still have the microphone closed |
| Microphone with outgoing waves | Recent voice frames transmitted |
| Microphone with exclamation mark | Server/session not ready, or microphone startup/read failed |

The shapes distinguish states without depending on color. HUD textures are original procedural artwork, generated once per state and destroyed when the plugin unloads. Drawing the HUD does not capture input or open a microphone. The microphone failure indicator reflects failures observed by the audio client; it does not probe/open an idle push-to-talk microphone merely to test availability.

F8 opens voice settings during gameplay when another menu/chat/input dialog is not active. The panel uses Unity uGUI with Valheim's installed settings panel image, menu button sprites and state colors, TMP font/material, slider artwork, and UIGroupHandler. No game assets are copied into the plugin or repository, and the native Settings prefab is never instantiated. Missing required native resources or an unavailable input guard prevent the panel from opening and produce a BepInEx warning.

Mouse and keyboard input work through the native event system. The focused controls have explicit Up/Down navigation; native sliders respond to Left/Right. Escape, controller B/Back, the Close button, and F8 close the panel. Escape/B first cancel key binding when binding is active. Mode and microphone buttons cycle their choices, so the device list cannot overflow the panel. Bindings retain the existing keyboard/mouse key configuration; controller voice binding was not introduced.

The panel inherits the game's canvas scale and fits inside the canvas at lower resolutions/large UI scales. The HUD follows the canvas scale with a readability floor. The full-screen dim image intercepts clicks only while open. Menu visibility guards prevent gameplay movement/camera input while the panel is open and through its closing frame. Cursor and previous selection are restored on close. Disconnect/reset, scene loss and plugin unload close and destroy the panel; reopening builds one new panel and does not duplicate listeners.

Voice modes, gain/volume/threshold ranges, microphone selection configuration, server policy, consent advertisement, encoding, relay and proximity playback are unchanged. Push-to-talk opens the microphone only while its configured key is held. Voice activation and open microphone retain their existing during-play behavior, including while settings is open.

## Validation and review artifact

- Client compiles against installed Mac Valheim assemblies; package build compatibility is also checked using dedicated-server managed assemblies. Server plugin compiles after the shared input guard change.
- `dotnet test ValheimServerManager.slnx` includes indicator policy/readiness/failure precedence and panel fit at 1080p, 720p, 800×600, 640×360 and a 200% UI-scale canvas.
- `scripts/render-voice-ui-review.mjs` renders `artifacts/voice-ui-review.png` with the existing Playwright dependency. It is a before/after **layout fixture**, with approximate native appearance and original icon geometry, not an in-game capture or proof of native prefab binding/input behavior. Run with `VSM_TEST_BROWSER` set to an existing Chromium executable if Playwright's default is unavailable.
- No Valheim session, microphone recording, live multiplayer, player teleport or world/character modification was used for this change.

## Remaining in-game acceptance checks

Use an authorized disposable local profile/session before release:

1. Verify the installed game's settings prefab supplies the expected panel image, font, buttons and slider resources; check BepInEx for resource/input-guard warnings.
2. Inspect all four HUD shapes over bright snow, dark forest and HUD notifications, including 720p/1080p/4K and UI scales 75–200%.
3. Open/close with F8 repeatedly. Click outside the panel, bind/cancel a key, close with Escape/B, and verify camera/movement and existing menus do not receive the closing action. Confirm normal gameplay input resumes after close.
4. Navigate each control with the controller, wrap Up/Down, adjust sliders with Left/Right, activate buttons and return focus after closing. Check keyboard/mouse switches without losing selection.
5. Refresh/unplug microphones while open, cycle back to system default, and retain the configured unavailable device until explicitly changed. Verify the audio client's existing retry/permission handling and all three modes.
6. Disconnect/reconnect and change scenes with the panel open. Confirm no leftover input guard, duplicate panel, duplicate listeners or stale cursor/focus state.

Actual native resource binding, in-game controller behavior and microphone hardware/permissions remain unverified until those checks run. No release or deployment is performed by this work.
