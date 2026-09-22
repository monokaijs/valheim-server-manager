using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
    public const string PluginName = "Valheim Server Manager Mod Installer";
    public const string PluginVersion = "2.2.1";
    internal static RuntimeUpdaterPlugin? Instance { get; private set; }

    private readonly NoticeOverlay _notices = new();
    private Task<SynchronizationResult>? _check;
    private CancellationTokenSource? _connectionLifetime;
    private object? _serverRpc;
    private string? _pendingManifest;
    private string? _lastManifest;
    private ClientModCatalog? _catalog;
    private HashSet<string> _choices = new(StringComparer.Ordinal);
    private HashSet<string> _installedChoices = new(StringComparer.Ordinal);
    private bool _showOptions;
    private bool _installationCurrent;
    private Vector2 _optionsScroll;
    private string? _pendingReceipt;
    private string? _checkingReceipt;
    private string? _verifiedReceipt;
    private float _nextReceipt;

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
            Logger.LogInfo("Client mod installer disabled in the dedicated-server process.");
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
        Logger.LogInfo("Server Manager manual mod installer and client notices are ready.");
    }

    private void Update()
    {
        CompleteCheck();
        StartCheck();
        if (_verifiedReceipt != null && _serverRpc != null && Time.unscaledTime >= _nextReceipt)
        {
            _nextReceipt = Time.unscaledTime + 2f;
            try { RpcReflectionBridge.InvokeString(_serverRpc, "VSM_ModReceipt", _verifiedReceipt); }
            catch (Exception error) { Logger.LogDebug("Mod receipt was not accepted: " + error.GetBaseException().Message); }
        }
        NoticeInputGuard.ExternalModal = _showOptions;
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
        _catalog = null;
        _choices.Clear();
        _installedChoices.Clear();
        _showOptions = _installationCurrent = false;
        _verifiedReceipt = _checkingReceipt = _pendingReceipt = null;
        NoticeInputGuard.ExternalModal = false;
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
        _pendingManifest = null;
        _verifiedReceipt = null;
        try
        {
            _catalog = ClientModSelection.Parse(manifest);
            var installed = new HashSet<string>(BootstrapSynchronizer.InstalledCoordinates(), StringComparer.OrdinalIgnoreCase);
            _installedChoices = new HashSet<string>(_catalog.OptionalGroups
                .Where(group => group.Packages.Any(package => installed.Contains(package.Coordinate)
                    && (package.Namespace + "-" + package.PackageName).Equals(group.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(group => group.Id), StringComparer.Ordinal);
            _choices = ClientModSelection.Load(Paths.BepInExRootPath, _catalog);
            _choices.UnionWith(_installedChoices);
            var effective = ClientModSelection.EffectiveManifest(_catalog, _choices);
            _installationCurrent = BootstrapSynchronizer.IsInstalled(effective);
            var required = ClientModSelection.EffectiveManifest(_catalog, Array.Empty<string>());
            if (_catalog.RequiredReceipt && BootstrapSynchronizer.HasInstalledPackages(required))
            {
                _verifiedReceipt = _catalog.Revision;
                _nextReceipt = 0;
            }
            _showOptions = !_installationCurrent || (_catalog.OptionalGroups.Count > 0
                && !File.Exists(ClientModSelection.PreferencesPath(Paths.BepInExRootPath, _catalog.ManifestId)));
            _lastManifest = manifest;
        }
        catch (Exception error)
        {
            _catalog = null;
            _showOptions = false;
            Logger.LogError("Client mod catalog was rejected: " + error);
            DisconnectClient();
            _notices.Show("Mod selection unavailable", "The server's mod catalog or your saved choices could not be read. Check the BepInEx log and reconnect after the problem is corrected.", NoticeKind.Error);
            return;
        }
        Logger.LogInfo("Received the server mod list; waiting for an explicit install choice.");
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
        _checkingReceipt = _pendingReceipt;
        _verifiedReceipt = null;
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
            var installOnly = error is InvalidOperationException && error.Message.StartsWith("Install-only:", StringComparison.Ordinal);
            _notices.Show(installOnly ? "Installed mods differ" : network ? "Mod download interrupted" : "Server mods could not be prepared",
                installOnly ? error.Message : network
                    ? "A required mod could not be downloaded. Check your internet connection, then reconnect to try again. Completed downloads will be reused."
                    : "The required mods could not be verified or staged. Check free disk space and ask an administrator to check the server's mod list, then reconnect. More details are in the BepInEx log.",
                NoticeKind.Error);
            return;
        }
        var result = completed.Result;
        if (!result.Changed)
        {
            _verifiedReceipt = _checkingReceipt;
            _nextReceipt = 0;
            _installationCurrent = true;
            return;
        }
        _showOptions = false;
        NoticeInputGuard.ExternalModal = false;
        Logger.LogInfo("Selected server mods are staged for the next Valheim launch.");
        try { WriteRestartMarker(result); }
        catch (Exception failure) { Logger.LogWarning("Could not write the restart reminder: " + failure.Message); }
        DisconnectClient();
        _notices.Show("Restart to finish installing",
            "Your selected server mods are ready.\n\n1. Quit Valheim.\n2. Start it again with your mod manager.\n3. Reconnect to this server.\n\nChoose Later to stay at the menu. You still need to restart before joining this server.",
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

    private void OnGUI()
    {
        if (_catalog != null)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F8)
            { _showOptions = !_showOptions; Event.current.Use(); }
            if (!_showOptions && GUI.Button(new Rect(16, 16, 245, 32), "Server mods (F8)")) _showOptions = true;
        }
        if (!_showOptions || _catalog == null) { _notices.Draw(); return; }
        var previousDepth = GUI.depth;
        GUI.depth = -1500;
        try
        {
            var width = Mathf.Min(660f, Screen.width - 32f);
            var height = Mathf.Min(620f, Screen.height - 48f);
            GUI.ModalWindow(0x56534E, new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height),
                _ => DrawOptionalMods(width, height), "SERVER MANAGER · MOD INSTALLER");
        }
        finally { GUI.depth = previousDepth; }
    }

    private void DrawOptionalMods(float width, float height)
    {
        if (_catalog == null) return;
        var text = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false, fontSize = 14 };
        var caption = new GUIStyle(text) { fontSize = 12 };
        var toggle = new GUIStyle(GUI.skin.toggle) { wordWrap = true, richText = false, fontSize = 14 };
        GUI.Label(new Rect(22, 35, width - 44, 58),
            "Saved selection: " + (_installationCurrent ? "installed" : "installation needed")
            + ". Required mods are locked; new optional mods start off. Installed optional mods stay selected. Nothing downloads until you choose Install selected.", text);
        var contentHeight = 80f + _catalog.Packages.Count * 22f
            + _catalog.OptionalGroups.Sum(group => 40f + group.Packages.Count * 20f);
        _optionsScroll = GUI.BeginScrollView(new Rect(18, 100, width - 36, height - 246), _optionsScroll, new Rect(0, 0, width - 58, contentHeight));
        GUI.Label(new Rect(4, 0, width - 70, 25), "REQUIRED · " + _catalog.Packages.Count + " packages", text);
        var y = 28f;
        foreach (var package in _catalog.Packages)
        {
            GUI.Label(new Rect(12, y, width - 85, 22), "[locked] " + package.PackageName + " · " + package.VersionNumber, caption);
            y += 22;
        }
        y += 12;
        GUI.Label(new Rect(4, y, width - 70, 25), "OPTIONAL · choose what you use", text);
        y += 32;
        var previousEnabled = GUI.enabled;
        GUI.enabled = _check == null && _pendingManifest == null;
        foreach (var group in _catalog.OptionalGroups)
        {
            GUI.enabled = previousEnabled && !_installedChoices.Contains(group.Id) && _check == null && _pendingManifest == null;
            var chosen = GUI.Toggle(new Rect(10, y, width - 85, 28), _choices.Contains(group.Id), group.Name + (_installedChoices.Contains(group.Id) ? " (installed)" : ""), toggle);
            if (chosen) _choices.Add(group.Id); else _choices.Remove(group.Id);
            y += 28;
            foreach (var package in group.Packages)
            {
                GUI.Label(new Rect(30, y, width - 100, 20), package.Namespace + "/" + package.PackageName + " · " + package.VersionNumber, caption);
                y += 20;
            }
            y += 12;
        }
        GUI.enabled = previousEnabled;
        GUI.EndScrollView();
        GUI.Label(new Rect(22, height - 139, width - 44, 80),
            "Bootstrap installs run in your active modded profile, but may not appear in r2modman's Installed list. Restarting r2modman does not register them. Restart Valheim after installation.", caption);
        GUI.enabled = _check == null && _pendingManifest == null;
        if (GUI.Button(new Rect(22, height - 56, 126, 34), "Required only")) { _choices.Clear(); _choices.UnionWith(_installedChoices); }
        if (GUI.Button(new Rect(width - 290, height - 56, 120, 34), "Cancel"))
        {
            try { _choices = ClientModSelection.Load(Paths.BepInExRootPath, _catalog); }
            catch (Exception error) { Logger.LogWarning("Could not reload optional choices: " + error.Message); }
            _choices.UnionWith(_installedChoices);
            _showOptions = false;
        }
        GUI.enabled = GUI.enabled && (_catalog.Packages.Count > 0 || _choices.Count > 0);
        if (GUI.Button(new Rect(width - 158, height - 56, 136, 34), "Install selected")) ApplyOptionalChoices();
        GUI.enabled = previousEnabled;
    }

    private void ApplyOptionalChoices()
    {
        if (_catalog == null) return;
        try
        {
            var effective = ClientModSelection.EffectiveManifest(_catalog, _choices);
            ClientModSelection.Save(Paths.BepInExRootPath, _catalog, _choices);
            _pendingManifest = effective;
            _pendingReceipt = _catalog.RequiredReceipt ? _catalog.Revision : null;
            _showOptions = false;
        }
        catch (Exception error)
        {
            Logger.LogError("Could not save optional mod choices: " + error);
            _showOptions = false;
            _notices.Show("Mod choices were not applied", "Your selections could not be saved or contain conflicting dependencies. Check the BepInEx log and try again.", NoticeKind.Error);
        }
    }

    private void OnDestroy()
    {
        NoticeInputGuard.ExternalModal = false;
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
