#nullable enable

using System;
using UnityEngine;

namespace Arena.Interaction
{
    [Flags]
    public enum WorldInteractionCancelCondition
    {
        None = 0,
        Movement = 1 << 0,
        Displacement = 1 << 1,
        Damage = 1 << 2,
        Death = 1 << 3,
        WorldChange = 1 << 4,
        RangeOrLineOfAccess = 1 << 5,
        ConflictingCombatAction = 1 << 6,
        TargetRevisionChanged = 1 << 7,
    }

    [CreateAssetMenu(
        fileName = "WorldInteractionProfile",
        menuName = "Arena/Interaction/World Interaction Profile")]
    public sealed class WorldInteractionProfile : ScriptableObject
    {
        [SerializeField] private string _profileId = string.Empty;
        [SerializeField] private string _progressLabelKey = "USING";
        [SerializeField, Min(0)] private int _durationMs;
        [SerializeField] private string _animationProfileId = string.Empty;
        [SerializeField] private bool _requiresGrounded = true;
        [SerializeField] private bool _requiresStationary = true;
        [SerializeField] private WorldInteractionCancelCondition _cancelConditions =
            WorldInteractionCancelCondition.Movement
            | WorldInteractionCancelCondition.Displacement
            | WorldInteractionCancelCondition.Damage
            | WorldInteractionCancelCondition.Death
            | WorldInteractionCancelCondition.WorldChange
            | WorldInteractionCancelCondition.RangeOrLineOfAccess
            | WorldInteractionCancelCondition.ConflictingCombatAction
            | WorldInteractionCancelCondition.TargetRevisionChanged;

        public string ProfileId => NormalizeId(_profileId);
        public string ProgressLabelKey => NormalizeId(_progressLabelKey);
        public int DurationMs => Mathf.Max(0, _durationMs);
        public string AnimationProfileId => NormalizeId(_animationProfileId);
        public bool RequiresGrounded => _requiresGrounded;
        public bool RequiresStationary => _requiresStationary;
        public WorldInteractionCancelCondition CancelConditions => _cancelConditions;

        public void Configure(
            string profileId,
            string progressLabelKey,
            int durationMs,
            string animationProfileId,
            bool requiresGrounded,
            bool requiresStationary,
            WorldInteractionCancelCondition cancelConditions)
        {
            _profileId = NormalizeId(profileId);
            _progressLabelKey = NormalizeId(progressLabelKey);
            _durationMs = Mathf.Max(0, durationMs);
            _animationProfileId = NormalizeId(animationProfileId);
            _requiresGrounded = requiresGrounded;
            _requiresStationary = requiresStationary;
            _cancelConditions = cancelConditions;
        }

        private void OnValidate()
        {
            _profileId = NormalizeId(_profileId);
            _progressLabelKey = NormalizeId(_progressLabelKey);
            _animationProfileId = NormalizeId(_animationProfileId);
            _durationMs = Mathf.Max(0, _durationMs);
        }

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }
}
