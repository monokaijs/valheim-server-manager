using System.Diagnostics;
using ValheimServerManager.Models;

namespace ValheimServerManager.Services;

public sealed class ProcessSupervisor(ServerState state, EventBus events, ServerSettingsService settings, IConfiguration config, ILogger<ProcessSupervisor> logger) : BackgroundService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private bool _intentionalStop;
    private DateOnly? _lastLogPrune;
    public bool IsRunning => _process is { HasExited: false };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.GetValue("VSM_AUTOSTART", true)) await StartServer(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        await StopServer(stoppingToken);
    }

    public async Task StartServer(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            var executable = config["VSM_SERVER_EXECUTABLE"];
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                await state.SetProcessState("unconfigured");
                return;
            }
            _intentionalStop = false;
            await state.SetProcessState("starting");
            await events.Publish(EventEnvelope.Create("server.starting", new { executable = Path.GetFileName(executable) }));
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = config["VSM_SERVER_WORKDIR"] ?? Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in await settings.BuildArguments(cancellationToken)) info.ArgumentList.Add(argument);
            info.Environment["DOORSTOP_ENABLED"] = "1";
            info.Environment["DOORSTOP_TARGET_ASSEMBLY"] = "./BepInEx/core/BepInEx.Preloader.dll";
            info.Environment["LD_LIBRARY_PATH"] = "./doorstop_libs:./linux64:" + (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "");
            info.Environment["LD_PRELOAD"] = "libdoorstop_x64.so:" + (Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "");
            info.Environment["SteamAppId"] = "892970";
            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _ = OnLog("stdout", e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _ = OnLog("stderr", e.Data); };
            _process.Exited += (_, _) => _ = OnExited(_process.ExitCode);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await state.SetProcessState("running");
            await events.Publish(EventEnvelope.Create("server.started", new { pid = _process.Id }));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopServer(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (!IsRunning) return;
            _intentionalStop = true;
            await state.SetProcessState("stopping");
            await events.Publish(EventEnvelope.Create("server.stopping", new { }));
            try
            {
                if (state.AgentConnected)
                {
                    // The caller normally requests an in-game save before entering this method.
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
                using (var signal = Process.Start(new ProcessStartInfo("kill", $"-INT {_process!.Id}") { UseShellExecute = false }))
                    if (signal is not null) await signal.WaitForExitAsync(cancellationToken);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
                await _process.WaitForExitAsync(linked.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Graceful stop failed; terminating server process tree");
                if (IsRunning) _process!.Kill(entireProcessTree: true);
            }
            await state.SetProcessState("stopped");
            await events.Publish(EventEnvelope.Create("server.stopped", new { }));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task Restart(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        await StopServer(cancellationToken);
        await StartServer(cancellationToken);
    }

    private async Task OnLog(string stream, string message)
    {
        await state.AddLog(stream, message);
        var logDirectory = config["VSM_LOG_PATH"] ?? "/data/logs";
        try
        {
            Directory.CreateDirectory(logDirectory);
            await File.AppendAllTextAsync(Path.Combine(logDirectory, $"server-{DateTime.UtcNow:yyyy-MM-dd}.log"), $"{DateTimeOffset.UtcNow:O} [{stream}] {message}{Environment.NewLine}");
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_lastLogPrune != today)
            {
                _lastLogPrune = today;
                foreach (var oldLog in Directory.GetFiles(logDirectory, "server-*.log").Where(path => File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)))
                    File.Delete(oldLog);
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Could not persist server log line"); }
    }

    private async Task OnExited(int exitCode)
    {
        await state.SetProcessState("stopped");
        if (_intentionalStop) return;
        await events.Publish(EventEnvelope.Create("server.crashed", new { exitCode }));
        for (var attempt = 1; attempt <= 3 && !_intentionalStop; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5));
            try { await StartServer(); if (IsRunning) return; }
            catch (Exception ex) { logger.LogError(ex, "Server restart attempt {Attempt} failed", attempt); }
        }
        await state.SetProcessState("failed");
    }
}
