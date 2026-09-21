using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;

namespace ValheimServerManager.Services;

public sealed class JoinRequestService(IServiceScopeFactory scopes, IHttpContextAccessor accessor)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task Record(string platformId, string playerName, CancellationToken cancellationToken = default)
    {
        platformId = NormalizePlatformId(platformId);
        playerName = (playerName ?? "").Trim();
        if (playerName.Length > 80) playerName = playerName[..80];
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var request = await db.JoinRequests.SingleOrDefaultAsync(
                item => item.PlatformId == platformId && item.Status == "pending", cancellationToken);
            if (request is null)
            {
                request = new JoinRequestRecord
                {
                    PlatformId = platformId,
                    PlayerName = playerName,
                    RequestedAt = now,
                    LastAttemptAt = now,
                    CorrelationId = Guid.NewGuid().ToString("N")
                };
                db.JoinRequests.Add(request);
            }
            else
            {
                request.PlayerName = playerName.Length > 0 ? playerName : request.PlayerName;
                request.LastAttemptAt = now;
                request.AttemptCount++;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<JoinRequestRecord>> List(string? status = null, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var query = db.JoinRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = NormalizeStatus(status);
            query = query.Where(item => item.Status == normalized);
        }
        var records = await query.ToListAsync(cancellationToken);
        return records.OrderByDescending(item => item.Status == "pending")
            .ThenByDescending(item => item.LastAttemptAt).Take(500).ToArray();
    }

    public async Task<JoinRequestRecord> Resolve(Guid id, string decision, AccessListService access, AuditService audit,
        CancellationToken cancellationToken = default)
    {
        decision = NormalizeStatus(decision);
        if (decision is not ("approved" or "denied")) throw new ArgumentException("Decision must be approved or denied.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var request = await db.JoinRequests.FindAsync([id], cancellationToken)
                ?? throw new KeyNotFoundException("Join request was not found.");
            if (request.Status != "pending") throw new InvalidOperationException($"Join request is already {request.Status}.");

            if (decision == "approved") await access.Add("permitted", request.PlatformId);
            request.Status = decision;
            request.ResolvedAt = DateTimeOffset.UtcNow;
            request.ResolvedBy = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? "system";
            await db.SaveChangesAsync(cancellationToken);
            await audit.Write($"join-request.{decision}", request.PlatformId, "success", $"request:{request.Id}", request.CorrelationId);
            return request;
        }
        finally { _gate.Release(); }
    }

    internal static string NormalizePlatformId(string value)
        => AccessListService.NormalizePlatformId(value);

    private static string NormalizeStatus(string value) => (value ?? "").Trim().ToLowerInvariant();
}
