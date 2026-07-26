#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Interaction
{
    public enum InteractionAnimationBodyMode
    {
        FullBody = 0,
        UpperBody = 1,
    }

    public enum WorldInteractionAnimationPhase
    {
        None = 0,
        Start = 1,
        Loop = 2,
    }

    public readonly struct WorldInteractionAnimationSample
    {
        public WorldInteractionAnimationSample(
            WorldInteractionAnimationPhase phase,
            float normalizedTime)
        {
            Phase = phase;
            NormalizedTime = Mathf.Clamp01(normalizedTime);
        }

        public WorldInteractionAnimationPhase Phase { get; }
        public float NormalizedTime { get; }
    }

    public static class WorldInteractionAnimationTiming
    {
        public static WorldInteractionAnimationSample Resolve(
            long serverNowMs,
            long startedAtMs,
            long completesAtMs,
            long startLengthMs,
            long loopLengthMs)
        {
            if (completesAtMs <= startedAtMs
                || serverNowMs >= completesAtMs
                || (startLengthMs <= 0L && loopLengthMs <= 0L))
            {
                return new WorldInteractionAnimationSample(
                    WorldInteractionAnimationPhase.None,
                    0f);
            }

            long elapsedMs = Math.Max(0L, serverNowMs - startedAtMs);
            if (startLengthMs > 0L && elapsedMs < startLengthMs)
            {
                return new WorldInteractionAnimationSample(
                    WorldInteractionAnimationPhase.Start,
                    elapsedMs / (float)startLengthMs);
            }

            if (loopLengthMs > 0L)
            {
                long loopElapsedMs = Math.Max(0L, elapsedMs - Math.Max(0L, startLengthMs));
                return new WorldInteractionAnimationSample(
                    WorldInteractionAnimationPhase.Loop,
                    (loopElapsedMs % loopLengthMs) / (float)loopLengthMs);
            }

            return new WorldInteractionAnimationSample(
                WorldInteractionAnimationPhase.Start,
                1f);
        }
    }

    [CreateAssetMenu(
        fileName = "WorldInteractionAnimationProfile",
        menuName = "Arena/Interaction/Animation Profile")]
    public sealed class WorldInteractionAnimationProfile : ScriptableObject
    {
        [SerializeField] private string _profileId = string.Empty;
        [SerializeField] private AnimationClip? _startClip;
        [SerializeField] private AnimationClip? _loopClip;
        [SerializeField] private AnimationClip? _endClip;
        [SerializeField] private AnimationClip? _cancelClip;
        [SerializeField] private InteractionAnimationBodyMode _bodyMode;
        [SerializeField] private AvatarMask? _avatarMask;
        [SerializeField] private bool _faceTarget = true;
        [SerializeField, Min(0f)] private float _blendSeconds = 0.12f;

        public string ProfileId => NormalizeId(_profileId);
        public AnimationClip? StartClip => _startClip;
        public AnimationClip? LoopClip => _loopClip;
        public AnimationClip? EndClip => _endClip;
        public AnimationClip? CancelClip => _cancelClip;
        public InteractionAnimationBodyMode BodyMode => _bodyMode;
        public AvatarMask? AvatarMask => _avatarMask;
        public bool FaceTarget => _faceTarget;
        public float BlendSeconds => Mathf.Max(0f, _blendSeconds);

        public void Configure(
            string profileId,
            AnimationClip? startClip,
            AnimationClip? loopClip,
            AnimationClip? endClip,
            AnimationClip? cancelClip,
            InteractionAnimationBodyMode bodyMode,
            AvatarMask? avatarMask,
            bool faceTarget,
            float blendSeconds)
        {
            _profileId = NormalizeId(profileId);
            _startClip = startClip;
            _loopClip = loopClip;
            _endClip = endClip;
            _cancelClip = cancelClip;
            _bodyMode = bodyMode;
            _avatarMask = avatarMask;
            _faceTarget = faceTarget;
            _blendSeconds = Mathf.Max(0f, blendSeconds);
        }

        private void OnValidate()
        {
            _profileId = NormalizeId(_profileId);
            _blendSeconds = Mathf.Max(0f, _blendSeconds);
        }

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }

    public static class WorldInteractionAnimationProfileCatalog
    {
        private const string ResourceFolder = "InteractionAnimations";
        private static Dictionary<string, WorldInteractionAnimationProfile>? _profiles;

        public static bool TryResolve(
            string profileId,
            out WorldInteractionAnimationProfile profile)
        {
            EnsureLoaded();
            return _profiles!.TryGetValue(NormalizeId(profileId), out profile!);
        }

        internal static void ResetForTests()
        {
            _profiles = null;
        }

        private static void EnsureLoaded()
        {
            if (_profiles != null)
                return;

            _profiles = new Dictionary<string, WorldInteractionAnimationProfile>(
                StringComparer.Ordinal);
            foreach (WorldInteractionAnimationProfile profile in
                     Resources.LoadAll<WorldInteractionAnimationProfile>(ResourceFolder))
            {
                if (string.IsNullOrWhiteSpace(profile.ProfileId))
                    continue;
                _profiles[profile.ProfileId] = profile;
            }
        }

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }
}
