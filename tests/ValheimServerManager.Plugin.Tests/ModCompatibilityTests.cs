using ValheimServerManager.ClientSupport;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class ModCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vsm-mod-status-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactInstalledVersionCanAcknowledgeRequiredMods()
    {
        Install("Author", "Required", "1.2.3");
        var manifestPath = Path.Combine(_root, "plugins", "Author-Required", "manifest.json");
        var original = File.ReadAllBytes(manifestPath);
        var status = ClientModCompatibility.Check(Catalog(), _root);
        Assert.True(status.RequiredPresent);
        Assert.True(status.RequiredReceipt);
        Assert.True(status.Required[0].Present);
        Assert.False(status.Optional[0].Packages[0].Present);
        Assert.Equal(original, File.ReadAllBytes(manifestPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "valheim-server-manager")));
    }

    [Fact]
    public void MissingOrDifferentVersionDoesNotAcknowledgeRequiredMods()
    {
        Assert.False(ClientModCompatibility.Check(Catalog(), _root).RequiredPresent);
        Install("Author", "Required", "1.2.4");
        var status = ClientModCompatibility.Check(Catalog(), _root);
        Assert.False(status.RequiredPresent);
        Assert.Equal("1.2.4", status.Required[0].InstalledVersion);
    }

    [Fact]
    public void ServerCannotUsePackageIdentityToReadOutsideProfile()
    {
        Assert.Throws<InvalidDataException>(() => ClientModCompatibility.Check(
            Catalog().Replace("Author-Required-1.2.3", "../x-Required-1.2.3")
                .Replace("\"namespace\":\"Author\"", "\"namespace\":\"../x\""), _root));
    }

    private void Install(string packageNamespace, string name, string version)
    {
        var directory = Path.Combine(_root, "plugins", packageNamespace + "-" + name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\"}");
    }

    private static string Catalog() => "{\"schemaVersion\":1,\"revision\":\"" + new string('a', 64)
        + "\",\"requiredReceipt\":true,\"packages\":[{\"coordinate\":\"Author-Required-1.2.3\",\"namespace\":\"Author\",\"packageName\":\"Required\",\"versionNumber\":\"1.2.3\"}],"
        + "\"optionalGroups\":[{\"name\":\"Author/Optional\",\"packages\":[{\"coordinate\":\"Author-Optional-1.0.0\",\"namespace\":\"Author\",\"packageName\":\"Optional\",\"versionNumber\":\"1.0.0\"}]}]}";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
