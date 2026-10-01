using System;
using System.Linq;
using ValheimServerManager.ServerSupport;

namespace ValheimServerManager.Server;

// Admission is exercised with synthetic peers in the regression suite; game profile
// loading and RPC transport remain supplied by the real server runtime.
public sealed partial class ServerPlugin
{
    private bool BeginAuthenticatedAdmission(ZNetPeer peer)
    {
        if (peer == null || peer.m_uid == 0) return false;
        if (!_peerAuthenticatedAt.ContainsKey(peer))
        {
            _peerAuthenticatedAt[peer] = DateTime.UtcNow;
            Logger.LogInfo($"Authenticated VSM admission started for peer {peer.m_uid}.");
        }
        return true;
    }

    private bool AdmissionGraceExpired(ZNetPeer peer) => BeginAuthenticatedAdmission(peer)
        && DateTime.UtcNow - _peerAuthenticatedAt[peer] >= TimeSpan.FromSeconds(_clientGraceSeconds.Value);

    private void EnforceServerCharacterClients()
    {
        if (DateTime.UtcNow < _policyGraceUntil) return;
        foreach (var peer in ZNet.instance.GetPeers().Where(peer => AdmissionGraceExpired(peer) && !_serverCharacterClients.Contains(peer.m_uid) && !_characterEnforcementHandled.Contains(peer.m_uid) && !_scheduledKicks.ContainsKey(peer.m_uid)).ToArray())
        {
            _characterEnforcementHandled.Add(peer.m_uid);
            var reason = "A compatible Server Manager client runtime is required for server-owned characters.";
            Logger.LogWarning($"Kicking {peer.m_playerName}: {reason}");
            var message = Render(_companionRequiredMessage, peer.m_playerName, reason);
            InvokePeer(peer, "VSM_AdminNotice", "Client update required", message);
            Event("server-character.client.required", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), reason, message });
            _scheduledKicks[peer.m_uid] = new ScheduledKick { Due = DateTime.UtcNow.AddSeconds(3), ServerCharacterRequirement = true };
        }
    }

    internal void ClientHello(long sender, bool inventoryAllowed, string companionVersion)
    {
        var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
        if (peer == null || sender == 0) return;
        var firstHello = !_companions.TryGetValue(sender, out var previousInventory);
        _companions[sender] = inventoryAllowed;
        var characterCapable = System.Version.TryParse(companionVersion, out var version) && version >= new System.Version(2, 7, 2);
        var editCapable = version != null && version >= new System.Version(2, 5, 2);
        if (editCapable) _inventoryEditClients.Add(sender);
        else _inventoryEditClients.Remove(sender);
        var previouslyCapable = _serverCharacterClients.Contains(sender);
        if (characterCapable)
        {
            _serverCharacterClients.Add(sender);
            _characterEnforcementHandled.Remove(sender);
            CancelSatisfiedRequirement(sender, ClientRequirement.ServerCharacter);
        }
        else _serverCharacterClients.Remove(sender);
        InvokePeer(peer, "VSM_ServerPolicy", _serverCharactersEnabled.Value, _clientGraceSeconds.Value);
        InvokePeer(peer, "VSM_InspectionPolicy", true);
        InvokePeer(peer, "VSM_VoicePolicy", _voiceEnabled, _voiceRange);
        if (inventoryAllowed)
        {
            _inspectionEnforcementHandled.Remove(sender);
            CancelSatisfiedRequirement(sender, ClientRequirement.Inspection);
        }
        if (firstHello || previousInventory != inventoryAllowed || previouslyCapable != characterCapable)
        {
            Logger.LogInfo($"Client runtime handshake from peer {sender}: version={companionVersion}, inventory={inventoryAllowed}, serverCharacters={characterCapable}.");
            Event("companion.connected", new { peerId = sender, inventoryAllowed, companionVersion, serverCharacters = characterCapable }, "companion", "reported");
        }
        if (_serverCharactersEnabled.Value && characterCapable && !_characterProfileSent.Contains(sender))
        {
            if (string.IsNullOrWhiteSpace(peer.m_playerName)) _pendingCharacterProfiles.Add(sender);
            else SendServerCharacter(peer);
        }
        PeerInfoPatch.ReleaseIfReady(peer);
    }

    internal void Joined(ZNetPeer peer)
    {
        if (peer == null) return;
        CompletePendingClientHello(peer);
        if (string.IsNullOrEmpty(peer.m_playerName) || _joined.ContainsKey(peer.m_uid)) return;
        _joined[peer.m_uid] = DateTime.UtcNow;
        ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, "ShowMessage", 2, Render(_welcomeMessage, peer.m_playerName));
        Event("player.joined", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid });
    }

    private void ClientHelloForPeer(ZNet znet, ZNetPeer peer, ZRpc rpc, bool allowed, string version)
    {
        var peerId = (PeerForRpc(znet, rpc) ?? peer).m_uid;
        if (_pendingPeerHellos.DeferIfUnauthenticated(peer, peerId, allowed, version))
        {
            Logger.LogDebug("Deferring VSM capability handshake until Valheim assigns the authenticated peer ID.");
            return;
        }
        ClientHello(peerId, allowed, version);
    }

    private void CancelSatisfiedRequirement(long peerId, ClientRequirement requirement)
    {
        if (_scheduledKicks.TryGetValue(peerId, out var kick) && kick.Satisfy(requirement)) _scheduledKicks.Remove(peerId);
    }

    private void CompletePendingClientHello(ZNetPeer peer)
    {
        if (peer == null) return;
        _pendingPeerHellos.Complete(peer, peer.m_uid, (allowed, version) => ClientHello(peer.m_uid, allowed, version));
    }

    private bool CompleteAuthenticatedPeerAdmission(ZNetPeer peer, bool buffering)
    {
        // A skipped/rejected native PeerInfo (including another mod's delay) must
        // not create admission state or consume a socket's authentication wait.
        if (!BeginAuthenticatedAdmission(peer)) return false;
        // RPC_PeerInfo has assigned the authenticated ID. Consume an early hello
        // before testing the same capability that keeps world admission closed.
        CompletePendingClientHello(peer);
        if (buffering && _serverCharactersEnabled.Value)
        {
            InvokePeer(peer, "VSM_ServerPolicy", true, _clientGraceSeconds.Value);
            SendServerCharacter(peer);
        }
        var holdForCharacter = _serverCharactersEnabled.Value
            && (peer == null || !_serverCharacterClients.Contains(peer.m_uid) || !_characterProfileSent.Contains(peer.m_uid));
        var holdForMods = peer != null && !string.IsNullOrEmpty(_requiredModRevision)
            && (!_modReceipts.TryGetValue(peer, out var receipt) || receipt != _requiredModRevision);
        var holdForAdmission = buffering && (holdForCharacter || holdForMods);
        if (!holdForAdmission && peer != null) Joined(peer);
        return holdForAdmission;
    }
}
