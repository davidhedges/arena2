#nullable enable

using System;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>Client presentation derived from the authoritative CapacitorState row.</summary>
    public static class CapacitorPresentation
    {
        public const string AbilityId = "SPELL_CAPACITOR";
        public const string ActionId = "CAPACITOR";
        public const string ChargedPresentationId = "SPELL_CAPACITOR_DISCHARGE";
        public const uint MaxCharges = 5;
        public const int DamagePerCharge = 40;
        public static readonly Color ReadyBorderColor = new(0.15f, 0.78f, 1f, 0.98f);

        public static bool IsCapacitorAction(ActiveActionBarAction action) =>
            string.Equals(WireIdentifier.Normalize(action.AbilityId), AbilityId, StringComparison.Ordinal)
            || string.Equals(WireIdentifier.Normalize(action.AuthoredActionId), ActionId, StringComparison.Ordinal)
            || string.Equals(WireIdentifier.Normalize(action.ActionId), ActionId, StringComparison.Ordinal);

        public static bool IsCapacitorAbility(string abilityId, string actionId = "") =>
            string.Equals(WireIdentifier.Normalize(abilityId), AbilityId, StringComparison.Ordinal)
            || string.Equals(WireIdentifier.Normalize(actionId), ActionId, StringComparison.Ordinal);

        public static uint ChargeCount(DbConnection? conn, SpacetimeDB.Identity? owner) =>
            conn != null && owner.HasValue
                ? Math.Min(conn.Db.CapacitorState.Owner.Find(owner.Value)?.ChargeCount ?? 0, MaxCharges)
                : 0;

        public static bool IsCharged(DbConnection? conn, SpacetimeDB.Identity? owner) =>
            ChargeCount(conn, owner) > 0;

        public static string ResolvePresentationId(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string abilityId,
            string actionId = "") =>
            IsCapacitorAbility(abilityId, actionId) && IsCharged(conn, owner)
                ? ChargedPresentationId
                : WireIdentifier.Normalize(abilityId);

        public static TooltipData DischargeTooltip(
            DbConnection? conn,
            SpacetimeDB.Identity? owner)
        {
            uint charges = Math.Max(1, ChargeCount(conn, owner));
            int damage = DamagePerCharge * (int)charges;
            return new TooltipData(
                "Discharge",
                $"Stored Charge: {charges}/{MaxCharges}",
                $"Instantly releases all stored energy through a hostile target, dealing {damage} lightning damage to enemies in an 18-meter by 2.5-meter column.",
                ReadyBorderColor,
                footnote: "Each critical strike with a lightning spell stores 40 damage, up to five times.");
        }
    }
}
