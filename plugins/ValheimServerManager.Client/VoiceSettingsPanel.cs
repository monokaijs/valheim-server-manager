using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimServerManager.ClientSupport;

namespace ValheimServerManager.Client;

// Build our own controls from the game's visual resources. Never instantiate Settings:
// its Awake/OnDestroy would change global settings, subscriptions and save behavior.
internal sealed class VoiceSettingsPanel
{
    private readonly ConfigFile _config;
    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<VoiceChatMode> _mode;
    private readonly ConfigEntry<KeyCode> _pushToTalk;
    private readonly ConfigEntry<string> _inputDevice;
    private readonly ConfigEntry<float> _volume, _microphoneGain, _activationThreshold;
    private readonly List<Selectable> _controls = new();
    private GameObject _root, _previousSelection;
    private RectTransform _panel, _canvasRect;
    private Canvas _gameCanvas;
    internal float HudScale { get; private set; } = 1f;
    private TMP_Text _fontTemplate, _status, _enableLabel, _modeLabel, _modeHelp, _deviceLabel, _deviceHelp, _keyLabel;
    private TMP_FontAsset _bodyFont;
    private Font _ownedBodySource;
    private bool _ownsBodyFont;
    private TMP_Text _captureStatus, _transportStatus, _playbackStatus, _meterText, _flowCounts;
    private RectTransform _thresholdMarker;
    private RectTransform _meterFill;
    private Image _meterImage, _voiceImage;
    private VoiceChatClient _audio;
    private string _gateStatus = "Waiting for server voice policy";
    internal void SetDiagnostics(string gate, VoiceChatClient audio) { _gateStatus = gate; _audio = audio; }
    private Button _buttonTemplate;
    private static readonly Color Gold = new(1f, .78f, .40f);
    private static readonly Color MutedText = new(.64f, .73f, .75f);
    private static readonly Color Surface = new(.12f, .18f, .20f);
    private static readonly Color Track = new(.055f, .10f, .12f);
    private bool _bindingKey, _previousCursorVisible;
    private readonly VoiceUiLifetime _lifetime = new();
    private bool _ownsModal => _lifetime.IsOpen;
    private CursorLockMode _previousCursorLock;
    private string[] _devices = Array.Empty<string>();
    private float _nextDeviceRefresh;
    private int _bindingStartedFrame;
    internal bool IsOpen => _root != null;
    internal bool IsBindingKey => _bindingKey;

    internal VoiceSettingsPanel(ConfigFile config, ConfigEntry<bool> enabled, ConfigEntry<VoiceChatMode> mode,
        ConfigEntry<KeyCode> pushToTalk, ConfigEntry<string> inputDevice, ConfigEntry<float> volume,
        ConfigEntry<float> microphoneGain, ConfigEntry<float> activationThreshold)
    {
        _config = config; _enabled = enabled; _mode = mode; _pushToTalk = pushToTalk;
        _inputDevice = inputDevice; _volume = volume; _microphoneGain = microphoneGain; _activationThreshold = activationThreshold;
    }

    internal void Tick(bool playerReady, bool policyReceived, bool serverEnabled)
    {
        if (!playerReady || _ownsModal && (_root == null || _canvasRect == null)) { Close(); return; }
        if (_gameCanvas == null && Menu.instance != null) _gameCanvas = Menu.instance.GetComponentInParent<Canvas>();
        HudScale = _gameCanvas != null ? Mathf.Clamp(_gameCanvas.scaleFactor, .5f, 3f) : Mathf.Clamp(Screen.height / 1080f, .75f, 2f);
        if (Input.GetKeyDown(KeyCode.F8))
        {
            if (IsOpen) { Close(); return; }
            // Do not steal focus from chat, inventory, native settings or other modals.
            if (!Menu.IsVisible() && !InventoryGui.IsVisible() && !Console.IsVisible()
                && !TextInput.IsVisible() && !UnifiedPopup.IsVisible() && !(Chat.instance && Chat.instance.HasFocus()))
            {
                try { Open(); }
                catch (Exception error) { Close(); ClientPlugin.Instance?.LogVoiceUiWarning("Voice settings UI: " + error.Message); }
            }
        }
        if (!IsOpen) return;
        var bindingBefore = _bindingKey;
        if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
        {
            ZInput.ResetButtonStatus("JoyButtonB");
            if (_bindingKey) _bindingKey = false; else { Close(); return; }
        }
        else if (_bindingKey && Time.frameCount > _bindingStartedFrame && Input.anyKeyDown)
        {
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                if (key != KeyCode.None && key != KeyCode.F8 && key != KeyCode.Escape && (int)key < (int)KeyCode.JoystickButton0
                    && Input.GetKeyDown(key)) { _pushToTalk.Value = key; _bindingKey = false; break; }
        }
        foreach (var control in _controls) control.interactable = !_bindingKey;
        if (bindingBefore && !_bindingKey) EventSystem.current?.SetSelectedGameObject(_keyLabel.GetComponentInParent<Button>().gameObject);
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        var size = _canvasRect.rect.size;
        _panel.localScale = Vector3.one * VoicePresentation.PanelScale(size.x, size.y);
        if (Time.unscaledTime >= _nextDeviceRefresh)
        {
            _nextDeviceRefresh = Time.unscaledTime + 2f;
            try { _devices = Microphone.devices ?? Array.Empty<string>(); } catch { _devices = Array.Empty<string>(); }
        }
        _status.text = _gateStatus;
        var meter = _audio?.Level.Meter(Time.unscaledTime) ?? 0f;
        _meterFill.sizeDelta = new Vector2(656 * meter, 10);
        _thresholdMarker.anchoredPosition = new Vector2(656 * Mathf.Clamp01(_activationThreshold.Value / .25f), 2);
        var clipping = meter > 0 && _audio?.Level.Clipping == true;
        _meterImage.color = clipping ? new Color(1f, .35f, .22f) : new Color(.5f, .82f, .65f);
        _meterText.text = clipping ? "Clipping · reduce gain" : meter > 0 ? "Input level" : "Input idle / silent";
        _voiceImage.sprite = VoiceIcon.GetSprite(_audio?.WaveCount ?? 0);
        _voiceImage.enabled = _voiceImage.sprite != null;
        _captureStatus.text = _audio?.CaptureStatus ?? "Microphone idle";
        _transportStatus.text = _audio?.TransportStatus ?? "No frames sent";
        _flowCounts.text = _audio == null ? "" : $"Sent {_audio.SentFrames} · received {_audio.ReceivedFrames} · output gaps {_audio.Underruns}";
        _playbackStatus.text = _audio == null ? "No voice received" : _audio.PlaybackStatus +
            (_audio.OutputSamples > 0 ? " · output samples consumed" : "");
        _enableLabel.text = _enabled.Value ? "Voice enabled" : "Voice muted";
        _modeLabel.text = _mode.Value switch { VoiceChatMode.PushToTalk => "Push to talk  ›", VoiceChatMode.VoiceActivation => "Voice activation  ›", _ => "Open microphone  ›" };
        _modeHelp.text = _mode.Value switch { VoiceChatMode.PushToTalk => "Hold your key to send. The input meter follows capture.", VoiceChatMode.VoiceActivation => "Sends speech above the threshold; brief release prevents cuts.", _ => "Sends continuously. The HUD appears only for speech." };
        _deviceLabel.text = (string.IsNullOrEmpty(_inputDevice.Value) ? "System default" : _inputDevice.Value) + "  ›";
        _deviceHelp.text = _devices.Length == 0 ? "No microphones detected. Check system permissions."
            : !string.IsNullOrEmpty(_inputDevice.Value) && Array.IndexOf(_devices, _inputDevice.Value) < 0 ? "Selected microphone unavailable. Choose another device." : "Select to cycle through available microphones.";
        _keyLabel.text = _bindingKey ? "Press a key · Esc / B cancels" : _pushToTalk.Value + " · Change";
    }

    private void Open()
    {
        if (!NoticeInputGuard.NativeUiSupported)
        { ClientPlugin.Instance?.LogVoiceUiWarning("Voice settings requires the native game input guard on this Valheim build."); return; }
        var menu = Menu.instance;
        if (menu == null || menu.m_settingsPrefab == null || menu.m_settingsButton == null) return;
        var canvas = menu.GetComponentInParent<Canvas>();
        _buttonTemplate = menu.m_settingsButton;
        _fontTemplate = _buttonTemplate.GetComponentInChildren<TMP_Text>(true);
        // Read fonts from inactive native resources; controls use our own geometry.
        if (canvas == null || _fontTemplate == null)
        { ClientPlugin.Instance?.LogVoiceUiWarning("Voice settings could not find the native game UI resources."); return; }
        ResolveBodyFont(menu.m_settingsPrefab);
        if (_bodyFont == null) { ClientPlugin.Instance?.LogVoiceUiWarning("Voice settings needs a readable body font; native/OS font fallback failed."); ReleaseBodyFont(); return; }
        _previousCursorLock = Cursor.lockState; _previousCursorVisible = Cursor.visible;
        _previousSelection = EventSystem.current?.currentSelectedGameObject;
        _lifetime.Begin(() =>
        {
            NoticeInputGuard.NativeModal = false;
            Cursor.lockState = _previousCursorLock; Cursor.visible = _previousCursorVisible;
            EventSystem.current?.SetSelectedGameObject(_previousSelection != null && _previousSelection.activeInHierarchy ? _previousSelection : null);
            _previousSelection = null;
            PlayerController.SetTakeInputDelay(.1f);
        });
        _canvasRect = canvas.GetComponent<RectTransform>();
        _root = new GameObject("VSM Voice Settings", typeof(RectTransform));
        _root.SetActive(false);
        _root.transform.SetParent(canvas.transform, false);
        Stretch(_root.GetComponent<RectTransform>());
        var overlayCanvas = _root.AddComponent<Canvas>(); overlayCanvas.overrideSorting = true; overlayCanvas.sortingOrder = 30000;
        _root.AddComponent<GraphicRaycaster>();
        _root.AddComponent<CanvasGroup>();
        var veil = _root.AddComponent<Image>(); veil.color = new Color(0, 0, 0, .65f);
        _panel = Rect("Panel", _root.transform, 0, 0, VoicePresentation.PanelWidth, VoicePresentation.PanelHeight);
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(.5f, .5f); _panel.anchoredPosition = Vector2.zero;
        var panelBack = _panel.gameObject.AddComponent<Image>(); panelBack.color = new Color(.075f, .12f, .14f);
        var topAccent = Rect("Header accent", _panel, 0, 0, VoicePresentation.PanelWidth, 2);
        DecorativeImage(topAccent, Gold);
        var iconRect = Rect("PNG microphone", _panel, 30, 25, 42, 34);
        _voiceImage = iconRect.gameObject.AddComponent<Image>(); _voiceImage.raycastTarget = false;
        _voiceImage.preserveAspect = true; _voiceImage.color = Gold;
        Label("Voice chat", 86, 20, 380, 44, 32, Gold, heading: true);
        _enableLabel = Button("", 512, 24, 176, () => _enabled.Value = !_enabled.Value);
        Label("Nearby voices fade naturally with distance.", 32, 70, 656, 24, 17, MutedText);
        var statusBack = Rect("Connection status", _panel, 32, 106, 656, 40);
        DecorativeImage(statusBack, Surface);
        _status = Label("", 44, 106, 632, 40, 16);
        _status.textWrappingMode = TextWrappingModes.Normal;
        Label("VOICE MODE", 32, 160, 320, 20, 14, MutedText);
        Label("PUSH TO TALK KEY", 372, 160, 316, 20, 14, MutedText);
        _modeLabel = Button("", 32, 184, 320, () => _mode.Value = (VoiceChatMode)(((int)_mode.Value + 1) % 3));
        _keyLabel = Button("", 372, 184, 316, () => { _bindingKey = true; _bindingStartedFrame = Time.frameCount; });
        _modeHelp = Label("", 32, 232, 656, 24, 16, MutedText);
        Label("MICROPHONE", 32, 270, 656, 20, 14, MutedText);
        _deviceLabel = Button("", 32, 294, 656, CycleDevice);
        _deviceHelp = Label("", 32, 342, 656, 22, 15, MutedText);
        _meterText = Label("", 32, 370, 656, 22, 15);
        var meterBack = Rect("Input meter", _panel, 32, 396, 656, 10);
        DecorativeImage(meterBack, Track);
        _meterFill = Rect("Level", meterBack, 0, 0, 0, 10);
        _meterImage = _meterFill.gameObject.AddComponent<Image>(); _meterImage.raycastTarget = false;
        _thresholdMarker = Rect("Speech threshold marker", meterBack, 0, -2, 2, 14);
        DecorativeImage(_thresholdMarker, Gold);
        _captureStatus = Label("", 32, 412, 656, 24, 15, MutedText);
        AddSlider("Playback volume", _volume, VoiceSliderKind.Playback, 450);
        AddSlider("Microphone gain", _microphoneGain, VoiceSliderKind.Gain, 532);
        AddSlider("Speech threshold", _activationThreshold, VoiceSliderKind.Threshold, 614);
        _transportStatus = Label("", 32, 700, 656, 20, 14, MutedText);
        _playbackStatus = Label("", 32, 720, 656, 20, 14, MutedText);
        _flowCounts = Label("", 32, 744, 420, 32, 13, MutedText);
        Button("Close · F8 / Esc / B", 472, 756, 216, Close);
        WireNavigation();
        var group = _root.AddComponent<UIGroupHandler>(); group.m_groupPriority = 1000; group.m_defaultElement = _controls[0].gameObject;
        _nextDeviceRefresh = 0;
        NoticeInputGuard.NativeModal = true;
        _root.SetActive(true);
        EventSystem.current?.SetSelectedGameObject(_controls[0].gameObject);
    }

    private void ResolveBodyFont(GameObject settings)
    {
        // System font keeps body text readable even when every native template is decorative.
        try
        {
            _ownedBodySource = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" }, 18);
            if (_ownedBodySource != null)
            { _bodyFont = TMP_FontAsset.CreateFontAsset(_ownedBodySource); _ownsBodyFont = _bodyFont != null; }
        }
        catch (Exception) { /* Try the TMP default after an unavailable system font. */ }
        // When OS fonts are unavailable, prefer a distinct regular native font.
        if (_bodyFont == null) foreach (var text in settings.GetComponentsInChildren<TMP_Text>(true))
            if (text.font != null && text.font != _fontTemplate.font && VoicePresentation.ReadableFont(text.font.name))
            { _bodyFont = text.font; return; }
        if (_bodyFont == null && TMP_Settings.defaultFontAsset != _fontTemplate.font
            && TMP_Settings.defaultFontAsset != null && VoicePresentation.ReadableFont(TMP_Settings.defaultFontAsset.name))
            _bodyFont = TMP_Settings.defaultFontAsset;
    }

    private void ReleaseBodyFont()
    {
        if (_ownsBodyFont && _bodyFont != null)
        {
            foreach (var texture in _bodyFont.atlasTextures) if (texture != null) UnityEngine.Object.Destroy(texture);
            if (_bodyFont.material != null) UnityEngine.Object.Destroy(_bodyFont.material);
            UnityEngine.Object.Destroy(_bodyFont);
        }
        if (_ownedBodySource != null) UnityEngine.Object.Destroy(_ownedBodySource);
        _bodyFont = null; _ownedBodySource = null; _ownsBodyFont = false;
    }

    private void CycleDevice()
    {
        var index = Array.IndexOf(_devices, _inputDevice.Value);
        _inputDevice.Value = string.IsNullOrEmpty(_inputDevice.Value) ? (_devices.Length > 0 ? _devices[0] : "")
            : index >= 0 && index + 1 < _devices.Length ? _devices[index + 1] : "";
    }

    internal void Close()
    {
        if (!_ownsModal && _root == null) { ReleaseBodyFont(); return; }
        var root = _root; _root = null;
        _controls.Clear(); _bindingKey = false;
        try
        {
            _lifetime.Close(() =>
            {
                try { if (root != null) { root.SetActive(false); UnityEngine.Object.Destroy(root); } }
                finally { ReleaseBodyFont(); }
            });
        }
        finally { _config.Save(); }
    }

    private TMP_Text Label(string text, float x, float y, float width, float height, float size, Color? color = null, Transform parent = null, bool heading = false)
    {
        var rect = Rect("Label", parent ?? _panel, x, y, width, height);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = heading ? _fontTemplate.font : _bodyFont;
        label.fontSharedMaterial = heading ? _fontTemplate.fontSharedMaterial : _bodyFont.material;
        label.richText = false;
        label.fontSize = size; label.color = color ?? new Color(.93f, .96f, .95f); label.alignment = TextAlignmentOptions.MidlineLeft;
        label.overflowMode = TextOverflowModes.Ellipsis; label.text = text; label.raycastTarget = false;
        return label;
    }

    private TMP_Text Button(string text, float x, float y, float width, Action action)
    {
        var rect = Rect("Button", _panel, x, y, width, 42);
        var image = rect.gameObject.AddComponent<Image>(); image.color = Color.white;
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        button.colors = ControlColors();
        button.onClick.AddListener(() => action()); _controls.Add(button);
        var label = Label(text, 12, 0, width - 24, 42, 18, parent: rect); label.alignment = TextAlignmentOptions.Center;
        return label;
    }

    private static ColorBlock ControlColors() => new()
    {
        normalColor = Surface, highlightedColor = new Color(.25f, .33f, .33f),
        selectedColor = new Color(.29f, .37f, .36f), pressedColor = new Color(.36f, .43f, .39f),
        disabledColor = new Color(.10f, .14f, .15f), colorMultiplier = 1f, fadeDuration = .08f
    };
    private static Image DecorativeImage(RectTransform rect, Color color)
    {
        var image = rect.gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
    }
    private void AddSlider(string title, ConfigEntry<float> entry, VoiceSliderKind kind, float y)
    {
        var spec = new VoiceSliderSpec(kind);
        Label(title, 32, y, 420, 24, 20);
        var valueLabel = Label(spec.Format(entry.Value), 500, y, 188, 24, 20, Gold);
        valueLabel.alignment = TextAlignmentOptions.MidlineRight;
        var rect = Rect(title, _panel, 32, y + 28, VoiceSliderSpec.Width, VoiceSliderSpec.HitHeight);
        // The entire 44 px row accepts a click/drag. The track and fill share the
        // same endpoints as the thumb centre; no prefab scaling or hidden offsets.
        var hit = rect.gameObject.AddComponent<Image>(); hit.color = Color.clear;
        var track = Rect("Track", rect, VoiceSliderSpec.Inset, 18, 632, 8);
        DecorativeImage(track, Track);
        var fill = Rect("Fill", track, 0, 0, 632, 8); Stretch(fill);
        DecorativeImage(fill, new Color(.57f, .77f, .68f));
        var handles = Rect("Thumb centres", rect, VoiceSliderSpec.Inset, 10, 632, VoiceSliderSpec.Thumb);
        var handle = Rect("Thumb", handles, 0, 0, VoiceSliderSpec.Thumb, 0);
        handle.pivot = new Vector2(.5f, .5f); handle.anchoredPosition = Vector2.zero;
        // Slider drives Y anchors to 0..1. A zero height delta preserves the
        // 24 px container height instead of doubling the thumb at runtime.
        var handleImage = DecorativeImage(handle, Color.white);
        var centre = Rect("Thumb centre", handle, 4, 4, 16, 16); DecorativeImage(centre, new Color(.075f, .12f, .14f));
        var slider = rect.gameObject.AddComponent<VoiceSlider>();
        slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = handleImage;
        slider.minValue = spec.Minimum; slider.maxValue = spec.Maximum; slider.Step = spec.Step;
        slider.direction = Slider.Direction.LeftToRight;
        slider.colors = new ColorBlock { normalColor = Gold, highlightedColor = Color.white,
            selectedColor = Color.white, pressedColor = new Color(.57f, .77f, .68f), disabledColor = MutedText,
            colorMultiplier = 1f, fadeDuration = .08f };
        var initial = spec.Clamp(entry.Value); slider.SetValueWithoutNotify(initial); entry.Value = initial;
        slider.onValueChanged.AddListener(value =>
        {
            var safe = spec.Clamp(value); entry.Value = safe;
            slider.SetValueWithoutNotify(safe); valueLabel.text = spec.Format(safe);
        });
        Label(spec.Format(spec.Minimum), 32, y + 68, 160, 16, 12, MutedText);
        var maximum = Label(spec.Format(spec.Maximum), 528, y + 68, 160, 16, 12, MutedText);
        maximum.alignment = TextAlignmentOptions.MidlineRight;
        _controls.Add(slider);
    }

    private void WireNavigation()
    {
        for (var index = 0; index < _controls.Count; index++)
        {
            var control = _controls[index]; var nav = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = _controls[(index + _controls.Count - 1) % _controls.Count], selectOnDown = _controls[(index + 1) % _controls.Count] };
            control.navigation = nav;
        }
    }
    private static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.sizeDelta = new Vector2(width, height); rect.anchoredPosition = new Vector2(x, -y);
        return rect;
    }
    private static void Stretch(RectTransform rect)
    { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
}
