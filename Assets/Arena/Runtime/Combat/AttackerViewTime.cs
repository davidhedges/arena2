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
    }
}
