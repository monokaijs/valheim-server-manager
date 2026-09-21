using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ServerMessageTemplates(
    string Welcome,
    string Kick,
    string Ban,
    string Restart,
    string WhitelistRejected,
    string CompanionRequired);

public sealed partial class ServerMessageService(IServiceScopeFactory scopes, IConfiguration configuration)
{
    private const string SettingKey = "server.message-templates";
    private const string LegacyClientRequired = "{server} requires the VSM client companion. Restart Valheim after the bootstrap finishes installing it.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _serverName = configuration["SERVER_NAME"] ?? "Valheim Server";

    public static ServerMessageTemplates Defaults { get; } = new(
        "Welcome {player} to {server}.",
        "You were kicked from {server}. Reason: {reason}",
        "You were banned from {server}. Reason: {reason}",
        "{server} restarts in {seconds} seconds. {reason}",
        "You are not on the {server} whitelist. A join request was sent to the administrators.",
        "{server} requires the Server Manager client runtime. Restart Valheim after Server Manager finishes installing it.");

    public async Task<ServerMessageTemplates> Get(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var value = await db.ManagerSettings.AsNoTracking().Where(item => item.Key == SettingKey)
            .Select(item => item.Value).SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value)) return Defaults;
        try
        {
            var templates = Validate(JsonSerializer.Deserialize<ServerMessageTemplates>(value, JsonOptions) ?? Defaults);
            return templates.CompanionRequired == LegacyClientRequired
                ? templates with { CompanionRequired = Defaults.CompanionRequired }
                : templates;
        }
        catch (JsonException) { return Defaults; }
    }

    public async Task<ServerMessageTemplates> Set(ServerMessageTemplates templates, CancellationToken cancellationToken = default)
    {
        templates = Validate(templates);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var setting = await db.ManagerSettings.FindAsync([SettingKey], cancellationToken);
            var value = JsonSerializer.Serialize(templates, JsonOptions);
            if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = SettingKey, Value = value });
            else setting.Value = value;
            await db.SaveChangesAsync(cancellationToken);
            return templates;
        }
        finally { Gate.Release(); }
    }

    public async Task<object> Payload(CancellationToken cancellationToken = default) => new
    {
        serverName = _serverName,
        templates = await Get(cancellationToken)
    };

    public async Task<string> Render(string kind, string player = "", string reason = "", int seconds = 0,
        CancellationToken cancellationToken = default)
    {
        var templates = await Get(cancellationToken);
        var template = kind switch
        {
            "welcome" => templates.Welcome,
            "kick" => templates.Kick,
            "ban" => templates.Ban,
            "restart" => templates.Restart,
            "whitelistRejected" => templates.WhitelistRejected,
            "companionRequired" => templates.CompanionRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return RenderTemplate(template, _serverName, player, NormalizeReason(reason), seconds);
    }

    public static string NormalizeReason(string? reason)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) return "No reason provided.";
        if (reason.Length > 300) throw new ArgumentException("Reason cannot exceed 300 characters.");
        if (reason.Any(character => char.IsControl(character))) throw new ArgumentException("Reason must be a single line without control characters.");
        return reason;
    }

    internal static string RenderTemplate(string template, string server, string player, string reason, int seconds) =>
        template.Replace("{server}", server, StringComparison.Ordinal)
            .Replace("{player}", player, StringComparison.Ordinal)
            .Replace("{reason}", reason, StringComparison.Ordinal)
            .Replace("{seconds}", seconds.ToString(), StringComparison.Ordinal);

    internal static ServerMessageTemplates Validate(ServerMessageTemplates templates)
    {
        return new ServerMessageTemplates(
            ValidateTemplate(nameof(templates.Welcome), templates.Welcome, "server", "player"),
            ValidateTemplate(nameof(templates.Kick), templates.Kick, "server", "player", "reason"),
            ValidateTemplate(nameof(templates.Ban), templates.Ban, "server", "player", "reason"),
            ValidateTemplate(nameof(templates.Restart), templates.Restart, "server", "seconds", "reason"),
            ValidateTemplate(nameof(templates.WhitelistRejected), templates.WhitelistRejected, "server", "player"),
            ValidateTemplate(nameof(templates.CompanionRequired), templates.CompanionRequired, "server", "player"));
    }

    private static string ValidateTemplate(string name, string? value, params string[] allowed)
    {
        value = (value ?? "").Trim();
        if (value.Length is < 1 or > 500) throw new ArgumentException($"{name} must contain between 1 and 500 characters.");
        if (value.Any(character => char.IsControl(character) && character is not '\n')) throw new ArgumentException($"{name} contains unsupported control characters.");
        foreach (Match match in PlaceholderPattern().Matches(value))
            if (!allowed.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                throw new ArgumentException($"{name} contains unsupported placeholder {match.Value}.");
        if (value.Count(character => character == '\n') > 3) throw new ArgumentException($"{name} cannot exceed four lines.");
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    [GeneratedRegex("\\{([^{}]+)\\}")]
    private static partial Regex PlaceholderPattern();
}
