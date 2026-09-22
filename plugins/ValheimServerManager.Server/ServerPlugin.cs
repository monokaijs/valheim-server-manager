using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimServerManager.Server;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ServerPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.creaton.valheim-server-manager";
    public const string PluginName = "Server Manager";
    public const string PluginVersion = "2.1.0";
    private const string LegacyPluginGuid = "dev.monokai.valheim-server-manager.server";
    private const string ClientManifestRpc = "ValheimServerManager_Manifest_v1";
    private const string LegacyClientManifestRpc = "ServerModBootstrap_Manifest_v1";
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly ConcurrentQueue<string> _outgoing = new();
    private readonly Dictionary<long, DateTime> _joined = new();
    private readonly Dictionary<long, bool> _companions = new();
    private readonly HashSet<long> _serverCharacterClients = new();
    private readonly HashSet<long> _characterEnforcementHandled = new();
    private readonly HashSet<long> _pendingCharacterProfiles = new();
    private readonly HashSet<long> _characterProfileSent = new();
    private readonly Dictionary<string, Tuple<long, DateTime>> _inventoryRequests = new();
    private readonly Dictionary<long, ScheduledKick> _scheduledKicks = new();
    private readonly Dictionary<ZNetPeer, Tuple<bool, string>> _pendingPeerHellos = new();
    private readonly Dictionary<ZDOID, bool> _dead = new();
    private CancellationTokenSource _lifetime;
    private float _nextSnapshot;
    private float _nextDeathPoll;
    private float _nextMaintenance;
    private string _saveRequestId;
    private bool _rpcsRegistered;
    private volatile string _clientModManifest;
    private volatile string _pluginRegistryMessage;
    private string _serverName = "Valheim Server";
    private string _welcomeMessage = "Welcome {player} to {server}.";
    private string _whitelistRejectedMessage = "You are not on the {server} whitelist. A join request was sent to the administrators.";
    private string _companionRequiredMessage = "{server} requires the Server Manager client runtime. Restart Valheim after Server Manager finishes installing it.";
    private bool _pluginRegistryPublished;
    private ConfigEntry<string> _managerUrl;
    private ConfigEntry<string> _agentToken;
    private ConfigEntry<bool> _serverCharactersEnabled;
    private ConfigEntry<bool> _acceptFirstJoinProfile;
    private ConfigEntry<bool> _rejectPreviouslyUsedCharacters;
    private ConfigEntry<int> _characterBackups;
    private ConfigEntry<int> _clientGraceSeconds;
    internal static ServerPlugin Instance { get; private set; }
    internal static int MaxPlayers { get; private set; } = 10;

    private void Awake()
    {
        if (!Application.isBatchMode)
        {
            Logger.LogInfo("Server agent disabled in the Valheim client process.");
            enabled = false;
            return;
        }
        Instance = this;
        MigrateLegacyConfig();
        _managerUrl = Config.Bind("Connection", "ManagerUrl", Environment.GetEnvironmentVariable("VSM_AGENT_URL") ?? "ws://127.0.0.1:8080/internal/agent", "Loopback manager WebSocket URL.");
        _agentToken = Config.Bind("Connection", "AgentToken", "", "Shared manager token; the VSM_AGENT_TOKEN environment variable takes precedence and is not persisted.");
        _serverCharactersEnabled = Config.Bind("ServerCharacters", "Enabled", false, "Make the server copy of each native Valheim character authoritative. When disabled, players without mods can join normally.");
        _acceptFirstJoinProfile = Config.Bind("ServerCharacters", "AcceptFirstJoinProfile", true, "Allow a character without a server save to seed its first server-owned profile. Disable after migration for a closed realm.");
        _rejectPreviouslyUsedCharacters = Config.Bind("ServerCharacters", "RejectPreviouslyUsedCharacters", false, "When accepting a first-join profile, require a character that has never entered another world or server.");
        _characterBackups = Config.Bind("ServerCharacters", "BackupsToKeep", 10, new ConfigDescription("Rolling native profile backups per character.", new AcceptableValueRange<int>(1, 50)));
        _clientGraceSeconds = Config.Bind("ServerCharacters", "ClientGraceSeconds", 20, new ConfigDescription("Seconds to wait for the client runtime handshake before rejecting a player while server-owned characters are enabled.", new AcceptableValueRange<int>(5, 120)));
        if (int.TryParse(Environment.GetEnvironmentVariable("VSM_MAX_PLAYERS"), out var configuredMaxPlayers))
            MaxPlayers = Math.Max(1, Math.Min(100, configuredMaxPlayers));
        _lifetime = new CancellationTokenSource();
        Patch(typeof(ClientModRelayPatch)); Patch(typeof(AdmissionPatch)); Patch(typeof(PeerInfoPatch)); Patch(typeof(CharacterIdPatch)); Patch(typeof(DisconnectPatch));
        Patch(typeof(WorldLoadPatch)); Patch(typeof(SaveStartPatch)); Patch(typeof(SaveCompletePatch)); Patch(typeof(GlobalKeyPatch));
        Patch(typeof(RaidPatch)); Patch(typeof(DayPatch)); Patch(typeof(SleepPatch)); Patch(typeof(BossDeathPatch)); Patch(typeof(ChatPatch));
        Patch(typeof(PlayerLimitPeerInfoPatch)); Patch(typeof(PlayerLimitCountPatch));
        Patch(typeof(PlayFabLobbyLimitPatch)); Patch(typeof(PlayFabNetworkLimitPatch));
        Patch(typeof(SteamServerLimitPatch)); Patch(typeof(SteamLobbyLimitPatch));
        Task.Run(() => ConnectionLoop(_lifetime.Token));
        Logger.LogInfo($"Server player limit set to {MaxPlayers}." + (MaxPlayers > 10 ? " Values above 10 are a modded, unsupported Valheim configuration." : ""));
        Logger.LogInfo("Server agent loaded; waiting for dedicated-server networking.");
    }

    private void MigrateLegacyConfig()
    {
        try
        {
            var legacy = Path.Combine(Paths.ConfigPath, LegacyPluginGuid + ".cfg");
            var current = Config.ConfigFilePath;
            if (File.Exists(legacy) && !File.Exists(current)) File.Copy(legacy, current, false);
            if (File.Exists(current)) Config.Reload();
        }
        catch (Exception exception) { Logger.LogWarning($"Could not migrate legacy plugin configuration: {exception.Message}"); }
    }

    private void OnDestroy()
    {
        _lifetime?.Cancel();
        Harmony.UnpatchID(PluginGuid);
    }

    private void Update()
    {
        for (var index = 0; index < 32 && _mainThread.TryDequeue(out var action); index++)
            try { action(); } catch (Exception ex) { Logger.LogError(ex); }
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        TryRegisterClientRpcs();
        if (!_pluginRegistryPublished && Time.unscaledTime > 2f) PublishPluginRegistry();
        if (Time.unscaledTime >= _nextSnapshot) { _nextSnapshot = Time.unscaledTime + 5f; SendSnapshot(); }
        if (Time.unscaledTime >= _nextDeathPoll) { _nextDeathPoll = Time.unscaledTime + 1f; PollDeaths(); }
        if (Time.unscaledTime < _nextMaintenance) return;
        _nextMaintenance = Time.unscaledTime + .25f;
        foreach (var expired in _inventoryRequests.Where(item => DateTime.UtcNow - item.Value.Item2 > TimeSpan.FromSeconds(30)).Select(item => item.Key).ToArray()) _inventoryRequests.Remove(expired);
        foreach (var scheduled in _scheduledKicks.Where(item => DateTime.UtcNow >= item.Value.Due).ToArray())
        {
            _scheduledKicks.Remove(scheduled.Key);
            var peer = ZNet.instance.GetPeers().FirstOrDefault(item => item.m_uid == scheduled.Key);
            if (peer != null) DisconnectPeer(peer);
        }
        if (!_serverCharactersEnabled.Value) _pendingCharacterProfiles.Clear();
        foreach (var peerId in _serverCharactersEnabled.Value ? _pendingCharacterProfiles.ToArray() : Array.Empty<long>())
        {
            var peer = ZNet.instance.GetPeers().FirstOrDefault(item => item.m_uid == peerId);
            if (peer == null || string.IsNullOrWhiteSpace(peer.m_playerName)) continue;
            _pendingCharacterProfiles.Remove(peerId);
            SendServerCharacter(peerId);
        }
        if (_serverCharactersEnabled.Value) EnforceServerCharacterClients();
    }

    private void Patch(Type patch)
    {
        try { Harmony.CreateAndPatchAll(patch, PluginGuid); }
        catch (Exception ex) { Logger.LogWarning($"Compatibility warning: {patch.Name} failed: {ex.Message}"); Event("compatibility.warning", new { patch = patch.Name, error = ex.Message }); }
    }

    private async Task ConnectionLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("X-VSM-Agent-Token", Environment.GetEnvironmentVariable("VSM_AGENT_TOKEN") ?? _agentToken.Value);
                await socket.ConnectAsync(new Uri(_managerUrl.Value), token);
                Enqueue(new { type = "hello", payload = new { version = PluginVersion, gameVersion = global::Version.GetVersionString(), protocolVersion = 1 } });
                if (!string.IsNullOrWhiteSpace(_pluginRegistryMessage)) _outgoing.Enqueue(_pluginRegistryMessage);
                var receive = ReceiveLoop(socket, token);
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    if (_outgoing.TryDequeue(out var message))
                    {
                        var bytes = Encoding.UTF8.GetBytes(message);
                        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                    }
                    else await Task.Delay(50, token);
                }
                await receive;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.LogWarning($"Manager connection: {ex.Message}"); }
            if (!token.IsCancellationRequested) await Task.Delay(5000, token);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var segment = new ArraySegment<byte>(buffer);
            var result = await socket.ReceiveAsync(segment, token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            while (!result.EndOfMessage)
            {
                result = await socket.ReceiveAsync(segment, token);
                text += Encoding.UTF8.GetString(buffer, 0, result.Count);
            }
            JObject message;
            try { message = JObject.Parse(text); } catch { continue; }
            if ((string)message["type"] == "clientModManifest")
            {
                var manifest = (string)message["payload"]?["json"] ?? "";
                if (manifest.Length > 0 && Encoding.UTF8.GetByteCount(manifest) <= 8 * 1024 * 1024)
                {
                    _clientModManifest = manifest;
                    Logger.LogInfo($"Loaded client mod manifest ({manifest.Length} characters).");
                }
                else Logger.LogWarning("Rejected an empty or oversized client mod manifest from the manager.");
                continue;
            }
            if ((string)message["type"] == "serverMessages")
            {
                _mainThread.Enqueue(() => ApplyServerMessages(message["payload"] as JObject));
                continue;
            }
            if ((string)message["type"] == "serverCharacterSettings")
            {
                _mainThread.Enqueue(() => ApplyServerCharacterSettings(message["payload"] as JObject));
                continue;
            }
            if ((string)message["type"] != "command") continue;
            var requestId = (string)message["requestId"];
            var payload = message["payload"] as JObject;
            _mainThread.Enqueue(() => Execute(requestId, (string)payload?["name"], payload?["data"] as JObject ?? new JObject()));
        }
    }

    private void Execute(string requestId, string name, JObject data)
    {
        try
        {
            object result;
            switch (name)
            {
                case "world.save":
                    if (ZNet.instance.IsSaving() || _saveRequestId != null) throw new InvalidOperationException("A save is already in progress.");
                    _saveRequestId = requestId;
                    ZNet.instance.Save(false, false, true);
                    return;
                case "broadcast":
                    var message = ((string)data["message"] ?? "").Trim();
                    foreach (var peer in ZNet.instance.GetPeers())
                    {
                        if (_companions.ContainsKey(peer.m_uid)) InvokePeer(peer, "VSM_AdminNotice", "Server notice", message);
                        else ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", 2, message);
                    }
                    Event("admin.broadcast", new { message }); result = new { ok = true }; break;
                case "player.kick": result = Kick(data); break;
                case "player.ban": result = Ban(data); break;
                case "access.permitted.add": result = Access("m_permittedList", data, true, "whitelist.added"); break;
                case "access.permitted.remove": result = Access("m_permittedList", data, false, "whitelist.removed"); break;
                case "access.banned.add": result = Access("m_bannedList", data, true, "player.banned"); break;
                case "access.banned.remove": result = Access("m_bannedList", data, false, "player.unbanned"); break;
                case "access.admin.add": result = Access("m_adminList", data, true, "admin.added"); break;
                case "access.admin.remove": result = Access("m_adminList", data, false, "admin.removed"); break;
                case "inventory.request":
                    var peerId = (long?)data["peerId"] ?? 0;
                    if (!_companions.TryGetValue(peerId, out var inventoryAllowed)) throw new InvalidOperationException("The player's Server Manager runtime is not connected.");
                    if (!inventoryAllowed) throw new InvalidOperationException("The player has not enabled inventory inspection.");
                    _inventoryRequests[requestId] = Tuple.Create(peerId, DateTime.UtcNow);
                    InvokePeer(ZNet.instance.GetPeers().FirstOrDefault(item => item.m_uid == peerId), "VSM_InventoryRequest", requestId);
                    return;
                default: throw new InvalidOperationException("Unsupported command: " + name);
            }
            Reply(requestId, result);
        }
        catch (Exception ex) { if (_saveRequestId == requestId) _saveRequestId = null; Reply(requestId, new { ok = false, error = ex.Message }); }
    }

    private object Kick(JObject data)
    {
        var peer = FindPeer(data);
        if (peer == null) throw new KeyNotFoundException("Player was not found.");
        var reason = SafeText((string)data["reason"], 300, "No reason provided.");
        var message = SafeText((string)data["message"], 500, Render("You were kicked from {server}. Reason: {reason}", peer.m_playerName, reason));
        ScheduleKick(peer, "Kicked", message, reason, "player.kicked");
        return new { ok = true, player = peer.m_playerName, reason, scheduled = true };
    }

    private object Ban(JObject data)
    {
        var peer = FindPeer(data);
        if (peer == null) throw new KeyNotFoundException("Player was not found.");
        var id = peer.m_socket?.GetHostName() ?? "";
        Access("m_bannedList", JObject.FromObject(new { platformId = id }), true, null);
        var reason = SafeText((string)data["reason"], 300, "No reason provided.");
        var message = SafeText((string)data["message"], 500, Render("You were banned from {server}. Reason: {reason}", peer.m_playerName, reason));
        ScheduleKick(peer, "Banned", message, reason, "player.banned");
        return new { ok = true, player = peer.m_playerName, platformId = id, reason, scheduled = true };
    }

    private ZNetPeer FindPeer(JObject data)
    {
        var peerId = (long?)data["peerId"] ?? 0;
        var target = ((string)data["target"] ?? "").Trim();
        return ZNet.instance.GetPeers().FirstOrDefault(p => p.m_uid == peerId || p.m_playerName.Equals(target, StringComparison.OrdinalIgnoreCase) || (p.m_socket?.GetHostName() ?? "") == target);
    }

    private object Access(string fieldName, JObject data, bool add, string eventName)
    {
        var id = ((string)data["platformId"] ?? "").Trim();
        if (id.Length == 0 || id.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid platform ID.");
        var field = AccessTools.Field(typeof(ZNet), fieldName);
        var synced = field?.GetValue(ZNet.instance);
        if (synced == null) throw new MissingFieldException(fieldName);
        var contains = AccessTools.Method(synced.GetType(), "Contains");
        var method = AccessTools.Method(synced.GetType(), add ? "Add" : "Remove");
        if (add && (bool)contains.Invoke(synced, new object[] { id })) return new { ok = true, unchanged = true };
        method.Invoke(synced, new object[] { id }); if (!string.IsNullOrWhiteSpace(eventName)) Event(eventName, new { platformId = id }); return new { ok = true };
    }

    private void ApplyServerMessages(JObject payload)
    {
        if (payload == null) return;
        _serverName = SafeText((string)payload["serverName"], 120, _serverName);
        var templates = payload["templates"] as JObject;
        if (templates == null) return;
        _welcomeMessage = SafeText((string)templates["welcome"], 500, _welcomeMessage);
        _whitelistRejectedMessage = SafeText((string)templates["whitelistRejected"], 500, _whitelistRejectedMessage);
        _companionRequiredMessage = SafeText((string)templates["companionRequired"], 500, _companionRequiredMessage);
        Logger.LogInfo("Loaded customizable server messages from the manager.");
    }

    private void ApplyServerCharacterSettings(JObject payload)
    {
        if (payload == null) return;
        if (payload["enabled"]?.Type == JTokenType.Boolean)
            _serverCharactersEnabled.Value = (bool)payload["enabled"];
        if (payload["acceptFirstJoinProfile"]?.Type == JTokenType.Boolean)
            _acceptFirstJoinProfile.Value = (bool)payload["acceptFirstJoinProfile"];
        if (payload["rejectPreviouslyUsedCharacters"]?.Type == JTokenType.Boolean)
            _rejectPreviouslyUsedCharacters.Value = (bool)payload["rejectPreviouslyUsedCharacters"];
        if (payload["backupsToKeep"]?.Type == JTokenType.Integer)
            _characterBackups.Value = Math.Max(1, Math.Min(50, (int)payload["backupsToKeep"]));
        if (payload["clientGraceSeconds"]?.Type == JTokenType.Integer)
            _clientGraceSeconds.Value = Math.Max(5, Math.Min(120, (int)payload["clientGraceSeconds"]));
        if (!_serverCharactersEnabled.Value)
        {
            _pendingCharacterProfiles.Clear();
            _characterEnforcementHandled.Clear();
            foreach (var scheduled in _scheduledKicks.Where(item => item.Value.ServerCharacterRequirement).Select(item => item.Key).ToArray())
                _scheduledKicks.Remove(scheduled);
        }
        Config.Save();
        Logger.LogInfo($"Applied server-character policy: enabled={_serverCharactersEnabled.Value}, acceptFirstJoin={_acceptFirstJoinProfile.Value}, rejectPreviouslyUsed={_rejectPreviouslyUsedCharacters.Value}, backups={_characterBackups.Value}, clientGrace={_clientGraceSeconds.Value}s.");
    }

    private void ScheduleKick(ZNetPeer peer, string title, string message, string reason, string eventType)
    {
        InvokePeer(peer, "VSM_AdminNotice", title, message);
        if (!_companions.ContainsKey(peer.m_uid)) ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, "ShowMessage", 2, message);
        _scheduledKicks[peer.m_uid] = new ScheduledKick { Due = DateTime.UtcNow.AddSeconds(3) };
        Event(eventType, new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), reason, message });
    }

    private static void DisconnectPeer(ZNetPeer peer)
    {
        var method = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) }) ?? AccessTools.Method(typeof(ZNet), "Kick", new[] { typeof(ZNetPeer) });
        if (method != null) method.Invoke(ZNet.instance, new object[] { peer }); else peer.m_rpc?.GetSocket()?.Close();
    }

    private static void InvokePeer(ZNetPeer peer, string methodName, params object[] arguments)
    {
        if (peer?.m_rpc == null) return;
        try
        {
            peer.m_rpc.Invoke(methodName, arguments);
        }
        catch (Exception exception) { Instance.Logger.LogDebug($"Could not send client notice: {exception.GetBaseException().Message}"); }
    }

    private string Render(string template, string player = "", string reason = "", int seconds = 0) => template
        .Replace("{server}", _serverName).Replace("{player}", player ?? "").Replace("{reason}", reason ?? "").Replace("{seconds}", seconds.ToString());

    private static string SafeText(string value, int maximum, string fallback)
    {
        value = (value ?? "").Trim();
        return value.Length is > 0 && value.Length <= maximum && !value.Any(character => char.IsControl(character) && character != '\n') ? value : fallback;
    }

    private sealed class ScheduledKick { public DateTime Due; public bool ServerCharacterRequirement; }

    private void SendSnapshot()
    {
        var players = ZNet.instance.GetPeers().Where(p => p != null && p.IsReady()).Select(p => new
        {
            peerId = p.m_uid,
            name = p.m_playerName ?? "?",
            platformId = p.m_socket?.GetHostName() ?? "",
            connectedAt = (_joined.TryGetValue(p.m_uid, out var joined) ? joined : DateTime.UtcNow).ToString("O"),
            ping = p.m_rpc != null ? (int?)(p.m_rpc.GetTimeSinceLastPing() * 1000f) : null,
            companion = _companions.ContainsKey(p.m_uid),
            inventoryAllowed = _companions.TryGetValue(p.m_uid, out var allowed) && allowed,
            serverCharacter = _serverCharacterClients.Contains(p.m_uid)
        }).ToArray();
        Enqueue(new { type = "snapshot", payload = new { players } });
    }

    private void PublishPluginRegistry()
    {
        try
        {
            var plugins = Chainloader.PluginInfos.Values.Select(info => new
            {
                guid = info.Metadata.GUID,
                name = info.Metadata.Name,
                version = info.Metadata.Version == null ? "" : info.Metadata.Version.ToString(),
                dll = RelativePath(Paths.BepInExRootPath, info.Location),
                configFile = RelativePath(Paths.ConfigPath, info.Instance?.Config?.ConfigFilePath ?? Path.Combine(Paths.ConfigPath, info.Metadata.GUID + ".cfg"))
            }).ToArray();
            _pluginRegistryMessage = JsonConvert.SerializeObject(new { type = "pluginRegistry", payload = new { plugins } }, Formatting.None);
            _outgoing.Enqueue(_pluginRegistryMessage);
            _pluginRegistryPublished = true;
        }
        catch (Exception exception)
        {
            _pluginRegistryPublished = true;
            Logger.LogWarning($"Could not publish the BepInEx plugin registry: {exception.GetBaseException().Message}");
        }
    }

    private static string RelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')
            : "";
    }

    private void PollDeaths()
    {
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (peer.m_characterID.IsNone()) continue;
            var zdo = ZDOMan.instance.GetZDO(peer.m_characterID); if (zdo == null) continue;
            var dead = zdo.GetBool("dead".GetStableHashCode());
            _dead.TryGetValue(peer.m_characterID, out var wasDead);
            if (dead && !wasDead) Event("player.died", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), position = zdo.GetPosition() });
            _dead[peer.m_characterID] = dead;
        }
    }

    private void EnforceServerCharacterClients()
    {
        foreach (var peer in ZNet.instance.GetPeers().Where(peer => peer != null && _joined.TryGetValue(peer.m_uid, out var joined) && DateTime.UtcNow - joined > TimeSpan.FromSeconds(_clientGraceSeconds.Value) && !_serverCharacterClients.Contains(peer.m_uid) && !_characterEnforcementHandled.Contains(peer.m_uid)).ToArray())
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

    private void SendServerCharacter(long sender)
    {
        var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
        SendServerCharacter(peer);
    }

    private void SendServerCharacter(ZNetPeer peer)
    {
        if (peer == null || peer.m_uid == 0 || !_characterProfileSent.Add(peer.m_uid)) return;
        _pendingCharacterProfiles.Remove(peer.m_uid);
        try
        {
            var path = CharacterPath(peer);
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                ValidateNativeProfile(bytes);
                InvokePeer(peer, "VSM_CharacterProfile", true, Convert.ToBase64String(bytes), _rejectPreviouslyUsedCharacters.Value);
                Event("server-character.loaded", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), bytes = bytes.Length });
            }
            else if (_acceptFirstJoinProfile.Value)
            {
                InvokePeer(peer, "VSM_CharacterProfile", false, "", _rejectPreviouslyUsedCharacters.Value);
                Event("server-character.first-join", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName() });
            }
            else
            {
                Logger.LogWarning($"Rejecting {peer.m_playerName}: no imported server character exists.");
                ScheduleKick(peer, "Server character required",
                    "No server character exists for you yet. Ask an administrator to import your character save, then reconnect.",
                    "No imported server character exists.", "server-character.load.failed");
            }
        }
        catch (Exception exception)
        {
            Logger.LogError($"Unable to load server character for {peer.m_playerName}: {exception}");
            Event("server-character.load.failed", new { player = peer.m_playerName, error = exception.Message });
            ScheduleKick(peer, "Server character unavailable",
                "Your server character could not be loaded. Ask an administrator to check or restore your save, then reconnect.",
                "The server character could not be loaded.", "server-character.load.failed");
        }
    }

    private string CharacterPath(ZNetPeer peer)
    {
        var platformId = peer.m_socket?.GetHostName() ?? "";
        var character = peer.m_playerName ?? "";
        if (Regex.IsMatch(platformId, "^[0-9]{17}$")) platformId = "Steam_" + platformId;
        if (!Regex.IsMatch(platformId, "^[A-Za-z]+_[A-Za-z0-9]+$") || character.Length == 0 || character.Length > 64 || character.Contains("_") || character.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Player identity cannot be represented as a safe server-character filename.");
        var root = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);
        Directory.CreateDirectory(root);
        var expected = platformId + "_" + character + ".fch";
        return Directory.EnumerateFiles(root, "*.fch", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(root, expected);
    }

    private void BackupCharacter(string path)
    {
        if (!File.Exists(path)) return;
        var root = Path.Combine(Path.GetDirectoryName(path), "vsm-character-backups");
        Directory.CreateDirectory(root);
        var stem = Path.GetFileNameWithoutExtension(path);
        var backup = Path.Combine(root, stem + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".fch");
        File.Copy(path, backup, false);
        foreach (var expired in Directory.EnumerateFiles(root, stem + ".*.fch").OrderByDescending(File.GetLastWriteTimeUtc).Skip(_characterBackups.Value).ToArray()) File.Delete(expired);
    }

    private static void ValidateNativeProfile(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes, false))
        using (var reader = new BinaryReader(stream))
        {
            var payloadLength = reader.ReadInt32();
            if (payloadLength < 16 || payloadLength > bytes.Length - 8) throw new InvalidDataException("Invalid native character payload length.");
            var payload = reader.ReadBytes(payloadLength);
            var hashLength = reader.ReadInt32();
            if (hashLength != 64 || stream.Length - stream.Position != hashLength) throw new InvalidDataException("Invalid native character signature length.");
            var expected = reader.ReadBytes(hashLength);
            var actual = SHA512.Create().ComputeHash(payload);
            if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Native character signature is invalid.");
        }
    }

    internal void RequestCharacterCheckpoints()
    {
        if (!_serverCharactersEnabled.Value || ZNet.instance == null) return;
        foreach (var peerId in _serverCharacterClients.ToArray())
            InvokePeer(ZNet.instance.GetPeers().FirstOrDefault(item => item.m_uid == peerId), "VSM_CharacterCheckpoint");
    }

    internal void ClientHello(long sender, bool inventoryAllowed, string companionVersion)
    {
        var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
        if (peer == null || sender == 0) return;
        var firstHello = !_companions.TryGetValue(sender, out var previousInventory);
        _companions[sender] = inventoryAllowed;
        var characterCapable = System.Version.TryParse(companionVersion, out var version) && version >= new System.Version(1, 2, 0);
        var previouslyCapable = _serverCharacterClients.Contains(sender);
        if (characterCapable) _serverCharacterClients.Add(sender);
        else _serverCharacterClients.Remove(sender);
        InvokePeer(peer, "VSM_ServerPolicy", _serverCharactersEnabled.Value, _clientGraceSeconds.Value);
        if (firstHello || previousInventory != inventoryAllowed || previouslyCapable != characterCapable)
        {
            Logger.LogInfo($"Client runtime handshake from peer {sender}: version={companionVersion}, inventory={inventoryAllowed}, serverCharacters={characterCapable}.");
            Event("companion.connected", new { peerId = sender, inventoryAllowed, companionVersion, serverCharacters = characterCapable }, "companion", "reported");
        }
        if (_serverCharactersEnabled.Value && characterCapable && !_characterProfileSent.Contains(sender)) _pendingCharacterProfiles.Add(sender);
    }

    internal void CharacterUpload(long sender, string encoded)
    {
        if (!_serverCharactersEnabled.Value || !_serverCharacterClients.Contains(sender)) return;
        var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
        if (peer == null) return;
        try
        {
            if (encoded == null || encoded.Length > 2796204) throw new InvalidDataException("Native character profile size is invalid.");
            var bytes = Convert.FromBase64String(encoded ?? "");
            if (bytes.Length < 32 || bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Native character profile size is invalid.");
            ValidateNativeProfile(bytes);
            var destination = CharacterPath(peer);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            BackupCharacter(destination);
            var temporary = destination + ".vsm-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            var hash = BitConverter.ToString(SHA256.Create().ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            Event("server-character.saved", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), bytes = bytes.Length, sha256 = hash });
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Rejected server character upload from {peer.m_playerName}: {exception.Message}");
            Event("server-character.save.failed", new { player = peer.m_playerName, error = exception.Message });
        }
    }
    internal void InventoryResponse(long sender, string requestId, string json)
    {
        if (!_inventoryRequests.TryGetValue(requestId, out var pending) || pending.Item1 != sender) return;
        _inventoryRequests.Remove(requestId);
        Enqueue(new { type = "inventoryResponse", requestId, payload = JObject.Parse(json) });
    }
    internal void CompanionEvent(long sender, string json)
    {
        try { var data = JObject.Parse(json); Event((string)data["eventType"], data["data"], "companion", "reported"); } catch { }
    }
    internal void SaveFinished(bool ok, string error = null)
    {
        if (_saveRequestId == null) return;
        Reply(_saveRequestId, ok ? (object)new { ok = true, message = "World save completed." } : new { ok = false, error = error ?? "World save failed." });
        _saveRequestId = null;
    }
    internal void Joined(ZNetPeer peer) { if (peer == null) return; CompletePendingClientHello(peer); if (string.IsNullOrEmpty(peer.m_playerName) || _joined.ContainsKey(peer.m_uid)) return; _joined[peer.m_uid] = DateTime.UtcNow; InvokePeer(peer, "VSM_AdminNotice", "Welcome", Render(_welcomeMessage, peer.m_playerName)); Event("player.joined", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid }); }
    internal void AdmissionChecked(ZNet znet, string hostName, string playerName, bool allowed)
    {
        if (allowed || znet == null || !znet.IsServer() || string.IsNullOrWhiteSpace(hostName)) return;
        try
        {
            var permitted = AccessTools.Field(typeof(ZNet), "m_permittedList")?.GetValue(znet);
            var banned = AccessTools.Field(typeof(ZNet), "m_bannedList")?.GetValue(znet);
            if (permitted == null || banned == null) return;
            var count = AccessTools.Method(permitted.GetType(), "Count");
            var contains = AccessTools.Method(banned.GetType(), "Contains");
            var listContainsId = AccessTools.Method(typeof(ZNet), "ListContainsId");
            if (count == null || contains == null || listContainsId == null || (int)count.Invoke(permitted, null) == 0) return;
            var bannedById = (bool)listContainsId.Invoke(znet, new[] { banned, hostName });
            var bannedByName = !string.IsNullOrWhiteSpace(playerName) && (bool)contains.Invoke(banned, new object[] { playerName });
            var permittedById = (bool)listContainsId.Invoke(znet, new[] { permitted, hostName });
            if (!bannedById && !bannedByName && !permittedById)
            {
                var peer = znet.GetPeers().FirstOrDefault(item => item?.m_socket?.GetHostName() == hostName);
                InvokePeer(peer, "VSM_AdminNotice", "Whitelist approval required", Render(_whitelistRejectedMessage, playerName));
                Event("access.join-request", new { player = playerName ?? "", platformId = hostName, message = Render(_whitelistRejectedMessage, playerName) });
            }
        }
        catch (Exception exception) { Logger.LogWarning($"Could not capture rejected join request: {exception.GetBaseException().Message}"); }
    }
    private static ZNetPeer PeerForRpc(ZNet znet, ZRpc rpc) => znet.GetPeers().FirstOrDefault(peer => peer != null && peer.m_rpc == rpc);
    internal void RegisterPeerProtocol(ZNet znet, ZNetPeer peer)
    {
        if (znet == null || peer?.m_rpc == null) return;
        peer.m_rpc.Register<bool, string>("VSM_ClientHello", (rpc, allowed, version) => ClientHelloForPeer(znet, peer, rpc, allowed, version));
        peer.m_rpc.Register<string, string>("VSM_InventoryResponse", (rpc, request, json) =>
        {
            var peerId = (PeerForRpc(znet, rpc) ?? peer).m_uid;
            if (peerId != 0) InventoryResponse(peerId, request, json);
        });
        peer.m_rpc.Register<string>("VSM_CompanionEvent", (rpc, json) =>
        {
            var peerId = (PeerForRpc(znet, rpc) ?? peer).m_uid;
            if (peerId != 0) CompanionEvent(peerId, json);
        });
        peer.m_rpc.Register<string>("VSM_CharacterUpload", (rpc, encoded) =>
        {
            var peerId = (PeerForRpc(znet, rpc) ?? peer).m_uid;
            if (peerId != 0) CharacterUpload(peerId, encoded);
        });
    }
    private void ClientHelloForPeer(ZNet znet, ZNetPeer peer, ZRpc rpc, bool allowed, string version)
    {
        var peerId = (PeerForRpc(znet, rpc) ?? peer).m_uid;
        if (peerId == 0)
        {
            _pendingPeerHellos[peer] = Tuple.Create(allowed, version ?? "");
            Logger.LogDebug("Deferring VSM capability handshake until Valheim assigns the authenticated peer ID.");
            return;
        }
        ClientHello(peerId, allowed, version);
    }
    private void CompletePendingClientHello(ZNetPeer peer)
    {
        if (peer == null || peer.m_uid == 0 || !_pendingPeerHellos.TryGetValue(peer, out var hello)) return;
        _pendingPeerHellos.Remove(peer);
        ClientHello(peer.m_uid, hello.Item1, hello.Item2);
    }
    internal void Spawned(ZNetPeer peer) { if (peer != null) Event("player.spawned", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid }); }
    internal void Left(ZNetPeer peer)
    {
        if (peer == null) return;
        _pendingPeerHellos.Remove(peer);
        var joined = _joined.Remove(peer.m_uid);
        _companions.Remove(peer.m_uid);
        _serverCharacterClients.Remove(peer.m_uid);
        _characterEnforcementHandled.Remove(peer.m_uid);
        _pendingCharacterProfiles.Remove(peer.m_uid);
        _characterProfileSent.Remove(peer.m_uid);
        _scheduledKicks.Remove(peer.m_uid);
        _dead.Remove(peer.m_characterID);
        foreach (var request in _inventoryRequests.Where(item => item.Value.Item1 == peer.m_uid).Select(item => item.Key).ToArray())
        {
            _inventoryRequests.Remove(request);
            Reply(request, new { ok = false, error = "The player disconnected before the snapshot was ready." });
        }
        if (joined) Event("player.left", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid });
    }
    internal void RelayClientManifest(ZNetPeer peer)
    {
        var manifest = _clientModManifest;
        if (peer?.m_rpc == null || string.IsNullOrWhiteSpace(manifest)) return;
        try
        {
            peer.m_rpc.Invoke(ClientManifestRpc, manifest);
            peer.m_rpc.Invoke(LegacyClientManifestRpc, manifest);
            Logger.LogInfo($"Relayed the client mod manifest to peer {peer.m_uid}.");
        }
        catch (Exception exception) { Logger.LogWarning($"Could not relay the client mod manifest: {exception.GetBaseException().Message}"); }
    }
    internal void Event(string eventType, object data, string source = "server", string confidence = "authoritative") => Enqueue(new { type = "event", payload = new { eventType, source, confidence, data } });
    private void Reply(string requestId, object payload) => Enqueue(new { type = "commandResult", requestId, payload });
    private void Enqueue(object message) => _outgoing.Enqueue(JsonConvert.SerializeObject(message, Formatting.None));
    private void TryRegisterClientRpcs()
    {
        if (_rpcsRegistered || ZRoutedRpc.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        ZRoutedRpc.instance.Register<bool, string>("VSM_ClientHello", (sender, allowed, version) => ClientHello(sender, allowed, version));
        ZRoutedRpc.instance.Register<string, string>("VSM_InventoryResponse", (sender, request, json) => InventoryResponse(sender, request, json));
        ZRoutedRpc.instance.Register<string>("VSM_CompanionEvent", (sender, json) => CompanionEvent(sender, json));
        ZRoutedRpc.instance.Register<string>("VSM_CharacterUpload", (sender, encoded) => CharacterUpload(sender, encoded));
        _rpcsRegistered = true;
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    private static class ClientModRelayPatch { private static void Postfix(ZNet __instance, ZNetPeer __0) { if (__instance.IsServer()) { Instance.RegisterPeerProtocol(__instance, __0); Instance.RelayClientManifest(__0); } } }
    [HarmonyPatch(typeof(ZNet), "IsAllowed")]
    private static class AdmissionPatch { private static void Postfix(ZNet __instance, string __0, string __1, bool __result) => Instance.AdmissionChecked(__instance, __0, __1, __result); }
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    private static class PeerInfoPatch
    {
        private sealed class BufferedSocket : ISocket
        {
            internal readonly ISocket Original;
            private readonly List<ZPackage> _packets = new();
            private int _versionMatchIndex = -1;
            private bool _released;

            internal BufferedSocket(ISocket original) { Original = original; }
            public bool IsConnected() => Original.IsConnected();
            public ZPackage Recv() => Original.Recv();
            public int GetSendQueueSize() => Original.GetSendQueueSize();
            public int GetCurrentSendRate() => Original.GetCurrentSendRate();
            public bool IsHost() => Original.IsHost();
            public void Dispose() => Original.Dispose();
            public bool GotNewData() => Original.GotNewData();
            public void Close() => Original.Close();
            public string GetEndPointString() => Original.GetEndPointString();
            public void GetAndResetStats(out int totalSent, out int totalRecv) => Original.GetAndResetStats(out totalSent, out totalRecv);
            public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec) => Original.GetConnectionQuality(out localQuality, out remoteQuality, out ping, out outByteSec, out inByteSec);
            public ISocket Accept() => Original.Accept();
            public int GetHostPort() => Original.GetHostPort();
            public bool Flush() => Original.Flush();
            public string GetHostName() => Original.GetHostName();

            public void VersionMatch()
            {
                if (_released) Original.VersionMatch();
                else _versionMatchIndex = _packets.Count;
            }

            public void Send(ZPackage package)
            {
                var position = package.GetPos();
                package.SetPos(0);
                var methodHash = package.ReadInt();
                var delayWorldLoad = methodHash == "PeerInfo".GetStableHashCode()
                    || methodHash == "RoutedRPC".GetStableHashCode()
                    || methodHash == "ZDOData".GetStableHashCode();
                package.SetPos(position);
                if (!_released && delayWorldLoad)
                {
                    var copy = new ZPackage(package.GetArray());
                    copy.SetPos(position);
                    _packets.Add(copy);
                    return;
                }
                Original.Send(package);
            }

            internal void Release()
            {
                if (_released) return;
                _released = true;
                for (var index = 0; index < _packets.Count; index++)
                {
                    if (index == _versionMatchIndex) Original.VersionMatch();
                    Original.Send(_packets[index]);
                }
                if (_packets.Count == _versionMatchIndex) Original.VersionMatch();
                _packets.Clear();
            }
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(ZNet __instance, ZRpc rpc, ref BufferedSocket __state)
        {
            if (!__instance.IsServer() || rpc?.GetSocket() == null) return;
            __state = new BufferedSocket(rpc.GetSocket());
            Traverse.Create(rpc).Field("m_socket").SetValue(__state);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNet __instance, ZRpc rpc, BufferedSocket __state)
        {
            if (!__instance.IsServer()) return;
            var peer = PeerForRpc(__instance, rpc);
            try
            {
                // Send the authoritative profile before releasing Valheim's world-data packets.
                // Otherwise the client blocks in synchronous world generation and cannot process it.
                if (Instance._serverCharactersEnabled.Value)
                {
                    InvokePeer(peer, "VSM_ServerPolicy", true, Instance._clientGraceSeconds.Value);
                    Instance.SendServerCharacter(peer);
                }
                Instance.Joined(peer);
            }
            finally
            {
                if (__state != null)
                {
                    Traverse.Create(rpc).Field("m_socket").SetValue(__state.Original);
                    __state.Release();
                }
            }
        }
    }
    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    private static class CharacterIdPatch { private static void Postfix(ZNet __instance, ZRpc rpc) { if (__instance.IsServer()) Instance.Spawned(PeerForRpc(__instance, rpc)); } }
    [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
    private static class DisconnectPatch { private static void Prefix(ZNetPeer peer) => Instance.Left(peer); }
    [HarmonyPatch(typeof(ZNet), "LoadWorld")]
    private static class WorldLoadPatch { private static void Postfix() => Instance.Event("world.loaded", new { }); }
    [HarmonyPatch(typeof(ZNet), "Save")]
    private static class SaveStartPatch { private static void Prefix() { Instance.RequestCharacterCheckpoints(); Instance.Event("world.save.started", new { }); } }
    [HarmonyPatch(typeof(ZNet), "SaveWorldThread")]
    private static class SaveCompletePatch
    {
        private static void Postfix() { Instance.Event("world.save.completed", new { }); Instance.SaveFinished(true); }
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null) { Instance.Event("world.save.failed", new { error = __exception.Message }); Instance.SaveFinished(false, __exception.Message); }
            return __exception;
        }
    }
    [HarmonyPatch(typeof(ZoneSystem), "SetGlobalKey", typeof(string))]
    private static class GlobalKeyPatch { private static void Postfix(string name) { Instance.Event(name != null && name.StartsWith("defeated_") ? "boss.progression.unlocked" : "world.global_key.added", new { key = name }); } }
    [HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
    private static class RaidPatch { private static void Postfix(RandomEvent ev, Vector3 pos) => Instance.Event(ev == null ? "raid.ended" : "raid.started", new { name = ev?.m_name, position = pos }); }
    [HarmonyPatch(typeof(EnvMan), "UpdateTriggers")]
    private static class DayPatch
    {
        private static void Prefix(float oldDayFraction, float newDayFraction)
        {
            if (oldDayFraction < .25f && newDayFraction >= .25f) Instance.Event("environment.morning.started", new { dayFraction = newDayFraction });
            if (oldDayFraction < .75f && newDayFraction >= .75f) Instance.Event("environment.evening.started", new { dayFraction = newDayFraction });
        }
    }
    [HarmonyPatch(typeof(Game), "UpdateSleeping")]
    private static class SleepPatch
    {
        private static readonly FieldInfo Sleeping = AccessTools.Field(typeof(Game), "m_sleeping");
        private static void Prefix(Game __instance, out bool __state) => __state = Sleeping != null && (bool)Sleeping.GetValue(__instance);
        private static void Postfix(Game __instance, bool __state)
        {
            var sleeping = Sleeping != null && (bool)Sleeping.GetValue(__instance);
            if (!__state && sleeping) Instance.Event("sleep.started", new { });
            if (__state && !sleeping) Instance.Event("sleep.ended", new { });
        }
    }
    [HarmonyPatch(typeof(Character), "OnDeath")]
    private static class BossDeathPatch
    {
        private static void Prefix(Character __instance)
        {
            if (__instance != null && __instance.IsBoss()) Instance.Event("boss.killed", new { boss = __instance.GetHoverName(), position = __instance.transform.position });
        }
    }
    [HarmonyPatch(typeof(Talker), "RPC_Say")]
    private static class ChatPatch
    {
        private static void Prefix(long sender, int ctype, UserInfo user, string text)
        {
            if (ctype == (int)Talker.Type.Whisper) return;
            var eventType = ctype == (int)Talker.Type.Shout ? "chat.shout" : ctype == (int)Talker.Type.Ping ? "map.ping" : "chat.normal";
            var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
            Instance.Event(eventType, new { player = user.Name, platformId = peer?.m_socket?.GetHostName(), peerId = sender, message = text });
        }
    }

    // Valheim's admission check still compares GetNrOfPlayers() with its vanilla constant of 10.
    // During that one check, map the configured capacity to the values the game already understands.
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    private static class PlayerLimitPeerInfoPatch
    {
        [ThreadStatic] private static int _depth;
        internal static bool IsChecking => _depth > 0;
        private static void Prefix() => _depth++;
        private static void Finalizer() { if (_depth > 0) _depth--; }
    }

    [HarmonyPatch(typeof(ZNet), "GetNrOfPlayers")]
    private static class PlayerLimitCountPatch
    {
        private static void Postfix(ref int __result)
        {
            if (!PlayerLimitPeerInfoPatch.IsChecking || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (__result >= MaxPlayers) __result = 10;
            else if (__result >= 10) __result = 9;
        }
    }

    // Backend advertisements have their own capacities. Reflection keeps this compatible with
    // both Steam-only and PlayFab/crossplay server builds without hard assembly references.
    [HarmonyPatch]
    private static class PlayFabLobbyLimitPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => NamedMethods("PlayFab.PlayFabMultiplayerAPI", "CreateLobby");
        private static void Prefix(object __0) => SetCapacity(__0, "MaxPlayers");
    }

    [HarmonyPatch]
    private static class PlayFabNetworkLimitPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => NamedMethods("PlayFab.Party.PlayFabMultiplayerManager", "CreateAndJoinNetwork");
        private static void Prefix(object __0) => SetCapacity(__0, "MaxPlayerCount");
    }

    [HarmonyPatch]
    private static class SteamServerLimitPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => NamedMethods("Steamworks.SteamGameServer", "SetMaxPlayerCount")
            .Where(method => method.GetParameters()[0].ParameterType == typeof(int));
        private static void Prefix(ref int __0) => __0 = MaxPlayers;
    }

    [HarmonyPatch]
    private static class SteamLobbyLimitPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => NamedMethods("Steamworks.SteamMatchmaking", "CreateLobby")
            .Where(method => method.GetParameters().Length > 1 && method.GetParameters()[1].ParameterType == typeof(int));
        private static void Prefix(ref int __1) => __1 = MaxPlayers;
    }

    private static IEnumerable<MethodBase> NamedMethods(string typeName, string methodName)
    {
        var type = AccessTools.TypeByName(typeName);
        return type == null
            ? Enumerable.Empty<MethodBase>()
            : type.GetMethods(AccessTools.all).Where(method => method.Name == methodName && method.GetParameters().Length > 0);
    }

    private static void SetCapacity(object target, string memberName)
    {
        if (target == null) return;
        var property = AccessTools.Property(target.GetType(), memberName);
        if (property != null && property.CanWrite)
        {
            property.SetValue(target, Convert.ChangeType(MaxPlayers, property.PropertyType), null);
            return;
        }
        var field = AccessTools.Field(target.GetType(), memberName);
        if (field != null) field.SetValue(target, Convert.ChangeType(MaxPlayers, field.FieldType));
    }
}
