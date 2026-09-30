using ValheimServerManager.MapSupport;

namespace ValheimServerManager.Services;

public sealed record MapPosition(float X, float Y, float Z);
public sealed record MapPlayer(string PeerKey, string PlatformId, string Session, string Name, MapPosition? Position, string State, bool TeleportCapable);
public sealed record MapTerrain(string WorldId, string Name, int Size, float Extent, string Biomes, string Heights, string Forests, float WaterLevel, int WorldSize = 2048, int X = 0, int Y = 0, string Revision = "");
public sealed record MapFrame(string WorldId, string Name, string Status, DateTimeOffset ReceivedAt, MapPlayer[] Players, string TerrainStatus = "generating", string TerrainRevision = "");
public sealed record TeleportRequest(Guid CommandId, string WorldId, string PlatformId, string Session, float X, float? Y, float Z, string TerrainRevision = "");

// Never broadcast these samples through the general live hub or event/webhook bus.
public sealed class WorldMapState
{
    private readonly object _gate = new();
    private MapFrame _frame = new("", "", "unavailable", DateTimeOffset.MinValue, []);
    private MapTerrain? _terrain;
    private readonly Dictionary<string, MapTerrain> _tiles = new();
    public void Invalidate() { lock (_gate) _frame = _frame with { Status = "disconnected", ReceivedAt = DateTimeOffset.MinValue }; }
    public void BeginConnection(bool supported) { lock (_gate) _frame = _frame with { Status = supported ? "loading" : "unsupported", ReceivedAt = DateTimeOffset.UtcNow }; }
    public void Update(MapFrame frame)
    {
        if (frame.WorldId.Length is < 1 or > 100 || frame.Players.Length > 100) throw new ArgumentException("Invalid map frame.");
        foreach (var player in frame.Players)
            if (player.Position is { } p) if (!MapCoordinates.Finite(p.X) || !MapCoordinates.Finite(p.Y) || !MapCoordinates.Finite(p.Z)) throw new ArgumentException("Invalid player position.");
        lock (_gate)
        {
            if (_frame.WorldId != frame.WorldId || _frame.TerrainRevision != frame.TerrainRevision) { _terrain = null; _tiles.Clear(); }
            _frame = frame with { ReceivedAt = DateTimeOffset.UtcNow };
        }
    }
    private static void ValidateTerrain(MapTerrain terrain)
    {
        if (terrain.Size != 256 || terrain.Extent != 12288 || terrain.WorldSize != 2048 || Convert.FromBase64String(terrain.Biomes).Length != 256 * 256 || Convert.FromBase64String(terrain.Heights).Length != 256 * 256 * 4 || Convert.FromBase64String(terrain.Forests).Length != 256 * 256 || !MapCoordinates.Finite(terrain.WaterLevel))
            throw new ArgumentException("Invalid map terrain.");
    }
    public void SetTerrain(MapTerrain terrain)
    {
        ValidateTerrain(terrain);
        lock (_gate) { if (_frame.WorldId == terrain.WorldId && _frame.TerrainRevision == terrain.Revision) _terrain = terrain; }
    }
    public void SetTile(MapTerrain terrain)
    {
        if (terrain.X is < 0 or > 7 || terrain.Y is < 0 or > 7) throw new ArgumentException("Invalid tile index.");
        ValidateTerrain(terrain);
        lock (_gate) { if (_frame.WorldId == terrain.WorldId && _frame.TerrainRevision == terrain.Revision) _tiles[$"{terrain.X}:{terrain.Y}"] = terrain; }
    }
    public string[] TileKeys(string world) { lock (_gate) return _frame.WorldId == world ? _tiles.Keys.ToArray() : []; }
    public MapTerrain? Tile(string world, int x, int y) { lock (_gate) return _frame.WorldId == world ? _tiles.GetValueOrDefault($"{x}:{y}") : null; }
    public MapTerrain? Terrain { get { lock (_gate) return _terrain; } }
    public MapFrame Read(bool connected)
    {
        lock (_gate) return _frame with { Status = !connected ? "disconnected" : _frame.Status == "unsupported" ? "unsupported" : DateTimeOffset.UtcNow - _frame.ReceivedAt > TimeSpan.FromSeconds(5) ? "stale" : _frame.Status };
    }
    public MapPlayer RequirePlayer(string peerKey, TeleportRequest request, bool connected)
    {
        MapCoordinates.Validate(request.X, request.Y, request.Z);
        if (request.CommandId == Guid.Empty) throw new ArgumentException("A command ID is required.");
        var frame = Read(connected);
        if (frame.Status != "live" || frame.WorldId != request.WorldId || frame.TerrainRevision != request.TerrainRevision) throw new InvalidOperationException("Map is stale, disconnected, or belongs to a different world. Refresh it.");
        var player = frame.Players.SingleOrDefault(p => p.PeerKey == peerKey);
        if (player is null || player.PlatformId != request.PlatformId || player.Session != request.Session) throw new InvalidOperationException("Player disconnected or reconnected. Select the current player again.");
        if (player.State != "ready" || player.Position is null || !player.TeleportCapable) throw new InvalidOperationException("Player must be alive and ready with the updated Server Manager client.");
        return player;
    }
}
