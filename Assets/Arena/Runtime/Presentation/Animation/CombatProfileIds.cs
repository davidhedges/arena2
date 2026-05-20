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
        private static readonly Dictionary<string, CombatAnimationSet?> SetsByCombatProfile =
            new(StringComparer.OrdinalIgnoreCase);
        private static bool _loadedAll;

        public static CombatAnimationSet? Resolve(string? combatProfileId)
        {
            string normalizedCombatProfileId = CombatProfileIds.Normalize(combatProfileId);
            if (SetsByCombatProfile.TryGetValue(normalizedCombatProfileId, out CombatAnimationSet? cached))
                return cached;

            LoadAll();
            if (SetsByCombatProfile.TryGetValue(normalizedCombatProfileId, out cached))
                return cached;

            SetsByCombatProfile[normalizedCombatProfileId] = null;
            return null;
        }

        private static void LoadAll()
        {
            if (_loadedAll)
                return;

            foreach (CombatAnimationSet set in Resources.LoadAll<CombatAnimationSet>(ResourceFolder))
            {
                string profileId = CombatProfileIds.Normalize(set.CombatProfileIdOrDefault);
                if (string.IsNullOrWhiteSpace(profileId))
                    continue;

                SetsByCombatProfile[profileId] = set;
            }

            _loadedAll = true;
        }
    }
}
