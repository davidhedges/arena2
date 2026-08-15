#nullable enable

using Arena.Presentation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Combat
{
    public static class ActionTooltipResolver
    {
        public const string PresentationKindAbility = "ABILITY";
        public const string PresentationKindSpell = "SPELL";
        public const string PresentationKindFixed = "FIXED";
        public const string PresentationKindStatus = "STATUS";
        public const string PresentationKindCombatDisciplineSwitch = "COMBAT_DISCIPLINE_SWITCH";

        public static TooltipData ResolveForSelectableAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            ActiveActionBarAction action)
        {
            if (!action.HasAssignedAction)
                return default;
            if (action.IsFixed)
                return ResolveForFixedAction(conn, action.ActionId);
            if (action.IsCombatDisciplineSwitch)
                return ResolveForCombatDisciplineSwitch(conn, action);
            if (SoulstealerPresentation.IsSoulstealerAction(action)
                && SoulstealerPresentation.HasStolenSoul(conn, owner))
            {
                return SoulstealerPresentation.EmpowerTooltip();
            }
            if (CapacitorPresentation.IsCapacitorAction(action)
                && CapacitorPresentation.IsCharged(conn, owner))
            {
                return CapacitorPresentation.DischargeTooltip(conn, owner);
            }

            ActionPresentationCatalog? presentation =
                ActionPresentation.FindPresentation(conn, PresentationKindAbility, action.AbilityId)
                ?? ActionPresentation.FindActionPresentation(conn, action.AuthoredActionId)
                ?? ActionPresentation.FindActionPresentation(conn, action.ActionId);

            string displayName = !string.IsNullOrWhiteSpace(presentation?.DisplayName)
                ? presentation.DisplayName
                : action.DisplayName;

            string resourceKind = action.ResourceKind;
            float resourceCost = action.ResourceCost;
            SpellDefinition? spellDefinition = ResolveSpellDefinition(conn, action.AuthoredActionId, action.ActionId);
            bool costIsPerSecond = false;
            if (resourceCost <= 0.0001f && spellDefinition?.PrimaryResourceCost > 0.0001f)
            {
                resourceCost = spellDefinition.PrimaryResourceCost;
                costIsPerSecond = SpellDefinitionContracts.UsesPerSecondResourceCost(spellDefinition);
                if (string.IsNullOrWhiteSpace(resourceKind))
                    resourceKind = "MANA";
            }
            if (string.IsNullOrWhiteSpace(resourceKind) && resourceCost > 0.0001f)
                resourceKind = ResolvePrimaryResourceKind(conn, owner);

            return new TooltipData(
                displayName,
                FormatCost(ResolveResourceDisplayName(conn, resourceKind), resourceCost, costIsPerSecond),
                presentation?.Description ?? string.Empty);
        }

        public static TooltipData ResolveForAbility(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            AbilityCatalog? ability)
        {
            if (ability == null)
                return default;

            string runtimeActionId = AbilityKinds.UsesRawActionId(ability.AbilityKind)
                ? WireIdentifier.Normalize(ability.ActionId)
                : CombatActionIds.ResolveRuntimeActionId(
                    conn,
                    CombatProfileResolver.ResolveForOwner(conn, owner),
                    ability.ActionId);

            return ResolveForSelectableAction(
                conn,
                owner,
                new ActiveActionBarAction(
                    string.Empty,
                    ability.AbilityId,
                    ability.ActionId,
                    runtimeActionId,
                    ability.DisplayName,
                    ability.ResourceKind,
                    ability.ResourceCost,
                    ability.AbilityKind));
        }

        public static TooltipData ResolveForActionRef(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            ActiveActionBarAction action)
        {
            return action.IsFixed
                ? ResolveForFixedAction(conn, action.ActionId)
                : ResolveForSelectableAction(conn, owner, action);
        }

        public static TooltipData ResolveForFixedAction(DbConnection? conn, string actionId)
        {
            if (conn == null || string.IsNullOrWhiteSpace(actionId))
                return default;

            string normalizedActionId = WireIdentifier.Normalize(actionId);
            ActionPresentationCatalog? presentation =
                ActionPresentation.FindPresentation(conn, PresentationKindFixed, normalizedActionId);
            if (presentation == null)
                return default;

            return new TooltipData(
                presentation.DisplayName,
                string.Empty,
                presentation.Description);
        }

        public static TooltipData ResolveForCombatDisciplineSwitch(
            DbConnection? conn,
            ActiveActionBarAction action)
        {
            if (conn == null || string.IsNullOrWhiteSpace(action.ActionId))
                return default;

            string normalizedActionId = WireIdentifier.Normalize(action.ActionId);
            ActionPresentationCatalog? presentation =
                ActionPresentation.FindPresentation(conn, PresentationKindCombatDisciplineSwitch, normalizedActionId);
            string displayName = string.IsNullOrWhiteSpace(presentation?.DisplayName)
                ? action.DisplayName
                : presentation.DisplayName;
            string metadata = action.IsAvailable ? string.Empty : "No saved weapon loadout";
            return new TooltipData(displayName, action.ResourceKind, metadata);
        }

        private static SpellDefinition? ResolveSpellDefinition(
            DbConnection? conn,
            string authoredActionId,
            string runtimeActionId)
        {
            if (conn == null)
                return null;

            SpellDefinition? authored = conn.Db.SpellDefinition.Kind.Find(WireIdentifier.Normalize(authoredActionId));
            if (authored != null)
                return authored;

            return conn.Db.SpellDefinition.Kind.Find(WireIdentifier.Normalize(runtimeActionId));
        }

        private static string ResolvePrimaryResourceKind(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            if (conn == null || !owner.HasValue)
                return string.Empty;

            return "STAMINA";
        }

        private static string ResolveResourceDisplayName(DbConnection? conn, string resourceKind)
        {
            if (string.IsNullOrWhiteSpace(resourceKind))
                return string.Empty;

            ResourceCatalog? resource = conn?.Db.ResourceCatalog.ResourceKind.Find(WireIdentifier.Normalize(resourceKind));
            return string.IsNullOrWhiteSpace(resource?.DisplayName)
                ? resourceKind.Trim()
                : resource.DisplayName;
        }

        private static string FormatCost(string resourceLabel, float resourceCost, bool perSecond = false)
        {
            if (resourceCost <= 0.0001f)
                return "Cost: Free";

            string amount = Mathf.Approximately(resourceCost, Mathf.Round(resourceCost))
                ? Mathf.RoundToInt(resourceCost).ToString()
                : resourceCost.ToString("0.#");
            string resource = string.IsNullOrWhiteSpace(resourceLabel) ? "Resource" : resourceLabel;
            return perSecond ? $"Cost: {amount} {resource}/s" : $"Cost: {amount} {resource}";
        }
    }
}
