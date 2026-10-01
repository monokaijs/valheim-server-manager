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
