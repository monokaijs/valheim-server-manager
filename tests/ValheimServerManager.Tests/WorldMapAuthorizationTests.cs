using System.Net.Http.Json;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class WorldMapAuthorizationTests
{
    [Fact]
    public async Task AnonymousAndModeratorCannotReadMapOrTeleportAndAdminRequiresCsrf()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/api/v1/world-map", "/api/v1/world-map/terrain?worldId=world", "/api/v1/world-map/tiles?worldId=world", "/api/v1/world-map/tiles/0/0?worldId=world" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Role", "mod");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Role");
        }
        var destination = JsonSerializer.Serialize(new { commandId = Guid.NewGuid(), worldId = "world", platformId = "Steam_76561198000000001", session = "session", x = 100, z = 200 });
        var target = "/api/v1/world-map/players/9223372036854775806/teleport";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(target, Body(destination))).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "mod");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(target, Body(destination))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        var frame = await client.GetAsync("/api/v1/world-map"); Assert.Equal(HttpStatusCode.OK, frame.StatusCode); Assert.Contains("no-store", frame.Headers.CacheControl!.ToString());
        Assert.False((await client.PostAsync(target, Body(destination))).IsSuccessStatusCode);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        // Valid CSRF reaches domain validation; offline agent blocks all game mutation.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(target, Body(destination))).StatusCode);
    }
    private static StringContent Body(string body) => new(body, System.Text.Encoding.UTF8, "application/json");
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vsm-map-auth-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("VSM_DATA_PATH", _root); builder.UseSetting("VSM_SAVE_PATH", _root); builder.UseSetting("VSM_AUTOSTART", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "test"; options.DefaultChallengeScheme = "test"; options.DefaultForbidScheme = "test"; }).AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "76561198000000001"), new(ClaimTypes.Name, "Test admin"), new(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) { Response.StatusCode = 401; return Task.CompletedTask; }
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties) { Response.StatusCode = 403; return Task.CompletedTask; }
    }
}
