# Voice audio validation

The user confirmed that **2.7.6 produces audible remote speech**, then reported
karaoke-like reverb and interruptions. Listening quality remains unaccepted.
Read-only live relay counters showed 92 rate-limited frames among 622 otherwise
eligible frames (14.8%) while all 530 accepted frames reached a recipient.
This confirms relay loss; it does not identify the precise native callback size
or prove that the reverb tail originates in the game rather than an input device.

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
3. Each update catches up at most eight frames (320 ms). A backlog beyond half the input ring resumes at the latest complete frame. Read failure is visible
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
   token bucket permits an eight-frame/320 ms burst so ordinary RPC batching does not discard speech. Credits remain bounded, including backward-clock handling. The sender is excluded. Each recipient also needs
   eligibility, a character, proximity, a socket and less than 16 KiB queued data.
6. Accepted recipients receive a sender/position/frame envelope. Client receipt
   checks server identity, enablement and local character. Base64/envelope errors
   are counted. Playback creates a mono streamed AudioClip and spatial AudioSource,
   bypasses source/listener effects and reverb zones while retaining 3D linear distance rolloff (full volume within two units, fading to zero at server range). Decode feeds a bounded 480 ms queue, starts with 120 ms, and adapts the priming cushion to observed callback requests. Starvation fades the final 64 samples into silence, then re-primes instead of exposing isolated late frames. No old speech is repeated; an arrival after a 500 ms pause discards any unplayed tail. Failure
   destroys that stream so the next frame can retry. Live policy updates adjust
   existing playback range. Consumed samples/underruns are counted atomically in
   the audio callback; no log is emitted from the audio thread.

Capture/transport/output status and cumulative connection counters appear in F8
settings. BepInEx `Voice flow:` summaries are emitted at most once every 10 seconds
when state/counts change. They include capture/encode/local-send/drop/receive/
receive-gate-drop/decode/start/output-consumption/underrun/invalid/failure/clipping
counts, rebuffer waits, trimmed samples and maximum callback request size. `Voice relay:` server summaries use bounded anonymous reason counters at
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
| Decode increases but start/output stalls | Preload/rebuffer cushion, AudioSource/device failure and output callback |
| Output consumes samples but nothing audible | Sender silence, playback/game/OS volume, spatial listener/output device |

## Mac validation and limits

SDK 8 builds both net48 plugins against installed Mac Unity 6000.0.75f1, including
actual AudioSource effect properties and the custom Slider subclass. The .NET 10
suite uses a disposable offline Docker stage. Production capture, codec, buffer,
slider event handling, relay policy and rate budget are linked into synthetic
Unity boundaries; controller tests exercise production OnMove, not a controller.

A separate deterministic fixture extracts the current server `OnVoiceFrame` and
`VoiceEligibility` **verbatim**, replacing only clock/peer/socket/Unity boundaries.
It runs synthetic PCM through server routing and production receive/decode/output.
Released code at normal 25 frames/s loses 50/250 frames with 100 ms batching and
150/250 with 200 ms batching. Timed 1,024-sample output callbacks contain 19.7% /
59.6% zero fill after warmup. The fix retains all 250 frames and produces zero fill
of 0% in those scenarios. At 10 rendering updates/s, released capture delivers
196/250 frames; the fix delivers 250/250. An abuse fixture admits only eight of
1,000 simultaneous frames, then one additional frame after 40 ms. These are
synthetic behavior checks, not measurement of real microphone or speaker quality.

Native callbacks can differ from 1,024 samples. `callbackSamplesMax` now reports
the observed request size without samples or identifiers. The queue/cushion stay
bounded at 480 ms; larger callbacks or long reliable-transport stalls can still
cause gaps. No concealment claims are made for audio that was never received.
The stream clip is shortened from one second to 160 ms so a clip-sized prefill fits the bounded queue; a synthetic creation-time prefill regression covers this case. This does not prove native callback sizes. The initial cushion adds buffering latency, and very short utterances/endings
must be included in hardware acceptance. The existing mono 16 kHz μ-law wire
format is unchanged; a pure 440 Hz synthetic tone measures about 36.45 dB SNR.

The installed ZRpc drains pending packets in one update. Installed Steam sockets
use Reliable (flag 8); PlayFab uses Guaranteed delivery and sequence/duplicate
handling. One production playback source is retained per sender. No duplicate
relay loop was found. Native voice mixing is delegated to Unity Linear rolloff;
synthetic near/mid/far and moving speaker/listener fixtures check source policy,
position updates and server-range changes, not actual native panning/volume.
Unity documents that [Linear reaches zero at maxDistance](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AudioSource-maxDistance.html)
and that [bypassReverbZones excludes global zone reverb](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AudioSource-bypassReverbZones.html).

PNG assets remain embedded and unchanged. Settings images are explicitly marked
**layout fixtures**, with approximate heading font; there is no native game UI
screenshot. No microphone was opened/recorded, permission granted, live client DLL
replaced, server settings/world changed or game restarted for this quality work.

## Exact remaining listening and UI acceptance

After separately approved installation/restarts in a disposable two-client test
profile/server, use the users' selected hardware and headphones for a short PTT
conversation. No recording is needed; microphone use/permission grants require
explicit approval. Match both companion and server versions.

1. Speak ordinary phrases and very short words for 10 seconds each, then pause and
   resume; reverse speaker/listener. Listen for missing endings, gaps, delayed
   words, clipping and karaoke tails. Repeat outdoors and indoors. Compare
   anonymous capture/send/relay/receive/output/underrun/rebuffer/callback counters.
2. With server range 40, compare approximately 2, 21 and 40 world units, including
   moving each participant independently. Expect gradual fade, then silence;
   change the policy range and verify existing streams respond. Native third-person
   listener position can differ from the player's exact position.
3. Open F8 at actual UI scales. Check regular body/title fonts, aligned slider
   thumb/fill at min/default/max, 44-unit hit rows, percentage/×/RMS readouts,
   fine keyboard/controller steps, binding cancellation, no capture while binding,
   F8/Escape/B close, movement/camera blocking and cursor/focus return. Verify
   quiet/muted HUD remains hidden and speech waves still track loudness.

Publication is separately authorized; it does not constitute listening acceptance
or permission for live server deployment/restarts.
