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
    private Button _buttonTemplate;
    private Slider _sliderTemplate;
    private Image _panelTemplate;
    private bool _bindingKey, _previousCursorVisible, _ownsModal;
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
        _status.text = policyReceived ? serverEnabled ? "Proximity voice · F8 / Esc / B to close" : "Voice is disabled by this server" : "Waiting for the server voice policy";
        _enableLabel.text = _enabled.Value ? "Voice chat: Enabled" : "Voice chat: Muted";
        _modeLabel.text = _mode.Value switch { VoiceChatMode.PushToTalk => "Push to talk  ›", VoiceChatMode.VoiceActivation => "Voice activation  ›", _ => "Open microphone  ›" };
        _modeHelp.text = _mode.Value switch { VoiceChatMode.PushToTalk => "Microphone opens only while your key is held.", VoiceChatMode.VoiceActivation => "Microphone stays open; sends speech above the threshold.", _ => "Microphone stays open and transmits during play." };
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
        _previousCursorLock = Cursor.lockState; _previousCursorVisible = Cursor.visible;
        _previousSelection = EventSystem.current?.currentSelectedGameObject;
        _canvasRect = canvas.GetComponent<RectTransform>();
        _root = new GameObject("VSM Voice Settings", typeof(RectTransform));
        _root.SetActive(false);
        _root.transform.SetParent(canvas.transform, false);
        Stretch(_root.GetComponent<RectTransform>());
        var overlayCanvas = _root.AddComponent<Canvas>(); overlayCanvas.overrideSorting = true; overlayCanvas.sortingOrder = 30000;
        _root.AddComponent<GraphicRaycaster>();
        _root.AddComponent<CanvasGroup>();
        var veil = _root.AddComponent<Image>(); veil.color = new Color(0, 0, 0, .65f);
        _panel = Rect("Panel", _root.transform, 0, 0, 620, 680);
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(.5f, .5f); _panel.anchoredPosition = Vector2.zero;
        CopyImage(_panel.gameObject.AddComponent<Image>(), _panelTemplate);
        Label("Voice chat", 32, 24, 556, 40, 32, new Color(1f, .79f, .35f));
        _status = Label("", 32, 72, 556, 26, 18);
        _enableLabel = Button("", 32, 110, 556, () => _enabled.Value = !_enabled.Value);
        Label("Voice mode", 32, 170, 180, 40, 22);
        _modeLabel = Button("", 228, 170, 360, () => _mode.Value = (VoiceChatMode)(((int)_mode.Value + 1) % 3));
        _modeHelp = Label("", 32, 216, 556, 30, 17);
        Label("Microphone", 32, 252, 556, 24, 22);
        _deviceLabel = Button("", 32, 280, 556, CycleDevice);
        _deviceHelp = Label("", 32, 326, 556, 24, 16);
        Label("Push to talk key", 32, 360, 180, 40, 22);
        _keyLabel = Button("", 228, 360, 360, () => { _bindingKey = true; _bindingStartedFrame = Time.frameCount; });
        AddSlider("Playback volume", _volume, 0, 2, "0.0", 420);
        AddSlider("Microphone gain", _microphoneGain, 0, 3, "0.0", 482);
        AddSlider("Activation threshold", _activationThreshold, .001f, .2f, "0.000", 544);
        Button("Close", 200, 618, 220, Close);
        WireNavigation();
        var group = _root.AddComponent<UIGroupHandler>(); group.m_groupPriority = 1000; group.m_defaultElement = _controls[0].gameObject;
        _nextDeviceRefresh = 0;
        _ownsModal = true;
        NoticeInputGuard.NativeModal = true;
        _root.SetActive(true);
        EventSystem.current?.SetSelectedGameObject(_controls[0].gameObject);
    }

    private void CycleDevice()
    {
        var index = Array.IndexOf(_devices, _inputDevice.Value);
        _inputDevice.Value = string.IsNullOrEmpty(_inputDevice.Value) ? (_devices.Length > 0 ? _devices[0] : "")
            : index >= 0 && index + 1 < _devices.Length ? _devices[index + 1] : "";
    }

    internal void Close()
    {
        if (!_ownsModal && _root == null) return;
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null; _ownsModal = false;
        _controls.Clear(); _bindingKey = false;
        NoticeInputGuard.NativeModal = false;
        Cursor.lockState = _previousCursorLock; Cursor.visible = _previousCursorVisible;
        EventSystem.current?.SetSelectedGameObject(_previousSelection != null && _previousSelection.activeInHierarchy ? _previousSelection : null);
        _previousSelection = null;
        PlayerController.SetTakeInputDelay(.1f);
        _config.Save();
    }

    private TMP_Text Label(string text, float x, float y, float width, float height, float size, Color? color = null, Transform parent = null)
    {
        var rect = Rect("Label", parent ?? _panel, x, y, width, height);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = _fontTemplate.font; label.fontSharedMaterial = _fontTemplate.fontSharedMaterial;
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
