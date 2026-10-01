#nullable disable
using System;

namespace ValheimServerManager.Client;

internal enum VoiceIndicatorState { Muted, Ready, Transmitting, Unavailable }

// Presentation only: never opens a microphone or changes transport/voice policy.
internal static class VoicePresentation
{
    internal const float PanelWidth = 720f, PanelHeight = 814f;
    internal static bool HudVisible(VoiceIndicatorState state, bool speaking) =>
        state == VoiceIndicatorState.Transmitting && speaking;
    internal static bool ReadableFont(string name) => !string.IsNullOrWhiteSpace(name)
        && name.IndexOf("viking", StringComparison.OrdinalIgnoreCase) < 0
        && name.IndexOf("norse", StringComparison.OrdinalIgnoreCase) < 0;

    internal static VoiceIndicatorState State(bool enabled, bool policyReceived, bool serverEnabled,
        bool ready, bool microphoneUnavailable, bool transmitting)
    {
        if (!enabled || policyReceived && !serverEnabled) return VoiceIndicatorState.Muted;
        if (!ready || microphoneUnavailable) return VoiceIndicatorState.Unavailable;
        return transmitting ? VoiceIndicatorState.Transmitting : VoiceIndicatorState.Ready;
    }

    internal static float PanelScale(float width, float height) =>
        Math.Max(.1f, Math.Min(1f, Math.Min((width - 32f) / PanelWidth, (height - 32f) / PanelHeight)));

    internal static string Description(VoiceIndicatorState state) => state switch
    {
        VoiceIndicatorState.Muted => "Voice muted",
        VoiceIndicatorState.Ready => "Voice ready",
        VoiceIndicatorState.Transmitting => "Transmitting",
        _ => "Voice unavailable"
    };
}

// Sample-driven attack/release and hysteresis. Never opens a device.
internal sealed class VoiceLevel
{
    private float _lastFrameAt = float.NegativeInfinity, _speechUntil = float.NegativeInfinity;
    private int _waves;
    internal float Rms { get; private set; }
    internal bool Clipping { get; private set; }
    internal float Meter(float now) => now - _lastFrameAt <= .2f ? Math.Min(1f, Rms / .25f) : 0f;
    internal bool Speaking(float now) => now - _lastFrameAt <= .2f && now <= _speechUntil;
    internal int Waves(float now) => Speaking(now) ? _waves : 0;
    internal void Add(float rms, bool clipped, float threshold, float now)
    {
        if (float.IsNaN(rms) || float.IsInfinity(rms)) rms = 0f;
        rms = Math.Max(0f, Math.Min(1f, rms));
        threshold = float.IsNaN(threshold) || float.IsInfinity(threshold) ? .015f : Math.Max(.001f, Math.Min(.2f, threshold));
        Rms = now - _lastFrameAt > .2f ? rms : Rms + (rms - Rms) * (rms > Rms ? .65f : .25f);
        _lastFrameAt = now; Clipping = clipped;
        if (rms >= threshold || Speaking(now) && rms >= threshold * .7f) _speechUntil = now + .12f;
        var target = Rms >= threshold * 6f ? 3 : Rms >= threshold * 2.5f ? 2 : 1;
        // A small dead band keeps the wave count steady around level boundaries.
        if (target >= _waves || Rms < threshold * (_waves == 3 ? 5f : 2f)) _waves = target;
    }
    internal void Reset()
    { Rms = 0; Clipping = false; _waves = 0; _lastFrameAt = _speechUntil = float.NegativeInfinity; }
}

// Restore focus/cursor exactly once, including a partially built panel or failed
// teardown. The Unity-facing snapshots remain in VoiceSettingsPanel.
internal sealed class VoiceUiLifetime
{
    private Action _restore;
    internal bool IsOpen => _restore != null;
    internal void Begin(Action restore)
    {
        if (IsOpen) throw new InvalidOperationException("Voice panel already open");
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
    }
    internal void Close(Action teardown)
    {
        var restore = _restore;
        if (restore == null) return;
        _restore = null;
        try { teardown(); } finally { restore(); }
    }
}

internal enum VoiceSliderKind { Playback, Gain, Threshold }
internal sealed class VoiceSliderSpec
{
    internal const float Width = 656f, HitHeight = 44f, Thumb = 24f, Inset = 12f;
    internal readonly VoiceSliderKind Kind;
    internal readonly float Minimum, Maximum, Step, Default;
    internal VoiceSliderSpec(VoiceSliderKind kind)
    {
        Kind = kind;
        Minimum = kind == VoiceSliderKind.Threshold ? .001f : 0f;
        Maximum = kind == VoiceSliderKind.Playback ? 2f : kind == VoiceSliderKind.Gain ? 3f : .2f;
        Step = kind == VoiceSliderKind.Threshold ? .001f : .01f;
        Default = kind == VoiceSliderKind.Threshold ? .015f : 1f;
    }
    internal float Clamp(float value) => float.IsNaN(value) || float.IsInfinity(value) ? Default
        : Math.Max(Minimum, Math.Min(Maximum, (float)Math.Round(value / Step) * Step));
    internal string Format(float value) => Kind switch
    {
        VoiceSliderKind.Playback => (Clamp(value) * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
        VoiceSliderKind.Gain => Clamp(value).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "×",
        _ => (Clamp(value) * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "% RMS"
    };
}
