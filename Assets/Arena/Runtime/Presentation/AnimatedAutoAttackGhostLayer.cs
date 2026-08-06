#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Presentation-only animated ghost for auto-attacks suppressed by higher-priority combat animations.
    /// Builds a visual-only hierarchy from transforms/renderers; never clones gameplay MonoBehaviours.
    /// </summary>
    public sealed class AnimatedAutoAttackGhostLayer : MonoBehaviour
    {
        private const string GhostRootName = "AnimatedAutoAttackGhost";
        private const float DefaultFadeSeconds = 0.75f;
        private const float DefaultStartAlpha = 0.44f;
        private const int DefaultMaxGhosts = 1;

        private static readonly int InCombatHash = Animator.StringToHash("InCombat");
        private static readonly int TriggerStrike1Hash = Animator.StringToHash("TriggerStrike1");
        private static readonly int TriggerStrike2Hash = Animator.StringToHash("TriggerStrike2");
        private static readonly int TriggerStrike3Hash = Animator.StringToHash("TriggerStrike3");
        private static readonly int TriggerStrike4Hash = Animator.StringToHash("TriggerStrike4");
        private static readonly int IdleCombatStateHash = Animator.StringToHash("IdleCombat");
        private static readonly int UpperBodyEmptyStateHash = Animator.StringToHash("Empty");
        private static readonly int MeleeAttackEmptyStateHash = Animator.StringToHash("Empty");

        private const int BaseLayerIndex = 0;
        private const int UpperBodyLayerIndex = 1;
        private const int MeleeAttackLayerIndex = 3;

        [SerializeField] private bool enabledForSuppressedAutoAttack = true;
        [SerializeField] [Range(0.05f, 1.5f)] private float fadeSeconds = DefaultFadeSeconds;
        [SerializeField] [Range(0.02f, 0.8f)] private float startAlpha = DefaultStartAlpha;
        [SerializeField] [Range(1, 3)] private int maxGhosts = DefaultMaxGhosts;
        [SerializeField] private Color tint = new(0.45f, 0.95f, 1f, 1f);

        private readonly List<GhostActor> _actors = new();
        private Animator? _sourceAnimator;
        private Transform? _sourceRootOverride;

        public void SetSource(Animator? sourceAnimator, Transform? sourceRoot)
        {
            _sourceAnimator = sourceAnimator;
            _sourceRootOverride = sourceRoot;
            ClearActors();
        }

        public void InvalidateVisualClone()
        {
            ClearActors();
        }

        public bool PlayStrikeGhost(int bankSlot, AnimationClip? strikeClip, Vector3 targetPoint)
        {
            if (!enabledForSuppressedAutoAttack
                || !isActiveAndEnabled
                || _sourceAnimator == null
                || _sourceAnimator.runtimeAnimatorController == null)
            {
                return false;
            }

            Transform sourceRoot = ResolveFacingRoot();
            if (!TryResolveFacing(sourceRoot.position, targetPoint, out Quaternion facing))
                return false;

            GhostActor actor = GetActor();
            if (!actor.PrepareFromSource(_sourceAnimator, sourceRoot, facing, bankSlot, strikeClip, tint, startAlpha))
                return false;

            actor.PlayStrike(bankSlot);
            return true;
        }

        private void LateUpdate()
        {
            for (int i = 0; i < _actors.Count; i++)
                _actors[i].Tick(Time.deltaTime, fadeSeconds, startAlpha);
        }

        private void OnDisable()
        {
            ClearActors();
        }

        private void OnDestroy()
        {
            ClearActors();
        }

        private Transform ResolveFacingRoot()
        {
            return _sourceRootOverride != null ? _sourceRootOverride : (_sourceAnimator != null ? _sourceAnimator.transform : transform);
        }

        private GhostActor GetActor()
        {
            int capacity = Mathf.Max(1, maxGhosts);
            while (_actors.Count > capacity)
            {
                _actors[^1].Destroy();
                _actors.RemoveAt(_actors.Count - 1);
            }

            for (int i = 0; i < _actors.Count; i++)
            {
                if (!_actors[i].Active)
                    return _actors[i];
            }

            if (_actors.Count < capacity)
            {
                GhostActor actor = new();
                _actors.Add(actor);
                return actor;
            }

            GhostActor oldest = _actors[0];
            for (int i = 1; i < _actors.Count; i++)
            {
                if (_actors[i].Elapsed > oldest.Elapsed)
                    oldest = _actors[i];
            }

            return oldest;
        }

        private void ClearActors()
        {
            for (int i = 0; i < _actors.Count; i++)
                _actors[i].Destroy();
            _actors.Clear();
        }

        private static bool TryResolveFacing(Vector3 origin, Vector3 targetPoint, out Quaternion rotation)
        {
            Vector3 toTarget = targetPoint - origin;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                rotation = Quaternion.identity;
                return false;
            }

            rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            return true;
        }

        internal sealed class GhostActor
        {
            private readonly Dictionary<Transform, Transform> _transformMap = new();
            private readonly List<MaterialBinding> _materials = new();
            private GameObject? _root;
            private Animator? _animator;
            private AnimatorOverrideController? _overrideController;
            private RuntimeAnimatorController? _overrideSourceController;
            private readonly Dictionary<string, AnimationClip> _strikeOverrides = new();
            private Transform? _sourceAnimatorTransform;
            private Color _materialTint;
            private bool _solidSilhouette;

            public bool Active { get; private set; }
            public float Elapsed { get; private set; }

            public bool PrepareFromSource(
                Animator sourceAnimator,
                Transform facingRoot,
                Quaternion facing,
                int bankSlot,
                AnimationClip? strikeClip,
                Color materialTint,
                float alpha,
                bool solidSilhouette = false)
            {
                if (_root == null
                    || _animator == null
                    || !ReferenceEquals(_sourceAnimatorTransform, sourceAnimator.transform)
                    || _materialTint != materialTint
                    || _solidSilhouette != solidSilhouette)
                {
                    RebuildVisualClone(sourceAnimator, materialTint, solidSilhouette);
                }

                if (_root == null || _animator == null)
                    return false;

                Transform rootTransform = _root.transform;
                rootTransform.SetPositionAndRotation(facingRoot.position, facing);
                rootTransform.localScale = Vector3.one;
                _root.SetActive(true);

                BindStrikeOverride(sourceAnimator.runtimeAnimatorController, bankSlot, strikeClip);
                _animator.runtimeAnimatorController = _overrideController;
                _animator.avatar = sourceAnimator.avatar;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.Rebind();
                _animator.Update(0f);

                ApplyAlpha(alpha);
                Elapsed = 0f;
                Active = true;
                return true;
            }

            public bool PrepareCombatIdleFromSource(
                Animator sourceAnimator,
                Vector3 position,
                Quaternion facing,
                Color materialTint,
                float alpha)
            {
                if (_root == null
                    || _animator == null
                    || !ReferenceEquals(_sourceAnimatorTransform, sourceAnimator.transform)
                    || _materialTint != materialTint
                    || !_solidSilhouette)
                {
                    RebuildVisualClone(sourceAnimator, materialTint, solidSilhouette: true);
                }

                if (_root == null || _animator == null || sourceAnimator.runtimeAnimatorController == null)
                    return false;

                _root.transform.SetPositionAndRotation(position, facing);
                _root.transform.localScale = Vector3.one;
                _root.SetActive(true);

                _animator.runtimeAnimatorController = sourceAnimator.runtimeAnimatorController;
                _animator.avatar = sourceAnimator.avatar;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.Rebind();
                _animator.Update(0f);

                ApplyAlpha(alpha);
                Elapsed = 0f;
                Active = true;
                return true;
            }

            public void PlayStrike(int bankSlot)
            {
                if (_animator == null)
                    return;

                PlayCombatIdle();
                _animator.ResetTrigger(TriggerStrike1Hash);
                _animator.ResetTrigger(TriggerStrike2Hash);
                _animator.ResetTrigger(TriggerStrike3Hash);
                _animator.ResetTrigger(TriggerStrike4Hash);
                _animator.SetTrigger(ResolveStrikeTriggerHash(bankSlot));
            }

            public void PlayCombatIdle()
            {
                if (_animator == null)
                    return;

                _animator.SetBool(InCombatHash, true);
                _animator.Play(IdleCombatStateHash, BaseLayerIndex, 0f);
                PlayLayerIfAvailable(_animator, UpperBodyLayerIndex, UpperBodyEmptyStateHash);
                PlayLayerIfAvailable(_animator, MeleeAttackLayerIndex, MeleeAttackEmptyStateHash);
            }

            public void Tick(float deltaTime, float fadeSeconds, float startAlpha)
            {
                if (!Active)
                    return;

                Elapsed += deltaTime;
                float duration = Mathf.Max(0.01f, fadeSeconds);
                float alpha = startAlpha * Mathf.Clamp01(1f - (Elapsed / duration));
                ApplyAlpha(alpha);

                if (Elapsed >= duration)
                    Hide();
            }

            public void Destroy()
            {
                Active = false;
                if (_root != null)
                    Object.Destroy(_root);
                _root = null;
                _animator = null;
                _sourceAnimatorTransform = null;
                DestroyOverrideController();
                _strikeOverrides.Clear();
                _transformMap.Clear();
                DestroyMaterials();
            }

            private void DestroyOverrideController()
            {
                if (_overrideController == null)
                    return;

                if (Application.isPlaying)
                    Object.Destroy(_overrideController);
                else
                    Object.DestroyImmediate(_overrideController);

                _overrideController = null;
                _overrideSourceController = null;
            }

            private void DestroyMaterials()
            {
                for (int i = 0; i < _materials.Count; i++)
                {
                    Material material = _materials[i].Material;
                    if (material == null)
                        continue;

                    if (Application.isPlaying)
                        Object.Destroy(material);
                    else
                        Object.DestroyImmediate(material);
                }

                _materials.Clear();
            }

            public void Hide()
            {
                Active = false;
                Elapsed = 0f;
                if (_root != null)
                    _root.SetActive(false);
            }

            private void RebuildVisualClone(
                Animator sourceAnimator,
                Color materialTint,
                bool solidSilhouette)
            {
                Destroy();

                _sourceAnimatorTransform = sourceAnimator.transform;
                _materialTint = materialTint;
                _solidSilhouette = solidSilhouette;
                _root = CloneTransformHierarchy(sourceAnimator.transform, null);
                _root.name = GhostRootName;
                _animator = _root.AddComponent<Animator>();
                _animator.avatar = sourceAnimator.avatar;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                CombatAnimationEventReceiver.EnsureOn(_animator);

                CopyRenderers(sourceAnimator.transform, materialTint, solidSilhouette);
                _root.SetActive(false);
            }

            private void EnsureOverrideController(RuntimeAnimatorController sourceController)
            {
                if (_overrideController != null
                    && ReferenceEquals(_overrideSourceController, sourceController))
                {
                    return;
                }

                DestroyOverrideController();
                _strikeOverrides.Clear();
                _overrideSourceController = sourceController;
                _overrideController = new AnimatorOverrideController(sourceController);
            }

            private void BindStrikeOverride(
                RuntimeAnimatorController sourceController,
                int bankSlot,
                AnimationClip? strikeClip)
            {
                EnsureOverrideController(sourceController);
                if (_overrideController == null)
                    return;

                string slotName = $"slot_strike_{bankSlot}";
                if (strikeClip == null)
                {
                    if (_strikeOverrides.Remove(slotName))
                        RebuildOverrideControllerWithoutSlot(sourceController, slotName);
                    return;
                }

                if (_strikeOverrides.TryGetValue(slotName, out AnimationClip boundClip)
                    && ReferenceEquals(boundClip, strikeClip))
                {
                    return;
                }

                _overrideController[slotName] = strikeClip;
                _strikeOverrides[slotName] = strikeClip;
            }

            private void RebuildOverrideControllerWithoutSlot(
                RuntimeAnimatorController sourceController,
                string excludedSlotName)
            {
                var retainedOverrides = new Dictionary<string, AnimationClip>(_strikeOverrides);
                retainedOverrides.Remove(excludedSlotName);

                DestroyOverrideController();
                _strikeOverrides.Clear();
                _overrideSourceController = sourceController;
                _overrideController = new AnimatorOverrideController(sourceController);

                foreach ((string slotName, AnimationClip clip) in retainedOverrides)
                {
                    _overrideController[slotName] = clip;
                    _strikeOverrides[slotName] = clip;
                }
            }

            private GameObject CloneTransformHierarchy(Transform source, Transform? parent)
            {
                GameObject clone = new(source.name);
                Transform cloneTransform = clone.transform;
                if (parent != null)
                    cloneTransform.SetParent(parent, worldPositionStays: false);
                cloneTransform.localPosition = source.localPosition;
                cloneTransform.localRotation = source.localRotation;
                cloneTransform.localScale = source.localScale;
                _transformMap[source] = cloneTransform;

                for (int i = 0; i < source.childCount; i++)
                    CloneTransformHierarchy(source.GetChild(i), cloneTransform);

                return clone;
            }

            private void CopyRenderers(
                Transform sourceRoot,
                Color materialTint,
                bool solidSilhouette)
            {
                Renderer[] renderers = sourceRoot.GetComponentsInChildren<Renderer>(includeInactive: false);
                foreach (Renderer renderer in renderers)
                {
                    if (!renderer.enabled || IsRuntimeOverlayRenderer(renderer))
                        continue;

                    if (!_transformMap.TryGetValue(renderer.transform, out Transform cloneTransform))
                        continue;

                    switch (renderer)
                    {
                        case SkinnedMeshRenderer skinned:
                            CopySkinnedRenderer(skinned, cloneTransform, materialTint, solidSilhouette);
                            break;
                        case MeshRenderer meshRenderer:
                            CopyMeshRenderer(meshRenderer, cloneTransform, materialTint, solidSilhouette);
                            break;
                    }
                }
            }

            private void CopySkinnedRenderer(
                SkinnedMeshRenderer source,
                Transform cloneTransform,
                Color materialTint,
                bool solidSilhouette)
            {
                var clone = cloneTransform.gameObject.AddComponent<SkinnedMeshRenderer>();
                clone.sharedMesh = source.sharedMesh;
                clone.sharedMaterials = CreateGhostMaterials(source.sharedMaterials, materialTint, solidSilhouette);
                clone.rootBone = source.rootBone != null && _transformMap.TryGetValue(source.rootBone, out Transform mappedRoot)
                    ? mappedRoot
                    : null;

                Transform[] mappedBones = new Transform[source.bones.Length];
                for (int i = 0; i < source.bones.Length; i++)
                {
                    Transform sourceBone = source.bones[i];
                    mappedBones[i] = sourceBone != null && _transformMap.TryGetValue(sourceBone, out Transform mapped)
                        ? mapped
                        : cloneTransform;
                }

                clone.bones = mappedBones;
                clone.localBounds = source.localBounds;
                clone.quality = source.quality;
                clone.updateWhenOffscreen = source.updateWhenOffscreen;
                clone.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                clone.receiveShadows = false;
            }

            private void CopyMeshRenderer(
                MeshRenderer source,
                Transform cloneTransform,
                Color materialTint,
                bool solidSilhouette)
            {
                if (!source.TryGetComponent(out MeshFilter sourceFilter) || sourceFilter.sharedMesh == null)
                    return;

                cloneTransform.gameObject.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
                var clone = cloneTransform.gameObject.AddComponent<MeshRenderer>();
                clone.sharedMaterials = CreateGhostMaterials(source.sharedMaterials, materialTint, solidSilhouette);
                clone.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                clone.receiveShadows = false;
            }

            private Material[] CreateGhostMaterials(
                Material[] sourceMaterials,
                Color materialTint,
                bool solidSilhouette)
            {
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    return new[] { CreateGhostMaterial(null, materialTint, solidSilhouette) };

                Material[] materials = new Material[sourceMaterials.Length];
                for (int i = 0; i < sourceMaterials.Length; i++)
                    materials[i] = CreateGhostMaterial(sourceMaterials[i], materialTint, solidSilhouette);
                return materials;
            }

            private Material CreateGhostMaterial(
                Material? source,
                Color materialTint,
                bool solidSilhouette)
            {
                Material material = source != null
                    ? new Material(source)
                    : new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));

                material.name = $"{(source != null ? source.name : "Default")}_animated_auto_attack_ghost";
                ConfigureTransparentMaterial(material);
                if (solidSilhouette)
                    ConfigureSolidSilhouetteMaterial(material);
                Color baseColor = solidSilhouette
                    ? materialTint
                    : ResolveBaseColor(material) * materialTint;
                _materials.Add(new MaterialBinding(material, baseColor));
                return material;
            }

            private static void ConfigureSolidSilhouetteMaterial(Material material)
            {
                if (material.HasProperty("_BaseMap"))
                    material.SetTexture("_BaseMap", null);
                if (material.HasProperty("_MainTex"))
                    material.SetTexture("_MainTex", null);
                if (material.HasProperty("_EmissionColor"))
                    material.SetColor("_EmissionColor", Color.black);
                material.DisableKeyword("_EMISSION");
            }

            private static void ConfigureTransparentMaterial(Material material)
            {
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                if (material.HasProperty("_Surface"))
                    material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_Blend"))
                    material.SetFloat("_Blend", 0f);
                if (material.HasProperty("_SrcBlend"))
                    material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend"))
                    material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite"))
                    material.SetFloat("_ZWrite", 0f);

                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.EnableKeyword("_ALPHABLEND_ON");
            }

            private static Color ResolveBaseColor(Material material)
            {
                if (material.HasProperty("_BaseColor"))
                    return material.GetColor("_BaseColor");
                if (material.HasProperty("_Color"))
                    return material.GetColor("_Color");
                return Color.white;
            }

            private void ApplyAlpha(float alpha)
            {
                foreach (MaterialBinding binding in _materials)
                {
                    Color color = binding.BaseColor;
                    color.a = Mathf.Clamp01(alpha);
                    if (binding.Material.HasProperty("_BaseColor"))
                        binding.Material.SetColor("_BaseColor", color);
                    if (binding.Material.HasProperty("_Color"))
                        binding.Material.SetColor("_Color", color);
                }
            }

            private static bool IsRuntimeOverlayRenderer(Renderer renderer)
            {
                Transform? current = renderer.transform;
                while (current != null)
                {
                    if (current.name == "NameTag" || current.name == "WorldHealthBar")
                        return true;

                    current = current.parent;
                }

                return false;
            }

            private static void PlayLayerIfAvailable(Animator animator, int layerIndex, int stateHash)
            {
                if (animator.layerCount > layerIndex)
                    animator.Play(stateHash, layerIndex, 0f);
            }

            private static int ResolveStrikeTriggerHash(int bankSlot)
            {
                return bankSlot switch
                {
                    1 => TriggerStrike1Hash,
                    2 => TriggerStrike2Hash,
                    3 => TriggerStrike3Hash,
                    4 => TriggerStrike4Hash,
                    _ => TriggerStrike1Hash,
                };
            }
        }

        private readonly struct MaterialBinding
        {
            public readonly Material Material;
            public readonly Color BaseColor;

            public MaterialBinding(Material material, Color baseColor)
            {
                Material = material;
                BaseColor = baseColor;
            }
        }
    }
}
