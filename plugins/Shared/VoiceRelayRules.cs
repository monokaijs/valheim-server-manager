#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimServerManager.VoiceSupport;

internal enum VoiceRelayResult
{
    Allowed, Disabled, NotReady, NotParticipating, ConsentRequired, ModReceiptRequired,
    AdmissionRejected, CharacterMissing, Self, OutOfRange, Disconnected, Backlog,
    InvalidPacket, RateLimited, SendFailure, NoRecipient, Relayed
}

internal static class VoiceRelayRules
{
    internal static VoiceRelayResult Eligibility(bool enabled, bool ready, bool participating,
        bool consent, bool modReceipt, bool pendingKick)
    {
        if (!enabled) return VoiceRelayResult.Disabled;
        if (!ready) return VoiceRelayResult.NotReady;
        if (!participating) return VoiceRelayResult.NotParticipating;
        if (!consent) return VoiceRelayResult.ConsentRequired;
        if (pendingKick) return VoiceRelayResult.AdmissionRejected;
        return modReceipt ? VoiceRelayResult.Allowed : VoiceRelayResult.ModReceiptRequired;
    }
    internal static VoiceRelayResult Recipient(bool self, VoiceRelayResult eligibility, bool character,
        float distanceSquared, float range, bool socket, int queued)
    {
        if (self) return VoiceRelayResult.Self;
        if (eligibility != VoiceRelayResult.Allowed) return eligibility;
        if (!character) return VoiceRelayResult.CharacterMissing;
        if (float.IsNaN(distanceSquared) || float.IsInfinity(distanceSquared) || distanceSquared > range * range)
            return VoiceRelayResult.OutOfRange;
        if (!socket) return VoiceRelayResult.Disconnected;
        return queued >= 16 * 1024 ? VoiceRelayResult.Backlog : VoiceRelayResult.Allowed;
    }
}

// Anonymous cumulative counters, no player/device identifiers or packet contents.
internal sealed class VoiceRelayDiagnostics
{
    private readonly Dictionary<VoiceRelayResult, long> _counts = new();
    private bool _dirty;
    private float _nextLog;
    internal void Record(VoiceRelayResult result)
    { _counts[result] = _counts.TryGetValue(result, out var count) ? count + 1 : 1; _dirty = true; }
    internal string Poll(float now)
    {
        if (!_dirty || now < _nextLog) return null;
        _nextLog = now + 10f; _dirty = false;
        return "Voice relay: " + string.Join(" ", _counts.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
    }
}

// Game RPC updates can deliver multiple 40 ms frames together. Permit 320 ms of
// catch-up while keeping the same 25 frame/s sustained per-sender limit.
internal sealed class VoiceFrameBudget
{
    internal const double FramesPerSecond = VoiceCodec.SampleRate / (double)VoiceCodec.FrameSamples;
    internal const double BurstFrames = 8d;
    private double _credits = BurstFrames;
    private DateTime _updatedAt;
    private bool _initialized;
    internal bool TryTake(DateTime now)
    {
        if (!_initialized) { _updatedAt = now; _initialized = true; }
        if (now > _updatedAt)
        { _credits = Math.Min(BurstFrames, _credits + (now - _updatedAt).TotalSeconds * FramesPerSecond); _updatedAt = now; }
        if (_credits + 1e-9d < 1d) return false;
        _credits = Math.Max(0d, _credits - 1d); return true;
    }
}
