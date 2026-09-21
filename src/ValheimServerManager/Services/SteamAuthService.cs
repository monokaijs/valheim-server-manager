using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace ValheimServerManager.Services;

public sealed partial class SteamAuthService(IConfiguration configuration, IDataProtectionProvider protection, IHttpClientFactory clients)
{
    // Steam's provider URL is an XRDS discovery document. Its advertised
    // OpenID 2.0 server endpoint is /openid/login; navigating to the discovery
    // URL directly makes browsers download the XML document instead.
    internal const string Provider = "https://steamcommunity.com/openid/login";
    private const string StateCookie = "vsm.steam.state";
    private readonly IDataProtector _state = protection.CreateProtector("steam-openid-state-v1");

    public string CreateLoginUrl(HttpContext context)
    {
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var state = _state.Protect($"{issued}:{nonce}");
        context.Response.Cookies.Append(StateCookie, nonce, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            IsEssential = true
        });
        var returnTo = PublicUrl(context) + "/api/v1/auth/steam/callback?state=" + Uri.EscapeDataString(state);
        return QueryHelpers.AddQueryString(Provider, new Dictionary<string, string?>
        {
            ["openid.ns"] = "http://specs.openid.net/auth/2.0",
            ["openid.mode"] = "checkid_setup",
            ["openid.return_to"] = returnTo,
            ["openid.realm"] = PublicUrl(context) + "/",
            ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
            ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select"
        });
    }

    public async Task<string?> ValidateCallback(HttpContext context, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (!query.TryGetValue("state", out var protectedState) || !context.Request.Cookies.TryGetValue(StateCookie, out var cookieNonce)) return null;
        context.Response.Cookies.Delete(StateCookie);
        string state;
        try { state = _state.Unprotect(protectedState.ToString()); }
        catch (CryptographicException) { return null; }
        var separator = state.IndexOf(':');
        if (separator <= 0 || !long.TryParse(state[..separator], CultureInfo.InvariantCulture, out var issued)) return null;
        var expectedNonce = Encoding.UTF8.GetBytes(state[(separator + 1)..]);
        var suppliedNonce = Encoding.UTF8.GetBytes(cookieNonce);
        if (expectedNonce.Length != suppliedNonce.Length || !CryptographicOperations.FixedTimeEquals(expectedNonce, suppliedNonce)) return null;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issued);
        if (issuedAt > DateTimeOffset.UtcNow.AddMinutes(1) || DateTimeOffset.UtcNow - issuedAt > TimeSpan.FromMinutes(10)) return null;

        if (query["openid.mode"] != "id_res" || query["openid.ns"] != "http://specs.openid.net/auth/2.0") return null;
        if (!Uri.TryCreate(query["openid.op_endpoint"].ToString(), UriKind.Absolute, out var endpoint) || !endpoint.AbsoluteUri.Equals(Provider, StringComparison.OrdinalIgnoreCase)) return null;
        var expectedReturnTo = PublicUrl(context) + "/api/v1/auth/steam/callback?state=" + Uri.EscapeDataString(protectedState.ToString());
        if (!query["openid.return_to"].ToString().Equals(expectedReturnTo, StringComparison.Ordinal)) return null;
        var claimed = query["openid.claimed_id"].ToString();
        if (!claimed.Equals(query["openid.identity"].ToString(), StringComparison.Ordinal)) return null;
        var steamId = SteamIdFromClaimedId(claimed);
        if (steamId is null) return null;

        var form = query.Where(item => item.Key.StartsWith("openid.", StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.Ordinal);
        form["openid.mode"] = "check_authentication";
        using var response = await clients.CreateClient("steam").PostAsync(Provider, new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var verification = await response.Content.ReadAsStringAsync(cancellationToken);
        return verification.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("is_valid:true", StringComparer.Ordinal) ? steamId : null;
    }

    internal static string? SteamIdFromClaimedId(string claimedId)
    {
        var match = ClaimedIdPattern().Match(claimedId);
        return match.Success ? match.Groups[1].Value : null;
    }

    private string PublicUrl(HttpContext context)
    {
        var configured = configuration["VSM_PUBLIC_URL"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
            return uri.GetLeftPart(UriPartial.Authority);
        return $"{context.Request.Scheme}://{context.Request.Host}";
    }

    [GeneratedRegex("^https?://steamcommunity\\.com/openid/id/([0-9]{17})/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaimedIdPattern();
}
