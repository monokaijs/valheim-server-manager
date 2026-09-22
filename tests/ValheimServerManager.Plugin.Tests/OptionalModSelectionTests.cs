using System.Text;
using ValheimServerManager.Bootstrap;
using ValheimServerManager.ClientSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class OptionalModSelectionTests
{
    [Fact]
    public void RequiredPackages_CannotBeOmittedAndOptionalGroupsDefaultOff()
    {
        var catalog = Catalog();
        var none = Effective(catalog, []);
        Assert.Single(none.Packages);
        Assert.Equal(catalog.Revision, none.Revision);
        var selected = Effective(catalog, ["Author-Optional"]);
        Assert.Equal(3, selected.Packages.Count);
        Assert.Contains(selected.Packages, item => item.PackageName == "Required");
        Assert.Contains(selected.Packages, item => item.PackageName == "Dependency");
        Assert.NotEqual(none.Revision, selected.Revision);
        Assert.Equal(none.Revision, Effective(catalog, []).Revision); // Opt-out returns to the required set.
    }

    [Fact]
    public void OptionalGroups_CannotOverrideRequiredVersionsOrHashes()
    {
        var catalog = Catalog();
        catalog.OptionalGroups[0].Packages.Add(Package("Required", "2.0.0"));
        Assert.Throws<InvalidDataException>(() => Effective(catalog, ["Author-Optional"]));
        catalog = Catalog();
        var differentDigest = Package("Required");
        differentDigest.Sha256 = new string('b', 64);
        catalog.OptionalGroups[0].Packages.Add(differentDigest);
        Assert.Throws<InvalidDataException>(() => Effective(catalog, ["Author-Optional"]));
    }

    [Fact]
    public void OptionalPreferences_AreExplicitAndIsolatedByServerIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-optional-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Catalog();
            var b = Catalog(); b.ManifestId = "vsm:another-server-same-world-name";
            Assert.Empty(ClientModSelection.Load(root, a));
            ClientModSelection.Save(root, a, ["Author-Optional", "unknown-package"]);
            Assert.Equal(["Author-Optional"], ClientModSelection.Load(root, a));
            Assert.Empty(ClientModSelection.Load(root, b));
            Assert.NotEqual(ClientModSelection.PreferencesPath(root, a.ManifestId), ClientModSelection.PreferencesPath(root, b.ManifestId));
            ClientModSelection.Save(root, a, []);
            Assert.Empty(ClientModSelection.Load(root, a));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacySchemaOne_IsStillAcceptedWithoutOptionalMetadata()
    {
        var manifest = new BootstrapManifest { SchemaVersion = 1, ManifestId = "old-server", Revision = new string('a', 64), GeneratedAt = DateTimeOffset.UtcNow.ToString("O"), Packages = [Package("Required")], Configs = [] };
        var catalog = ClientModSelection.Parse(Encoding.UTF8.GetString(Json.Write(manifest)));
        Assert.Empty(catalog.OptionalGroups);
        Assert.False(catalog.RequiredReceipt);
        Assert.Single(Effective(catalog, []).Packages);
    }

    private static BootstrapManifest Effective(ClientModCatalog catalog, string[] choices) => Json.Read<BootstrapManifest>(Encoding.UTF8.GetBytes(ClientModSelection.EffectiveManifest(catalog, choices)));
    private static ManifestPackage Package(string name, string version = "1.0.0") => new() { Namespace = "Author", PackageName = name, VersionNumber = version, Coordinate = "Author-" + name + "-" + version, Sha256 = new string('a', 64), Dependencies = [] };
    private static ClientModCatalog Catalog() => new()
    {
        SchemaVersion = 1, ManifestId = "vsm:server-one", Revision = new string('a', 64), GeneratedAt = DateTimeOffset.UtcNow.ToString("O"), Packages = [Package("Required")], Configs = [],
        OptionalGroups = [new OptionalModChoice { Id = "Author-Optional", Name = "An optional mod", Packages = [Package("Optional"), Package("Dependency")] }]
    };
}
