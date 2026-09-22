#nullable disable
using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimServerManager.ClientSupport;

internal enum NoticeKind { Information, Attention, Error, RestartRequired }

internal sealed class ClientNotice
{
    public string Title { get; }
    public string Message { get; }
    public NoticeKind Kind { get; }
    public bool Persistent => Kind != NoticeKind.Information;
    public float VisibleUntil { get; set; }
    public ClientNotice(string title, string message, NoticeKind kind)
    {
        Title = Clean(title, 80, "Server notice");
        Message = Clean(message, 1600, "");
        Kind = kind;
    }

    internal static string Clean(string value, int maximum, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var result = new StringBuilder(Math.Min(value.Length, maximum));
        foreach (var character in value.Trim())
        {
            if (result.Length >= maximum) break;
            if (character == '\n' || !char.IsControl(character)) result.Append(character);
        }
        // Avoid ending a bounded string in the middle of an emoji/surrogate pair.
        if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
        return result.Length == 0 ? fallback : result.ToString();
    }

    public static NoticeKind ForServerTitle(string title) =>
        string.Equals(title, "Welcome", StringComparison.OrdinalIgnoreCase)
        || string.Equals(title, "Server notice", StringComparison.OrdinalIgnoreCase)
        || string.Equals(title, "Server restarting", StringComparison.OrdinalIgnoreCase)
            ? NoticeKind.Information : NoticeKind.Attention;
}

internal sealed class NoticeQueue
{
    private readonly List<ClientNotice> _pending = new();
    private ClientNotice _last;
    private float _lastAt;
    public ClientNotice Current { get; private set; }
    internal int Count => _pending.Count + (Current == null ? 0 : 1);

    public void Add(ClientNotice notice, float now)
    {
        if (Same(Current, notice) || _pending.Exists(item => Same(item, notice))
            || (Same(_last, notice) && now - _lastAt < 10f)) return;
        _last = notice;
        _lastAt = now;
        // A critical notice must not wait behind a welcome toast.
        if (notice.Persistent && Current != null && !Current.Persistent) Current = null;
        if (Count >= 8)
        {
            var informational = _pending.FindIndex(item => !item.Persistent);
            if (informational >= 0) _pending.RemoveAt(informational);
            else if (!notice.Persistent) return;
            else if (_pending.Count > 0) _pending.RemoveAt(0);
        }
        _pending.Add(notice);
    }

    public void Tick(bool playerReady, float now)
    {
        if (Current != null && !Current.Persistent && now >= Current.VisibleUntil) Current = null;
        if (Current != null) return;
        var index = _pending.FindIndex(item => item.Persistent);
        if (index < 0 && playerReady && _pending.Count > 0) index = 0;
        if (index < 0) return;
        Current = _pending[index];
        _pending.RemoveAt(index);
        Current.VisibleUntil = now + Math.Min(45f, Math.Max(8f, Current.Message.Length / 18f));
    }

    public void Dismiss() => Current = null;
    public void ClearTransient()
    {
        _pending.RemoveAll(item => !item.Persistent);
        if (Current != null && !Current.Persistent) Current = null;
    }

    private static bool Same(ClientNotice left, ClientNotice right) => left != null && right != null
        && left.Title == right.Title && left.Message == right.Message && left.Kind == right.Kind;
}
