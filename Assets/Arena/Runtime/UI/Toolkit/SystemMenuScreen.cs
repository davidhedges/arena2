#nullable enable

using UnityEngine;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// System menu (Escape with nothing else open): resume, client settings
    /// (window mode, vsync, quality, master volume), quit. Entirely client-local.
    /// First UI Toolkit screen; the visual source of truth is the prototype at
    /// docs/ui-prototypes/system-menu, translated to SystemMenu.uxml/.uss
    /// (see docs/ui-toolkit-workflow.md). This controller only binds names,
    /// toggles state classes, and applies/persists the settings.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class SystemMenuScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string VolumePrefKey = "arena.settings.masterVolume";
        private const string VsyncPrefKey = "arena.settings.vsync";
        private const string QualityPrefKey = "arena.settings.quality";
        private const string FullscreenPrefKey = "arena.settings.fullscreen";

        private const string OpenClass = "is-open";
        private const string SelectedClass = "is-selected";
        private const long WindowTransitionMs = 130;

        private static SystemMenuScreen? s_instance;

        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private VisualElement? _window;
        private Button? _windowModeButton;
        private Button? _vsyncButton;
        private Button? _qualityButton;
        private VisualElement? _qualityDropdown;
        private VisualElement? _qualityMenu;
        private VisualElement? _volumeSlider;
        private VisualElement? _volumeFill;
        private Label? _volumeValue;
        private bool _open;
        private bool _draggingVolume;

        public int EscapeClosePriority => 120;
        public bool IsEscapeCloseable => _open;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ApplySavedSettings();

            if (FindAnyObjectByType<SystemMenuScreen>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject go = new("SystemMenuScreen");
            DontDestroyOnLoad(go);
            go.AddComponent<SystemMenuScreen>();
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
            if (_panelSettings != null)
                Destroy(_panelSettings);
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
            if (_root == null || _window == null)
                return;

            _open = open;
            CloseQualityMenu();
            if (open)
            {
                if (_panelSettings != null)
                    _panelSettings.sortingOrder = RuntimeUiLayer.NextSortingOrder();
                RefreshSettingRows();

                // Veil shows instantly; the window class lands a frame later so
                // the 120ms fade/scale transition plays from its closed state.
                _root.AddToClassList(OpenClass);
                if (instant)
                    _window.AddToClassList(OpenClass);
                else
                    _window.schedule.Execute(() =>
                    {
                        if (_open)
                            _window.AddToClassList(OpenClass);
                    }).ExecuteLater(16);
            }
            else
            {
                _window.RemoveFromClassList(OpenClass);
                if (instant)
                    _root.RemoveFromClassList(OpenClass);
                else
                    _root.schedule.Execute(() =>
                    {
                        if (!_open)
                            _root.RemoveFromClassList(OpenClass);
                    }).ExecuteLater(WindowTransitionMs);
            }
        }

        private void BuildUi()
        {
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/SystemMenu", 60f);
            _panelSettings = document.panelSettings;

            VisualElement tree = document.rootVisualElement;
            _root = tree.Q<VisualElement>("SystemMenu");
            if (_root == null)
            {
                Debug.LogError("SystemMenuScreen: SystemMenu.uxml did not load or is missing its root element.");
                return;
            }

            _window = _root.Q<VisualElement>("Window");
            _windowModeButton = _root.Q<Button>("WindowModeButton");
            _vsyncButton = _root.Q<Button>("VsyncButton");
            _qualityButton = _root.Q<Button>("QualityButton");
            _qualityDropdown = _root.Q<VisualElement>("QualityDropdown");
            _qualityMenu = _root.Q<VisualElement>("QualityMenu");
            _volumeSlider = _root.Q<VisualElement>("VolumeSlider");
            _volumeFill = _root.Q<VisualElement>("VolumeFill");
            _volumeValue = _root.Q<Label>("VolumeValue");

            _root.Q<Button>("ResumeButton")!.clicked += () => SetOpen(false);
            _root.Q<Button>("QuitButton")!.clicked += QuitGame;
            _windowModeButton!.clicked += CycleWindowMode;
            _vsyncButton!.clicked += ToggleVsync;
            _qualityButton!.clicked += ToggleQualityMenu;

            BuildQualityItems();
            BindVolumeSlider();

            // Any press outside the dropdown collapses it.
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target is VisualElement element &&
                    _qualityDropdown != null &&
                    element.FindCommonAncestor(_qualityDropdown) != _qualityDropdown)
                    CloseQualityMenu();
            }, TrickleDown.TrickleDown);
        }

        private void BuildQualityItems()
        {
            if (_qualityMenu == null)
                return;

            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                int level = i;
                Label item = new(names[i]);
                item.AddToClassList("dropdown-item");
                item.RegisterCallback<ClickEvent>(_ =>
                {
                    SelectQuality(level);
                    CloseQualityMenu();
                });
                _qualityMenu.Add(item);
            }
        }

        private void BindVolumeSlider()
        {
            if (_volumeSlider == null)
                return;

            _volumeSlider.RegisterCallback<PointerDownEvent>(evt =>
            {
                _draggingVolume = true;
                _volumeSlider.CapturePointer(evt.pointerId);
                SetVolumeFromPointer(evt.position);
            });
            _volumeSlider.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_draggingVolume)
                    SetVolumeFromPointer(evt.position);
            });
            _volumeSlider.RegisterCallback<PointerUpEvent>(evt =>
            {
                _draggingVolume = false;
                _volumeSlider.ReleasePointer(evt.pointerId);
            });
        }

        private void SetVolumeFromPointer(Vector3 pointerPosition)
        {
            if (_volumeSlider == null)
                return;

            float width = _volumeSlider.resolvedStyle.width;
            if (width <= 0f)
                return;

            float t = _volumeSlider.WorldToLocal(pointerPosition).x / width;
            OnVolumeChanged(Mathf.Clamp01(t));
        }

        private void ToggleQualityMenu()
        {
            if (_qualityDropdown == null)
                return;

            if (_qualityDropdown.ClassListContains(OpenClass))
                CloseQualityMenu();
            else
                _qualityDropdown.AddToClassList(OpenClass);
        }

        private void CloseQualityMenu()
        {
            _qualityDropdown?.RemoveFromClassList(OpenClass);
        }

        private void RefreshSettingRows()
        {
            if (_windowModeButton != null)
                _windowModeButton.text = Screen.fullScreen ? "FULLSCREEN" : "WINDOWED";
            if (_vsyncButton != null)
                _vsyncButton.text = QualitySettings.vSyncCount > 0 ? "ON" : "OFF";

            string[] names = QualitySettings.names;
            int level = QualitySettings.GetQualityLevel();
            if (_qualityButton != null)
            {
                string label = level >= 0 && level < names.Length ? names[level].ToUpperInvariant() : level.ToString();
                _qualityButton.text = $"{label}  ▾";
            }
            if (_qualityMenu != null)
                for (int i = 0; i < _qualityMenu.childCount; i++)
                    _qualityMenu[i].EnableInClassList(SelectedClass, i == level);

            RefreshVolume();
        }

        private void RefreshVolume()
        {
            int percent = Mathf.RoundToInt(AudioListener.volume * 100f);
            if (_volumeFill != null)
                _volumeFill.style.width = Length.Percent(percent);
            if (_volumeValue != null)
                _volumeValue.text = $"{percent}%";
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

        private void SelectQuality(int level)
        {
            if (level < 0 || level >= QualitySettings.names.Length)
                return;

            QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
            PlayerPrefs.SetInt(QualityPrefKey, level);
            PlayerPrefs.Save();
            RefreshSettingRows();
        }

        private void OnVolumeChanged(float value)
        {
            AudioListener.volume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(VolumePrefKey, AudioListener.volume);
            PlayerPrefs.Save();
            RefreshVolume();
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
