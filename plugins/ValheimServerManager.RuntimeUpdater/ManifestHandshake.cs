using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using ValheimServerManager.Bootstrap;

namespace ValheimServerManager.RuntimeUpdater;

internal sealed class ManifestHandshake
{
    private const string ManifestRpc = "ValheimServerManager_Manifest_v1";
    private const string LegacyManifestRpc = "ServerModBootstrap_Manifest_v1";
    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static ManifestHandshake? _instance;
    private readonly ManualLogSource _log;

    public ManifestHandshake(ManualLogSource log)
    {
        _log = log;
    }

    public void Install(Harmony harmony)
    {
        _instance = this;
        var znetType = AccessTools.TypeByName("ZNet") ?? throw new TypeLoadException("Valheim ZNet type was not found");
        var onNewConnection = AccessTools.Method(znetType, "OnNewConnection")
            ?? throw new MissingMethodException(znetType.FullName, "OnNewConnection");
        var callback = AccessTools.Method(typeof(ManifestHandshake), nameof(OnNewConnectionPostfix))
            ?? throw new MissingMethodException(nameof(OnNewConnectionPostfix));
        harmony.Patch(onNewConnection, postfix: new HarmonyMethod(callback) { priority = Priority.First });
        _log.LogInfo("Installed the server manifest relay handshake");
    }

    private static void OnNewConnectionPostfix(object __instance, object __0)
    {
        _instance?.OnNewConnection(__instance, __0);
    }

    private void OnNewConnection(object znet, object peer)
    {
        try
        {
            var rpc = peer.GetType().GetField("m_rpc", AllMembers)?.GetValue(peer);
            if (rpc == null || IsServer(znet)) return;
            RuntimeUpdaterPlugin.Instance?.BeginConnection(rpc);
            RpcReflectionBridge.RegisterString(rpc, ManifestRpc, ReceiveManifest);
            RpcReflectionBridge.RegisterString(rpc, LegacyManifestRpc, ReceiveManifest);
            RpcReflectionBridge.RegisterStrings(rpc, "VSM_AdminNotice", (source, title, message) =>
                RuntimeUpdaterPlugin.Instance?.ReceiveServerNotice(source, title, message));
        }
        catch (Exception error)
        {
            _log.LogError("Manifest handshake failed: " + error);
        }
    }

    private static void ReceiveManifest(object rpc, string manifest)
    {
        RuntimeUpdaterPlugin.Instance?.QueueRelayedManifest(rpc, manifest);
    }

    private static bool IsServer(object znet)
    {
        var method = znet.GetType().GetMethod("IsServer", AllMembers, null, Type.EmptyTypes, null);
        if (method?.Invoke(znet, null) is bool result) return result;
        var field = znet.GetType().GetField("m_isServer", AllMembers);
        return field?.GetValue(field.IsStatic ? null : znet) is bool fallback && fallback;
    }
}
