using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed class ClientModManifestService(IServiceScopeFactory scopes, IConfiguration configuration)
{
    private const string SettingPrefix = "client-mod-sync:";
    private const string ServerCharactersEnabledSetting = "server-characters.enabled";
    private const string RuntimeCoordinate = "Creaton-Server_Manager-2.1.3";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataPath = configuration["VSM_DATA_PATH"] ?? "/data/manager";

    public async Task<string> BuildJson(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var mods = await db.InstalledMods.AsNoTracking()
            .Where(mod => mod.Enabled && mod.Source == "thunderstore" && !mod.Protected)
            .OrderBy(mod => mod.Namespace).ThenBy(mod => mod.Name)
            .ToListAsync(cancellationToken);
        var settings = await db.ManagerSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(SettingPrefix) || setting.Key == ServerCharactersEnabledSetting)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        var requiredMods = mods
            .Where(mod => !IsBootstrapInfrastructure(mod) && IsClientRequired(mod, settings))
            .ToList();
        var serverCharactersEnabled = settings.TryGetValue(ServerCharactersEnabledSetting, out var enabled)
            && enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!serverCharactersEnabled && requiredMods.Count == 0) return "";

        var packages = new List<ClientManifestPackage>();
        if (serverCharactersEnabled) packages.Add(await Runtime(cancellationToken));
        foreach (var mod in requiredMods)
        {
            packages.Add(new ClientManifestPackage(
                $"{mod.Namespace}-{mod.Name}-{mod.Version}",
                mod.Namespace,
                mod.Name,
                mod.Version,
                $"https://thunderstore.io/package/download/{Uri.EscapeDataString(mod.Namespace)}/{Uri.EscapeDataString(mod.Name)}/{Uri.EscapeDataString(mod.Version)}/",
                null,
                JsonSerializer.Deserialize<string[]>(mod.DependenciesJson, JsonOptions) ?? [],
                mod.Sha256,
                null));
        }

        var revisionInput = string.Join("\n", packages.Select(package => $"{package.Coordinate}:{package.Sha256}"));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput))).ToLowerInvariant();
        var manifestId = "vsm:" + (configuration["SERVER_WORLD"] ?? "Dedicated");
        return JsonSerializer.Serialize(new ClientModManifest(1, manifestId, revision, DateTimeOffset.UtcNow, packages, []), JsonOptions);
    }

    public async Task<IReadOnlyDictionary<Guid, bool>> Status(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var mods = await db.InstalledMods.AsNoTracking().ToListAsync(cancellationToken);
        var settings = await db.ManagerSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(SettingPrefix))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);
        return mods.ToDictionary(mod => mod.Id, mod => IsClientRequired(mod, settings));
    }

    public async Task SetRequired(Guid id, bool required, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var mod = await db.InstalledMods.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("Mod was not found.");
        if (mod.Protected || mod.Source != "thunderstore" || IsBootstrapInfrastructure(mod))
            throw new InvalidOperationException("Only managed Thunderstore gameplay mods can be synchronized to clients.");
        var key = SettingPrefix + id.ToString("N");
        var setting = await db.ManagerSettings.FindAsync([key], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = required ? "true" : "false" });
        else setting.Value = required ? "true" : "false";
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ClientManifestPackage> Runtime(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_dataPath, "runtime", "ValheimServerManager-2.1.3-client.zip");
        if (!File.Exists(path)) throw new FileNotFoundException("The VSM client runtime artifact is missing.", path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("The VSM client runtime exceeds the inline package limit.");
        return new ClientManifestPackage(
            RuntimeCoordinate,
            "Creaton",
            "Server_Manager",
            "2.1.3",
            "",
            bytes.LongLength,
            [],
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToBase64String(bytes));
    }

    private static bool IsClientRequired(InstalledMod mod, IReadOnlyDictionary<string, string> settings)
    {
        if (mod.Protected || mod.Source != "thunderstore" || IsBootstrapInfrastructure(mod)) return false;
        return !settings.TryGetValue(SettingPrefix + mod.Id.ToString("N"), out var value)
            || !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBootstrapInfrastructure(InstalledMod mod) =>
        mod.Name.Contains("BepInExPack", StringComparison.OrdinalIgnoreCase)
        || mod.Name.Equals("ServerModBootstrap", StringComparison.OrdinalIgnoreCase)
        || mod.Name.Equals("ValheimServerManagerClient", StringComparison.OrdinalIgnoreCase)
        || mod.Name.Equals("ValheimServerManagerServer", StringComparison.OrdinalIgnoreCase)
        || mod.Name.Equals("Server_Manager", StringComparison.OrdinalIgnoreCase);

    private sealed record ClientModManifest(
        int SchemaVersion,
        string ManifestId,
        string Revision,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<ClientManifestPackage> Packages,
        IReadOnlyList<object> Configs);

    private sealed record ClientManifestPackage(
        string Coordinate,
        string Namespace,
        string PackageName,
        string VersionNumber,
        string DownloadUrl,
        long? FileSize,
        IReadOnlyList<string> Dependencies,
        string Sha256,
        string? ContentBase64);
}
