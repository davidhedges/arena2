#nullable enable

using Arena.Entity;
using Arena.Network;
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>
    /// S8 attacker-view report (docs/lag-compensation-design-2026-07-04.md):
    /// the server-time at which the pose of the pressed target was rendered,
    /// i.e. ServerNowMs minus the delay that entity's presentation is paying.
    /// 0 means "no report" — the server validates present-time, exactly the
    /// pre-S8 behavior. Sent on hostile targeted presses; the server clamps
    /// the claim (≤ 250 ms) and kill-switches its use.
    /// </summary>
    public static class AttackerViewTime
    {
        public static ulong ViewServerTimeMsFor(ICombatTargetEntity? target)
        {
            if (target == null || !ArenaServerClock.HasPreciseSample)
            {
                return 0UL;
            }

            float delayMs = target.PresentationEffectiveDelayMs;
            if (delayMs <= 0f || float.IsNaN(delayMs) || float.IsInfinity(delayMs))
            {
                return 0UL;
            }

            long viewMs = ArenaServerClock.ServerNowMs - (long)Mathf.Round(delayMs);
            return viewMs > 0 ? (ulong)viewMs : 0UL;
        }

        /// <summary>
        /// S10 (docs/sweep-projectile-rewind-design-2026-07-05.md, G2): the
        /// view report for a no-target (area) cast — a cone/radius sweep has
        /// no single entity to derive a per-target delay from. Uses the shared
        /// S7 per-connection delay budget (the honest render delay every remote
        /// entity on the connection is seen at), so all sweep victims rewind by
        /// one caster-level delay. 0 without a precise clock, exactly like the
        /// per-target report.
        /// </summary>
        public static ulong ViewServerTimeMsForConnection()
        {
            if (!ArenaServerClock.HasPreciseSample)
            {
                return 0UL;
            }

            float delayMs = Arena.Simulation.ServerTimeDelayBudget.LastAppliedBudgetMs;
            if (delayMs <= 0f || float.IsNaN(delayMs) || float.IsInfinity(delayMs))
            {
                return 0UL;
            }

            long viewMs = ArenaServerClock.ServerNowMs - (long)Mathf.Round(delayMs);
            return viewMs > 0 ? (ulong)viewMs : 0UL;
        }
    }
}
