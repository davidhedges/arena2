#nullable enable
using System;
using System.Collections.Generic;
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
            public Vector3 BaseLocalScale = Vector3.one;
            public float PresentationScale = 1f;
            public ScalePulse? ActiveScalePulse;
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
            DestroySpawnedVisuals();
            DestroyOrphanedWeaponVisuals(transform);
            VisualVersion++;
        }

        public void ApplyAnimationSet(CombatAnimationSet set)
        {
            ApplyAnimationSet(set, allowedVisualItemIds: null);
        }

        public void ApplyAnimationSet(CombatAnimationSet set, ISet<string>? allowedVisualItemIds)
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
                if (allowedVisualItemIds != null && !allowedVisualItemIds.Contains(itemId))
                    continue;

                var instance = Instantiate(binding.prefab, transform);
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

            if (changed)
                VisualVersion++;
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

            if (!IsVisibleForState(visual))
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
