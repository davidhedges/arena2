#nullable enable
using UnityEngine;

namespace Arena.Presentation.Dice
{
    [CreateAssetMenu(menuName = "Arena/Dice/Dice Motion Profile")]
    public sealed class DiceMotionProfile : ScriptableObject
    {
        [SerializeField] private string profileId = string.Empty;
        [SerializeField] private string displayName = string.Empty;
        [SerializeField, Min(0f)] private float anticipationDuration = 0.28f;
        [SerializeField, Min(0.1f)] private float movingDuration = 1.2f;
        [SerializeField, Range(0.1f, 0.95f)] private float settleStart = 0.68f;
        [SerializeField] private Vector3 entryEuler;
        [SerializeField] private Vector3 spinAxis = new(0.7f, 0.6f, 0.2f);
        [SerializeField, Min(0.25f)] private float turnCount = 4.5f;
        [SerializeField] private AnimationCurve horizontalTravel = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        [SerializeField] private AnimationCurve verticalTravel = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        [SerializeField] private AnimationCurve depthArc = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        [SerializeField] private AnimationCurve scaleArc = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [SerializeField] private AnimationCurve positionEasing = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [SerializeField] private AnimationCurve rotationEasing = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public string ProfileId => profileId;
        public string DisplayName => displayName;
        public float AnticipationDuration => anticipationDuration;
        public float MovingDuration => movingDuration;
        public float TotalDuration => anticipationDuration + movingDuration;
        public float SettleStart => settleStart;
        public Vector3 EntryEuler => entryEuler;
        public Vector3 SpinAxis => spinAxis.sqrMagnitude > 0.0001f ? spinAxis.normalized : Vector3.up;
        public float TurnCount => turnCount;

        public float EvaluateHorizontal(float normalizedTime)
            => Evaluate(horizontalTravel, normalizedTime, 0f);

        public float EvaluateVertical(float normalizedTime)
            => Evaluate(verticalTravel, normalizedTime, 0f);

        public float EvaluateDepth(float normalizedTime)
            => Evaluate(depthArc, normalizedTime, 0f);

        public float EvaluateScale(float normalizedTime)
            => Mathf.Max(0.01f, Evaluate(scaleArc, normalizedTime, 1f));

        public float EvaluatePositionTime(float normalizedTime)
            => Mathf.Clamp01(Evaluate(positionEasing, normalizedTime, normalizedTime));

        public float EvaluateSettle(float normalizedSettleTime)
            => Mathf.Clamp01(Evaluate(rotationEasing, normalizedSettleTime, normalizedSettleTime));

        public void SetAuthoringData(
            string stableProfileId,
            string authoredDisplayName,
            float authoredAnticipationDuration,
            float authoredMovingDuration,
            float authoredSettleStart,
            Vector3 authoredEntryEuler,
            Vector3 authoredSpinAxis,
            float authoredTurnCount,
            AnimationCurve authoredHorizontalTravel,
            AnimationCurve authoredVerticalTravel,
            AnimationCurve authoredDepthArc,
            AnimationCurve authoredScaleArc,
            AnimationCurve authoredPositionEasing,
            AnimationCurve authoredRotationEasing)
        {
            profileId = stableProfileId ?? string.Empty;
            displayName = authoredDisplayName ?? string.Empty;
            anticipationDuration = authoredAnticipationDuration;
            movingDuration = authoredMovingDuration;
            settleStart = authoredSettleStart;
            entryEuler = authoredEntryEuler;
            spinAxis = authoredSpinAxis.normalized;
            turnCount = authoredTurnCount;
            horizontalTravel = authoredHorizontalTravel;
            verticalTravel = authoredVerticalTravel;
            depthArc = authoredDepthArc;
            scaleArc = authoredScaleArc;
            positionEasing = authoredPositionEasing;
            rotationEasing = authoredRotationEasing;
        }

        private static float Evaluate(AnimationCurve? curve, float normalizedTime, float fallback)
        {
            return curve != null && curve.length > 0
                ? curve.Evaluate(Mathf.Clamp01(normalizedTime))
                : fallback;
        }
    }
}
