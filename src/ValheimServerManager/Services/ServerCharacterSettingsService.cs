using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ServerCharacterSettings(
    bool Enabled,
    bool AcceptFirstJoinProfile,
    bool RejectPreviouslyUsedCharacters,
    int BackupsToKeep,
    int ClientGraceSeconds,
    bool RequireInventoryInspection = true);

public sealed class ServerCharacterSettingsService(IServiceScopeFactory scopes, IConfiguration configuration)
{
    public const string InspectionRequiredKey = "inventory-inspection.required";
    private const string EnabledKey = "server-characters.enabled";
    private const string AcceptKey = "server-characters.accept-first-join-profile";
    private const string RejectUsedKey = "server-characters.reject-previously-used";
    private const string BackupsKey = "server-characters.backups-to-keep";
    private const string ClientGraceKey = "server-characters.client-grace-seconds";

    public async Task<ServerCharacterSettings> Get(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var values = await db.ManagerSettings.AsNoTracking()
            .Where(item => item.Key == EnabledKey || item.Key == AcceptKey || item.Key == RejectUsedKey
                || item.Key == BackupsKey || item.Key == ClientGraceKey || item.Key == InspectionRequiredKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
        return new ServerCharacterSettings(
            Read(values, EnabledKey, configuration.GetValue("VSM_SERVER_CHARACTERS_ENABLED", false)),
            Read(values, AcceptKey, configuration.GetValue("VSM_SERVER_CHARACTERS_ACCEPT_FIRST_JOIN", true)),
            Read(values, RejectUsedKey, configuration.GetValue("VSM_SERVER_CHARACTERS_REJECT_USED", false)),
            Read(values, BackupsKey, configuration.GetValue("VSM_SERVER_CHARACTERS_BACKUPS", 10), 1, 50),
            Read(values, ClientGraceKey, configuration.GetValue("VSM_CLIENT_MOD_GRACE_SECONDS", 20), 5, 120),
            true);
    }

    public async Task<ServerCharacterSettings> Set(ServerCharacterSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.BackupsToKeep is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(settings.BackupsToKeep), "Keep between 1 and 50 character backups.");
        if (settings.ClientGraceSeconds is < 5 or > 120) throw new ArgumentOutOfRangeException(nameof(settings.ClientGraceSeconds), "Client grace period must be between 5 and 120 seconds.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        await Put(db, EnabledKey, settings.Enabled, cancellationToken);
        await Put(db, InspectionRequiredKey, true, cancellationToken);
        await Put(db, AcceptKey, settings.AcceptFirstJoinProfile, cancellationToken);
        await Put(db, RejectUsedKey, settings.RejectPreviouslyUsedCharacters, cancellationToken);
        await Put(db, BackupsKey, settings.BackupsToKeep, cancellationToken);
        await Put(db, ClientGraceKey, settings.ClientGraceSeconds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return settings with { RequireInventoryInspection = true };
    }

    public async Task<object> Payload(CancellationToken cancellationToken = default)
    {
        var settings = await Get(cancellationToken);
        return new
        {
            enabled = settings.Enabled,
            acceptFirstJoinProfile = settings.AcceptFirstJoinProfile,
            rejectPreviouslyUsedCharacters = settings.RejectPreviouslyUsedCharacters,
            backupsToKeep = settings.BackupsToKeep,
            clientGraceSeconds = settings.ClientGraceSeconds,
            requireInventoryInspection = settings.RequireInventoryInspection
        };
    }

    private static bool Read(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;

    private static int Read(IReadOnlyDictionary<string, string> values, string key, int fallback, int minimum, int maximum) =>
        Math.Clamp(values.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback, minimum, maximum);

    private static async Task Put(ManagerDbContext db, string key, bool value, CancellationToken cancellationToken)
    {
        var setting = await db.ManagerSettings.FindAsync([key], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = value ? "true" : "false" });
        else setting.Value = value ? "true" : "false";
    }

    private static async Task Put(ManagerDbContext db, string key, int value, CancellationToken cancellationToken)
    {
        var setting = await db.ManagerSettings.FindAsync([key], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        else setting.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
