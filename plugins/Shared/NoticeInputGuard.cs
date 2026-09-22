using HarmonyLib;

namespace ValheimServerManager.ClientSupport;

internal static class NoticeInputGuard
{
    internal static bool ExternalModal { get; set; }

    internal static bool Install(Harmony harmony)
    {
        // IMGUI's ModalWindow blocks other IMGUI windows, but Valheim's menus use uGUI.
        var eventSystem = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
        var update = eventSystem == null ? null : AccessTools.Method(eventSystem, "Update");
        var popup = AccessTools.TypeByName("UnifiedPopup");
        var visible = popup == null ? null : AccessTools.Method(popup, "IsVisible");
        if (update == null || visible == null) return false;
        harmony.Patch(update, prefix: new HarmonyMethod(typeof(NoticeInputGuard), nameof(AllowMenuInput)));
        harmony.Patch(visible, postfix: new HarmonyMethod(typeof(NoticeInputGuard), nameof(IncludeOurModal)));
        return true;
    }

    private static bool AllowMenuInput() => !(NoticeOverlay.BlocksMenuInput || ExternalModal);
    private static void IncludeOurModal(ref bool __result) => __result |= NoticeOverlay.BlocksMenuInput || ExternalModal;
}
