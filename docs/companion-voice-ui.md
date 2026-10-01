# Companion voice UI

The gameplay HUD is hidden while idle, muted, unavailable, or below the configured
speech threshold. It appears only after a locally accepted voice send and recent
captured speech. Push to talk also requires the configured key; voice activation
retains its 300 ms send hangover, while the HUD has a shorter 120 ms speech release.
Open microphone still sends continuously, including silence; silence has no HUD.

Four original transparent PNGs (`Assets/Voice/microphone-0.png` through `-3.png`)
are embedded in the client DLL. The settings header can use the plain microphone;
the HUD uses one, two, or three waves according to smoothed input RMS relative to
the speech threshold. Attack/release smoothing, speech hysteresis and wave-level
dead bands limit flicker. Textures/sprites load once and are destroyed on unload.
There is no runtime procedural icon artwork or fallback icon. Missing/invalid PNGs
produce a bounded warning with a reinstall action. The loader resolves Unity 6's
`byte[]` overload explicitly for net48 compatibility. The original assets can be
regenerated with `python3 scripts/generate-voice-icons.py` (Pillow required).

F8 opens a 620 × 814 uGUI panel built from installed Valheim panel/button/slider
resources, without instantiating native Settings. Only the title uses the menu's
Valheim heading font/material. Body text, button labels, help and diagnostics use
a dynamic OS Arial/Helvetica/Liberation Sans/DejaVu Sans font, with a distinct
regular native font or suitable TMP default as fallback. Viking/Norse fonts and the heading
font are excluded from body fallback. Owned font atlas/material/source resources
are released on close; game-owned fonts are retained. Labels disable rich text,
so device names are displayed as text.

The input meter reflects capture already authorized by the selected mode. Opening
settings never opens an idle push-to-talk microphone. A marker shows the speech
threshold; clipping turns the meter red and suggests reducing gain. Capture
failures and connection/mod/consent gates have visible status. Separate counts
show captured, locally queued, skipped and received frames. Transport/output status
helps distinguish a closed input, failed send, no incoming voice, invalid packets
and failed output. A local queue success or consumed output sample does not prove
remote delivery or audible hardware output. Anonymous server relay diagnostics
explain eligibility rejection, recipient exclusion and no matching recipient.

The panel scales to fit the canvas; long gate/capture/playback statuses wrap.
Buttons retain explicit Up/Down focus order; sliders retain Left/Right adjustment.
F8, Escape, controller B and Close exit. Escape/B first cancel key binding. Other
native modals/chat prevent opening. A full-screen veil intercepts clicks only
while open. The native input guard blocks gameplay while open and through closing.
Cursor/focus restoration runs exactly once, including partial-open/teardown
failure. Disconnect, scene loss and unload close the panel. Reopening creates one
new panel and control set. None of these behaviors have been exercised in a live
Valheim session by this change.

## Validation

Production audio/presentation code is linked into synthetic Unity tests for HUD
visibility, silent PTT/open mic, three loudness levels, hysteresis, stale capture,
clipping, fit, font fallback policy, modal ownership restoration and audio error
paths. Pure lifecycle tests verify restoration callbacks, not real game focus.
Both plugins compile against installed Mac Valheim Unity 6000.0.75f1 assemblies.
Embedded resource inspection verifies all four packaged PNGs match their sources.

`scripts/render-voice-ui-review.mjs` renders a **layout fixture**, with actual PNGs,
production coordinates, regular body text and approximate game artwork/title font.
It is not an in-game screenshot or evidence of native resource/controller binding.
`artifacts/voice-png-pixel-review.png` shows the PNGs at source and HUD scale against
bright/dark backgrounds. Native font rendering, input focus, controller behavior,
low-resolution readability and audible voice require an approved disposable game
session. No release, live DLL replacement or deployment is performed here.
