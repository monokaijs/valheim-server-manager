using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ServerCharacterSettings(bool AcceptFirstJoinProfile, bool RejectPreviouslyUsedCharacters);

public sealed class ServerCharacterSettingsService(IServiceScopeFactory scopes)
{
    private const string AcceptKey = "server-characters.accept-first-join-profile";
    private const string RejectUsedKey = "server-characters.reject-previously-used";

    public async Task<ServerCharacterSettings> Get(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var values = await db.ManagerSettings.AsNoTracking()
            .Where(item => item.Key == AcceptKey || item.Key == RejectUsedKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
        return new ServerCharacterSettings(
            Read(values, AcceptKey, true),
            Read(values, RejectUsedKey, false));
    }

    public async Task<ServerCharacterSettings> Set(ServerCharacterSettings settings, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        await Put(db, AcceptKey, settings.AcceptFirstJoinProfile, cancellationToken);
        await Put(db, RejectUsedKey, settings.RejectPreviouslyUsedCharacters, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<object> Payload(CancellationToken cancellationToken = default)
    {
        var settings = await Get(cancellationToken);
        return new
        {
            acceptFirstJoinProfile = settings.AcceptFirstJoinProfile,
            rejectPreviouslyUsedCharacters = settings.RejectPreviouslyUsedCharacters
        };
    }

    private static bool Read(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;

    private static async Task Put(ManagerDbContext db, string key, bool value, CancellationToken cancellationToken)
    {
        var setting = await db.ManagerSettings.FindAsync([key], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = value ? "true" : "false" });
        else setting.Value = value ? "true" : "false";
    }
}
