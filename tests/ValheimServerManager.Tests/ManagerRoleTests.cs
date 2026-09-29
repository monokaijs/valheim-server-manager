using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ValheimServerManager.Data;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class ManagerRoleTests
{
    [Fact]
    public async Task ExplicitModeratorRoleOverridesGameAdminAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string gameAdmin = "76561198000000001";
        const string moderator = "76561198000000002";
        try
        {
            File.WriteAllText(Path.Combine(root, "adminlist.txt"), "Steam_" + gameAdmin);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VSM_SAVE_PATH"] = root }).Build();
            var access = new AccessListService(configuration, null!);
            var options = new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite($"Data Source={Path.Combine(root, "manager.db")};Pooling=False").Options;
            await using (var db = new ManagerDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var roles = new ManagerRoleService(db, access);
                Assert.Equal(ManagerRoleService.Admin, await roles.GetRole(gameAdmin));
                Assert.Null(await roles.GetRole(moderator));
                await roles.SetRole(gameAdmin, ManagerRoleService.Mod);
                await roles.SetRole(moderator, ManagerRoleService.Mod);
                Assert.Equal(ManagerRoleService.Mod, await roles.GetRole(gameAdmin));
                Assert.Equal(2, (await roles.List()).Count);
            }
            await using (var db = new ManagerDbContext(options))
            {
                var roles = new ManagerRoleService(db, access);
                Assert.Equal(ManagerRoleService.Mod, await roles.GetRole(gameAdmin));
                Assert.Equal(ManagerRoleService.Mod, await roles.GetRole(moderator));
                await roles.SetRole(moderator, ManagerRoleService.Admin);
                Assert.Equal(ManagerRoleService.Admin, await roles.GetRole(moderator));
                await Assert.ThrowsAsync<ArgumentException>(() => roles.SetRole(moderator, "owner"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("76561198000000001", true)]
    [InlineData("Steam_76561198000000001", false)]
    [InlineData("123", false)]
    public void RoleIdsRequireExactSteam64(string steamId, bool valid) =>
        Assert.Equal(valid, ManagerRoleService.ValidSteamId(steamId));
}
