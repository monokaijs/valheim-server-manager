using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimServerManager.MapSupport;

namespace ValheimServerManager.Server;

public sealed partial class ServerPlugin
{
    private readonly HashSet<ZNetPeer> _mapClients = new();
    private readonly Dictionary<ZNetPeer, string> _mapSessions = new();
    private sealed class TeleportPending
    {
        public ZNetPeer Peer;
        public ZDOID Character;
        public string World;
        public DateTime Deadline;
    }
    private readonly Dictionary<string, TeleportPending> _teleports = new();
    private float _nextMapFrame;
    private volatile string _mapFrameMessage;
    private static string MapWorldId() => WorldGenerator.instance?.m_world?.m_uid.ToString(CultureInfo.InvariantCulture) ?? "";
    private string MapSession(ZNetPeer peer)
    {
        if (!_mapSessions.TryGetValue(peer, out var session)) _mapSessions[peer] = session = Guid.NewGuid().ToString("N");
        return session;
    }
    private void TickWorldMap()
    {
        var generator = WorldGenerator.instance;
        if (generator?.m_world == null || generator.m_world.m_menu) return;
        var world = MapWorldId();
        TickTerrain(generator, world);
        if (Time.unscaledTime >= _nextMapFrame)
        {
            _nextMapFrame = Time.unscaledTime + 1;
            var players = ZNet.instance.GetPeers().Where(p => p != null && p.IsReady()).Select(p =>
            {
                var zdo = p.m_characterID.IsNone() ? null : ZDOMan.instance?.GetZDO(p.m_characterID);
                var position = zdo?.GetPosition();
                var valid = position.HasValue && MapCoordinates.Finite(position.Value.x) && MapCoordinates.Finite(position.Value.y) && MapCoordinates.Finite(position.Value.z);
                return new { peerKey = p.m_uid.ToString(CultureInfo.InvariantCulture), platformId = p.m_socket?.GetHostName() ?? "", session = MapSession(p), name = p.m_playerName ?? "?", position = valid ? new { x = position.Value.x, y = position.Value.y, z = position.Value.z } : null, state = zdo == null ? "loading" : zdo.GetBool("dead", false) ? "dead" : _teleports.Values.Any(t => ReferenceEquals(t.Peer, p)) ? "teleporting" : "ready", teleportCapable = _mapClients.Contains(p) };
            }).ToArray();
            _mapFrameMessage = Newtonsoft.Json.JsonConvert.SerializeObject(new { type = "mapFrame", payload = new { worldId = world, name = generator.m_world.m_name, status = "live", terrainRevision = _terrainRevision, terrainStatus = _mapFailed ? "failed" : _mapTileMessages.Count == 64 ? "ready" : "generating", players } });
        }
        foreach (var pending in _teleports.ToArray())
        {
            if (pending.Value.World == world && ZNet.instance.GetPeers().Contains(pending.Value.Peer) && pending.Value.Peer.m_characterID == pending.Value.Character && DateTime.UtcNow < pending.Value.Deadline) continue;
            _teleports.Remove(pending.Key);
            Reply(pending.Key, new { ok = false, status = "unknown", error = "Teleport outcome unknown: player/world changed or the client timed out. Check position before retrying." });
        }
    }
    private void BeginTeleport(string requestId, JObject data)
    {
        var x = (float?)data["x"] ?? float.NaN; var z = (float?)data["z"] ?? float.NaN; var y = (float?)data["y"];
        MapCoordinates.Validate(x, y, z);
        var peerId = (long?)data["peerId"] ?? 0;
        var peer = ZNet.instance.GetPeers().SingleOrDefault(p => p != null && p.m_uid == peerId && p.IsReady());
        var world = MapWorldId();
        if (peer == null || !_mapClients.Contains(peer) || !_mapSessions.TryGetValue(peer, out var session) || session != (string)data["session"] || (peer.m_socket?.GetHostName() ?? "") != (string)data["platformId"] || world != (string)data["worldId"] || _terrainRevision != (string)data["terrainRevision"])
            throw new InvalidOperationException("Player, client capability, session or world changed. Refresh the map.");
        var zdo = peer.m_characterID.IsNone() ? null : ZDOMan.instance?.GetZDO(peer.m_characterID);
        if (zdo == null || zdo.GetBool("dead", false) || _scheduledKicks.ContainsKey(peer.m_uid) || _teleports.Values.Any(t => ReferenceEquals(t.Peer, peer)))
            throw new InvalidOperationException("Player is loading, dead, disconnecting, or already teleporting.");
        var height = WorldGenerator.instance.GetHeight(x, z);
        if (!MapCoordinates.Finite(height) || height < MapWaterLevel() + .5f) throw new ArgumentException("Destination must be on dry natural terrain. Ocean destinations are refused.");
        if (y.HasValue && Math.Abs(y.Value - (height + .5f)) > 2) throw new ArgumentException("Manual Y must be within 2 metres of natural terrain. Leave Y empty for automatic ground placement.");
        _teleports[requestId] = new TeleportPending { Peer = peer, Character = peer.m_characterID, World = world, Deadline = DateTime.UtcNow.AddSeconds(55) };
        InvokePeer(peer, "VSM_Teleport", requestId, Newtonsoft.Json.JsonConvert.SerializeObject(new { worldId = world, x, y, z }));
    }
    private void RegisterMapRpcs(ZNetPeer peer)
    {
        peer.m_rpc.Register<int>("VSM_MapHello", (rpc, protocol) => { if (ReferenceEquals(rpc, peer.m_rpc) && protocol == 1) _mapClients.Add(peer); });
        peer.m_rpc.Register<string, string>("VSM_TeleportResult", (rpc, request, json) =>
        {
            if (!ReferenceEquals(rpc, peer.m_rpc) || json == null || json.Length > 2000 || !_teleports.TryGetValue(request, out var pending) || !ReferenceEquals(pending.Peer, peer) || pending.Character != peer.m_characterID || pending.World != MapWorldId()) return;
            _teleports.Remove(request);
            try { Reply(request, JObject.Parse(json)); } catch { Reply(request, new { ok = false, error = "Invalid client teleport result." }); }
        });
    }
    private void ForgetMapPeer(ZNetPeer peer)
    {
        _mapClients.Remove(peer); _mapSessions.Remove(peer);
        foreach (var pending in _teleports.Where(p => ReferenceEquals(p.Value.Peer, peer)).ToArray())
        { _teleports.Remove(pending.Key); Reply(pending.Key, new { ok = false, status = "unknown", error = "Player disconnected. Teleport outcome unknown; check position after reconnecting." }); }
    }
}
