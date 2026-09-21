using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ModConfigEntry(string Section, string Key, string Value, string Description, string SettingType,
    string DefaultValue, string AcceptableValues, bool Sensitive, bool HasValue);
public sealed record ModConfigFile(string File, string PluginGuid, string PluginName, string Revision,
    DateTimeOffset ModifiedAt, IReadOnlyList<ModConfigEntry> Entries);
public sealed record ModConfigValueMutation(string Section, string Key, string Value);

public sealed partial class ModConfigService(
    IServiceScopeFactory scopes,
    PluginRegistryService registry,
    ServerState state,
    AgentGateway agent,
    ProcessSupervisor supervisor,
    AuditService audit,
    IConfiguration config)
{
    private const long MaxConfigBytes = 2 * 1024 * 1024;
    private readonly string _configRoot = Path.Combine(config["VSM_BEPINEX_PATH"] ?? "/data/server/BepInEx", "config");
    private readonly string _backupRoot = Path.Combine(config["VSM_DATA_PATH"] ?? "/data/manager", "config-backups");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^\\s*\\[(?<section>[^]\\r\\n]+)]\\s*$")]
    private static partial Regex SectionPattern();

    [GeneratedRegex("^(?<before>\\s*(?<key>[^#;=\\r\\n][^=\\r\\n]*?)\\s*=\\s*)(?<value>.*)$")]
    private static partial Regex EntryPattern();

    [GeneratedRegex("password|secret|token|api.?key|private.?key|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SensitivePattern();

    public async Task<IReadOnlyList<ModConfigFile>> List(Guid modId, CancellationToken cancellationToken = default)
    {
        var owned = await ResolveOwnedFiles(modId, cancellationToken);
        var result = new List<ModConfigFile>();
        foreach (var binding in owned.OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase))
        {
            var path = SafePath(binding.File);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            if (info.Length > MaxConfigBytes) continue;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            result.Add(new ModConfigFile(binding.File, binding.PluginGuid, binding.PluginName, Hash(bytes),
                info.LastWriteTimeUtc, ParseText(Decode(bytes), maskSensitive: true)));
        }
        return result;
    }

    public async Task<ModConfigFile> Update(Guid modId, string file, string revision,
        IReadOnlyCollection<ModConfigValueMutation> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 500) throw new ArgumentException("Change between 1 and 500 settings at a time.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var owned = await ResolveOwnedFiles(modId, cancellationToken);
            var binding = owned.SingleOrDefault(item => item.File.Equals(NormalizeRelative(file), StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("That configuration file is not owned by this package.");
            var path = SafePath(binding.File);
            if (!File.Exists(path)) throw new KeyNotFoundException("Configuration file was not found.");
            var original = await File.ReadAllBytesAsync(path, cancellationToken);
            if (original.Length > MaxConfigBytes) throw new InvalidDataException("Configuration file exceeds the 2 MiB limit.");
            if (!Hash(original).Equals(revision ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configuration changed since it was opened. Reload it before saving.");

            var replacements = values.ToDictionary(item => EntryId(item.Section, item.Key), item => ValidateValue(item.Value), StringComparer.Ordinal);
            var changed = ApplyValues(Decode(original), replacements);
            if (changed.Equals(Decode(original), StringComparison.Ordinal))
                throw new InvalidOperationException("No configuration values changed.");

            Backup(modId, binding.File, original);
            var temporary = path + ".vsm-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var encoding = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf
                    ? new UTF8Encoding(true) : new UTF8Encoding(false);
                await File.WriteAllTextAsync(temporary, changed, encoding, cancellationToken);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }

            await MarkPending(cancellationToken);
            await audit.Write("mod.config.update", modId.ToString(), "success", $"file:{binding.File}; settings:{values.Count}");
            var updated = await File.ReadAllBytesAsync(path, cancellationToken);
            return new ModConfigFile(binding.File, binding.PluginGuid, binding.PluginName, Hash(updated),
                File.GetLastWriteTimeUtc(path), ParseText(Decode(updated), maskSensitive: true));
        }
        finally { _gate.Release(); }
    }

    public async Task Apply(CancellationToken cancellationToken = default)
    {
        if (!await HasPending(cancellationToken)) return;
        if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60));
        var previousGeneration = agent.Generation;
        await supervisor.Restart(TimeSpan.Zero, cancellationToken);
        var started = DateTimeOffset.UtcNow;
        while ((!agent.IsConnected || agent.Generation <= previousGeneration) && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(120))
            await Task.Delay(1000, cancellationToken);
        if (!agent.IsConnected || agent.Generation <= previousGeneration)
            throw new TimeoutException("Server agent did not complete a fresh handshake within 120 seconds. Config backups were retained.");
        await ClearPending(cancellationToken);
        await audit.Write("mod.config.apply", "pending configuration", "success", "World saved and server restarted.");
    }

    public async Task<bool> HasPending(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        return await db.ManagerSettings.AnyAsync(item => item.Key == "configs.pending" && item.Value == "true", cancellationToken);
    }

    public async Task ClearPending(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var setting = await db.ManagerSettings.FindAsync(["configs.pending"], cancellationToken);
        if (setting is not null) db.ManagerSettings.Remove(setting);
        await db.SaveChangesAsync(cancellationToken);
        state.RestartRequired = await db.ManagerSettings.AnyAsync(item => item.Key == "mods.pending" && item.Value == "true", cancellationToken);
    }

    internal static IReadOnlyList<ModConfigEntry> ParseText(string text, bool maskSensitive)
    {
        var entries = new List<ModConfigEntry>();
        var section = "General";
        var description = "";
        var type = "String";
        var defaultValue = "";
        var acceptable = "";
        foreach (var line in SplitLines(text))
        {
            var sectionMatch = SectionPattern().Match(line);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups["section"].Value.Trim();
                description = defaultValue = acceptable = "";
                type = "String";
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var comment = trimmed.TrimStart('#').Trim();
                if (comment.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)) description = comment[12..].Trim();
                else if (comment.StartsWith("Setting type:", StringComparison.OrdinalIgnoreCase)) type = comment[13..].Trim();
                else if (comment.StartsWith("Default value:", StringComparison.OrdinalIgnoreCase)) defaultValue = comment[14..].Trim();
                else if (comment.StartsWith("Acceptable values:", StringComparison.OrdinalIgnoreCase)) acceptable = comment[18..].Trim();
                else if (comment.StartsWith("Acceptable value range:", StringComparison.OrdinalIgnoreCase)) acceptable = comment[23..].Trim();
                continue;
            }
            var match = EntryPattern().Match(line);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value.Trim();
            var actual = match.Groups["value"].Value.Trim();
            var sensitive = SensitivePattern().IsMatch(section + " " + key);
            entries.Add(new ModConfigEntry(section, key, sensitive && maskSensitive ? "" : actual, description, type,
                defaultValue, acceptable, sensitive, actual.Length > 0));
            description = defaultValue = acceptable = "";
            type = "String";
        }
        return entries;
    }

    internal static string ApplyValues(string text, IReadOnlyDictionary<string, string> replacements)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text).ToArray();
        var trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
        var section = "General";
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var sectionMatch = SectionPattern().Match(lines[index]);
            if (sectionMatch.Success) { section = sectionMatch.Groups["section"].Value.Trim(); continue; }
            var match = EntryPattern().Match(lines[index]);
            if (!match.Success) continue;
            var id = EntryId(section, match.Groups["key"].Value.Trim());
            if (!replacements.TryGetValue(id, out var value)) continue;
            lines[index] = match.Groups["before"].Value + value;
            found.Add(id);
        }
        var missing = replacements.Keys.Except(found, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new KeyNotFoundException("One or more configuration settings no longer exist.");
        return string.Join(newline, lines) + (trailingNewline ? newline : "");
    }

    private async Task<HashSet<ConfigBinding>> ResolveOwnedFiles(Guid modId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var mod = await db.InstalledMods.AsNoTracking().SingleOrDefaultAsync(item => item.Id == modId, cancellationToken)
            ?? throw new KeyNotFoundException("Mod was not found.");
        if (mod.Protected) throw new InvalidOperationException("Protected infrastructure configuration is not editable here.");
        var owned = (JsonSerializer.Deserialize<string[]>(mod.FilesJson, JsonOptions) ?? [])
            .Select(NormalizeRelative).Where(path => path.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindings = new HashSet<ConfigBinding>();
        foreach (var file in owned.Where(path => path.StartsWith("config/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
            bindings.Add(new ConfigBinding(file["config/".Length..], "", mod.Name));

        foreach (var plugin in await registry.Read(cancellationToken))
        {
            if (!owned.Contains(NormalizeRelative(plugin.Dll))) continue;
            if (plugin.ConfigFile.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                bindings.Add(new ConfigBinding(plugin.ConfigFile, plugin.Guid, plugin.Name));
            if (!Directory.Exists(_configRoot)) continue;
            foreach (var path in Directory.EnumerateFiles(_configRoot, plugin.Guid + "*.cfg", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.Equals(plugin.Guid + ".cfg", StringComparison.OrdinalIgnoreCase) || name.StartsWith(plugin.Guid + ".", StringComparison.OrdinalIgnoreCase))
                    bindings.Add(new ConfigBinding(name, plugin.Guid, plugin.Name));
            }
        }
        return bindings.GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.PluginGuid.Length).First()).ToHashSet();
    }

    private async Task MarkPending(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var setting = await db.ManagerSettings.FindAsync(["configs.pending"], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = "configs.pending", Value = "true" });
        else setting.Value = "true";
        await db.SaveChangesAsync(cancellationToken);
        state.RestartRequired = true;
    }

    private void Backup(Guid modId, string relative, byte[] bytes)
    {
        var directory = Path.Combine(_backupRoot, modId.ToString("N"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant());
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".cfg");
        File.WriteAllBytes(target, bytes);
        foreach (var stale in Directory.EnumerateFiles(directory, "*.cfg").OrderByDescending(File.GetLastWriteTimeUtc).Skip(20)) File.Delete(stale);
    }

    private string SafePath(string relative) => ResolveSafePath(_configRoot, relative);

    internal static string ResolveSafePath(string root, string relative)
    {
        relative = NormalizeRelative(relative);
        if (!relative.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only BepInEx .cfg files are supported.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.Ordinal)) throw new InvalidDataException("Configuration path escaped the BepInEx config directory.");
        var current = path;
        while (current.StartsWith(fullRoot, StringComparison.Ordinal) && !current.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are not supported for mod configuration.");
            current = Path.GetDirectoryName(current) ?? "";
        }
        return path;
    }

    private static string NormalizeRelative(string? value) => PluginRegistryService.CleanRelative(value);
    private static string EntryId(string section, string key) => section + "\0" + key;
    private static string ValidateValue(string? value)
    {
        value ??= "";
        if (value.Length > 16_384 || value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Configuration values must be a single line of at most 16 KiB.");
        return value;
    }
    private static IEnumerable<string> SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n').SkipLast(text.EndsWith("\n", StringComparison.Ordinal) ? 1 : 0);
    private static string Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record ConfigBinding(string File, string PluginGuid, string PluginName);
}
