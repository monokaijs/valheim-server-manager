using System.Text.Json;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager;
using ValheimServerManager.Data;
using ValheimServerManager.Hubs;
using ValheimServerManager.Models;
using ValheimServerManager.Services;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["VSM_DATA_PATH"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataPath);

builder.Services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(dataPath, "manager.db")}"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "vsm.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.EventsType = typeof(SteamAdminCookieEvents);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys"))).SetApplicationName("ValheimServerManager");
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; options.Cookie.Name = "vsm.csrf"; options.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
    context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("service-api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSignalR().AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("webhooks", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("thunderstore", client => { client.Timeout = TimeSpan.FromMinutes(5); client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimServerManager/1.0"); });
builder.Services.AddHttpClient("steam", client => { client.Timeout = TimeSpan.FromSeconds(15); client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimServerManager/1.0"); });
builder.Services.AddHttpClient("manager-updates", client => { client.Timeout = TimeSpan.FromSeconds(20); client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimServerManager/1.6"); });
builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<InventoryInspectionService>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<AgentGateway>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<AccessListService>();
builder.Services.AddSingleton<SafeConsoleService>();
builder.Services.AddSingleton<ModService>();
builder.Services.AddSingleton<ClientModManifestService>();
builder.Services.AddSingleton<ServerCharacterService>();
builder.Services.AddSingleton<ServerCharacterSettingsService>();
builder.Services.AddSingleton<ServerSettingsService>();
builder.Services.AddSingleton<ServerMessageService>();
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddSingleton<JoinRequestService>();
builder.Services.AddSingleton<PluginRegistryService>();
builder.Services.AddSingleton<ModConfigService>();
builder.Services.AddSingleton<ManagerUpdateService>();
builder.Services.AddSingleton<ProcessSupervisor>();
builder.Services.AddSingleton<SteamAuthService>();
builder.Services.AddScoped<SteamAdminCookieEvents>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddHostedService<WebhookDispatcher>();
builder.Services.AddHostedService<ModUpdateChecker>();
builder.Services.AddHostedService<ManagerUpdateChecker>();

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ApiTokens" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_ApiTokens" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "TokenHash" TEXT NOT NULL,
            "Prefix" TEXT NOT NULL,
            "ScopesJson" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "LastUsedAt" TEXT NULL,
            "RevokedAt" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApiTokens_TokenHash" ON "ApiTokens" ("TokenHash");
        CREATE TABLE IF NOT EXISTS "JoinRequests" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_JoinRequests" PRIMARY KEY,
            "PlatformId" TEXT NOT NULL,
            "PlayerName" TEXT NOT NULL,
            "Status" TEXT NOT NULL,
            "RequestedAt" TEXT NOT NULL,
            "LastAttemptAt" TEXT NOT NULL,
            "AttemptCount" INTEGER NOT NULL,
            "ResolvedAt" TEXT NULL,
            "ResolvedBy" TEXT NOT NULL,
            "CorrelationId" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_JoinRequests_Status_LastAttemptAt" ON "JoinRequests" ("Status", "LastAttemptAt");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_JoinRequests_PendingPlatform" ON "JoinRequests" ("PlatformId") WHERE "Status" = 'pending';
        """);
    scope.ServiceProvider.GetRequiredService<ServerState>().RestartRequired = await db.ManagerSettings.AnyAsync(x =>
        (x.Key == "mods.pending" || x.Key == "configs.pending") && x.Value == "true");
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    await next();
});
app.UseExceptionHandler();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Task.CompletedTask;
    });
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        else if (context.Context.Request.Path.StartsWithSegments("/assets"))
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    if (context.Request.Path.StartsWithSegments("/api/v1") && method is not ("GET" or "HEAD" or "OPTIONS"))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});

app.Map("/internal/agent", async context => await context.RequestServices.GetRequiredService<AgentGateway>().Accept(context));
app.MapGet("/healthz", (ServerState state) => Results.Ok(new { status = "ok", serverStatus = state.Status, agentConnected = state.AgentConnected })).AllowAnonymous();
app.MapHub<LiveHub>("/hubs/live").RequireAuthorization();

var auth = app.MapGroup("/api/v1/auth");
auth.MapGet("/state", (HttpContext context) => Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true, userName = context.User.Identity?.Name, steamId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) }));
auth.MapGet("/csrf", (IAntiforgery antiforgery, HttpContext context) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
auth.MapGet("/steam", (SteamAuthService steam, HttpContext context) => Results.Redirect(steam.CreateLoginUrl(context))).RequireRateLimiting("auth");
auth.MapGet("/steam/callback", async (SteamAuthService steam, AccessListService access, HttpContext context, AuditService audit, CancellationToken ct) =>
{
    var steamId = await steam.ValidateCallback(context, ct);
    if (steamId is null)
    {
        await audit.Write("auth.steam", "unknown", "failure", "Steam OpenID verification failed.");
        return Results.Redirect("/?authError=verification_failed");
    }
    if (!await access.IsSteamAdmin(steamId))
    {
        await audit.Write("auth.steam", steamId, "failure", "Steam ID is not present in adminlist.txt.");
        return Results.Redirect("/?authError=not_admin");
    }
    var identity = new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, steamId),
        new Claim(ClaimTypes.Name, "Steam_" + steamId)
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
    await audit.Write("auth.steam", steamId);
    return Results.Redirect("/");
}).RequireRateLimiting("auth");
auth.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.NoContent(); }).RequireAuthorization().RequireAntiforgery();

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapModFiles();
api.MapGet("/status", (ServerState state) => Results.Ok(state.Snapshot()));
api.MapGet("/players", (ServerState state) => Results.Ok(state.Players));
api.MapPost("/players/{peerId:long}/kick", async (long peerId, ModerationRequest request, AgentGateway agent, ServerMessageService messages, ServerState state, AuditService audit, CancellationToken ct) =>
{
    var reason = ServerMessageService.NormalizeReason(request.Reason);
    var player = state.Players.FirstOrDefault(item => item.PeerId == peerId)?.Name ?? peerId.ToString();
    var message = await messages.Render("kick", player, reason, cancellationToken: ct);
    var result = await agent.Command("player.kick", new { peerId, reason, message }, TimeSpan.FromSeconds(10));
    var ok = !result.TryGetProperty("ok", out var succeeded) || succeeded.GetBoolean();
    await audit.Write("player.kick", peerId.ToString(), ok ? "success" : "failure", ok ? $"reason:{reason}" : result.ToString());
    return Results.Json(result);
}).RequireAntiforgery();
api.MapPost("/players/{peerId:long}/ban", async (long peerId, ModerationRequest request, AgentGateway agent, ServerMessageService messages, ServerState state, AuditService audit, CancellationToken ct) =>
{
    var reason = ServerMessageService.NormalizeReason(request.Reason);
    var player = state.Players.FirstOrDefault(item => item.PeerId == peerId)?.Name ?? peerId.ToString();
    var message = await messages.Render("ban", player, reason, cancellationToken: ct);
    var result = await agent.Command("player.ban", new { peerId, reason, message }, TimeSpan.FromSeconds(10));
    var ok = !result.TryGetProperty("ok", out var succeeded) || succeeded.GetBoolean();
    await audit.Write("player.ban", peerId.ToString(), ok ? "success" : "failure", ok ? $"reason:{reason}" : result.ToString());
    return Results.Json(result);
}).RequireAntiforgery();
api.MapPost("/players/{peerId:long}/inventory", async (long peerId, AgentGateway agent, AuditService audit) =>
{
    var result = await agent.Command("inventory.request", new { peerId }, TimeSpan.FromSeconds(15));
    var ok = !result.TryGetProperty("ok", out var succeeded) || succeeded.GetBoolean();
    await audit.Write("player.inventory.request", peerId.ToString(), ok ? "success" : "failure");
    if (!ok)
    {
        var reason = result.TryGetProperty("error", out var error) ? error.GetString() : "Inventory is unavailable.";
        return Results.Conflict(new ProblemDetails { Title = "Inventory unavailable", Detail = reason });
    }
    return Results.Json(result);
}).RequireAntiforgery();

api.MapGet("/characters", (ServerCharacterService characters, ServerState state) => Results.Ok(new
{
    installed = characters.IsInstalled,
    importAvailable = characters.ImportAvailable,
    serverStatus = state.Status,
    characters = characters.List()
}));
api.MapPost("/characters/import", async (HttpRequest request, ServerCharacterService characters, AuditService audit, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new ProblemDetails { Title = "multipart/form-data is required." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("profile");
    if (file is null) return Results.BadRequest(new ProblemDetails { Title = "A native .fch profile is required." });
    if (file.Length > ServerCharacterService.MaxProfileBytes) return Results.BadRequest(new ProblemDetails { Title = "Character save exceeds the 2 MiB limit." });
    var platformId = form["platformId"].ToString();
    var overwrite = bool.TryParse(form["overwrite"], out var replace) && replace;
    await using var stream = file.OpenReadStream();
    var imported = await characters.Import(stream, file.FileName, platformId, overwrite, ct);
    await audit.Write("server-character.import", imported.PlatformId + "/" + imported.CharacterName, "success", $"sha256:{imported.Sha256}");
    return Results.Created($"/api/v1/characters/{Uri.EscapeDataString(imported.FileName)}", imported);
}).RequireAntiforgery();

api.MapGet("/access", async (AccessListService access) =>
{
    var permitted = await access.Read("permitted");
    return Results.Ok(new { whitelistEnabled = permitted.Count > 0, permitted, banned = await access.Read("banned"), admins = await access.Read("admin") });
});
api.MapGet("/join-requests", async (string? status, JoinRequestService requests, CancellationToken ct) => Results.Ok(await requests.List(status, ct)));
api.MapPost("/join-requests/{id:guid}/approve", async (Guid id, JoinRequestService requests, AccessListService access, AuditService audit, CancellationToken ct) =>
    Results.Ok(await requests.Resolve(id, "approved", access, audit, ct))).RequireAntiforgery();
api.MapPost("/join-requests/{id:guid}/deny", async (Guid id, JoinRequestService requests, AccessListService access, AuditService audit, CancellationToken ct) =>
    Results.Ok(await requests.Resolve(id, "denied", access, audit, ct))).RequireAntiforgery();
api.MapPost("/access/{kind}/", async (string kind, AccessMutation request, AccessListService access, AuditService audit) =>
{
    await access.Add(kind, request.PlatformId);
    await audit.Write($"access.{kind}.add", request.PlatformId);
    return Results.NoContent();
}).RequireAntiforgery();
api.MapDelete("/access/{kind}/{*platformId}", async (string kind, string platformId, AccessListService access, AuditService audit) =>
{
    await access.Remove(kind, Uri.UnescapeDataString(platformId));
    await audit.Write($"access.{kind}.remove", platformId);
    return Results.NoContent();
}).RequireAntiforgery();

api.MapGet("/mods", async (ManagerDbContext db) => Results.Ok(await db.InstalledMods.AsNoTracking().OrderBy(x => x.Name).ToListAsync()));
api.MapGet("/mods/search", async (string q, ModService mods, CancellationToken ct) => Results.Ok(await mods.Search(q ?? "", ct)));
api.MapGet("/mods/updates", (ModService mods) => Results.Ok(mods.AvailableUpdates));
api.MapGet("/mods/client-policies", async (ClientModManifestService manifests, CancellationToken ct) => Results.Ok(await manifests.Policies(ct)));
api.MapPost("/mods/{id:guid}/client-policy", async (Guid id, ClientPolicyMutation request, ClientModManifestService manifests, AgentGateway agent, AuditService audit, CancellationToken ct) =>
{
    await manifests.SetPolicy(id, request.Policy, ct);
    await agent.PublishClientManifest(ct);
    await audit.Write("mod.client-policy", id.ToString(), detail: request.Policy);
    return Results.NoContent();
}).RequireAntiforgery();
api.MapGet("/mods/client-sync", async (ClientModManifestService manifests, CancellationToken ct) => Results.Ok(await manifests.Status(ct)));
api.MapGet("/mods/client-manifest", async (ClientModManifestService manifests, CancellationToken ct) => Results.Text(await manifests.BuildJson(ct), "application/json"));
api.MapGet("/mods/{id:guid}/configs", async (Guid id, ModConfigService configs, CancellationToken ct) => Results.Ok(await configs.List(id, ct)));
api.MapPost("/mods/{id:guid}/configs", async (Guid id, ModConfigUpdate request, ModConfigService configs, CancellationToken ct) =>
    Results.Ok(await configs.Update(id, request.File, request.Revision, request.Values, ct))).RequireAntiforgery();
api.MapPost("/mods/configs/apply", async (ModService mods, CancellationToken ct) =>
{
    await mods.Apply(ct);
    return Results.NoContent();
}).RequireAntiforgery();
api.MapPost("/mods/{id:guid}/client-sync", async (Guid id, ClientSyncMutation request, ClientModManifestService manifests, AgentGateway agent, AuditService audit, CancellationToken ct) =>
{
    await manifests.SetRequired(id, request.Required, ct);
    await agent.PublishClientManifest(ct);
    await audit.Write("mod.client-sync", id.ToString(), "success", request.Required ? "required" : "server-only");
    return Results.NoContent();
}).RequireAntiforgery();
api.MapPost("/mods/updates/check", async (ModService mods, CancellationToken ct) => { await mods.CheckForUpdates(ct); return Results.Ok(mods.AvailableUpdates); }).RequireAntiforgery();
api.MapPost("/mods/updates/stage-all", async (ModService mods, CancellationToken ct) => Results.Ok(new { staged = await mods.StageAllUpdates(ct) })).RequireAntiforgery();
api.MapPost("/mods/thunderstore", async ([FromBody] ThunderstoreInstall request, ModService mods, CancellationToken ct) => { await mods.InstallThunderstore(request.Namespace, request.Name, request.Version, ct); return Results.Accepted(); }).RequireAntiforgery();
api.MapPost("/mods/upload", async (HttpRequest request, ModService mods, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new ProblemDetails { Title = "multipart/form-data is required." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("package");
    if (file is null) return Results.BadRequest(new ProblemDetails { Title = "Package file is required." });
    await using var stream = file.OpenReadStream();
    return Results.Ok(await mods.InstallUpload(stream, file.FileName, ct));
}).RequireAntiforgery();
api.MapGet("/downloads/plugin", (IConfiguration configuration) =>
{
    var path = Path.Combine(configuration["VSM_DATA_PATH"] ?? "/data/manager", "downloads", "ValheimServerManager-2.1.3.zip");
    return File.Exists(path) ? Results.File(path, "application/zip", Path.GetFileName(path)) : Results.NotFound();
});
api.MapPost("/mods/{id:guid}/enable", async (Guid id, ModService mods, CancellationToken ct) => { await mods.SetEnabled(id, true, ct); return Results.NoContent(); }).RequireAntiforgery();
api.MapPost("/mods/{id:guid}/disable", async (Guid id, ModService mods, CancellationToken ct) => { await mods.SetEnabled(id, false, ct); return Results.NoContent(); }).RequireAntiforgery();
api.MapDelete("/mods/{id:guid}", async (Guid id, ModService mods, CancellationToken ct) => { await mods.Remove(id, ct); return Results.NoContent(); }).RequireAntiforgery();
api.MapPost("/mods/apply", async (ModService mods, CancellationToken ct) => { await mods.Apply(ct); return Results.NoContent(); }).RequireAntiforgery();

api.MapGet("/settings/server-access", async (ServerSettingsService settings, CancellationToken ct) => Results.Ok(await settings.Get(ct)));
api.MapPost("/settings/server-access", async (ServerSettingsMutation request, ServerSettingsService settings, ProcessSupervisor process, AgentGateway agent, AuditService audit, CancellationToken ct) =>
{
    var saved = await settings.Set(request, ct);
    if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60));
    await process.Restart(TimeSpan.Zero, ct);
    await audit.Write("server.settings.update", "valheim", "success",
        $"world={saved.WorldName};crossplay={saved.Crossplay};public={saved.PublicListing};maxPlayers={saved.MaxPlayers};preset={(saved.ManageWorldModifiers ? saved.Preset : "unmanaged")}");
    return Results.Ok(saved);
}).RequireAntiforgery();

api.MapGet("/settings/server-characters", async (ServerCharacterSettingsService settings, CancellationToken ct) => Results.Ok(await settings.Get(ct)));
api.MapPut("/settings/server-characters", async (ServerCharacterSettings request, ServerCharacterSettingsService settings, AgentGateway agent, AuditService audit, CancellationToken ct) =>
{
    var saved = await settings.Set(request, ct);
    await agent.PublishServerCharacterSettings(ct);
    await agent.PublishClientManifest(ct);
    await audit.Write("server-characters.settings.update", "valheim", "success",
        $"enabled={saved.Enabled};acceptFirstJoin={saved.AcceptFirstJoinProfile};rejectPreviouslyUsed={saved.RejectPreviouslyUsedCharacters};backups={saved.BackupsToKeep};clientGrace={saved.ClientGraceSeconds}");
    return Results.Ok(saved);
}).RequireAntiforgery();

api.MapGet("/settings/messages", async (ServerMessageService messages, CancellationToken ct) => Results.Ok(await messages.Get(ct)));
api.MapPut("/settings/messages", async (ServerMessageTemplates request, ServerMessageService messages, AgentGateway agent, AuditService audit, CancellationToken ct) =>
{
    var saved = await messages.Set(request, ct);
    await agent.PublishServerMessages(ct);
    await audit.Write("server.messages.update", "valheim");
    return Results.Ok(saved);
}).RequireAntiforgery();

api.MapGet("/manager-update", async (ManagerUpdateService updates, CancellationToken ct) => Results.Ok(await updates.Get(ct)));
api.MapPost("/manager-update/check", async (ManagerUpdateService updates, CancellationToken ct) => Results.Ok(await updates.Check(ct))).RequireAntiforgery();
api.MapPut("/manager-update/settings", async (ManagerUpdateSettings request, ManagerUpdateService updates, CancellationToken ct) => Results.Ok(await updates.SetAutomatic(request.Automatic, ct))).RequireAntiforgery();
api.MapPost("/manager-update/apply", async (ManagerUpdateService updates, CancellationToken ct) => Results.Accepted(value: await updates.RequestApply(false, ct))).RequireAntiforgery();

api.MapGet("/api-tokens", async (ApiTokenService tokens, CancellationToken ct) => Results.Ok(await tokens.List(ct)));
api.MapPost("/api-tokens", async (ApiTokenCreate request, ApiTokenService tokens, AuditService audit, CancellationToken ct) =>
{
    var created = await tokens.Create(request.Name, request.Scopes, ct);
    await audit.Write("api-token.create", created.Id.ToString(), "success", string.Join(',', created.Scopes));
    return Results.Created($"/api/v1/api-tokens/{created.Id}", created);
}).RequireAntiforgery();
api.MapDelete("/api-tokens/{id:guid}", async (Guid id, ApiTokenService tokens, AuditService audit, CancellationToken ct) =>
{
    await tokens.Revoke(id, ct);
    await audit.Write("api-token.revoke", id.ToString());
    return Results.NoContent();
}).RequireAntiforgery();

api.MapGet("/webhooks", async (ManagerDbContext db) => Results.Ok((await db.WebhookSubscriptions.AsNoTracking().ToListAsync()).Select(x => new { x.Id, x.Name, x.Url, x.Kind, secret = string.IsNullOrEmpty(x.Secret) ? "" : "••••••••", eventTypes = JsonSerializer.Deserialize<string[]>(x.EventTypesJson), x.Template, x.Enabled, x.AllowPrivateNetwork, x.ConsecutiveFailures })));
api.MapPost("/webhooks", async (WebhookRequest request, ManagerDbContext db, IDataProtectionProvider protection, AuditService audit) =>
{
    var secret = string.IsNullOrWhiteSpace(request.Secret) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : request.Secret;
    var item = new WebhookSubscription { Name = request.Name, Url = request.Url, Kind = request.Kind, Secret = protection.CreateProtector("webhook-secrets-v1").Protect(secret), EventTypesJson = JsonSerializer.Serialize(request.EventTypes), Template = request.Template ?? "**{{type}}** on {{server}}", Enabled = request.Enabled, AllowPrivateNetwork = request.AllowPrivateNetwork };
    db.Add(item); await db.SaveChangesAsync(); await audit.Write("webhook.create", item.Name); return Results.Created($"/api/v1/webhooks/{item.Id}", new { item.Id, secret });
}).RequireAntiforgery();
api.MapPut("/webhooks/{id:guid}", async (Guid id, WebhookRequest request, ManagerDbContext db, IDataProtectionProvider protection, AuditService audit) =>
{
    var item = await db.WebhookSubscriptions.FindAsync(id);
    if (item is null) return Results.NotFound();
    item.Name = request.Name;
    item.Url = request.Url;
    item.Kind = request.Kind;
    item.EventTypesJson = JsonSerializer.Serialize(request.EventTypes);
    item.Template = request.Template ?? item.Template;
    item.Enabled = request.Enabled;
    item.AllowPrivateNetwork = request.AllowPrivateNetwork;
    if (!string.IsNullOrWhiteSpace(request.Secret)) item.Secret = protection.CreateProtector("webhook-secrets-v1").Protect(request.Secret);
    await db.SaveChangesAsync();
    await audit.Write("webhook.update", item.Name);
    return Results.NoContent();
}).RequireAntiforgery();
api.MapDelete("/webhooks/{id:guid}", async (Guid id, ManagerDbContext db, AuditService audit) => { var item = await db.WebhookSubscriptions.FindAsync(id); if (item is null) return Results.NotFound(); db.Remove(item); await db.SaveChangesAsync(); await audit.Write("webhook.delete", item.Name); return Results.NoContent(); }).RequireAntiforgery();
api.MapPost("/webhooks/{id:guid}/test", async (Guid id, ManagerDbContext db, EventBus events, AuditService audit) => { if (!await db.WebhookSubscriptions.AnyAsync(x => x.Id == id)) return Results.NotFound(); await events.PublishTo(id, EventEnvelope.Create("webhook.test", new { subscriptionId = id })); await audit.Write("webhook.test", id.ToString()); return Results.Accepted(); }).RequireAntiforgery();
api.MapGet("/webhooks/deliveries", async (ManagerDbContext db) => Results.Ok((await db.WebhookDeliveries.AsNoTracking().ToListAsync()).OrderByDescending(x => x.CreatedAt).Take(200)));
api.MapPost("/webhooks/deliveries/{id:guid}/redeliver", async (Guid id, ManagerDbContext db, AuditService audit) => { var item = await db.WebhookDeliveries.FindAsync(id); if (item is null) return Results.NotFound(); item.Status = "pending"; item.Attempts = 0; item.NextAttemptAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); await audit.Write("webhook.redeliver", id.ToString()); return Results.Accepted(); }).RequireAntiforgery();

api.MapGet("/console/history", (ServerState state) => Results.Ok(state.Logs));
api.MapPost("/console", async (CommandRequest request, SafeConsoleService console, CancellationToken ct) => Results.Ok(await console.Execute(request.Command, ct))).RequireAntiforgery();
api.MapGet("/audit", async (ManagerDbContext db) => Results.Ok((await db.AuditRecords.AsNoTracking().ToListAsync()).OrderByDescending(x => x.OccurredAt).Take(500)));
api.MapPost("/server/start", async (ProcessSupervisor process, AuditService audit, CancellationToken ct) => { await process.StartServer(ct); await audit.Write("server.start", "valheim"); return Results.Accepted(); }).RequireAntiforgery();
api.MapPost("/server/stop", async (ProcessSupervisor process, AgentGateway agent, AuditService audit, CancellationToken ct) => { if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60)); await process.StopServer(ct); await audit.Write("server.stop", "valheim"); return Results.Accepted(); }).RequireAntiforgery();
api.MapPost("/server/restart", async (ProcessSupervisor process, AgentGateway agent, AuditService audit, CancellationToken ct) => { if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60)); await process.Restart(TimeSpan.Zero, ct); await audit.Write("server.restart", "valheim"); return Results.Accepted(); }).RequireAntiforgery();

var external = app.MapGroup("/api/external/v1").RequireRateLimiting("service-api");
external.MapGet("/whitelist", async (HttpContext context, ApiTokenService tokens, AccessListService access, CancellationToken ct) =>
{
    var principal = await tokens.Authenticate(context.Request.Headers.Authorization, "whitelist.read", ct);
    if (principal is null) return ExternalAuthProblem(context, StatusCodes.Status401Unauthorized, "A valid Bearer token is required.");
    if (principal.Identity?.IsAuthenticated != true) return ExternalAuthProblem(context, StatusCodes.Status403Forbidden, "The token does not have whitelist.read scope.");
    context.User = principal;
    return Results.Ok(new { permitted = await access.Read("permitted") });
});
external.MapPost("/whitelist", async (AccessMutation request, HttpContext context, ApiTokenService tokens, AccessListService access, AuditService audit, CancellationToken ct) =>
{
    var principal = await tokens.Authenticate(context.Request.Headers.Authorization, "whitelist.write", ct);
    if (principal is null) return ExternalAuthProblem(context, StatusCodes.Status401Unauthorized, "A valid Bearer token is required.");
    if (principal.Identity?.IsAuthenticated != true) return ExternalAuthProblem(context, StatusCodes.Status403Forbidden, "The token does not have whitelist.write scope.");
    context.User = principal;
    var platformId = NormalizeRegistrationId(request.PlatformId);
    await access.Add("permitted", platformId);
    await audit.Write("access.permitted.add", platformId);
    return Results.Ok(new { platformId, registered = true });
});
external.MapDelete("/whitelist/{*platformId}", async (string platformId, HttpContext context, ApiTokenService tokens, AccessListService access, AuditService audit, CancellationToken ct) =>
{
    var principal = await tokens.Authenticate(context.Request.Headers.Authorization, "whitelist.write", ct);
    if (principal is null) return ExternalAuthProblem(context, StatusCodes.Status401Unauthorized, "A valid Bearer token is required.");
    if (principal.Identity?.IsAuthenticated != true) return ExternalAuthProblem(context, StatusCodes.Status403Forbidden, "The token does not have whitelist.write scope.");
    context.User = principal;
    var normalized = NormalizeRegistrationId(Uri.UnescapeDataString(platformId));
    await access.Remove("permitted", normalized);
    await audit.Write("access.permitted.remove", normalized);
    return Results.Ok(new { platformId = normalized, registered = false });
});
external.MapGet("/join-requests", async (string? status, HttpContext context, ApiTokenService tokens, JoinRequestService requests, CancellationToken ct) =>
{
    var principal = await tokens.Authenticate(context.Request.Headers.Authorization, "join-requests.read", ct);
    if (principal is null) return ExternalAuthProblem(context, StatusCodes.Status401Unauthorized, "A valid Bearer token is required.");
    if (principal.Identity?.IsAuthenticated != true) return ExternalAuthProblem(context, StatusCodes.Status403Forbidden, "The token does not have join-requests.read scope.");
    context.User = principal;
    return Results.Ok(await requests.List(status, ct));
});
external.MapPost("/join-requests/{id:guid}/approve", async (Guid id, HttpContext context, ApiTokenService tokens, JoinRequestService requests, AccessListService access, AuditService audit, CancellationToken ct) =>
{
    var principal = await tokens.Authenticate(context.Request.Headers.Authorization, "join-requests.write", ct);
    if (principal is null) return ExternalAuthProblem(context, StatusCodes.Status401Unauthorized, "A valid Bearer token is required.");
    if (principal.Identity?.IsAuthenticated != true) return ExternalAuthProblem(context, StatusCodes.Status403Forbidden, "The token does not have join-requests.write scope.");
    context.User = principal;
    return Results.Ok(await requests.Resolve(id, "approved", access, audit, ct));
});
external.MapPost("/join-requests/{id:guid}/deny", async (Guid id, HttpContext context, ApiTokenService tokens, JoinRequestService requests, AccessListService access, AuditService audit, CancellationToken ct) =>
{
    var principal = await tokens.Authenticate(context.Request.Headers.Authorization, "join-requests.write", ct);
    if (principal is null) return ExternalAuthProblem(context, StatusCodes.Status401Unauthorized, "A valid Bearer token is required.");
    if (principal.Identity?.IsAuthenticated != true) return ExternalAuthProblem(context, StatusCodes.Status403Forbidden, "The token does not have join-requests.write scope.");
    context.User = principal;
    return Results.Ok(await requests.Resolve(id, "denied", access, audit, ct));
});

app.MapFallbackToFile("index.html");
await app.RunAsync();

static IResult ExternalAuthProblem(HttpContext context, int status, string detail) => Results.Problem(
    statusCode: status,
    title: status == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Forbidden",
    detail: detail,
    extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier });

static string NormalizeRegistrationId(string value)
{
    value = (value ?? "").Trim();
    if (value.Length == 17 && value.All(char.IsDigit)) value = "Steam_" + value;
    AccessListService.ValidateId(value);
    return value;
}

public sealed record ThunderstoreInstall(string Namespace, string Name, string Version);
public sealed record ClientSyncMutation(bool Required);
public sealed record ModerationRequest(string? Reason);
public sealed record ApiTokenCreate(string Name, string[] Scopes);
public sealed record ModConfigUpdate(string File, string Revision, ModConfigValueMutation[] Values);
public sealed record ManagerUpdateSettings(bool Automatic);
public partial class Program { }

record ClientPolicyMutation(string Policy);
