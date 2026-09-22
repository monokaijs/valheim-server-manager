using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed class ModService(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    ServerState state,
    AgentGateway agent,
    ProcessSupervisor supervisor,
    ModConfigService modConfigs,
    AuditService audit,
    IConfiguration config)
{
    internal const string BepInExNamespace = "denikson";
    internal const string BepInExPackage = "BepInExPack_Valheim";
    internal const string ServerManagerNamespace = "Creaton";
    internal const string ServerManagerPackage = "Server_Manager";
    private readonly string _bepInEx = config["VSM_BEPINEX_PATH"] ?? "/data/server/BepInEx";
    private readonly string _managerData = config["VSM_DATA_PATH"] ?? "/data/manager";
    private readonly string _bepInExPackVersion = config["BEPINEX_PACK_VERSION"] ?? "5.4.2350";
    private string RollbackRoot => Path.Combine(_managerData, "pending-mod-rollback");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ThunderstorePackage>? _catalog;
    private DateTimeOffset _catalogAt;
    private IReadOnlyDictionary<Guid, string> _availableUpdates = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> AvailableUpdates => _availableUpdates;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<object>> Search(string query, CancellationToken cancellationToken)
    {
        var catalog = await Catalog(cancellationToken);
        return catalog.Where(x => !x.IsDeprecated && !IsBundledInfrastructure(x.Namespace, x.Name) &&
                (x.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(50).Select(x => (object)new
            {
                x.Namespace,
                x.Name,
                x.FullName,
                version = x.Versions.FirstOrDefault()?.VersionNumber,
                description = x.Versions.FirstOrDefault()?.Description,
                downloadCount = x.Versions.Sum(v => v.Downloads)
            }).ToArray();
    }

    public async Task CheckForUpdates(CancellationToken cancellationToken)
    {
        var catalog = await Catalog(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var installed = await db.InstalledMods.AsNoTracking().Where(x => x.Source == "thunderstore").ToListAsync(cancellationToken);
        var updates = new Dictionary<Guid, string>();
        foreach (var mod in installed)
        {
            var package = catalog.FirstOrDefault(x => x.Namespace.Equals(mod.Namespace, StringComparison.OrdinalIgnoreCase) && x.Name.Equals(mod.Name, StringComparison.OrdinalIgnoreCase));
            var latest = package?.Versions.FirstOrDefault()?.VersionNumber;
            if (!string.IsNullOrWhiteSpace(latest) && !latest.Equals(mod.Version, StringComparison.OrdinalIgnoreCase)) updates[mod.Id] = latest;
        }
        _availableUpdates = updates;
    }

    public async Task InstallThunderstore(string packageNamespace, string name, string version, CancellationToken cancellationToken)
    {
        if (IsBundledInfrastructure(packageNamespace, name))
            throw new InvalidOperationException($"{packageNamespace}/{name} is managed by the Server Manager container and cannot be installed from the mod catalog.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRollbackSnapshot(cancellationToken);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var constraints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await InstallRecursive(packageNamespace, name, version, visiting, constraints, false, cancellationToken);
            await MarkPending(cancellationToken);
        }
        catch
        {
            await RollbackStaging(cancellationToken);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> StageAllUpdates(CancellationToken cancellationToken)
    {
        var pending = _availableUpdates.ToArray();
        if (pending.Length == 0) return 0;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var installed = await db.InstalledMods.AsNoTracking()
            .Where(item => pending.Select(update => update.Key).Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var staged = 0;
        foreach (var update in pending)
        {
            if (!installed.TryGetValue(update.Key, out var mod) || mod.Protected) continue;
            await InstallThunderstore(mod.Namespace, mod.Name, update.Value, cancellationToken);
            staged++;
        }
        await CheckForUpdates(cancellationToken);
        await audit.Write("mod.update.stage-all", staged.ToString(), "success", $"packages:{staged}");
        return staged;
    }

    public async Task<InstalledMod> InstallUpload(Stream stream, string fallbackName, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), "vsm-upload-" + Guid.NewGuid().ToString("N") + ".zip");
        var stagingStarted = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using (var file = File.Create(temp)) await stream.CopyToAsync(file, cancellationToken);
            if (new FileInfo(temp).Length > 512L * 1024 * 1024) throw new InvalidDataException("Package exceeds the 512 MiB limit.");
            var manifest = await ReadManifest(temp, fallbackName, cancellationToken);
            if (IsBundledInfrastructure("", manifest.Name))
                throw new InvalidOperationException($"{manifest.Name} is managed by the Server Manager container and cannot be installed as a mod.");
            await EnsureRollbackSnapshot(cancellationToken);
            stagingStarted = true;
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var constraints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in manifest.Dependencies)
            {
                var coordinate = ParseDependency(dependency);
                await InstallRecursive(coordinate.Namespace, coordinate.Name, coordinate.Version, visiting, constraints, true, cancellationToken);
            }
            var installed = await InstallArchive(temp, "manual", manifest.Name, manifest.VersionNumber, manifest.Dependencies, "manual", cancellationToken);
            await MarkPending(cancellationToken);
            await audit.Write("mod.install", $"manual/{manifest.Name}@{manifest.VersionNumber}");
            return installed;
        }
        catch
        {
            if (stagingStarted) await RollbackStaging(cancellationToken);
            throw;
        }
        finally
        {
            _gate.Release();
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task SetEnabled(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRollbackSnapshot(cancellationToken);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var mod = await db.InstalledMods.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("Mod was not found.");
            if (mod.Protected) throw new InvalidOperationException("This package is protected.");
            var files = JsonSerializer.Deserialize<string[]>(mod.FilesJson, JsonOptions) ?? [];
            foreach (var relative in files.Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var path = SafeDestination(relative);
                var disabled = path + ".disabled";
                if (enabled && File.Exists(disabled)) File.Move(disabled, path, true);
                if (!enabled && File.Exists(path)) File.Move(path, disabled, true);
            }
            mod.Enabled = enabled;
            await db.SaveChangesAsync(cancellationToken);
            await MarkPending(cancellationToken);
            await audit.Write(enabled ? "mod.enable" : "mod.disable", $"{mod.Namespace}/{mod.Name}");
        }
        catch
        {
            await RollbackStaging(cancellationToken);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task Remove(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRollbackSnapshot(cancellationToken);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var mod = await db.InstalledMods.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("Mod was not found.");
            if (mod.Protected) throw new InvalidOperationException("This package is protected.");
            var dependencyPrefix = $"{mod.Namespace}-{mod.Name}-";
            var dependent = await db.InstalledMods.AsNoTracking().FirstOrDefaultAsync(x => x.Id != id && x.DependenciesJson.Contains(dependencyPrefix), cancellationToken);
            if (dependent is not null) throw new InvalidOperationException($"{dependent.Namespace}/{dependent.Name} depends on this package.");
            var files = JsonSerializer.Deserialize<string[]>(mod.FilesJson, JsonOptions) ?? [];
            foreach (var relative in files)
            {
                if (relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase)) continue;
                var path = SafeDestination(relative);
                if (File.Exists(path) && await OwnedOnlyBy(db, mod.Id, relative, cancellationToken)) File.Delete(path);
                if (File.Exists(path + ".disabled") && await OwnedOnlyBy(db, mod.Id, relative, cancellationToken)) File.Delete(path + ".disabled");
            }
            db.InstalledMods.Remove(mod);
            await db.SaveChangesAsync(cancellationToken);
            await MarkPending(cancellationToken);
            await audit.Write("mod.remove", $"{mod.Namespace}/{mod.Name}");
        }
        catch
        {
            await RollbackStaging(cancellationToken);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task Apply(CancellationToken cancellationToken)
    {
        if (!state.RestartRequired) return;
        var snapshot = RollbackRoot;
        if (!Directory.Exists(Path.Combine(snapshot, "BepInEx")))
        {
            if (await modConfigs.HasPending(cancellationToken)) { await modConfigs.Apply(cancellationToken); return; }
            throw new InvalidOperationException("The pre-change mod snapshot is missing; refusing an unsafe restart.");
        }
        try
        {
            if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60));
            var previousGeneration = agent.Generation;
            await supervisor.Restart(TimeSpan.Zero, cancellationToken);
            var started = DateTimeOffset.UtcNow;
            while ((!agent.IsConnected || agent.Generation <= previousGeneration) && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(120)) await Task.Delay(1000, cancellationToken);
            if (!agent.IsConnected || agent.Generation <= previousGeneration) throw new TimeoutException("Server agent did not complete a fresh handshake within 120 seconds.");
            var retained = Path.Combine(_managerData, "snapshots", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
            Directory.Move(snapshot, retained);
            await modConfigs.ClearPending(cancellationToken);
            await ClearPending(cancellationToken);
            await audit.Write("mod.apply", "pending changes");
        }
        catch
        {
            ClearPackageFilesPreservingConfig(_bepInEx);
            CopyDirectory(Path.Combine(snapshot, "BepInEx"), _bepInEx, true);
            await RestoreModRecords(Path.Combine(snapshot, "mods.json"), cancellationToken);
            await supervisor.Restart(TimeSpan.Zero, cancellationToken);
            await modConfigs.ClearPending(cancellationToken);
            await ClearPending(cancellationToken);
            await audit.Write("mod.rollback", snapshot, "success", "Automatic rollback after failed startup.");
            throw;
        }
    }

    private async Task InstallRecursive(string packageNamespace, string name, string version, HashSet<string> visiting, Dictionary<string, string> constraints, bool isDependency, CancellationToken cancellationToken)
    {
        var packageKey = $"{packageNamespace}/{name}";
        if (IsBundledInfrastructure(packageNamespace, name))
        {
            if (!isDependency)
                throw new InvalidOperationException($"{packageKey} is managed by the Server Manager container and cannot be installed from the mod catalog.");
            if (IsBepInEx(packageNamespace, name) && !VersionAtLeast(_bepInExPackVersion, version))
                throw new InvalidOperationException($"{packageKey} {version} is required, but this container bundles older version {_bepInExPackVersion}. Update the Server Manager image first.");
            return;
        }
        if (constraints.TryGetValue(packageKey, out var requiredVersion) && !requiredVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Dependency conflict: {packageKey} requires both {requiredVersion} and {version}.");
        constraints[packageKey] = version;
        var key = $"{packageNamespace}-{name}-{version}";
        if (!visiting.Add(key)) throw new InvalidDataException("Dependency cycle detected at " + key);
        var package = (await Catalog(cancellationToken)).FirstOrDefault(x => x.Namespace.Equals(packageNamespace, StringComparison.OrdinalIgnoreCase) && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Thunderstore package {packageNamespace}/{name} was not found.");
        var packageVersion = package.Versions.FirstOrDefault(x => x.VersionNumber == version) ?? throw new KeyNotFoundException($"Version {version} was not found.");
        foreach (var dependency in packageVersion.Dependencies)
        {
            var coordinate = ParseDependency(dependency);
            await InstallRecursive(coordinate.Namespace, coordinate.Name, coordinate.Version, visiting, constraints, true, cancellationToken);
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var existing = await db.InstalledMods.FirstOrDefaultAsync(x => x.Namespace == packageNamespace && x.Name == name, cancellationToken);
        if (existing?.Version == version) { visiting.Remove(key); return; }
        var temp = Path.Combine(Path.GetTempPath(), "vsm-download-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using var source = await clients.CreateClient("thunderstore").GetStreamAsync(packageVersion.DownloadUrl, cancellationToken);
            await using (var target = File.Create(temp)) await source.CopyToAsync(target, cancellationToken);
            if (new FileInfo(temp).Length > 512L * 1024 * 1024) throw new InvalidDataException("Package exceeds the 512 MiB limit.");
            await InstallArchive(temp, packageNamespace, name, version, packageVersion.Dependencies, "thunderstore", cancellationToken);
            await audit.Write(existing is null ? "mod.install" : "mod.update", $"{packageNamespace}/{name}@{version}");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); visiting.Remove(key); }
    }

    internal static bool IsBundledInfrastructure(string packageNamespace, string name) =>
        IsBepInEx(packageNamespace, name) ||
        (name.Equals(ServerManagerPackage, StringComparison.OrdinalIgnoreCase) &&
         (string.IsNullOrWhiteSpace(packageNamespace) || packageNamespace.Equals(ServerManagerNamespace, StringComparison.OrdinalIgnoreCase)));

    internal static bool VersionAtLeast(string installed, string required) =>
        Version.TryParse(installed, out var installedVersion) && Version.TryParse(required, out var requiredVersion)
            ? installedVersion >= requiredVersion
            : installed.Equals(required, StringComparison.OrdinalIgnoreCase);

    internal static (string Namespace, string Name, string Version) ParseDependency(string dependency)
    {
        var parts = dependency.Split('-');
        if (parts.Length < 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid dependency string: " + dependency);
        return (string.Join('-', parts[..^2]), parts[^2], parts[^1]);
    }

    internal static string? PackageDestination(string normalized, string packageNamespace, string name)
    {
        var segments = normalized.Split('/');
        var bepInExIndex = Array.FindIndex(segments, segment => segment.Equals("BepInEx", StringComparison.OrdinalIgnoreCase));
        if (bepInExIndex >= 0)
        {
            var relative = string.Join('/', segments[(bepInExIndex + 1)..]);
            return string.IsNullOrWhiteSpace(relative) ? null : relative;
        }

        if (segments.Length == 1 && IsPackageMetadata(segments[0])) return null;
        if (segments[0].Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("patchers", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("config", StringComparison.OrdinalIgnoreCase)) return normalized;

        var owner = SafePackageSegment(string.IsNullOrWhiteSpace(packageNamespace) ? "manual" : packageNamespace);
        var package = SafePackageSegment(name);
        return $"plugins/{owner}-{package}/{normalized}";
    }

    private static bool IsPackageMetadata(string fileName) =>
        fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("icon.png", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("LICENSE.md", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase);

    private static string SafePackageSegment(string value) => Regex.Replace(value, "[^A-Za-z0-9_]", "_");

    private static bool IsBepInEx(string packageNamespace, string name) =>
        name.Equals(BepInExPackage, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(packageNamespace) || packageNamespace.Equals(BepInExNamespace, StringComparison.OrdinalIgnoreCase));

    private async Task<InstalledMod> InstallArchive(string archivePath, string packageNamespace, string name, string version, string[] dependencies, string source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_bepInEx);
        var files = new List<string>();
        await using var ownershipScope = scopes.CreateAsyncScope();
        var ownershipDb = ownershipScope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var current = await ownershipDb.InstalledMods.AsNoTracking().FirstOrDefaultAsync(x => x.Namespace == packageNamespace && x.Name == name, cancellationToken);
        var ownedByCurrent = current is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(JsonSerializer.Deserialize<string[]>(current.FilesJson, JsonOptions) ?? [], StringComparer.OrdinalIgnoreCase);
        var ownedByOthers = (await ownershipDb.InstalledMods.AsNoTracking().Where(x => current == null || x.Id != current.Id).Select(x => x.FilesJson).ToListAsync(cancellationToken))
            .SelectMany(x => JsonSerializer.Deserialize<string[]>(x, JsonOptions) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<(ZipArchiveEntry Entry, string Relative)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;
            var normalized = ArchiveSafety.NormalizeEntry(entry.FullName);
            if (ArchiveSafety.IsSymbolicLink(entry.ExternalAttributes)) throw new InvalidDataException("Package contains a symbolic link.");
            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > 1024L * 1024 * 1024) throw new InvalidDataException("Expanded package exceeds the 1 GiB limit.");
            var relative = PackageDestination(normalized, packageNamespace, name);
            if (relative is null) continue;
            _ = SafeDestination(relative);
            if (!seen.Add(relative)) throw new InvalidDataException($"Package contains a duplicate file: {relative}");
            var destination = SafeDestination(relative);
            if (File.Exists(destination) && !ownedByCurrent.Contains(relative))
                throw new InvalidDataException(ownedByOthers.Contains(relative) ? $"File collision with another managed package: {relative}" : $"Refusing to overwrite unmanaged file: {relative}");
            entries.Add((entry, relative));
        }
        var extractionRoot = Path.Combine(_managerData, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);
        try
        {
            foreach (var (entry, relative) in entries)
            {
                var staged = Path.GetFullPath(Path.Combine(extractionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!staged.StartsWith(Path.GetFullPath(extractionRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Package path escaped extraction root.");
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await using var input = entry.Open();
                await using var output = File.Create(staged);
                await input.CopyToAsync(output, cancellationToken);
            }
            foreach (var (_, relative) in entries)
            {
                var staged = Path.Combine(extractionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var destination = SafeDestination(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(staged, destination, true);
                files.Add(relative);
            }
            foreach (var stale in ownedByCurrent.Except(files, StringComparer.OrdinalIgnoreCase).Where(x => !x.StartsWith("config/", StringComparison.OrdinalIgnoreCase)))
            {
                var destination = SafeDestination(stale);
                if (!ownedByOthers.Contains(stale) && File.Exists(destination)) File.Delete(destination);
                if (!ownedByOthers.Contains(stale) && File.Exists(destination + ".disabled")) File.Delete(destination + ".disabled");
            }
        }
        finally { if (Directory.Exists(extractionRoot)) Directory.Delete(extractionRoot, true); }
        if (files.Count == 0) throw new InvalidDataException("Package does not contain installable BepInEx files.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(archivePath), cancellationToken)).ToLowerInvariant();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var record = await db.InstalledMods.FirstOrDefaultAsync(x => x.Namespace == packageNamespace && x.Name == name, cancellationToken);
        if (record is null) { record = new InstalledMod { Namespace = packageNamespace, Name = name }; db.InstalledMods.Add(record); }
        record.Version = version;
        record.Source = source;
        record.DependenciesJson = JsonSerializer.Serialize(dependencies, JsonOptions);
        record.FilesJson = JsonSerializer.Serialize(files.Distinct(StringComparer.OrdinalIgnoreCase), JsonOptions);
        record.Sha256 = hash;
        record.Enabled = true;
        record.InstalledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    private async Task<List<ThunderstorePackage>> Catalog(CancellationToken cancellationToken)
    {
        if (_catalog is not null && DateTimeOffset.UtcNow - _catalogAt < TimeSpan.FromMinutes(15)) return _catalog;
        _catalog = await clients.CreateClient("thunderstore").GetFromJsonAsync<List<ThunderstorePackage>>("https://thunderstore.io/c/valheim/api/v1/package/", JsonOptions, cancellationToken) ?? [];
        _catalogAt = DateTimeOffset.UtcNow;
        return _catalog;
    }

    private async Task<PackageManifest> ReadManifest(string archivePath, string fallbackName, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(x => x.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("manifest.json is required at package root.");
        if (!archive.Entries.Any(x => x.FullName.Equals("README.md", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("README.md is required at package root.");
        if (!archive.Entries.Any(x => x.FullName.Equals("icon.png", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("icon.png is required at package root.");
        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(stream, JsonOptions, cancellationToken) ?? throw new InvalidDataException("manifest.json is invalid.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = Path.GetFileNameWithoutExtension(fallbackName);
        if (!Regex.IsMatch(manifest.Name, "^[A-Za-z0-9_]+$")) throw new InvalidDataException("manifest.json name may contain only letters, numbers, and underscores.");
        if (!Regex.IsMatch(manifest.VersionNumber ?? "", "^[0-9]+\\.[0-9]+\\.[0-9]+$")) throw new InvalidDataException("manifest.json requires a semantic version_number such as 1.2.3.");
        manifest.Dependencies ??= [];
        return manifest;
    }

    private string SafeDestination(string relative)
    {
        var root = Path.GetFullPath(_bepInEx) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Path escaped BepInEx root.");
        return destination;
    }

    private async Task EnsureRollbackSnapshot(CancellationToken cancellationToken)
    {
        if (Directory.Exists(RollbackRoot)) return;
        Directory.CreateDirectory(_managerData);
        var temporary = Path.Combine(_managerData, ".pending-mod-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            CopyDirectory(_bepInEx, Path.Combine(temporary, "BepInEx"));
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var records = await db.InstalledMods.AsNoTracking().ToListAsync(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(temporary, "mods.json"), JsonSerializer.Serialize(records, JsonOptions), cancellationToken);
            Directory.Move(temporary, RollbackRoot);
            await MarkPending(cancellationToken);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private async Task MarkPending(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var setting = await db.ManagerSettings.FindAsync(["mods.pending"], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = "mods.pending", Value = "true" });
        else setting.Value = "true";
        await db.SaveChangesAsync(cancellationToken);
        state.RestartRequired = true;
    }

    private async Task ClearPending(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var setting = await db.ManagerSettings.FindAsync(["mods.pending"], cancellationToken);
        if (setting is not null) db.ManagerSettings.Remove(setting);
        await db.SaveChangesAsync(cancellationToken);
        state.RestartRequired = await db.ManagerSettings.AnyAsync(item => item.Key == "configs.pending" && item.Value == "true", cancellationToken);
    }

    private async Task RestoreModRecords(string path, CancellationToken cancellationToken)
    {
        var records = File.Exists(path)
            ? JsonSerializer.Deserialize<List<InstalledMod>>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions) ?? []
            : [];
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        db.InstalledMods.RemoveRange(await db.InstalledMods.ToListAsync(cancellationToken));
        db.InstalledMods.AddRange(records);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RollbackStaging(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(RollbackRoot, "BepInEx"))) return;
        ClearPackageFilesPreservingConfig(_bepInEx);
        CopyDirectory(Path.Combine(RollbackRoot, "BepInEx"), _bepInEx, true);
        await RestoreModRecords(Path.Combine(RollbackRoot, "mods.json"), cancellationToken);
        Directory.Delete(RollbackRoot, true);
        await ClearPending(cancellationToken);
    }

    private static void ClearPackageFilesPreservingConfig(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.GetFiles(root)) File.Delete(file);
        foreach (var directory in Directory.GetDirectories(root))
            if (!Path.GetFileName(directory).Equals("config", StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
    }

    private static async Task<bool> OwnedOnlyBy(ManagerDbContext db, Guid owner, string relative, CancellationToken cancellationToken) =>
        !await db.InstalledMods.AsNoTracking().AnyAsync(x => x.Id != owner && x.FilesJson.Contains($"\"{relative.Replace("\\", "\\\\")}\""), cancellationToken);

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private sealed class PackageManifest
    {
        public string Name { get; set; } = "";
        [JsonPropertyName("version_number")]
        public string VersionNumber { get; set; } = "";
        public string[] Dependencies { get; set; } = [];
    }
    internal sealed class ThunderstorePackage
    {
        [JsonPropertyName("owner")]
        public string Namespace { get; set; } = "";
        public string Name { get; set; } = "";
        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = "";
        [JsonPropertyName("is_deprecated")]
        public bool IsDeprecated { get; set; }
        public List<ThunderstoreVersion> Versions { get; set; } = [];
    }
    internal sealed class ThunderstoreVersion
    {
        [JsonPropertyName("version_number")]
        public string VersionNumber { get; set; } = "";
        public string Description { get; set; } = "";
        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = "";
        public int Downloads { get; set; }
        public string[] Dependencies { get; set; } = [];
    }
}
