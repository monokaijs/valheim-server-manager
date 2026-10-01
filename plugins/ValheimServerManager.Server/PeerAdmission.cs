using System;
using System.Collections.Generic;

namespace ValheimServerManager.ServerSupport;

// Pending hellos belong to the connection, not an ID that Valheim has yet to assign.
internal sealed class PendingClientHellos<TPeer> where TPeer : class
{
    private readonly Dictionary<TPeer, Tuple<bool, string>> _pending = new();

    internal bool DeferIfUnauthenticated(TPeer peer, long authenticatedId, bool inventoryAllowed, string version)
    {
        if (peer == null || authenticatedId != 0) return false;
        _pending[peer] = Tuple.Create(inventoryAllowed, version ?? "");
        return true;
    }

    internal void Complete(TPeer peer, long authenticatedId, Action<bool, string> accept)
    {
        if (peer == null || authenticatedId == 0 || !_pending.TryGetValue(peer, out var hello)) return;
        // Remove first: accepting a hello may release admission and re-enter Joined.
        _pending.Remove(peer);
        accept(hello.Item1, hello.Item2);
    }

    internal void Remove(TPeer peer) { if (peer != null) _pending.Remove(peer); }
}

internal enum ClientRequirement { ServerCharacter, Inspection, Mods }

internal sealed class ScheduledKick
{
    internal DateTime Due;
    internal bool ServerCharacterRequirement, InspectionRequirement, ModRequirement;

    // A hello/receipt can satisfy its own requirement; it cannot cancel an unrelated kick.
    internal bool Satisfy(ClientRequirement requirement)
    {
        bool matched;
        switch (requirement)
        {
            case ClientRequirement.ServerCharacter:
                matched = ServerCharacterRequirement; ServerCharacterRequirement = false; break;
            case ClientRequirement.Inspection:
                matched = InspectionRequirement; InspectionRequirement = false; break;
            default:
                matched = ModRequirement; ModRequirement = false; break;
        }
        return matched && !ServerCharacterRequirement && !InspectionRequirement && !ModRequirement;
    }
}
