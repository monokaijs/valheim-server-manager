using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerManager.Data;
using ValheimServerManager.Models;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class InspectionAndFilesTests
{
    [Fact]
    public async Task PasswordDisabled_SurvivesColdRestartAndPopulatedEnvironment()
    {
        var root = Temporary();
        try
        {
            await using (var provider = Provider(root, new() { ["SERVER_PASSWORD"] = "environment-password", ["SERVER_PUBLIC"] = "1" }))
            {
                await Initialize(provider);
                var service = provider.GetRequiredService<ServerSettingsService>();
                var defaults = await service.Get();
                Assert.True(defaults.PasswordEnabled);
                var request = JsonSerializer.Deserialize<ServerSettingsMutation>(JsonSerializer.Serialize(defaults))!;
                await service.Set(request with { PasswordEnabled = true, Password = "persisted-password" });
                await service.Set(request with { PasswordEnabled = false, Password = null });
            }
            // A new container/service provider, same persistent database and key ring,
            // and an environment variable that would otherwise enable the password.
            await using (var restarted = Provider(root, new() { ["SERVER_PASSWORD"] = "new-environment-password", ["SERVER_PUBLIC"] = "1" }))
            {
                var service = restarted.GetRequiredService<ServerSettingsService>();
                var settings = await service.Get();
                Assert.False(settings.PasswordEnabled);
                Assert.True(settings.HasPassword);
                Assert.False(settings.PublicListing);
                var args = (await service.BuildArguments()).ToList();
                Assert.Single(args.Where(arg => arg == "-password"));
                Assert.Equal("", args[args.IndexOf("-password") + 1]);
                Assert.Equal("0", args[args.IndexOf("-public") + 1]);
                var request = JsonSerializer.Deserialize<ServerSettingsMutation>(JsonSerializer.Serialize(settings))!;
                await service.Set(request with { PasswordEnabled = true });
                args = (await service.BuildArguments()).ToList();
                Assert.Equal("persisted-password", args[args.IndexOf("-password") + 1]);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InspectionPolicy_IsRequiredIndependentlyOfServerCharactersAndPersists()
    {
        var root = Temporary();
        try
        {
            await using (var provider = Provider(root))
            {
                await Initialize(provider);
                var service = provider.GetRequiredService<ServerCharacterSettingsService>();
                var settings = await service.Get();
                Assert.False(settings.Enabled);
                Assert.True(settings.RequireInventoryInspection);
                var updated = await service.Set(settings with { RequireInventoryInspection = false });
                Assert.True(updated.RequireInventoryInspection);
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                var legacy = await db.ManagerSettings.SingleAsync(item => item.Key == ServerCharacterSettingsService.InspectionRequiredKey);
                legacy.Value = "false";
                await db.SaveChangesAsync();
            }
            await using var restarted = Provider(root);
            var saved = await restarted.GetRequiredService<ServerCharacterSettingsService>().Get();
            Assert.True(saved.RequireInventoryInspection);
            Assert.False(saved.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9007199254740993", 9007199254740993L)]
    public void PlayerPeerKey_PreservesAll64Bits(string expected, long id)
    {
        var player = new PlayerInfo(id, "Viking", "Steam_12345678901234567", DateTimeOffset.UtcNow, null, true, true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(player, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(expected, json.RootElement.GetProperty("peerKey").GetString());
    }

    [Fact]
    public void LiveInspection_LimitsSessionsAndReleasesCapacity()
    {
        var service = new InventoryInspectionService(null!, null!);
        for (var i = 0; i < 4; i++) service.Begin("a" + i, "admin", i + 1);
        Assert.Throws<InvalidOperationException>(() => service.Begin("a4", "admin", 5));
        Assert.Throws<InvalidOperationException>(() => service.Begin("a0", "another-admin", 2));
        service.End("a0");
        service.Begin("a4", "admin", 5);
        service.End("does-not-exist");
    }

    [Theory]
    [InlineData("../outside.cfg")]
    [InlineData("/etc/passwd.txt")]
    [InlineData("C:/Windows/secret.txt")]
    [InlineData("author\\..\\outside.cfg")]
    [InlineData("author/../outside.json")]
    [InlineData("author//items.yml")]
    [InlineData("author/./items.yml")]
    [InlineData("author/ secret.cfg")]
    [InlineData("author/evil.dll")]
    [InlineData("author/script.sh")]
    [InlineData("author/null\0.txt")]
    public void FileManager_RejectsEscapesAndExecutables(string relative)
    {
        Assert.Throws<InvalidDataException>(() => ModConfigService.ResolveTextPath(Path.GetTempPath(), relative));
    }

    [Theory]
    [InlineData("author/data/items.json")]
    [InlineData("author/recipes/wood.yml")]
    [InlineData("author/settings.cfg")]
    [InlineData("author/settings.toml")]
    [InlineData("author/settings.xml")]
    public void FileManager_AcceptsNestedTextFiles(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-safe");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, relative)), ModConfigService.ResolveTextPath(root, relative));
    }

    [Fact]
    public void FileManager_RejectsLinkedRootAndLinkedSubdirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Temporary();
        try
        {
            var real = Path.Combine(root, "real");
            Directory.CreateDirectory(real);
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), real);
            Assert.Throws<InvalidDataException>(() => ModConfigService.ResolveTextPath(Path.Combine(root, "linked"), "secret.cfg"));
            Directory.CreateSymbolicLink(Path.Combine(real, "escape"), root);
            Assert.Throws<InvalidDataException>(() => ModConfigService.ResolveTextPath(real, "escape/secret.cfg"));
            File.CreateSymbolicLink(Path.Combine(real, "dangling.cfg"), Path.Combine(root, "missing.cfg"));
            Assert.Throws<InvalidDataException>(() => ModConfigService.ResolveTextPath(real, "dangling.cfg"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FileManager_ValidatesTextAndRejectsXmlEntities()
    {
        Assert.Throws<InvalidDataException>(() => ModConfigService.ValidateText("a.json", "{"));
        Assert.Throws<InvalidDataException>(() => ModConfigService.ValidateText("a.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"));
        Assert.Throws<InvalidDataException>(() => ModConfigService.ValidateText("a.cfg", "a\0b"));
        Assert.Throws<InvalidDataException>(() => ModConfigService.ValidateText("a.cfg", new string('x', 2 * 1024 * 1024 + 1)));
        Assert.NotEmpty(ModConfigService.ValidateText("a.json", "{\"enabled\":true}"));
    }

    [Fact]
    public async Task FileManager_CreatesNestedFilesChecksRevisionsAndProtectsSiblingPackages()
    {
        var root = Temporary();
        try
        {
            await using var provider = Provider(root);
            await Initialize(provider);
            var first = new InstalledMod { Namespace = "Author", Name = "First", FilesJson = "[\"plugins/First.dll\"]" };
            var second = new InstalledMod { Namespace = "Author", Name = "Second", FilesJson = "[\"plugins/Second.dll\"]" };
            var protectedMod = new InstalledMod { Namespace = "Creaton", Name = "Server_Manager", Protected = true };
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                db.InstalledMods.AddRange(first, second, protectedMod);
                await db.SaveChangesAsync();
            }
            var service = provider.GetRequiredService<ModConfigService>();
            await service.MutateFile(first.Id, new("mkdir", "Author-First/recipes", Directory: true));
            await service.MutateFile(first.Id, new("create", "Author-First/recipes/items.json", Content: "{\"wood\":1}"));
            var file = await service.ReadFile(first.Id, "Author-First/recipes/items.json");
            Assert.Equal("{\"wood\":1}", file.Content);
            Assert.Contains((await service.FileTree(first.Id)).Entries, item => item.Path == file.Path && item.Kind == "file");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadFile(second.Id, file.Path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FileTree(protectedMod.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.MutateFile(first.Id, new("create", file.Path, Content: "{}")));
            await service.MutateFile(first.Id, new("save", file.Path, file.Revision, "{\"wood\":2}"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.MutateFile(first.Id, new("save", file.Path, file.Revision, "{}")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.MutateFile(first.Id, new("delete", "Author-First/recipes", Directory: true)));
            file = await service.ReadFile(first.Id, file.Path);
            await service.MutateFile(first.Id, new("rename", file.Path, file.Revision, Destination: "Author-First/recipes/renamed.json"));
            file = await service.ReadFile(first.Id, "Author-First/recipes/renamed.json");
            Assert.Equal("{\"wood\":2}", file.Content);
            await service.MutateFile(first.Id, new("delete", file.Path, file.Revision));
            await service.MutateFile(first.Id, new("delete", "Author-First/recipes", Directory: true));
            Assert.True(await service.HasPending());
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, "config-backups"), "*.cfg", SearchOption.AllDirectories).Count() >= 3);
            await using var finalScope = provider.CreateAsyncScope();
            var audit = await finalScope.ServiceProvider.GetRequiredService<ManagerDbContext>().AuditRecords.ToListAsync();
            Assert.DoesNotContain(audit, record => record.Detail.Contains("wood"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Temporary()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-inspection-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static ServiceProvider Provider(string root, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["VSM_DATA_PATH"] = root, ["VSM_BEPINEX_PATH"] = Path.Combine(root, "BepInEx"),
            ["VSM_SAVE_PATH"] = Path.Combine(root, "worlds"), ["SERVER_NAME"] = "Test realm", ["SERVER_WORLD"] = "SameWorld"
        };
        foreach (var pair in extra ?? []) values[pair.Key] = pair.Value;
        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")};Pooling=False"));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys"))).SetApplicationName("vsm-regression-tests");
        services.AddSingleton<ServerSettingsService>();
        services.AddSingleton<ServerCharacterSettingsService>();
        services.AddSingleton<ClientModManifestService>();
        services.AddSingleton<PluginRegistryService>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<AuditService>();
        services.AddSingleton(new ServerState(null!));
        services.AddSingleton(provider => new ModConfigService(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<PluginRegistryService>(), provider.GetRequiredService<ServerState>(), null!, null!,
            provider.GetRequiredService<AuditService>(), provider.GetRequiredService<IConfiguration>()));
        return services.BuildServiceProvider();
    }

    internal static async Task Initialize(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
    }
}
