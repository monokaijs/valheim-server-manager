using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record CreatedApiToken(Guid Id, string Name, string Token, string Prefix, string[] Scopes, DateTimeOffset CreatedAt);
public sealed record ApiTokenSummary(Guid Id, string Name, string Prefix, string[] Scopes, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

public sealed class ApiTokenService(IServiceScopeFactory scopes)
{
    public static readonly HashSet<string> AllowedScopes = ["whitelist.read", "whitelist.write", "join-requests.read", "join-requests.write"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ApiTokenSummary>> List(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var records = await db.ApiTokens.AsNoTracking().ToListAsync(cancellationToken);
        return records.OrderByDescending(item => item.CreatedAt).Select(item => new ApiTokenSummary(item.Id, item.Name, item.Prefix,
            JsonSerializer.Deserialize<string[]>(item.ScopesJson, JsonOptions) ?? [],
            item.CreatedAt, item.LastUsedAt, item.RevokedAt)).ToArray();
    }

    public async Task<CreatedApiToken> Create(string name, IEnumerable<string> requestedScopes, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Token name must contain between 1 and 80 characters.");
        var tokenScopes = requestedScopes.Distinct(StringComparer.Ordinal).ToArray();
        if (tokenScopes.Length == 0 || tokenScopes.Any(scope => !AllowedScopes.Contains(scope)))
            throw new ArgumentException("Choose at least one supported API scope.");
        var id = Guid.NewGuid();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = $"vsm1_{id:N}_{secret}";
        var prefix = token[..Math.Min(22, token.Length)];
        var createdAt = DateTimeOffset.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        db.ApiTokens.Add(new ApiToken
        {
            Id = id, Name = name, Prefix = prefix, TokenHash = Hash(token),
            ScopesJson = JsonSerializer.Serialize(tokenScopes, JsonOptions), CreatedAt = createdAt
        });
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiToken(id, name, token, prefix, tokenScopes, createdAt);
    }

    public async Task Revoke(Guid id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var token = await db.ApiTokens.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("API token was not found.");
        token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ClaimsPrincipal?> Authenticate(string? authorization, string requiredScope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var raw = authorization[7..].Trim();
        if (raw.Length < 70 || !raw.StartsWith("vsm1_", StringComparison.Ordinal) || raw[37] != '_'
            || !Guid.TryParseExact(raw.Substring(5, 32), "N", out var id) || raw.Length - 38 < 32) return null;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var token = await db.ApiTokens.SingleOrDefaultAsync(item => item.Id == id && item.RevokedAt == null, cancellationToken);
        if (token is null || !FixedEquals(token.TokenHash, Hash(raw))) return null;
        var tokenScopes = JsonSerializer.Deserialize<string[]>(token.ScopesJson, JsonOptions) ?? [];
        if (!tokenScopes.Contains(requiredScope, StringComparer.Ordinal)) return new ClaimsPrincipal(new ClaimsIdentity());
        token.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, token.Id.ToString()),
            new Claim(ClaimTypes.Name, "api-token:" + token.Name),
            new Claim("vsm_scope", requiredScope)
        ], "VsmApiToken"));
    }

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
