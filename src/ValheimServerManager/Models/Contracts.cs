using System.Text.Json;

namespace ValheimServerManager.Models;

public sealed record PlayerInfo(
    long PeerId,
    string Name,
    string PlatformId,
    DateTimeOffset ConnectedAt,
    int? Ping,
    bool Companion,
    bool InventoryAllowed,
    bool ServerCharacter = false);

public sealed record InventoryItem(
    string Prefab,
    string Name,
    int Stack,
    int Quality,
    float Durability,
    bool Equipped,
    int X,
    int Y,
    int Variant,
    string CrafterName,
    long CrafterId);

public sealed record EventEnvelope(
    string Id,
    string Type,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    string Server,
    string Source,
    string Confidence,
    string CorrelationId,
    object Data)
{
    public static EventEnvelope Create(string type, object data, string server = "valheim", string source = "manager", string confidence = "authoritative", string? correlationId = null) =>
        new(Guid.NewGuid().ToString("N"), type, 1, DateTimeOffset.UtcNow, server, source, confidence, correlationId ?? Guid.NewGuid().ToString("N"), data);
}

public sealed record LogEntry(DateTimeOffset Timestamp, string Stream, string Message);
public sealed record AgentMessage(string Type, JsonElement Payload, string? RequestId = null);
public sealed record CommandRequest(string Command);
public sealed record AccessMutation(string PlatformId);
public sealed record WebhookRequest(string Name, string Url, string Kind, string Secret, string[] EventTypes, string? Template, bool Enabled, bool AllowPrivateNetwork);
