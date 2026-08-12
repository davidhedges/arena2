#nullable enable

using System;
using System.Collections.Generic;
using Arena.Combat;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.UI
{
    public static class ActionIconResolver
    {
        private const string ResourceRoot = "UI/AbilityIcons";
        private static readonly Dictionary<string, Sprite?> Cache = new(StringComparer.Ordinal);

        public static Sprite? ResolveForAction(ActiveActionBarAction action)
            => ResolveForAction(null, null, action);

        public static Sprite? ResolveForAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            ActiveActionBarAction action)
        {
            if (!action.HasAssignedAction)
                return null;

            if (action.IsFixed)
                return Resolve(ActionKinds.Fixed, action.ActionId);

            if (action.IsCombatDisciplineSwitch)
                return Resolve(ActionKinds.CombatDisciplineSwitch, action.ActionId);

            return Resolve(
                ActionKinds.Ability,
                CapacitorPresentation.ResolvePresentationId(
                    conn,
                    owner,
                    action.AbilityId,
                    action.ActionId));
        }

        public static Sprite? ResolveForAvailableAction(string actionKind, string actionId, string abilityId)
            => ResolveForAvailableAction(null, null, actionKind, actionId, abilityId);

        public static Sprite? ResolveForAvailableAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string actionKind,
            string actionId,
            string abilityId)
        {
            string normalizedKind = WireIdentifier.Normalize(actionKind);
            string normalizedId = string.Equals(normalizedKind, ActionKinds.Ability, StringComparison.Ordinal)
                ? CapacitorPresentation.ResolvePresentationId(conn, owner, abilityId, actionId)
                : WireIdentifier.Normalize(actionId);
            return Resolve(normalizedKind, normalizedId);
        }

        public static Sprite? Resolve(string presentationKind, string presentationId)
        {
            string normalizedKind = WireIdentifier.Normalize(presentationKind);
            string normalizedId = WireIdentifier.Normalize(presentationId);
            if (string.IsNullOrWhiteSpace(normalizedKind) || string.IsNullOrWhiteSpace(normalizedId))
                return null;

            string cacheKey = $"{normalizedKind}:{normalizedId}";
            if (Cache.TryGetValue(cacheKey, out Sprite? cached))
                return cached;

            string resourcePath = $"{ResourceRoot}/{normalizedKind}/{normalizedId}";
            Sprite? loaded = Resources.Load<Sprite>(resourcePath);
            Cache[cacheKey] = loaded;
            return loaded;
        }
    }
}
