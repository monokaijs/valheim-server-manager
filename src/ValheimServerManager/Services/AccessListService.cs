using System.Text.RegularExpressions;

namespace ValheimServerManager.Services;

public sealed partial class AccessListService(IConfiguration config, AgentGateway agent)
{
    private readonly string _root = config["VSM_SAVE_PATH"] ?? "/data/worlds";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*_[^\\s]{1,128}$|^[0-9]{5,32}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9]{17}$")]
    private static partial Regex SteamIdPattern();

    public async Task<IReadOnlyList<string>> Read(string kind)
    {
        var path = PathFor(kind);
        if (!File.Exists(path)) return [];
        return (await File.ReadAllLinesAsync(path)).Select(x => x.Trim()).Where(IsDataLine).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<bool> IsSteamAdmin(string steamId)
    {
        if (!SteamIdPattern().IsMatch(steamId)) return false;
        return MatchesSteamAdmin(await Read("admin"), steamId);
    }

    internal static bool MatchesSteamAdmin(IEnumerable<string> admins, string steamId) =>
        SteamIdPattern().IsMatch(steamId) && (admins.Contains(steamId, StringComparer.Ordinal) || admins.Contains("Steam_" + steamId, StringComparer.Ordinal));

    internal static bool IsDataLine(string value) =>
        value.Length > 0 && !value.StartsWith('#') && !value.StartsWith("//", StringComparison.Ordinal);

    public async Task Add(string kind, string platformId)
    {
        ValidateId(platformId);
        if (agent.IsConnected)
        {
            await agent.Command($"access.{kind}.add", new { platformId }, TimeSpan.FromSeconds(10));
            return;
        }
        await MutateFile(kind, list => { if (!list.Contains(platformId, StringComparer.Ordinal)) list.Add(platformId); });
    }

    public async Task Remove(string kind, string platformId)
    {
        ValidateId(platformId);
        if (agent.IsConnected)
        {
            await agent.Command($"access.{kind}.remove", new { platformId }, TimeSpan.FromSeconds(10));
            return;
        }
        await MutateFile(kind, list => list.RemoveAll(x => x.Equals(platformId, StringComparison.Ordinal)));
    }

    private async Task MutateFile(string kind, Action<List<string>> mutate)
    {
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            var path = PathFor(kind);
            var list = File.Exists(path) ? (await File.ReadAllLinesAsync(path)).Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : [];
            mutate(list);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllLinesAsync(temp, list.Distinct(StringComparer.Ordinal));
            File.Move(temp, path, true);
        }
        finally { Gate.Release(); }
    }

    private string PathFor(string kind) => kind switch
    {
        "permitted" => Path.Combine(_root, "permittedlist.txt"),
        "banned" => Path.Combine(_root, "bannedlist.txt"),
        "admin" => Path.Combine(_root, "adminlist.txt"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdPattern().IsMatch(value))
            throw new ArgumentException("Use an exact Platform_UserId or numeric Steam ID without spaces.");
    }
}
