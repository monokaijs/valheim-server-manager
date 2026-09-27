using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record DiscordActivitySettings(string ApplicationId, string DetailsTemplate, string StateTemplate, string ImageUrl);

public sealed partial class DiscordActivitySettingsService(IServiceScopeFactory scopes, IConfiguration configuration, ServerSettingsService serverSettings)
{
    private const string SettingKey = "discord.activity";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private DiscordActivitySettings Defaults => new(
        ValidApplicationId(configuration["VSM_DISCORD_APPLICATION_ID"] ?? "") ? configuration["VSM_DISCORD_APPLICATION_ID"] ?? "" : "",
        "{server}",
        "{region} · {players} players online",
        "");

    public async Task<DiscordActivitySettings> Get(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var value = await db.ManagerSettings.AsNoTracking().Where(item => item.Key == SettingKey)
            .Select(item => item.Value).SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value)) return Validate(Defaults);
        try { return Validate(JsonSerializer.Deserialize<DiscordActivitySettings>(value, JsonOptions) ?? Defaults); }
        catch (Exception error) when (error is JsonException or ArgumentException) { return Validate(Defaults); }
    }

    public async Task<DiscordActivitySettings> Set(DiscordActivitySettings settings, CancellationToken cancellationToken = default)
    {
        settings = Validate(settings);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var setting = await db.ManagerSettings.FindAsync([SettingKey], cancellationToken);
            var value = JsonSerializer.Serialize(settings, JsonOptions);
            if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = SettingKey, Value = value });
            else setting.Value = value;
            await db.SaveChangesAsync(cancellationToken);
            return settings;
        }
        finally { Gate.Release(); }
    }

    public async Task<object> Payload(CancellationToken cancellationToken = default)
    {
        var settings = await Get(cancellationToken);
        var server = await serverSettings.Get(cancellationToken);
        return new
        {
            settings.ApplicationId,
            settings.DetailsTemplate,
            settings.StateTemplate,
            settings.ImageUrl,
            server.ServerName,
            server.WorldName,
            server.MaxPlayers
        };
    }

    internal static DiscordActivitySettings Validate(DiscordActivitySettings settings)
    {
        var applicationId = (settings.ApplicationId ?? "").Trim();
        if (applicationId.Length > 0 && !ValidApplicationId(applicationId))
            throw new ArgumentException("Discord application ID must contain 17–20 digits.");
        var imageUrl = (settings.ImageUrl ?? "").Trim();
        if (imageUrl.Length > 0 && (imageUrl.Length > 300 || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0))
            throw new ArgumentException("Discord activity image must be an HTTPS URL of at most 300 characters.");
        return new DiscordActivitySettings(applicationId,
            ValidateTemplate(settings.DetailsTemplate, "Details template"),
            ValidateTemplate(settings.StateTemplate, "State template"), imageUrl);
    }

    private static string ValidateTemplate(string? value, string label)
    {
        value = (value ?? "").Trim();
        if (value.Length is < 1 or > 128 || value.Any(char.IsControl))
            throw new ArgumentException($"{label} must be a single line of 1–128 characters.");
        foreach (Match match in PlaceholderPattern().Matches(value))
            if (match.Value is not ("{server}" or "{world}" or "{players}" or "{maxPlayers}" or "{freeSlots}" or "{region}"))
                throw new ArgumentException($"{label} contains unsupported placeholder {match.Value}.");
        var literal = PlaceholderPattern().Replace(value, "");
        if (literal.Contains('{') || literal.Contains('}')) throw new ArgumentException($"{label} contains an incomplete placeholder.");
        return value;
    }

    private static bool ValidApplicationId(string value) => value.Length is >= 17 and <= 20 && value.All(character => character is >= '0' and <= '9');

    [GeneratedRegex("\\{[^{}]*\\}")]
    private static partial Regex PlaceholderPattern();
}
