using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record VoiceChatSettings(bool Enabled, int Range);

public sealed class VoiceChatSettingsService(IServiceScopeFactory scopes)
{
    private const string EnabledKey = "voice-chat.enabled";
    private const string RangeKey = "voice-chat.range";

    public async Task<VoiceChatSettings> Get(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var settings = await db.ManagerSettings.AsNoTracking()
            .Where(item => item.Key == EnabledKey || item.Key == RangeKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);
        var enabled = settings.TryGetValue(EnabledKey, out var rawEnabled) && bool.TryParse(rawEnabled, out var storedEnabled)
            ? storedEnabled : true;
        var range = settings.TryGetValue(RangeKey, out var rawRange) && int.TryParse(rawRange, out var storedRange)
            ? storedRange : 40;
        return new VoiceChatSettings(enabled, Math.Clamp(range, 5, 100));
    }

    public async Task<VoiceChatSettings> Set(VoiceChatSettings request, CancellationToken ct = default)
    {
        if (request.Range is < 5 or > 100) throw new ArgumentOutOfRangeException(nameof(request.Range), "Voice range must be between 5 and 100 world units.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        await Put(db, EnabledKey, request.Enabled ? "true" : "false", ct);
        await Put(db, RangeKey, request.Range.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
        return request;
    }

    private static async Task Put(ManagerDbContext db, string key, string value, CancellationToken ct)
    {
        var setting = await db.ManagerSettings.FindAsync([key], ct);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = value });
        else setting.Value = value;
    }
}
