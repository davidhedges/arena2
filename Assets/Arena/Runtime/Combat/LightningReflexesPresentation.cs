#nullable enable

using System;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>
    /// Client presentation derived from the authoritative Lightning Reflexes status row.
    /// While the dodge window is up the keybind is a Trip button: the server redirects the
    /// press, so the icon, name and tooltip follow it rather than describing a spell that
    /// cannot be cast twice.
    /// </summary>
    public static class LightningReflexesPresentation
    {
        public const string AbilityId = "DAGGER_LIGHTNING_REFLEXES";
        public const string ActionId = "LIGHTNING_REFLEXES";
        public const string ArmedPresentationId = "DAGGER_TRIP";
        public const string ArmedDisplayName = "Trip";

        /// <summary>Stack group, not status kind: other buffs share TARGETED_ABILITY_AVOIDANCE.</summary>
        public const string ArmedStatusStackGroup = "LIGHTNING_REFLEXES";
        public static readonly Color ReadyColor = new(0.98f, 0.85f, 0.25f, 0.98f);

        public static bool IsLightningReflexesAction(ActiveActionBarAction action) =>
            IsLightningReflexesAbility(action.AbilityId, action.AuthoredActionId)
            || IsLightningReflexesAbility(action.AbilityId, action.ActionId);

        public static bool IsLightningReflexesAbility(string abilityId, string actionId = "") =>
            string.Equals(WireIdentifier.Normalize(abilityId), AbilityId, StringComparison.Ordinal)
            || string.Equals(WireIdentifier.Normalize(actionId), ActionId, StringComparison.Ordinal);

        /// <summary>True while a press would be redirected into Trip by the server.</summary>
        public static bool IsArmed(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            if (conn == null || !owner.HasValue)
                return false;

            long nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            foreach (StatusEffect status in conn.Db.StatusEffect.Target.Filter(owner.Value))
            {
                if (status.ExpiresAtMicros > nowMicros
                    && string.Equals(
                        WireIdentifier.Normalize(status.StackGroup),
                        ArmedStatusStackGroup,
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
            IsLightningReflexesAbility(abilityId, actionId) && IsArmed(conn, owner)
                ? ArmedPresentationId
                : WireIdentifier.Normalize(abilityId);

        public static TooltipData TripTooltip() => new(
            ArmedDisplayName,
            "Reflexes Ready",
            "Sweeps the legs of everyone within 4 meters, stunning Off Balance targets for 5 seconds. Spends the remaining dodge window.",
            ReadyColor,
            footnote: "Only enemies you have already dodged are Off Balance.");
    }
}
