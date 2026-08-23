#nullable enable

using System;
using System.Collections.Generic;
using Arena.Input;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Arena.Editor
{
    internal sealed partial class SpellAuthoringWindow
    {
        private const float CastPreviewHeight = 260f;
        private const float CastPreviewMinDistance = 0.5f;
        private const float CastPreviewMaxDistance = 2.5f;

        private readonly struct CastPreviewClipOption
        {
            public CastPreviewClipOption(string label, AnimationClip clip)
            {
                Label = label;
                Clip = clip;
            }

            public string Label { get; }
            public AnimationClip Clip { get; }
        }

        private PreviewRenderUtility? _castPreviewUtility;
        private GameObject? _castPreviewInstance;
        private Animator? _castPreviewAnimator;
        private PlayableGraph _castPreviewGraph;
        private AnimationClipPlayable _castPreviewPlayable;
        private bool _castPreviewGraphCreated;
        private CombatAnimationSet? _castPreviewAnimationSet;
        private AnimationClip? _castPreviewClip;
        private Renderer[] _castPreviewAvatarRenderers = Array.Empty<Renderer>();
        private Bounds _castPreviewFrameBounds;
        private bool _castPreviewHasFrameBounds;
        private string _castPreviewError = string.Empty;
        private float _castPreviewTime;
        private bool _castPreviewPlaying;
        private double _castPreviewLastEditorTime;
        private Vector2 _castPreviewOrbit = new(25f, -12f);
        private float _castPreviewDistanceMultiplier = 1f;

        private void DrawCastAnimationPreview(
            string spellId,
            SpellCastAnimationRecipe recipe)
        {
            string foldoutKey = $"Arena.SpellAuthoring.CastPreview.Visible.V2.{spellId}";
            bool expanded = SessionState.GetBool(foldoutKey, false);
            expanded = EditorGUILayout.Foldout(
                expanded,
                "Animation Preview",
                true,
                EditorStyles.foldoutHeader);
            SessionState.SetBool(foldoutKey, expanded);
            if (!expanded)
            {
                if (_castPreviewUtility != null || _castPreviewInstance != null)
                    DestroyCastAnimationPreview();
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                DestroyCastAnimationPreview();
                EditorGUILayout.HelpBox(
                    "Animation preview is disabled while entering or running Play Mode.",
                    MessageType.None);
                return;
            }

            CombatAnimationSet? previewSet = DrawCastPreviewAnimationSetPicker();
            List<CastPreviewClipOption> clipOptions = BuildCastPreviewClipOptions(recipe);
            if (clipOptions.Count == 0)
            {
                DestroyCastAnimationPreview();
                EditorGUILayout.HelpBox(
                    "The selected recipe has no playable clip to preview.",
                    MessageType.Warning);
                return;
            }

            string phaseKey =
                $"Arena.SpellAuthoring.CastPreview.Phase.{recipe.AnimationIdOrEmpty}";
            int selectedPhaseIndex = Mathf.Clamp(
                SessionState.GetInt(phaseKey, 0),
                0,
                clipOptions.Count - 1);
            string[] phaseLabels = new string[clipOptions.Count];
            for (int index = 0; index < clipOptions.Count; index++)
                phaseLabels[index] = clipOptions[index].Label;

            int nextPhaseIndex = EditorGUILayout.Popup(
                "Preview Phase",
                selectedPhaseIndex,
                phaseLabels);
            if (nextPhaseIndex != selectedPhaseIndex)
            {
                selectedPhaseIndex = nextPhaseIndex;
                SessionState.SetInt(phaseKey, selectedPhaseIndex);
                ResetCastAnimationPreview();
            }

            AnimationClip previewClip = clipOptions[selectedPhaseIndex].Clip;
            EnsureCastAnimationPreview(previewSet, previewClip);
            UpdateCastAnimationPreviewPlayback(previewClip);

            using (new EditorGUILayout.HorizontalScope())
            {
                string playLabel = _castPreviewPlaying ? "Pause" : "Play";
                if (GUILayout.Button(playLabel, GUILayout.Width(64f)))
                {
                    _castPreviewPlaying = !_castPreviewPlaying;
                    _castPreviewLastEditorTime = EditorApplication.timeSinceStartup;
                }

                if (GUILayout.Button("Restart", GUILayout.Width(70f)))
                {
                    _castPreviewTime = 0f;
                    SampleCastAnimationPreview();
                }

                const string loopKey = "Arena.SpellAuthoring.CastPreview.Loop";
                bool loop = SessionState.GetBool(loopKey, true);
                bool nextLoop = GUILayout.Toggle(loop, "Loop", "Button", GUILayout.Width(52f));
                if (nextLoop != loop)
                    SessionState.SetBool(loopKey, nextLoop);

                GUILayout.Label(
                    $"{_castPreviewTime:0.000}s / {previewClip.length:0.000}s",
                    GUILayout.Width(130f));
            }

            EditorGUI.BeginChangeCheck();
            float scrubbedTime = EditorGUILayout.Slider(
                "Timeline",
                _castPreviewTime,
                0f,
                Mathf.Max(0.001f, previewClip.length));
            if (EditorGUI.EndChangeCheck())
            {
                _castPreviewTime = scrubbedTime;
                _castPreviewPlaying = false;
                SampleCastAnimationPreview();
            }

            EditorGUILayout.HelpBox(
                DescribeCastPreviewPlayback(recipe),
                MessageType.None);
            EditorGUILayout.LabelField("Preview Viewport", EditorStyles.miniBoldLabel);
            Rect previewRect = GUILayoutUtility.GetRect(
                10f,
                CastPreviewHeight,
                GUILayout.ExpandWidth(true),
                GUILayout.MinHeight(CastPreviewHeight));
            DrawCastAnimationPreviewViewport(previewRect);
            HandleCastAnimationPreviewCameraInput(previewRect);

            if (_castPreviewPlaying)
                Repaint();
        }

        private CombatAnimationSet? DrawCastPreviewAnimationSetPicker()
        {
            EnsureAnimationSetsLoaded();
            if (_animationSets.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No CombatAnimationSet assets were found. The preview will use an unequipped avatar.",
                    MessageType.Warning);
                return null;
            }

            const string setKey = "Arena.SpellAuthoring.CastPreview.AnimationSet";
            int defaultSetIndex = 0;
            for (int index = 0; index < _animationSets.Length; index++)
            {
                if (string.Equals(
                        _animationSets[index].name,
                        "Staff",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    defaultSetIndex = index;
                    break;
                }
            }

            int selectedSetIndex = SessionState.GetInt(setKey, -1);
            if (selectedSetIndex < 0 || selectedSetIndex >= _animationSets.Length)
                selectedSetIndex = defaultSetIndex;
            string[] labels = new string[_animationSets.Length];
            for (int index = 0; index < _animationSets.Length; index++)
                labels[index] = _animationSets[index].name;

            int nextSetIndex = EditorGUILayout.Popup(
                "Preview Combat Set",
                selectedSetIndex,
                labels);
            if (nextSetIndex != selectedSetIndex)
            {
                selectedSetIndex = nextSetIndex;
                SessionState.SetInt(setKey, selectedSetIndex);
                ResetCastAnimationPreview();
            }

            return _animationSets[selectedSetIndex];
        }

        private static List<CastPreviewClipOption> BuildCastPreviewClipOptions(
            SpellCastAnimationRecipe recipe)
        {
            var options = new List<CastPreviewClipOption>();
            if (recipe.presentationMode != SpellAnimationPresentationMode.ReleaseOnly)
            {
                AddCastPreviewClipOption(options, "Hold Start", recipe.hold.enter);
                AddCastPreviewClipOption(options, "Hold Loop", recipe.hold.idleLoop);
            }

            AddCastPreviewClipOption(options, "Cast / Release", recipe.clip);
            return options;
        }

        private static void AddCastPreviewClipOption(
            List<CastPreviewClipOption> options,
            string label,
            AnimationClip? clip)
        {
            if (clip != null)
                options.Add(new CastPreviewClipOption($"{label}: {clip.name}", clip));
        }

        private static string DescribeCastPreviewPlayback(SpellCastAnimationRecipe recipe)
        {
            string layerDescription = recipe.playbackLayer switch
            {
                SpellPlaybackLayer.UpperBodyWhileMoving =>
                    "full body at rest and upper body when movement is active at release",
                SpellPlaybackLayer.FullBody => "full body",
                SpellPlaybackLayer.UpperBody => "upper body",
                SpellPlaybackLayer.LeftGesture => "left-side gesture layer",
                SpellPlaybackLayer.RightGesture => "right-side gesture layer",
                _ => recipe.playbackLayer.ToString(),
            };
            return $"{recipe.presentationMode}; runtime release playback is {layerDescription}. "
                + "This viewport shows the unmasked source clip so the full authored motion remains visible.";
        }

        private void EnsureCastAnimationPreview(
            CombatAnimationSet? animationSet,
            AnimationClip previewClip)
        {
            if (_castPreviewUtility != null
                && _castPreviewInstance != null
                && ReferenceEquals(_castPreviewAnimationSet, animationSet)
                && ReferenceEquals(_castPreviewClip, previewClip))
            {
                return;
            }

            DestroyCastAnimationPreview();
            _castPreviewAnimationSet = animationSet;
            _castPreviewClip = previewClip;
            _castPreviewTime = 0f;
            _castPreviewLastEditorTime = EditorApplication.timeSinceStartup;

            GameObject? prefab = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            if (prefab == null)
            {
                _castPreviewError = "Runtime avatar prefab could not be loaded.";
                return;
            }

            _castPreviewUtility = new PreviewRenderUtility();
            _castPreviewUtility.cameraFieldOfView = 30f;
            _castPreviewUtility.lights[0].intensity = 1.35f;
            _castPreviewUtility.lights[0].transform.rotation = Quaternion.Euler(45f, 35f, 0f);
            _castPreviewUtility.lights[1].intensity = 0.65f;
            _castPreviewUtility.lights[1].transform.rotation = Quaternion.Euler(340f, 220f, 0f);

            _castPreviewInstance = Instantiate(prefab);
            _castPreviewInstance.name = $"{prefab.name}_SpellAuthoringPreview";
            StarterAssetsRuntimeStripper.StripFrom(_castPreviewInstance);
            _castPreviewInstance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            RuntimeAvatarController? avatarController =
                _castPreviewInstance.GetComponent<RuntimeAvatarController>();
            if (avatarController == null)
                avatarController = _castPreviewInstance.AddComponent<RuntimeAvatarController>();
            avatarController.SetVisualRootParent(_castPreviewInstance.transform);

            CharacterAppearanceSelection appearance = CharacterAppearanceSelection.DefaultHumanMale();
            appearance.outfitId = string.Empty;
            string signature = RuntimeAvatarController.SignatureFor(appearance);
            if (!avatarController.Apply(
                    appearance,
                    signature,
                    out RuntimeAvatarBinding binding,
                    out string appearanceError))
            {
                _castPreviewError =
                    $"Runtime avatar appearance could not be assembled: {appearanceError}";
                Debug.LogWarning(
                    $"[{nameof(SpellAuthoringWindow)}] {_castPreviewError}",
                    _spellAnimationCatalog);
                DestroyImmediate(_castPreviewInstance);
                _castPreviewInstance = null;
                return;
            }

            PrepareCastPreviewAvatar(binding);
            _castPreviewFrameBounds = CalculateCastAnimationPreviewBounds(
                binding.Renderers,
                binding.AvatarRoot.transform.position);
            _castPreviewHasFrameBounds = true;

            WeaponAttachmentController? attachments =
                _castPreviewInstance.GetComponentInChildren<WeaponAttachmentController>(true);
            if (attachments != null)
            {
                attachments.Initialize();
                attachments.BindMounts(binding.Mounts);
                if (animationSet != null)
                    attachments.ApplyAnimationSet(animationSet);
                attachments.SetInCombat(true);
            }

            SetCastPreviewHideFlags(_castPreviewInstance, HideFlags.HideAndDontSave);
            _castPreviewAnimator = binding.Animator;
            CreateCastAnimationPreviewGraph(previewClip);
            _castPreviewUtility.AddSingleGO(_castPreviewInstance);
            SampleCastAnimationPreview();
        }

        private void ResetCastAnimationPreview()
        {
            DestroyCastAnimationPreview();
            _castPreviewTime = 0f;
        }

        private void DestroyCastAnimationPreview()
        {
            DestroyCastAnimationPreviewGraph();
            if (_castPreviewUtility != null)
            {
                _castPreviewUtility.Cleanup();
                _castPreviewUtility = null;
            }

            if (_castPreviewInstance != null)
                DestroyImmediate(_castPreviewInstance);

            _castPreviewInstance = null;
            _castPreviewAnimator = null;
            _castPreviewAnimationSet = null;
            _castPreviewClip = null;
            _castPreviewAvatarRenderers = Array.Empty<Renderer>();
            _castPreviewFrameBounds = default;
            _castPreviewHasFrameBounds = false;
            _castPreviewError = string.Empty;
            _castPreviewPlaying = false;
        }

        private void PrepareCastPreviewAvatar(RuntimeAvatarBinding binding)
        {
            _castPreviewAnimator = binding.Animator;
            _castPreviewAnimator.enabled = true;
            _castPreviewAnimator.applyRootMotion = false;
            _castPreviewAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _castPreviewAnimator.Rebind();
            _castPreviewAnimator.Update(0f);

            _castPreviewAvatarRenderers = binding.Renderers ?? Array.Empty<Renderer>();
            foreach (Renderer renderer in _castPreviewAvatarRenderers)
            {
                if (renderer == null)
                    continue;

                renderer.enabled = true;
                if (renderer is SkinnedMeshRenderer skinnedRenderer)
                {
                    skinnedRenderer.updateWhenOffscreen = true;
                    skinnedRenderer.forceMatrixRecalculationPerRender = true;
                }
            }
        }

        private void CreateCastAnimationPreviewGraph(AnimationClip previewClip)
        {
            if (_castPreviewAnimator == null)
                return;

            _castPreviewGraph = PlayableGraph.Create("SpellAuthoringWindowPreview");
            _castPreviewGraphCreated = _castPreviewGraph.IsValid();
            _castPreviewGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _castPreviewPlayable = AnimationClipPlayable.Create(_castPreviewGraph, previewClip);
            _castPreviewPlayable.SetApplyFootIK(false);
            _castPreviewPlayable.SetApplyPlayableIK(false);
            AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                _castPreviewGraph,
                "PreviewAnimation",
                _castPreviewAnimator);
            output.SetSourcePlayable(_castPreviewPlayable);
            _castPreviewGraph.Play();
        }

        private void DestroyCastAnimationPreviewGraph()
        {
            if (_castPreviewGraph.IsValid())
                _castPreviewGraph.Destroy();
            _castPreviewGraphCreated = false;
        }

        private void UpdateCastAnimationPreviewPlayback(AnimationClip previewClip)
        {
            if (!_castPreviewPlaying)
                return;

            if (previewClip.length <= 0f)
            {
                _castPreviewTime = 0f;
                _castPreviewPlaying = false;
                SampleCastAnimationPreview();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float delta = Mathf.Max(0f, (float)(now - _castPreviewLastEditorTime));
            _castPreviewLastEditorTime = now;
            _castPreviewTime += delta;
            bool loop = SessionState.GetBool("Arena.SpellAuthoring.CastPreview.Loop", true);
            if (_castPreviewTime > previewClip.length)
                _castPreviewTime = loop
                    ? Mathf.Repeat(_castPreviewTime, previewClip.length)
                    : previewClip.length;
            if (!loop && Mathf.Approximately(_castPreviewTime, previewClip.length))
                _castPreviewPlaying = false;

            SampleCastAnimationPreview();
        }

        private void SampleCastAnimationPreview()
        {
            if (_castPreviewInstance == null || _castPreviewClip == null)
                return;

            _castPreviewTime = Mathf.Clamp(
                _castPreviewTime,
                0f,
                Mathf.Max(0f, _castPreviewClip.length));
            if (_castPreviewGraphCreated && _castPreviewPlayable.IsValid())
            {
                _castPreviewPlayable.SetTime(_castPreviewTime);
                _castPreviewGraph.Evaluate(0f);
            }
            else
            {
                GameObject sampleRoot = _castPreviewAnimator != null
                    ? _castPreviewAnimator.gameObject
                    : _castPreviewInstance;
                _castPreviewClip.SampleAnimation(sampleRoot, _castPreviewTime);
            }
        }

        private void DrawCastAnimationPreviewViewport(Rect previewRect)
        {
            if (_castPreviewUtility == null || _castPreviewInstance == null)
            {
                string message = string.IsNullOrWhiteSpace(_castPreviewError)
                    ? "Runtime avatar preview is not available."
                    : _castPreviewError;
                EditorGUI.HelpBox(previewRect, message, MessageType.Warning);
                return;
            }

            if (Event.current.type != EventType.Repaint)
                return;

            ConfigureCastAnimationPreviewCamera();
            _castPreviewUtility.BeginPreview(previewRect, GUIStyle.none);
            _castPreviewUtility.Render(true);
            Texture texture = _castPreviewUtility.EndPreview();
            GUI.DrawTexture(previewRect, texture, ScaleMode.StretchToFill, false);
        }

        private void ConfigureCastAnimationPreviewCamera()
        {
            if (_castPreviewUtility == null || _castPreviewInstance == null)
                return;

            Bounds bounds = _castPreviewHasFrameBounds
                ? _castPreviewFrameBounds
                : CalculateCastAnimationPreviewBounds(
                    _castPreviewAvatarRenderers,
                    _castPreviewInstance.transform.position);
            Camera camera = _castPreviewUtility.camera;
            camera.clearFlags = CameraClearFlags.Color;
            camera.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1f);
            float radius = Mathf.Max(0.75f, bounds.extents.magnitude);
            float distance = radius * 2.8f * Mathf.Clamp(
                _castPreviewDistanceMultiplier,
                CastPreviewMinDistance,
                CastPreviewMaxDistance);
            Quaternion orbit = Quaternion.Euler(_castPreviewOrbit.y, _castPreviewOrbit.x, 0f);
            Vector3 center = bounds.center + Vector3.up * Mathf.Min(0.4f, radius * 0.2f);
            Vector3 forward = orbit * Vector3.forward;
            camera.transform.position = center - forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = Mathf.Max(50f, distance + radius * 4f);
        }

        private static Bounds CalculateCastAnimationPreviewBounds(
            IReadOnlyList<Renderer> renderers,
            Vector3 fallbackOrigin)
        {
            bool hasBounds = false;
            Bounds bounds = default;
            for (int index = 0; index < renderers.Count; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer == null || !renderer.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds
                ? bounds
                : new Bounds(fallbackOrigin + Vector3.up, Vector3.one * 2f);
        }

        private void HandleCastAnimationPreviewCameraInput(Rect previewRect)
        {
            Event current = Event.current;
            if (!previewRect.Contains(current.mousePosition))
                return;

            if (current.type == EventType.MouseDrag && current.button == 0)
            {
                _castPreviewOrbit.x += current.delta.x;
                _castPreviewOrbit.y = Mathf.Clamp(
                    _castPreviewOrbit.y + current.delta.y,
                    -80f,
                    80f);
                current.Use();
                Repaint();
            }
            else if (current.type == EventType.ScrollWheel)
            {
                _castPreviewDistanceMultiplier = Mathf.Clamp(
                    _castPreviewDistanceMultiplier + current.delta.y * 0.05f,
                    CastPreviewMinDistance,
                    CastPreviewMaxDistance);
                current.Use();
                Repaint();
            }
        }

        private static void SetCastPreviewHideFlags(GameObject root, HideFlags flags)
        {
            root.hideFlags = flags;
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform child in children)
                child.gameObject.hideFlags = flags;
        }
    }
}
