using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ServerSettings(
    bool PasswordEnabled, bool HasPassword, bool PublicListing, string ServerName, string WorldName, int Port,
    bool Crossplay, string InstanceId, int SaveIntervalSeconds, int BackupCount, int BackupShortSeconds,
    int BackupLongSeconds, int MaxPlayers, bool ManageWorldModifiers, string Preset, string CombatModifier,
    string DeathPenaltyModifier, string ResourceModifier, string RaidModifier, string PortalModifier,
    bool NoBuildCost, bool PlayerEvents, bool PassiveMobs, bool NoMap);

public sealed record ServerSettingsMutation(
    bool PasswordEnabled, string? Password, bool PublicListing, string ServerName, string WorldName,
    bool Crossplay, string InstanceId, int SaveIntervalSeconds, int BackupCount, int BackupShortSeconds,
    int BackupLongSeconds, int MaxPlayers, bool ManageWorldModifiers, string Preset, string CombatModifier,
    string DeathPenaltyModifier, string ResourceModifier, string RaidModifier, string PortalModifier,
    bool NoBuildCost, bool PlayerEvents, bool PassiveMobs, bool NoMap);

public sealed class ServerSettingsService(IServiceScopeFactory scopes, IDataProtectionProvider protection, IConfiguration configuration)
{
    private const string Prefix = "server.";
    private const string PasswordEnabledKey = Prefix + "password.enabled";
    private const string PasswordKey = Prefix + "password.protected";
    private readonly IDataProtector _protector = protection.CreateProtector("server-password-v1");

    private static readonly HashSet<string> Presets = new(StringComparer.OrdinalIgnoreCase)
        { "normal", "casual", "easy", "hard", "hardcore", "immersive", "hammer" };
    private static readonly Dictionary<string, HashSet<string>> ModifierValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["combat"] = new(StringComparer.OrdinalIgnoreCase) { "", "veryeasy", "easy", "hard", "veryhard" },
        ["deathpenalty"] = new(StringComparer.OrdinalIgnoreCase) { "", "casual", "veryeasy", "easy", "hard", "hardcore" },
        ["resources"] = new(StringComparer.OrdinalIgnoreCase) { "", "muchless", "less", "more", "muchmore", "most" },
        ["raids"] = new(StringComparer.OrdinalIgnoreCase) { "", "none", "muchless", "less", "more", "muchmore" },
        ["portals"] = new(StringComparer.OrdinalIgnoreCase) { "", "casual", "hard", "veryhard" }
    };

    public async Task<ServerSettings> Get(CancellationToken cancellationToken = default)
    {
        var values = await Values(cancellationToken);
        var fallbackPassword = configuration["SERVER_PASSWORD"] ?? "";
        var passwordEnabled = Bool(values, PasswordEnabledKey, fallbackPassword.Length > 0);
        return new ServerSettings(
            passwordEnabled,
            EffectivePassword(values, fallbackPassword).Length > 0,
            passwordEnabled && Bool(values, Prefix + "public", configuration["SERVER_PUBLIC"] != "0"),
            Text(values, Prefix + "name", configuration["SERVER_NAME"] ?? "Valheim Server"),
            Text(values, Prefix + "world", configuration["SERVER_WORLD"] ?? "Dedicated"),
            configuration.GetValue("SERVER_PORT", 2456),
            Bool(values, Prefix + "crossplay", configuration.GetValue("SERVER_CROSSPLAY", false)),
            Text(values, Prefix + "instance-id", configuration["SERVER_INSTANCE_ID"] ?? ""),
            Int(values, Prefix + "save-interval", configuration.GetValue("SERVER_SAVE_INTERVAL", 1800)),
            Int(values, Prefix + "backup-count", configuration.GetValue("SERVER_BACKUPS", 4)),
            Int(values, Prefix + "backup-short", configuration.GetValue("SERVER_BACKUP_SHORT", 7200)),
            Int(values, Prefix + "backup-long", configuration.GetValue("SERVER_BACKUP_LONG", 43200)),
            Int(values, Prefix + "max-players", configuration.GetValue("SERVER_MAX_PLAYERS", 10)),
            Bool(values, Prefix + "world-modifiers.enabled", configuration.GetValue("SERVER_MANAGE_WORLD_MODIFIERS", false)),
            Text(values, Prefix + "world-modifiers.preset", configuration["SERVER_PRESET"] ?? "normal").ToLowerInvariant(),
            Text(values, Prefix + "world-modifiers.combat", configuration["SERVER_MODIFIER_COMBAT"] ?? "").ToLowerInvariant(),
            Text(values, Prefix + "world-modifiers.death-penalty", configuration["SERVER_MODIFIER_DEATH_PENALTY"] ?? "").ToLowerInvariant(),
            Text(values, Prefix + "world-modifiers.resources", configuration["SERVER_MODIFIER_RESOURCES"] ?? "").ToLowerInvariant(),
            Text(values, Prefix + "world-modifiers.raids", configuration["SERVER_MODIFIER_RAIDS"] ?? "").ToLowerInvariant(),
            Text(values, Prefix + "world-modifiers.portals", configuration["SERVER_MODIFIER_PORTALS"] ?? "").ToLowerInvariant(),
            Bool(values, Prefix + "world-modifiers.no-build-cost", configuration.GetValue("SERVER_SETKEY_NO_BUILD_COST", false)),
            Bool(values, Prefix + "world-modifiers.player-events", configuration.GetValue("SERVER_SETKEY_PLAYER_EVENTS", false)),
            Bool(values, Prefix + "world-modifiers.passive-mobs", configuration.GetValue("SERVER_SETKEY_PASSIVE_MOBS", false)),
            Bool(values, Prefix + "world-modifiers.no-map", configuration.GetValue("SERVER_SETKEY_NO_MAP", false)));
    }

    public async Task<ServerSettings> Set(ServerSettingsMutation request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var currentValues = await Values(cancellationToken);
        var effectivePassword = !string.IsNullOrEmpty(request.Password)
            ? request.Password
            : EffectivePassword(currentValues, configuration["SERVER_PASSWORD"] ?? "");
        if (request.PasswordEnabled) ValidatePassword(effectivePassword, request.ServerName);
        else if (!string.IsNullOrEmpty(request.Password)) ValidatePassword(request.Password, request.ServerName);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var updates = new Dictionary<string, string>
        {
            [PasswordEnabledKey] = request.PasswordEnabled.ToString(),
            [Prefix + "public"] = request.PublicListing.ToString(),
            [Prefix + "name"] = request.ServerName.Trim(),
            [Prefix + "world"] = request.WorldName.Trim(),
            [Prefix + "crossplay"] = request.Crossplay.ToString(),
            [Prefix + "instance-id"] = request.InstanceId.Trim(),
            [Prefix + "save-interval"] = request.SaveIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            [Prefix + "backup-count"] = request.BackupCount.ToString(CultureInfo.InvariantCulture),
            [Prefix + "backup-short"] = request.BackupShortSeconds.ToString(CultureInfo.InvariantCulture),
            [Prefix + "backup-long"] = request.BackupLongSeconds.ToString(CultureInfo.InvariantCulture),
            [Prefix + "max-players"] = request.MaxPlayers.ToString(CultureInfo.InvariantCulture),
            [Prefix + "world-modifiers.enabled"] = request.ManageWorldModifiers.ToString(),
            [Prefix + "world-modifiers.preset"] = request.Preset.ToLowerInvariant(),
            [Prefix + "world-modifiers.combat"] = request.CombatModifier.ToLowerInvariant(),
            [Prefix + "world-modifiers.death-penalty"] = request.DeathPenaltyModifier.ToLowerInvariant(),
            [Prefix + "world-modifiers.resources"] = request.ResourceModifier.ToLowerInvariant(),
            [Prefix + "world-modifiers.raids"] = request.RaidModifier.ToLowerInvariant(),
            [Prefix + "world-modifiers.portals"] = request.PortalModifier.ToLowerInvariant(),
            [Prefix + "world-modifiers.no-build-cost"] = request.NoBuildCost.ToString(),
            [Prefix + "world-modifiers.player-events"] = request.PlayerEvents.ToString(),
            [Prefix + "world-modifiers.passive-mobs"] = request.PassiveMobs.ToString(),
            [Prefix + "world-modifiers.no-map"] = request.NoMap.ToString()
        };
        foreach (var (key, value) in updates) await Put(db, key, value, cancellationToken);
        if (!string.IsNullOrEmpty(request.Password)) await Put(db, PasswordKey, _protector.Protect(request.Password), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await Get(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> BuildArguments(CancellationToken cancellationToken = default)
    {
        var settings = await Get(cancellationToken);
        var values = await Values(cancellationToken);
        var password = EffectivePassword(values, configuration["SERVER_PASSWORD"] ?? "");
        if (settings.PasswordEnabled) ValidatePassword(password, settings.ServerName);

        var arguments = new List<string>
        {
            "-name", settings.ServerName,
            "-port", settings.Port.ToString(CultureInfo.InvariantCulture),
            "-world", settings.WorldName,
            "-savedir", configuration["VSM_SAVE_PATH"] ?? "/data/worlds",
            "-public", settings.PublicListing ? "1" : "0",
            "-saveinterval", settings.SaveIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            "-backups", settings.BackupCount.ToString(CultureInfo.InvariantCulture),
            "-backupshort", settings.BackupShortSeconds.ToString(CultureInfo.InvariantCulture),
            "-backuplong", settings.BackupLongSeconds.ToString(CultureInfo.InvariantCulture)
        };
        if (settings.PasswordEnabled) Add(arguments, "-password", password);
        if (settings.Crossplay) arguments.Add("-crossplay");
        if (settings.InstanceId.Length > 0) Add(arguments, "-instanceid", settings.InstanceId);
        if (settings.ManageWorldModifiers)
        {
            Add(arguments, "-preset", settings.Preset);
            AddModifier(arguments, "combat", settings.CombatModifier);
            AddModifier(arguments, "deathpenalty", settings.DeathPenaltyModifier);
            AddModifier(arguments, "resources", settings.ResourceModifier);
            AddModifier(arguments, "raids", settings.RaidModifier);
            AddModifier(arguments, "portals", settings.PortalModifier);
            if (settings.NoBuildCost) Add(arguments, "-setkey", "nobuildcost");
            if (settings.PlayerEvents) Add(arguments, "-setkey", "playerevents");
            if (settings.PassiveMobs) Add(arguments, "-setkey", "passivemobs");
            if (settings.NoMap) Add(arguments, "-setkey", "nomap");
        }
        return arguments;
    }

    public async Task<int> GetMaxPlayers(CancellationToken cancellationToken = default) => (await Get(cancellationToken)).MaxPlayers;

    internal static void ValidatePassword(string password, string serverName)
    {
        if (password.Length < 5) throw new ArgumentException("A Valheim server password must contain at least five characters.");
        if (password.Any(char.IsControl)) throw new ArgumentException("The server password cannot contain control characters.");
        if (serverName.Contains(password, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The server password cannot appear in the server name.");
    }

    private static void Validate(ServerSettingsMutation request)
    {
        ValidateText(request.ServerName, "Server name", 1, 64);
        ValidateText(request.WorldName, "World name", 1, 64);
        if (request.WorldName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("The world name cannot contain path separators.");
        ValidateText(request.InstanceId, "Instance ID", 0, 64);
        if (request.SaveIntervalSeconds is < 60 or > 86400) throw new ArgumentOutOfRangeException(nameof(request.SaveIntervalSeconds), "Save interval must be between 60 and 86400 seconds.");
        if (request.BackupCount is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(request.BackupCount), "Backup count must be between 1 and 50.");
        if (request.BackupShortSeconds is < 60 or > 604800) throw new ArgumentOutOfRangeException(nameof(request.BackupShortSeconds), "Short backup interval must be between 60 and 604800 seconds.");
        if (request.BackupLongSeconds is < 60 or > 2592000) throw new ArgumentOutOfRangeException(nameof(request.BackupLongSeconds), "Long backup interval must be between 60 and 2592000 seconds.");
        if (request.BackupLongSeconds < request.BackupShortSeconds) throw new ArgumentException("Long backup interval cannot be shorter than the short backup interval.");
        if (request.MaxPlayers is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request.MaxPlayers), "Player limit must be between 1 and 100.");
        if (!Presets.Contains(request.Preset)) throw new ArgumentException("Unsupported world preset.");
        ValidateModifier("combat", request.CombatModifier);
        ValidateModifier("deathpenalty", request.DeathPenaltyModifier);
        ValidateModifier("resources", request.ResourceModifier);
        ValidateModifier("raids", request.RaidModifier);
        ValidateModifier("portals", request.PortalModifier);
    }

    private static void ValidateText(string value, string label, int minimum, int maximum)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length < minimum || trimmed.Length > maximum) throw new ArgumentException($"{label} must be between {minimum} and {maximum} characters.");
        if (trimmed.Any(char.IsControl)) throw new ArgumentException($"{label} cannot contain control characters.");
    }

    private static void ValidateModifier(string name, string value)
    {
        if (!ModifierValues[name].Contains(value ?? "")) throw new ArgumentException($"Unsupported {name} modifier.");
    }

    private async Task<Dictionary<string, string>> Values(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        return await db.ManagerSettings.AsNoTracking()
            .Where(item => item.Key.StartsWith(Prefix))
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
    }

    private string EffectivePassword(IReadOnlyDictionary<string, string> values, string fallback)
    {
        if (!values.TryGetValue(PasswordKey, out var protectedPassword)) return fallback;
        try { return _protector.Unprotect(protectedPassword); }
        catch (Exception error) { throw new InvalidOperationException("The stored server password cannot be decrypted.", error); }
    }

    private static bool Bool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : fallback;
    private static int Int(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static string Text(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static void Add(List<string> arguments, string key, string value) { arguments.Add(key); arguments.Add(value); }
    private static void AddModifier(List<string> arguments, string name, string value)
    {
        if (value.Length == 0) return;
        arguments.Add("-modifier"); arguments.Add(name); arguments.Add(value);
    }

    private static async Task Put(ManagerDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.ManagerSettings.FindAsync([key], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = value });
        else setting.Value = value;
    }
}
