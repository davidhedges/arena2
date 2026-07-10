#nullable enable

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// System menu (Escape with nothing else open): resume, client settings
    /// (window mode, vsync, quality, master volume), quit. Entirely client-local.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class SystemMenuPanel : MonoBehaviour, IEscapeCloseable
    {
        private const string VolumePrefKey = "arena.settings.masterVolume";
        private const string VsyncPrefKey = "arena.settings.vsync";
        private const string QualityPrefKey = "arena.settings.quality";
        private const string FullscreenPrefKey = "arena.settings.fullscreen";

        private static SystemMenuPanel? s_instance;

        private Canvas? _canvas;
        private RectTransform? _veil;
        private ArenaWindow? _window;
        private ArenaButtonHandle _windowModeButton;
        private ArenaButtonHandle _vsyncButton;
        private ArenaButtonHandle _qualityButton;
        private Slider? _volumeSlider;
        private TextMeshProUGUI? _volumeValue;
        private bool _open;

        public int EscapeClosePriority => 120;
        public bool IsEscapeCloseable => _open;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ApplySavedSettings();

            if (FindAnyObjectByType<SystemMenuPanel>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject go = new("SystemMenuPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<SystemMenuPanel>();
        }

        /// <summary>Applies persisted client settings on startup, before any UI exists.</summary>
        private static void ApplySavedSettings()
        {
            AudioListener.volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumePrefKey, 1f));
            if (PlayerPrefs.HasKey(VsyncPrefKey))
                QualitySettings.vSyncCount = PlayerPrefs.GetInt(VsyncPrefKey, 1) != 0 ? 1 : 0;
            if (PlayerPrefs.HasKey(QualityPrefKey))
            {
                int quality = PlayerPrefs.GetInt(QualityPrefKey, QualitySettings.GetQualityLevel());
                if (quality >= 0 && quality < QualitySettings.names.Length)
                    QualitySettings.SetQualityLevel(quality, applyExpensiveChanges: false);
            }
            if (PlayerPrefs.HasKey(FullscreenPrefKey))
                Screen.fullScreen = PlayerPrefs.GetInt(FullscreenPrefKey, Screen.fullScreen ? 1 : 0) != 0;
        }

        /// <summary>Opens the menu from the Escape fallback path.</summary>
        public static void OpenFromEscape()
        {
            if (s_instance == null || s_instance._open)
                return;

            s_instance.SetOpen(true);
        }

        private void Awake()
        {
            s_instance = this;
            RuntimeUiEventSystem.Ensure();
            BuildUi();
        }

        private void OnEnable()
        {
            RuntimeUiEscapeRouter.Register(this);
        }

        private void OnDisable()
        {
            RuntimeUiEscapeRouter.Unregister(this);
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        private void Update()
        {
            if (_open && !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                SetOpen(false, instant: true);
        }

        public bool TryCloseForEscape()
        {
            if (!_open)
                return false;

            SetOpen(false);
            return true;
        }

        private void SetOpen(bool open, bool instant = false)
        {
            _open = open;
            if (_veil != null)
                _veil.gameObject.SetActive(open);
            _window?.SetVisible(open, instant);
            if (open)
            {
                RuntimeUiLayer.BringToFront(_canvas);
                RefreshSettingRows();
            }
        }

        private void BuildUi()
        {
            GameObject canvasGo = new("SystemMenuCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = ArenaUiKit.MakeOverlayCanvas(canvasGo, 60);

            // Modal scrim: dims the game and blocks clicks while the menu is open.
            _veil = ArenaUiKit.MakePanel(canvasGo.transform, "Veil", ArenaUiTheme.Veil);
            ArenaUiKit.Fill(_veil);
            _veil.gameObject.SetActive(false);

            _window = ArenaWindow.Create(canvasGo.transform, "SystemMenuWindow", "System Menu", new Vector2(380f, 432f));
            _window.CloseRequested += () => SetOpen(false);
            _window.SetSubtitle("Esc");

            RectTransform content = _window.Content;
            float y = 0f;

            ArenaButtonHandle resume = ArenaUiKit.MakeButton(
                content, "ResumeButton", "RESUME", ArenaButtonStyle.Primary, () => SetOpen(false));
            PlaceRow(resume.Rect, ref y, ArenaUiTheme.ButtonHeight + 6f);

            TextMeshProUGUI settingsHeading = ArenaUiKit.MakeSectionLabel(content, "SettingsHeading", "Settings");
            PlaceRow(settingsHeading.rectTransform, ref y, 26f, topPad: 10f);
            RectTransform divider = ArenaUiKit.MakeDivider(content);
            PlaceRow(divider, ref y, 1f);

            _windowModeButton = MakeSettingRow(content, "WindowMode", "Window Mode", ref y, CycleWindowMode);
            _vsyncButton = MakeSettingRow(content, "Vsync", "VSync", ref y, ToggleVsync);
            _qualityButton = MakeSettingRow(content, "Quality", "Quality", ref y, CycleQuality);
            BuildVolumeRow(content, ref y);

            ArenaButtonHandle quit = ArenaUiKit.MakeButton(
                content, "QuitButton", "QUIT GAME", ArenaButtonStyle.Danger, QuitGame);
            PlaceRow(quit.Rect, ref y, ArenaUiTheme.ButtonHeight, topPad: 16f);

            _window.SetVisible(false, instant: true);
        }

        private ArenaButtonHandle MakeSettingRow(
            RectTransform content,
            string name,
            string label,
            ref float y,
            UnityEngine.Events.UnityAction onClick)
        {
            RectTransform row = ArenaUiKit.MakeRect(content, $"{name}Row");
            PlaceRow(row, ref y, ArenaUiTheme.ButtonHeight, topPad: 8f);

            TextMeshProUGUI title = ArenaUiKit.MakeText(row, "Label", label, ArenaUiTheme.BodySize, ArenaUiTheme.MutedText);
            ArenaUiKit.SetAnchors(title.rectTransform, new Vector2(0f, 0f), new Vector2(0.45f, 1f), Vector2.zero, Vector2.zero);

            ArenaButtonHandle value = ArenaUiKit.MakeButton(row, "Value", "-", ArenaButtonStyle.Secondary, onClick);
            ArenaUiKit.SetAnchors(value.Rect, new Vector2(0.45f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            return value;
        }

        private void BuildVolumeRow(RectTransform content, ref float y)
        {
            RectTransform row = ArenaUiKit.MakeRect(content, "VolumeRow");
            PlaceRow(row, ref y, ArenaUiTheme.ButtonHeight, topPad: 8f);

            TextMeshProUGUI title = ArenaUiKit.MakeText(row, "Label", "Master Volume", ArenaUiTheme.BodySize, ArenaUiTheme.MutedText);
            ArenaUiKit.SetAnchors(title.rectTransform, new Vector2(0f, 0f), new Vector2(0.45f, 1f), Vector2.zero, Vector2.zero);

            _volumeValue = ArenaUiKit.MakeText(
                row, "Value", "100%", ArenaUiTheme.SmallSize, ArenaUiTheme.Text, ArenaUiTheme.StrongFont, TextAlignmentOptions.MidlineRight);
            ArenaUiKit.SetAnchors(_volumeValue.rectTransform, new Vector2(0.86f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            RectTransform sliderRect = ArenaUiKit.MakeRect(row, "Slider");
            ArenaUiKit.SetAnchors(sliderRect, new Vector2(0.45f, 0.5f), new Vector2(0.84f, 0.5f), new Vector2(0f, -4f), new Vector2(0f, 4f));

            RectTransform background = ArenaUiKit.MakePanel(sliderRect, "Background", ArenaUiTheme.CellEmpty, cornerRadius: ArenaUiSprites.SmallRadius);
            ArenaUiKit.Fill(background);

            RectTransform fillArea = ArenaUiKit.MakeRect(sliderRect, "FillArea");
            ArenaUiKit.Fill(fillArea);
            RectTransform fill = ArenaUiKit.MakePanel(fillArea, "Fill", ArenaUiTheme.Accent, raycastTarget: false, cornerRadius: ArenaUiSprites.SmallRadius);
            ArenaUiKit.Fill(fill);

            Slider slider = sliderRect.gameObject.AddComponent<Slider>();
            slider.targetGraphic = background.GetComponent<Image>();
            slider.fillRect = fill;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = AudioListener.volume;
            slider.onValueChanged.AddListener(OnVolumeChanged);
            _volumeSlider = slider;
        }

        private static void PlaceRow(RectTransform rect, ref float y, float height, float topPad = 0f)
        {
            y += topPad;
            ArenaUiKit.SetAnchors(
                rect,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -(y + height)),
                new Vector2(0f, -y));
            y += height;
        }

        private void RefreshSettingRows()
        {
            _windowModeButton.SetLabel(Screen.fullScreen ? "FULLSCREEN" : "WINDOWED");
            _vsyncButton.SetLabel(QualitySettings.vSyncCount > 0 ? "ON" : "OFF");

            string[] names = QualitySettings.names;
            int level = QualitySettings.GetQualityLevel();
            _qualityButton.SetLabel(level >= 0 && level < names.Length ? names[level].ToUpperInvariant() : level.ToString());

            if (_volumeSlider != null)
                _volumeSlider.SetValueWithoutNotify(AudioListener.volume);
            if (_volumeValue != null)
                _volumeValue.text = $"{Mathf.RoundToInt(AudioListener.volume * 100f)}%";
        }

        private void CycleWindowMode()
        {
            bool fullscreen = !Screen.fullScreen;
            Screen.fullScreen = fullscreen;
            PlayerPrefs.SetInt(FullscreenPrefKey, fullscreen ? 1 : 0);
            PlayerPrefs.Save();
            RefreshSettingRows();
        }

        private void ToggleVsync()
        {
            QualitySettings.vSyncCount = QualitySettings.vSyncCount > 0 ? 0 : 1;
            PlayerPrefs.SetInt(VsyncPrefKey, QualitySettings.vSyncCount);
            PlayerPrefs.Save();
            RefreshSettingRows();
        }

        private void CycleQuality()
        {
            int count = QualitySettings.names.Length;
            if (count == 0)
                return;

            int next = (QualitySettings.GetQualityLevel() + 1) % count;
            QualitySettings.SetQualityLevel(next, applyExpensiveChanges: true);
            PlayerPrefs.SetInt(QualityPrefKey, next);
            PlayerPrefs.Save();
            RefreshSettingRows();
        }

        private void OnVolumeChanged(float value)
        {
            AudioListener.volume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(VolumePrefKey, AudioListener.volume);
            PlayerPrefs.Save();
            if (_volumeValue != null)
                _volumeValue.text = $"{Mathf.RoundToInt(AudioListener.volume * 100f)}%";
        }

        private static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
