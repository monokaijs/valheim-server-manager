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
    public const string PluginVersion = "1.5.0";
    private const string LegacyPluginGuid = "dev.monokai.valheim-server-manager.server";
    private const string ClientManifestRpc = "ServerModBootstrap_Manifest_v1";
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly ConcurrentQueue<string> _outgoing = new();
    private readonly Dictionary<long, DateTime> _joined = new();
    private readonly Dictionary<long, bool> _companions = new();
    private readonly HashSet<long> _serverCharacterClients = new();
    private readonly HashSet<long> _characterEnforcementHandled = new();
    private readonly HashSet<long> _pendingCharacterProfiles = new();
    private readonly Dictionary<string, Tuple<long, DateTime>> _inventoryRequests = new();
    private readonly Dictionary<ZDOID, bool> _dead = new();
    private CancellationTokenSource _lifetime;
    private float _nextSnapshot;
    private float _nextDeathPoll;
    private string _saveRequestId;
    private bool _rpcsRegistered;
    private volatile string _clientModManifest;
    private volatile string _pluginRegistryMessage;
    private bool _pluginRegistryPublished;
    private ConfigEntry<string> _managerUrl;
    private ConfigEntry<string> _agentToken;
    private ConfigEntry<bool> _serverCharactersEnabled;
    private ConfigEntry<bool> _acceptFirstJoinProfile;
    private ConfigEntry<int> _characterBackups;
    internal static ServerPlugin Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
        MigrateLegacyConfig();
        _managerUrl = Config.Bind("Connection", "ManagerUrl", Environment.GetEnvironmentVariable("VSM_AGENT_URL") ?? "ws://127.0.0.1:8080/internal/agent", "Loopback manager WebSocket URL.");
        _agentToken = Config.Bind("Connection", "AgentToken", "", "Shared manager token; the VSM_AGENT_TOKEN environment variable takes precedence and is not persisted.");
        _serverCharactersEnabled = Config.Bind("ServerCharacters", "Enabled", true, "Make the server copy of each native Valheim character authoritative. Requires the VSM client companion.");
        _acceptFirstJoinProfile = Config.Bind("ServerCharacters", "AcceptFirstJoinProfile", true, "Allow a character without a server save to seed its first server-owned profile. Disable after migration for a closed realm.");
        _characterBackups = Config.Bind("ServerCharacters", "BackupsToKeep", 10, new ConfigDescription("Rolling native profile backups per character.", new AcceptableValueRange<int>(1, 50)));
        _lifetime = new CancellationTokenSource();
        Patch(typeof(ClientModRelayPatch)); Patch(typeof(AdmissionPatch)); Patch(typeof(PeerInfoPatch)); Patch(typeof(CharacterIdPatch)); Patch(typeof(DisconnectPatch));
        Patch(typeof(WorldLoadPatch)); Patch(typeof(SaveStartPatch)); Patch(typeof(SaveCompletePatch)); Patch(typeof(GlobalKeyPatch));
        Patch(typeof(RaidPatch)); Patch(typeof(DayPatch)); Patch(typeof(SleepPatch)); Patch(typeof(BossDeathPatch)); Patch(typeof(ChatPatch));
        Task.Run(() => ConnectionLoop(_lifetime.Token));
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
        while (_mainThread.TryDequeue(out var action)) try { action(); } catch (Exception ex) { Logger.LogError(ex); }
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        TryRegisterClientRpcs();
        if (!_pluginRegistryPublished && Time.unscaledTime > 2f) PublishPluginRegistry();
        if (Time.unscaledTime >= _nextSnapshot) { _nextSnapshot = Time.unscaledTime + 5f; SendSnapshot(); }
        if (Time.unscaledTime >= _nextDeathPoll) { _nextDeathPoll = Time.unscaledTime + 1f; PollDeaths(); }
        foreach (var expired in _inventoryRequests.Where(item => DateTime.UtcNow - item.Value.Item2 > TimeSpan.FromSeconds(30)).Select(item => item.Key).ToArray()) _inventoryRequests.Remove(expired);
        foreach (var peerId in _pendingCharacterProfiles.ToArray())
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
                    foreach (var peer in ZNet.instance.GetPeers()) ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", 2, message);
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
                    if (!_companions.TryGetValue(peerId, out var inventoryAllowed)) throw new InvalidOperationException("The player companion is not connected.");
                    if (!inventoryAllowed) throw new InvalidOperationException("The player has not enabled inventory inspection.");
                    _inventoryRequests[requestId] = Tuple.Create(peerId, DateTime.UtcNow);
                    ZRoutedRpc.instance.InvokeRoutedRPC(peerId, "VSM_InventoryRequest", requestId);
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
        var id = peer.m_socket?.GetHostName() ?? ""; var name = peer.m_playerName;
        var method = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) }) ?? AccessTools.Method(typeof(ZNet), "Kick", new[] { typeof(ZNetPeer) });
        if (method != null) method.Invoke(ZNet.instance, new object[] { peer }); else peer.m_rpc?.GetSocket()?.Close();
        Event("player.kicked", new { player = name, platformId = id });
        return new { ok = true, player = name };
    }

    private object Ban(JObject data)
    {
        var peer = FindPeer(data);
        if (peer == null) throw new KeyNotFoundException("Player was not found.");
        var id = peer.m_socket?.GetHostName() ?? "";
        Access("m_bannedList", JObject.FromObject(new { platformId = id }), true, "player.banned");
        return Kick(data);
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
        method.Invoke(synced, new object[] { id }); Event(eventName, new { platformId = id }); return new { ok = true };
    }

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
        foreach (var peer in ZNet.instance.GetPeers().Where(peer => peer != null && _joined.TryGetValue(peer.m_uid, out var joined) && DateTime.UtcNow - joined > TimeSpan.FromSeconds(15) && !_serverCharacterClients.Contains(peer.m_uid) && !_characterEnforcementHandled.Contains(peer.m_uid)).ToArray())
        {
            _characterEnforcementHandled.Add(peer.m_uid);
            Logger.LogWarning($"Kicking {peer.m_playerName}: VSM client companion 1.2.0 or newer is required for server-owned characters.");
            Event("server-character.client.required", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName() });
            var method = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) });
            if (method != null) method.Invoke(ZNet.instance, new object[] { peer }); else peer.m_rpc?.GetSocket()?.Close();
        }
    }

    private void SendServerCharacter(long sender)
    {
        var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
        if (peer == null) return;
        try
        {
            var path = CharacterPath(peer);
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                ValidateNativeProfile(bytes);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "VSM_CharacterProfile", true, Convert.ToBase64String(bytes));
                Event("server-character.loaded", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), bytes = bytes.Length });
            }
            else if (_acceptFirstJoinProfile.Value)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "VSM_CharacterProfile", false, "");
                Event("server-character.first-join", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName() });
            }
            else
            {
                Logger.LogWarning($"Rejecting {peer.m_playerName}: no imported server character exists.");
                var method = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) });
                if (method != null) method.Invoke(ZNet.instance, new object[] { peer }); else peer.m_rpc?.GetSocket()?.Close();
            }
        }
        catch (Exception exception)
        {
            Logger.LogError($"Unable to load server character for {peer.m_playerName}: {exception}");
            Event("server-character.load.failed", new { player = peer.m_playerName, error = exception.Message });
            var method = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) });
            if (method != null) method.Invoke(ZNet.instance, new object[] { peer }); else peer.m_rpc?.GetSocket()?.Close();
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
        if (!_serverCharactersEnabled.Value || ZRoutedRpc.instance == null) return;
        foreach (var peerId in _serverCharacterClients.ToArray()) ZRoutedRpc.instance.InvokeRoutedRPC(peerId, "VSM_CharacterCheckpoint");
    }

    internal void ClientHello(long sender, bool inventoryAllowed, string companionVersion)
    {
        _companions[sender] = inventoryAllowed;
        var characterCapable = System.Version.TryParse(companionVersion, out var version) && version >= new System.Version(1, 2, 0);
        if (characterCapable) _serverCharacterClients.Add(sender);
        Event("companion.connected", new { peerId = sender, inventoryAllowed, companionVersion, serverCharacters = characterCapable }, "companion", "reported");
        if (_serverCharactersEnabled.Value && characterCapable) _pendingCharacterProfiles.Add(sender);
    }

    internal void CharacterUpload(long sender, string encoded)
    {
        if (!_serverCharactersEnabled.Value || !_serverCharacterClients.Contains(sender)) return;
        var peer = ZNet.instance?.GetPeers().FirstOrDefault(item => item.m_uid == sender);
        if (peer == null) return;
        try
        {
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
    internal void Joined(ZNetPeer peer) { if (peer == null || string.IsNullOrEmpty(peer.m_playerName) || _joined.ContainsKey(peer.m_uid)) return; _joined[peer.m_uid] = DateTime.UtcNow; Event("player.joined", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid }); }
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
                Event("access.join-request", new { player = playerName ?? "", platformId = hostName });
        }
        catch (Exception exception) { Logger.LogWarning($"Could not capture rejected join request: {exception.GetBaseException().Message}"); }
    }
    private static ZNetPeer PeerForRpc(ZNet znet, ZRpc rpc) => znet.GetPeers().FirstOrDefault(peer => peer != null && peer.m_rpc == rpc);
    internal void Spawned(ZNetPeer peer) { if (peer != null) Event("player.spawned", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid }); }
    internal void Left(ZNetPeer peer) { if (peer == null || !_joined.Remove(peer.m_uid)) return; _companions.Remove(peer.m_uid); _serverCharacterClients.Remove(peer.m_uid); _characterEnforcementHandled.Remove(peer.m_uid); _pendingCharacterProfiles.Remove(peer.m_uid); Event("player.left", new { player = peer.m_playerName, platformId = peer.m_socket?.GetHostName(), peerId = peer.m_uid }); }
    internal void RelayClientManifest(ZNetPeer peer)
    {
        var manifest = _clientModManifest;
        if (peer?.m_rpc == null || string.IsNullOrWhiteSpace(manifest)) return;
        try
        {
            var invoke = peer.m_rpc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "Invoke") return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(object[]);
                });
            if (invoke == null) throw new MissingMethodException(peer.m_rpc.GetType().FullName, "Invoke(string, object[])");
            invoke.Invoke(peer.m_rpc, new object[] { ClientManifestRpc, new object[] { manifest } });
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
    private static class ClientModRelayPatch { private static void Postfix(ZNet __instance, ZNetPeer __0) { if (__instance.IsServer()) Instance.RelayClientManifest(__0); } }
    [HarmonyPatch(typeof(ZNet), "IsAllowed")]
    private static class AdmissionPatch { private static void Postfix(ZNet __instance, string __0, string __1, bool __result) => Instance.AdmissionChecked(__instance, __0, __1, __result); }
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    private static class PeerInfoPatch { private static void Postfix(ZNet __instance, ZRpc rpc) { if (__instance.IsServer()) Instance.Joined(PeerForRpc(__instance, rpc)); } }
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
}
