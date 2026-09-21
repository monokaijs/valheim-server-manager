using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ValheimServerManager.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ClientPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.creaton.valheim-server-manager.client";
    public const string PluginName = "Valheim Server Manager Companion";
    public const string PluginVersion = "1.3.0";
    private const string LegacyPluginGuid = "dev.monokai.valheim-server-manager.client";
    internal static ClientPlugin Instance { get; private set; }
    private ConfigEntry<bool> _allowInventory;
    private ConfigEntry<bool> _allowTelemetry;
    private ConfigEntry<bool> _enableServerCharacters;
    private bool _registered;
    private bool _announced;
    private bool _awaitingCharacterProfile;
    private bool _serverCharacterMode;
    private bool _characterReady;
    private bool _bootstrapPending;
    private float _characterHandshakeStarted;
    private float _nextCharacterUpload;
    private byte[] _pendingCharacterProfile;
    private PlayerProfile _serverProfile;
    private string _temporaryProfileName;
    private bool _uploading;
    private Heightmap.Biome _biome;

    private void Awake()
    {
        Instance = this;
        MigrateLegacyConfig();
        _allowInventory = Config.Bind("Privacy", "AllowInventoryInspection", false, "Allow this server's authenticated dashboard to request an on-demand inventory snapshot.");
        _allowTelemetry = Config.Bind("Privacy", "AllowDetailedTelemetry", false, "Share death and biome events with the connected server.");
        _enableServerCharacters = Config.Bind("ServerCharacters", "Enabled", true, "Allow this server to make its native character profile authoritative for this session.");
        Harmony.CreateAndPatchAll(typeof(DeathPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(KillPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(CraftPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(FindSpawnPointPatch), PluginGuid);
        Harmony.CreateAndPatchAll(typeof(ProfileSavePatch), PluginGuid);
        Logger.LogInfo("Client companion loaded. Inventory and detailed telemetry are disabled until opted in.");
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
        if (!_registered && ZRoutedRpc.instance != null)
        {
            ZRoutedRpc.instance.Register<string>("VSM_InventoryRequest", OnInventoryRequest);
            ZRoutedRpc.instance.Register<bool, string>("VSM_CharacterProfile", OnCharacterProfile);
            ZRoutedRpc.instance.Register("VSM_CharacterCheckpoint", OnCharacterCheckpoint);
            _registered = true;
        }
        if (!_announced && _registered && ZNet.instance != null && !ZNet.instance.IsServer() && ZNet.instance.GetServerPeer() != null)
        {
            _awaitingCharacterProfile = _enableServerCharacters.Value;
            _characterReady = !_enableServerCharacters.Value;
            _characterHandshakeStarted = Time.unscaledTime;
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeerId(), "VSM_ClientHello", _allowInventory.Value, _enableServerCharacters.Value ? PluginVersion : "");
            _announced = true;
        }
        if (_pendingCharacterProfile != null && Game.instance != null) ApplyServerProfile();
        if (_bootstrapPending && Player.m_localPlayer != null && Game.instance != null) AdoptCurrentProfile();
        if (_awaitingCharacterProfile && !_serverCharacterMode && Time.unscaledTime - _characterHandshakeStarted > 8f)
        {
            _awaitingCharacterProfile = false;
            _characterReady = true;
            Logger.LogInfo("Connected server did not negotiate VSM server characters; continuing with the selected local profile.");
        }
        if (_serverCharacterMode && _characterReady && Player.m_localPlayer != null && Time.unscaledTime >= _nextCharacterUpload)
        {
            _nextCharacterUpload = Time.unscaledTime + 30f;
            UploadCharacter();
        }
        if (_allowTelemetry.Value && Player.m_localPlayer != null)
        {
            var biome = Player.m_localPlayer.GetCurrentBiome();
            if (_biome != Heightmap.Biome.None && biome != _biome) SendEvent("player.biome.changed", new { from = _biome.ToString(), to = biome.ToString(), player = Player.m_localPlayer.GetPlayerName() });
            _biome = biome;
        }
        if (ZNet.instance == null || ZNet.instance.GetServerPeer() == null) ResetConnection();
    }

    private void OnCharacterProfile(long sender, bool found, string encoded)
    {
        if (sender != ServerPeerId() || !_enableServerCharacters.Value) return;
        _serverCharacterMode = true;
        _awaitingCharacterProfile = true;
        try
        {
            if (found)
            {
                var bytes = Convert.FromBase64String(encoded ?? "");
                if (bytes.Length < 32 || bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Server character profile size is invalid.");
                _pendingCharacterProfile = bytes;
                if (Game.instance != null) ApplyServerProfile();
            }
            else
            {
                _awaitingCharacterProfile = false;
                _characterReady = true;
                _bootstrapPending = true;
                Logger.LogInfo("No server character exists yet; the selected profile will seed the first server-owned save.");
            }
        }
        catch (Exception exception)
        {
            Logger.LogError("Unable to receive server character: " + exception);
            _characterReady = false;
        }
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
            _awaitingCharacterProfile = false;
            _characterReady = true;
            _nextCharacterUpload = Time.unscaledTime + 30f;
            Logger.LogInfo("Server-owned character profile loaded for " + profile.GetName() + ".");
        }
        catch (Exception exception)
        {
            Logger.LogError("Unable to apply server-owned character: " + exception);
            _characterReady = false;
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
            _characterReady = true;
            UploadCharacter();
            Logger.LogInfo("First server-owned character profile created for " + profile.GetName() + ".");
        }
        catch (Exception exception)
        {
            Logger.LogError("Unable to seed server-owned character: " + exception);
            _characterReady = false;
        }
    }

    private void PrepareTemporaryProfileName()
    {
        if (!string.IsNullOrEmpty(_temporaryProfileName)) return;
        _temporaryProfileName = "VSMServer_" + Guid.NewGuid().ToString("N");
    }

    private void OnCharacterCheckpoint(long sender)
    {
        if (sender == ServerPeerId()) UploadCharacter();
    }

    internal void UploadCharacter(bool alreadySaved = false)
    {
        if (_uploading || !_serverCharacterMode || !_characterReady || _serverProfile == null || Player.m_localPlayer == null || ZRoutedRpc.instance == null) return;
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
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeerId(), "VSM_CharacterUpload", Convert.ToBase64String(bytes));
            try { File.Delete(path); } catch { }
        }
        catch (Exception exception) { Logger.LogError("Unable to upload server-owned character: " + exception); }
        finally { _uploading = false; }
    }

    private void ResetConnection()
    {
        if (!_announced && !_serverCharacterMode && !_awaitingCharacterProfile) return;
        if (!string.IsNullOrEmpty(_temporaryProfileName))
        {
            try { File.Delete(SaveSystem.GetCharacterPath(FileHelpers.FileSource.Local, _temporaryProfileName)); } catch { }
        }
        _announced = false;
        _awaitingCharacterProfile = false;
        _serverCharacterMode = false;
        _characterReady = false;
        _bootstrapPending = false;
        _pendingCharacterProfile = null;
        _serverProfile = null;
        _temporaryProfileName = null;
    }

    internal bool ShouldWaitForServerCharacter => _enableServerCharacters.Value && _awaitingCharacterProfile && !_characterReady;

    private void OnInventoryRequest(long sender, string requestId)
    {
        var server = ServerPeerId();
        if (sender != server) return;
        if (!_allowInventory.Value)
        {
            Reply(requestId, new { ok = false, status = "denied", error = "The player has not enabled inventory inspection." });
            return;
        }
        var player = Player.m_localPlayer;
        if (player == null) { Reply(requestId, new { ok = false, status = "unavailable", error = "Player character is not loaded." }); return; }
        var inventory = player.GetInventory();
        var icons = new Dictionary<string, string>();
        var items = inventory.GetAllItems().Select(item =>
        {
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
            var iconKey = prefab + ":" + item.m_variant;
            if (!icons.ContainsKey(iconKey))
            {
                var icon = EncodeIcon(item.GetIcon());
                if (!string.IsNullOrEmpty(icon)) icons[iconKey] = icon;
            }
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
        Reply(requestId, new
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
        });
    }

    private static string Localize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            var type = AccessTools.TypeByName("Localization");
            if (type == null) return value;
            var instance = AccessTools.Field(type, "instance")?.GetValue(null) ?? AccessTools.Property(type, "instance")?.GetValue(null, null);
            var method = AccessTools.Method(type, "Localize", new[] { typeof(string) });
            return instance != null && method != null ? (string)method.Invoke(instance, new object[] { value }) : value;
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
        ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeerId(), "VSM_InventoryResponse", requestId, JsonConvert.SerializeObject(payload));
    }

    internal void SendEvent(string eventType, object data)
    {
        if (!_allowTelemetry.Value || ZRoutedRpc.instance == null) return;
        ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeerId(), "VSM_CompanionEvent", JsonConvert.SerializeObject(new { eventType, data }));
    }

    private static long ServerPeerId() => ZNet.instance?.GetServerPeer()?.m_uid ?? 0L;

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
        private static void Postfix() => Instance?.UploadCharacter(alreadySaved: true);
    }

    [HarmonyPatch(typeof(Player), "OnDeath")]
    private static class DeathPatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
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
            if (__instance == null || __instance.IsPlayer() || Player.m_localPlayer == null) return;
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
