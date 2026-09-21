using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record PluginDescriptor(string Guid, string Name, string Version, string Dll, string ConfigFile);

public sealed class PluginRegistryService(IServiceScopeFactory scopes)
{
    private const string SettingKey = "plugins.registry";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task Update(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var plugins = payload.TryGetProperty("plugins", out var items)
            ? items.Deserialize<List<PluginDescriptor>>(JsonOptions) ?? []
            : [];
        if (plugins.Count > 500) throw new InvalidDataException("Plugin registry is too large.");
        var clean = plugins.Select(plugin => new PluginDescriptor(
                Clean(plugin.Guid, 200), Clean(plugin.Name, 200), Clean(plugin.Version, 80),
                CleanRelative(plugin.Dll), CleanRelative(plugin.ConfigFile)))
            .Where(plugin => plugin.Guid.Length > 0 && plugin.Dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(plugin => plugin.Guid, StringComparer.OrdinalIgnoreCase).ToArray();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var setting = await db.ManagerSettings.FindAsync([SettingKey], cancellationToken);
        var json = JsonSerializer.Serialize(clean, JsonOptions);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = SettingKey, Value = json });
        else setting.Value = json;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PluginDescriptor>> Read(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var json = await db.ManagerSettings.AsNoTracking().Where(item => item.Key == SettingKey)
            .Select(item => item.Value).SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<PluginDescriptor[]>(json, JsonOptions) ?? [];
    }

    private static string Clean(string? value, int maximum)
    {
        var clean = (value ?? "").Trim();
        return clean.Length > maximum ? clean[..maximum] : clean;
    }

    internal static string CleanRelative(string? value)
    {
        value = (value ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (value.Length > 500 || value.Split('/').Any(segment => segment is "" or "." or "..") || value.Contains(':')) return "";
        return value;
    }
}
