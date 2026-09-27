using System.Diagnostics;
using System.Text.Json;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed record MonitorSample(DateTimeOffset At, int Players, double? CpuPercent, long? MemoryBytes);
public sealed record MonitorEvent(string Id, string Type, DateTimeOffset OccurredAt, string? Player);

public sealed class MonitorService(ServerState state, IServiceProvider services, ILogger<MonitorService> logger) : BackgroundService
{
    private const int MaxSamples = 720; // One hour at five-second intervals.
    private const int MaxEvents = 40;
    private readonly object _gate = new();
    private readonly Queue<MonitorSample> _samples = new();
    private readonly Queue<MonitorEvent> _events = new();
    private int? _lastPid;
    private TimeSpan _lastCpu;
    private long _lastTick;

    public object Snapshot()
    {
        lock (_gate) return new { samples = _samples.ToArray(), events = _events.Reverse().ToArray() };
    }

    public void Record(EventEnvelope envelope)
    {
        // Chat and map content is outside the scope of the operations feed.
        if (envelope.Type.StartsWith("chat.", StringComparison.OrdinalIgnoreCase) ||
            envelope.Type.StartsWith("map.", StringComparison.OrdinalIgnoreCase)) return;

        string? player = null;
        try
        {
            var data = JsonSerializer.SerializeToElement(envelope.Data);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("player", out var value) && value.ValueKind == JsonValueKind.String)
                player = value.GetString();
        }
        catch (Exception error) { logger.LogDebug(error, "Could not read monitor event details"); }

        lock (_gate)
        {
            _events.Enqueue(new MonitorEvent(envelope.Id, envelope.Type, envelope.OccurredAt, player));
            while (_events.Count > MaxEvents) _events.Dequeue();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Sample();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken)) Sample();
    }

    private void Sample()
    {
        double? cpu = null;
        long? memory = null;
        if (services.GetRequiredService<ProcessSupervisor>().TryGetProcessMetrics(out var pid, out var cpuTime, out var workingSet))
        {
            memory = workingSet;
            var tick = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_lastPid == pid && tick > _lastTick)
                    cpu = Math.Clamp((cpuTime - _lastCpu).TotalSeconds / Stopwatch.GetElapsedTime(_lastTick, tick).TotalSeconds / Environment.ProcessorCount * 100, 0, 100);
                _lastPid = pid;
                _lastCpu = cpuTime;
                _lastTick = tick;
            }
        }
        else
        {
            lock (_gate) { _lastPid = null; _lastTick = 0; }
        }

        lock (_gate)
        {
            _samples.Enqueue(new MonitorSample(DateTimeOffset.UtcNow, state.Players.Count, cpu is null ? null : Math.Round(cpu.Value, 1), memory));
            while (_samples.Count > MaxSamples) _samples.Dequeue();
        }
    }
}
