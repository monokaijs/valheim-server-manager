using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerManager.Data;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class ClientPolicyTests
{
    [Theory]
    [InlineData("true", "required")]
    [InlineData("false", "serverOnly")]
    [InlineData("True", "required")]
    [InlineData("False", "serverOnly")]
    [InlineData("optional", "optional")]
    public void LegacyPolicyBooleans_KeepTheirMeaning(string stored, string expected)
    {
        var mod = Mod("Example");
        Assert.Equal(expected, ClientModManifestService.ReadPolicy(mod, new Dictionary<string, string> { ["client-mod-sync:" + mod.Id.ToString("N")] = stored }));
    }

    [Fact]
    public void DependencyClosure_IsCycleSafeAndRejectsMissingOrDisabledDependencies()
    {
        var a = Mod("A", "Author-B-1.0.0");
        var b = Mod("B", "Author-A-1.0.0");
        Assert.Equal(2, ClientModManifestService.Closure([a], [a, b]).Count);
        Assert.Throws<InvalidOperationException>(() => ClientModManifestService.Closure([a], [a]));
        b.Enabled = false;
        Assert.Throws<InvalidOperationException>(() => ClientModManifestService.Closure([a], [a, b]));
    }

    [Fact]
    public async Task OptionalCatalog_DoesNotChangeRequiredRevisionAndIncludesDependencyClosure()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-mod-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        // Release validation runs after set-version.sh, so fixtures follow the built manager.
        var runtimeVersion = typeof(ClientModManifestService).Assembly.GetName().Version!.ToString(3);
        await File.WriteAllBytesAsync(Path.Combine(root, "runtime", $"ValheimServerManager-{runtimeVersion}-client.zip"), [1, 2, 3]);
        try
        {
            await using var provider = InspectionAndFilesTests.Provider(root);
            await InspectionAndFilesTests.Initialize(provider);
            var required = Mod("Required", "Author-Dependency-1.0.0");
            var optional = Mod("Optional", "Author-OptionalDependency-1.0.0");
            var dependency = Mod("Dependency");
            var optionalDependency = Mod("OptionalDependency");
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                db.InstalledMods.AddRange(required, optional, dependency, optionalDependency);
                await db.SaveChangesAsync();
            }
            var service = provider.GetRequiredService<ClientModManifestService>();
            await service.SetPolicy(optional.Id, "optional");
            await service.SetPolicy(dependency.Id, "serverOnly");
            await service.SetPolicy(optionalDependency.Id, "serverOnly");
            var policies = await service.Policies();
            Assert.Equal("required", policies[dependency.Id].Effective);
            Assert.Equal("serverOnly", policies[dependency.Id].Policy);
            Assert.Contains("Author/Required", policies[dependency.Id].RequiredBy);
            using var first = JsonDocument.Parse(await service.BuildJson());
            Assert.Equal(3, first.RootElement.GetProperty("packages").GetArrayLength()); // runtime + required + dependency
            var runtime = first.RootElement.GetProperty("packages")[0];
            Assert.Equal("Creaton", runtime.GetProperty("namespace").GetString());
            Assert.Equal("Server_Manager", runtime.GetProperty("packageName").GetString());
            Assert.Equal(runtimeVersion, runtime.GetProperty("versionNumber").GetString());
            Assert.Equal($"Creaton-Server_Manager-{runtimeVersion}", runtime.GetProperty("coordinate").GetString());
            var group = Assert.Single(first.RootElement.GetProperty("optionalGroups").EnumerateArray());
            Assert.Equal(2, group.GetProperty("packages").GetArrayLength());
            var revision = first.RootElement.GetProperty("revision").GetString();
            var identity = first.RootElement.GetProperty("manifestId").GetString();
            Assert.True(first.RootElement.GetProperty("inventoryInspectionRequired").GetBoolean());
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                (await db.InstalledMods.FindAsync(optional.Id))!.Version = "1.1.0";
                await db.SaveChangesAsync();
            }
            using var changedOptional = JsonDocument.Parse(await service.BuildJson());
            Assert.Equal(revision, changedOptional.RootElement.GetProperty("revision").GetString());
            Assert.Equal(identity, changedOptional.RootElement.GetProperty("manifestId").GetString());
            Assert.NotEqual(first.RootElement.GetProperty("optionalRevision").GetString(), changedOptional.RootElement.GetProperty("optionalRevision").GetString());
            await service.SetPolicy(optional.Id, "required");
            using var changedRequired = JsonDocument.Parse(await service.BuildJson());
            Assert.NotEqual(revision, changedRequired.RootElement.GetProperty("revision").GetString());
            Assert.Empty(changedRequired.RootElement.GetProperty("optionalGroups").EnumerateArray());
            await Assert.ThrowsAsync<ArgumentException>(() => service.SetPolicy(optional.Id, "some-random-policy"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static InstalledMod Mod(string name, params string[] dependencies) => new()
    { Namespace = "Author", Name = name, Source = "thunderstore", Enabled = true, Version = "1.0.0", Sha256 = new string('a', 64), DependenciesJson = JsonSerializer.Serialize(dependencies) };
}
