using System.Globalization;
using System.Text.Json;

namespace ValheimServerManager.Services;

public sealed record InspectionFrame(long Sequence, string PeerId, DateTimeOffset ReceivedAt,
    string Status, string? Error, JsonElement? Snapshot);

/// <summary>
/// Demand-driven, bounded, single-flight inventory reads. No background collection,
/// persistence, event-bus publication, or webhook delivery takes place here.
/// </summary>
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
            // All viewers of the same player share one game RPC, including slow responses.
            if (sample.Pending is { IsCompleted: false }) pending = sample.Pending;
            else if (sample.Last is not null && sample.Generation == agent.Generation
                && DateTimeOffset.UtcNow - sample.Last.ReceivedAt < TimeSpan.FromMilliseconds(1100))
                pending = Task.FromResult(sample.Last);
            else pending = sample.Pending = Capture(peer, sample);
        }
        // Cancel the viewer's wait, not the shared request belonging to other viewers.
        return pending.WaitAsync(cancellationToken);
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
                else if (result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                    && result.TryGetProperty("character", out var character) && character.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                { status = "live"; snapshot = result.Clone(); }
                else
                {
                    error = result.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString() : "The client could not produce a current inventory sample.";
                }
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
