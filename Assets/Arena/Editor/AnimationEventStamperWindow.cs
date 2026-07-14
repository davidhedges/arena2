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
        private string _hitWindowSyncStatus = string.Empty;
        private bool _hitWindowSyncSucceeded;

        private UnityEditor.Editor? _embeddedClipEditor;
        private bool _embeddedPreviewSyncs;
        private const float EmbeddedPreviewHeight = 220f;

        private DefaultAsset? _folder;
        private AnimationClip[]? _folderClips;
        private Vector2 _folderListScroll;
        private const float FolderListMaxHeight = 180f;

        private Dictionary<AnimationClip, List<CombatClipRoleObservation>>? _roleMap;
        private Vector2 _mainScroll;

        [SerializeField] private bool _showFolderBrowser;
        [SerializeField] private bool _showPreview = true;
        [SerializeField] private bool _showCustomEvent;
        [SerializeField] private bool _showExistingEvents = true;

        private void OnEnable()
        {
            // Reset any restrictive dimensions retained by an older saved layout.
            minSize = new Vector2(300, 300);
            maxSize = new Vector2(4096, 4096);
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
                    "Clip preview",
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
                }

                DrawTimeAndRole();
                EditorGUILayout.Space(8f);
                DrawStampButtons();
                EditorGUILayout.Space(8f);
                DrawCustomEventStamp();
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

            using (EditorGUILayout.ScrollViewScope scroll = new(_folderListScroll, GUILayout.MaxHeight(FolderListMaxHeight)))
            {
                _folderListScroll = scroll.scrollPosition;
                foreach (AnimationClip clip in _folderClips)
                {
                    DrawFolderClipRow(clip);
                }
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
                    $"No event templates defined for role '{role}'. Use the custom-name field below.",
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

        private void DrawHitWindowSynchronization()
        {
            CombatClipRole role = ResolveActiveRole();
            bool meleeRole = role == CombatClipRole.MeleeStrike
                || role == CombatClipRole.PhasedMeleeStart
                || role == CombatClipRole.PhasedMeleeLoop
                || role == CombatClipRole.PhasedMeleeEnd;
            if (!meleeRole)
                return;

            EditorGUILayout.Space(8f);
            using EditorGUILayout.VerticalScope section = new(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Gameplay hit-window synchronization:", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "OnStrikeHit is authoritative for assigned melee attacks. Adding or removing one automatically mirrors the CombatAnimationSet hit-window array and updates only the affected strike in the shared server manifest.",
                MessageType.Info);
            int hitEventCount = AnimationUtility.GetAnimationEvents(_clip!)
                .Count(animationEvent => string.Equals(
                    animationEvent.functionName,
                    CombatAnimationEvents.OnStrikeHit,
                    StringComparison.Ordinal));
            EditorGUILayout.LabelField(
                $"Authored gameplay hit windows: {hitEventCount}",
                EditorStyles.miniBoldLabel);
            if (GUILayout.Button("Synchronize This Clip Now", GUILayout.Height(22)))
                SynchronizeHitWindows();

            if (!string.IsNullOrWhiteSpace(_hitWindowSyncStatus))
            {
                EditorGUILayout.HelpBox(
                    _hitWindowSyncStatus,
                    _hitWindowSyncSucceeded ? MessageType.Info : MessageType.Error);
            }
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
