#nullable enable

using System;
using Arena.Combat;
using Arena.Debugging;
using Arena.Network;
using Arena.Simulation;
using SpacetimeDB.Types;

namespace Arena.Input
{
    /// <summary>
    /// Intercepts a second press of the movement action that created the local
    /// Lingering Shade anchor. The authoritative row is the single source of
    /// truth for availability and expiry; normal action dispatch resumes once
    /// the row is absent or expired.
    /// </summary>
    public static class LingeringShadeInput
    {
        public const string ReturnActionKind = "LINGERING_SHADE_RETURN";

        public static bool TryConsumeRecast(DbConnection conn, ActiveActionBarAction action)
        {
            if (!TryGetMatchingAnchor(conn, action.ActionId, action.AbilityId, out LingeringShadeState? anchor)
                || anchor == null)
                return false;

            DispatchReturn(conn, anchor);
            return true;
        }

        public static bool TryConsumeFixedRecast(DbConnection conn, string actionId)
        {
            if (!TryGetMatchingAnchor(conn, actionId, string.Empty, out LingeringShadeState? anchor)
                || anchor == null)
                return false;

            DispatchReturn(conn, anchor);
            return true;
        }

        public static bool TryGetMatchingAnchor(
            DbConnection? conn,
            string actionId,
            string abilityId,
            out LingeringShadeState? anchor)
        {
            anchor = null;
            if (conn == null || !conn.Identity.HasValue)
                return false;

            LingeringShadeState? candidate = conn.Db.LingeringShadeState.Owner.Find(conn.Identity.Value);
            if (candidate == null || RemainingMilliseconds(candidate) <= 0L)
                return false;

            string normalizedAbilityId = WireIdentifier.Normalize(abilityId);
            bool matchesAbility = !string.IsNullOrWhiteSpace(normalizedAbilityId)
                && string.Equals(
                    WireIdentifier.Normalize(candidate.SourceAbilityId),
                    normalizedAbilityId,
                    StringComparison.Ordinal);
            bool matchesAction = string.Equals(
                WireIdentifier.Normalize(candidate.SourceActionId),
                WireIdentifier.Normalize(actionId),
                StringComparison.Ordinal);
            if (!matchesAbility && !matchesAction)
                return false;

            anchor = candidate;
            return true;
        }

        public static long RemainingMilliseconds(LingeringShadeState anchor)
        {
            long nowMs = ArenaServerClock.HasEstimate
                ? ArenaServerClock.ServerNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Math.Max(0L, (anchor.ExpiresAt.MicrosecondsSinceUnixEpoch / 1000L) - nowMs);
        }

        private static void DispatchReturn(DbConnection conn, LingeringShadeState anchor)
        {
            ActionPredictionToken token = LocalCombatState.Instance.CreateActionPredictionToken(ReturnActionKind);
            conn.Reducers.ReturnToLingeringShade(
                anchor.AnchorId,
                token.PredictedActionId,
                token.ClientActionSeq);
            ActionBarTrace.Trace(
                $"lingering shade return dispatched anchor={anchor.AnchorId} source={anchor.SourceActionId}");
        }
    }
}
