using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ValheimServerManager.Services;

namespace ValheimServerManager.Hubs;

[Authorize]
public sealed class LiveHub(InventoryInspectionService inspection, ManagerRoleService roles, AuditService audit) : Hub
{
    public async IAsyncEnumerable<InspectionFrame> WatchPlayer(string peerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!long.TryParse(peerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var peer) || peer == 0)
            throw new HubException("Invalid player identity.");
        var steamId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (steamId is null || await roles.GetRole(steamId) is null) throw new HubException("Manager access is required.");
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
                if (await roles.GetRole(steamId) is null) throw new HubException("Manager access was revoked.");
                InspectionFrame? frame = null;
                try { frame = await inspection.Read(peer, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                if (frame is null || token.IsCancellationRequested) break;
                yield return frame;
                if (frame.Status == "offline") yield break;
                try { await Task.Delay(InventoryInspectionService.SampleInterval, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            inspection.End(Context.ConnectionId);
            await audit.Write("player.inventory.watch.stop", peerId, actor: actor);
        }
    }
}
