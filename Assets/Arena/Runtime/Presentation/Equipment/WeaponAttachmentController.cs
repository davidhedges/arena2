#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Handles weapon mesh instantiation and avatar-authored mount attachment.
    /// Animation clip overrides remain in PlayerAnimator; this class owns visual
    /// equipment placement only.
    /// </summary>
    [DisallowMultipleComponent]
    public class WeaponAttachmentController : MonoBehaviour
    {
        private AvatarWeaponMounts? _mounts;
        private readonly List<AttachedVisual> _spawnedVisuals = new();
        private readonly List<TemporaryAnimatedProp> _temporaryAnimatedProps = new();
        private readonly HashSet<string> _temporarilyHiddenItemIds = new(StringComparer.OrdinalIgnoreCase);
        private bool _externallyHidden;
        private readonly HashSet<string> _missingMountWarnings = new(StringComparer.Ordinal);
        private bool _inCombat;

        public bool IsInCombatVisual => _inCombat;
        public int VisualVersion { get; private set; }

        private sealed class AttachedVisual
        {
            public string ItemId = string.Empty;
            public string CombatMountId = string.Empty;
            public string StowedMountId = string.Empty;
            public Vector3 CombatLocalPosition;
            public Quaternion CombatLocalRotation;
            public Vector3 StowedLocalPosition;
            public Quaternion StowedLocalRotation;
            public float DrawHandoffTime;
            public float SheathHandoffTime;
            public WeaponVisualAttachmentMode AttachmentMode;
            public WeaponVisualVisibility Visibility;
            public WeaponVisualBoneRemap[] BoneRemaps = Array.Empty<WeaponVisualBoneRemap>();
            public bool IsInCombat;
            public GameObject Instance = null!;
            public GameObject SourcePrefab = null!;
            public Vector3 BaseLocalScale = Vector3.one;
            public float PresentationScale = 1f;
            public ScalePulse? ActiveScalePulse;
        }

        private sealed class TemporaryAnimatedProp
        {
            public string ActionId = string.Empty;
            public string ItemId = string.Empty;
            public GameObject Instance = null!;
            public bool HidesEquippedVisual;
            public float ReleaseAtSeconds;
            public float ExpiresAtSeconds;
        }

        private sealed class ScalePulse
        {
            public float Multiplier = 1f;
            public float ScaleInSeconds;
            public float HoldSeconds;
            public float ScaleOutSeconds;
            public float StartedAtSeconds;
        }

        public void Initialize()
        {
            _missingMountWarnings.Clear();
        }

        public void BindMounts(AvatarWeaponMounts mounts)
        {
            _mounts = mounts;
            _missingMountWarnings.Clear();
            RefreshAttachments();
            VisualVersion++;
        }

        public void ClearVisuals()
        {
            _missingMountWarnings.Clear();
            DestroyTemporaryAnimatedProps();
            DestroySpawnedVisuals();
            DestroyOrphanedWeaponVisuals(transform);
            VisualVersion++;
        }

        public void ApplyAnimationSet(CombatAnimationSet set)
        {
            ApplyAnimationSet(set, equippedVisualsByRole: null);
        }

        public void ApplyAnimationSet(
            CombatAnimationSet set,
            IReadOnlyDictionary<string, EquippedWeaponVisual>? equippedVisualsByRole)
        {
            _missingMountWarnings.Clear();
            ClearVisuals();
            if (_mounts == null)
                return;

            var visuals = set.VisualBindings;
            if (visuals.Length == 0)
            {
                VisualVersion++;
                return;
            }

            for (int i = 0; i < visuals.Length; i++)
            {
                var binding = visuals[i];
                if (binding.prefab == null)
                    continue;

                string itemId = string.IsNullOrWhiteSpace(binding.itemId)
                    ? binding.prefab.name
                    : binding.itemId;
                GameObject prefab = binding.prefab;
                if (equippedVisualsByRole != null)
                {
                    if (!equippedVisualsByRole.TryGetValue(itemId, out EquippedWeaponVisual equippedVisual))
                        continue;

                    prefab = equippedVisual.Prefab;
                }

                var instance = Instantiate(prefab, transform);
                instance.name = itemId;
                if (instance.GetComponent<WeaponAttachmentSpawnedVisual>() == null)
                    instance.AddComponent<WeaponAttachmentSpawnedVisual>();

                var attachedVisual = new AttachedVisual
                {
                    ItemId = itemId,
                    CombatMountId = binding.drawnMountId,
                    StowedMountId = binding.stowedMountId,
                    CombatLocalPosition = binding.ResolveLocalPosition(inCombat: true),
                    CombatLocalRotation = binding.ResolveLocalRotation(inCombat: true),
                    StowedLocalPosition = binding.ResolveLocalPosition(inCombat: false),
                    StowedLocalRotation = binding.ResolveLocalRotation(inCombat: false),
                    DrawHandoffTime = binding.ResolveDrawHandoffTime(set.DrawWeaponHandoffTime),
                    SheathHandoffTime = binding.ResolveSheathHandoffTime(set.SheathWeaponHandoffTime),
                    AttachmentMode = binding.attachmentMode,
                    Visibility = binding.visibility,
                    BoneRemaps = binding.SkinnedBoneRemapsOrEmpty,
                    IsInCombat = _inCombat,
                    Instance = instance,
                    SourcePrefab = prefab,
                    BaseLocalScale = instance.transform.localScale,
                };
                RebindSkinnedBones(attachedVisual);
                _spawnedVisuals.Add(attachedVisual);
            }

            RefreshAttachments();
            VisualVersion++;
        }

        public void SetInCombat(bool inCombat)
        {
            bool changed = _inCombat != inCombat;
            _inCombat = inCombat;
            RefreshAttachments();
            if (changed)
                VisualVersion++;
        }

        public void SetExternallyHidden(bool hidden)
        {
            if (_externallyHidden == hidden)
                return;

            _externallyHidden = hidden;
            RefreshAttachments();
            VisualVersion++;
        }

        public void PlayScalePulse(
            WeaponPresentationEffectTarget target,
            string? itemId,
            float scaleMultiplier,
            float scaleInSeconds,
            float holdSeconds,
            float scaleOutSeconds)
        {
            if (_spawnedVisuals.Count == 0)
                return;

            float multiplier = Mathf.Max(0.01f, scaleMultiplier);
            float startedAt = Time.time;
            bool applied = false;
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                var visual = _spawnedVisuals[i];
                if (!MatchesTarget(visual, i, target, itemId))
                    continue;

                visual.ActiveScalePulse = new ScalePulse
                {
                    Multiplier = multiplier,
                    ScaleInSeconds = Mathf.Max(0f, scaleInSeconds),
                    HoldSeconds = Mathf.Max(0f, holdSeconds),
                    ScaleOutSeconds = Mathf.Max(0f, scaleOutSeconds),
                    StartedAtSeconds = startedAt,
                };
                UpdatePresentationScale(visual, startedAt);
                ApplyVisualTransform(visual);
                applied = true;
            }

            if (applied)
                VisualVersion++;
        }

        public bool ApplyTransitionProgress(bool targetInCombat, float normalizedTime)
        {
            normalizedTime = Mathf.Clamp01(normalizedTime);
            bool allAtTarget = true;
            bool changed = false;

            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                var visual = _spawnedVisuals[i];
                bool desiredInCombat = targetInCombat
                    ? normalizedTime >= visual.DrawHandoffTime
                    : normalizedTime < visual.SheathHandoffTime;

                if (visual.IsInCombat != desiredInCombat)
                {
                    visual.IsInCombat = desiredInCombat;
                    string mountId = desiredInCombat ? visual.CombatMountId : visual.StowedMountId;
                    Attach(visual, mountId);
                    changed = true;
                }

                if (desiredInCombat != targetInCombat)
                    allAtTarget = false;
            }

            _inCombat = targetInCombat && _spawnedVisuals.Count > 0
                ? allAtTarget
                : targetInCombat;

            if (changed)
                VisualVersion++;

            return allAtTarget;
        }

        public bool TryGetVisibleVisualForMount(string mountId, out Transform visual)
        {
            visual = null!;
            if (string.IsNullOrWhiteSpace(mountId))
                return false;

            string canonicalMountId = AvatarWeaponMounts.CanonicalizeMountId(mountId);
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                AttachedVisual attached = _spawnedVisuals[i];
                if (attached.Instance == null || !attached.Instance.activeInHierarchy)
                    continue;

                string attachedMountId = attached.IsInCombat ? attached.CombatMountId : attached.StowedMountId;
                if (!string.Equals(
                    AvatarWeaponMounts.CanonicalizeMountId(attachedMountId),
                    canonicalMountId,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                visual = attached.Instance.transform;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the first currently visible equipped weapon visual. Visual bindings
        /// are authored primary-hand first, so playground presentation tools can use
        /// this without duplicating combat-profile or mount-resolution rules.
        /// </summary>
        public bool TryGetPrimaryVisibleVisual(out Transform visual)
        {
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                GameObject instance = _spawnedVisuals[i].Instance;
                if (instance == null || !instance.activeInHierarchy)
                    continue;

                visual = instance.transform;
                return true;
            }

            visual = null!;
            return false;
        }

        /// <summary>
        /// Appends renderers owned by current equipped and temporary weapon visuals.
        /// Callers use this for whole-character presentation effects without scanning
        /// unrelated UI or VFX under the player entity root.
        /// </summary>
        public void AppendVisualRenderers(List<Renderer> results)
        {
            if (results == null)
                throw new ArgumentNullException(nameof(results));

            // GetComponentsInChildren(List<T>) clears the destination list, so collect
            // into a scratch list and append to preserve the caller's entries.
            var scratch = new List<Renderer>();
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                GameObject instance = _spawnedVisuals[i].Instance;
                if (instance == null)
                    continue;

                instance.GetComponentsInChildren(includeInactive: true, scratch);
                results.AddRange(scratch);
            }

            for (int i = 0; i < _temporaryAnimatedProps.Count; i++)
            {
                GameObject instance = _temporaryAnimatedProps[i].Instance;
                if (instance == null)
                    continue;

                instance.GetComponentsInChildren(includeInactive: true, scratch);
                results.AddRange(scratch);
            }
        }

        public bool BeginTemporaryAnimatedProp(
            string actionId,
            in SpellAnimatedPropHandoff handoff,
            float releaseOffsetSeconds)
        {
            if (_mounts == null || !handoff.enabled)
                return false;

            string normalizedActionId = WireIdentifier.Normalize(actionId);
            string itemId = handoff.ItemIdOrEmpty;
            string socketPath = handoff.AnimatedSocketPathOrEmpty;
            if (string.IsNullOrWhiteSpace(normalizedActionId)
                || string.IsNullOrWhiteSpace(itemId)
                || string.IsNullOrWhiteSpace(socketPath))
            {
                return false;
            }

            AttachedVisual? source = FindVisualByItemId(itemId);
            if (source == null || source.SourcePrefab == null)
                return false;

            Transform? socket = _mounts.transform.Find(socketPath);
            if (socket == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning(
                    $"[{nameof(WeaponAttachmentController)}] Avatar '{_mounts.name}' is missing animated prop socket '{socketPath}' for '{normalizedActionId}'.",
                    this);
#endif
                return false;
            }

            ReleaseTemporaryAnimatedProp(normalizedActionId);

            GameObject instance = Instantiate(source.SourcePrefab, socket, false);
            instance.name = $"{source.ItemId}_{normalizedActionId}_AnimatedProp";
            if (instance.GetComponent<WeaponAttachmentSpawnedVisual>() == null)
                instance.AddComponent<WeaponAttachmentSpawnedVisual>();
            instance.transform.localPosition = handoff.localPosition;
            instance.transform.localRotation = handoff.ResolveLocalRotation();
            instance.transform.localScale = Vector3.one * handoff.ResolveLocalScale();

            float now = Time.time;
            float releaseAt = now + Mathf.Max(0f, releaseOffsetSeconds);
            _temporaryAnimatedProps.Add(new TemporaryAnimatedProp
            {
                ActionId = normalizedActionId,
                ItemId = itemId,
                Instance = instance,
                HidesEquippedVisual = handoff.hideEquippedVisual,
                ReleaseAtSeconds = releaseAt,
                ExpiresAtSeconds = releaseAt + handoff.ResolveMaxLifetimeSeconds(),
            });

            if (handoff.hideEquippedVisual)
            {
                _temporarilyHiddenItemIds.Add(itemId);
                RefreshAttachments();
            }

            VisualVersion++;
            return true;
        }

        public bool TryGetTemporaryAnimatedPropReleaseDelaySeconds(string actionId, out float delaySeconds)
        {
            delaySeconds = 0f;
            string normalizedActionId = WireIdentifier.Normalize(actionId);
            if (string.IsNullOrWhiteSpace(normalizedActionId))
                return false;

            float now = Time.time;
            for (int i = _temporaryAnimatedProps.Count - 1; i >= 0; i--)
            {
                TemporaryAnimatedProp prop = _temporaryAnimatedProps[i];
                if (prop.Instance == null)
                {
                    RemoveTemporaryAnimatedPropAt(i);
                    continue;
                }

                if (!string.Equals(prop.ActionId, normalizedActionId, StringComparison.Ordinal))
                    continue;

                delaySeconds = Mathf.Max(0f, prop.ReleaseAtSeconds - now);
                return true;
            }

            return false;
        }

        public void ReleaseTemporaryAnimatedProp(string actionId)
        {
            string normalizedActionId = WireIdentifier.Normalize(actionId);
            if (string.IsNullOrWhiteSpace(normalizedActionId))
                return;

            bool changed = false;
            for (int i = _temporaryAnimatedProps.Count - 1; i >= 0; i--)
            {
                TemporaryAnimatedProp prop = _temporaryAnimatedProps[i];
                if (!string.Equals(prop.ActionId, normalizedActionId, StringComparison.Ordinal))
                    continue;

                RemoveTemporaryAnimatedPropAt(i);
                changed = true;
            }

            if (changed)
            {
                RefreshAttachments();
                VisualVersion++;
            }
        }

        private void Update()
        {
            bool changed = false;
            float now = Time.time;
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                var visual = _spawnedVisuals[i];
                if (visual.ActiveScalePulse == null)
                    continue;

                if (UpdatePresentationScale(visual, now))
                    changed = true;
                ApplyVisualTransform(visual);
            }

            for (int i = _temporaryAnimatedProps.Count - 1; i >= 0; i--)
            {
                TemporaryAnimatedProp prop = _temporaryAnimatedProps[i];
                if (prop.Instance != null && now < prop.ExpiresAtSeconds)
                    continue;

                RemoveTemporaryAnimatedPropAt(i);
                changed = true;
            }

            if (changed)
            {
                RefreshAttachments();
                VisualVersion++;
            }
        }

        private void RefreshAttachments()
        {
            if (_mounts == null)
            {
                if (_spawnedVisuals.Count == 0)
                    return;

                Debug.LogWarning(
                    $"[{nameof(WeaponAttachmentController)}] Avatar '{name}' is missing {nameof(AvatarWeaponMounts)}; weapon visuals cannot be attached.",
                    this);
                return;
            }

            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                var visual = _spawnedVisuals[i];
                visual.IsInCombat = _inCombat;
                string mountId = visual.IsInCombat ? visual.CombatMountId : visual.StowedMountId;
                Attach(visual, mountId);
            }
        }

        private static bool MatchesTarget(
            AttachedVisual visual,
            int visualIndex,
            WeaponPresentationEffectTarget target,
            string? itemId)
        {
            return target switch
            {
                WeaponPresentationEffectTarget.AllEquipped => true,
                WeaponPresentationEffectTarget.ItemId => string.Equals(
                    visual.ItemId,
                    itemId?.Trim(),
                    StringComparison.OrdinalIgnoreCase),
                WeaponPresentationEffectTarget.MainHand => visualIndex == 0,
                _ => false,
            };
        }

        private AttachedVisual? FindVisualByItemId(string itemId)
        {
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                AttachedVisual visual = _spawnedVisuals[i];
                if (string.Equals(visual.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                    return visual;
            }

            return null;
        }

        private static bool UpdatePresentationScale(AttachedVisual visual, float now)
        {
            ScalePulse? pulse = visual.ActiveScalePulse;
            if (pulse == null)
                return false;

            float previous = visual.PresentationScale;
            float elapsed = Mathf.Max(0f, now - pulse.StartedAtSeconds);
            float scaleIn = pulse.ScaleInSeconds;
            float hold = pulse.HoldSeconds;
            float scaleOut = pulse.ScaleOutSeconds;
            float total = scaleIn + hold + scaleOut;

            if (total <= 0f || elapsed >= total)
            {
                visual.ActiveScalePulse = null;
                visual.PresentationScale = 1f;
                return !Mathf.Approximately(previous, visual.PresentationScale);
            }

            if (scaleIn > 0f && elapsed < scaleIn)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / scaleIn);
                visual.PresentationScale = Mathf.Lerp(1f, pulse.Multiplier, t);
            }
            else if (elapsed < scaleIn + hold)
            {
                visual.PresentationScale = pulse.Multiplier;
            }
            else if (scaleOut > 0f)
            {
                float t = Mathf.SmoothStep(0f, 1f, (elapsed - scaleIn - hold) / scaleOut);
                visual.PresentationScale = Mathf.Lerp(pulse.Multiplier, 1f, t);
            }
            else
            {
                visual.ActiveScalePulse = null;
                visual.PresentationScale = 1f;
            }

            return !Mathf.Approximately(previous, visual.PresentationScale);
        }

        private void Attach(AttachedVisual visual, string mountId)
        {
            if (_mounts == null)
                return;

            if (_externallyHidden
                || !IsVisibleForState(visual)
                || _temporarilyHiddenItemIds.Contains(visual.ItemId))
            {
                if (visual.Instance.activeSelf)
                    visual.Instance.SetActive(false);
                return;
            }

            if (visual.AttachmentMode == WeaponVisualAttachmentMode.AvatarRoot)
            {
                if (!visual.Instance.activeSelf)
                    visual.Instance.SetActive(true);

                visual.Instance.transform.SetParent(transform, false);
                ApplyVisualTransform(visual);
                return;
            }

            if (string.IsNullOrWhiteSpace(mountId) || !_mounts.TryGetMount(mountId, out var mount))
            {
                WarnMissingMount(visual.ItemId, mountId);
                if (visual.Instance.activeSelf)
                    visual.Instance.SetActive(false);
                return;
            }

            if (!visual.Instance.activeSelf)
                visual.Instance.SetActive(true);

            visual.Instance.transform.SetParent(mount, false);
            ApplyVisualTransform(visual);
        }

        private void RebindSkinnedBones(AttachedVisual visual)
        {
            if (_mounts == null || visual.BoneRemaps.Length == 0)
                return;

            var renderers = visual.Instance.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            if (renderers.Length == 0)
                return;

            Dictionary<string, Transform> remappedBones = new(StringComparer.Ordinal);
            for (int i = 0; i < visual.BoneRemaps.Length; i++)
            {
                WeaponVisualBoneRemap remap = visual.BoneRemaps[i];
                if (string.IsNullOrWhiteSpace(remap.localBoneName) || string.IsNullOrWhiteSpace(remap.mountId))
                    continue;

                if (_mounts.TryGetMount(remap.mountId, out var avatarBone))
                    remappedBones[remap.localBoneName] = avatarBone;
                else
                    WarnMissingMount(visual.ItemId, remap.mountId);
            }

            if (remappedBones.Count == 0)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                Transform[] bones = renderer.bones;
                for (int j = 0; j < bones.Length; j++)
                {
                    Transform bone = bones[j];
                    if (bone != null && remappedBones.TryGetValue(bone.name, out var avatarBone))
                        bones[j] = avatarBone;
                }

                renderer.bones = bones;

                for (int j = 0; j < visual.BoneRemaps.Length; j++)
                {
                    WeaponVisualBoneRemap remap = visual.BoneRemaps[j];
                    if (!remap.rootBone || !remappedBones.TryGetValue(remap.localBoneName, out var rootBone))
                        continue;

                    renderer.rootBone = rootBone;
                    break;
                }
            }
        }

        private static bool IsVisibleForState(AttachedVisual visual)
        {
            return visual.Visibility switch
            {
                WeaponVisualVisibility.CombatOnly => visual.IsInCombat,
                WeaponVisualVisibility.StowedOnly => !visual.IsInCombat,
                _ => true,
            };
        }

        private static void ApplyVisualTransform(AttachedVisual visual)
        {
            visual.Instance.transform.localPosition = visual.IsInCombat
                ? visual.CombatLocalPosition
                : visual.StowedLocalPosition;
            visual.Instance.transform.localRotation = visual.IsInCombat
                ? visual.CombatLocalRotation
                : visual.StowedLocalRotation;
            visual.Instance.transform.localScale = visual.BaseLocalScale * visual.PresentationScale;
        }

        private void DestroySpawnedVisuals()
        {
            for (int i = 0; i < _spawnedVisuals.Count; i++)
            {
                if (_spawnedVisuals[i].Instance != null)
                    DestroyVisual(_spawnedVisuals[i].Instance);
            }

            _spawnedVisuals.Clear();
        }

        private void DestroyTemporaryAnimatedProps()
        {
            for (int i = _temporaryAnimatedProps.Count - 1; i >= 0; i--)
                RemoveTemporaryAnimatedPropAt(i);

            _temporaryAnimatedProps.Clear();
            _temporarilyHiddenItemIds.Clear();
        }

        private void RemoveTemporaryAnimatedPropAt(int index)
        {
            if (index < 0 || index >= _temporaryAnimatedProps.Count)
                return;

            TemporaryAnimatedProp prop = _temporaryAnimatedProps[index];
            if (prop.Instance != null)
                DestroyVisual(prop.Instance);
            _temporaryAnimatedProps.RemoveAt(index);

            if (prop.HidesEquippedVisual)
                RebuildTemporaryHiddenItemIds();
        }

        private void RebuildTemporaryHiddenItemIds()
        {
            _temporarilyHiddenItemIds.Clear();
            for (int i = 0; i < _temporaryAnimatedProps.Count; i++)
            {
                TemporaryAnimatedProp prop = _temporaryAnimatedProps[i];
                if (prop.HidesEquippedVisual && !string.IsNullOrWhiteSpace(prop.ItemId))
                    _temporarilyHiddenItemIds.Add(prop.ItemId);
            }
        }

        private static void DestroyOrphanedWeaponVisuals(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                DestroyOrphanedWeaponVisuals(child);

                if (child.GetComponent<WeaponAttachmentSpawnedVisual>() != null)
                    DestroyVisual(child.gameObject);
            }
        }

        private static void DestroyVisual(GameObject visual)
        {
            if (Application.isPlaying)
                Destroy(visual);
            else
                DestroyImmediate(visual);
        }

        private void WarnMissingMount(string itemId, string mountId)
        {
            string normalizedMountId = string.IsNullOrWhiteSpace(mountId) ? "<empty>" : mountId;
            string warningKey = $"{itemId}|{normalizedMountId}";
            if (!_missingMountWarnings.Add(warningKey))
                return;

            Debug.LogWarning(
                $"[{nameof(WeaponAttachmentController)}] Avatar '{name}' is missing mount '{normalizedMountId}' for item '{itemId}'.",
                this);
        }
    }
}
