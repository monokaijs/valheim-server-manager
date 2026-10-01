using ValheimServerManager.ServerSupport;

namespace ValheimServerManager.Server;

// Only game/transport/profile boundaries are faked. ClientAdmission.cs is linked
// directly from the server project so these tests execute production gate methods.
public sealed partial class ServerPlugin
{
    private readonly FakeConfig<bool> _serverCharactersEnabled = new(true);
    private readonly FakeConfig<int> _clientGraceSeconds = new(20);
    private readonly Dictionary<long, bool> _companions = new();
    private readonly HashSet<long> _serverCharacterClients = new(), _inventoryEditClients = new(),
        _characterProfileSent = new(), _pendingCharacterProfiles = new(),
        _characterEnforcementHandled = new(), _inspectionEnforcementHandled = new();
    private readonly Dictionary<long, DateTime> _joined = new();
    private readonly Dictionary<ZNetPeer, DateTime> _peerConnectedAt = new();
    private readonly PendingClientHellos<ZNetPeer> _pendingPeerHellos = new();
    private readonly Dictionary<ZNetPeer, string> _modReceipts = new();
    private readonly Dictionary<long, ScheduledKick> _scheduledKicks = new();
    private readonly string _requiredModRevision = "fixture-revision";
    private readonly string _welcomeMessage = "", _companionRequiredMessage = "Client update required";
    private readonly bool _voiceEnabled = true;
    private readonly float _voiceRange = 40;
    private readonly DateTime _policyGraceUntil = DateTime.MinValue;
    private readonly FakeLogger Logger = new();
    internal int ProfileSends { get; private set; }

    public ServerPlugin() { ZNet.instance = new ZNet(); }
    internal ZNetPeer AddPeer(long uid)
    {
        var peer = new ZNetPeer { m_uid = uid, m_playerName = "synthetic player" };
        ZNet.instance.Peers.Add(peer);
        _peerConnectedAt[peer] = DateTime.UtcNow.AddSeconds(-21);
        if (uid != 0) SetReceipt(peer);
        return peer;
    }
    internal void Authenticate(ZNetPeer peer, long uid, bool receipt = true)
    {
        peer.m_uid = uid;
        if (receipt) SetReceipt(peer);
    }
    internal void SetReceipt(ZNetPeer peer) => _modReceipts[peer] = _requiredModRevision;
    internal void Hello(ZNetPeer peer, bool allowed, string version) => ClientHelloForPeer(ZNet.instance, peer, peer.m_rpc, allowed, version);
    internal bool Admit(ZNetPeer peer) => CompleteAuthenticatedPeerAdmission(peer, true);
    internal void Enforce() => EnforceServerCharacterClients();
    internal bool CharacterCapable(ZNetPeer peer) => _serverCharacterClients.Contains(peer.m_uid);
    internal bool JoinedPeer(ZNetPeer peer) => _joined.ContainsKey(peer.m_uid);
    internal bool InventoryAllowed(ZNetPeer peer) => _companions.TryGetValue(peer.m_uid, out var allowed) && allowed;
    internal bool HasKick(ZNetPeer peer) => _scheduledKicks.ContainsKey(peer.m_uid);
    internal ScheduledKick Kick(ZNetPeer peer) => _scheduledKicks[peer.m_uid];
    internal void SetKick(ZNetPeer peer, ScheduledKick kick) => _scheduledKicks[peer.m_uid] = kick;
    internal void SatisfyMods(ZNetPeer peer) => CancelSatisfiedRequirement(peer.m_uid, ClientRequirement.Mods);
    internal void ForgetPending(ZNetPeer peer) => _pendingPeerHellos.Remove(peer);
    private static ZNetPeer? PeerForRpc(ZNet znet, ZRpc rpc) => znet.Peers.FirstOrDefault(peer => ReferenceEquals(peer.m_rpc, rpc));
    private static void InvokePeer(ZNetPeer? peer, string name, params object[] args)
    {
        if (name == "VSM_ServerPolicy") peer?.OnPolicy?.Invoke();
    }
    private void SendServerCharacter(ZNetPeer? peer)
    {
        if (peer != null && peer.m_uid != 0 && _serverCharacterClients.Contains(peer.m_uid)
            && _characterProfileSent.Add(peer.m_uid)) ProfileSends++;
    }
    private void Event(string name, object payload, params string[] options) { }
    private string Render(string template, string player, string reason = "") => template;
    private static class PeerInfoPatch { internal static void ReleaseIfReady(ZNetPeer peer) { } }
}

internal sealed class FakeConfig<T>(T value) { internal T Value = value; }
internal sealed class FakeLogger
{
    internal void LogInfo(string message) { }
    internal void LogDebug(string message) { }
    internal void LogWarning(string message) { }
}
internal sealed class ZNet
{
    internal static ZNet instance = new();
    internal readonly List<ZNetPeer> Peers = new();
    internal List<ZNetPeer> GetPeers() => Peers;
}
internal sealed class ZNetPeer
{
    internal long m_uid;
    internal string m_playerName = "";
    internal readonly ZRpc m_rpc = new();
    internal readonly FakeSocket m_socket = new();
    internal Action? OnPolicy;
}
internal sealed class ZRpc { }
internal sealed class FakeSocket { internal string GetHostName() => "synthetic"; }
internal sealed class ZRoutedRpc
{
    internal static ZRoutedRpc? instance = null;
    internal void InvokeRoutedRPC(long peer, string name, params object[] args) { }
}
