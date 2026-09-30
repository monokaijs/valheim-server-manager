using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerManager.Data;
using ValheimServerManager.MapSupport;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class WorldMapTests
{
    private const string Peer = "9223372036854775806";
    private static MapFrame Frame(string session = "first", string world = "world") => new(world, "Test", "live", DateTimeOffset.MinValue,
        [new(Peer, "Steam_76561198000000001", session, "Viking", new(1, 50, 2), "ready", true)]);
    private static TeleportRequest Request() => new(Guid.NewGuid(), "world", "Steam_76561198000000001", "first", 100, null, 200);
    [Theory]
    [InlineData(float.NaN, null, 0f)]
    [InlineData(float.PositiveInfinity, null, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(8000f, null, 8000f)]
    [InlineData(0f, 501f, 0f)]
    public void RefusesNonFiniteCoordinatesAndCircularWorldEdge(float x, float? y, float z) => Assert.Throws<ArgumentException>(() => MapCoordinates.Validate(x, y, z));
    [Fact]
    public void TelemetryIsPrivateStateWithExactIdsAndSessionChecks()
    {
        var state = new WorldMapState(); state.BeginConnection(false); Assert.Equal("unsupported", state.Read(true).Status); state.BeginConnection(true); Assert.Equal("loading", state.Read(true).Status); state.Update(Frame());
        var request = Request();
        Assert.Equal(Peer, state.RequirePlayer(Peer, request, true).PeerKey);
        Assert.Throws<InvalidOperationException>(() => state.RequirePlayer(Peer, request, false));
        state.Update(Frame("reconnected"));
        Assert.Throws<InvalidOperationException>(() => state.RequirePlayer(Peer, request, true));
        state.Update(Frame(world: "different"));
        Assert.Throws<InvalidOperationException>(() => state.RequirePlayer(Peer, request, true));
        state.Invalidate();
        Assert.NotEqual("live", state.Read(true).Status);
    }
    [Theory]
    [InlineData("dead", true)]
    [InlineData("loading", true)]
    [InlineData("teleporting", true)]
    [InlineData("ready", false)]
    public void RefusesBusyOrUnsupportedPlayers(string status, bool capable)
    {
        var state = new WorldMapState(); var frame = Frame(); state.Update(frame with { Players = [frame.Players[0] with { State = status, TeleportCapable = capable }] });
        Assert.Throws<InvalidOperationException>(() => state.RequirePlayer(Peer, Request(), true));
    }
    [Fact]
    public void TerrainCannotLeakAcrossWorldChange()
    {
        var state = new WorldMapState(); state.Update(Frame());
        var pixels = Convert.ToBase64String(new byte[65536]); var heights = Convert.ToBase64String(new byte[65536 * 4]);
        state.SetTerrain(new("world", "Test", 256, 12288, pixels, heights, pixels, 30));
        Assert.NotNull(state.Terrain);
        state.SetTile(new("world", "Test", 256, 12288, pixels, heights, pixels, 30, X: 4, Y: 3));
        Assert.Equal(new[] { "4:3" }, state.TileKeys("world"));
        Assert.Null(state.Tile("other", 4, 3));
        state.Update(Frame() with { TerrainRevision = "regenerated" }); Assert.Null(state.Terrain); Assert.Empty(state.TileKeys("world"));
        state.Update(Frame());
        state.Update(Frame(world: "new-world")); Assert.Null(state.Terrain); Assert.Empty(state.TileKeys("world"));
        state.SetTerrain(new("world", "Old", 256, 12288, pixels, heights, pixels, 30)); Assert.Null(state.Terrain);
        Assert.Throws<ArgumentException>(() => state.SetTerrain(new("new-world", "Bad", 256, 12288, "AA==", heights, pixels, 30)));
    }
    [Fact]
    public async Task RepeatedCommandsExecuteOnceAndAuditResultAfterCompletion()
    {
        await using var fixture = await Fixture.Create();
        var state = new WorldMapState(); state.Update(Frame()); var commands = new Commands();
        var service = new WorldMapService(state, commands, fixture.Audit); var request = Request();
        var first = service.Teleport(Peer, request, "admin"); var retry = service.Teleport(Peer, request, "admin");
        await commands.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, commands.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Teleport(Peer, request with { X = 300 }, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Teleport(Peer, Request(), "admin"));
        commands.Result.SetResult(JsonSerializer.SerializeToElement(new { ok = true, position = new { x = 100, y = 50, z = 200 } }));
        await Task.WhenAll(first, retry);
        await service.Teleport(Peer, request, "admin"); Assert.Equal(1, commands.Calls);
        await using var scope = fixture.Provider.CreateAsyncScope(); var records = await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().AuditRecords.ToListAsync();
        Assert.Equal(2, records.Count); Assert.Equal("pending", records[0].Result); Assert.Equal("success", records[1].Result);
        Assert.All(records, record => Assert.Equal("admin", record.Actor));
    }
    [Fact]
    public async Task UnknownOutcomeCannotBeReplayedAfterReconnect()
    {
        await using var fixture = await Fixture.Create(); var state = new WorldMapState(); state.Update(Frame()); var commands = new Commands();
        var service = new WorldMapService(state, commands, fixture.Audit); var request = Request();
        var first = service.Teleport(Peer, request, "admin"); await commands.Started.Task;
        commands.Result.SetException(new IOException("disconnected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        state.Update(Frame("reconnected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Teleport(Peer, request, "admin"));
        Assert.Equal(1, commands.Calls);
    }
    private sealed class Commands : IWorldMapCommands
    {
        public bool IsConnected => true;
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<JsonElement> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonElement> Command(string name, object payload, TimeSpan timeout) { Calls++; Assert.Equal("player.teleport", name); Started.TrySetResult(); return Result.Task; }
    }
    private sealed class Fixture(ServiceProvider provider, string root) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;
        public AuditService Audit => new(provider.GetRequiredService<IServiceScopeFactory>(), new Microsoft.AspNetCore.Http.HttpContextAccessor());
        public static async Task<Fixture> Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vsm-map-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var services = new ServiceCollection(); services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "db")};Pooling=False"));
            var fixture = new Fixture(services.BuildServiceProvider(), root);
            await using var scope = fixture.Provider.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync(); return fixture;
        }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); Directory.Delete(root, true); }
    }
}
