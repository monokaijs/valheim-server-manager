using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerManager.Data;
using ValheimServerManager.Models;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class PlayerDirectoryTests
{
    [Fact]
    public async Task RecordsPlayersAcrossSnapshotsAndKeepsDisconnectedPlayers()
    {
        var database = Path.Combine(Path.GetTempPath(), $"vsm-players-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={database}"));
            services.AddSingleton<PlayerDirectoryService>();
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();

            var directory = provider.GetRequiredService<PlayerDirectoryService>();
            await directory.Record([
                new PlayerInfo(1, "Astrid", "Steam_76561198000000001", DateTimeOffset.UtcNow, null, true, true),
                new PlayerInfo(2, "Bjorn", "Steam_76561198000000002", DateTimeOffset.UtcNow, null, true, true)
            ], CancellationToken.None);
            await directory.Record([
                new PlayerInfo(1, "Astrid II", "Steam_76561198000000001", DateTimeOffset.UtcNow, null, true, true)
            ], CancellationToken.None);

            var roster = await directory.List(CancellationToken.None);
            Assert.Equal(2, roster.Count);
            Assert.Contains(roster, player => player.PlatformId == "Steam_76561198000000002" && player.Name == "Bjorn");
            Assert.Contains(roster, player => player.PlatformId == "Steam_76561198000000001" && player.Name == "Astrid II");
        }
        finally { if (File.Exists(database)) File.Delete(database); }
    }
}
