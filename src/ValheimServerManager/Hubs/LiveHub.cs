using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ValheimServerManager.Services;

namespace ValheimServerManager.Hubs;

[Authorize]
public sealed class LiveHub(InventoryInspectionService inspection, AccessListService access, AuditService audit) : Hub
{
    public async IAsyncEnumerable<InspectionFrame> WatchPlayer(string peerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!long.TryParse(peerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var peer) || peer == 0)
            throw new HubException("Invalid player identity.");
        var steamId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (steamId is null || !await access.IsSteamAdmin(steamId)) throw new HubException("Administrator access is required.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        var token = lifetime.Token;
        var actor = "Steam_" + steamId;
        try { inspection.Begin(Context.ConnectionId, actor, peer); }
        catch (InvalidOperationException error) { throw new HubException(error.Message); }
        try
        {
            await audit.Write("player.inventory.watch.start", peerId, actor: actor);
            while (!token.IsCancellationRequested)
            {
                // Do not let a long-lived socket bypass a revoked administrator membership.
                if (!await access.IsSteamAdmin(steamId)) throw new HubException("Administrator access was revoked.");
                var frame = await inspection.Read(peer, token);
                token.ThrowIfCancellationRequested();
                yield return frame;
                if (frame.Status == "offline") yield break;
                await Task.Delay(InventoryInspectionService.SampleInterval, token);
            }
        }
        finally
        {
            inspection.End(Context.ConnectionId);
            await audit.Write("player.inventory.watch.stop", peerId, actor: actor);
        }
    }
}
