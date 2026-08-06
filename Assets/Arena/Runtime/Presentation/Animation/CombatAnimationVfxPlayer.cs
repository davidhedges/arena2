#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation.VFX;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Evaluates deterministic semantic VFX slots against the animation time
    /// actually sampled by PlayerAnimator. It is presentation-only and never
    /// participates in hit or gameplay timing.
    /// </summary>
    internal sealed class CombatAnimationVfxPlayer
    {
        private const float RewindToleranceSeconds = 0.001f;
        private const float FallbackLifetimeSeconds = 3f;
        private static readonly HashSet<string> MissingTemplateWarnings = new(StringComparer.Ordinal);

        private readonly MonoBehaviour _owner;
        private readonly Func<Animator?> _resolveAnimator;
        private readonly Func<Transform> _resolveCharacterRoot;
        private readonly Func<WeaponAttachmentController?> _resolveWeaponAttachments;
        private readonly Dictionary<string, string> _vfxBySlot = new(StringComparer.Ordinal);
        private readonly HashSet<CombatAnimationVfxTrack> _spawnedTracks = new();
        private readonly List<SpawnedVisual> _spawnedVisuals = new();
        private CombatAnimationSet? _animationSet;
        private AnimationClip? _currentClip;
        private float _previousClipTime = -1f;

        private sealed class SpawnedVisual
        {
            public GameObject Instance = null!;
            public CombatAnimationVfxTrack Track = null!;
            public AnimationClip Clip = null!;
        }

        public CombatAnimationVfxPlayer(
            MonoBehaviour owner,
            Func<Animator?> resolveAnimator,
            Func<Transform> resolveCharacterRoot,
            Func<WeaponAttachmentController?> resolveWeaponAttachments)
        {
            _owner = owner;
            _resolveAnimator = resolveAnimator;
            _resolveCharacterRoot = resolveCharacterRoot;
            _resolveWeaponAttachments = resolveWeaponAttachments;
        }

        public void Begin(
            CombatAnimationSet animationSet,
            int strikeIndex,
            CombatAnimationVfxBinding[]? requestBindings)
        {
            Clear();
            _animationSet = animationSet;

            IReadOnlyList<CombatAnimationVfxBinding> bindings = requestBindings
                ?? animationSet.GetStrikeAnimationVfxBindings(strikeIndex);
            for (int index = 0; index < bindings.Count; index++)
            {
                CombatAnimationVfxBinding binding = bindings[index];
                string slotId = binding.NormalizedSlotId;
                if (string.IsNullOrEmpty(slotId))
                    continue;

                // Last binding wins. An empty id is a useful explicit runtime disable.
                _vfxBySlot[slotId] = binding.NormalizedVfxId;
            }
        }

        public void Update(AnimationClip? clip, float clipTimeSeconds)
        {
            if (_animationSet == null || clip == null)
                return;

            clipTimeSeconds = Mathf.Max(0f, clipTimeSeconds);
            bool clipChanged = !ReferenceEquals(_currentClip, clip);
            bool rewound = !clipChanged
                && _previousClipTime >= 0f
                && clipTimeSeconds + RewindToleranceSeconds < _previousClipTime;
            if (clipChanged || rewound)
            {
                EndFiniteWindowsForPriorSample(clip, destroySameClip: rewound);
                _spawnedTracks.Clear();
                _currentClip = clip;
            }

            CleanupDestroyedVisuals();
            foreach (CombatAnimationVfxTrack track in _animationSet.GetAnimationVfxTracks(clip))
            {
                if (track == null || _spawnedTracks.Contains(track))
                    continue;

                string slotId = track.NormalizedSlotId;
                if (string.IsNullOrEmpty(slotId)
                    || !_vfxBySlot.TryGetValue(slotId, out string vfxId)
                    || string.IsNullOrEmpty(vfxId))
                {
                    continue;
                }

                float startTime = Mathf.Max(0f, track.startTimeSeconds);
                if (clipTimeSeconds + RewindToleranceSeconds < startTime)
                    continue;
                if (track.HasFiniteWindow && clipTimeSeconds >= track.endTimeSeconds)
                {
                    _spawnedTracks.Add(track);
                    continue;
                }

                _spawnedTracks.Add(track);
                Spawn(track, clip, vfxId, clipTimeSeconds - startTime);
            }

            EndExpiredFiniteWindows(clip, clipTimeSeconds);
            _previousClipTime = clipTimeSeconds;
        }

        public void Clear()
        {
            for (int index = 0; index < _spawnedVisuals.Count; index++)
            {
                GameObject instance = _spawnedVisuals[index].Instance;
                if (instance != null)
                    DestroyVisual(instance);
            }

            _spawnedVisuals.Clear();
            _spawnedTracks.Clear();
            _vfxBySlot.Clear();
            _animationSet = null;
            _currentClip = null;
            _previousClipTime = -1f;
        }

        private void Spawn(
            CombatAnimationVfxTrack track,
            AnimationClip clip,
            string vfxId,
            float elapsedSinceStart)
        {
            if (CombatVFXTemplateRegistry.IsScriptedTemplate(vfxId))
            {
                WarnMissingTemplate(vfxId, "scripted templates are not supported by animation slots");
                return;
            }

            CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(vfxId);
            if (template == null)
            {
                WarnMissingTemplate(vfxId, "no prefab is registered");
                return;
            }

            if (!track.HasFiniteWindow
                && elapsedSinceStart > ResolveNaturalLifetimeSeconds(template.Prefab))
            {
                return;
            }

            Animator? animator = _resolveAnimator();
            Transform characterRoot = _resolveCharacterRoot();
            WeaponAttachmentController? attachments = _resolveWeaponAttachments();
            Transform anchor = CombatAnimationVfxAnchorUtility.Resolve(
                track.anchor,
                characterRoot,
                animator,
                attachments);

            GameObject instance = UnityEngine.Object.Instantiate(template.Prefab);
            instance.name = $"{template.Prefab.name}_{track.NormalizedSlotId}";

            Vector3 localPosition = track.localPosition + template.LocalPositionOffset;
            Quaternion localRotation = Quaternion.Euler(track.localEulerAngles) * template.LocalRotation;
            if (track.attachment == CombatAnimationVfxAttachment.FollowAnchor)
            {
                instance.transform.SetParent(anchor, false);
                instance.transform.localPosition = localPosition;
                instance.transform.localRotation = localRotation;
            }
            else
            {
                instance.transform.SetPositionAndRotation(
                    anchor.TransformPoint(localPosition),
                    anchor.rotation * localRotation);
            }

            instance.transform.localScale = Vector3.Scale(
                instance.transform.localScale,
                SanitizeScale(track.localScale));
            VFXUtils.ApplyPrefabPresentationScale(instance, template.Scale);
            PlayAndCatchUpParticleSystems(instance, Mathf.Max(0f, elapsedSinceStart));

            _spawnedVisuals.Add(new SpawnedVisual
            {
                Instance = instance,
                Track = track,
                Clip = clip,
            });

            if (!track.HasFiniteWindow)
            {
                _owner.StartCoroutine(
                    CombatVFXLifecycleRegistry.DestroyWhenParticleSystemsFinish(instance, vfxId));
            }
        }

        private void EndExpiredFiniteWindows(AnimationClip clip, float clipTimeSeconds)
        {
            for (int index = _spawnedVisuals.Count - 1; index >= 0; index--)
            {
                SpawnedVisual visual = _spawnedVisuals[index];
                if (visual.Instance == null)
                {
                    _spawnedVisuals.RemoveAt(index);
                    continue;
                }

                if (!visual.Track.HasFiniteWindow
                    || !ReferenceEquals(visual.Clip, clip)
                    || clipTimeSeconds < visual.Track.endTimeSeconds)
                {
                    continue;
                }

                DestroyVisual(visual.Instance);
                _spawnedVisuals.RemoveAt(index);
            }
        }

        private void EndFiniteWindowsForPriorSample(AnimationClip nextClip, bool destroySameClip)
        {
            for (int index = _spawnedVisuals.Count - 1; index >= 0; index--)
            {
                SpawnedVisual visual = _spawnedVisuals[index];
                if (visual.Instance == null)
                {
                    _spawnedVisuals.RemoveAt(index);
                    continue;
                }

                if (!visual.Track.HasFiniteWindow
                    || (!destroySameClip && ReferenceEquals(visual.Clip, nextClip)))
                    continue;

                DestroyVisual(visual.Instance);
                _spawnedVisuals.RemoveAt(index);
            }
        }

        private void CleanupDestroyedVisuals()
        {
            for (int index = _spawnedVisuals.Count - 1; index >= 0; index--)
            {
                if (_spawnedVisuals[index].Instance == null)
                    _spawnedVisuals.RemoveAt(index);
            }
        }

        private static void PlayAndCatchUpParticleSystems(GameObject instance, float elapsedSeconds)
        {
            ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            for (int index = 0; index < systems.Length; index++)
            {
                ParticleSystem system = systems[index];
                if (elapsedSeconds > 0.001f)
                    system.Simulate(elapsedSeconds, withChildren: false, restart: true, fixedTimeStep: true);
                system.Play(withChildren: false);
            }
        }

        private static float ResolveNaturalLifetimeSeconds(GameObject prefab)
        {
            ParticleSystem[] systems = prefab.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length == 0)
                return FallbackLifetimeSeconds;

            float lifetime = 0f;
            for (int index = 0; index < systems.Length; index++)
            {
                ParticleSystem.MainModule main = systems[index].main;
                if (main.loop)
                    return FallbackLifetimeSeconds;

                lifetime = Mathf.Max(
                    lifetime,
                    main.duration + main.startDelay.constantMax + main.startLifetime.constantMax);
            }

            return lifetime > 0f ? lifetime : FallbackLifetimeSeconds;
        }

        private static Vector3 SanitizeScale(Vector3 scale)
        {
            return new Vector3(
                Mathf.Approximately(scale.x, 0f) ? 1f : scale.x,
                Mathf.Approximately(scale.y, 0f) ? 1f : scale.y,
                Mathf.Approximately(scale.z, 0f) ? 1f : scale.z);
        }

        private static void DestroyVisual(GameObject instance)
        {
            instance.SetActive(false);
            UnityEngine.Object.Destroy(instance);
        }

        private static void WarnMissingTemplate(string vfxId, string reason)
        {
            string normalized = string.IsNullOrWhiteSpace(vfxId)
                ? "<empty>"
                : vfxId.Trim().ToUpperInvariant();
            if (!MissingTemplateWarnings.Add(normalized))
                return;

            Debug.LogWarning(
                $"Animation VFX slot could not spawn '{normalized}': {reason}. "
                + "Use a prefab entry in CombatVFXRegistry.");
        }
    }

    public static class CombatAnimationVfxAnchorUtility
    {
        public const string BladeStartMarkerName = "ArenaVFX_BladeStart";
        public const string BladeEndMarkerName = "ArenaVFX_BladeEnd";

        public static Transform Resolve(
            CombatAnimationVfxAnchor anchor,
            Transform characterRoot,
            Animator? animator,
            WeaponAttachmentController? attachments)
        {
            if (anchor == CombatAnimationVfxAnchor.RightHand
                && TryResolveHumanoidBone(animator, HumanBodyBones.RightHand, out Transform rightHand))
            {
                return rightHand;
            }

            if (anchor == CombatAnimationVfxAnchor.LeftHand
                && TryResolveHumanoidBone(animator, HumanBodyBones.LeftHand, out Transform leftHand))
            {
                return leftHand;
            }

            if (anchor == CombatAnimationVfxAnchor.MainWeapon
                && TryResolveMainWeapon(attachments, out Transform weapon))
            {
                return weapon;
            }

            if ((anchor == CombatAnimationVfxAnchor.MainWeaponBladeStart
                    || anchor == CombatAnimationVfxAnchor.MainWeaponBladeEnd)
                && TryResolveMainWeapon(attachments, out Transform weaponRoot))
            {
                string markerName = anchor == CombatAnimationVfxAnchor.MainWeaponBladeStart
                    ? BladeStartMarkerName
                    : BladeEndMarkerName;
                if (TryFindDescendant(weaponRoot, markerName, out Transform marker))
                    return marker;
                return weaponRoot;
            }

            return characterRoot;
        }

        private static bool TryResolveHumanoidBone(
            Animator? animator,
            HumanBodyBones bone,
            out Transform transform)
        {
            transform = animator != null && animator.isHuman
                ? animator.GetBoneTransform(bone)
                : null!;
            return transform != null;
        }

        private static bool TryResolveMainWeapon(
            WeaponAttachmentController? attachments,
            out Transform transform)
        {
            if (attachments != null
                && attachments.TryGetVisibleVisualForMount(
                    AvatarWeaponMounts.MainHandMountId,
                    out transform))
            {
                return true;
            }

            transform = null!;
            return false;
        }

        private static bool TryFindDescendant(Transform root, string name, out Transform result)
        {
            if (string.Equals(root.name, name, StringComparison.Ordinal))
            {
                result = root;
                return true;
            }

            for (int index = 0; index < root.childCount; index++)
            {
                if (TryFindDescendant(root.GetChild(index), name, out result))
                    return true;
            }

            result = null!;
            return false;
        }
    }
}
