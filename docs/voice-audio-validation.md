# Voice audio validation

The reported lack of audible voice is **not yet reproduced on real hardware**.
The implementation corrects confirmed send-accounting and playback-recovery
problems and adds diagnostics to identify the first failing stage. Synthetic
fixtures are not proof that a microphone, game RPC socket, Unity audio device or
speaker works.

## Production path and gates

1. Client `OnNewConnection` registers voice policy/frame RPCs against the server
   peer. `Update` checks server policy, capability/character handshake, mod-list
   verification, inventory consent and local character readiness. Voice
   participation is advertised only after a successful local RPC invoke; failed
   advertisement is retried and capture remains closed until advertisement succeeds.
2. Enabled push to talk opens input only while the key is held. Activation/open
   mic capture during play. `Microphone.Start` requests 16 kHz; supported clip
   channels/capacity/frequency are validated. The reflected `GetData(float[], int)`
   overload reads 640 sample frames per channel, downmixes, applies finite gain,
   clamps and computes RMS/clipping. Unity cursor/offset units remain sample frames.
3. Capture backlog resumes at the latest complete frame. Read failure is visible
   immediately; a 0.5 s read failure or 2 s stalled/invalid cursor stops capture
   and retries after 3 s. Missing inputs, incompatible formats, API/start/read
   failures and silence have distinct actionable capture status.
4. Activation sends above the configured RMS threshold with a 300 ms hangover.
   Encoding produces one 640-byte mono frame. The transport callback returns
   success only after connected-socket/backlog checks and `ZRpc.Invoke`. Frames
   skipped or throwing during send do not increment sent counts or transmission
   time, and do not falsely mark a healthy microphone failed.
5. Server RPC registration is refreshed from `Update`. Relay requires server
   enablement, a ready peer, voice participation, inventory consent, a current
   required-mod receipt, no scheduled kick and valid character ZDO. A 25-frame/s
   token bucket limits input. The sender is excluded. Each recipient also needs
   eligibility, a character, proximity, a socket and less than 16 KiB queued data.
6. Accepted recipients receive a sender/position/frame envelope. Client receipt
   checks server identity, enablement and local character. Base64/envelope errors
   are counted. Playback creates a mono streamed AudioClip and spatial AudioSource,
   decodes into a bounded six-frame queue and starts after two frames. Failure
   destroys that stream so the next frame can retry. Live policy updates adjust
   existing playback range. Consumed samples/underruns are counted atomically in
   the audio callback; no log is emitted from the audio thread.

Capture/transport/output status and cumulative connection counters appear in F8
settings. BepInEx `Voice flow:` summaries are emitted at most once every 10 seconds
when state/counts change. They include capture/encode/local-send/drop/receive/
receive-gate-drop/decode/start/output-consumption/underrun/invalid/failure/clipping
counts. `Voice relay:` server summaries use bounded anonymous reason counters at
most once every 10 seconds. `NoRecipient` means a valid/rate-accepted source frame
had no successful recipient RPC invoke. Recipient reasons show why; sender
eligibility failures are recorded before this point. Counts contain no player or
device identifiers, encoded packets, recorded audio or private exception messages.

| First stalled stage | Next check |
| --- | --- |
| Capture remains zero while PTT held | Selected input, OS permission/mute, API/format/start/read status |
| Capture increases, encode stays zero | Voice activation threshold and measured level |
| Encode increases, local-send stays zero | Socket connection/backlog, send status, voice advertisement |
| Local-send increases, server rejects | Participation, consent, receipt, character/admission/rate counters |
| Server accepts but NoRecipient increases | Another eligible client within range; sender never self-monitors |
| Relay increases but receive stays zero | Recipient connection/RPC registration and local receive gate |
| Receive increases but decode stalls | Invalid packet or output creation/property failures |
| Decode increases but start/output stalls | Two-frame preload, AudioSource/device failure and output callback |
| Output consumes samples but nothing audible | Sender silence, playback/game/OS volume, spatial listener/output device |

## Mac validation and limits

The .NET 10 suite runs in an offline disposable Docker build stage using the
existing cached test runtime. Tests link production VoiceChatClient/VoiceCodec,
VoicePresentation and relay rules to synthetic Unity boundaries. Coverage includes
mono/stereo/multichannel capture, ring wrap/backlog, read/start/stall/retry failures,
privacy, threshold/hangover, silent HUD, three-wave smoothing, clipping, truthful
send/drop accounting, safe exception messages, invalid receive/gate counters,
playback creation/start recovery, decoded/output/underrun counters, policy updates
and reset. A two-client **synthetic transport** fixture runs capture → encode →
relay rules/envelope → receive → decode → output callback, with self exclusion.
It does not execute the live server RPC handler/socket, native microphone or audio
hardware. Modal tests cover ownership callbacks, not actual native UI focus.

Both net48 plugins build with SDK 8 against the installed Mac game assemblies
(Unity 6000.0.75f1). PNG alpha/corners and actual HUD-scale pixels were inspected;
DLL manifest resource bytes match all four source PNGs. The UI screenshot is an
honestly labeled layout fixture. No native microphone was opened or recorded.

Read-only SSH inspection on 2026-10-01 found no persisted `voice-chat.enabled` or
`voice-chat.range` overrides in the manager database (service defaults: enabled,
40 units). Recent container output and today's server log had no stage diagnostics.
This does not prove capture, forwarding or playback, and does not identify the
reported audio root cause. No server restart/configuration/world changes occurred.

## Exact remaining acceptance

Obtain approval to install these staged plugins **only in a disposable test
profile/server** with two authorized matching clients, including the Mac input
and output hardware to be used. Separately approve opening the chosen microphone
for a short PTT test; OS permission grants require explicit approval.

Place both clients within two world units. Verify muted/idle HUD is absent, open
F8, hold PTT for 10 seconds and speak softly/normally/loudly while watching the
meter/waves. Check capture → local-send → anonymous relay → remote-receive →
decode → callback counts at each end; confirm the other person hears speech and
the sender does not hear self-loopback. Repeat reversed direction, then silence,
activation, open mic, range exclusion and output-volume zero/recovery. Exercise
F8/Escape/B/open-close, binding cancel, controller navigation, cursor/focus return,
scene/disconnect cleanup and body/title fonts at relevant resolutions/UI scales.
Keep logs free of audio/payloads; recording is not required. Live-game deployment,
restarts, permissions and release publication remain outside this implementation.
