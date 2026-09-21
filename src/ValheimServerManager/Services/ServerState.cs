using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ValheimServerManager.Hubs;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed class ServerState(IHubContext<LiveHub> hub)
{
    private readonly ConcurrentDictionary<long, PlayerInfo> _players = new();
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private const int MaxLogs = 10_000;

    public string Status { get; private set; } = "stopped";
    public DateTimeOffset? StartedAt { get; private set; }
    public bool AgentConnected { get; private set; }
    public string AgentVersion { get; private set; } = "";
    public string GameVersion { get; private set; } = "";
    public bool RestartRequired { get; set; }
    public IReadOnlyCollection<PlayerInfo> Players => _players.Values.OrderBy(x => x.Name).ToArray();
    public IReadOnlyCollection<LogEntry> Logs => _logs.ToArray();

    public async Task SetProcessState(string status)
    {
        Status = status;
        if (status == "running") StartedAt = DateTimeOffset.UtcNow;
        if (status == "stopped") StartedAt = null;
        await hub.Clients.All.SendAsync("status", Snapshot());
    }

    public async Task SetAgent(bool connected, string version = "", string gameVersion = "")
    {
        AgentConnected = connected;
        if (!string.IsNullOrWhiteSpace(version)) AgentVersion = version;
        if (!string.IsNullOrWhiteSpace(gameVersion)) GameVersion = gameVersion;
        await hub.Clients.All.SendAsync("status", Snapshot());
    }

    public async Task ReplacePlayers(IEnumerable<PlayerInfo> players)
    {
        _players.Clear();
        foreach (var player in players) _players[player.PeerId] = player;
        await hub.Clients.All.SendAsync("players", Players);
    }

    public async Task AddLog(string stream, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, stream, message);
        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogs) _logs.TryDequeue(out _);
        await hub.Clients.All.SendAsync("log", entry);
    }

    public object Snapshot() => new
    {
        status = Status,
        startedAt = StartedAt,
        uptimeSeconds = StartedAt is null ? 0 : (long)(DateTimeOffset.UtcNow - StartedAt.Value).TotalSeconds,
        agentConnected = AgentConnected,
        agentVersion = AgentVersion,
        gameVersion = GameVersion,
        restartRequired = RestartRequired,
        players = Players.Count
    };
}

