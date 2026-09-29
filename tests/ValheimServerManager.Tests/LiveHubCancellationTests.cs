using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimServerManager.Data;
using ValheimServerManager.Hubs;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class LiveHubCancellationTests
{
    [Fact]
    public async Task CancelledWatch_EndsWithoutErrorAndReleasesViewer()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-watch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var steamId = "12345678901234567";
        Directory.CreateDirectory(Path.Combine(root, "worlds"));
        File.WriteAllText(Path.Combine(root, "worlds", "adminlist.txt"), steamId);
        try
        {
            await using var provider = InspectionAndFilesTests.Provider(root);
            await InspectionAndFilesTests.Initialize(provider);
            var config = provider.GetRequiredService<IConfiguration>();
            var gateway = new AgentGateway(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, config,
                NullLogger<AgentGateway>.Instance);
            var inspection = new InventoryInspectionService(gateway, new ServerState(null!));
            var access = new AccessListService(config, gateway);
            await using var roleScope = provider.CreateAsyncScope();
            var roles = new ManagerRoleService(roleScope.ServiceProvider.GetRequiredService<ManagerDbContext>(), access);
            var hub = new LiveHub(inspection, roles, provider.GetRequiredService<AuditService>())
            {
                Context = new WatchContext(steamId)
            };
            using var cancellation = new CancellationTokenSource();
            await using (var watch = hub.WatchPlayer("42", cancellation.Token).GetAsyncEnumerator())
            {
                Assert.True(await watch.MoveNextAsync());
                Assert.Equal("disconnected", watch.Current.Status);
                cancellation.Cancel();
                Assert.False(await watch.MoveNextAsync());
            }
            inspection.Begin("test-connection", "Steam_" + steamId, 42);
            inspection.End("test-connection");
            await using var scope = provider.CreateAsyncScope();
            var actions = await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().AuditRecords
                .Where(record => record.Target == "42")
                .Select(record => record.Action).ToListAsync();
            Assert.Contains("player.inventory.watch.start", actions);
            Assert.Contains("player.inventory.watch.stop", actions);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class WatchContext(string steamId) : HubCallerContext
    {
        public override string ConnectionId => "test-connection";
        public override string? UserIdentifier => steamId;
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, steamId)], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
