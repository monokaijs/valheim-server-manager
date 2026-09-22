using System.Globalization;
using System.Text.Json;

namespace ValheimServerManager.Services;

public sealed record InspectionFrame(long Sequence, string PeerId, DateTimeOffset ReceivedAt,
    string Status, string? Error, JsonElement? Snapshot);

/// <summary>Demand-driven, bounded, single-flight reads; snapshots are never persisted or broadcast.</summary>
public sealed class InventoryInspectionService(AgentGateway agent, ServerState state)
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(1250);
    private readonly object _gate = new();
    private readonly Dictionary<long, Sample> _samples = new();
    private readonly Dictionary<string, (long Peer, string Actor)> _viewers = new();
    private long _sequence;
    private sealed class Sample
    {
        public Task<InspectionFrame>? Pending;
        public InspectionFrame? Last;
        public long Generation;
    }

    public void Begin(string connectionId, string actor, long peer)
    {
        lock (_gate)
        {
            if (_viewers.ContainsKey(connectionId)) throw new InvalidOperationException("Close the current inspection before starting another.");
            if (_viewers.Count >= 64 || _viewers.Values.Count(item => item.Actor == actor) >= 4)
                throw new InvalidOperationException("The live inspection session limit has been reached.");
            _viewers.Add(connectionId, (peer, actor));
        }
    }

    public void End(string connectionId)
    {
        lock (_gate)
        {
            if (!_viewers.Remove(connectionId, out var viewer)) return;
            if (!_viewers.Values.Any(item => item.Peer == viewer.Peer)) _samples.Remove(viewer.Peer);
        }
    }

    public Task<InspectionFrame> Read(long peer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<InspectionFrame> pending;
        lock (_gate)
        {
            if (!_samples.TryGetValue(peer, out var sample))
            {
                if (_samples.Count >= 64) throw new InvalidOperationException("Too many active inspection targets.");
                _samples.Add(peer, sample = new Sample());
            }
            // Viewers of the same player share one RPC, including slow responses.
            if (sample.Pending is { IsCompleted: false }) pending = sample.Pending;
            else if (sample.Last is not null && sample.Generation == agent.Generation
                && DateTimeOffset.UtcNow - sample.Last.ReceivedAt < TimeSpan.FromMilliseconds(1100))
                pending = Task.FromResult(sample.Last);
            else pending = sample.Pending = Capture(peer, sample);
        }
        return pending.WaitAsync(cancellationToken);
    }

    // Client payloads are untrusted even after a compatible handshake. Reject malformed
    // collection members and non-finite values rather than crashing an administrator's UI.
    public static bool IsValidSnapshot(JsonElement value)
    {
        static bool Property(JsonElement obj, string key, JsonValueKind kind, out JsonElement result)
        { result = default; return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out result) && result.ValueKind == kind; }
        static bool Text(JsonElement obj, string key) => Property(obj, key, JsonValueKind.String, out var text) && text.GetString()!.Length <= 8192;
        static bool Number(JsonElement obj, string key) => Property(obj, key, JsonValueKind.Number, out var number) && number.TryGetDouble(out var n) && double.IsFinite(n);
        if (!Property(value, "ok", JsonValueKind.True, out _)
            || !Property(value, "character", JsonValueKind.Object, out var character)
            || !Property(value, "items", JsonValueKind.Array, out var items) || items.GetArrayLength() > 1024
            || !Property(value, "icons", JsonValueKind.Object, out var icons)
            || !Property(character, "skills", JsonValueKind.Array, out var skills) || skills.GetArrayLength() > 256) return false;
        foreach (var key in new[] { "id", "name", "biome" }) if (!Text(character, key)) return false;
        foreach (var key in new[] { "health", "maxHealth", "stamina", "maxStamina", "eitr", "maxEitr", "armor", "weight", "inventoryWidth", "inventoryHeight" })
            if (!Number(character, key)) return false;
        foreach (var skill in skills.EnumerateArray()) if (!Text(skill, "name") || !Number(skill, "level")) return false;
        foreach (var item in items.EnumerateArray())
        {
            foreach (var key in new[] { "prefab", "name", "description", "type", "iconKey", "crafterId" }) if (!Text(item, key)) return false;
            if (!Text(item, "crafterName") && !Property(item, "crafterName", JsonValueKind.Null, out _)) return false;
            foreach (var key in new[] { "stack", "maxStack", "quality", "maxQuality", "durability", "maxDurability", "weight", "x", "y", "variant" })
                if (!Number(item, key)) return false;
            foreach (var key in new[] { "equipped", "teleportable" })
                if (!Property(item, key, JsonValueKind.True, out _) && !Property(item, key, JsonValueKind.False, out _)) return false;
        }
        var count = 0;
        foreach (var icon in icons.EnumerateObject())
            if (++count > 1024 || icon.Value.ValueKind != JsonValueKind.String || icon.Value.GetString()!.Length > 200_000) return false;
        return true;
    }

    private async Task<InspectionFrame> Capture(long peer, Sample sample)
    {
        var generation = agent.Generation;
        var status = "unavailable";
        string? error = null;
        JsonElement? snapshot = null;
        try
        {
            var player = state.Players.FirstOrDefault(item => item.PeerId == peer);
            if (!agent.IsConnected) { status = "disconnected"; error = "The server agent is disconnected."; }
            else if (player is null) { status = "offline"; error = "The player has left the server."; }
            else if (!player.Companion || !player.InventoryAllowed)
                error = "A compatible client runtime with inventory-sharing permission is required.";
            else
            {
                var result = await agent.Command("inventory.request", new { peerId = peer }, TimeSpan.FromSeconds(12));
                if (generation != agent.Generation) { status = "disconnected"; error = "The server restarted during this sample."; }
                else if (IsValidSnapshot(result)) { status = "live"; snapshot = result.Clone(); }
                else
                    error = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString() : "The client returned an unavailable or malformed inventory sample.";
            }
        }
        catch (OperationCanceledException) { status = "stale"; error = "The inventory request timed out. Waiting for a fresh sample."; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
        { status = "disconnected"; error = "The inventory connection was interrupted. Reconnecting…"; }
        var frame = new InspectionFrame(Interlocked.Increment(ref _sequence), peer.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow, status, error, snapshot);
        lock (_gate) { sample.Last = frame; sample.Generation = generation; }
        return frame;
    }
}
