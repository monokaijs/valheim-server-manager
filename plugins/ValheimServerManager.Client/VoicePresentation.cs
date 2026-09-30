using System;

namespace ValheimServerManager.Client;

internal enum VoiceIndicatorState { Muted, Ready, Transmitting, Unavailable }

// Presentation only: never opens a microphone or changes transport/voice policy.
internal static class VoicePresentation
{
    internal static VoiceIndicatorState State(bool enabled, bool policyReceived, bool serverEnabled,
        bool ready, bool microphoneUnavailable, bool transmitting)
    {
        if (!enabled || policyReceived && !serverEnabled) return VoiceIndicatorState.Muted;
        if (!ready || microphoneUnavailable) return VoiceIndicatorState.Unavailable;
        return transmitting ? VoiceIndicatorState.Transmitting : VoiceIndicatorState.Ready;
    }

    internal static float PanelScale(float width, float height) =>
        Math.Max(.1f, Math.Min(1f, Math.Min((width - 32f) / 620f, (height - 32f) / 680f)));

    internal static string Description(VoiceIndicatorState state) => state switch
    {
        VoiceIndicatorState.Muted => "Voice muted",
        VoiceIndicatorState.Ready => "Voice ready",
        VoiceIndicatorState.Transmitting => "Transmitting",
        _ => "Voice unavailable"
    };
}
