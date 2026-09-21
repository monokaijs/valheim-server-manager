using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed class AgentGateway(ServerState state, EventBus events, ClientModManifestService clientMods, JoinRequestService joinRequests,
    PluginRegistryService pluginRegistry, ServerMessageService serverMessages, ServerCharacterSettingsService characterSettings,
    IConfiguration config, ILogger<AgentGateway> logger)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _socket;
    private long _generation;
    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public long Generation => Interlocked.Read(ref _generation);

    public async Task Accept(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = 403;
            return;
        }
        var expected = config["VSM_AGENT_TOKEN"];
        var supplied = context.Request.Headers["X-VSM-Agent-Token"].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? "");
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        if (expectedBytes.Length == 0 || expectedBytes.Length != suppliedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            context.Response.StatusCode = 401;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var previous = Interlocked.Exchange(ref _socket, socket);
        if (previous is { State: WebSocketState.Open }) await previous.CloseAsync(WebSocketCloseStatus.PolicyViolation, "superseded", CancellationToken.None);
        try { await ReceiveLoop(socket, context.RequestAborted); }
        finally
        {
            Interlocked.CompareExchange(ref _socket, null, socket);
            await state.SetAgent(false);
            foreach (var item in _pending.Values) item.TrySetException(new IOException("Server agent disconnected."));
            _pending.Clear();
        }
    }

    public async Task<JsonElement> Command(string name, object payload, TimeSpan timeout)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) throw new InvalidOperationException("Server agent is not connected.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            await Send(socket, new { type = "command", requestId, payload = new { name, data = payload } }, CancellationToken.None);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var registration = timeoutCts.Token.Register(() => completion.TrySetCanceled(timeoutCts.Token));
            return await completion.Task;
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public async Task PublishClientManifest(CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;
        var json = await clientMods.BuildJson(cancellationToken);
        await Send(socket, new { type = "clientModManifest", payload = new { json } }, cancellationToken);
    }

    public async Task PublishServerMessages(CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;
        await Send(socket, new { type = "serverMessages", payload = await serverMessages.Payload(cancellationToken) }, cancellationToken);
    }

    public async Task PublishServerCharacterSettings(CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;
        await Send(socket, new { type = "serverCharacterSettings", payload = await characterSettings.Payload(cancellationToken) }, cancellationToken);
    }

    private async Task ReceiveLoop(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var data = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (data.Length + result.Count > 2 * 1024 * 1024) throw new InvalidDataException("Agent message exceeded 2 MiB.");
                data.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
            switch (type)
            {
                case "hello":
                    var protocolVersion = payload.TryGetProperty("protocolVersion", out var protocol) && protocol.TryGetInt32(out var value) ? value : 0;
                    if (protocolVersion != 1)
                    {
                        await events.Publish(EventEnvelope.Create("compatibility.warning", new { expectedProtocol = 1, receivedProtocol = protocolVersion }, source: "manager", confidence: "authoritative"));
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unsupported protocol version", cancellationToken);
                        return;
                    }
                    Interlocked.Increment(ref _generation);
                    await state.SetAgent(true, String(payload, "version"), String(payload, "gameVersion"));
                    try { await PublishClientManifest(cancellationToken); }
                    catch (Exception error) { logger.LogError(error, "Could not publish the client mod manifest to the server agent."); }
                    try { await PublishServerMessages(cancellationToken); }
                    catch (Exception error) { logger.LogError(error, "Could not publish server message templates to the server agent."); }
                    try { await PublishServerCharacterSettings(cancellationToken); }
                    catch (Exception error) { logger.LogError(error, "Could not publish server-character settings to the server agent."); }
                    break;
                case "snapshot":
                    if (payload.TryGetProperty("players", out var players))
                    {
                        var parsed = players.Deserialize<List<PlayerInfo>>(JsonOptions) ?? [];
                        await state.ReplacePlayers(parsed);
                    }
                    break;
                case "pluginRegistry":
                    try { await pluginRegistry.Update(payload, cancellationToken); }
                    catch (Exception error) { logger.LogWarning(error, "Rejected malformed plugin registry from the server agent."); }
                    break;
                case "event":
                    var eventType = String(payload, "eventType");
                    var source = String(payload, "source", "server");
                    var confidence = String(payload, "confidence", source == "companion" ? "reported" : "authoritative");
                    object eventData = payload.TryGetProperty("data", out var eventPayload) ? eventPayload.Clone() : new { };
                    if (eventType == "access.join-request" && eventPayload.ValueKind == JsonValueKind.Object)
                    {
                        try { await joinRequests.Record(String(eventPayload, "platformId"), String(eventPayload, "player"), cancellationToken); }
                        catch (Exception error) { logger.LogWarning(error, "Rejected malformed join-request event from the server agent."); }
                    }
                    await events.Publish(EventEnvelope.Create(eventType, eventData, source: source, confidence: confidence));
                    break;
                case "commandResult":
                case "inventoryResponse":
                    var requestId = root.TryGetProperty("requestId", out var id) ? id.GetString() : null;
                    if (requestId is not null && _pending.TryGetValue(requestId, out var waiter)) waiter.TrySetResult(payload);
                    break;
                case "log":
                    await state.AddLog(String(payload, "stream", "plugin"), String(payload, "message"));
                    break;
                default:
                    logger.LogDebug("Ignored agent message {Type}", type);
                    break;
            }
        }
    }

    private async Task Send(WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendLock.Release(); }
    }

    private static string String(JsonElement element, string property, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.GetString() ?? fallback : fallback;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
