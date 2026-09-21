using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ValheimServerManager.Data;
using ValheimServerManager.Hubs;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed class EventBus(IServiceScopeFactory scopes, IHubContext<LiveHub> hub)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task Publish(EventEnvelope envelope)
        => await PublishInternal(envelope, null);

    public async Task PublishTo(Guid subscriptionId, EventEnvelope envelope)
        => await PublishInternal(envelope, subscriptionId);

    private async Task PublishInternal(EventEnvelope envelope, Guid? subscriptionId)
    {
        await hub.Clients.All.SendAsync("event", envelope);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var subscriptions = await db.WebhookSubscriptions.Where(x => x.Enabled && (subscriptionId == null || x.Id == subscriptionId)).ToListAsync();
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        foreach (var subscription in subscriptions)
        {
            var filters = JsonSerializer.Deserialize<string[]>(subscription.EventTypesJson, JsonOptions) ?? [];
            if (filters.Length > 0 && !filters.Any(x => Matches(x, envelope.Type))) continue;
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                SubscriptionId = subscription.Id,
                EventId = envelope.Id,
                EventType = envelope.Type,
                PayloadJson = json
            });
        }
        await db.SaveChangesAsync();
    }

    internal static bool Matches(string filter, string eventType) => filter == "*" || filter.Equals(eventType, StringComparison.OrdinalIgnoreCase) ||
        (filter.EndsWith(".*") && eventType.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase));
}
