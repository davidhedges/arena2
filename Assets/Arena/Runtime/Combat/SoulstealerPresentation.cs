#nullable enable

using System;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>Client presentation derived from the authoritative Soulstealer status rows.</summary>
    public static class SoulstealerPresentation
    {
        public const string AbilityId = "SPELL_SOULSTEALER";
        public const string ActionId = "SOULSTEALER";
        public const string SoulStolenStatusId = "SOUL_STOLEN";
        // Keep the wire id stable for existing status rows and generated bindings.
        public const string MortalityEmpoweredStatusId = "BLIGHT_EMPOWERED";
        public static readonly Color ReadyColor = new(0.55f, 0.20f, 0.82f, 0.98f);

        public static bool IsSoulstealerAction(ActiveActionBarAction action) =>
            IsSoulstealerAbility(action.AbilityId, action.AuthoredActionId)
            || IsSoulstealerAbility(action.AbilityId, action.ActionId);

        public static bool IsSoulstealerAbility(string abilityId, string actionId = "") =>
            string.Equals(WireIdentifier.Normalize(abilityId), AbilityId, StringComparison.Ordinal)
            || string.Equals(WireIdentifier.Normalize(actionId), ActionId, StringComparison.Ordinal);

        public static bool HasStolenSoul(DbConnection? conn, SpacetimeDB.Identity? owner) =>
            HasActiveStatus(conn, owner, SoulStolenStatusId);

        public static bool HasEmpoweredMortality(DbConnection? conn, SpacetimeDB.Identity? owner) =>
            HasActiveStatus(conn, owner, MortalityEmpoweredStatusId);

        public static TooltipData EmpowerTooltip() => new(
            "Empower",
            "Stolen Soul Ready",
            "Press to consume the stolen soul and empower your next damaging Mortality spell by 50%.",
            ReadyColor,
            footnote: "Empower is armed only by pressing this Soulstealer keybind again.");

        private static bool HasActiveStatus(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string statusKind)
        {
            if (conn == null || !owner.HasValue)
                return false;

            long nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            foreach (StatusEffect status in conn.Db.StatusEffect.Target.Filter(owner.Value))
            {
                if (status.ExpiresAtMicros > nowMicros
                    && string.Equals(
                        WireIdentifier.Normalize(status.EffectKind),
                        statusKind,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
