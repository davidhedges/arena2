#nullable enable

using System;
using System.Collections.Generic;
using Arena.Input;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal sealed partial class SpellAuthoringWindow
    {
        private const float CastPreviewHeight = 260f;
        private const float CastPreviewMinDistance = 0.5f;
        private const float CastPreviewMaxDistance = 2.5f;
        private const int CastPreviewSpellActionLayerIndex = 4;
        private const string CastPreviewControllerPath =
            "Assets/Arena/Content/Animation/Arena_Character.controller";
        private static readonly int CastPreviewStateHash =
            Animator.StringToHash("SpellAction.SpellCastHoldAction1");
        private static readonly int CastPreviewMirrorHash =
            Animator.StringToHash("MirrorSpellAction");

        private readonly struct CastPreviewClipOption
        {
            public CastPreviewClipOption(string label, AnimationClip clip, bool isTimeline = false)
            {
                Label = label;
                Clip = clip;
                IsTimeline = isTimeline;
            }

            public string Label { get; }
            public AnimationClip Clip { get; }
            public bool IsTimeline { get; }
        }

        private readonly struct CastPreviewTimeline
        {
            public CastPreviewTimeline(
                SpellCastHoldProfile leadIn,
                AnimationClip release,
                float castDurationSeconds,
                float releaseOffsetSeconds)
            {
                Enter = leadIn.EnterOrIdle;
                Loop = leadIn.IdleOrEnter;
                Release = release;
                CastDurationSeconds = Mathf.Max(0f, castDurationSeconds);
                ReleaseOffsetSeconds = Mathf.Clamp(
                    releaseOffsetSeconds,
                    0f,
                    Mathf.Max(0f, release.length));
                ReleaseStartsAtSeconds = Mathf.Max(
                    0f,
                    CastDurationSeconds - ReleaseOffsetSeconds);
                ReleasePlaybackStartOffsetSeconds = Mathf.Max(
                    0f,
                    ReleaseOffsetSeconds - CastDurationSeconds);
                EnterDurationSeconds = Enter != null
                    ? Mathf.Min(
                        Enter.length,
                        Enter.length * leadIn.ResolveEnterCompleteNormalizedTime(0.85f))
                    : 0f;
                DurationSeconds = Mathf.Max(
                    CastDurationSeconds,
                    ReleaseStartsAtSeconds
                    + Mathf.Max(0.001f, release.length - ReleasePlaybackStartOffsetSeconds));
            }

            public AnimationClip? Enter { get; }
            public AnimationClip? Loop { get; }
            public AnimationClip Release { get; }
            public float CastDurationSeconds { get; }
            public float ReleaseOffsetSeconds { get; }
            public float ReleaseStartsAtSeconds { get; }
            public float ReleasePlaybackStartOffsetSeconds { get; }
            public float EnterDurationSeconds { get; }
            public float DurationSeconds { get; }

            public void ResolveSample(
                float timelineTime,
                out AnimationClip clip,
                out float clipTime,
                out string phase)
            {
                float time = Mathf.Clamp(timelineTime, 0f, DurationSeconds);
                if (time >= ReleaseStartsAtSeconds)
                {
                    clip = Release;
                    clipTime = Mathf.Clamp(
                        ReleasePlaybackStartOffsetSeconds + time - ReleaseStartsAtSeconds,
                        0f,
                        Mathf.Max(0f, Release.length));
                    phase = "Release";
                    return;
                }

                if (Enter != null && (Loop == null || time < EnterDurationSeconds))
                {
                    clip = Enter;
                    clipTime = Mathf.Clamp(time, 0f, Mathf.Max(0f, Enter.length));
                    phase = "Aim Start";
                    return;
                }

                if (Loop != null)
                {
                    clip = Loop;
                    float loopTime = Mathf.Max(0f, time - EnterDurationSeconds);
                    clipTime = Loop.length > 0.001f
                        ? Mathf.Repeat(loopTime, Loop.length)
                        : 0f;
                    phase = "Aim Loop";
                    return;
                }

                clip = Release;
                clipTime = 0f;
                phase = "Waiting (no lead-in)";
            }
        }

        private PreviewRenderUtility? _castPreviewUtility;
        private GameObject? _castPreviewInstance;
        private Animator? _castPreviewAnimator;
        private AnimatorOverrideController? _castPreviewOverrideController;
        private CombatAnimationSet? _castPreviewAnimationSet;
        private AnimationClip? _castPreviewPrimaryClip;
        private AnimationClip? _castPreviewClip;
        private CastPreviewTimeline? _castPreviewTimeline;
        private bool _castPreviewMirrored;
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
            SpellCastAnimationRecipe recipe,
            int authoredCastTimeMs)
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
            bool mirrored = previewSet != null
                && previewSet.TryGetSpellCastAnimationOverride(
                    spellId,
                    out SpellCastAnimationOverride animationOverride)
                && animationOverride.mirrorPresentation;
            SpellCastHoldProfile effectiveLeadIn = ResolveCastPreviewLeadIn(recipe);
            string castDurationKey =
                $"Arena.SpellAuthoring.CastPreview.Duration.{spellId}";
            float authoredCastDurationSeconds = Mathf.Max(0f, authoredCastTimeMs / 1000f);
            float simulatedCastDurationSeconds = SessionState.GetFloat(
                castDurationKey,
                authoredCastDurationSeconds);
            EditorGUI.BeginChangeCheck();
            simulatedCastDurationSeconds = Mathf.Max(
                0f,
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Simulated Cast Duration",
                        "Preview-only duration. Runtime still uses the authoritative cast-time and cast-speed-scaled ActiveCast window."),
                    simulatedCastDurationSeconds));
            if (EditorGUI.EndChangeCheck())
            {
                SessionState.SetFloat(castDurationKey, simulatedCastDurationSeconds);
                ResetCastAnimationPreview();
            }

            List<CastPreviewClipOption> clipOptions = BuildCastPreviewClipOptions(
                recipe,
                effectiveLeadIn);
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

            CastPreviewClipOption selectedOption = clipOptions[selectedPhaseIndex];
            AnimationClip previewClip = selectedOption.Clip;
            CastPreviewTimeline? timeline = null;
            AnimationClip initialClip = previewClip;
            if (selectedOption.IsTimeline)
            {
                var builtTimeline = new CastPreviewTimeline(
                    effectiveLeadIn,
                    previewClip,
                    simulatedCastDurationSeconds,
                    ResolveCastPreviewReleaseOffsetSeconds(recipe));
                timeline = builtTimeline;
                builtTimeline.ResolveSample(0f, out initialClip, out _, out _);
            }

            EnsureCastAnimationPreview(
                previewSet,
                previewClip,
                initialClip,
                mirrored,
                timeline);
            UpdateCastAnimationPreviewPlayback();

            SpellCastOrigin effectiveOrigin = ResolvePreviewCastOrigin(
                recipe.castOrigin,
                mirrored);
            EditorGUILayout.LabelField("Natural Cast Origin", DescribeCastOrigin(recipe.castOrigin));
            EditorGUILayout.LabelField(
                "Preview Cast Origin",
                $"{DescribeCastOrigin(effectiveOrigin)}{(mirrored ? " (mirrored)" : string.Empty)}");

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
                    $"{_castPreviewTime:0.000}s / {ResolveCastPreviewDuration():0.000}s",
                    GUILayout.Width(130f));
            }

            EditorGUI.BeginChangeCheck();
            float scrubbedTime = EditorGUILayout.Slider(
                "Timeline",
                _castPreviewTime,
                0f,
                Mathf.Max(0.001f, ResolveCastPreviewDuration()));
            if (EditorGUI.EndChangeCheck())
            {
                _castPreviewTime = scrubbedTime;
                _castPreviewPlaying = false;
                SampleCastAnimationPreview();
            }

            if (timeline.HasValue)
            {
                CastPreviewTimeline activeTimeline = timeline.Value;
                activeTimeline.ResolveSample(
                    _castPreviewTime,
                    out _,
                    out _,
                    out string phase);
                EditorGUILayout.LabelField("Timeline Phase", phase);
                EditorGUILayout.LabelField(
                    "Release Animation Starts",
                    $"{activeTimeline.ReleaseStartsAtSeconds:0.000}s");
                EditorGUILayout.LabelField(
                    "Gameplay / VFX Release",
                    $"{activeTimeline.CastDurationSeconds:0.000}s (OnReleaseFrame)");
                if (activeTimeline.ReleasePlaybackStartOffsetSeconds > 0.001f)
                {
                    EditorGUILayout.HelpBox(
                        $"This cast is shorter than the release wind-up, so runtime enters {activeTimeline.ReleasePlaybackStartOffsetSeconds:0.000}s into the release clip. Its OnReleaseFrame still lands at cast completion.",
                        MessageType.Info);
                }
            }

            EditorGUILayout.HelpBox(
                DescribeCastPreviewPlayback(recipe, effectiveLeadIn, timeline.HasValue),
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

        private SpellCastHoldProfile ResolveCastPreviewLeadIn(
            in SpellCastAnimationRecipe recipe)
        {
            if (recipe.presentationMode == SpellAnimationPresentationMode.ReleaseOnly)
            {
                return _spellAnimationCatalog != null
                    ? _spellAnimationCatalog.ResolveCastTimeLeadIn(recipe)
                    : recipe.ResolveCastTimeLeadIn(default);
            }

            return recipe.hold;
        }

        private static float ResolveCastPreviewReleaseOffsetSeconds(
            in SpellCastAnimationRecipe recipe)
        {
            var entry = new WeaponSpellAnimationEntry
            {
                spellId = recipe.AnimationIdOrEmpty,
                clip = recipe.clip,
            };
            return entry.ResolveReleaseOffsetSeconds();
        }

        private static List<CastPreviewClipOption> BuildCastPreviewClipOptions(
            SpellCastAnimationRecipe recipe,
            SpellCastHoldProfile effectiveLeadIn)
        {
            var options = new List<CastPreviewClipOption>();
            bool playsRelease = recipe.presentationMode == SpellAnimationPresentationMode.ReleaseOnly
                || recipe.presentationMode == SpellAnimationPresentationMode.HoldThenRelease;
            if (playsRelease && recipe.clip != null)
            {
                options.Add(new CastPreviewClipOption(
                    $"Full Cast Timeline: {recipe.DisplayNameOrId}",
                    recipe.clip,
                    isTimeline: true));
            }

            if (effectiveLeadIn.HasAny)
            {
                AddCastPreviewClipOption(options, "Cast Lead-In Start", effectiveLeadIn.enter);
                AddCastPreviewClipOption(options, "Cast Lead-In Loop", effectiveLeadIn.idleLoop);
            }

            string primaryLabel = recipe.presentationMode == SpellAnimationPresentationMode.HoldWithPulse
                ? "Pulse Attack"
                : "Cast / Release";
            AddCastPreviewClipOption(options, primaryLabel, recipe.clip);
            AddCastPreviewClipOption(options, "Return to Hold", recipe.returnToHold);
            AddCastPreviewClipOption(options, "Hold Exit / Cancel", effectiveLeadIn.exit);
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

        private static string DescribeCastPreviewPlayback(
            SpellCastAnimationRecipe recipe,
            SpellCastHoldProfile effectiveLeadIn,
            bool isTimeline)
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
            string exitDescription = effectiveLeadIn.exit != null
                ? $" Cancellation before release uses {effectiveLeadIn.exit.name}."
                : string.Empty;
            string lifecycleDescription = recipe.presentationMode switch
            {
                SpellAnimationPresentationMode.HoldWithPulse =>
                    "pulse attack temporarily leaves the hold and Return to Hold resumes its loop",
                SpellAnimationPresentationMode.HoldOnly =>
                    "the hold loop persists until the channel ends",
                _ => $"runtime release playback is {layerDescription}",
            };
            string timelineDescription = isTimeline
                ? " The cast bar continues through the release wind-up and completes at the clip's OnReleaseFrame marker."
                : string.Empty;
            return $"{recipe.presentationMode}; {lifecycleDescription}.{exitDescription}{timelineDescription} "
                + "This viewport uses the canonical full-body spell state, including the selected CombatAnimationSet mirror, so the humanoid and equipped weapon follow runtime playback.";
        }

        private void EnsureCastAnimationPreview(
            CombatAnimationSet? animationSet,
            AnimationClip primaryClip,
            AnimationClip initialClip,
            bool mirrored,
            CastPreviewTimeline? timeline)
        {
            if (_castPreviewUtility != null
                && _castPreviewInstance != null
                && ReferenceEquals(_castPreviewAnimationSet, animationSet)
                && ReferenceEquals(_castPreviewPrimaryClip, primaryClip)
                && _castPreviewMirrored == mirrored
                && _castPreviewTimeline.HasValue == timeline.HasValue)
            {
                _castPreviewTimeline = timeline;
                return;
            }

            DestroyCastAnimationPreview();
            _castPreviewAnimationSet = animationSet;
            _castPreviewPrimaryClip = primaryClip;
            _castPreviewClip = initialClip;
            _castPreviewTimeline = timeline;
            _castPreviewMirrored = mirrored;
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
            CreateCastAnimationPreviewController(initialClip);
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
            DestroyCastAnimationPreviewController();
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
            _castPreviewPrimaryClip = null;
            _castPreviewClip = null;
            _castPreviewTimeline = null;
            _castPreviewMirrored = false;
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

        private void CreateCastAnimationPreviewController(AnimationClip previewClip)
        {
            if (_castPreviewAnimator == null)
                return;

            RuntimeAnimatorController? controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                CastPreviewControllerPath);
            if (controller == null)
            {
                _castPreviewError = $"Canonical animator controller is missing at {CastPreviewControllerPath}.";
                return;
            }

            _castPreviewOverrideController = new AnimatorOverrideController(controller)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _castPreviewOverrideController["slot_spell_1"] = previewClip;
            _castPreviewAnimator.runtimeAnimatorController = _castPreviewOverrideController;
            _castPreviewAnimator.Rebind();
            _castPreviewAnimator.SetBool(CastPreviewMirrorHash, _castPreviewMirrored);
            _castPreviewAnimator.SetLayerWeight(CastPreviewSpellActionLayerIndex, 1f);
        }

        private void DestroyCastAnimationPreviewController()
        {
            if (_castPreviewOverrideController != null)
                DestroyImmediate(_castPreviewOverrideController);
            _castPreviewOverrideController = null;
        }

        private void UpdateCastAnimationPreviewPlayback()
        {
            if (!_castPreviewPlaying)
                return;

            float previewDuration = ResolveCastPreviewDuration();
            if (previewDuration <= 0f)
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
            if (_castPreviewTime > previewDuration)
                _castPreviewTime = loop
                    ? Mathf.Repeat(_castPreviewTime, previewDuration)
                    : previewDuration;
            if (!loop && Mathf.Approximately(_castPreviewTime, previewDuration))
                _castPreviewPlaying = false;

            SampleCastAnimationPreview();
        }

        private void SampleCastAnimationPreview()
        {
            if (_castPreviewInstance == null || _castPreviewClip == null)
                return;

            float previewDuration = ResolveCastPreviewDuration();
            _castPreviewTime = Mathf.Clamp(
                _castPreviewTime,
                0f,
                Mathf.Max(0f, previewDuration));
            if (_castPreviewAnimator == null || _castPreviewOverrideController == null)
                return;

            AnimationClip sampleClip = _castPreviewClip;
            float sampleTime = _castPreviewTime;
            if (_castPreviewTimeline.HasValue)
            {
                _castPreviewTimeline.Value.ResolveSample(
                    _castPreviewTime,
                    out sampleClip,
                    out sampleTime,
                    out _);
                BindCastAnimationPreviewClip(sampleClip);
            }

            float normalizedTime = sampleClip.length > 0.001f
                ? Mathf.Clamp01(sampleTime / sampleClip.length)
                : 0f;
            _castPreviewAnimator.SetBool(CastPreviewMirrorHash, _castPreviewMirrored);
            _castPreviewAnimator.SetLayerWeight(CastPreviewSpellActionLayerIndex, 1f);
            _castPreviewAnimator.Play(
                CastPreviewStateHash,
                CastPreviewSpellActionLayerIndex,
                normalizedTime);
            _castPreviewAnimator.Update(0f);
        }

        private float ResolveCastPreviewDuration()
        {
            if (_castPreviewTimeline.HasValue)
                return _castPreviewTimeline.Value.DurationSeconds;

            return _castPreviewClip != null ? Mathf.Max(0f, _castPreviewClip.length) : 0f;
        }

        private void BindCastAnimationPreviewClip(AnimationClip clip)
        {
            if (_castPreviewAnimator == null
                || _castPreviewOverrideController == null
                || ReferenceEquals(_castPreviewClip, clip))
            {
                return;
            }

            _castPreviewOverrideController["slot_spell_1"] = clip;
            _castPreviewClip = clip;
            _castPreviewAnimator.Rebind();
            _castPreviewAnimator.SetBool(CastPreviewMirrorHash, _castPreviewMirrored);
            _castPreviewAnimator.SetLayerWeight(CastPreviewSpellActionLayerIndex, 1f);
        }

        private static SpellCastOrigin ResolvePreviewCastOrigin(
            SpellCastOrigin naturalOrigin,
            bool mirrored)
        {
            if (!mirrored)
                return naturalOrigin;

            return naturalOrigin switch
            {
                SpellCastOrigin.LeftHand => SpellCastOrigin.RightHand,
                SpellCastOrigin.RightHand => SpellCastOrigin.LeftHand,
                _ => SpellCastOrigin.UseVfxCue,
            };
        }

        private static string DescribeCastOrigin(SpellCastOrigin castOrigin)
        {
            return castOrigin switch
            {
                SpellCastOrigin.LeftHand => "Left Hand",
                SpellCastOrigin.RightHand => "Right Hand",
                _ => "Use legacy VFX cue",
            };
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
