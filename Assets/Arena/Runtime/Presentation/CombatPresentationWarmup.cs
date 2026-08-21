#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Arena.Presentation.VFX;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.VFX;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Arena.Presentation
{
    /// <summary>
    /// Populates immutable combat-presentation caches before gameplay can put
    /// their first-use costs on an input frame. The cache is client-only and
    /// deliberately survives scene and disposable-match database changes.
    /// </summary>
    [DefaultExecutionOrder(-1_000)]
    internal sealed class CombatPresentationWarmup : MonoBehaviour
    {
        private const double AnimationFrameBudgetMilliseconds = 2d;
        private const int WarmupRenderLayer = 31;

        private static CombatPresentationWarmup? s_instance;
        private static bool s_assetsReady;
        private static bool s_complete;
        private static int s_warmedAnimationClipCount;
        private static int s_warmedVfxPrefabCount;

        private readonly List<CombatAnimationSet> _animationSets = new();
        private readonly HashSet<AnimationClip> _animationClips = new();
        private SharedActionProfile? _sharedActionProfile;
        private SpellCastAnimationLibrary? _spellLibrary;
        private SpellCastAnimationMap? _spellMap;
        private CombatVFXRegistry? _vfxRegistry;
        private GameObject? _runtimeAvatarPrefab;
        private Coroutine? _warmupCoroutine;

        internal static bool AssetsReady => s_assetsReady;
        internal static bool IsComplete => s_complete;
        internal static int WarmedAnimationClipCount => s_warmedAnimationClipCount;
        internal static int WarmedVfxPrefabCount => s_warmedVfxPrefabCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_instance = null;
            s_assetsReady = false;
            s_complete = false;
            s_warmedAnimationClipCount = 0;
            s_warmedVfxPrefabCount = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            EnsureInstance();
        }

        internal static CombatPresentationWarmup EnsureInstance()
        {
            if (s_instance != null)
                return s_instance;

            CombatPresentationWarmup? existing = FindAnyObjectByType<CombatPresentationWarmup>();
            if (existing != null)
            {
                s_instance = existing;
                return existing;
            }

            var host = new GameObject(nameof(CombatPresentationWarmup));
            return host.AddComponent<CombatPresentationWarmup>();
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);

            // Resolve immutable dependencies before input can dispatch. Presentation
            // entry points keep synchronous fallbacks for robustness, but ordinary
            // play should never need to take those first-use paths.
            LoadAndRetainPresentationAssets();
            _warmupCoroutine = StartCoroutine(WarmRuntimeStateProgressively());
        }

        private void LoadAndRetainPresentationAssets()
        {
            _animationSets.Clear();
            foreach (CombatAnimationSet set in Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets"))
            {
                CombatAnimationSetCatalog.RegisterPreloaded(set);
                _animationSets.Add(set);
            }

            _sharedActionProfile = Resources.Load<SharedActionProfile>("ActionProfiles/SharedActions");
            _spellLibrary = Resources.Load<SpellCastAnimationLibrary>(SpellCastAnimationResolver.LibraryResource);
            _spellMap = Resources.Load<SpellCastAnimationMap>(SpellCastAnimationResolver.MapResource);
            SpellCastAnimationResolver.RegisterPreloaded(_spellLibrary, _spellMap);

            _vfxRegistry = Resources.Load<CombatVFXRegistry>(CombatVFXRegistry.RegistryResourcePath);
            CombatVFXRegistry.RegisterPreloaded(_vfxRegistry);

            _runtimeAvatarPrefab = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            s_assetsReady = true;
        }

        private IEnumerator WarmRuntimeStateProgressively()
        {
            yield return WarmRegisteredVfxPrefabs();
            yield return WarmAnimationClips();

            s_complete = true;
            _warmupCoroutine = null;
            Debug.Log(
                $"[CombatPresentationWarmup] Ready. animations={s_warmedAnimationClipCount} "
                + $"vfx_prefabs={s_warmedVfxPrefabCount}.");
        }

        private IEnumerator WarmAnimationClips()
        {
            _animationClips.Clear();
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            foreach (CombatAnimationSet set in _animationSets)
                CollectAnimationClipsForWarmup(set, _animationClips, visited);
            if (_sharedActionProfile != null)
                CollectAnimationClipsForWarmup(_sharedActionProfile, _animationClips, visited);
            if (_spellLibrary != null)
                CollectAnimationClipsForWarmup(_spellLibrary, _animationClips, visited);
            if (_spellMap != null)
                CollectAnimationClipsForWarmup(_spellMap, _animationClips, visited);

            GameObject? animationStage = null;
            GameObject? avatar = null;
            Animator? animator = null;
            AnimatorOverrideController? overrideController = null;
            if (_runtimeAvatarPrefab != null)
            {
                animationStage = new GameObject("CombatAnimationWarmupStage");
                animationStage.hideFlags = HideFlags.HideAndDontSave;
                animationStage.SetActive(false);
                animationStage.transform.SetParent(transform, false);
                avatar = Instantiate(_runtimeAvatarPrefab, animationStage.transform, false);
                avatar.name = "CombatAnimationWarmupAvatar";
                avatar.hideFlags = HideFlags.HideAndDontSave;

                animator = avatar.GetComponentInChildren<Animator>(true);
                if (animator?.runtimeAnimatorController != null)
                {
                    foreach (AnimationClip controllerClip in animator.runtimeAnimatorController.animationClips)
                    {
                        if (controllerClip != null)
                            _animationClips.Add(controllerClip);
                    }

                    overrideController = new AnimatorOverrideController(animator.runtimeAnimatorController);
                    animator.runtimeAnimatorController = overrideController;
                    animator.applyRootMotion = false;
                    animator.fireEvents = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }

                DisableAnimationWarmupSideEffects(avatar);
                animationStage.SetActive(true);
                animator?.Rebind();
                animator?.Update(0f);
            }

            var clips = new List<AnimationClip>(_animationClips);
            var stopwatch = new Stopwatch();
            int index = 0;
            int failureCount = 0;
            while (index < clips.Count)
            {
                stopwatch.Restart();
                do
                {
                    AnimationClip clip = clips[index++];
                    try
                    {
                        WarmAnimationClip(avatar, animator, overrideController, clip, index);
                        s_warmedAnimationClipCount++;
                    }
                    catch (Exception exception)
                    {
                        failureCount++;
                        if (failureCount <= 3)
                        {
                            Debug.LogWarning(
                                $"[CombatPresentationWarmup] Could not warm animation clip '{clip.name}': "
                                + exception.Message);
                        }
                    }
                }
                while (index < clips.Count
                       && stopwatch.Elapsed.TotalMilliseconds < AnimationFrameBudgetMilliseconds);

                yield return null;
            }

            if (avatar != null)
                Destroy(avatar);
            if (overrideController != null)
                Destroy(overrideController);
            if (animationStage != null)
                Destroy(animationStage);
            if (failureCount > 0)
                Debug.LogWarning($"[CombatPresentationWarmup] Animation warm-up skipped {failureCount} clips after errors.");
        }

        private static void WarmAnimationClip(
            GameObject? avatar,
            Animator? animator,
            AnimatorOverrideController? overrideController,
            AnimationClip clip,
            int index)
        {
            if (avatar == null)
            {
                _ = clip.length;
                return;
            }

            // SampleAnimation forces Unity to resolve/decode the clip against
            // the same hierarchy used by runtime players without firing events.
            clip.SampleAnimation(avatar, 0f);
            if (overrideController == null)
                return;

            int bankSlot = ((index - 1) % CombatAnimationSet.AnimatorStrikeBankCount) + 1;
            overrideController[$"slot_strike_{bankSlot}"] = clip;
            overrideController[$"slot_spell_{bankSlot}"] = clip;

            int strikeState = Animator.StringToHash($"MeleeAttack.Strike{bankSlot}");
            const int meleeAttackLayer = 3;
            if (animator != null && animator.HasState(meleeAttackLayer, strikeState))
            {
                animator.Play(strikeState, meleeAttackLayer, 0f);
                animator.Update(0f);
            }
        }

        private static void DisableAnimationWarmupSideEffects(GameObject avatar)
        {
            foreach (MonoBehaviour behaviour in avatar.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null)
                    behaviour.enabled = false;
            }
            foreach (Renderer renderer in avatar.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
            foreach (AudioSource source in avatar.GetComponentsInChildren<AudioSource>(true))
                source.enabled = false;
            foreach (Collider collider in avatar.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        }

        private IEnumerator WarmRegisteredVfxPrefabs()
        {
            if (_vfxRegistry == null)
                yield break;

            var templates = new List<CombatVFXRegistry.Template>();
            var prefabs = new HashSet<GameObject>();
            foreach (CombatVFXRegistry.Entry entry in _vfxRegistry.Entries)
            {
                CombatVFXRegistry.Template? template = _vfxRegistry.ResolveTemplate(entry.vfxId);
                if (template?.Prefab != null && prefabs.Add(template.Prefab))
                    templates.Add(template);
            }

            GameObject stage = new("CombatVfxWarmupStage");
            stage.hideFlags = HideFlags.HideAndDontSave;
            stage.SetActive(false);
            stage.transform.SetParent(transform, false);

            GameObject cameraObject = new("CombatVfxWarmupCamera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            cameraObject.transform.SetParent(transform, false);
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 0f, -10f), Quaternion.identity);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.cullingMask = 1 << WarmupRenderLayer;
            camera.orthographic = true;
            camera.orthographicSize = 12f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            camera.allowHDR = false;
            camera.allowMSAA = false;

            // URP RenderGraph requires an output render texture with both an
            // explicit color format and a depth-stencil format.
            var renderTexture = new RenderTexture(
                16,
                16,
                GraphicsFormat.R8G8B8A8_SRGB,
                CoreUtils.GetDefaultDepthOnlyFormat())
            {
                name = "CombatVfxWarmupTarget",
                hideFlags = HideFlags.HideAndDontSave,
            };
            renderTexture.Create();
            camera.targetTexture = renderTexture;

            int failureCount = 0;
            bool renderSubmissionEnabled = true;
            foreach (CombatVFXRegistry.Template template in templates)
            {
                GameObject? instance = null;
                try
                {
                    instance = Instantiate(template.Prefab, stage.transform, false);
                    instance.name = $"Warm_{template.VfxId}";
                    instance.hideFlags = HideFlags.HideAndDontSave;
                    instance.transform.SetLocalPositionAndRotation(Vector3.zero, template.LocalRotation);
                    instance.transform.localScale = Vector3.one;
                    VFXUtils.ApplyPrefabPresentationScale(instance, template.Scale);
                    if (template.FollowAuthoritativeProjectileMotion)
                        VFXUtils.ApplyAuthoritativeProjectileParticleMotion(instance);
                    SetLayerRecursively(instance, WarmupRenderLayer);

                    foreach (AudioSource source in instance.GetComponentsInChildren<AudioSource>(true))
                        source.enabled = false;
                    foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                        collider.enabled = false;
                    foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (behaviour != null)
                            behaviour.enabled = false;
                    }

                    // Scripts, audio, and colliders stay disabled so activation only
                    // exercises renderer, particle, and VFX Graph runtime state.
                    instance.SetActive(true);
                    stage.SetActive(true);
                    foreach (ParticleSystem particles in instance.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        particles.Simulate(1f / 60f, true, true, false);
                        particles.Play(true);
                    }
                    foreach (VisualEffect visualEffect in instance.GetComponentsInChildren<VisualEffect>(true))
                    {
                        visualEffect.Reinit();
                        visualEffect.Play();
                    }

                    if (renderSubmissionEnabled)
                    {
                        try
                        {
                            RenderWarmupCamera(camera, renderTexture);
                        }
                        catch (Exception exception)
                        {
                            // A render-pipeline incompatibility must not be retried
                            // once per prefab or poison the rest of the warm-up.
                            renderSubmissionEnabled = false;
                            failureCount++;
                            Debug.LogWarning(
                                "[CombatPresentationWarmup] GPU render warm-up disabled after one failure: "
                                + exception.Message);
                        }
                    }
                    s_warmedVfxPrefabCount++;
                }
                catch (Exception exception)
                {
                    failureCount++;
                    if (failureCount <= 3)
                    {
                        Debug.LogWarning(
                            $"[CombatPresentationWarmup] Could not warm VFX '{template.VfxId}': "
                            + exception.Message);
                    }
                }
                finally
                {
                    stage.SetActive(false);
                    if (instance != null)
                    {
                        instance.SetActive(false);
                        Destroy(instance);
                    }
                }

                yield return null;
            }

            camera.targetTexture = null;
            renderTexture.Release();
            Destroy(renderTexture);
            Destroy(cameraObject);
            Destroy(stage);
            if (failureCount > 0)
                Debug.LogWarning($"[CombatPresentationWarmup] VFX warm-up skipped {failureCount} prefabs after errors.");
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        private static void RenderWarmupCamera(Camera camera, RenderTexture destination)
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                camera.Render();
                return;
            }

            var request = new RenderPipeline.StandardRequest
            {
                destination = destination,
                mipLevel = 0,
                slice = 0,
                face = CubemapFace.Unknown,
            };
            RenderPipeline.SubmitRenderRequest(camera, request);
        }

        internal static void CollectAnimationClipsForWarmup(
            object root,
            ISet<AnimationClip> destination,
            ISet<object>? visited = null)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            visited ??= new HashSet<object>(ReferenceComparer.Instance);
            CollectFields(root, destination, visited);
        }

        private static void CollectFields(
            object value,
            ISet<AnimationClip> destination,
            ISet<object> visited)
        {
            Type type = value.GetType();
            if (!type.IsValueType && !visited.Add(value))
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsStatic || field.IsNotSerialized)
                    continue;

                try
                {
                    CollectValue(field.GetValue(value), destination, visited);
                }
                catch (Exception)
                {
                    // Warm-up is best effort. An editor/runtime-only serialized
                    // field must not disable the ordinary synchronous fallback.
                }
            }
        }

        private static void CollectValue(
            object? value,
            ISet<AnimationClip> destination,
            ISet<object> visited)
        {
            if (value == null)
                return;
            if (value is AnimationClip clip)
            {
                destination.Add(clip);
                return;
            }
            if (value is Object)
                return;
            if (value is string)
                return;
            if (value is IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                    CollectValue(item, destination, visited);
                return;
            }

            Type type = value.GetType();
            string typeNamespace = type.Namespace ?? string.Empty;
            if (!typeNamespace.StartsWith("Arena.Presentation", StringComparison.Ordinal))
                return;

            CollectFields(value, destination, visited);
        }

        private void OnDestroy()
        {
            if (_warmupCoroutine != null)
                StopCoroutine(_warmupCoroutine);
            if (s_instance == this)
                s_instance = null;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
