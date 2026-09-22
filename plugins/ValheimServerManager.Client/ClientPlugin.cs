using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using ValheimServerManager.ClientSupport;

namespace ValheimServerManager.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ClientPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.creaton.valheim-server-manager.client";
    public const string PluginName = "Valheim Server Manager Client";
    public const string PluginVersion = "2.2.2";
    private const string LegacyPluginGuid = "dev.monokai.valheim-server-manager.client";
    internal static ClientPlugin Instance { get; private set; }
    private ConfigEntry<bool> _allowInventory;
    private bool? _advertisedInventory;
    private bool? _inspectionPolicy;
    private ConfigEntry<bool> _allowTelemetry;
    private ConfigEntry<bool> _enableServerCharacters;
    private ZRoutedRpc _registeredRouter;
    private readonly ClientHandshake _handshake = new();
    private readonly NoticeOverlay _notices = new();
    private readonly Dictionary<int, string> _iconCache = new();
    private ClientModCatalogStatus _modCatalog;
    private string _lastModCatalog;
    private float _nextModReceipt;
    private bool _bootstrapPending;
    private bool _pendingFirstProfile;
    private bool _rejectPreviouslyUsed;
    private float _nextCharacterUpload;
    private float _nextBiomeCheck;
    private float _nextInventoryRequest;
    private Coroutine _inventoryCapture;
    private byte[] _pendingCharacterProfile;
    private PlayerProfile _serverProfile;
    private string _temporaryProfileName;
    private bool _uploading;
    private Heightmap.Biome _biome;
    private ZNetPeer _serverPeer;
    private ZRpc _serverRpc;

    private void Awake()
    {
        if (Application.isBatchMode)
        {
            Logger.LogInfo("Client runtime disabled in the dedicated-server process.");
            enabled = false;
            return;
        }
        Instance = this;
        MigrateLegacyConfig();
        _allowInventory = Config.Bind("Privacy", "AllowInventoryInspection", false, "Allow the connected server's authenticated administrators to inspect current stats and inventory, including a live view. Some realms require this permission to join.");
        _allowTelemetry = Config.Bind("Privacy", "AllowDetailedTelemetry", false, "Share death and biome events with the connected server.");
        _enableServerCharacters = Config.Bind("ServerCharacters", "Enabled", true, "Allow this server to make its native character profile authoritative for this session.");
        if (!NoticeInputGuard.Install(new Harmony(PluginGuid))) Logger.LogWarning("The game menu input guard is unavailable on this Valheim build.");
        Harmony.CreateAndPatchAll(typeof(DeathPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(KillPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(CraftPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(FindSpawnPointPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(ProfileSavePatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(ConnectionNoticePatch), PluginGuid);
        Logger.LogInfo("VSM client component loaded. Inventory and detailed telemetry are disabled until opted in.");
    }

    private void MigrateLegacyConfig()
    {
        try
        {
            var legacy = Path.Combine(Paths.ConfigPath, LegacyPluginGuid + ".cfg");
            var current = Config.ConfigFilePath;
            if (File.Exists(legacy) && !File.Exists(current)) File.Copy(legacy, current, false);
            if (File.Exists(current)) Config.Reload();
        }
        catch (Exception exception) { Logger.LogWarning($"Could not migrate legacy plugin configuration: {exception.Message}"); }
    }

    private void Update()
    {
        _notices.Tick(Player.m_localPlayer != null);
        if (ZNet.instance == null || ZNet.instance.IsServer())
        {
            if (_handshake.Active) ResetConnection();
            return;
        }
        if (!ReferenceEquals(_registeredRouter, ZRoutedRpc.instance) && ZRoutedRpc.instance != null)
        {
            ZRoutedRpc.instance.Register<string>("VSM_InventoryRequest", OnInventoryRequest);
            ZRoutedRpc.instance.Register<bool, string, bool>("VSM_CharacterProfile", OnCharacterProfile);
            ZRoutedRpc.instance.Register("VSM_CharacterCheckpoint", OnCharacterCheckpoint);
            ZRoutedRpc.instance.Register<string, string>("VSM_AdminNotice", OnAdminNotice);
            _registeredRouter = ZRoutedRpc.instance;
        }
        if (_serverRpc != null && (_handshake.TrySendHello(Time.unscaledTime) || (_handshake.Active && _advertisedInventory != _allowInventory.Value)))
        {
            _advertisedInventory = _allowInventory.Value;
            InvokeServer("VSM_ClientHello", _allowInventory.Value, _enableServerCharacters.Value ? PluginVersion : "");
            Logger.LogDebug("Sent VSM capability handshake to the server.");
        }
        if (_serverRpc != null && _modCatalog?.RequiredReceipt == true && _modCatalog.CanAcknowledge
            && Time.unscaledTime >= _nextModReceipt)
        {
            _nextModReceipt = Time.unscaledTime + 2f;
            InvokeServer("VSM_ModReceipt", _modCatalog.Revision);
        }
        if (_pendingCharacterProfile != null && Game.instance != null) ApplyServerProfile();
        if (_pendingFirstProfile && Game.instance != null) PrepareFirstProfile();
        if (_bootstrapPending && Player.m_localPlayer != null && Game.instance != null) AdoptCurrentProfile();
        if (_handshake.HasTimedOut(Time.unscaledTime))
            FailCharacter("Server character timed out", "Your server character did not finish loading. Return to the menu and reconnect. If this continues, ask an administrator to check your server save.");
        if (_handshake.RequiresCharacter && _handshake.Ready && Player.m_localPlayer != null && Time.unscaledTime >= _nextCharacterUpload)
        {
            _nextCharacterUpload = Time.unscaledTime + 30f;
            UploadCharacter();
        }
        if (_allowTelemetry.Value && Player.m_localPlayer != null && Time.unscaledTime >= _nextBiomeCheck)
        {
            _nextBiomeCheck = Time.unscaledTime + 1f;
            var biome = Player.m_localPlayer.GetCurrentBiome();
            if (_biome != Heightmap.Biome.None && biome != _biome) SendEvent("player.biome.changed", new { from = _biome.ToString(), to = biome.ToString(), player = Player.m_localPlayer.GetPlayerName() });
            _biome = biome;
        }
    }

    private void OnInspectionPolicy(bool required)
    {
        if (_inspectionPolicy == required) return;
        _inspectionPolicy = required;
        if (!required) return;
        QueueAdminNotice("Inventory inspection required", _allowInventory.Value
            ? "This realm requires live, read-only inventory and character inspection by authenticated administrators. Your inventory-sharing permission is enabled. Inspection snapshots are not saved or forwarded to webhooks."
            : "This realm requires live inventory and character inspection by authenticated administrators. Enable Privacy > AllowInventoryInspection in the Server Manager client configuration and reconnect, or choose another realm. Your privacy setting has not been changed; this connection will be refused after the grace period.");
    }

    private void OnServerPolicy(bool serverCharacters, int timeoutSeconds)
    {
        _handshake.SetPolicy(serverCharacters, timeoutSeconds, Time.unscaledTime);
        if (!serverCharacters) return;
        if (!_enableServerCharacters.Value)
            FailCharacter("Server characters required", "This server uses server-owned characters. Enable ServerCharacters → Enabled in your Server Manager client configuration, then reconnect.");
        else if (Player.m_localPlayer != null && !_handshake.ReceivedProfile)
            FailCharacter("Reconnect to load your character", "This server requires a server-owned character. Return to the menu and reconnect so it can load before you enter the world.");
    }

    private void OnCharacterProfile(long sender, bool found, string encoded, bool rejectPreviouslyUsed)
    {
        if (_serverRpc == null || sender != ServerPeerId() || !_enableServerCharacters.Value || !_handshake.TryReceiveProfile(Time.unscaledTime)) return;
        if (Player.m_localPlayer != null)
        {
            FailCharacter("Reconnect to load your character", "Your server character arrived after the world loaded. Please reconnect to load it safely.");
            return;
        }
        try
        {
            if (found)
            {
                if (encoded == null || encoded.Length > 2796204) throw new InvalidDataException("Server character profile size is invalid.");
                var bytes = Convert.FromBase64String(encoded ?? "");
                if (bytes.Length < 32 || bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Server character profile size is invalid.");
                _pendingCharacterProfile = bytes;
                if (Game.instance != null) ApplyServerProfile();
            }
            else
            {
                _pendingFirstProfile = true;
                _rejectPreviouslyUsed = rejectPreviouslyUsed;
                if (Game.instance != null) PrepareFirstProfile();
            }
        }
        catch (Exception exception)
        {
            Logger.LogError("Unable to receive server character: " + exception);
            FailCharacter("Server character unavailable", "Your server character could not be read. Reconnect once; if this continues, ask an administrator to restore or re-import your server save.");
        }
    }

    private void PrepareFirstProfile()
    {
        _pendingFirstProfile = false;
        var selected = Game.instance.GetPlayerProfile();
        var worldData = selected == null ? null : Traverse.Create(selected).Field("m_worldData").GetValue();
        if (selected == null || (_rejectPreviouslyUsed && worldData is ICollection worlds && worlds.Count != 0))
        {
            FailCharacter("New character required", "Select a character that has never entered a world, or ask an administrator to import your existing save. Then reconnect.");
            return;
        }
        _handshake.CompleteProfile();
        _bootstrapPending = true;
    }

    private void FailCharacter(string title, string message)
    {
        _handshake.Fail();
        _pendingCharacterProfile = null;
        _pendingFirstProfile = _bootstrapPending = false;
        QueueAdminNotice(title, message);
        // Never save a partially applied profile or retry a failed disk write every frame.
        if (Game.instance != null) Game.instance.Logout();
        else _serverRpc?.GetSocket()?.Close();
    }

    private void OnAdminNotice(long sender, string title, string message)
    {
        if (_serverRpc == null || sender != ServerPeerId()) return;
        QueueAdminNotice(title, message);
    }

    private void QueueAdminNotice(string title, string message)
    {
        var kind = ClientNotice.ForServerTitle(title);
        _notices.Show(title, message, kind);
    }

    private void ReceiveModCatalog(string json)
    {
        if (string.Equals(json, _lastModCatalog, StringComparison.Ordinal)) return;
        try
        {
            _modCatalog = ClientModCompatibility.Check(json, Paths.BepInExRootPath);
            _lastModCatalog = json;
            _nextModReceipt = 0;
            Logger.LogInfo("Checked server mods against the active BepInEx profile.");
            if (_modCatalog.RequiredReceipt && !_modCatalog.CanAcknowledge)
            {
                var missing = _modCatalog.Required.Where(package => !package.Present)
                    .Select(package => package.Namespace + "-" + package.Name + "-" + package.Version
                        + (package.InstalledVersion == null ? " (missing)" : " (found " + package.InstalledVersion + ")")).ToArray();
                var details = new List<string>();
                if (missing.Length > 0) details.Add("Required versions:\n" + string.Join("\n", missing));
                if (_modCatalog.Unexpected.Count > 0) details.Add("Unapproved packages:\n" + string.Join("\n", _modCatalog.Unexpected));
                QueueAdminNotice("Mod list does not match",
                    string.Join("\n\n", details)
                    + "\n\nChange this profile in your external mod manager, restart Valheim, then reconnect.");
                _serverRpc?.GetSocket()?.Close();
            }
        }
        catch (Exception error)
        {
            _modCatalog = null;
            Logger.LogWarning("Could not check the server mod list: " + error.Message);
            QueueAdminNotice("Mod list unavailable", "The server's mod list could not be checked. Review your BepInEx log and reconnect.");
            _serverRpc?.GetSocket()?.Close();
        }
    }

    private void OnGUI()
    {
        _notices.Draw();
    }

    private void ApplyServerProfile()
    {
        if (_pendingCharacterProfile == null || Game.instance == null) return;
        try
        {
            PrepareTemporaryProfileName();
            var path = SaveSystem.GetCharacterPath(FileHelpers.FileSource.Local, _temporaryProfileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, _pendingCharacterProfile);
            var profile = new PlayerProfile(_temporaryProfileName, FileHelpers.FileSource.Local);
            if (!profile.Load()) throw new InvalidDataException("Valheim rejected the server-owned native character profile.");
            Traverse.Create(Game.instance).Field("m_playerProfile").SetValue(profile);
            _serverProfile = profile;
            _pendingCharacterProfile = null;
            _handshake.CompleteProfile();
            _nextCharacterUpload = Time.unscaledTime + 30f;
            Logger.LogInfo("Server-owned character profile loaded for " + profile.GetName() + ".");
        }
        catch (Exception exception)
        {
            Logger.LogError("Unable to apply server-owned character: " + exception);
            FailCharacter("Server character unavailable", "Your server character could not be loaded. Check that Valheim can write to its save folder, then reconnect. If it still fails, ask an administrator to check your server save.");
        }
    }

    private void AdoptCurrentProfile()
    {
        if (!_bootstrapPending || Player.m_localPlayer == null || Game.instance == null) return;
        try
        {
            var current = Game.instance.GetPlayerProfile();
            PrepareTemporaryProfileName();
            var profile = new PlayerProfile(_temporaryProfileName, FileHelpers.FileSource.Local);
            profile.SetName(current.GetName());
            Traverse.Create(profile).Field("m_playerID").SetValue(current.GetPlayerID());
            profile.SavePlayerData(Player.m_localPlayer);
            Traverse.Create(Game.instance).Field("m_playerProfile").SetValue(profile);
            _serverProfile = profile;
            _bootstrapPending = false;
            _handshake.CompleteProfile();
            UploadCharacter();
            Logger.LogInfo("First server-owned character profile created for " + profile.GetName() + ".");
        }
        catch (Exception exception)
        {
            Logger.LogError("Unable to seed server-owned character: " + exception);
            FailCharacter("Character import failed", "Your first server character could not be created. Check that Valheim can write to its save folder, then reconnect.");
        }
    }

    private void PrepareTemporaryProfileName()
    {
        if (!string.IsNullOrEmpty(_temporaryProfileName)) return;
        _temporaryProfileName = "VSMServer_" + Guid.NewGuid().ToString("N");
    }

    private void OnCharacterCheckpoint(long sender)
    {
        if (_serverRpc != null && sender == ServerPeerId()) UploadCharacter();
    }

    internal void UploadCharacter(bool alreadySaved = false)
    {
        if (_uploading || !_handshake.RequiresCharacter || !_handshake.Ready || _serverProfile == null || Player.m_localPlayer == null || _serverRpc == null) return;
        _uploading = true;
        try
        {
            if (!alreadySaved)
            {
                _serverProfile.SavePlayerData(Player.m_localPlayer);
                _serverProfile.SaveLogoutPoint();
                if (!_serverProfile.Save()) throw new IOException("Valheim could not serialize the server-owned character.");
            }
            var path = _serverProfile.GetPath();
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Server character profile exceeds 2 MiB.");
            InvokeServer("VSM_CharacterUpload", Convert.ToBase64String(bytes));
            _nextCharacterUpload = Time.unscaledTime + 30f;
            try { File.Delete(path); } catch { }
        }
        catch (Exception exception) { Logger.LogError("Unable to upload server-owned character: " + exception); }
        finally { _uploading = false; }
    }

    private void ResetConnection()
    {
        _advertisedInventory = null;
        _inspectionPolicy = null;
        if (!string.IsNullOrEmpty(_temporaryProfileName))
        {
            try { File.Delete(SaveSystem.GetCharacterPath(FileHelpers.FileSource.Local, _temporaryProfileName)); } catch { }
        }
        _handshake.Reset();
        _registeredRouter = null;
        _bootstrapPending = false;
        _pendingFirstProfile = false;
        _pendingCharacterProfile = null;
        _serverProfile = null;
        _temporaryProfileName = null;
        _serverPeer = null;
        _serverRpc = null;
        _biome = Heightmap.Biome.None;
        _nextBiomeCheck = _nextInventoryRequest = 0f;
        if (_inventoryCapture != null) StopCoroutine(_inventoryCapture);
        _inventoryCapture = null;
        _iconCache.Clear();
        _notices.ClearTransient();
        _modCatalog = null;
        _lastModCatalog = null;
    }

    private void AttachServerPeer(ZNetPeer peer)
    {
        ResetConnection();
        _serverPeer = peer;
        _serverRpc = peer?.m_rpc;
        _handshake.Begin(Time.unscaledTime);
    }

    private void OnDestroy()
    {
        _notices.Dispose();
        ResetConnection();
        Harmony.UnpatchID(PluginGuid);
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
    internal bool ShouldWaitForServerCharacter => _handshake.ShouldWait;

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    private static class ConnectionNoticePatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer __0)
        {
            if (Instance == null || __instance.IsServer() || __0?.m_rpc == null) return;
            Instance.AttachServerPeer(__0);
            __0.m_rpc.Register<bool>("VSM_InspectionPolicy", (rpc, required) => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.OnInspectionPolicy(required); });
            __0.m_rpc.Register<bool, int>("VSM_ServerPolicy", (rpc, required, timeout) => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.OnServerPolicy(required, timeout); });
            __0.m_rpc.Register<string>("VSM_InventoryRequest", (rpc, requestId) => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.OnInventoryRequest(__0.m_uid, requestId); });
            __0.m_rpc.Register<bool, string, bool>("VSM_CharacterProfile", (rpc, found, encoded, rejectPreviouslyUsed) => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.OnCharacterProfile(__0.m_uid, found, encoded, rejectPreviouslyUsed); });
            __0.m_rpc.Register("VSM_CharacterCheckpoint", rpc => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.OnCharacterCheckpoint(__0.m_uid); });
            __0.m_rpc.Register<string, string>("VSM_AdminNotice", (rpc, title, message) => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.QueueAdminNotice(title, message); });
            __0.m_rpc.Register<string>("ValheimServerManager_Manifest_v1", (rpc, json) => { if (ReferenceEquals(rpc, Instance?._serverRpc)) Instance.ReceiveModCatalog(json); });
        }
    }

    private void OnInventoryRequest(long sender, string requestId)
    {
        var server = ServerPeerId();
        if (_serverRpc == null || sender != server || string.IsNullOrEmpty(requestId) || requestId.Length > 100) return;
        if (!_allowInventory.Value)
        {
            Reply(requestId, new { ok = false, status = "denied", error = "The player has not enabled inventory inspection." });
            return;
        }
        var player = Player.m_localPlayer;
        if (player == null) { Reply(requestId, new { ok = false, status = "unavailable", error = "Player character is not loaded." }); return; }
        if (_inventoryCapture != null || Time.unscaledTime < _nextInventoryRequest)
        {
            Reply(requestId, new { ok = false, status = "busy", error = "An inventory snapshot is already being prepared. Try again in a moment." });
            return;
        }
        _nextInventoryRequest = Time.unscaledTime + 1f;
        try { CaptureInventory(requestId, player); }
        catch (Exception error)
        {
            Logger.LogWarning("Could not prepare inventory snapshot: " + error.Message);
            Reply(requestId, new { ok = false, status = "unavailable", error = "The character snapshot could not be prepared. Try again in a moment." });
        }
    }

    private void CaptureInventory(string requestId, Player player)
    {
        var inventory = player.GetInventory();
        var icons = new Dictionary<string, string>();
        var sprites = new Dictionary<string, Sprite>();
        var items = inventory.GetAllItems().Select(item =>
        {
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
            var iconKey = prefab + ":" + item.m_variant;
            if (!sprites.ContainsKey(iconKey)) sprites[iconKey] = item.GetIcon();
            return new
            {
                prefab,
                name = Localize(item.m_shared.m_name),
                description = Localize(item.m_shared.m_description),
                type = item.m_shared.m_itemType.ToString(),
                stack = item.m_stack,
                maxStack = item.m_shared.m_maxStackSize,
                quality = item.m_quality,
                maxQuality = item.m_shared.m_maxQuality,
                durability = item.m_durability,
                maxDurability = item.GetMaxDurability(),
                weight = item.GetWeight(),
                equipped = item.m_equipped,
                x = item.m_gridPos.x,
                y = item.m_gridPos.y,
                variant = item.m_variant,
                crafterName = item.m_crafterName,
                crafterId = item.m_crafterID.ToString(),
                teleportable = item.m_shared.m_teleportable,
                iconKey
            };
        }).ToArray();
        var skills = player.GetSkills().GetSkillList()
            .Where(skill => skill.m_level > 0f)
            .OrderByDescending(skill => skill.m_level)
            .Select(skill => new { name = skill.m_info.m_skill.ToString(), level = skill.m_level })
            .ToArray();
        var snapshot = new
        {
            ok = true,
            status = "available",
            player = player.GetPlayerName(),
            capturedAt = DateTime.UtcNow.ToString("O"),
            character = new
            {
                id = player.GetPlayerID().ToString(),
                name = player.GetPlayerName(),
                biome = player.GetCurrentBiome().ToString(),
                health = player.GetHealth(),
                maxHealth = player.GetMaxHealth(),
                stamina = player.GetStamina(),
                maxStamina = player.GetMaxStamina(),
                eitr = player.GetEitr(),
                maxEitr = player.GetMaxEitr(),
                armor = player.GetBodyArmor(),
                inventoryWidth = inventory.GetWidth(),
                inventoryHeight = inventory.GetHeight(),
                weight = inventory.GetTotalWeight(),
                skills
            },
            items,
            icons
        };
        _inventoryCapture = StartCoroutine(ReplyWithIcons(requestId, snapshot, sprites, icons, _serverRpc));
    }

    private IEnumerator ReplyWithIcons(string requestId, object snapshot, Dictionary<string, Sprite> sprites,
        Dictionary<string, string> icons, ZRpc serverRpc)
    {
        // Yield first so the coroutine handle is assigned even for an empty inventory.
        yield return null;
        var renderedThisFrame = 0;
        foreach (var pair in sprites)
        {
            if (!ReferenceEquals(serverRpc, _serverRpc) || !_allowInventory.Value) break;
            if (pair.Value == null) continue;
            var id = pair.Value.GetInstanceID();
            if (!_iconCache.TryGetValue(id, out var icon))
            {
                icon = EncodeIcon(pair.Value);
                if (_iconCache.Count >= 256) _iconCache.Clear();
                _iconCache[id] = icon;
                renderedThisFrame++;
            }
            if (!string.IsNullOrEmpty(icon)) icons[pair.Key] = icon;
            if (renderedThisFrame >= 2) { renderedThisFrame = 0; yield return null; }
        }
        try
        {
            if (ReferenceEquals(serverRpc, _serverRpc))
            {
                if (_allowInventory.Value) Reply(requestId, snapshot);
                else Reply(requestId, new { ok = false, status = "denied", error = "Inventory inspection was disabled by the player." });
            }
        }
        finally { _inventoryCapture = null; }
    }

    private static readonly Type LocalizationType = AccessTools.TypeByName("Localization");
    private static readonly FieldInfo LocalizationInstance = LocalizationType == null ? null : AccessTools.Field(LocalizationType, "instance");
    private static readonly PropertyInfo LocalizationProperty = LocalizationType == null ? null : AccessTools.Property(LocalizationType, "instance");
    private static readonly MethodInfo LocalizeMethod = LocalizationType == null ? null : AccessTools.Method(LocalizationType, "Localize", new[] { typeof(string) });

    private static string Localize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            var instance = LocalizationInstance?.GetValue(null) ?? LocalizationProperty?.GetValue(null, null);
            return instance != null && LocalizeMethod != null ? (string)LocalizeMethod.Invoke(instance, new object[] { value }) : value;
        }
        catch { return value; }
    }

    private string EncodeIcon(Sprite sprite)
    {
        if (sprite == null || sprite.texture == null) return null;
        RenderTexture target = null;
        Texture2D output = null;
        var previous = RenderTexture.active;
        try
        {
            const int size = 48;
            target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture.active = target;
            GL.Clear(true, true, Color.clear);
            var rect = sprite.textureRect;
            var scale = new Vector2(rect.width / sprite.texture.width, rect.height / sprite.texture.height);
            var offset = new Vector2(rect.x / sprite.texture.width, rect.y / sprite.texture.height);
            Graphics.Blit(sprite.texture, target, scale, offset);
            output = new Texture2D(size, size, TextureFormat.RGBA32, false);
            output.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            output.Apply();
            var bytes = ImageConversion.EncodeToPNG(output);
            return bytes == null || bytes.Length == 0 ? null : "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception exception)
        {
            Logger.LogDebug("Unable to encode inventory icon: " + exception.Message);
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (output != null) UnityEngine.Object.Destroy(output);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private void Reply(string requestId, object payload)
    {
        InvokeServer("VSM_InventoryResponse", requestId, JsonConvert.SerializeObject(payload));
    }

    internal void SendEvent(string eventType, object data)
    {
        if (!TelemetryEnabled) return;
        InvokeServer("VSM_CompanionEvent", JsonConvert.SerializeObject(new { eventType, data }));
    }

    private static void InvokeServer(string methodName, params object[] arguments)
    {
        Instance?._serverRpc?.Invoke(methodName, arguments);
    }

    private static long ServerPeerId() => Instance?._serverPeer?.m_uid ?? 0L;
    private bool TelemetryEnabled => _allowTelemetry.Value && _serverRpc != null;

    [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
    private static class FindSpawnPointPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (Instance == null || !Instance.ShouldWaitForServerCharacter) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
    private static class ProfileSavePatch
    {
        private static bool Prefix() => Instance?._handshake.Failed != true;
        private static void Postfix() => Instance?.UploadCharacter(alreadySaved: true);
    }

    [HarmonyPatch(typeof(Player), "OnDeath")]
    private static class DeathPatch
    {
        private static void Postfix(Player __instance)
        {
            if (Instance?.TelemetryEnabled != true || __instance != Player.m_localPlayer) return;
            var hit = Traverse.Create(__instance).Field("m_lastHit").GetValue<HitData>();
            var attacker = hit?.GetAttacker();
            Instance.SendEvent("player.death.reported", new
            {
                player = __instance.GetPlayerName(),
                position = __instance.transform.position,
                cause = attacker != null ? attacker.GetHoverName() : "environment",
                damage = hit?.GetTotalDamage() ?? 0f
            });
        }
    }

    [HarmonyPatch(typeof(Character), "OnDeath")]
    private static class KillPatch
    {
        private static void Postfix(Character __instance)
        {
            if (Instance?.TelemetryEnabled != true || __instance == null || __instance.IsPlayer() || Player.m_localPlayer == null) return;
            var hit = Traverse.Create(__instance).Field("m_lastHit").GetValue<HitData>();
            if (hit?.GetAttacker() != Player.m_localPlayer) return;
            Instance.SendEvent(__instance.IsBoss() ? "boss.kill.reported" : "creature.kill.reported", new
            {
                player = Player.m_localPlayer.GetPlayerName(),
                target = __instance.GetHoverName(),
                position = __instance.transform.position
            });
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    private static class CraftPatch
    {
        private sealed class CraftState { public string Name; public string Prefab; public int Before; }
        private static void Prefix(InventoryGui __instance, Player player, out CraftState __state)
        {
            __state = null;
            if (Instance?.TelemetryEnabled != true || player == null || player != Player.m_localPlayer) return;
            var recipe = Traverse.Create(__instance).Field("m_craftRecipe").GetValue<Recipe>();
            var item = recipe?.m_item?.m_itemData;
            __state = item == null ? null : new CraftState
            {
                Name = item.m_shared.m_name,
                Prefab = recipe.m_item.gameObject.name,
                Before = player.GetInventory().CountItems(item.m_shared.m_name)
            };
        }
        private static void Postfix(Player player, CraftState __state)
        {
            if (__state == null || player != Player.m_localPlayer) return;
            var crafted = player.GetInventory().CountItems(__state.Name) - __state.Before;
            if (crafted > 0) Instance.SendEvent("item.crafted", new { player = player.GetPlayerName(), prefab = __state.Prefab, name = __state.Name, stack = crafted });
        }
    }
}
