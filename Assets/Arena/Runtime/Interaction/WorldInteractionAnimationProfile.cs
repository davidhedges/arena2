#nullable enable

using UnityEngine;

namespace Arena.Interaction
{
    public enum InteractionAnimationBodyMode
    {
        FullBody = 0,
        UpperBody = 1,
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
}
