# Voice audio validation

The client now reads 640 sample frames across every microphone channel, then
averages the interleaved values into the existing 16 kHz mono codec. Microphone
positions and read offsets stay in sample frames, and cursor wrap uses the clip's
actual sample capacity, following [Unity’s GetData contract](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AudioClip.GetData.html). Backlogged capture resumes at the latest complete frame.
Non-finite channel values are treated as silence before applying microphone gain.
Invalid clip formats are stopped before reading rather than encoded incorrectly.

Capture failure is observable without opening an idle push-to-talk microphone.
A failed read or invalid capture position marks the microphone unavailable;
a successful read clears that state, including below-threshold voice activation.
Read failures lasting 0.5 seconds or a capture cursor with no progress for two
seconds stop and destroy the capture clip and wait three seconds before retrying.
Startup failures use the same cleanup/backoff. The failure indicator remains set
through a retry attempt until reading succeeds. Changing device or resetting a
connection clears the old failure/backoff. Inactive voice and released
push-to-talk stop capture and never start a retry in the background.

Server voice-policy changes update every existing playback source immediately;
new received frames also apply the current range. Finite ranges clamp to 5–100
world units. NaN/infinite range or volume values cannot reach audio properties;
existing valid settings are retained, and new sources use finite defaults.

`VoiceChatClientTests` links the production audio client and codec with synthetic
Unity/device boundaries. Coverage includes mono/stereo/multichannel capture,
non-aligned ring wrap, backlog recovery, encode/relay/decode/stream output,
transient and persistent failures, stalled startup/capture, retries, invalid
formats/positions/samples, reset/device cleanup, push-to-talk privacy, activation
hangover and repeated live range updates without additional incoming frames.
Tests use the full .NET 10 suite in a disposable container; plugin compatibility
is checked against installed Mac Valheim Unity 6000.0.75f1 assemblies.

These are client fixes. Users need an updated companion DLL/package after an
approved release; no server voice protocol change is required. The separate
connection fix in `1cea4a4` still requires an updated server plugin. Package
publication, production restarts, microphone permissions and real-game testing
remain separate approved actions.

No native microphone was opened or recorded by validation. Windows device
permissions/driver behavior, native voice UI/controller behavior, real transport
jitter/throughput and audible two-client proximity voice remain unverified.
Acceptance needs a disposable session with matching game/mod versions and two
authorized clients, using synthetic loopback audio or explicitly approved
microphone capture.
