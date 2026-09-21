using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ServerAccessSettings(bool PasswordEnabled, bool HasPassword, bool PublicListing, string ServerName, int Port);

public sealed class ServerSettingsService(IServiceScopeFactory scopes, IDataProtectionProvider protection, IConfiguration configuration)
{
    private const string EnabledKey = "server.password.enabled";
    private const string PasswordKey = "server.password.protected";
    private readonly IDataProtector _protector = protection.CreateProtector("server-password-v1");

    public async Task<ServerAccessSettings> Get(CancellationToken cancellationToken = default)
    {
        var values = await Values(cancellationToken);
        var fallback = configuration["SERVER_PASSWORD"] ?? "";
        var enabled = values.TryGetValue(EnabledKey, out var rawEnabled)
            ? bool.TryParse(rawEnabled, out var configured) && configured
            : fallback.Length > 0;
        return new ServerAccessSettings(
            enabled,
            EffectivePassword(values, fallback).Length > 0,
            enabled && configuration["SERVER_PUBLIC"] != "0",
            configuration["SERVER_NAME"] ?? "Valheim Server",
            configuration.GetValue("SERVER_PORT", 2456));
    }

    public async Task Set(bool passwordEnabled, string? password, CancellationToken cancellationToken = default)
    {
        var serverName = configuration["SERVER_NAME"] ?? "Valheim Server";
        if (!string.IsNullOrEmpty(password)) ValidatePassword(password, serverName);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var current = await db.ManagerSettings.AsNoTracking()
            .Where(item => item.Key == PasswordKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
        if (passwordEnabled) ValidatePassword(!string.IsNullOrEmpty(password) ? password : EffectivePassword(current, configuration["SERVER_PASSWORD"] ?? ""), serverName);
        await Put(db, EnabledKey, passwordEnabled ? "true" : "false", cancellationToken);
        if (!string.IsNullOrEmpty(password)) await Put(db, PasswordKey, _protector.Protect(password), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> BuildArguments(CancellationToken cancellationToken = default)
    {
        var values = await Values(cancellationToken);
        var fallback = configuration["SERVER_PASSWORD"] ?? "";
        var enabled = values.TryGetValue(EnabledKey, out var rawEnabled)
            ? bool.TryParse(rawEnabled, out var configured) && configured
            : fallback.Length > 0;
        var password = EffectivePassword(values, fallback);
        var name = configuration["SERVER_NAME"] ?? "Valheim Server";
        if (enabled) ValidatePassword(password, name);

        var arguments = new List<string>
        {
            "-name", name,
            "-port", configuration.GetValue("SERVER_PORT", 2456).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-world", configuration["SERVER_WORLD"] ?? "Dedicated",
            "-savedir", configuration["VSM_SAVE_PATH"] ?? "/data/worlds",
            "-public", enabled && configuration["SERVER_PUBLIC"] != "0" ? "1" : "0"
        };
        if (enabled) { arguments.Add("-password"); arguments.Add(password); }
        if (configuration.GetValue("SERVER_CROSSPLAY", false)) arguments.Add("-crossplay");
        return arguments;
    }

    internal static void ValidatePassword(string password, string serverName)
    {
        if (password.Length < 5) throw new ArgumentException("A Valheim server password must contain at least five characters.");
        if (password.Any(char.IsControl)) throw new ArgumentException("The server password cannot contain control characters.");
        if (serverName.Contains(password, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The server password cannot appear in the server name.");
    }

    private async Task<Dictionary<string, string>> Values(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        return await db.ManagerSettings.AsNoTracking()
            .Where(item => item.Key == EnabledKey || item.Key == PasswordKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
    }

    private string EffectivePassword(IReadOnlyDictionary<string, string> values, string fallback)
    {
        if (!values.TryGetValue(PasswordKey, out var protectedPassword)) return fallback;
        try { return _protector.Unprotect(protectedPassword); }
        catch (Exception error) { throw new InvalidOperationException("The stored server password cannot be decrypted.", error); }
    }

    private static async Task Put(ManagerDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.ManagerSettings.FindAsync([key], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = key, Value = value });
        else setting.Value = value;
    }
}
