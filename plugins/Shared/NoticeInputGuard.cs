using HarmonyLib;
using UnityEngine;

namespace ValheimServerManager.ClientSupport;

internal static class NoticeInputGuard
{
    internal static bool NativeUiSupported { get; private set; }
    internal static bool ExternalModal { get; set; }
    private static bool _nativeModal;
    private static int _closedFrame = -1;
    internal static bool NativeModal
    {
        get => _nativeModal || Time.frameCount == _closedFrame;
        set { if (_nativeModal && !value) _closedFrame = Time.frameCount; _nativeModal = value; }
    }

    internal static bool Install(Harmony harmony)
    {
        NativeUiSupported = false;
        // IMGUI's ModalWindow blocks other IMGUI windows, but Valheim's menus use uGUI.
        var eventSystem = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
        var update = eventSystem == null ? null : AccessTools.Method(eventSystem, "Update");
        var popup = AccessTools.TypeByName("UnifiedPopup");
        var visible = popup == null ? null : AccessTools.Method(popup, "IsVisible");
        if (update == null || visible == null) return false;
        harmony.Patch(update, prefix: new HarmonyMethod(typeof(NoticeInputGuard), nameof(AllowMenuInput)));
        harmony.Patch(visible, postfix: new HarmonyMethod(typeof(NoticeInputGuard), nameof(IncludeOurModal)));
        // Menu.IsVisible also guards player movement and the camera. Keep the closing
        // frame blocked so Esc / controller Back cannot reopen a menu or trigger play.
        var menu = AccessTools.TypeByName("Menu");
        var menuVisible = menu == null ? null : AccessTools.Method(menu, "IsVisible");
        var menuActive = menu == null ? null : AccessTools.Method(menu, "IsActive");
        if (menuVisible == null || menuActive == null) return false;
        harmony.Patch(menuVisible, postfix: new HarmonyMethod(typeof(NoticeInputGuard), nameof(IncludeNativeModal)));
        harmony.Patch(menuActive, postfix: new HarmonyMethod(typeof(NoticeInputGuard), nameof(IncludeNativeModal)));
        NativeUiSupported = true;
        return true;
    }

    private static bool AllowMenuInput() => !(NoticeOverlay.BlocksMenuInput || ExternalModal);
    private static void IncludeOurModal(ref bool __result) => __result |= NoticeOverlay.BlocksMenuInput || ExternalModal || NativeModal;
    private static void IncludeNativeModal(ref bool __result) => __result |= NativeModal;
}
