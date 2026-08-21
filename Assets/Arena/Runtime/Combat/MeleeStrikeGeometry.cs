#nullable enable
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>
    /// Range/facing math shared by the melee press-time gate
    /// (MeleeInputHandler.TryTriggerAction) and the predicted contact cue's
    /// advisory hit test (feel audit F5 slice 2). Pure math against whatever
    /// positions the caller samples — the press gate feeds predicted/current
    /// positions, the advisory test feeds rendered positions at the authored
    /// first hit window. Advisory results never touch gameplay state.
    /// </summary>
    public static class MeleeStrikeGeometry
    {
        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Mathf.Sqrt(
                (a.x - b.x) * (a.x - b.x) +
                (a.z - b.z) * (a.z - b.z));
        }

        public static float MaximumContactDistance(float strikeRange, float targetRadius)
            => strikeRange + Mathf.Max(0f, targetRadius);

        public static float MinimumContactDistance(float minimumRange, float targetRadius)
            => minimumRange + Mathf.Max(0f, targetRadius);

        public static float ImpactReachLoopScale(
            float horizontalDistance,
            float impactRange,
            float targetRadius)
        {
            float impactContactDistance = MaximumContactDistance(
                Mathf.Max(0f, impactRange),
                targetRadius);
            return impactContactDistance > 0.001f
                ? Mathf.Clamp01(Mathf.Max(0f, horizontalDistance) / impactContactDistance)
                : 1f;
        }

        public static bool ShouldActivateGapCloseOutsideImpactReach(
            float horizontalDistance,
            float impactRange,
            float targetRadius)
        {
            return horizontalDistance > MaximumContactDistance(
                Mathf.Max(0f, impactRange),
                targetRadius);
        }

        /// <summary>
        /// Combined range gate: inside the strike's reach and, when a minimum
        /// range is authored, outside it. Boundary values pass, matching the
        /// press gate's strict "> max" / "&lt; min" rejections.
        /// </summary>
        public static bool PassesRangeGate(
            float horizontalDistance,
            float strikeRange,
            float minimumRange,
            float targetRadius)
        {
            if (horizontalDistance > MaximumContactDistance(strikeRange, targetRadius))
                return false;

            return minimumRange <= 0f
                || horizontalDistance >= MinimumContactDistance(minimumRange, targetRadius);
        }

        /// <summary>
        /// Frontal-hemisphere facing arc. Degenerate deltas or forwards pass,
        /// matching the press gate's permissive fallback.
        /// </summary>
        public static bool IsWithinFacingArc(
            Vector3 attackerForward,
            Vector3 attackerPosition,
            Vector3 targetPosition)
        {
            Vector3 delta = targetPosition - attackerPosition;
            delta.y = 0.0f;
            if (delta.sqrMagnitude <= 0.0001f)
                return true;

            Vector3 forward = attackerForward;
            forward.y = 0.0f;
            if (forward.sqrMagnitude <= 0.0001f)
                return true;

            float dot = Vector3.Dot(forward.normalized, delta.normalized);
            return dot >= 0.0f;
        }
    }
}
