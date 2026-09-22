using System.IO.Compression;
using System.Security.Cryptography;
using ValheimServerManager.Bootstrap;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class StagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vsm-staging-tests-" + Guid.NewGuid().ToString("N"));
    private readonly BootstrapSynchronizer.BootstrapContext _context;

    public StagingTests()
    {
        Directory.CreateDirectory(_root);
        _context = new(_root, _root, Path.Combine(_root, "state.json"), Path.Combine(_root, "last.json"),
            Path.Combine(_root, "pending.json"), new BootstrapSettings());
    }

    [Fact]
    public void SameVersionRebuildIsStagedWithoutReplacingLoadedPlugin()
    {
        var manifest = Manifest();
        var previous = State(manifest);
        previous.Revision = new string('b', 64);
        var live = Path.Combine(_root, "plugins", "ValheimServerManagerManaged", "Example-Mod", "Mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        File.WriteAllText(live, "currently loaded DLL");
        Json.WriteFile(_context.StatePath, previous);

        var progress = new List<string>();
        var result = BootstrapSynchronizer.StageManifestLocked(_context, previous, Json.Write(manifest), default, progress.Add);

        Assert.True(result.Changed);
        Assert.False(result.PackagesChanged);
        Assert.Equal("currently loaded DLL", File.ReadAllText(live));
        Assert.Equal(previous.Revision, Json.ReadFile<BootstrapState>(_context.StatePath).Revision);
        Assert.Equal(manifest.Revision, Json.ReadFile<BootstrapManifest>(_context.PendingManifestPath).Revision);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public void ConfigRepairIsStagedWithoutModifyingLiveConfig()
    {
        var manifest = Manifest();
        var content = System.Text.Encoding.UTF8.GetBytes("new config");
        manifest.Configs.Add(new ManifestConfig { Path = "test.cfg", ContentBase64 = Convert.ToBase64String(content), Sha256 = Hash(content) });
        var previous = State(manifest);
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "ValheimServerManagerManaged"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        var config = Path.Combine(_root, "config", "test.cfg");
        File.WriteAllText(config, "old config");
        previous.ManagedConfigs.Add("test.cfg");
        previous.ManagedConfigHashes.Add("test.cfg", Hash(content));

        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, previous, Json.Write(manifest)).Changed);
        Assert.Equal("old config", File.ReadAllText(config));
        Assert.True(File.Exists(_context.PendingManifestPath));
    }

    [Fact]
    public void CancellationDoesNotStageAnOldServersManifest()
    {
        var manifest = Manifest();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => BootstrapSynchronizer.StageManifestLocked(
            _context, new BootstrapState(), Json.Write(manifest), cancellation.Token, _ => cancellation.Cancel()));
        Assert.False(File.Exists(_context.PendingManifestPath));
        Assert.False(File.Exists(_context.PendingManifestPath + ".new"));
    }

    [Fact]
    public void ReturningToInstalledServerClearsSupersededPendingManifest()
    {
        var manifest = Manifest();
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "ValheimServerManagerManaged"));
        File.WriteAllText(_context.PendingManifestPath, "old server update");
        Assert.False(BootstrapSynchronizer.StageManifestLocked(_context, State(manifest), Json.Write(manifest)).Changed);
        Assert.False(File.Exists(_context.PendingManifestPath));
    }

    [Fact]
    public void InvalidPackageCannotBecomeThePendingUpdate()
    {
        var manifest = Manifest();
        manifest.Packages[0].Sha256 = new string('0', 64);
        Assert.Throws<InvalidDataException>(() => BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(manifest)));
        Assert.False(File.Exists(_context.PendingManifestPath));
    }

    [Fact]
    public void ServerManagerRuntimeUpdatesItsUpdaterBeforePluginLoading()
    {
        var manifest = RuntimeManifest();
        var stableUpdater = Path.Combine(_root, "plugins", "ValheimServerManager", "ValheimServerManagerRuntimeUpdater.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(stableUpdater)!);
        File.WriteAllText(stableUpdater, "old updater");

        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(manifest)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);

        Assert.Equal("new updater", File.ReadAllText(stableUpdater));
        Assert.True(File.Exists(Path.Combine(_root, "plugins", "ValheimServerManagerManaged", "Creaton-Server_Manager", "ValheimServerManager", "ValheimServerManager.Client.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "plugins", "ValheimServerManagerManaged", "Creaton-Server_Manager", "ValheimServerManager", "ValheimServerManagerRuntimeUpdater.dll")));
        var state = Json.ReadFile<BootstrapState>(_context.StatePath);
        Assert.Contains("plugins/ValheimServerManager/ValheimServerManagerRuntimeUpdater.dll", state.InfrastructureHashes.Keys);

        File.WriteAllText(stableUpdater, "tampered updater");
        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, state, Json.Write(manifest)).Changed);
    }

    private static BootstrapManifest Manifest()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write("{\"name\":\"Mod\",\"version_number\":\"1.0.0\"}");
            using (var writer = new StreamWriter(archive.CreateEntry("BepInEx/plugins/Mod.dll").Open()))
                writer.Write("replacement DLL");
        }
        var bytes = stream.ToArray();
        return new BootstrapManifest
        {
            SchemaVersion = 1, ManifestId = "server-a", Revision = new string('a', 64), GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            Packages = [new ManifestPackage
            {
                Coordinate = "Example-Mod-1.0.0", Namespace = "Example", PackageName = "Mod", VersionNumber = "1.0.0",
                ContentBase64 = Convert.ToBase64String(bytes), FileSize = bytes.Length, Sha256 = Hash(bytes)
            }]
        };
    }

    private static BootstrapManifest RuntimeManifest()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write("{\"name\":\"Server_Manager\",\"version_number\":\"2.1.0\"}");
            using (var writer = new StreamWriter(archive.CreateEntry("BepInEx/plugins/ValheimServerManager/ValheimServerManagerRuntimeUpdater.dll").Open()))
                writer.Write("new updater");
            using (var writer = new StreamWriter(archive.CreateEntry("BepInEx/plugins/ValheimServerManager/ValheimServerManager.Client.dll").Open()))
                writer.Write("new client");
        }
        var bytes = stream.ToArray();
        return new BootstrapManifest
        {
            SchemaVersion = 1, ManifestId = "server-a", Revision = new string('c', 64), GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            Packages = [new ManifestPackage
            {
                Coordinate = "Creaton-Server_Manager-2.1.0", Namespace = "Creaton", PackageName = "Server_Manager", VersionNumber = "2.1.0",
                ContentBase64 = Convert.ToBase64String(bytes), FileSize = bytes.Length, Sha256 = Hash(bytes)
            }]
        };
    }

    private static BootstrapState State(BootstrapManifest manifest) => new()
    {
        ManifestId = manifest.ManifestId, Revision = manifest.Revision, GeneratedAt = manifest.GeneratedAt,
        Packages = manifest.Packages.Select(package => package.Coordinate).ToList()
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose() => Directory.Delete(_root, true);
}
