# Client admission handshake validation

A 2.7.3 companion can be rejected with **Client update required** even though
its version supports the server-owned character protocol. An early
`VSM_ClientHello` can arrive before Valheim assigns `ZNetPeer.m_uid`. The server
defers it, then acknowledges policy and holds world admission before processing
that deferred hello. The client stops hello retries after policy receipt, and
the server eventually schedules a character compatibility kick.

The server now consumes deferred hellos immediately after authenticated
`RPC_PeerInfo` completion, before deciding whether to hold world traffic.
Pending entries are removed before invoking the handler, so repeated or reentrant
join events cannot process the same hello twice. Disconnect removes pending state
by connection object; a new peer cannot inherit an earlier connection's hello.

A compatible late hello also cancels its character compatibility requirement.
Inspection consent and mod receipts satisfy only their own requirements. Other
requirements and unrelated kicks remain scheduled. Character enforcement does
not overwrite an existing unrelated kick. Unsupported clients, failed character
profile loading and missing mod receipts still block admission.

`ServerAdmissionTests` links the production `ClientAdmission.cs` methods into the
test project with synthetic peer, transport and profile boundaries. It covers
early/late/repeated hellos, authenticated negative IDs, rejected versions,
missing mod receipts, consent changes, compound/unrelated kick preservation,
reentrant pending completion and disconnected/reconnected peers.

The public Thunderstore `Creaton-Server_Manager-2.7.3` ZIP was inspected: its
manifest and client `BepInPlugin` version are 2.7.3, its hello sends 2.7.3, and it
includes both `ValheimServerManager.Client.dll` and `Newtonsoft.Json.dll`. The
server's existing character protocol minimum remains 2.7.2. No client protocol
or package-version change is needed for this server fix.

Validation uses the full .NET 10 suite in the established disposable Docker test
image and plugin compilation against installed Mac Valheim Unity 6000.0.75f1
assemblies. This establishes code behavior with synthetic connections; it does
not establish a successful Windows game connection or functioning microphone,
native voice controls, hardware playback or multi-client audio.

Deployment requires building/installing the updated server plugin and restarting
the server through an approved release/deployment process. Existing 2.7.3 clients
can use this fix. No release, tag, client republish, production restart, microphone
permission change or live multiplayer validation is part of this source change.

## Authentication grace correction after 2.7.5

Read-only deployment inspection confirmed the running 2.7.5 image and the
BepInEx-loaded Server Manager 2.7.5 DLL. The deployed DLL matched the image build
output (SHA256 `f3b940e37d434bd354584ad12863ac6317b56f34097aa6b69bb8775eafc02e6c`),
and decompilation confirmed deferred-hello processing before admission and
requirement-specific cancellation. The public 2.7.5 client DLL advertises and
sends 2.7.5; the character protocol minimum remains 2.7.2.

A subsequent failed connection never reached native authenticated PeerInfo.
The server logged its socket connection at 08:09:05 on 2026-10-01, then the
missing-runtime warning for an unnamed peer at 08:09:25 and kick at 08:09:28.
The old requirement timer started at socket connection and could reject UID 0,
even if a valid capability hello was deferred awaiting native authentication.
Two production-method regressions reproduced this with and without an early hello.
This explains the premature rejection; it does not establish why native
PeerInfo did not finish. Client startup/failed-attempt logs are still required.
Server debug logging was disabled, so a missing deferred-hello debug line does
not prove the client sent no hello.

Character and required-mod grace now starts on the connection's first observed
nonzero authenticated UID. Native password/authentication waits do not create
VSM admission state or consume this grace. Repeated admission does not extend
the timer; disconnect removes the connection's clock. Requirement enforcement
also observes authenticated IDs so a missing admission hook cannot disable
its eventual checks. Deferred hellos survive the wait; unsupported clients and
missing mod receipts still hold world admission and are rejected after grace.
Native authentication and all existing server-owned-character protections remain
required. This correction requires an updated server plugin; the 2.7.5 client
protocol is compatible. No server restart, configuration edit, release or
live-session validation was performed for this correction.
