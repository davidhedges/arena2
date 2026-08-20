#nullable enable

using System;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>
    /// Client presentation derived from the authoritative Stalk mark. While the
    /// caster's shadow is attached to someone the keybind is a Shadowstep button:
    /// the server redirects the press, so the icon and tooltip follow it rather
    /// than describing a mark that is already out.
    /// </summary>
    public static class StalkPresentation
    {
        public const string AbilityId = "DAGGER_STALK";
        public const string ActionId = "STALK";
        public const string MarkedPresentationId = "DAGGER_STALK_SHADOWSTEP";
        /// <summary>Runtime action id the server redirects the press to; also the cooldown key.</summary>
        public const string FollowUpActionId = "STALK_SHADOWSTEP";

        /// <summary>Stack group, not status kind, to match how the server authors the mark.</summary>
        public const string MarkStatusStackGroup = "STALKED";
        public static readonly Color ReadyColor = new(0.62f, 0.45f, 0.92f, 0.98f);

        public static bool IsStalkAction(ActiveActionBarAction action) =>
            IsStalkAbility(action.AbilityId, action.AuthoredActionId)
            || IsStalkAbility(action.AbilityId, action.ActionId);

        public static bool IsStalkAbility(string abilityId, string actionId = "") =>
            string.Equals(WireIdentifier.Normalize(abilityId), AbilityId, StringComparison.Ordinal)
            || string.Equals(WireIdentifier.Normalize(actionId), ActionId, StringComparison.Ordinal);

        /// <summary>True while a press would be redirected into the shadow step by the server.</summary>
        public static bool IsShadowAttached(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            if (conn == null || !owner.HasValue)
                return false;

            long nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            // Source, not Target: the mark rides the victim, and it is the caster
            // who owns the press it changes.
            foreach (StatusEffect status in conn.Db.StatusEffect.Source.Filter(owner.Value))
            {
                if (status.ExpiresAtMicros > nowMicros
                    && string.Equals(
                        WireIdentifier.Normalize(status.StackGroup),
                        MarkStatusStackGroup,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static string ResolvePresentationId(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string abilityId,
            string actionId = "") =>
            IsStalkAbility(abilityId, actionId) && IsShadowAttached(conn, owner)
                ? MarkedPresentationId
                : WireIdentifier.Normalize(abilityId);

        /// <summary>
        /// Reads the follow-up's authored row rather than restating it, so the
        /// swapped tooltip cannot drift from the catalog.
        /// </summary>
        public static TooltipData ShadowstepTooltip(DbConnection? conn)
        {
            ActionPresentationCatalog? presentation = ActionPresentation.FindPresentation(
                conn,
                ActionTooltipResolver.PresentationKindAbility,
                MarkedPresentationId);
            return new TooltipData(
                string.IsNullOrWhiteSpace(presentation?.DisplayName) ? "Stalk" : presentation.DisplayName,
                "Shadow Attached",
                presentation?.Description ?? string.Empty,
                ReadyColor,
                footnote: "Your shadow is already out; this press spends it.");
        }
    }
}
