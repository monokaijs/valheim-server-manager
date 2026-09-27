using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimServerManager.Data;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class InventoryArchiveServiceTests
{
    [Fact]
    public async Task SQLitePendingEditsCanBeReadAndBackgroundPollDoesNotStop()
    {
        var database = Path.Combine(Path.GetTempPath(), $"vsm-inventory-{Guid.NewGuid():N}.db");
        const string platformId = "Steam_76561198000000001";
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={database}"));
            await using var provider = services.BuildServiceProvider();
            var ids = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).ToArray();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.PendingInventoryEdits.AddRange(ids.Select((id, index) => new PendingInventoryEdit
                {
                    Id = id,
                    PlatformId = platformId,
                    Status = "pending",
                    CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(index)
                }));
                await db.SaveChangesAsync();
            }

            var archive = new InventoryArchiveService(provider.GetRequiredService<IServiceScopeFactory>(), null!,
                new ServerState(null!), null!, NullLogger<InventoryArchiveService>.Instance);
            using var result = JsonDocument.Parse(JsonSerializer.Serialize(await archive.Read(platformId, CancellationToken.None)));
            var edits = result.RootElement.GetProperty("edits").EnumerateArray().ToArray();
            Assert.Equal(50, edits.Length);
            Assert.Equal(ids[59], edits[0].GetProperty("Id").GetGuid());
            Assert.Equal(ids[10], edits[^1].GetProperty("Id").GetGuid());

            await archive.StartAsync(CancellationToken.None);
            try
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(6));
                Assert.Same(delay, await Task.WhenAny(archive.ExecuteTask!, delay));
            }
            finally { await archive.StopAsync(CancellationToken.None); }
        }
        finally { if (File.Exists(database)) File.Delete(database); }
    }
}
