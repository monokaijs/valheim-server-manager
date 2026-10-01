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
