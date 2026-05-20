#nullable enable
using Arena.Network;
using UnityEngine;

namespace Arena.Presentation
{
    public static class CombatAnimationRemoteTiming
    {
        public const float MaxRemoteCatchupSeconds = 0.20f;
        public const float HitWindowSafetyMarginSeconds = 0.05f;

        public static bool TryResolveStartNormalizedTime(
            in CombatAnimationRequest request,
            bool isLocalPlayer,
            float timingReferenceLengthSeconds,
            float playedClipLengthSeconds,
            float firstHitWindowSeconds,
            out float normalizedStart,
            out float appliedCatchupSeconds)
        {
            normalizedStart = 0f;
            appliedCatchupSeconds = 0f;

            if (isLocalPlayer
                || request.Authority != CombatAnimationAuthority.Authoritative
                || request.StartedAtMs <= 0L
                || !ArenaServerClock.HasEstimate)
            {
                return false;
            }

            if (request.Category != CombatAnimationCategory.MeleeSkill
                && request.Category != CombatAnimationCategory.AutoAttack
                && request.Category != CombatAnimationCategory.Spell)
            {
                return false;
            }

            if (timingReferenceLengthSeconds <= 0.001f || playedClipLengthSeconds <= 0.001f)
                return false;

            float contactCeilingSeconds =
                ScaleAuthoredSeconds(firstHitWindowSeconds, timingReferenceLengthSeconds, playedClipLengthSeconds)
                - HitWindowSafetyMarginSeconds;
            if (contactCeilingSeconds <= 0f)
                return false;

            long eventAgeMs = ArenaServerClock.ServerNowMs - request.StartedAtMs;
            if (eventAgeMs <= 0L)
                return false;

            appliedCatchupSeconds = Mathf.Min(
                eventAgeMs / 1000f,
                MaxRemoteCatchupSeconds,
                contactCeilingSeconds);
            if (appliedCatchupSeconds <= 0.001f)
                return false;

            normalizedStart = Mathf.Clamp01(appliedCatchupSeconds / playedClipLengthSeconds);
            return normalizedStart > 0f;
        }

        private static float ScaleAuthoredSeconds(
            float authoredSeconds,
            float timingReferenceLengthSeconds,
            float playedClipLengthSeconds)
        {
            if (authoredSeconds <= 0f
                || timingReferenceLengthSeconds <= 0.001f
                || playedClipLengthSeconds <= 0.001f)
            {
                return authoredSeconds;
            }

            return authoredSeconds * (playedClipLengthSeconds / timingReferenceLengthSeconds);
        }
    }
}
