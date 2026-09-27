using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed record InventoryMutation(string Action, string Prefab, int Quantity, int Quality, int X = 0, int Y = 0,
    int ExpectedStack = 0, int ExpectedQuality = 0, int Stack = 0, float Durability = 0, string? TargetCharacter = null);

public sealed class InventoryArchiveService(IServiceScopeFactory scopes, AgentGateway agent, ServerState state, AuditService audit, ILogger<InventoryArchiveService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCapture = new();

    public async Task<object> Read(string platformId, CancellationToken ct)
    {
        platformId = ServerCharacterService.CanonicalPlatform(platformId);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var snapshot = await db.SavedInventories.AsNoTracking().SingleOrDefaultAsync(x => x.PlatformId == platformId, ct);
        var edits = await db.PendingInventoryEdits.AsNoTracking().Where(x => x.PlatformId == platformId)
            .OrderByDescending(x => x.CreatedAt).Take(50).ToArrayAsync(ct);
        JsonElement? content = null;
        if (snapshot is not null) { using var document = JsonDocument.Parse(snapshot.SnapshotJson); content = document.RootElement.Clone(); }
        return new { capturedAt = snapshot?.CapturedAt, snapshot = content, edits };
    }

    public async Task<PendingInventoryEdit> Queue(string platformId, InventoryMutation edit, CancellationToken ct)
    {
        platformId = ServerCharacterService.CanonicalPlatform(platformId);
        if (edit.Action is not ("give" or "replace" or "remove") || string.IsNullOrWhiteSpace(edit.Prefab) || edit.Prefab.Length > 128 ||
            edit.Prefab.Any(char.IsControl) || edit.Quantity is < 0 or > 1000 || edit.Quality is < 0 or > 100 ||
            edit.X is < 0 or > 15 || edit.Y is < 0 or > 15 || edit.Stack is < 0 or > 1000 ||
            edit.ExpectedStack is < 0 or > 1000 || edit.ExpectedQuality is < 0 or > 100 || !float.IsFinite(edit.Durability) || edit.Durability < 0 ||
            edit.Action == "give" && (edit.Quantity < 1 || edit.Quality < 1) ||
            edit.Action != "give" && (edit.ExpectedStack < 1 || edit.ExpectedQuality < 1 || string.IsNullOrWhiteSpace(edit.TargetCharacter) ||
                edit.TargetCharacter.Length > 64 || edit.Action == "replace" && (edit.Stack < 1 || edit.Quality < 1)))
            throw new ArgumentException("Invalid inventory edit.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var numericId = platformId.StartsWith("Steam_", StringComparison.Ordinal) ? platformId[6..] : platformId;
        if (!await db.KnownPlayers.AnyAsync(x => x.PlatformId == platformId || x.PlatformId == numericId, ct)) throw new FileNotFoundException("Player was not found.");
        if (await db.PendingInventoryEdits.CountAsync(x => x.PlatformId == platformId && x.Status == "pending", ct) >= 100)
            throw new InvalidOperationException("Too many pending edits for this player.");
        var entry = new PendingInventoryEdit { PlatformId = platformId, Action = edit.Action, PayloadJson = JsonSerializer.Serialize(edit, JsonOptions) };
        db.PendingInventoryEdits.Add(entry);
        await db.SaveChangesAsync(ct);
        await audit.Write("player.item.queue", platformId, detail: $"action:{edit.Action};prefab:{edit.Prefab};quantity:{edit.Quantity};quality:{edit.Quality}");
        return entry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await Deliver(stoppingToken);
                await Capture(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task Capture(CancellationToken ct)
    {
        foreach (var player in state.Players.Where(p => p.Companion && p.InventoryAllowed && !p.ServerCharacter && p.PlatformId.Length > 0))
        {
            var platformId = ServerCharacterService.CanonicalPlatform(player.PlatformId);
            if (_lastCapture.TryGetValue(platformId, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(15)) continue;
            try
            {
                var result = await agent.Command("inventory.archive", new { peerId = player.PeerId }, TimeSpan.FromSeconds(12));
                if (!InventoryInspectionService.IsValidSnapshot(result)) continue;
                var archive = new { capturedAt = result.GetProperty("capturedAt").GetString(), character = result.GetProperty("character").Clone(),
                    items = result.GetProperty("items").Clone(), icons = new { } };
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                var saved = await db.SavedInventories.FindAsync([platformId], ct);
                if (saved is null) { saved = new SavedInventory { PlatformId = platformId }; db.SavedInventories.Add(saved); }
                saved.CapturedAt = DateTimeOffset.UtcNow;
                saved.SnapshotJson = JsonSerializer.Serialize(archive, JsonOptions);
                await db.SaveChangesAsync(ct);
                _lastCapture[platformId] = saved.CapturedAt;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogDebug(error, "Could not archive inventory for {PlatformId}", player.PlatformId); }
        }
    }

    private async Task Deliver(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var pending = await db.PendingInventoryEdits.Where(x => x.Status == "pending")
            .OrderBy(x => x.CreatedAt).Take(50).ToArrayAsync(ct);
        foreach (var entry in pending)
        {
            var player = state.Players.FirstOrDefault(p => ServerCharacterService.SamePlatform(p.PlatformId, entry.PlatformId) && p.Companion && p.InventoryAllowed);
            if (player is null) continue;
            var edit = JsonSerializer.Deserialize<InventoryMutation>(entry.PayloadJson, JsonOptions)!;
            if (edit.TargetCharacter is not null && !string.Equals(edit.TargetCharacter, player.Name, StringComparison.Ordinal)) continue;
            entry.Status = "sending";
            await db.SaveChangesAsync(ct);
            try
            {
                var result = edit.Action == "give"
                    ? await agent.Command("items.give", new { peerId = player.PeerId, prefab = edit.Prefab, quantity = edit.Quantity, quality = edit.Quality }, TimeSpan.FromSeconds(15))
                    : await agent.Command("items.edit", new { peerId = player.PeerId, action = edit.Action, prefab = edit.Prefab, x = edit.X, y = edit.Y,
                        expectedStack = edit.ExpectedStack, expectedQuality = edit.ExpectedQuality, stack = edit.Stack, quality = edit.Quality,
                        durability = edit.Durability }, TimeSpan.FromSeconds(15));
                var ok = result.TryGetProperty("ok", out var flag) && flag.GetBoolean();
                var message = result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : "";
                var deliveredCount = result.TryGetProperty("given", out var delivered) && delivered.TryGetInt32(out var count) ? count : 0;
                var partial = ok && edit.Action == "give" && deliveredCount < edit.Quantity;
                entry.Status = ok ? partial ? "partial" : "applied" : message.Contains("must be online", StringComparison.OrdinalIgnoreCase) || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ? "pending" : "failed";
                entry.Error = partial ? $"Delivered {deliveredCount} of {edit.Quantity}. {message}" : message;
                entry.CompletedAt = entry.Status == "pending" ? null : DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                if (entry.Status != "pending") await audit.Write("player.item.queued." + entry.Status, entry.PlatformId, detail: $"id:{entry.Id};action:{entry.Action};{entry.Error}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Delivery may have happened before the connection failed. Keep this state
                // visible for manual resolution rather than risking a duplicate grant.
                entry.Error = "Delivery status unknown: " + error.Message;
                await db.SaveChangesAsync(ct);
                logger.LogWarning(error, "Inventory edit {Id} has unknown delivery status", entry.Id);
            }
        }
    }
}
