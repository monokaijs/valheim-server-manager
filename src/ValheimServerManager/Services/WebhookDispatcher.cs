using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using ValheimServerManager.Data;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed class WebhookDispatcher(IServiceScopeFactory scopes, IHttpClientFactory clients, IDataProtectionProvider protection, EventBus events, ILogger<WebhookDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchBatch(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Webhook dispatch batch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task DispatchBatch(CancellationToken cancellationToken)
    {
        var disabled = new List<(Guid Id, string Name)>();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var pending = await db.WebhookDeliveries.Where(x => x.Status == "pending").ToListAsync(cancellationToken);
        var deliveries = pending.Where(x => x.NextAttemptAt <= DateTimeOffset.UtcNow).OrderBy(x => x.CreatedAt).Take(20).ToList();
        foreach (var delivery in deliveries)
        {
            var subscription = await db.WebhookSubscriptions.FindAsync([delivery.SubscriptionId], cancellationToken);
            if (subscription is null || !subscription.Enabled) { delivery.Status = "cancelled"; continue; }
            delivery.Attempts++;
            try
            {
                await ValidateDestination(subscription.Url, subscription.AllowPrivateNetwork, cancellationToken);
                var body = subscription.Kind == "discord" ? DiscordBody(subscription, delivery) : delivery.PayloadJson;
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                request.Headers.TryAddWithoutValidation("X-Valheim-Event", delivery.EventType);
                request.Headers.TryAddWithoutValidation("X-Valheim-Delivery", delivery.Id.ToString());
                request.Headers.TryAddWithoutValidation("X-Valheim-Timestamp", timestamp);
                if (!string.IsNullOrWhiteSpace(subscription.Secret))
                    request.Headers.TryAddWithoutValidation("X-Valheim-Signature-256", "sha256=" + Sign(Unprotect(subscription.Secret), timestamp + "." + body));
                using var response = await clients.CreateClient("webhooks").SendAsync(request, cancellationToken);
                delivery.ResponseStatus = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Destination returned {(int)response.StatusCode}.");
                delivery.Status = "delivered";
                delivery.CompletedAt = DateTimeOffset.UtcNow;
                delivery.LastError = "";
                subscription.ConsecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                delivery.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                subscription.ConsecutiveFailures++;
                if (delivery.Attempts >= 10) delivery.Status = "failed";
                else delivery.NextAttemptAt = DateTimeOffset.UtcNow + Backoff(delivery.Attempts);
                if (subscription.ConsecutiveFailures >= 20)
                {
                    subscription.Enabled = false;
                    disabled.Add((subscription.Id, subscription.Name));
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var item in disabled)
            await events.Publish(EventEnvelope.Create("webhook.disabled", new { subscriptionId = item.Id, name = item.Name, reason = "repeated delivery failures" }));
    }

    internal static string Sign(string secret, string body) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    private string Unprotect(string secret)
    {
        try { return protection.CreateProtector("webhook-secrets-v1").Unprotect(secret); }
        catch { return secret; }
    }
    internal static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, attempts) * 15));

    private static string DiscordBody(WebhookSubscription subscription, WebhookDelivery delivery)
    {
        var content = subscription.Template.Replace("{{type}}", delivery.EventType, StringComparison.OrdinalIgnoreCase)
            .Replace("{{server}}", "valheim", StringComparison.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new { content, embeds = new[] { new { description = delivery.PayloadJson.Length > 3500 ? delivery.PayloadJson[..3500] : delivery.PayloadJson, color = 5793266 } } });
    }

    private static async Task ValidateDestination(string url, bool allowPrivate, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Webhook URL must use HTTP or HTTPS.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        foreach (var address in addresses)
        {
            if (IPAddress.IsLoopback(address) || IsLinkLocal(address) || (!allowPrivate && IsPrivate(address)))
                throw new InvalidOperationException("Webhook destination resolves to a blocked network address.");
        }
    }

    private static bool IsLinkLocal(IPAddress ip) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        ? ip.GetAddressBytes() is var b && b[0] == 169 && b[1] == 254
        : ip.IsIPv6LinkLocal;
    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return ip.IsIPv6SiteLocal || ip.ToString().StartsWith("fc") || ip.ToString().StartsWith("fd");
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
