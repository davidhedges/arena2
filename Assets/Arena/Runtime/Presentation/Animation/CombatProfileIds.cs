#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    public static class CombatProfileIds
    {
        public const string SwordAndShield = "SWORD_AND_SHIELD";
        public const string TwoHandedSword = "TWO_HANDED_SWORD";
        public const string ArcherBow = "ARCHER_BOW";
        public const string Daggers = "DAGGERS";
        public const string Staff = "STAFF";

        public static string Default => SwordAndShield;

        public static string Normalize(string? combatProfileId)
        {
            if (string.IsNullOrWhiteSpace(combatProfileId))
                return Default;

            return combatProfileId.Trim().ToUpperInvariant();
        }

    }

    public static class CombatAnimationSetCatalog
    {
        private const string ResourceFolder = "CombatAnimationSets";
        private static readonly IReadOnlyDictionary<string, string> ResourcePathByCombatProfile =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CombatProfileIds.SwordAndShield] = $"{ResourceFolder}/SwordAndShield",
                [CombatProfileIds.TwoHandedSword] = $"{ResourceFolder}/TwoHandedSword",
                [CombatProfileIds.ArcherBow] = $"{ResourceFolder}/ArcherBow",
                [CombatProfileIds.Daggers] = $"{ResourceFolder}/Daggers",
                [CombatProfileIds.Staff] = $"{ResourceFolder}/Staff",
            };
        private static readonly Dictionary<string, CombatAnimationSet?> SetsByCombatProfile =
            new(StringComparer.OrdinalIgnoreCase);

        public static CombatAnimationSet? Resolve(string? combatProfileId)
        {
            string normalizedCombatProfileId = CombatProfileIds.Normalize(combatProfileId);
            if (SetsByCombatProfile.TryGetValue(normalizedCombatProfileId, out CombatAnimationSet? cached))
                return cached;

            if (ResourcePathByCombatProfile.TryGetValue(normalizedCombatProfileId, out string resourcePath))
            {
                CombatAnimationSet? loaded = Resources.Load<CombatAnimationSet>(resourcePath);
                RegisterPreloaded(loaded);
                if (SetsByCombatProfile.TryGetValue(normalizedCombatProfileId, out cached))
                    return cached;
            }

            SetsByCombatProfile[normalizedCombatProfileId] = null;
            return null;
        }

        internal static void RegisterPreloaded(CombatAnimationSet? set)
        {
            if (set == null)
                return;

            string profileId = CombatProfileIds.Normalize(set.CombatProfileIdOrDefault);
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            SetsByCombatProfile[profileId] = set;
        }
    }
}
