using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimServerManager.MapSupport;

namespace ValheimServerManager.Client;

public sealed partial class ClientPlugin
{
    private float _nextMapHello;
    private Coroutine _mapTeleport;
    private Player _mapTeleportPlayer;
    private Vector3 _mapTeleportOrigin;
    private Quaternion _mapTeleportRotation;
    private static readonly System.Reflection.FieldInfo TeleportingField = HarmonyLib.AccessTools.Field(typeof(Player), "m_teleporting");
    private static readonly System.Reflection.FieldInfo TeleportTimerField = HarmonyLib.AccessTools.Field(typeof(Player), "m_teleportTimer");
    private static readonly System.Reflection.FieldInfo TeleportCooldownField = HarmonyLib.AccessTools.Field(typeof(Player), "m_teleportCooldown");
    private static readonly System.Reflection.FieldInfo AirAltitudeField = HarmonyLib.AccessTools.Field(typeof(Player), "m_maxAirAltitude");
    private static bool MapRecoveryAvailable => TeleportingField?.FieldType == typeof(bool) && TeleportTimerField?.FieldType == typeof(float) && TeleportCooldownField?.FieldType == typeof(float) && AirAltitudeField?.FieldType == typeof(float);
    // Valheim has no public abort method. These verified native fields are also required before
    // advertising capability, so a failed area load cannot strand a player in the teleport state.
    private bool AbortMapTeleport(Player player, Vector3 origin, Quaternion rotation)
    {
        if (!MapRecoveryAvailable || player == null || player != Player.m_localPlayer || player.IsDead() || !player.GetComponent<ZNetView>().IsOwner()) return false;
        try
        {
            player.transform.SetPositionAndRotation(origin, rotation);
            var body = player.GetComponent<Rigidbody>();
#pragma warning disable CS0618
            if (body != null) body.velocity = Vector3.zero;
#pragma warning restore CS0618
            AirAltitudeField.SetValue(player, origin.y);
            TeleportingField.SetValue(player, false); TeleportTimerField.SetValue(player, 0f); TeleportCooldownField.SetValue(player, 0f);
            return true;
        }
        catch (Exception error) { Logger.LogWarning("Could not recover map teleport: " + error.Message); return false; }
    }
    private void TickMapHello()
    {
        if (!MapRecoveryAvailable || _serverRpc == null || !_handshake.Ready || Time.unscaledTime < _nextMapHello) return;
        _nextMapHello = Time.unscaledTime + 5;
        InvokeServer("VSM_MapHello", 1);
    }
    private void ResetMapConnection()
    {
        if (_mapTeleport != null) { StopCoroutine(_mapTeleport); AbortMapTeleport(_mapTeleportPlayer, _mapTeleportOrigin, _mapTeleportRotation); }
        _mapTeleportPlayer = null;
        _mapTeleport = null; _nextMapHello = 0;
    }
    private void OnMapTeleport(string requestId, string json)
    {
        if (_serverRpc == null || string.IsNullOrEmpty(requestId) || requestId.Length > 100 || json == null || json.Length > 2000) return;
        try
        {
            var data = JObject.Parse(json);
            var world = WorldGenerator.instance?.m_world;
            var player = Player.m_localPlayer;
            var x = (float?)data["x"] ?? float.NaN; var z = (float?)data["z"] ?? float.NaN; var y = (float?)data["y"];
            MapCoordinates.Validate(x, y, z);
            if (world == null || world.m_uid.ToString(System.Globalization.CultureInfo.InvariantCulture) != (string)data["worldId"])
                throw new InvalidOperationException("Client is in a different world.");
            if (!MapRecoveryAvailable || !_handshake.Ready || _bootstrapPending || _pendingDeathId != null || player == null || player.IsDead() || player.InIntro() || player.InCutscene() || player.IsTeleporting() || player.IsAttached() || _mapTeleport != null || !player.GetComponent<ZNetView>().IsOwner())
                throw new InvalidOperationException("Player is dead, loading, attached, teleporting, or not locally owned.");
            var height = WorldGenerator.instance.GetHeight(x, z);
            if (!MapCoordinates.Finite(height) || height < (ZoneSystem.instance?.m_waterLevel ?? 30) + .5f) throw new ArgumentException("Destination is not dry natural terrain.");
            if (y.HasValue && Math.Abs(y.Value - height - .5f) > 2) throw new ArgumentException("Y is too far from natural terrain.");
            var destination = new Vector3(x, height + .5f, z);
            var origin = player.transform.position;
            var rotation = player.transform.rotation;
            // Non-distant native teleport loads the target area and returns to origin if no floor exists.
            if (!player.TeleportTo(destination, rotation, false)) throw new InvalidOperationException("Native teleport refused; wait for the player cooldown.");
            _mapTeleportPlayer = player; _mapTeleportOrigin = origin; _mapTeleportRotation = rotation;
            _mapTeleport = StartCoroutine(FinishMapTeleport(_serverRpc, player, requestId, destination, origin, rotation, y));
        }
        catch (Exception error) { MapTeleportReply(requestId, false, error.Message); }
    }
    private IEnumerator FinishMapTeleport(ZRpc connection, Player player, string requestId, Vector3 destination, Vector3 origin, Quaternion rotation, float? manualY)
    {
        var deadline = Time.unscaledTime + 25;
        while (ReferenceEquals(connection, _serverRpc) && player == Player.m_localPlayer && !player.IsDead() && player.IsTeleporting() && Time.unscaledTime < deadline) yield return null;
        if (!ReferenceEquals(connection, _serverRpc) || player != Player.m_localPlayer || player.IsDead()) { _mapTeleport = null; yield break; }
        if (player.IsTeleporting())
        {
            var restored = AbortMapTeleport(player, origin, rotation);
            MapTeleportReply(requestId, false, restored ? "Destination did not load. Returned to the original position." : "Teleport outcome unknown: destination loading timed out. Check position before retrying.", status: restored ? "failure" : "unknown");
            _mapTeleport = null; _mapTeleportPlayer = null; yield break;
        }
        var valid = !player.IsTeleporting() && ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(destination)
            && Vector2.Distance(new Vector2(player.transform.position.x, player.transform.position.z), new Vector2(destination.x, destination.z)) < 2;
        var position = destination;
        if (valid)
        {
            // Check the actual loaded floor (including terrain modifications), not only the seed height.
            var mask = LayerMask.GetMask("terrain", "piece", "static_solid", "Default");
            valid = Physics.Raycast(destination + Vector3.up * 4, Vector3.down, out var hit, 12, mask, QueryTriggerInteraction.Ignore)
                && hit.normal.y >= .6f && hit.point.y >= (ZoneSystem.instance?.m_waterLevel ?? 30) + .5f && (!manualY.HasValue || (manualY.Value >= hit.point.y + .5f && manualY.Value <= hit.point.y + 2));
            if (valid)
            {
                position = new Vector3(destination.x, manualY ?? hit.point.y + .5f, destination.z);
                valid = !Physics.CheckCapsule(position + Vector3.up * .4f, position + Vector3.up * 1.5f, .35f, mask, QueryTriggerInteraction.Ignore);
            }
        }
        if (valid)
        {
            player.transform.position = position;
            MapTeleportReply(requestId, true, "", position);
        }
        else
        {
            // Native teleport has finished; its cooldown must elapse before a safe return.
            var cooldown = Time.unscaledTime + 2.1f;
            while (ReferenceEquals(connection, _serverRpc) && player == Player.m_localPlayer && !player.IsDead() && Time.unscaledTime < cooldown) yield return null;
            if (ReferenceEquals(connection, _serverRpc) && player == Player.m_localPlayer && !player.IsDead() && !player.IsTeleporting() && player.TeleportTo(origin, rotation, false))
            {
                deadline = Time.unscaledTime + 18;
                while (ReferenceEquals(connection, _serverRpc) && player == Player.m_localPlayer && !player.IsDead() && player.IsTeleporting() && Time.unscaledTime < deadline) yield return null;
            }
            if (ReferenceEquals(connection, _serverRpc) && player == Player.m_localPlayer && !player.IsDead())
            {
                if (player.IsTeleporting()) AbortMapTeleport(player, origin, rotation);
                var restored = Vector3.Distance(player.transform.position, origin) < 3 && !player.IsTeleporting();
                MapTeleportReply(requestId, false, restored ? "Destination floor/collision check failed. Returned to the original position." : "Destination check failed; return outcome is unknown. Verify position before retrying.", status: restored ? "failure" : "unknown");
            }
        }
        _mapTeleport = null; _mapTeleportPlayer = null;
    }
    private void MapTeleportReply(string requestId, bool ok, string error, Vector3? position = null, string status = null) => InvokeServer("VSM_TeleportResult", requestId, Newtonsoft.Json.JsonConvert.SerializeObject(new { ok, error, status = status ?? (ok ? "success" : "failure"), position = position.HasValue ? new { x = position.Value.x, y = position.Value.y, z = position.Value.z } : null }));
}
