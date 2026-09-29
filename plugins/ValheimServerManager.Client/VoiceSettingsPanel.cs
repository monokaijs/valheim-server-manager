using System;
using BepInEx.Configuration;
using UnityEngine;
using ValheimServerManager.ClientSupport;

namespace ValheimServerManager.Client;

internal sealed class VoiceSettingsPanel
{
    private readonly ConfigFile _config;
    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<VoiceChatMode> _mode;
    private readonly ConfigEntry<KeyCode> _pushToTalk;
    private readonly ConfigEntry<string> _inputDevice;
    private readonly ConfigEntry<float> _volume;
    private readonly ConfigEntry<float> _microphoneGain;
    private readonly ConfigEntry<float> _activationThreshold;
    private Vector2 _scroll;
    private Rect _window;
    private bool _bindingKey;
    private bool _previousCursorVisible;
    private CursorLockMode _previousCursorLock;

    internal bool IsOpen { get; private set; }

    internal VoiceSettingsPanel(ConfigFile config, ConfigEntry<bool> enabled, ConfigEntry<VoiceChatMode> mode,
        ConfigEntry<KeyCode> pushToTalk, ConfigEntry<string> inputDevice, ConfigEntry<float> volume,
        ConfigEntry<float> microphoneGain, ConfigEntry<float> activationThreshold)
    {
        _config = config;
        _enabled = enabled;
        _mode = mode;
        _pushToTalk = pushToTalk;
        _inputDevice = inputDevice;
        _volume = volume;
        _microphoneGain = microphoneGain;
        _activationThreshold = activationThreshold;
    }

    internal void Tick(bool playerReady)
    {
        if (!playerReady) { Close(); return; }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            if (IsOpen) Close(); else Open();
        }
        if (!IsOpen) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void Open()
    {
        _previousCursorLock = Cursor.lockState;
        _previousCursorVisible = Cursor.visible;
        IsOpen = true;
        NoticeInputGuard.ExternalModal = true;
    }

    internal void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _bindingKey = false;
        NoticeInputGuard.ExternalModal = false;
        Cursor.lockState = _previousCursorLock;
        Cursor.visible = _previousCursorVisible;
        _config.Save();
    }

    internal void Draw(bool serverPolicyReceived, bool serverEnabled)
    {
        if (!IsOpen) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        _window = new Rect(Mathf.Max(10f, (Screen.width - 540f) / 2f), 10f,
            Mathf.Min(540f, Screen.width - 20f), Mathf.Min(600f, Screen.height - 20f));
        var depth = GUI.depth;
        try
        {
            GUI.depth = -900;
            GUI.ModalWindow(0x56564F, _window, _ => DrawContents(serverPolicyReceived, serverEnabled),
                "Server Manager · Voice chat");
        }
        finally { GUI.depth = depth; }
    }

    private void DrawContents(bool serverPolicyReceived, bool serverEnabled)
    {
        var current = Event.current;
        if (current.type == EventType.KeyDown)
        {
            if (current.keyCode == KeyCode.Escape)
            {
                if (_bindingKey) _bindingKey = false; else Close();
                current.Use();
            }
            else if (_bindingKey && current.keyCode != KeyCode.None && current.keyCode != KeyCode.F8)
            {
                _pushToTalk.Value = current.keyCode;
                _bindingKey = false;
                current.Use();
            }
        }

        GUILayout.Space(8f);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Max(180f, _window.height - 75f)));
        _enabled.Value = GUILayout.Toggle(_enabled.Value, "Enable voice chat");
        if (serverPolicyReceived && !serverEnabled)
            GUILayout.Label("Voice is disabled by this server.");
        GUILayout.Space(10f);

        GUILayout.Label("Mode");
        var modes = new[] { "Push to talk", "Voice activation", "Open mic" };
        _mode.Value = (VoiceChatMode)GUILayout.Toolbar((int)_mode.Value, modes);
        GUILayout.Space(10f);

        GUILayout.Label("Microphone");
        if (GUILayout.Button(string.IsNullOrEmpty(_inputDevice.Value) ? "● System default" : "System default"))
            _inputDevice.Value = "";
        string[] devices;
        try { devices = Microphone.devices; }
        catch (Exception) { devices = Array.Empty<string>(); }
        if (devices == null || devices.Length == 0) GUILayout.Label("No microphones detected. Check system microphone permissions.");
        else
        {
            foreach (var device in devices)
                if (GUILayout.Button((_inputDevice.Value == device ? "● " : "") + device))
                    _inputDevice.Value = device;
            if (!string.IsNullOrEmpty(_inputDevice.Value) && Array.IndexOf(devices, _inputDevice.Value) < 0)
                GUILayout.Label("Selected microphone is unavailable; choose another device.");
        }
        GUILayout.Space(10f);

        GUILayout.Label("Push to talk key");
        if (GUILayout.Button(_bindingKey ? "Press a key · Esc cancels" : _pushToTalk.Value + " · click to change"))
            _bindingKey = true;
        GUILayout.Label("F8 opens or closes this panel.");
        GUILayout.Space(10f);

        _volume.Value = Slider("Playback volume", _volume.Value, 0f, 2f, "0.0");
        _microphoneGain.Value = Slider("Microphone gain", _microphoneGain.Value, 0f, 3f, "0.0");
        _activationThreshold.Value = Slider("Voice activation threshold", _activationThreshold.Value, .001f, .2f, "0.000");
        GUILayout.EndScrollView();
        if (GUILayout.Button("Close", GUILayout.Height(32f))) Close();
    }

    private static float Slider(string title, float value, float minimum, float maximum, string format)
    {
        GUILayout.Label(title + ": " + value.ToString(format));
        return GUILayout.HorizontalSlider(value, minimum, maximum);
    }
}
