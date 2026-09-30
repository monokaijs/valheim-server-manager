using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ValheimServerManager.Services;

public interface IWorldMapCommands
{
    bool IsConnected { get; }
    Task<JsonElement> Command(string name, object payload, TimeSpan timeout);
}

public sealed class WorldMapService(WorldMapState map, IWorldMapCommands agent, AuditService audit)
{
    private sealed record Command(string Actor, string Peer, TeleportRequest Request, DateTimeOffset At, Lazy<Task<JsonElement>> Work);
    private readonly ConcurrentDictionary<Guid, Command> _commands = new();
    private readonly ConcurrentDictionary<string, byte> _busy = new();
    public async Task<JsonElement> Teleport(string peerKey, TeleportRequest request, string actor)
    {
        if (!long.TryParse(peerKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var peerId) || peerId == 0) throw new ArgumentException("Invalid player identity.");
        // Retries use the same command ID; never replay an uncertain operation automatically.
        foreach (var entry in _commands.Where(e => e.Value.Work.IsValueCreated && e.Value.Work.Value.IsCompleted && DateTimeOffset.UtcNow - e.Value.At > TimeSpan.FromMinutes(10))) _commands.TryRemove(entry.Key, out _);
        if (_commands.Count >= 1000) throw new InvalidOperationException("Too many teleport requests. Try again later.");
        var candidate = new Command(actor, peerKey, request, DateTimeOffset.UtcNow, new Lazy<Task<JsonElement>>(() => Execute(peerId, peerKey, request, actor)));
        var command = _commands.GetOrAdd(request.CommandId, candidate);
        if (command.Actor != actor || command.Peer != peerKey || command.Request != request) throw new InvalidOperationException("Command ID already belongs to a different teleport request.");
        return await command.Work.Value;
    }
    private async Task<JsonElement> Execute(long peerId, string peerKey, TeleportRequest request, string actor)
    {
        map.RequirePlayer(peerKey, request, agent.IsConnected);
        if (!_busy.TryAdd(peerKey, 0)) throw new InvalidOperationException("A teleport is already pending for this player.");
        var detail = FormattableString.Invariant($"world={request.WorldId};x={request.X};y={request.Y};z={request.Z};command={request.CommandId}");
        try
        {
            await audit.Write("player.teleport.request", request.PlatformId, "pending", detail, actor: actor);
            var result = await agent.Command("player.teleport", new { peerId, request.WorldId, request.TerrainRevision, request.PlatformId, request.Session, request.X, request.Y, request.Z }, TimeSpan.FromSeconds(60));
            var ok = result.TryGetProperty("ok", out var success) && success.ValueKind == JsonValueKind.True;
            await audit.Write("player.teleport.result", request.PlatformId, ok ? "success" : result.TryGetProperty("status", out var status) && status.GetString() == "unknown" ? "unknown" : "failure", detail, actor: actor);
            if (!ok) throw new InvalidOperationException(result.TryGetProperty("error", out var error) ? error.GetString() ?? "Teleport failed." : "Teleport failed.");
            return result;
        }
        catch (TaskCanceledException)
        {
            await audit.Write("player.teleport.result", request.PlatformId, "unknown", detail, actor: actor);
            throw new TimeoutException("Teleport outcome is unknown. Check the live position before issuing a new command.");
        }
        catch (IOException)
        {
            await audit.Write("player.teleport.result", request.PlatformId, "unknown", detail, actor: actor);
            throw new InvalidOperationException("Server disconnected during teleport. Outcome is unknown; check position after reconnecting.");
        }
        finally { _busy.TryRemove(peerKey, out _); }
    }
}
