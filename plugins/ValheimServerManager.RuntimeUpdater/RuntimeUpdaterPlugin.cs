using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using ValheimServerManager.Bootstrap;
using ValheimServerManager.ClientSupport;

namespace ValheimServerManager.RuntimeUpdater;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RuntimeUpdaterPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.creaton.valheim-server-manager.runtime-updater";
    public const string PluginName = "Valheim Server Manager Runtime Updater";
    public const string PluginVersion = "2.1.0";
    internal static RuntimeUpdaterPlugin? Instance { get; private set; }

    private readonly NoticeOverlay _notices = new();
    private Task<SynchronizationResult>? _check;
    private CancellationTokenSource? _connectionLifetime;
    private object? _serverRpc;
    private string? _pendingManifest;
    private string? _lastManifest;
    private volatile string? _progress;
    private int _connectionVersion;
    private int _checkVersion;
    private Harmony? _harmony;
    private FieldInfo? _localPlayer;
    private FieldInfo? _gameInstance;
    private PropertyInfo? _gameInstanceProperty;
    private MethodInfo? _logout;

    private void Awake()
    {
        if (Application.isBatchMode)
        {
            Logger.LogInfo("Client runtime updater disabled in the dedicated-server process.");
            enabled = false;
            return;
        }
        Instance = this;
        var playerType = AccessTools.TypeByName("Player");
        var gameType = AccessTools.TypeByName("Game");
        _localPlayer = AccessTools.Field(playerType, "m_localPlayer");
        _gameInstance = AccessTools.Field(gameType, "instance");
        _gameInstanceProperty = AccessTools.Property(gameType, "instance");
        _logout = AccessTools.Method(gameType, "Logout", new[] { typeof(bool), typeof(bool) });
        _harmony = new Harmony(PluginGuid);
        if (!NoticeInputGuard.Install(_harmony)) Logger.LogWarning("The game menu input guard is unavailable on this Valheim build.");
        try { new ManifestHandshake(Logger).Install(_harmony); }
        catch (Exception error) { Logger.LogError("Could not install the manifest handshake: " + error); }
        Logger.LogInfo("Server Manager update receiver and client notices are ready.");
    }

    private void Update()
    {
        CompleteCheck();
        StartCheck();
        _notices.SetProgress(_progress);
        _notices.Tick(_localPlayer?.GetValue(null) is UnityEngine.Object player && player != null);
    }

    internal void BeginConnection(object rpc)
    {
        _connectionLifetime?.Cancel();
        _connectionLifetime?.Dispose();
        _connectionLifetime = new CancellationTokenSource();
        _connectionVersion++;
        _serverRpc = rpc;
        _pendingManifest = _lastManifest = _progress = null;
        _notices.ClearTransient();
    }

    internal void QueueRelayedManifest(object rpc, string manifest)
    {
        if (!ReferenceEquals(rpc, _serverRpc) || string.IsNullOrWhiteSpace(manifest)) return;
        if (manifest.Length > 8 * 1024 * 1024)
        {
            DisconnectClient();
            _notices.Show("Server mods could not be verified", "The server sent an oversized mod list. Ask an administrator to check the server's required mods, then reconnect.", NoticeKind.Error);
            return;
        }
        // Current and legacy relay names can deliver the same manifest in adjacent frames.
        if (string.Equals(_lastManifest, manifest, StringComparison.Ordinal)) return;
        _lastManifest = _pendingManifest = manifest;
        Logger.LogInfo("Received the server mod manifest; checking managed mods.");
    }

    internal void ReceiveServerNotice(object rpc, string title, string message)
    {
        if (ReferenceEquals(rpc, _serverRpc)) ShowClientNotice(title, message, (int)ClientNotice.ForServerTitle(title));
    }

    // Invoked by the optional client runtime without creating an assembly dependency.
    public static bool ShowClientNotice(string title, string message, int kind)
    {
        if (Instance == null) return false;
        var category = Enum.IsDefined(typeof(NoticeKind), kind) ? (NoticeKind)kind : NoticeKind.Attention;
        Instance._notices.Show(title, message, category);
        return true;
    }

    private void StartCheck()
    {
        if (_check != null || _pendingManifest == null || _connectionLifetime == null) return;
        var manifest = _pendingManifest;
        var token = _connectionLifetime.Token;
        _pendingManifest = null;
        _checkVersion = _connectionVersion;
        _check = Task.Run(() => BootstrapSynchronizer.StageRelayedManifest(manifest, token, progress =>
        {
            if (!token.IsCancellationRequested) _progress = progress;
        }), token);
    }

    private void CompleteCheck()
    {
        if (_check == null || !_check.IsCompleted) return;
        var completed = _check;
        _check = null;
        // Observe old faults, but never disconnect a new server session for an old result.
        var error = completed.Exception?.GetBaseException();
        if (_checkVersion != _connectionVersion || _connectionLifetime?.IsCancellationRequested == true) return;
        if (completed.IsCanceled) error = new TimeoutException("The mod download timed out.");
        _progress = null;
        if (error != null)
        {
            Logger.LogError("Managed mod synchronization failed: " + error);
            DisconnectClient();
            var network = error is HttpRequestException || error is OperationCanceledException || error is TimeoutException;
            _notices.Show(network ? "Mod download interrupted" : "Server mods could not be prepared",
                network
                    ? "A required mod could not be downloaded. Check your internet connection, then reconnect to try again. Completed downloads will be reused."
                    : "The required mods could not be verified or staged. Check free disk space and ask an administrator to check the server's mod list, then reconnect. More details are in the BepInEx log.",
                NoticeKind.Error);
            return;
        }
        var result = completed.Result;
        if (!result.Changed) return;
        Logger.LogInfo("Server mods are staged for the next Valheim launch.");
        try { WriteRestartMarker(result); }
        catch (Exception failure) { Logger.LogWarning("Could not write the restart reminder: " + failure.Message); }
        DisconnectClient();
        _notices.Show("Restart to finish updating",
            "The required server mods are ready.\n\n1. Quit Valheim.\n2. Start it again with your mod manager.\n3. Reconnect to this server.\n\nChoose Later to stay at the menu. You still need to restart before joining this server.",
            NoticeKind.RestartRequired);
    }

    private void DisconnectClient()
    {
        try
        {
            var game = _gameInstance?.GetValue(null) ?? _gameInstanceProperty?.GetValue(null, null);
            if (game is UnityEngine.Object instance && instance != null && _logout != null)
                _logout.Invoke(game, new object[] { true, true });
            else if (_serverRpc != null)
            {
                var socket = AccessTools.Method(_serverRpc.GetType(), "GetSocket")?.Invoke(_serverRpc, null);
                if (socket != null) AccessTools.Method(socket.GetType(), "Close")?.Invoke(socket, null);
            }
        }
        catch (Exception error) { Logger.LogWarning("Could not disconnect after a managed mod update: " + error.GetBaseException().Message); }
    }

    private void OnGUI() => _notices.Draw();

    private void OnDestroy()
    {
        _notices.Dispose();
        _connectionLifetime?.Cancel();
        _connectionLifetime?.Dispose();
        _harmony?.UnpatchSelf();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private static void WriteRestartMarker(SynchronizationResult result)
    {
        var root = Path.Combine(Paths.BepInExRootPath, "valheim-server-manager");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "restart-required"),
            $"revision={result.Revision}{Environment.NewLine}createdAt={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    }
}
