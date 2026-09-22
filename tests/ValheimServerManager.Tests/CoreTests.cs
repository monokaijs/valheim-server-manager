using ValheimServerManager.Services;
using ValheimServerManager.Models;
using ValheimServerManager.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("1.6.1", "1.6.0", true)]
    [InlineData("v2.0.0", "1.99.99", true)]
    [InlineData("1.6.0", "1.6.0", false)]
    [InlineData("1.5.9", "1.6.0", false)]
    public void ManagerUpdates_CompareSemanticVersions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, ManagerUpdateService.IsNewer(candidate, current));
    }

    [Fact]
    public void ConsoleTokenizer_PreservesQuotedArguments()
    {
        var tokens = SafeConsoleService.Tokenize("broadcast \"The server restarts soon\"");
        Assert.Equal(["broadcast", "The server restarts soon"], tokens);
    }

    [Fact]
    public void ConsoleTokenizer_RejectsUnterminatedQuote()
    {
        Assert.Throws<ArgumentException>(() => SafeConsoleService.Tokenize("broadcast \"oops"));
    }

    [Fact]
    public void ServerMessages_RenderOnlySupportedPlaceholders()
    {
        var templates = ServerMessageService.Validate(ServerMessageService.Defaults with
        {
            Kick = "Bye {player} from {server}: {reason}"
        });
        Assert.Equal("Bye Viking from The Hall: Griefing",
            ServerMessageService.RenderTemplate(templates.Kick, "The Hall", "Viking", "Griefing", 0));
        Assert.Throws<ArgumentException>(() => ServerMessageService.Validate(templates with { Kick = "{unknown}" }));
    }

    [Theory]
    [InlineData(null, "No reason provided.")]
    [InlineData("", "No reason provided.")]
    [InlineData("  griefing  ", "griefing")]
    public void ModerationReasons_AreNormalized(string? input, string expected)
    {
        Assert.Equal(expected, ServerMessageService.NormalizeReason(input));
    }

    [Fact]
    public void ModerationReasons_RejectControlCharactersAndOversizeText()
    {
        Assert.Throws<ArgumentException>(() => ServerMessageService.NormalizeReason("line one\nline two"));
        Assert.Throws<ArgumentException>(() => ServerMessageService.NormalizeReason(new string('x', 301)));
    }

    [Fact]
    public void WebhookSignature_IsStableHmacSha256()
    {
        Assert.Equal("c1afc7c2df3db0690d7d75954610ed1a1d959ce96355ccb8c0a8bc09fd0cfc27", WebhookDispatcher.Sign("secret", "1700000000.{\"ok\":true}"));
    }

    [Theory]
    [InlineData("../plugins/evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("BepInEx/plugins/../../evil.dll")]
    public void ArchiveSafety_RejectsEscapingPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.NormalizeEntry(path));
    }

    [Fact]
    public void ArchiveSafety_PreservesSafePackagePath()
    {
        Assert.Equal("BepInEx/plugins/Author.Mod.dll", ArchiveSafety.NormalizeEntry("BepInEx/plugins/Author.Mod.dll"));
        Assert.True(ArchiveSafety.IsSymbolicLink(0xA000 << 16));
    }

    [Theory]
    [InlineData("denikson", "BepInExPack_Valheim", true)]
    [InlineData("DENIKSON", "bepinexpack_valheim", true)]
    [InlineData("", "BepInExPack_Valheim", true)]
    [InlineData("Creaton", "Server_Manager", true)]
    [InlineData("SomeAuthor", "Server_Manager", false)]
    [InlineData("Author", "GameplayMod", false)]
    public void ModInstaller_RecognizesContainerManagedPackages(string packageNamespace, string name, bool expected)
    {
        Assert.Equal(expected, ModService.IsBundledInfrastructure(packageNamespace, name));
    }

    [Theory]
    [InlineData("5.4.2350", "5.4.2202", true)]
    [InlineData("5.4.2350", "5.4.2350", true)]
    [InlineData("5.4.2202", "5.4.2350", false)]
    [InlineData("custom", "custom", true)]
    [InlineData("custom", "other", false)]
    public void ModInstaller_ValidatesBundledDependencyVersions(string installed, string required, bool expected)
    {
        Assert.Equal(expected, ModService.VersionAtLeast(installed, required));
    }

    [Fact]
    public void ModInstaller_ParsesThunderstoreDependencyCoordinates()
    {
        var dependency = ModService.ParseDependency("some-author-Useful_Mod-1.2.3");
        Assert.Equal("some-author", dependency.Namespace);
        Assert.Equal("Useful_Mod", dependency.Name);
        Assert.Equal("1.2.3", dependency.Version);
        Assert.Throws<InvalidDataException>(() => ModService.ParseDependency("missing-version"));
    }

    [Theory]
    [InlineData("Advize_PlantEverything.dll", "plugins/Advize-PlantEverything/Advize_PlantEverything.dll")]
    [InlineData("assets/pieces.json", "plugins/Advize-PlantEverything/assets/pieces.json")]
    [InlineData("plugins/Example.dll", "plugins/Example.dll")]
    [InlineData("config/author.mod.cfg", "config/author.mod.cfg")]
    [InlineData("BepInEx/plugins/Example.dll", "plugins/Example.dll")]
    [InlineData("Package/BepInEx/patchers/Example.dll", "patchers/Example.dll")]
    public void ModInstaller_MapsSupportedThunderstoreLayouts(string entry, string expected)
    {
        Assert.Equal(expected, ModService.PackageDestination(entry, "Advize", "PlantEverything"));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    [InlineData("icon.png")]
    [InlineData("LICENSE")]
    public void ModInstaller_DoesNotInstallPackageMetadata(string entry)
    {
        Assert.Null(ModService.PackageDestination(entry, "Advize", "PlantEverything"));
    }

    [Fact]
    public void ModInstaller_ReadsThunderstoreOwnerAsNamespace()
    {
        var package = JsonSerializer.Deserialize<ModService.ThunderstorePackage>("""
            {"owner":"Advize","name":"PlantEverything","full_name":"Advize-PlantEverything","versions":[]}
            """);
        Assert.NotNull(package);
        Assert.Equal("Advize", package.Namespace);
    }

    [Fact]
    public async Task ModInstaller_InstallsRootDllAndSatisfiesBundledBepInExDependency()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-root-package-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "PlantEverything.zip");
        var bepInEx = Path.Combine(root, "BepInEx");
        Directory.CreateDirectory(bepInEx);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using (var output = manifest.Open())
                await JsonSerializer.SerializeAsync(output, new { name = "PlantEverything", version_number = "1.21.2", dependencies = new[] { "denikson-BepInExPack_Valheim-5.4.2350" } });
            archive.CreateEntry("README.md");
            archive.CreateEntry("icon.png");
            var plugin = archive.CreateEntry("Advize_PlantEverything.dll");
            await using var pluginOutput = plugin.Open();
            await pluginOutput.WriteAsync(new byte[] { 1, 2, 3, 4 });
        }

        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VSM_BEPINEX_PATH"] = bepInEx,
            ["VSM_DATA_PATH"] = Path.Combine(root, "manager"),
            ["BEPINEX_PACK_VERSION"] = "5.4.2350"
        }).Build();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var audit = new AuditService(scopes, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        var service = new ModService(scopes, null!, new ServerState(null!), null!, null!, null!, audit, configuration);

        await using (var input = File.OpenRead(archivePath))
            await service.InstallUpload(input, "PlantEverything.zip", CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(bepInEx, "plugins", "manual-PlantEverything", "Advize_PlantEverything.dll")));
        Assert.False(File.Exists(Path.Combine(bepInEx, "plugins", "manual-PlantEverything", "manifest.json")));
        await using (var scope = provider.CreateAsyncScope())
            Assert.Equal("1.21.2", (await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().InstalledMods.SingleAsync()).Version);
        Directory.Delete(root, true);
    }

    [Theory]
    [InlineData("Steam_76561198000000000")]
    [InlineData("PlayFab_AaBbCc123")]
    [InlineData("76561198000000000")]
    public void PlatformIds_AcceptExactCaseSensitiveValues(string id)
    {
        AccessListService.ValidateId(id);
    }

    [Theory]
    [InlineData("76561198400688240", "Steam_76561198400688240")]
    [InlineData("  76561198400688240  ", "Steam_76561198400688240")]
    [InlineData("Steam_76561198400688240", "Steam_76561198400688240")]
    [InlineData("PlayFab_UserAbc", "PlayFab_UserAbc")]
    public void AccessLists_NormalizeNumericSteamIdentities(string input, string expected)
    {
        Assert.Equal(expected, AccessListService.NormalizePlatformId(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Steam user")]
    [InlineData("../7656119")]
    public void PlatformIds_RejectMalformedValues(string id)
    {
        Assert.Throws<ArgumentException>(() => AccessListService.ValidateId(id));
    }

    [Theory]
    [InlineData("76561198400688240", "Steam_76561198400688240")]
    [InlineData("Steam_76561198400688240", "Steam_76561198400688240")]
    [InlineData("PlayFab_UserAbc", "PlayFab_UserAbc")]
    public void JoinRequests_NormalizePlatformIdentity(string input, string expected)
    {
        Assert.Equal(expected, JoinRequestService.NormalizePlatformId(input));
    }

    [Fact]
    public void ApiTokens_ExposeSeparateJoinRequestScopes()
    {
        Assert.Contains("join-requests.read", ApiTokenService.AllowedScopes);
        Assert.Contains("join-requests.write", ApiTokenService.AllowedScopes);
    }

    [Fact]
    public async Task JoinRequests_DeduplicatePendingAttemptsPerParticipant()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-join-requests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
        var service = new JoinRequestService(provider.GetRequiredService<IServiceScopeFactory>(), new Microsoft.AspNetCore.Http.HttpContextAccessor());

        await service.Record("76561198400688240", "FirstName");
        await service.Record("Steam_76561198400688240", "CurrentName");

        var requests = await service.List("pending");
        var request = Assert.Single(requests);
        Assert.Equal("Steam_76561198400688240", request.PlatformId);
        Assert.Equal("CurrentName", request.PlayerName);
        Assert.Equal(2, request.AttemptCount);
        Directory.Delete(root, true);
    }

    [Fact]
    public void ModConfigParser_ProvidesMetadataMasksSecretsAndPreservesFormatting()
    {
        const string source = """
            [General]

            ## Description: Turn the feature on.
            # Setting type: Boolean
            # Default value: true
            Enabled = true

            ## Description: Private API credential.
            # Setting type: String
            ApiToken = do-not-return-this
            """;
        var entries = ModConfigService.ParseText(source, maskSensitive: true);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Turn the feature on.", entries[0].Description);
        Assert.Equal("Boolean", entries[0].SettingType);
        Assert.True(entries[1].Sensitive);
        Assert.True(entries[1].HasValue);
        Assert.Equal("", entries[1].Value);

        var changed = ModConfigService.ApplyValues(source, new Dictionary<string, string>
        {
            ["General\0Enabled"] = "false"
        });
        Assert.Contains("Enabled = false", changed);
        Assert.Contains("ApiToken = do-not-return-this", changed);
        Assert.Contains("## Description: Turn the feature on.", changed);
    }

    [Fact]
    public void ModConfigPaths_StayInsideBepInExConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-config-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.Equal(Path.Combine(root, "author.plugin.cfg"), ModConfigService.ResolveSafePath(root, "author.plugin.cfg"));
        Assert.Throws<InvalidDataException>(() => ModConfigService.ResolveSafePath(root, "../outside.cfg"));
        Assert.Throws<InvalidDataException>(() => ModConfigService.ResolveSafePath(root, "plugin.json"));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ModConfigs_MapRuntimePluginGuidThroughOwnedDll()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-config-map-" + Guid.NewGuid().ToString("N"));
        var bepInEx = Path.Combine(root, "BepInEx");
        Directory.CreateDirectory(Path.Combine(bepInEx, "config"));
        await File.WriteAllTextAsync(Path.Combine(bepInEx, "config", "author.example.cfg"), "[General]\nEnabled = true\n");
        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        await using var provider = services.BuildServiceProvider();
        Guid modId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            await db.Database.EnsureCreatedAsync();
            var mod = new InstalledMod { Namespace = "Author", Name = "Example", Version = "1.0.0", FilesJson = "[\"plugins/Author/Example.dll\"]" };
            db.InstalledMods.Add(mod);
            db.ManagerSettings.Add(new ManagerSetting
            {
                Key = "plugins.registry",
                Value = "[{\"guid\":\"author.example\",\"name\":\"Example\",\"version\":\"1.0.0\",\"dll\":\"plugins/Author/Example.dll\",\"configFile\":\"author.example.cfg\"}]"
            });
            await db.SaveChangesAsync();
            modId = mod.Id;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VSM_BEPINEX_PATH"] = bepInEx,
            ["VSM_DATA_PATH"] = Path.Combine(root, "manager")
        }).Build();
        var registry = new PluginRegistryService(provider.GetRequiredService<IServiceScopeFactory>());
        var configs = new ModConfigService(provider.GetRequiredService<IServiceScopeFactory>(), registry, null!, null!, null!, null!, configuration);

        var files = await configs.List(modId);
        var file = Assert.Single(files);
        Assert.Equal("author.example.cfg", file.File);
        Assert.Equal("author.example", file.PluginGuid);
        Assert.Equal("Enabled", Assert.Single(file.Entries).Key);
        Directory.Delete(root, true);
    }

    [Fact]
    public void SteamAdminAuthorization_AcceptsCanonicalOrNumericIdOnly()
    {
        const string steamId = "76561198874901279";
        Assert.True(AccessListService.MatchesSteamAdmin(["Steam_" + steamId], steamId));
        Assert.True(AccessListService.MatchesSteamAdmin([steamId], steamId));
        Assert.False(AccessListService.MatchesSteamAdmin(["steam_" + steamId], steamId));
        Assert.False(AccessListService.MatchesSteamAdmin(["Steam_76561198000000000"], steamId));
    }

    [Theory]
    [InlineData("76561198400688240", "Steam_76561198400688240")]
    [InlineData("Steam_76561198400688240", "Steam_76561198400688240")]
    public void ServerCharacterImport_NormalizesSteamOwner(string input, string expected)
    {
        Assert.Equal(expected, ServerCharacterService.NormalizeSteamId(input));
    }

    [Theory]
    [InlineData("MyHero.fch", "MyHero")]
    [InlineData("Viking One.FCH", "Viking One")]
    public void ServerCharacterImport_UsesSafeNativeFilename(string input, string expected)
    {
        Assert.Equal(expected, ServerCharacterService.CharacterNameFromFile(input));
    }

    [Theory]
    [InlineData("../MyHero.fch")]
    [InlineData("My_Hero.fch")]
    [InlineData("MyHero.zip")]
    public void ServerCharacterImport_RejectsUnsafeOrWrongFiles(string input)
    {
        Assert.Throws<InvalidDataException>(() => ServerCharacterService.CharacterNameFromFile(input));
    }

    [Fact]
    public void ServerCharacterImport_VerifiesNativeProfileSignature()
    {
        var payload = Enumerable.Range(0, 96).Select(value => (byte)value).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(payload.Length);
            writer.Write(payload);
            var signature = System.Security.Cryptography.SHA512.HashData(payload);
            writer.Write(signature.Length);
            writer.Write(signature);
        }
        ServerCharacterService.ValidateNativeProfile(stream.ToArray());
        var tampered = stream.ToArray();
        tampered[8] ^= 0xff;
        Assert.Throws<InvalidDataException>(() => ServerCharacterService.ValidateNativeProfile(tampered));
    }

    [Theory]
    [InlineData("// List admin players ID  ONE per line", false)]
    [InlineData("# generated comment", false)]
    [InlineData("", false)]
    [InlineData("Steam_76561198400688240", true)]
    public void AccessLists_HideCommentsFromDashboard(string line, bool expected)
    {
        Assert.Equal(expected, AccessListService.IsDataLine(line));
    }

    [Theory]
    [InlineData("https://steamcommunity.com/openid/id/76561198874901279", "76561198874901279")]
    [InlineData("http://steamcommunity.com/openid/id/76561199211158687/", "76561199211158687")]
    [InlineData("https://evil.example/openid/id/76561198874901279", null)]
    [InlineData("https://steamcommunity.com/openid/id/123", null)]
    public void SteamClaimedId_RequiresOfficialOriginAndSteam64(string claimedId, string? expected)
    {
        Assert.Equal(expected, SteamAuthService.SteamIdFromClaimedId(claimedId));
    }

    [Fact]
    public void SteamLogin_UsesDiscoveredAuthenticationEndpoint()
    {
        Assert.Equal("https://steamcommunity.com/openid/login", SteamAuthService.Provider);
    }

    [Fact]
    public void EventFilters_SupportExactAndPrefixMatches()
    {
        Assert.True(EventBus.Matches("player.*", "player.joined"));
        Assert.True(EventBus.Matches("server.started", "server.started"));
        Assert.False(EventBus.Matches("chat.*", "player.joined"));
    }

    [Fact]
    public void EventEnvelope_HasVersionAndCorrelationId()
    {
        var envelope = EventEnvelope.Create("server.started", new { }, correlationId: "correlation-1");
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal("correlation-1", envelope.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Id));
    }

    [Fact]
    public void ConsoleAuthorization_RejectsShellAndArbitraryCommands()
    {
        Assert.True(SafeConsoleService.IsAllowedCommand("save"));
        Assert.False(SafeConsoleService.IsAllowedCommand("sh"));
        Assert.False(SafeConsoleService.IsAllowedCommand("devcommands"));
    }

    [Fact]
    public void WebhookBackoff_IsExponentialAndBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WebhookDispatcher.Backoff(1));
        Assert.Equal(TimeSpan.FromHours(1), WebhookDispatcher.Backoff(99));
    }

    [Fact]
    public async Task ClientManifest_EmbedsRuntimeAndRespectsRequiredToggle()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-client-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VSM_DATA_PATH"] = root,
            ["SERVER_WORLD"] = "TestWorld",
            ["VSM_REQUIRE_INVENTORY_INSPECTION"] = "false"
        }).Build());
        services.AddSingleton<ClientModManifestService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.InstalledMods.Add(new InstalledMod
            {
                Namespace = "Author", Name = "GameplayMod", Version = "1.2.3", Source = "thunderstore",
                DependenciesJson = "[]", Sha256 = new string('a', 64), Enabled = true
            });
            await db.SaveChangesAsync();
        }

        var service = provider.GetRequiredService<ClientModManifestService>();
        using var initial = JsonDocument.Parse(await service.BuildJson());
        var packages = initial.RootElement.GetProperty("packages");
        Assert.Single(packages.EnumerateArray());
        Assert.Equal("Author-GameplayMod-1.2.3", packages[0].GetProperty("coordinate").GetString());
        Assert.False(packages[0].TryGetProperty("downloadUrl", out _));
        Assert.False(packages[0].TryGetProperty("contentBase64", out _));
        Assert.False(packages[0].TryGetProperty("sha256", out _));
        Assert.Equal(64, initial.RootElement.GetProperty("revision").GetString()!.Length);
        Assert.True(initial.RootElement.GetProperty("inventoryInspectionRequired").GetBoolean());

        Guid modId;
        await using (var scope = provider.CreateAsyncScope())
            modId = await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().InstalledMods.Select(mod => mod.Id).SingleAsync();
        await service.SetRequired(modId, false);
        using var noRequired = JsonDocument.Parse(await service.BuildJson());
        Assert.True(noRequired.RootElement.GetProperty("requiredReceipt").GetBoolean());
        Assert.Empty(noRequired.RootElement.GetProperty("packages").EnumerateArray());
        await service.SetPolicy(modId, "optional");
        using var optionalAllowed = JsonDocument.Parse(await service.BuildJson());
        Assert.NotEqual(noRequired.RootElement.GetProperty("revision").GetString(), optionalAllowed.RootElement.GetProperty("revision").GetString());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            db.ManagerSettings.Add(new ManagerSetting { Key = "server-characters.enabled", Value = "true" });
            await db.SaveChangesAsync();
        }
        using var stillNoRequired = JsonDocument.Parse(await service.BuildJson());
        Assert.Equal(optionalAllowed.RootElement.GetProperty("revision").GetString(), stillNoRequired.RootElement.GetProperty("revision").GetString());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ServerCharacterSettings_PersistStrictFirstJoinPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-character-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddSingleton<ServerCharacterSettingsService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
        var settings = provider.GetRequiredService<ServerCharacterSettingsService>();

        Assert.Equal(new ServerCharacterSettings(false, true, false, 10, 20), await settings.Get());
        await settings.Set(new ServerCharacterSettings(true, true, true, 20, 30));
        Assert.Equal(new ServerCharacterSettings(true, true, true, 20, 30), await settings.Get());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ServerAccessSettings_ForcePasswordlessServerToUnlistedMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-server-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SERVER_NAME"] = "Test Realm", ["SERVER_WORLD"] = "Dedicated", ["SERVER_PORT"] = "2466",
            ["SERVER_PUBLIC"] = "1", ["SERVER_PASSWORD"] = "", ["VSM_SAVE_PATH"] = root
        }).Build());
        services.AddSingleton<ServerSettingsService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
        var settings = provider.GetRequiredService<ServerSettingsService>();

        var defaults = await settings.Get();
        ServerSettingsMutation Mutation(bool passwordEnabled, string? password, bool manageModifiers = false) => new(
            passwordEnabled, password, true, defaults.ServerName, defaults.WorldName, true, "realm-1",
            900, 8, 3600, 21600, 16, manageModifiers, "immersive", "hard", "hardcore", "more", "less", "hard",
            true, true, true, true);

        await settings.Set(Mutation(false, null));
        var passwordless = (await settings.BuildArguments()).ToList();
        Assert.Equal("", passwordless[passwordless.IndexOf("-password") + 1]);
        Assert.Equal("0", passwordless[passwordless.IndexOf("-public") + 1]);
        Assert.Contains("-crossplay", passwordless);
        Assert.Equal("900", passwordless[passwordless.IndexOf("-saveinterval") + 1]);
        Assert.DoesNotContain("-preset", passwordless);

        await settings.Set(Mutation(true, "different-secret", true));
        var protectedServer = (await settings.BuildArguments()).ToList();
        Assert.Equal("different-secret", protectedServer[protectedServer.IndexOf("-password") + 1]);
        Assert.Equal("1", protectedServer[protectedServer.IndexOf("-public") + 1]);
        Assert.Equal("immersive", protectedServer[protectedServer.IndexOf("-preset") + 1]);
        Assert.Contains("nomap", protectedServer);
        Assert.Equal(16, await settings.GetMaxPlayers());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ApiTokens_AreScopedHashedAndRevocable()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-api-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")}"));
        services.AddSingleton<ApiTokenService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
        var tokens = provider.GetRequiredService<ApiTokenService>();

        var created = await tokens.Create("Discord bot", ["whitelist.write"]);
        Assert.StartsWith("vsm1_", created.Token);
        Assert.DoesNotContain(created.Token, (await tokens.List()).Select(item => item.Prefix));
        Assert.True((await tokens.Authenticate("Bearer " + created.Token, "whitelist.write"))?.Identity?.IsAuthenticated);
        Assert.False((await tokens.Authenticate("Bearer " + created.Token, "whitelist.read"))?.Identity?.IsAuthenticated);
        Assert.Null(await tokens.Authenticate("Bearer " + created.Token + "tampered", "whitelist.write"));
        await tokens.Revoke(created.Id);
        Assert.Null(await tokens.Authenticate("Bearer " + created.Token, "whitelist.write"));
        Directory.Delete(root, true);
    }
}
