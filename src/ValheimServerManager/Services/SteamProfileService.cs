using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ValheimServerManager.Services;

public sealed record SteamProfile(string SteamId, string Name, string AvatarUrl, string ProfileUrl);

public sealed partial class SteamProfileService(IConfiguration configuration, ILogger<SteamProfileService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, (SteamProfile? Profile, DateTimeOffset Expires)> _cache = new();
    // Keep the API key out of IHttpClientFactory's request-URL logging.
    private readonly HttpClient _client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    { Timeout = TimeSpan.FromSeconds(15) };

    public void Dispose() => _client.Dispose();

    public async Task<IReadOnlyDictionary<string, SteamProfile>> Get(string? ids, CancellationToken cancellationToken)
    {
        var key = configuration["VSM_STEAM_API_KEY"];
        if (string.IsNullOrWhiteSpace(key)) return new Dictionary<string, SteamProfile>();
        var requested = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).Take(100).ToArray();
        if (requested.Length == 0) return new Dictionary<string, SteamProfile>();

        var now = DateTimeOffset.UtcNow;
        var missing = requested.Where(id => !_cache.TryGetValue(id, out var entry) || entry.Expires <= now).ToArray();
        if (missing.Length > 0)
        {
            try
            {
                var url = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key=" + Uri.EscapeDataString(key)
                    + "&steamids=" + string.Join(',', missing);
                using var response = await _client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var found = new Dictionary<string, SteamProfile>(StringComparer.Ordinal);
                foreach (var item in json.RootElement.GetProperty("response").GetProperty("players").EnumerateArray())
                {
                    var id = item.GetProperty("steamid").GetString() ?? "";
                    if (!SteamIdPattern().IsMatch(id)) continue;
                    var name = item.GetProperty("personaname").GetString() ?? "";
                    var avatar = item.TryGetProperty("avatarmedium", out var image) ? image.GetString() ?? "" : "";
                    if (!Uri.TryCreate(avatar, UriKind.Absolute, out var avatarUri) || avatarUri.Scheme != "https" ||
                        !(avatarUri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
                          avatarUri.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))) avatar = "";
                    // Construct the link from the validated ID; remote profileurl values are not trusted.
                    found[id] = new SteamProfile(id, name, avatar, "https://steamcommunity.com/profiles/" + id);
                }
                foreach (var id in missing)
                    _cache[id] = (found.GetValueOrDefault(id), now.AddMinutes(found.ContainsKey(id) ? 30 : 5));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Steam profile lookup failed ({ErrorType}).", error.GetType().Name);
                foreach (var id in missing) _cache[id] = (null, now.AddMinutes(2));
            }
        }
        return requested.Where(id => _cache.TryGetValue(id, out var entry) && entry.Profile is not null)
            .ToDictionary(id => id, id => _cache[id].Profile!, StringComparer.Ordinal);
    }

    private static string? Normalize(string id)
    {
        if (id.StartsWith("Steam_", StringComparison.Ordinal)) id = id[6..];
        return SteamIdPattern().IsMatch(id) ? id : null;
    }

    [GeneratedRegex("^[0-9]{17}$", RegexOptions.CultureInvariant)] private static partial Regex SteamIdPattern();
}
