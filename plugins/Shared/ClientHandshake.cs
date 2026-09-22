using System;

namespace ValheimServerManager.ClientSupport;

// No Unity dependencies: connection timing is exercised by the regression suite.
internal sealed class ClientHandshake
{
    private float _nextHello;
    private float _deadline;
    private int _helloCount;
    public bool Active { get; private set; }
    public bool Acknowledged { get; private set; }
    public bool RequiresCharacter { get; private set; }
    public bool ReceivedProfile { get; private set; }
    public bool Ready { get; private set; }
    public bool Failed { get; private set; }
    public bool ShouldWait => Active && RequiresCharacter && !Ready;

    public void Begin(float now)
    {
        Reset();
        Active = true;
        _nextHello = now;
    }

    public bool TrySendHello(float now)
    {
        if (!Active || Acknowledged || Failed || _helloCount >= 3 || now < _nextHello) return false;
        _helloCount++;
        _nextHello = now + 2f;
        return true;
    }

    public void SetPolicy(bool requiresCharacter, int timeoutSeconds, float now)
    {
        if (!Active || Acknowledged || ReceivedProfile || Failed) return;
        Acknowledged = true;
        RequiresCharacter = requiresCharacter;
        Ready = !requiresCharacter;
        _deadline = now + Math.Max(30, Math.Min(120, timeoutSeconds));
    }

    public bool TryReceiveProfile(float now)
    {
        if (!Active || ReceivedProfile || Failed || (Acknowledged && !RequiresCharacter)) return false;
        Acknowledged = true;
        RequiresCharacter = true;
        ReceivedProfile = true;
        _deadline = now + 30f;
        return true;
    }

    public bool HasTimedOut(float now) => ShouldWait && !Failed && now >= _deadline;
    public void CompleteProfile() { if (!Failed) Ready = true; }
    public void Fail() { Failed = true; Ready = false; }

    public void Reset()
    {
        Active = Acknowledged = RequiresCharacter = ReceivedProfile = Ready = Failed = false;
        _nextHello = _deadline = 0;
        _helloCount = 0;
    }
}
