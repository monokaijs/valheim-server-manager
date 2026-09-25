#nullable disable
using System;
using UnityEngine;

namespace ValheimServerManager.ClientSupport;

// Shared source keeps the early updater and the client fallback visually identical.
internal sealed class NoticeOverlay
{
    internal static bool BlocksMenuInput { get; private set; }
    private readonly NoticeQueue _queue = new();
    private GUIStyle _title;
    private GUIStyle _body;
    private GUIStyle _caption;
    private GUIStyle _button;
    private ClientNotice _displayed;
    private Vector2 _scroll;
    private bool _playerReady;
    private string _progress;
    private float _progressSince;
    private float _bodyHeight;
    private float _headerHeight;
    private float _panelHeight;
    private float _panelWidth;

    public void Show(string title, string message, NoticeKind kind) =>
        _queue.Add(new ClientNotice(title, message, kind), Time.unscaledTime);

    public void SetProgress(string progress)
    {
        if (string.IsNullOrEmpty(_progress)) _progressSince = Time.unscaledTime;
        _progress = progress;
    }

    public void ClearTransient() { _queue.ClearTransient(); _progress = null; }
    public void Tick(bool playerReady)
    {
        if (_playerReady && !playerReady) _queue.ClearTransient();
        _playerReady = playerReady;
        _queue.Tick(playerReady, Time.unscaledTime);
        BlocksMenuInput = _queue.Current?.Persistent == true && !playerReady;
    }

    public void Dispose() => BlocksMenuInput = false;

    public void Draw()
    {
        // Keep the custom panel at the menu; active play uses Valheim's message HUD.
        if (_playerReady) return;
        var notice = _queue.Current;
        var showProgress = !string.IsNullOrEmpty(_progress) && Time.unscaledTime - _progressSince >= 1f;
        if (notice == null && !showProgress) return;
        if (!ReferenceEquals(_displayed, notice)) { _displayed = notice; _scroll = Vector2.zero; }
        EnsureStyles();
        var matrix = GUI.matrix;
        var color = GUI.color;
        var depth = GUI.depth;
        try
        {
            var scale = Mathf.Min(Mathf.Clamp(Screen.height / 900f, 1f, 2f), Screen.width / 420f, Screen.height / 360f);
            var width = Screen.width / scale;
            var height = Screen.height / scale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            GUI.color = Color.white;
            GUI.depth = -1000;
            var modal = notice != null && notice.Persistent && !_playerReady;
            _panelWidth = Mathf.Min(modal ? 620f : 440f, width - 32f);
            var title = notice?.Title ?? "Preparing server mods";
            var message = notice?.Message ?? _progress;
            _headerHeight = 50f + _title.CalcHeight(new GUIContent(title), _panelWidth - 48f);
            _bodyHeight = _body.CalcHeight(new GUIContent(message), _panelWidth - 64f);
            _panelHeight = Mathf.Min(height - 48f, _headerHeight + Mathf.Min(_bodyHeight, modal ? 300f : 170f) + (modal ? 92f : 54f));
            if (modal)
            {
                GUI.ModalWindow(0x56534D, new Rect((width - _panelWidth) / 2f, (height - _panelHeight) / 2f, _panelWidth, _panelHeight),
                    _ => DrawPanel(title, message, true), GUIContent.none, GUIStyle.none);
            }
            else
            {
                GUI.BeginGroup(new Rect(width - _panelWidth - 16f, height - _panelHeight - 24f, _panelWidth, _panelHeight));
                DrawPanel(title, message, false);
                GUI.EndGroup();
            }
        }
        finally { GUI.matrix = matrix; GUI.color = color; GUI.depth = depth; }
    }

    private void DrawPanel(string title, string message, bool modal)
    {
        var notice = _queue.Current;
        var accent = notice?.Kind == NoticeKind.Error ? new Color(1f, .52f, .42f) : new Color(.94f, .76f, .39f);
        Fill(new Rect(0, 0, _panelWidth, _panelHeight), new Color(.075f, .085f, .10f, .98f));
        Fill(new Rect(0, 0, 4f, _panelHeight), accent);
        GUI.Label(new Rect(24f, 16f, _panelWidth - 48f, 20f), "SERVER MANAGER", _caption);
        GUI.Label(new Rect(24f, 42f, _panelWidth - 48f, _headerHeight - 42f), title, _title);
        var viewport = new Rect(24f, _headerHeight, _panelWidth - 48f, _panelHeight - _headerHeight - (modal ? 82f : 42f));
        if (modal || _bodyHeight > viewport.height)
        {
            _scroll = GUI.BeginScrollView(viewport, _scroll, new Rect(0, 0, viewport.width - 16f, _bodyHeight));
            GUI.Label(new Rect(0, 0, viewport.width - 16f, _bodyHeight), message, _body);
            GUI.EndScrollView();
        }
        else GUI.Label(viewport, message, _body);
        if (modal)
        {
            var buttonY = _panelHeight - 58f;
            var restart = notice?.Kind == NoticeKind.RestartRequired;
            if (GUI.Button(new Rect(24f, buttonY, 156f, 38f), restart ? "Quit Valheim" : "Dismiss", _button))
            {
                if (restart) Application.Quit(); else _queue.Dismiss();
            }
            if (GUI.Button(new Rect(_panelWidth - 180f, buttonY, 156f, 38f), restart ? "Later" : "Copy message", _button))
            {
                if (restart) _queue.Dismiss(); else GUIUtility.systemCopyBuffer = title + "\n" + message;
            }
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _queue.Dismiss();
                Event.current.Use();
            }
        }
        else
        {
            GUI.Label(new Rect(24f, _panelHeight - 30f, _panelWidth - 48f, 22f),
                notice?.Persistent == true ? "Details remain available after disconnecting." : _bodyHeight > viewport.height ? "Scroll to read the full message." : notice == null ? "Please wait. Files are being prepared for restart." : "From your server", _caption);
        }
    }

    private void EnsureStyles()
    {
        if (_body != null) return;
        _body = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true, richText = false, padding = new RectOffset(), alignment = TextAnchor.UpperLeft };
        _body.normal.textColor = new Color(.92f, .94f, .96f);
        _title = new GUIStyle(_body) { fontSize = 23, fontStyle = FontStyle.Bold };
        _caption = new GUIStyle(_body) { fontSize = 12 };
        _caption.normal.textColor = new Color(.72f, .76f, .81f);
        _button = new GUIStyle(GUI.skin.button) { fontSize = 16, richText = false, wordWrap = true };
    }

    private static void Fill(Rect area, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(area, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
