using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed class PlayerDirectoryService(IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task Record(IEnumerable<PlayerInfo> players, CancellationToken cancellationToken)
    {
        var seen = players.Where(player => !string.IsNullOrWhiteSpace(player.PlatformId)).ToArray();
        if (seen.Length == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
            var ids = seen.Select(player => player.PlatformId).Distinct(StringComparer.Ordinal).ToArray();
            var known = await db.KnownPlayers.Where(player => ids.Contains(player.PlatformId))
                .ToDictionaryAsync(player => player.PlatformId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            foreach (var player in seen)
            {
                if (!known.TryGetValue(player.PlatformId, out var entry))
                {
                    entry = new KnownPlayer { PlatformId = player.PlatformId };
                    known.Add(player.PlatformId, entry);
                    db.KnownPlayers.Add(entry);
                }
                if (!string.IsNullOrWhiteSpace(player.Name)) entry.Name = player.Name;
                if (entry.LastSeenAt < now.AddMinutes(-1)) entry.LastSeenAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<KnownPlayer>> List(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var players = await db.KnownPlayers.AsNoTracking().ToArrayAsync(cancellationToken);
        return players.OrderByDescending(player => player.LastSeenAt).ToArray();
    }
}
