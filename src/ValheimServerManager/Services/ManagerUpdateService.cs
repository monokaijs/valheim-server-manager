using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed record ManagerUpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool AutomaticUpdates,
    bool HostUpdaterAvailable,
    string State,
    string? TargetVersion,
    string Detail,
    DateTimeOffset? LastCheckedAt,
    string? ReleaseUrl);

public sealed class ManagerUpdateService(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    AgentGateway agent,
    ProcessSupervisor supervisor,
    AuditService audit,
    IConfiguration configuration,
    ILogger<ManagerUpdateService> logger)
{
    private const string AutomaticSetting = "manager-updates.automatic";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataPath = configuration["VSM_DATA_PATH"] ?? "/data/manager";
    private readonly string _repository = configuration["VSM_UPDATE_REPOSITORY"] ?? "monokaijs/valheim-server-manager";
    private ReleaseInfo? _latest;
    private DateTimeOffset? _lastCheckedAt;

    public string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public async Task<ManagerUpdateStatus> Get(CancellationToken cancellationToken = default)
    {
        var automatic = await Automatic(cancellationToken);
        var operation = await ReadOperation(cancellationToken);
        var hostAvailable = HostUpdaterAvailable();
        return new ManagerUpdateStatus(
            CurrentVersion,
            _latest?.Version,
            _latest is not null && IsNewer(_latest.Version, CurrentVersion),
            automatic,
            hostAvailable,
            operation?.State ?? "idle",
            operation?.TargetVersion,
            operation?.Detail ?? (hostAvailable ? "Ready" : "Install the host updater to enable one-click application."),
            _lastCheckedAt,
            _latest?.Url);
    }

    public async Task<ManagerUpdateStatus> Check(CancellationToken cancellationToken = default)
    {
        var client = clients.CreateClient("manager-updates");
        ReleaseInfo? release = null;
        try
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                var root = document.RootElement;
                release = FromGitHub(root.GetProperty("tag_name").GetString(), root.TryGetProperty("html_url", out var url) ? url.GetString() : null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "GitHub release lookup failed; falling back to tags");
        }

        if (release is null)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{_repository}/tags?per_page=20", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            release = document.RootElement.EnumerateArray()
                .Select(item => FromGitHub(item.GetProperty("name").GetString(), $"https://github.com/{_repository}/releases/tag/{item.GetProperty("name").GetString()}"))
                .Where(item => item is not null)
                .OrderByDescending(item => ParseVersion(item!.Version))
                .FirstOrDefault();
        }

        _latest = release ?? throw new InvalidOperationException("No stable manager release was found.");
        _lastCheckedAt = DateTimeOffset.UtcNow;
        return await Get(cancellationToken);
    }

    public async Task<ManagerUpdateStatus> SetAutomatic(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var setting = await db.ManagerSettings.FindAsync([AutomaticSetting], cancellationToken);
        if (setting is null) db.ManagerSettings.Add(new ManagerSetting { Key = AutomaticSetting, Value = enabled ? "true" : "false" });
        else setting.Value = enabled ? "true" : "false";
        await db.SaveChangesAsync(cancellationToken);
        await audit.Write("manager.update.automatic", "manager", "success", enabled ? "enabled" : "disabled");
        return await Get(cancellationToken);
    }

    public async Task<ManagerUpdateStatus> RequestApply(bool automatic = false, CancellationToken cancellationToken = default)
    {
        var stoppedForUpdate = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_latest is null || DateTimeOffset.UtcNow - _lastCheckedAt > TimeSpan.FromHours(1)) await Check(cancellationToken);
            if (_latest is null || !IsNewer(_latest.Version, CurrentVersion)) throw new InvalidOperationException("The Server Manager is already up to date.");
            if (!HostUpdaterAvailable()) throw new InvalidOperationException("The host updater is unavailable. Run scripts/install-host-updater.sh on the Docker host first.");
            var updateDirectory = UpdateDirectory();
            Directory.CreateDirectory(updateDirectory);
            var requestPath = Path.Combine(updateDirectory, "manager-request.json");
            if (File.Exists(requestPath)) throw new InvalidOperationException("A manager update is already queued.");

            if (agent.IsConnected) await agent.Command("world.save", new { }, TimeSpan.FromSeconds(60));
            await supervisor.StopServer(cancellationToken);
            stoppedForUpdate = true;

            await WriteOperation(new UpdateOperation("queued", _latest.Version, automatic ? "Automatic stable update queued." : "Administrator update queued.", DateTimeOffset.UtcNow), cancellationToken);
            var temporary = requestPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { version = _latest.Version, requestedAt = DateTimeOffset.UtcNow, automatic }), cancellationToken);
            File.Move(temporary, requestPath, true);
            await audit.Write("manager.update.queued", _latest.Version, "success", automatic ? "automatic" : "manual");
            return await Get(cancellationToken);
        }
        catch
        {
            if (stoppedForUpdate && !supervisor.IsRunning) await supervisor.StartServer(CancellationToken.None);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> Automatic(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        return string.Equals(await db.ManagerSettings.AsNoTracking().Where(item => item.Key == AutomaticSetting).Select(item => item.Value).SingleOrDefaultAsync(cancellationToken), "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewer(string candidate, string current) => ParseVersion(candidate) > ParseVersion(current);

    private static Version ParseVersion(string value) => Version.TryParse(value.Trim().TrimStart('v', 'V').Split('-')[0], out var parsed) ? parsed : new Version(0, 0, 0);

    private static ReleaseInfo? FromGitHub(string? tag, string? url)
    {
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0) return null;
        return new ReleaseInfo(version.ToString(3), url ?? "");
    }

    private bool HostUpdaterAvailable()
    {
        var path = Path.Combine(UpdateDirectory(), "host-heartbeat");
        return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(3);
    }

    private string UpdateDirectory() => Path.Combine(_dataPath, "updates");

    private async Task<UpdateOperation?> ReadOperation(CancellationToken cancellationToken)
    {
        var path = Path.Combine(UpdateDirectory(), "manager-status.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<UpdateOperation>(await File.ReadAllTextAsync(path, cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not read host updater status"); return null; }
    }

    private async Task WriteOperation(UpdateOperation operation, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdateDirectory());
        var path = Path.Combine(UpdateDirectory(), "manager-status.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(operation, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);
        File.Move(temporary, path, true);
    }

    private sealed record ReleaseInfo(string Version, string Url);
    private sealed record UpdateOperation(string State, string? TargetVersion, string Detail, DateTimeOffset UpdatedAt);
}

public sealed class ManagerUpdateChecker(ManagerUpdateService updates, ServerState state, ILogger<ManagerUpdateChecker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = await updates.Check(stoppingToken);
                if (status.AutomaticUpdates && status.UpdateAvailable && status.HostUpdaterAvailable && state.Players.Count == 0)
                    await updates.RequestApply(true, stoppingToken);
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Manager update check failed");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }
}
