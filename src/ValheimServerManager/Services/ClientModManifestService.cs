using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ClientModPolicy(string Policy, string Effective, IReadOnlyList<string> RequiredBy);

public sealed class ClientModManifestService(IServiceScopeFactory scopes, IConfiguration configuration)
{
    private const string SettingPrefix = "client-mod-sync:";
    private const string ServerCharactersEnabledSetting = "server-characters.enabled";
    private const string InstanceKey = SettingPrefix + "instance-id";
    private const string RuntimeCoordinate = "Creaton-Server_Manager-2.1.3";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataPath = configuration["VSM_DATA_PATH"] ?? "/data/manager";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> BuildJson(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var mods = await db.InstalledMods.AsNoTracking().ToListAsync(cancellationToken);
            var settings = await Settings(db, cancellationToken);
            var enabled = mods.Where(mod => mod.Enabled && CanSynchronize(mod)).ToArray();
            var requiredRoots = enabled.Where(mod => ReadPolicy(mod, settings) == "required").ToArray();
            var required = Closure(requiredRoots, mods);
            var optional = enabled.Where(mod => ReadPolicy(mod, settings) == "optional" && !required.Any(item => item.Id == mod.Id)).ToArray();
            var characters = ReadBool(settings, ServerCharactersEnabledSetting, configuration.GetValue("VSM_SERVER_CHARACTERS_ENABLED", false));
            var inspection = ReadBool(settings, ServerCharacterSettingsService.InspectionRequiredKey, configuration.GetValue("VSM_REQUIRE_INVENTORY_INSPECTION", true));
            if (!characters && !inspection && required.Count == 0 && optional.Length == 0) return "";

            if (!settings.TryGetValue(InstanceKey, out var instanceId) || !Guid.TryParse(instanceId, out _))
            {
                instanceId = Guid.NewGuid().ToString("N");
                var stored = await db.ManagerSettings.FindAsync([InstanceKey], cancellationToken);
                if (stored is null) db.ManagerSettings.Add(new ManagerSetting { Key = InstanceKey, Value = instanceId });
                else stored.Value = instanceId;
                await db.SaveChangesAsync(cancellationToken);
            }
            // The protected runtime is always mandatory while client management is active.
            // This also upgrades legacy receivers so they can offer optional packages and receipts.
            var packages = new List<ClientManifestPackage> { await Runtime(cancellationToken) };
            packages.AddRange(required.Select(Package));
            var groups = optional.Select(mod => new OptionalModGroup(
                mod.Namespace + "-" + mod.Name, mod.Namespace + "/" + mod.Name,
                Closure([mod], mods).Select(Package).ToArray())).ToArray();
            var manifestId = "vsm:" + instanceId;
            // Optional catalog changes do not invalidate a player's mandatory-mod receipt.
            var revision = Hash(manifestId + "\n" + string.Join("\n", packages.Select(package => $"{package.Coordinate}:{package.Sha256}")));
            var optionalRevision = Hash(string.Join("\n", groups.Select(group => group.Id + ":" + string.Join(",", group.Packages.Select(package => package.Coordinate + ":" + package.Sha256)))));
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1, manifestId, revision, generatedAt = DateTimeOffset.UtcNow,
                packages, configs = Array.Empty<object>(), optionalGroups = groups, optionalRevision,
                requiredReceipt = true, inventoryInspectionRequired = inspection
            }, JsonOptions);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<Guid, ClientModPolicy>> Policies(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var mods = await db.InstalledMods.AsNoTracking().ToListAsync(ct);
        var settings = await Settings(db, ct);
        var roots = mods.Where(mod => mod.Enabled && CanSynchronize(mod) && ReadPolicy(mod, settings) == "required").ToArray();
        var requiredBy = new Dictionary<Guid, List<string>>();
        foreach (var root in roots)
            foreach (var dependency in Closure([root], mods))
            {
                if (dependency.Id == root.Id) continue;
                if (!requiredBy.TryGetValue(dependency.Id, out var parents)) requiredBy[dependency.Id] = parents = [];
                parents.Add(root.Namespace + "/" + root.Name);
            }
        return mods.ToDictionary(mod => mod.Id, mod =>
        {
            var policy = ReadPolicy(mod, settings);
            var parents = requiredBy.GetValueOrDefault(mod.Id) ?? [];
            return new ClientModPolicy(policy, !mod.Enabled ? "serverOnly" : parents.Count > 0 ? "required" : policy, parents);
        });
    }

    public async Task<IReadOnlyDictionary<Guid, bool>> Status(CancellationToken cancellationToken = default) =>
        (await Policies(cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value.Effective == "required");

    public Task SetRequired(Guid id, bool required, CancellationToken cancellationToken = default) =>
        SetPolicy(id, required ? "required" : "serverOnly", cancellationToken);

    public async Task SetPolicy(Guid id, string policy, CancellationToken ct = default)
    {
        if (policy is not ("required" or "optional" or "serverOnly")) throw new ArgumentException("Choose required, optional, or serverOnly.");
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var mods = await db.InstalledMods.ToListAsync(ct);
            var mod = mods.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("Mod was not found.");
            if (!CanSynchronize(mod)) throw new InvalidOperationException("Only managed Thunderstore gameplay packages can be synchronized to clients.");
            if (policy != "serverOnly" && mod.Enabled) _ = Closure([mod], mods);
            var key = SettingPrefix + id.ToString("N");
            var setting = await db.ManagerSettings.FindAsync([key], ct);
            if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = policy });
            else setting.Value = policy;
            await db.SaveChangesAsync(ct);
        }
        finally { _gate.Release(); }
    }

    internal static IReadOnlyList<InstalledMod> Closure(IEnumerable<InstalledMod> roots, IReadOnlyCollection<InstalledMod> all)
    {
        var result = new Dictionary<Guid, InstalledMod>();
        void Visit(InstalledMod mod)
        {
            if (!mod.Enabled) throw new InvalidOperationException($"Client dependency {mod.Namespace}/{mod.Name} is disabled.");
            if (!CanSynchronize(mod)) throw new InvalidOperationException($"Client dependency {mod.Namespace}/{mod.Name} cannot be synchronized.");
            if (!result.TryAdd(mod.Id, mod)) return; // Cycle-safe; each pinned package appears once.
            foreach (var coordinate in JsonSerializer.Deserialize<string[]>(mod.DependenciesJson, JsonOptions) ?? [])
            {
                var dependency = ModService.ParseDependency(coordinate);
                if (ModService.IsBundledInfrastructure(dependency.Namespace, dependency.Name)) continue;
                var installed = all.SingleOrDefault(item => item.Namespace.Equals(dependency.Namespace, StringComparison.OrdinalIgnoreCase)
                    && item.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase));
                if (installed is null || !ModService.VersionAtLeast(installed.Version, dependency.Version))
                    throw new InvalidOperationException($"Install compatible dependency {coordinate} before offering {mod.Namespace}/{mod.Name} to clients.");
                Visit(installed);
            }
        }
        foreach (var root in roots) Visit(root);
        return result.Values.OrderBy(mod => mod.Namespace, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ClientManifestPackage Package(InstalledMod mod) => new(
        $"{mod.Namespace}-{mod.Name}-{mod.Version}", mod.Namespace, mod.Name, mod.Version,
        $"https://thunderstore.io/package/download/{Uri.EscapeDataString(mod.Namespace)}/{Uri.EscapeDataString(mod.Name)}/{Uri.EscapeDataString(mod.Version)}/",
        null, JsonSerializer.Deserialize<string[]>(mod.DependenciesJson, JsonOptions) ?? [], mod.Sha256, null);

    private async Task<ClientManifestPackage> Runtime(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_dataPath, "runtime", "ValheimServerManager-2.1.3-client.zip");
        if (!File.Exists(path)) throw new FileNotFoundException("The VSM client runtime artifact is missing.", path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("The VSM client runtime exceeds the inline package limit.");
        return new(RuntimeCoordinate, "Creaton", "Server_Manager", "2.1.3", "", bytes.LongLength, [],
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Convert.ToBase64String(bytes));
    }

    private static Task<Dictionary<string, string>> Settings(ManagerDbContext db, CancellationToken ct) => db.ManagerSettings.AsNoTracking()
        .Where(setting => setting.Key.StartsWith(SettingPrefix) || setting.Key == ServerCharactersEnabledSetting || setting.Key == ServerCharacterSettingsService.InspectionRequiredKey)
        .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);
    private static bool ReadBool(IReadOnlyDictionary<string, string> settings, string key, bool fallback) =>
        settings.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) ? flag : fallback;
    internal static string ReadPolicy(InstalledMod mod, IReadOnlyDictionary<string, string> settings)
    {
        if (!CanSynchronize(mod)) return "serverOnly";
        if (!settings.TryGetValue(SettingPrefix + mod.Id.ToString("N"), out var value)) return "required";
        // Migrate old booleans without changing the administrator's meaning.
        return value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "serverOnly" ? "serverOnly"
            : value == "optional" ? "optional" : "required";
    }
    private static bool CanSynchronize(InstalledMod mod) => !mod.Protected && mod.Source == "thunderstore"
        && !ModService.IsBundledInfrastructure(mod.Namespace, mod.Name)
        && !mod.Name.Equals("ServerModBootstrap", StringComparison.OrdinalIgnoreCase)
        && !mod.Name.Equals("ValheimServerManagerClient", StringComparison.OrdinalIgnoreCase)
        && !mod.Name.Equals("ValheimServerManagerServer", StringComparison.OrdinalIgnoreCase);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private sealed record OptionalModGroup(string Id, string Name, IReadOnlyList<ClientManifestPackage> Packages);
    private sealed record ClientManifestPackage(string Coordinate, string Namespace, string PackageName, string VersionNumber,
        string DownloadUrl, long? FileSize, IReadOnlyList<string> Dependencies, string Sha256, string? ContentBase64);
}
