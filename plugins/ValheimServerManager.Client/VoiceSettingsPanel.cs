using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
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
    private Slider _sliderTemplate;
    private Image _panelTemplate;
    private bool _bindingKey, _previousCursorVisible;
    private readonly VoiceUiLifetime _lifetime = new();
    private bool _ownsModal => _lifetime.IsOpen;
    private CursorLockMode _previousCursorLock;
    private string[] _devices = Array.Empty<string>();
    private float _nextDeviceRefresh;
    private int _bindingStartedFrame;
    internal bool IsOpen => _root != null;

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
        _meterFill.sizeDelta = new Vector2(556 * meter, 10);
        _thresholdMarker.anchoredPosition = new Vector2(556 * Mathf.Clamp01(_activationThreshold.Value / .25f), 2);
        var clipping = meter > 0 && _audio?.Level.Clipping == true;
        _meterImage.color = clipping ? new Color(1f, .35f, .22f) : new Color(.5f, .82f, .65f);
        _meterText.text = clipping ? "Clipping · reduce gain" : meter > 0 ? "Input level" : "Input idle / silent";
        _voiceImage.sprite = VoiceIcon.GetSprite(_audio?.WaveCount ?? 0);
        _voiceImage.enabled = _voiceImage.sprite != null;
        _captureStatus.text = _audio?.CaptureStatus ?? "Microphone idle";
        _transportStatus.text = _audio?.TransportStatus ?? "No frames sent";
        _flowCounts.text = _audio == null ? "" : $"Captured {_audio.CapturedFrames} · queued {_audio.SentFrames} · skipped {_audio.SendDrops} · received {_audio.ReceivedFrames}";
        _playbackStatus.text = _audio == null ? "No voice received" : _audio.PlaybackStatus +
            (_audio.OutputSamples > 0 ? " · output samples consumed" : "");
        _enableLabel.text = _enabled.Value ? "Voice chat: Enabled" : "Voice chat: Muted";
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
        _sliderTemplate = menu.m_settingsPrefab.GetComponentInChildren<Slider>(true);
        var settings = menu.m_settingsPrefab.GetComponent<Settings>();
        var panelObject = settings == null ? null : AccessTools.Field(typeof(Settings), "m_settingsPanel")?.GetValue(settings) as GameObject;
        _panelTemplate = panelObject == null ? null : panelObject.GetComponent<Image>();
        if (_panelTemplate == null && panelObject != null) _panelTemplate = panelObject.GetComponentInChildren<Image>(true);
        // All required resources come from the installed game, not shipped copies.
        if (canvas == null || _fontTemplate == null || _sliderTemplate == null || _panelTemplate == null)
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
        CopyImage(_panel.gameObject.AddComponent<Image>(), _panelTemplate);
        Label("Voice chat", 32, 20, 490, 44, 32, new Color(1f, .79f, .35f), heading: true);
        var iconRect = Rect("PNG microphone", _panel, 536, 22, 52, 42);
        _voiceImage = iconRect.gameObject.AddComponent<Image>(); _voiceImage.raycastTarget = false;
        _voiceImage.preserveAspect = true; _voiceImage.color = new Color(1f, .79f, .35f);
        _status = Label("", 32, 66, 556, 40, 16);
        _status.textWrappingMode = TextWrappingModes.Normal;
        _enableLabel = Button("", 32, 114, 556, () => _enabled.Value = !_enabled.Value);
        Label("Voice mode", 32, 166, 180, 40, 20);
        _modeLabel = Button("", 228, 166, 360, () => _mode.Value = (VoiceChatMode)(((int)_mode.Value + 1) % 3));
        _modeHelp = Label("", 32, 210, 556, 28, 16);
        Label("Microphone", 32, 246, 556, 24, 20);
        _deviceLabel = Button("", 32, 274, 556, CycleDevice);
        _deviceHelp = Label("", 32, 320, 556, 24, 15);
        _meterText = Label("", 32, 350, 556, 22, 15);
        var meterBack = Rect("Input meter", _panel, 32, 376, 556, 10);
        var meterBackImage = meterBack.gameObject.AddComponent<Image>();
        meterBackImage.color = new Color(.12f, .16f, .17f); meterBackImage.raycastTarget = false;
        _meterFill = Rect("Level", meterBack, 0, 0, 0, 10);
        _meterImage = _meterFill.gameObject.AddComponent<Image>(); _meterImage.raycastTarget = false;
        _thresholdMarker = Rect("Speech threshold marker", meterBack, 0, -2, 2, 14);
        var thresholdImage = _thresholdMarker.gameObject.AddComponent<Image>();
        thresholdImage.color = new Color(1f, .79f, .35f); thresholdImage.raycastTarget = false;
        _captureStatus = Label("", 32, 388, 556, 40, 15);
        _captureStatus.textWrappingMode = TextWrappingModes.Normal;
        Label("Push to talk key", 32, 432, 180, 40, 20);
        _keyLabel = Button("", 228, 432, 360, () => { _bindingKey = true; _bindingStartedFrame = Time.frameCount; });
        AddSlider("Playback volume", _volume, 0, 2, "0.0", 482);
        AddSlider("Microphone gain", _microphoneGain, 0, 3, "0.0", 542);
        AddSlider("Speech threshold", _activationThreshold, .001f, .2f, "0.000", 602);
        _flowCounts = Label("", 32, 662, 556, 20, 14);
        _transportStatus = Label("", 32, 686, 556, 20, 15);
        _playbackStatus = Label("", 32, 710, 556, 36, 15);
        _playbackStatus.textWrappingMode = TextWrappingModes.Normal;
        Button("Close · F8 / Esc / B", 170, 756, 280, Close);
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
        label.fontSize = size; label.color = color ?? new Color(.95f, .9f, .78f); label.alignment = TextAlignmentOptions.MidlineLeft;
        label.overflowMode = TextOverflowModes.Ellipsis; label.text = text; label.raycastTarget = false;
        return label;
    }

    private TMP_Text Button(string text, float x, float y, float width, Action action)
    {
        var rect = Rect("Button", _panel, x, y, width, 42);
        var image = rect.gameObject.AddComponent<Image>(); CopyImage(image, _buttonTemplate.targetGraphic as Image);
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        button.transition = _buttonTemplate.transition; button.colors = _buttonTemplate.colors; button.spriteState = _buttonTemplate.spriteState;
        // Animation transitions depend on prefab animators; tint remains self-contained.
        if (button.transition == Selectable.Transition.Animation) button.transition = Selectable.Transition.ColorTint;
        button.onClick.AddListener(() => action()); _controls.Add(button);
        var label = Label(text, 12, 0, width - 24, 42, 21, parent: rect); label.alignment = TextAlignmentOptions.Center;
        return label;
    }

    private void AddSlider(string title, ConfigEntry<float> entry, float min, float max, string format, float y)
    {
        var label = Label(title + ": " + entry.Value.ToString(format), 32, y, 556, 24, 20);
        var rect = Rect(title, _panel, 32, y + 28, 556, 22);
        var background = Rect("Background", rect, 0, 8, 556, 6);
        var sourceBackground = _sliderTemplate.transform.Find("Background")?.GetComponent<Image>();
        CopyImage(background.gameObject.AddComponent<Image>(), sourceBackground ?? _sliderTemplate.GetComponentInChildren<Image>(true));
        var fillArea = Rect("Fill area", rect, 10, 8, 536, 6);
        var fill = Rect("Fill", fillArea, 0, 0, 536, 6); Stretch(fill);
        CopyImage(fill.gameObject.AddComponent<Image>(), _sliderTemplate.fillRect?.GetComponent<Image>());
        var handleArea = Rect("Handle area", rect, 10, 0, 536, 22);
        var handle = Rect("Handle", handleArea, 0, 0, 22, 22);
        var handleImage = handle.gameObject.AddComponent<Image>(); CopyImage(handleImage, _sliderTemplate.handleRect?.GetComponent<Image>());
        var slider = rect.gameObject.AddComponent<Slider>(); slider.fillRect = fill; slider.handleRect = handle;
        slider.targetGraphic = handleImage; slider.minValue = min; slider.maxValue = max;
        slider.colors = _sliderTemplate.colors; slider.direction = Slider.Direction.LeftToRight; slider.value = entry.Value;
        slider.onValueChanged.AddListener(value => { entry.Value = value; label.text = title + ": " + value.ToString(format); });
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
    private static void CopyImage(Image target, Image source)
    {
        target.raycastTarget = true;
        if (source == null) { target.color = new Color(.6f, .45f, .22f); return; }
        target.sprite = source.sprite; target.type = source.type; target.color = source.color;
        target.material = source.material; target.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
    }
}
