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
    public void SameVersionRebuildCannotReplaceInstalledPlugin()
    {
        var manifest = Manifest();
        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(manifest)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);
        var previous = Json.ReadFile<BootstrapState>(_context.StatePath);
        var live = Path.Combine(_root, "plugins", "ValheimServerManagerManaged", "Example-Mod", "Mod.dll");
        var original = File.ReadAllText(live);
        manifest.Packages[0].Sha256 = new string('b', 64);
        manifest.Revision = new string('b', 64);
        Assert.Throws<InvalidOperationException>(() => BootstrapSynchronizer.StageManifestLocked(_context, previous, Json.Write(manifest)));
        Assert.Equal(original, File.ReadAllText(live));
        Assert.Equal(previous.Revision, Json.ReadFile<BootstrapState>(_context.StatePath).Revision);
        Assert.False(File.Exists(_context.PendingManifestPath));
    }

    [Fact]
    public void ConfigRepairCannotOverwriteLocalConfig()
    {
        var manifest = Manifest();
        var content = System.Text.Encoding.UTF8.GetBytes("new config");
        manifest.Configs.Add(new ManifestConfig { Path = "test.cfg", ContentBase64 = Convert.ToBase64String(content), Sha256 = Hash(content) });
        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(manifest)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);
        var previous = Json.ReadFile<BootstrapState>(_context.StatePath);
        var config = Path.Combine(_root, "config", "test.cfg");
        File.WriteAllText(config, "old config");
        Assert.Throws<InvalidOperationException>(() => BootstrapSynchronizer.StageManifestLocked(_context, previous, Json.Write(manifest)));
        Assert.Equal("old config", File.ReadAllText(config));
        Assert.False(File.Exists(_context.PendingManifestPath));
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
        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(manifest)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);
        File.WriteAllText(_context.PendingManifestPath, "old server update");
        Assert.False(BootstrapSynchronizer.StageManifestLocked(_context, Json.ReadFile<BootstrapState>(_context.StatePath), Json.Write(manifest)).Changed);
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
    public void ServerManagerCannotBeInstalledByItsOwnBootstrap()
    {
        var manifest = RuntimeManifest();
        Assert.Throws<InvalidDataException>(() => BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(manifest)));
        Assert.False(File.Exists(_context.PendingManifestPath));
    }

    [Fact]
    public void InstalledPackageCannotBeRemovedByLaterManifest()
    {
        var installed = Manifest();
        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(installed)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);
        var empty = Manifest();
        empty.Packages.Clear();
        empty.Revision = new string('d', 64);
        Assert.Throws<InvalidOperationException>(() => BootstrapSynchronizer.StageManifestLocked(
            _context, Json.ReadFile<BootstrapState>(_context.StatePath), Json.Write(empty)));
    }

    [Fact]
    public void NewPackageCanBeAddedWithoutChangingInstalledPackage()
    {
        var first = Manifest();
        Assert.True(BootstrapSynchronizer.StageManifestLocked(_context, new BootstrapState(), Json.Write(first)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);
        var original = Path.Combine(_root, "plugins", "ValheimServerManagerManaged", "Example-Mod", "Mod.dll");
        var originalBytes = File.ReadAllBytes(original);

        var next = Manifest();
        var extra = Manifest().Packages[0];
        extra.Namespace = "More";
        extra.Coordinate = "More-Mod-1.0.0";
        next.Packages.Add(extra);
        next.Revision = new string('e', 64);
        Assert.True(BootstrapSynchronizer.StageManifestLocked(
            _context, Json.ReadFile<BootstrapState>(_context.StatePath), Json.Write(next)).Changed);
        BootstrapSynchronizer.ApplyPendingLocked(_context);

        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.True(File.Exists(Path.Combine(_root, "plugins", "ValheimServerManagerManaged", "More-Mod", "Mod.dll")));
    }

    [Theory]
    [InlineData("patchers/ValheimServerManagerBootstrap.dll")]
    [InlineData("patchers/Server_Manager/ValheimServerManagerBootstrap.dll")]
    [InlineData("patchers/vendor/package/ValheimServerManagerBootstrap.dll")]
    public void BootstrapFindsBepInExRootFromNestedModManagerFolders(string relativeAssemblyPath)
    {
        var bepInEx = Path.Combine(_root, "BepInEx");
        var assemblyPath = Path.Combine(bepInEx, relativeAssemblyPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        Assert.Equal(bepInEx, BootstrapSynchronizer.ResolveBepInExRoot(assemblyPath));
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
