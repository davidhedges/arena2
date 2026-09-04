#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Sidekick window for stamping animation events at the current playhead time
    /// with a single click. Uses CombatClipRoleInferer to know which clip you're
    /// editing and which event names are appropriate; surfaces those names as
    /// labeled buttons. Clicking a button writes an AnimationEvent at the current
    /// time on the selected clip.
    ///
    /// Usage: open Animation window, scrub to the desired frame, click the
    /// matching button (e.g. "OnReleaseFrame") in this window. Done.
    /// </summary>
    public sealed class AnimationEventStamperWindow : EditorWindow
    {
        [MenuItem("Arena/Animation/Event Stamper")]
        public static void Open()
        {
            AnimationEventStamperWindow window = GetWindow<AnimationEventStamperWindow>("Event Stamper");
            window.titleContent = new GUIContent(
                "Event Stamper",
                "Stamp combat animation events at the current animation playhead.");
            window.minSize = new Vector2(300, 300);
            window.maxSize = new Vector2(4096, 4096);
            window.Show();
        }

        // Reflection handles for Unity's internal AnimationWindow state. AnimationWindow
        // is internal, so we read the playhead/clip via reflection. Fields are best-effort:
        // if any reflection step fails, the window falls back to a manual slider + clip
        // picker so authoring still works without sync.
        private Type? _animWindowType;
        private FieldInfo? _stateField;
        private PropertyInfo? _stateCurrentTimeProp;
        private PropertyInfo? _stateActiveClipProp;

        private AnimationClip? _clip;
        private float _time;
        private bool _syncedWithAnimationWindow;
        private string _customEventName = string.Empty;
        private CombatClipRole _manualRoleOverride = CombatClipRole.Unknown;
        private bool _useManualRoleOverride;
        private AnimationClip? _previousClipForRoleSync;
        private AnimationClip? _startupTrimTargetsClip;
        private List<CombatAnimationSetEditor.StartupTrimTarget> _startupTrimTargets = new();
        private string _hitWindowSyncStatus = string.Empty;
        private bool _hitWindowSyncSucceeded;

        private UnityEditor.Editor? _embeddedClipEditor;
        private bool _embeddedPreviewSyncs;
        private const float EmbeddedPreviewHeight = 220f;
        private const int IdealInputToServerLatencyMinMs = 20;
        private const int IdealInputToServerLatencyMaxMs = 40;
        private const int ServerCombatTickMaxDelayMs = 33;
        private const float PreviewScrollSpeed = 20f;

        private DefaultAsset? _folder;
        private AnimationClip[]? _folderClips;
        private Vector2 _folderListScroll;
        private float _folderListResizeStartMouseY;
        private float _folderListResizeStartHeight;
        private const float FolderListMinHeight = 120f;
        private const float FolderListDefaultHeight = 320f;
        private const float FolderListMaxHeight = 1600f;
        private const float FolderListResizeHandleHeight = 16f;
        private const int FolderListResizeControlHint = 0x464C5253;

        private Dictionary<AnimationClip, List<CombatClipRoleObservation>>? _roleMap;
        private Vector2 _mainScroll;

        [SerializeField] private bool _showFolderBrowser;
        [SerializeField] private float _folderListHeight = FolderListDefaultHeight;
        [SerializeField] private bool _showPreview;
        [SerializeField] private bool _showCustomEvent;
        [SerializeField] private bool _showExistingEvents = true;

        private void OnEnable()
        {
            // Reset any restrictive dimensions retained by an older saved layout.
            minSize = new Vector2(300, 300);
            maxSize = new Vector2(4096, 4096);
            if (float.IsNaN(_folderListHeight)
                || float.IsInfinity(_folderListHeight)
                || _folderListHeight < FolderListMinHeight)
            {
                _folderListHeight = FolderListDefaultHeight;
            }
            else
            {
                _folderListHeight = Mathf.Min(_folderListHeight, FolderListMaxHeight);
            }
            CacheAnimationWindowReflection();
            RefreshRoleMap();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            DestroyEmbeddedClipEditor();
        }

        private void DestroyEmbeddedClipEditor()
        {
            if (_embeddedClipEditor != null)
            {
                UnityEngine.Object.DestroyImmediate(_embeddedClipEditor);
                _embeddedClipEditor = null;
            }
        }

        private void EnsureEmbeddedClipEditor()
        {
            if (_clip == null)
            {
                DestroyEmbeddedClipEditor();
                return;
            }
            if (_embeddedClipEditor == null || _embeddedClipEditor.target != _clip)
            {
                DestroyEmbeddedClipEditor();
                _embeddedClipEditor = UnityEditor.Editor.CreateEditor(_clip);
            }
        }

        /// <summary>
        /// Reflection-only read of the AnimationClipEditor's embedded preview playhead.
        /// AnimationClipEditor → m_AvatarPreview → timeControl → currentTime.
        /// All three layers are internal but stable across recent Unity versions. Falls
        /// back to the manual slider time if any step fails.
        /// </summary>
        private bool TryReadEmbeddedPreviewTime(out float time)
        {
            time = 0f;
            if (_embeddedClipEditor == null)
                return false;
            try
            {
                FieldInfo? avatarField = _embeddedClipEditor.GetType().GetField(
                    "m_AvatarPreview", BindingFlags.Instance | BindingFlags.NonPublic);
                object? avatar = avatarField?.GetValue(_embeddedClipEditor);
                if (avatar == null)
                    return false;

                FieldInfo? timeControlField = avatar.GetType().GetField(
                    "timeControl", BindingFlags.Instance | BindingFlags.Public)
                    ?? avatar.GetType().GetField(
                        "timeControl", BindingFlags.Instance | BindingFlags.NonPublic);
                object? timeControl = timeControlField?.GetValue(avatar);
                if (timeControl == null)
                    return false;

                FieldInfo? currentTimeField = timeControl.GetType().GetField(
                    "currentTime", BindingFlags.Instance | BindingFlags.Public);
                if (currentTimeField == null)
                    return false;
                object? raw = currentTimeField.GetValue(timeControl);
                time = raw switch
                {
                    float f => f,
                    double d => (float)d,
                    _ => 0f,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void CacheAnimationWindowReflection()
        {
            try
            {
                _animWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnimationWindow");
                if (_animWindowType == null)
                    return;

                _stateField = _animWindowType.GetField("m_State", BindingFlags.Instance | BindingFlags.NonPublic);
                Type? stateType = _stateField?.FieldType;
                if (stateType == null)
                    return;

                _stateCurrentTimeProp = stateType.GetProperty("currentTime", BindingFlags.Instance | BindingFlags.Public);
                _stateActiveClipProp = stateType.GetProperty("activeAnimationClip", BindingFlags.Instance | BindingFlags.Public);
            }
            catch
            {
                _animWindowType = null;
            }
        }

        private void RefreshRoleMap()
        {
            _roleMap = CombatClipRoleInferer.BuildClipRoleMap();
            _startupTrimTargetsClip = null;
        }

        private void OnEditorUpdate()
        {
            // When the user has scoped to a folder, the folder list owns clip selection
            // and the embedded preview owns time. Animation-window sync would fight both,
            // so it is suppressed in folder mode.
            if (_folder != null)
            {
                if (_syncedWithAnimationWindow)
                {
                    _syncedWithAnimationWindow = false;
                    Repaint();
                }
                return;
            }

            bool synced = TryReadAnimationWindow(out AnimationClip? clip, out float time);
            if (synced != _syncedWithAnimationWindow)
            {
                _syncedWithAnimationWindow = synced;
                Repaint();
            }
            if (!synced)
                return;

            bool changed = false;
            if (clip != null && !ReferenceEquals(clip, _clip))
            {
                _clip = clip;
                changed = true;
            }
            if (!Mathf.Approximately(time, _time))
            {
                _time = time;
                changed = true;
            }
            if (changed)
                Repaint();
        }

        private bool TryReadAnimationWindow(out AnimationClip? clip, out float time)
        {
            clip = null;
            time = 0f;
            if (_animWindowType == null || _stateField == null
                || _stateCurrentTimeProp == null || _stateActiveClipProp == null)
                return false;

            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(_animWindowType);
            if (windows.Length == 0)
                return false;

            try
            {
                EditorWindow animWindow = (EditorWindow)windows[0];
                object? state = _stateField.GetValue(animWindow);
                if (state == null)
                    return false;

                clip = _stateActiveClipProp.GetValue(state) as AnimationClip;
                object? rawTime = _stateCurrentTimeProp.GetValue(state);
                if (rawTime is float f)
                    time = f;
                else if (rawTime is double d)
                    time = (float)d;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private CombatClipRole InferRole(AnimationClip? clip)
        {
            return InferRoleWithSource(clip, out _);
        }

        private CombatClipRole InferRoleWithSource(AnimationClip? clip, out CombatClipRoleSource source)
        {
            source = CombatClipRoleSource.Unknown;
            if (clip == null)
                return CombatClipRole.Unknown;

            string assetPath = AssetDatabase.GetAssetPath(clip);
            CombatClipRole nameRole = CombatClipRoleNameInference.TryInferFromPath(assetPath);

            if (_roleMap != null
                && _roleMap.TryGetValue(clip, out List<CombatClipRoleObservation>? obs)
                && obs.Count > 0)
            {
                source = CombatClipRoleSource.Reference;
                if (nameRole != CombatClipRole.Unknown && obs.Any(o => o.Role == nameRole))
                    return nameRole;

                return obs.GroupBy(o => o.Role).OrderByDescending(g => g.Count()).First().Key;
            }

            if (nameRole != CombatClipRole.Unknown)
            {
                source = CombatClipRoleSource.Name;
                return nameRole;
            }

            return CombatClipRole.Unknown;
        }

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(2f);

            using (EditorGUILayout.ScrollViewScope scroll = new(_mainScroll))
            {
                _mainScroll = scroll.scrollPosition;
                DrawFolderContext();
                EditorGUILayout.Space(4f);

                if (_clip == null)
                {
                    EditorGUILayout.HelpBox(
                        _folder != null
                            ? "Pick a clip from the folder list above."
                            : _syncedWithAnimationWindow
                                ? "Select a clip in the Animation window."
                                : "Drop a clip into the Clip field, set a folder context above, or open the Animation window with a clip selected to enable sync.",
                        MessageType.Info);
                    return;
                }

                // When the active clip changes, reset the override dropdown to the new clip's
                // inferred role so the dropdown never shows the previous clip's value. The
                // override checkbox itself stays sticky — useful for batch authoring where
                // the inferred role is consistently Unknown and the user picks the right one.
                if (!ReferenceEquals(_clip, _previousClipForRoleSync))
                {
                    _previousClipForRoleSync = _clip;
                    _manualRoleOverride = InferRole(_clip);
                }

                _showPreview = EditorGUILayout.Foldout(
                    _showPreview,
                    "Clip preview (load on demand)",
                    true,
                    EditorStyles.foldoutHeader);
                if (_showPreview)
                {
                    EnsureEmbeddedClipEditor();
                    DrawEmbeddedPreview();
                    EditorGUILayout.Space(4f);
                }
                else
                {
                    _embeddedPreviewSyncs = false;
                    DestroyEmbeddedClipEditor();
                }

                DrawTimeAndRole();
                EditorGUILayout.Space(8f);
                DrawStampButtons();
                EditorGUILayout.Space(8f);
                DrawCustomEventStamp();
                DrawDodgeTiming();
                DrawCastReleaseEntry();
                DrawInstantCastStartupTrim();
                DrawHitWindowSynchronization();
                DrawExistingEvents();
                EditorGUILayout.Space(8f);
            }
        }

        private void DrawFolderContext()
        {
            string folderSummary = _folder == null
                ? "Folder browser"
                : $"Folder browser ({_folderClips?.Length ?? 0} clips)";
            _showFolderBrowser = EditorGUILayout.Foldout(
                _showFolderBrowser,
                folderSummary,
                true,
                EditorStyles.foldoutHeader);
            if (!_showFolderBrowser)
                return;

            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _folder = (DefaultAsset?)EditorGUILayout.ObjectField(
                    "Folder context", _folder, typeof(DefaultAsset), false);
                if (EditorGUI.EndChangeCheck())
                    RefreshFolderClips();
                using (new EditorGUI.DisabledScope(_folder == null))
                {
                    if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                        RefreshFolderClips();
                }
            }

            if (_folder == null)
                return;

            string folderPath = AssetDatabase.GetAssetPath(_folder);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                EditorGUILayout.HelpBox(
                    "Folder context must be a folder asset (drag a folder from the Project window).",
                    MessageType.Warning);
                return;
            }

            int total = _folderClips?.Length ?? 0;
            EditorGUILayout.LabelField($"Clips in folder (recursive): {total}", EditorStyles.miniLabel);
            if (_folderClips == null || _folderClips.Length == 0)
                return;

            using (EditorGUILayout.ScrollViewScope scroll = new(
                _folderListScroll,
                GUILayout.Height(_folderListHeight)))
            {
                _folderListScroll = scroll.scrollPosition;
                foreach (AnimationClip clip in _folderClips)
                {
                    DrawFolderClipRow(clip);
                }
            }

            DrawFolderListResizeHandle();
        }

        private void DrawFolderListResizeHandle()
        {
            Rect handleRect = GUILayoutUtility.GetRect(
                1f,
                FolderListResizeHandleHeight,
                GUILayout.ExpandWidth(true));
            int controlId = GUIUtility.GetControlID(
                FolderListResizeControlHint,
                FocusType.Passive,
                handleRect);

            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeVertical);
            GUI.Label(
                handleRect,
                new GUIContent(
                    "Drag to resize",
                    "Drag vertically to resize the clip list. Double-click to reset its height."),
                EditorStyles.centeredGreyMiniLabel);

            Event current = Event.current;
            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (current.button != 0 || !handleRect.Contains(current.mousePosition))
                        break;

                    if (current.clickCount == 2)
                    {
                        _folderListHeight = FolderListDefaultHeight;
                        GUI.changed = true;
                        Repaint();
                    }
                    else
                    {
                        GUIUtility.hotControl = controlId;
                        _folderListResizeStartMouseY = current.mousePosition.y;
                        _folderListResizeStartHeight = _folderListHeight;
                    }
                    current.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != controlId)
                        break;

                    float mouseDelta = current.mousePosition.y - _folderListResizeStartMouseY;
                    _folderListHeight = Mathf.Clamp(
                        _folderListResizeStartHeight + mouseDelta,
                        FolderListMinHeight,
                        FolderListMaxHeight);
                    GUI.changed = true;
                    current.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != controlId)
                        break;

                    GUIUtility.hotControl = 0;
                    current.Use();
                    break;
            }
        }

        private void DrawFolderClipRow(AnimationClip clip)
        {
            CombatClipRole role = InferRoleWithSource(clip, out CombatClipRoleSource source);
            (int present, int required) = CountRequiredEventsAuthored(clip, role);
            bool isSelected = ReferenceEquals(clip, _clip);

            Color rowColor = ResolveCompletionColor(present, required);
            Color prevBg = GUI.backgroundColor;
            if (isSelected)
                GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);

            bool narrow = position.width < 520f;
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(clip.name, EditorStyles.miniButton, GUILayout.ExpandWidth(true)))
                    {
                        _clip = clip;
                        _time = 0f;
                        Selection.activeObject = clip;
                        GUI.FocusControl(null);
                    }

                    Color prevFg = GUI.color;
                    GUI.color = rowColor;
                    string completion = required > 0 ? $"{present}/{required}" : "—";
                    GUILayout.Label(completion, GUILayout.Width(44));
                    GUI.color = prevFg;

                    if (!narrow)
                        GUILayout.Label(BuildRoleLabel(role, source), EditorStyles.miniLabel, GUILayout.Width(150));
                }

                if (narrow)
                    GUILayout.Label(BuildRoleLabel(role, source), EditorStyles.miniLabel);
            }

            GUI.backgroundColor = prevBg;
        }

        private static string BuildRoleLabel(CombatClipRole role, CombatClipRoleSource source)
        {
            // "·" prefix = name-inferred (lower confidence). No prefix = reference-
            // inferred (asset graph confirms the role).
            return source == CombatClipRoleSource.Name ? "· " + role : role.ToString();
        }

        private (int present, int required) CountRequiredEventsAuthored(AnimationClip clip, CombatClipRole role)
        {
            CombatClipEventTemplate[] templates = CombatClipEventTemplates.GetTemplates(role);
            HashSet<string> requiredNames = new(StringComparer.Ordinal);
            foreach (CombatClipEventTemplate t in templates)
            {
                if (t.Required)
                    requiredNames.Add(t.FunctionName);
            }
            if (requiredNames.Count == 0)
                return (0, 0);

            AnimationEvent[] events = AnimationUtility.GetAnimationEvents(clip);
            int present = 0;
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (AnimationEvent ev in events)
            {
                if (requiredNames.Contains(ev.functionName) && seen.Add(ev.functionName))
                    present++;
            }
            return (present, requiredNames.Count);
        }

        private static Color ResolveCompletionColor(int present, int required)
        {
            if (required == 0) return Color.gray;
            if (present == 0) return new Color(1f, 0.5f, 0.5f);   // none authored
            if (present < required) return new Color(1f, 0.85f, 0.4f); // partial
            return new Color(0.55f, 1f, 0.55f);                    // complete
        }

        private void RefreshFolderClips()
        {
            if (_folder == null)
            {
                _folderClips = null;
                return;
            }
            string path = AssetDatabase.GetAssetPath(_folder);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
            {
                _folderClips = null;
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { path });
            List<AnimationClip> clips = new();
            HashSet<string> seenPaths = new(StringComparer.Ordinal);
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                    continue; // skip FBX-embedded clips so the list shows only authorable .anim files
                if (!seenPaths.Add(assetPath))
                    continue;

                AnimationClip? loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (loaded != null)
                    clips.Add(loaded);
            }
            clips.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal));
            _folderClips = clips.ToArray();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUI.color = _syncedWithAnimationWindow ? new Color(0.6f, 1f, 0.6f) : Color.white;
                GUILayout.Label(
                    _syncedWithAnimationWindow ? "● Animation sync" : "○ Manual time",
                    EditorStyles.miniBoldLabel,
                    GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
                if (GUILayout.Button(
                        new GUIContent("Refresh", "Rebuild combat clip role inference."),
                        EditorStyles.toolbarButton,
                        GUILayout.Width(58f)))
                    RefreshRoleMap();
            }

            using (new EditorGUI.DisabledScope(_syncedWithAnimationWindow))
            {
                AnimationClip? newClip = (AnimationClip?)EditorGUILayout.ObjectField(
                    "Clip", _clip, typeof(AnimationClip), false);
                if (!ReferenceEquals(newClip, _clip))
                {
                    _clip = newClip;
                    _time = 0f;
                }
            }
        }

        private void DrawEmbeddedPreview()
        {
            if (_embeddedClipEditor == null || !_embeddedClipEditor.HasPreviewGUI())
            {
                EditorGUILayout.HelpBox(
                    "Preview not available yet. The AnimationClipEditor needs a frame to initialize.",
                    MessageType.None);
                return;
            }

            // Preview settings (play button, scrub controls) — Unity renders these as a
            // toolbar at the top of the preview when supplied via OnPreviewSettings.
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _embeddedClipEditor.OnPreviewSettings();
            }

            float previewHeight = Mathf.Clamp(position.height * 0.32f, 120f, EmbeddedPreviewHeight);
            Rect rect = GUILayoutUtility.GetRect(10f, previewHeight, GUILayout.ExpandWidth(true));
            RoutePreviewScrollToWindow(rect);
            _embeddedClipEditor.OnInteractivePreviewGUI(rect, GUIStyle.none);

            // Drive the stamp time from the preview's playhead when reflection works.
            if (TryReadEmbeddedPreviewTime(out float previewTime))
            {
                _embeddedPreviewSyncs = true;
                if (Mathf.Abs(previewTime - _time) > 0.0005f)
                {
                    _time = previewTime;
                    Repaint();
                }
            }
            else
            {
                _embeddedPreviewSyncs = false;
            }
        }

        private void RoutePreviewScrollToWindow(Rect previewRect)
        {
            Event current = Event.current;
            if (current.type != EventType.ScrollWheel
                || !previewRect.Contains(current.mousePosition))
            {
                return;
            }

            _mainScroll.y = Mathf.Max(
                0f,
                _mainScroll.y + current.delta.y * PreviewScrollSpeed);
            current.Use();
            Repaint();
        }

        private void DrawTimeAndRole()
        {
            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            CombatClipRole inferred = InferRoleWithSource(_clip, out CombatClipRoleSource source);
            string sourceLabel = source switch
            {
                CombatClipRoleSource.Reference => " (by reference)",
                CombatClipRoleSource.Name => " (by name)",
                _ => string.Empty,
            };
            EditorGUILayout.LabelField("Inferred role:", inferred + sourceLabel);

            if (position.width < 440f)
            {
                _useManualRoleOverride = EditorGUILayout.ToggleLeft(
                    "Manual role override", _useManualRoleOverride);
                using (new EditorGUI.DisabledScope(!_useManualRoleOverride))
                    _manualRoleOverride = (CombatClipRole)EditorGUILayout.EnumPopup(_manualRoleOverride);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _useManualRoleOverride = EditorGUILayout.ToggleLeft(
                        "Manual role override", _useManualRoleOverride, GUILayout.Width(170));
                    using (new EditorGUI.DisabledScope(!_useManualRoleOverride))
                        _manualRoleOverride = (CombatClipRole)EditorGUILayout.EnumPopup(_manualRoleOverride);
                }
            }

            bool timeIsExternallyDriven = _syncedWithAnimationWindow || _embeddedPreviewSyncs;
            using (new EditorGUI.DisabledScope(timeIsExternallyDriven))
            {
                float length = _clip!.length > 0f ? _clip.length : 1f;
                _time = Mathf.Clamp(EditorGUILayout.Slider("Time (s)", _time, 0f, length), 0f, length);
            }

            float normalized = _clip!.length > 0f ? _time / _clip.length : 0f;
            string timeSourceLabel = _syncedWithAnimationWindow ? " (Animation window)"
                : _embeddedPreviewSyncs ? " (embedded preview)"
                : string.Empty;
            EditorGUILayout.LabelField(
                $"Time: {_time:F3}s ({normalized:F3} normalized) of {_clip.length:F3}s{timeSourceLabel}");
        }

        private CombatClipRole ResolveActiveRole()
        {
            if (_useManualRoleOverride)
                return _manualRoleOverride;
            return InferRole(_clip);
        }

        private void DrawStampButtons()
        {
            CombatClipRole role = ResolveActiveRole();
            CombatClipEventTemplate[] templates = CombatClipEventTemplates.GetTemplates(role);

            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Stamp event at current time:", EditorStyles.boldLabel);
            if (templates.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    role == CombatClipRole.Dodge
                        ? "Dodge timing is authored in the dedicated section below."
                        : $"No event templates defined for role '{role}'. Use the custom-name field below.",
                    MessageType.None);
                return;
            }

            foreach (CombatClipEventTemplate tmpl in templates)
            {
                string label = tmpl.Required ? $"{tmpl.FunctionName} *" : tmpl.FunctionName;
                if (position.width < 600f)
                {
                    if (GUILayout.Button(label, GUILayout.Height(24f), GUILayout.ExpandWidth(true)))
                        StampEvent(tmpl.FunctionName, _time);
                    GUILayout.Label(tmpl.Description, EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space(2f);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(label, GUILayout.Width(220), GUILayout.Height(22)))
                            StampEvent(tmpl.FunctionName, _time);
                        GUILayout.Label(tmpl.Description, EditorStyles.wordWrappedMiniLabel);
                    }
                }
            }
            EditorGUILayout.LabelField("* = required for this role", EditorStyles.miniLabel);
        }

        private void DrawCustomEventStamp()
        {
            _showCustomEvent = EditorGUILayout.Foldout(
                _showCustomEvent,
                "Custom event",
                true,
                EditorStyles.foldoutHeader);
            if (!_showCustomEvent)
                return;

            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                _customEventName = EditorGUILayout.TextField(_customEventName);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_customEventName)))
                {
                    if (GUILayout.Button("Stamp", GUILayout.Width(80), GUILayout.Height(20)))
                    {
                        StampEvent(_customEventName.Trim(), _time);
                        _customEventName = string.Empty;
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        private void DrawDodgeTiming()
        {
            CombatClipRole role = ResolveActiveRole();
            AnimationEvent[] clipEvents = AnimationUtility.GetAnimationEvents(_clip!);
            AnimationEvent[] startEvents = clipEvents
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnDodgeStart,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            AnimationEvent[] travelEndEvents = clipEvents
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnDodgeTravelEnd,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            if (role != CombatClipRole.Dodge
                && startEvents.Length == 0
                && travelEndEvents.Length == 0)
            {
                return;
            }

            float clipLength = Mathf.Max(0f, _clip!.length);
            float startSeconds = startEvents.Length > 0
                ? Mathf.Clamp(startEvents[0].time, 0f, clipLength)
                : 0f;
            bool hasTravelEnd = travelEndEvents.Length > 0;
            float travelEndSeconds = hasTravelEnd
                ? Mathf.Clamp(travelEndEvents[0].time, startSeconds, clipLength)
                : clipLength;

            EditorGUILayout.Space(8f);
            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Dodge timing", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Startup trim skips the opening windup without deleting frames. The optional travel-end marker identifies the pose reached when dodge movement stops; the remaining recovery/settle tail plays at authored speed. Movement may crossfade to locomotion once authoritative recovery ends. Each directional clip keeps its own markers.",
                MessageType.Info);

            if (startEvents.Length > 1 || travelEndEvents.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    "This clip has duplicate dodge timing markers. Setting either marker here replaces all markers of that type with one.",
                    MessageType.Warning);
            }

            EditorGUI.BeginChangeCheck();
            float enteredStartSeconds = EditorGUILayout.DelayedFloatField(
                new GUIContent(
                    "Startup trim (seconds)",
                    $"The first played frame. Limited to {CombatAnimationEvents.OnDodgeTravelEnd} when that marker exists."),
                startSeconds);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyDodgeTimingMarker(
                    CombatAnimationEvents.OnDodgeStart,
                    enteredStartSeconds,
                    0f,
                    travelEndSeconds,
                    removeAtZero: true);
            }

            string travelEndStatus = hasTravelEnd
                ? $"{travelEndSeconds:0.000}s ({NormalizedTime(travelEndSeconds, clipLength):0.000} normalized)"
                : "Automatic — proportional to authoritative travel/recovery durations";
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Travel ends",
                    $"Optional {CombatAnimationEvents.OnDodgeTravelEnd} marker."),
                travelEndStatus);

            bool playheadCanBeStart = _time <= travelEndSeconds + 0.0001f;
            bool playheadCanBeTravelEnd = _time + 0.0001f >= startSeconds;
            if (position.width < 620f)
            {
                using (new EditorGUI.DisabledScope(!playheadCanBeStart))
                {
                    if (GUILayout.Button($"Set Start Here ({_time:0.000}s)"))
                    {
                        ApplyDodgeTimingMarker(
                            CombatAnimationEvents.OnDodgeStart,
                            _time,
                            0f,
                            travelEndSeconds,
                            removeAtZero: true);
                    }
                }
                using (new EditorGUI.DisabledScope(startEvents.Length == 0))
                {
                    if (GUILayout.Button("Remove Startup Trim"))
                        RemoveDodgeTimingMarker(CombatAnimationEvents.OnDodgeStart);
                }
                using (new EditorGUI.DisabledScope(!playheadCanBeTravelEnd))
                {
                    if (GUILayout.Button($"Set Travel End Here ({_time:0.000}s)"))
                    {
                        ApplyDodgeTimingMarker(
                            CombatAnimationEvents.OnDodgeTravelEnd,
                            _time,
                            startSeconds,
                            clipLength,
                            removeAtZero: false);
                    }
                }
                using (new EditorGUI.DisabledScope(!hasTravelEnd))
                {
                    if (GUILayout.Button("Use Automatic Travel End"))
                        RemoveDodgeTimingMarker(CombatAnimationEvents.OnDodgeTravelEnd);
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!playheadCanBeStart))
                    {
                        if (GUILayout.Button($"Set Start Here ({_time:0.000}s)"))
                        {
                            ApplyDodgeTimingMarker(
                                CombatAnimationEvents.OnDodgeStart,
                                _time,
                                0f,
                                travelEndSeconds,
                                removeAtZero: true);
                        }
                    }
                    using (new EditorGUI.DisabledScope(startEvents.Length == 0))
                    {
                        if (GUILayout.Button("Remove Startup Trim"))
                            RemoveDodgeTimingMarker(CombatAnimationEvents.OnDodgeStart);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!playheadCanBeTravelEnd))
                    {
                        if (GUILayout.Button($"Set Travel End Here ({_time:0.000}s)"))
                        {
                            ApplyDodgeTimingMarker(
                                CombatAnimationEvents.OnDodgeTravelEnd,
                                _time,
                                startSeconds,
                                clipLength,
                                removeAtZero: false);
                        }
                    }
                    using (new EditorGUI.DisabledScope(!hasTravelEnd))
                    {
                        if (GUILayout.Button("Use Automatic Travel End"))
                            RemoveDodgeTimingMarker(CombatAnimationEvents.OnDodgeTravelEnd);
                    }
                }
            }

            EditorGUILayout.LabelField(
                $"Playback starts at {startSeconds:0.000}s. " +
                (hasTravelEnd
                    ? $"Travel reaches its authored end pose at {travelEndSeconds:0.000}s; the remaining {Mathf.Max(0f, clipLength - travelEndSeconds):0.000}s is the recovery/settle portion."
                    : "Without a travel-end marker, the runtime estimates the travel boundary from authoritative travel/recovery proportions, then plays the recovery/settle tail at authored speed."),
                EditorStyles.wordWrappedMiniLabel);
        }

        private void ApplyDodgeTimingMarker(
            string functionName,
            float requestedSeconds,
            float minimumSeconds,
            float maximumSeconds,
            bool removeAtZero)
        {
            if (_clip == null)
                return;

            float resolvedSeconds = Mathf.Clamp(
                requestedSeconds,
                Mathf.Max(0f, minimumSeconds),
                Mathf.Min(Mathf.Max(0f, _clip.length), Mathf.Max(0f, maximumSeconds)));
            if (removeAtZero && resolvedSeconds <= 0.0001f)
            {
                RemoveDodgeTimingMarker(functionName);
                return;
            }

            Undo.RegisterCompleteObjectUndo(_clip, $"Set dodge timing marker '{functionName}'");
            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(_clip)
                .Where(animationEvent => !string.Equals(
                    animationEvent.functionName,
                    functionName,
                    StringComparison.Ordinal))
                .ToList();
            events.Add(new AnimationEvent
            {
                functionName = functionName,
                time = resolvedSeconds,
            });
            AnimationUtility.SetAnimationEvents(
                _clip,
                events.OrderBy(animationEvent => animationEvent.time).ToArray());
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);
            ShowNotification(new GUIContent(
                $"{functionName} set to {resolvedSeconds:0.000}s."));
            Repaint();
        }

        private void RemoveDodgeTimingMarker(string functionName)
        {
            if (_clip == null)
                return;

            AnimationEvent[] existing = AnimationUtility.GetAnimationEvents(_clip);
            AnimationEvent[] remaining = existing
                .Where(animationEvent => !string.Equals(
                    animationEvent.functionName,
                    functionName,
                    StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == existing.Length)
                return;

            Undo.RegisterCompleteObjectUndo(_clip, $"Remove dodge timing marker '{functionName}'");
            AnimationUtility.SetAnimationEvents(_clip, remaining);
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);
            ShowNotification(new GUIContent($"{functionName} removed."));
            Repaint();
        }

        private static float NormalizedTime(float seconds, float clipLength)
            => clipLength > 0.0001f ? Mathf.Clamp01(seconds / clipLength) : 0f;

        private void DrawHitWindowSynchronization()
        {
            CombatClipRole role = ResolveActiveRole();
            bool meleeRole = role == CombatClipRole.MeleeStrike
                || role == CombatClipRole.PhasedMeleeStart
                || role == CombatClipRole.PhasedMeleeLoop
                || role == CombatClipRole.PhasedMeleeEnd;

            AnimationEvent[] hitEvents = AnimationUtility.GetAnimationEvents(_clip!)
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnStrikeHit,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            if (!meleeRole && hitEvents.Length == 0)
                return;

            EditorGUILayout.Space(8f);
            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Melee contact and startup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Stamp OnStrikeHit on the physical contact pose.\n" +
                "2. Scrub the preview to the frame where the attack should begin.\n" +
                "3. Click Set Start Here below.\n\n" +
                "Startup trim skips the opening during playback; it does not modify the animation clip or move OnStrikeHit.",
                MessageType.Info);

            EditorGUILayout.LabelField(
                $"Authored contact events: {hitEvents.Length}",
                EditorStyles.miniBoldLabel);

            IReadOnlyList<CombatAnimationSetEditor.StartupTrimTarget> targets =
                GetStartupTrimTargets();
            DrawHypotheticalInputToDamageEstimate(hitEvents, targets);
            DrawStartupTrimAuthoring(hitEvents, targets);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Automatic synchronization", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Setting startup trim saves the CombatAnimationSet and updates its hit-window mirror and the shared server manifest immediately. "
                + LocalSpacetimeDbSharedDataPublisher.HubMatchRefreshGuidance,
                MessageType.None);
            if (GUILayout.Button("Synchronize This Clip Now", GUILayout.Height(22)))
                SynchronizeHitWindows();

            if (!string.IsNullOrWhiteSpace(_hitWindowSyncStatus))
            {
                EditorGUILayout.HelpBox(
                    _hitWindowSyncStatus,
                    _hitWindowSyncSucceeded ? MessageType.Info : MessageType.Error);
            }
        }

        private void DrawCastReleaseEntry()
        {
            CombatClipRole role = ResolveActiveRole();
            AnimationEvent[] clipEvents = AnimationUtility.GetAnimationEvents(_clip!);
            AnimationEvent[] entryEvents = clipEvents
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnCastReleaseEntry,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            if (role != CombatClipRole.SpellRelease && entryEvents.Length == 0)
                return;

            EditorGUILayout.Space(8f);
            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Cast-release receiving point", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"{CombatAnimationEvents.OnCastReleaseEntry} is the first frame sampled in this receiving release clip when a charged cast hands off from its lead-in. " +
                $"The lead-in continues until the remaining interval to {CombatAnimationEvents.OnReleaseFrame} fits exactly before cast completion. Instant casts keep their separate startup marker.",
                MessageType.Info);

            AnimationEvent[] releaseEvents = clipEvents
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnReleaseFrame,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            bool hasReleaseEvent = releaseEvents.Length > 0;
            float releaseSeconds = hasReleaseEvent ? releaseEvents[0].time : 0f;
            float authoredEntrySeconds = entryEvents.Length > 0 ? entryEvents[0].time : 0f;

            if (entryEvents.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    $"This clip has {entryEvents.Length} {CombatAnimationEvents.OnCastReleaseEntry} events. Setting an entry here will replace them with one marker.",
                    MessageType.Warning);
            }

            if (!hasReleaseEvent)
            {
                EditorGUILayout.HelpBox(
                    $"Stamp {CombatAnimationEvents.OnReleaseFrame} at the visible release pose before setting the receiving point.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                float enteredEntrySeconds = EditorGUILayout.DelayedFloatField(
                    new GUIContent(
                        "Receiving point (seconds)",
                        $"The first frame played after a charged cast lead-in. Limited to {CombatAnimationEvents.OnReleaseFrame}. Zero uses the clip start."),
                    authoredEntrySeconds);
                if (EditorGUI.EndChangeCheck())
                    ApplyCastReleaseEntry(enteredEntrySeconds, releaseSeconds);

                float resolvedEntrySeconds = Mathf.Clamp(
                    authoredEntrySeconds,
                    0f,
                    releaseSeconds);
                EditorGUILayout.LabelField(
                    $"Charged release playback enters at {resolvedEntrySeconds:0.000}s; " +
                    $"the visible release follows after {Mathf.Max(0f, releaseSeconds - resolvedEntrySeconds):0.000}s.",
                    EditorStyles.wordWrappedMiniLabel);

                bool playheadCanBeEntry = _time <= releaseSeconds + 0.0001f;
                if (position.width < 560f)
                {
                    using (new EditorGUI.DisabledScope(!playheadCanBeEntry))
                    {
                        if (GUILayout.Button($"Set Receiving Point Here ({_time:0.000}s)"))
                            ApplyCastReleaseEntry(_time, releaseSeconds);
                    }
                    using (new EditorGUI.DisabledScope(entryEvents.Length == 0))
                    {
                        if (GUILayout.Button("Use Clip Start"))
                            RemoveCastReleaseEntry();
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!playheadCanBeEntry))
                        {
                            if (GUILayout.Button($"Set Receiving Point Here ({_time:0.000}s)"))
                                ApplyCastReleaseEntry(_time, releaseSeconds);
                        }
                        using (new EditorGUI.DisabledScope(entryEvents.Length == 0))
                        {
                            if (GUILayout.Button("Use Clip Start", GUILayout.Width(110f)))
                                RemoveCastReleaseEntry();
                        }
                    }
                }

                EditorGUILayout.LabelField(
                    playheadCanBeEntry
                        ? "The current playhead can be used as the charged-cast receiving point."
                        : $"Scrub to or before {CombatAnimationEvents.OnReleaseFrame} to set the receiving point.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (!hasReleaseEvent && entryEvents.Length > 0 && GUILayout.Button("Use Clip Start"))
                RemoveCastReleaseEntry();
        }

        private void ApplyCastReleaseEntry(float requestedEntrySeconds, float releaseSeconds)
        {
            if (_clip == null)
                return;

            float resolvedEntrySeconds = Mathf.Clamp(
                requestedEntrySeconds,
                0f,
                Mathf.Min(Mathf.Max(0f, _clip.length), Mathf.Max(0f, releaseSeconds)));
            if (resolvedEntrySeconds <= 0.0001f)
            {
                RemoveCastReleaseEntry();
                return;
            }

            Undo.RegisterCompleteObjectUndo(_clip, "Set cast-release receiving point");
            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(_clip)
                .Where(animationEvent => !string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnCastReleaseEntry,
                    StringComparison.Ordinal))
                .ToList();
            events.Add(new AnimationEvent
            {
                functionName = CombatAnimationEvents.OnCastReleaseEntry,
                time = resolvedEntrySeconds,
            });
            AnimationUtility.SetAnimationEvents(
                _clip,
                events.OrderBy(animationEvent => animationEvent.time).ToArray());
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);
            ShowNotification(new GUIContent(
                $"Charged casts now enter this release at {resolvedEntrySeconds:0.000}s."));
            Repaint();
        }

        private void RemoveCastReleaseEntry()
        {
            if (_clip == null)
                return;

            AnimationEvent[] existing = AnimationUtility.GetAnimationEvents(_clip);
            AnimationEvent[] remaining = existing
                .Where(animationEvent => !string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnCastReleaseEntry,
                    StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == existing.Length)
                return;

            Undo.RegisterCompleteObjectUndo(_clip, "Use release clip start for charged casts");
            AnimationUtility.SetAnimationEvents(_clip, remaining);
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);
            ShowNotification(new GUIContent("Charged casts now enter this release at clip start."));
            Repaint();
        }

        private void DrawInstantCastStartupTrim()
        {
            CombatClipRole role = ResolveActiveRole();
            AnimationEvent[] clipEvents = AnimationUtility.GetAnimationEvents(_clip!);
            AnimationEvent[] trimEvents = clipEvents
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnInstantCastStart,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            if (role != CombatClipRole.SpellRelease && trimEvents.Length == 0)
                return;

            EditorGUILayout.Space(8f);
            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Instant-cast startup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"{CombatAnimationEvents.OnInstantCastStart} skips the selected clip's opening only when synced gameplay confirms the spell is Instant. " +
                "Charged and channel releases that share this clip still start at the beginning.",
                MessageType.Info);

            AnimationEvent[] releaseEvents = clipEvents
                .Where(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnReleaseFrame,
                    StringComparison.Ordinal))
                .OrderBy(animationEvent => animationEvent.time)
                .ToArray();
            bool hasReleaseEvent = releaseEvents.Length > 0;
            float releaseSeconds = hasReleaseEvent ? releaseEvents[0].time : 0f;
            float authoredTrimSeconds = trimEvents.Length > 0 ? trimEvents[0].time : 0f;

            if (trimEvents.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    $"This clip has {trimEvents.Length} {CombatAnimationEvents.OnInstantCastStart} events. Setting a start here will replace them with one marker.",
                    MessageType.Warning);
            }

            if (!hasReleaseEvent)
            {
                EditorGUILayout.HelpBox(
                    $"Stamp {CombatAnimationEvents.OnReleaseFrame} at the visible hand-release pose before setting instant startup trim.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                float enteredTrimSeconds = EditorGUILayout.DelayedFloatField(
                    new GUIContent(
                        "Startup trim (seconds)",
                        $"The first frame played by confirmed instant casts. Limited to {CombatAnimationEvents.OnReleaseFrame}."),
                    authoredTrimSeconds);
                if (EditorGUI.EndChangeCheck())
                    ApplyInstantCastStartupTrim(enteredTrimSeconds, releaseSeconds);

                float resolvedTrimSeconds = Mathf.Clamp(authoredTrimSeconds, 0f, releaseSeconds);
                EditorGUILayout.LabelField(
                    $"Instant playback starts at {resolvedTrimSeconds:0.000}s; " +
                    $"the visible release follows after {Mathf.Max(0f, releaseSeconds - resolvedTrimSeconds):0.000}s.",
                    EditorStyles.wordWrappedMiniLabel);

                bool playheadCanBeStartup = _time <= releaseSeconds + 0.0001f;
                if (position.width < 560f)
                {
                    using (new EditorGUI.DisabledScope(!playheadCanBeStartup))
                    {
                        if (GUILayout.Button($"Set Start Here ({_time:0.000}s)"))
                            ApplyInstantCastStartupTrim(_time, releaseSeconds);
                    }
                    if (GUILayout.Button($"Trim to Release ({releaseSeconds:0.000}s)"))
                        ApplyInstantCastStartupTrim(releaseSeconds, releaseSeconds);
                    using (new EditorGUI.DisabledScope(trimEvents.Length == 0))
                    {
                        if (GUILayout.Button("Remove Trim"))
                            RemoveInstantCastStartupTrim();
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!playheadCanBeStartup))
                        {
                            if (GUILayout.Button($"Set Start Here ({_time:0.000}s)"))
                                ApplyInstantCastStartupTrim(_time, releaseSeconds);
                        }
                        if (GUILayout.Button($"Trim to Release ({releaseSeconds:0.000}s)"))
                            ApplyInstantCastStartupTrim(releaseSeconds, releaseSeconds);
                        using (new EditorGUI.DisabledScope(trimEvents.Length == 0))
                        {
                            if (GUILayout.Button("Remove Trim", GUILayout.Width(90f)))
                                RemoveInstantCastStartupTrim();
                        }
                    }
                }

                EditorGUILayout.LabelField(
                    playheadCanBeStartup
                        ? "The current playhead can be used as the instant-cast start."
                        : $"Scrub to or before {CombatAnimationEvents.OnReleaseFrame} to use Set Start Here.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (!hasReleaseEvent && trimEvents.Length > 0 && GUILayout.Button("Remove Trim"))
                RemoveInstantCastStartupTrim();
        }

        private void ApplyInstantCastStartupTrim(float requestedTrimSeconds, float releaseSeconds)
        {
            if (_clip == null)
                return;

            float resolvedTrimSeconds = Mathf.Clamp(
                requestedTrimSeconds,
                0f,
                Mathf.Min(Mathf.Max(0f, _clip.length), Mathf.Max(0f, releaseSeconds)));
            if (resolvedTrimSeconds <= 0.0001f)
            {
                RemoveInstantCastStartupTrim();
                return;
            }

            Undo.RegisterCompleteObjectUndo(_clip, "Set instant-cast startup trim");
            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(_clip)
                .Where(animationEvent => !string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnInstantCastStart,
                    StringComparison.Ordinal))
                .ToList();
            events.Add(new AnimationEvent
            {
                functionName = CombatAnimationEvents.OnInstantCastStart,
                time = resolvedTrimSeconds,
            });
            AnimationUtility.SetAnimationEvents(
                _clip,
                events.OrderBy(animationEvent => animationEvent.time).ToArray());
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);
            ShowNotification(new GUIContent($"Instant casts now start at {resolvedTrimSeconds:0.000}s."));
            Repaint();
        }

        private void RemoveInstantCastStartupTrim()
        {
            if (_clip == null)
                return;

            AnimationEvent[] existing = AnimationUtility.GetAnimationEvents(_clip);
            AnimationEvent[] remaining = existing
                .Where(animationEvent => !string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnInstantCastStart,
                    StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == existing.Length)
                return;

            Undo.RegisterCompleteObjectUndo(_clip, "Remove instant-cast startup trim");
            AnimationUtility.SetAnimationEvents(_clip, remaining);
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);
            ShowNotification(new GUIContent("Instant-cast startup trim removed."));
            Repaint();
        }

        private static void DrawHypotheticalInputToDamageEstimate(
            AnimationEvent[] hitEvents,
            IReadOnlyList<CombatAnimationSetEditor.StartupTrimTarget> targets)
        {
            if (hitEvents.Length == 0)
                return;

            var effectiveFirstHitDelayMs = new List<int>();
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                CombatAnimationSetEditor.StartupTrimTarget target = targets[targetIndex];
                WeaponMeleeAttackAuthoring attack = target.Set.meleeAttacks[target.AttackIndex];
                if (attack.TryGetEffectiveStrikeHitTimesSeconds(out float[] effectiveHitTimes)
                    && effectiveHitTimes.Length > 0)
                {
                    effectiveFirstHitDelayMs.Add(
                        Mathf.Max(0, Mathf.RoundToInt(effectiveHitTimes[0] * 1000f)));
                }
            }

            bool usesUnassignedClipFallback = effectiveFirstHitDelayMs.Count == 0;
            if (usesUnassignedClipFallback)
            {
                effectiveFirstHitDelayMs.Add(
                    Mathf.Max(0, Mathf.RoundToInt(hitEvents[0].time * 1000f)));
            }

            int minimumEffectiveHitDelayMs = effectiveFirstHitDelayMs.Min();
            int maximumEffectiveHitDelayMs = effectiveFirstHitDelayMs.Max();
            int estimatedMinimumMs = minimumEffectiveHitDelayMs + IdealInputToServerLatencyMinMs;
            int estimatedMaximumMs = maximumEffectiveHitDelayMs
                + IdealInputToServerLatencyMaxMs
                + ServerCombatTickMaxDelayMs;
            string effectiveDelay = minimumEffectiveHitDelayMs == maximumEffectiveHitDelayMs
                ? $"{minimumEffectiveHitDelayMs} ms"
                : $"{minimumEffectiveHitDelayMs}–{maximumEffectiveHitDelayMs} ms across assignments";
            string fallbackNote = usesUnassignedClipFallback
                ? " No CombatAnimationSet assignment was found, so this assumes zero startup trim."
                : string.Empty;

            EditorGUILayout.HelpBox(
                $"Hypothetical input → first server damage: {estimatedMinimumMs}–{estimatedMaximumMs} ms\n" +
                $"Effective first hit {effectiveDelay} + {IdealInputToServerLatencyMinMs}–{IdealInputToServerLatencyMaxMs} ms input-to-server latency + 0–{ServerCombatTickMaxDelayMs} ms combat-tick alignment.{fallbackNote}\n\n" +
                "Informational direct-melee estimate only. Queueing, gap-close arrival, projectile travel, server stalls, and return replication are not included.",
                MessageType.None);
        }

        private void DrawStartupTrimAuthoring(
            AnimationEvent[] hitEvents,
            IReadOnlyList<CombatAnimationSetEditor.StartupTrimTarget> targets)
        {
            EditorGUILayout.LabelField("Startup trim", EditorStyles.boldLabel);

            List<CombatAnimationSetEditor.StartupTrimTarget> supportedTargets = targets
                .Where(target => target.SupportsStartupTrim)
                .ToList();
            int unsupportedTargetCount = targets.Count - supportedTargets.Count;

            if (supportedTargets.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    targets.Count == 0
                        ? "The selected clip is not used by a CombatAnimationSet melee attack, so there is no runtime melee playback to trim."
                        : "This clip is referenced only by phased melee attacks. Startup trim is supported only for single-clip melee.",
                    MessageType.Warning);
                return;
            }

            if (hitEvents.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    $"Stamp {CombatAnimationEvents.OnStrikeHit} before choosing a startup frame.",
                    MessageType.Warning);
                return;
            }

            float firstContactSeconds = hitEvents[0].time;
            float authoredTrimSeconds = supportedTargets[0].AuthoredTrimSeconds;
            bool hasMixedTrimValues = supportedTargets.Any(target =>
                !Mathf.Approximately(target.AuthoredTrimSeconds, authoredTrimSeconds));

            if (supportedTargets.Count > 1)
            {
                EditorGUILayout.LabelField(
                    $"This clip is reused by {supportedTargets.Count} melee attacks; this trim applies to all of them.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            if (hasMixedTrimValues)
            {
                EditorGUILayout.HelpBox(
                    "This clip currently has different startup values across its melee assignments. Setting it here will unify them, because the selected clip is the authoring context.",
                    MessageType.Warning);
            }

            EditorGUI.showMixedValue = hasMixedTrimValues;
            EditorGUI.BeginChangeCheck();
            float enteredTrimSeconds = EditorGUILayout.DelayedFloatField(
                new GUIContent(
                    "Startup trim (seconds)",
                    "The timestamp of the first frame that will play for the selected clip. Values are limited to the first OnStrikeHit contact event."),
                authoredTrimSeconds);
            if (EditorGUI.EndChangeCheck())
                ApplyStartupTrim(enteredTrimSeconds);
            EditorGUI.showMixedValue = false;

            if (!hasMixedTrimValues)
            {
                float resolvedTrimSeconds = Mathf.Clamp(
                    authoredTrimSeconds,
                    0f,
                    firstContactSeconds);
                EditorGUILayout.LabelField(
                    $"Playback starts at {resolvedTrimSeconds:0.000}s; " +
                    $"contact occurs {Mathf.Max(0f, firstContactSeconds - resolvedTrimSeconds):0.000}s after input.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            bool playheadCanBeStartup = _time <= firstContactSeconds + 0.0001f;
            if (position.width < 560f)
            {
                using (new EditorGUI.DisabledScope(!playheadCanBeStartup))
                {
                    if (GUILayout.Button($"Set Start Here ({_time:0.000}s)"))
                        ApplyStartupTrim(_time);
                }
                if (GUILayout.Button($"Trim to Contact ({firstContactSeconds:0.000}s, instant)"))
                    ApplyStartupTrim(firstContactSeconds);
                using (new EditorGUI.DisabledScope(
                           !hasMixedTrimValues && authoredTrimSeconds <= 0f))
                {
                    if (GUILayout.Button("Remove Trim"))
                        ApplyStartupTrim(0f);
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!playheadCanBeStartup))
                    {
                        if (GUILayout.Button($"Set Start Here ({_time:0.000}s)"))
                            ApplyStartupTrim(_time);
                    }
                    if (GUILayout.Button($"Trim to Contact ({firstContactSeconds:0.000}s, instant)"))
                        ApplyStartupTrim(firstContactSeconds);
                    using (new EditorGUI.DisabledScope(
                               !hasMixedTrimValues && authoredTrimSeconds <= 0f))
                    {
                        if (GUILayout.Button("Remove Trim", GUILayout.Width(90f)))
                            ApplyStartupTrim(0f);
                    }
                }
            }

            EditorGUILayout.LabelField(
                playheadCanBeStartup
                    ? "The current playhead can be used as the melee start."
                    : "Scrub to or before the first contact event to use Set Start Here.",
                EditorStyles.wordWrappedMiniLabel);

            if (unsupportedTargetCount > 0)
            {
                EditorGUILayout.LabelField(
                    $"{unsupportedTargetCount} phased reference(s) are unchanged; phased melee does not use startup trim.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private IReadOnlyList<CombatAnimationSetEditor.StartupTrimTarget> GetStartupTrimTargets()
        {
            if (!ReferenceEquals(_startupTrimTargetsClip, _clip))
                RefreshStartupTrimTargets();
            return _startupTrimTargets;
        }

        private void RefreshStartupTrimTargets()
        {
            _startupTrimTargetsClip = _clip;
            _startupTrimTargets = CombatAnimationSetEditor.FindStartupTrimTargets(_clip);
        }

        private void ApplyStartupTrim(float requestedTrimSeconds)
        {
            GUI.FocusControl(null);
            _hitWindowSyncSucceeded = CombatAnimationSetEditor.SetStartupTrimForClip(
                _clip,
                requestedTrimSeconds,
                out _hitWindowSyncStatus);
            RefreshStartupTrimTargets();
            ShowNotification(new GUIContent(_hitWindowSyncStatus));
            Repaint();
        }

        private void DrawExistingEvents()
        {
            AnimationEvent[] events = AnimationUtility.GetAnimationEvents(_clip!);
            EditorGUILayout.Space(8f);
            _showExistingEvents = EditorGUILayout.Foldout(
                _showExistingEvents,
                $"Existing events ({events.Length})",
                true,
                EditorStyles.foldoutHeader);
            if (!_showExistingEvents)
                return;

            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            if (events.Length == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
                return;
            }

            AnimationEvent[] sorted = events.OrderBy(e => e.time).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                AnimationEvent ev = sorted[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    float norm = _clip!.length > 0f ? ev.time / _clip.length : 0f;
                    EditorGUILayout.LabelField(
                        $"{ev.time:F3}s ({norm:F2})  {ev.functionName}",
                        GUILayout.MinWidth(120f),
                        GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(
                            new GUIContent("→", "Move the manual playhead to this event."),
                            GUILayout.Width(28f)))
                    {
                        // Best-effort: jump scrubber to this event's time.
                        if (!_syncedWithAnimationWindow)
                            _time = ev.time;
                    }
                    if (GUILayout.Button(
                            new GUIContent("×", "Remove this event."),
                            GUILayout.Width(28f)))
                    {
                        RemoveEventAt(ev.time, ev.functionName);
                        GUIUtility.ExitGUI();
                        return;
                    }
                }
            }
        }

        private void StampEvent(string functionName, float time)
        {
            if (_clip == null || string.IsNullOrWhiteSpace(functionName))
                return;

            Undo.RegisterCompleteObjectUndo(_clip, $"Stamp animation event '{functionName}'");
            AnimationEvent[] existing = AnimationUtility.GetAnimationEvents(_clip);
            AnimationEvent[] next = new AnimationEvent[existing.Length + 1];
            Array.Copy(existing, next, existing.Length);
            next[existing.Length] = new AnimationEvent { functionName = functionName, time = time };
            AnimationUtility.SetAnimationEvents(_clip, next);
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);

            if (string.Equals(functionName, CombatAnimationEvents.OnStrikeHit, StringComparison.Ordinal))
                SynchronizeHitWindows();
        }

        private void RemoveEventAt(float time, string functionName)
        {
            if (_clip == null)
                return;

            List<AnimationEvent> remaining = AnimationUtility.GetAnimationEvents(_clip).ToList();
            int idx = remaining.FindIndex(e =>
                Mathf.Approximately(e.time, time) && string.Equals(e.functionName, functionName, StringComparison.Ordinal));
            if (idx < 0)
                return;

            if (string.Equals(functionName, CombatAnimationEvents.OnStrikeHit, StringComparison.Ordinal)
                && remaining.Count(e => string.Equals(
                    e.functionName,
                    CombatAnimationEvents.OnStrikeHit,
                    StringComparison.Ordinal)) == 1
                && CombatAnimationSetEditor.IsReferencedByMeleeAttack(_clip))
            {
                _hitWindowSyncSucceeded = false;
                _hitWindowSyncStatus =
                    "Cannot remove the final OnStrikeHit from an assigned melee attack. Stamp its replacement first, then remove the old event.";
                ShowNotification(new GUIContent("Assigned melee attacks need at least one OnStrikeHit."));
                return;
            }

            Undo.RegisterCompleteObjectUndo(_clip, $"Remove animation event '{functionName}'");
            remaining.RemoveAt(idx);
            AnimationUtility.SetAnimationEvents(_clip, remaining.ToArray());
            EditorUtility.SetDirty(_clip);
            AssetDatabase.SaveAssetIfDirty(_clip);

            if (string.Equals(functionName, CombatAnimationEvents.OnStrikeHit, StringComparison.Ordinal))
                SynchronizeHitWindows();
        }

        private void SynchronizeHitWindows()
        {
            _hitWindowSyncSucceeded = CombatAnimationSetEditor.SynchronizeHitEventsForClip(
                _clip,
                out _hitWindowSyncStatus);
            ShowNotification(new GUIContent(_hitWindowSyncStatus));
            Repaint();
        }
    }
}
