#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Combat;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal readonly struct SpellGameplayAuthoringFacts
    {
        public SpellGameplayAuthoringFacts(ulong castTimeMs, string deliveryKind)
        {
            CastTimeMs = castTimeMs;
            DeliveryKind = deliveryKind;
        }

        public ulong CastTimeMs { get; }
        public string DeliveryKind { get; }
    }

    internal readonly struct CombatDisciplineAuthoringFacts
    {
        public CombatDisciplineAuthoringFacts(string disciplineId, string displayName, int sortOrder)
        {
            DisciplineId = disciplineId;
            DisplayName = displayName;
            SortOrder = sortOrder;
        }

        public string DisciplineId { get; }
        public string DisplayName { get; }
        public int SortOrder { get; }
    }

    internal sealed class CombatVfxDisciplineUsage
    {
        public CombatVfxDisciplineUsage(IReadOnlyList<CombatDisciplineAuthoringFacts> disciplines)
        {
            Disciplines = disciplines;
        }

        public IReadOnlyList<CombatDisciplineAuthoringFacts> Disciplines { get; }
    }

    /// <summary>
    /// Shared read boundary for spell-presentation editor tools. Runtime identifiers come from the
    /// catalog's authored <c>action_id</c>; callers never infer them from class/ability prefixes.
    /// </summary>
    internal static class SpellPresentationEditorData
    {
        public const string ProgressionCatalogPath = "server/src/progression_catalog.shared.json";

        public static string AbsoluteProgressionCatalogPath =>
            Path.Combine(Directory.GetCurrentDirectory(), ProgressionCatalogPath);

        public static T? FindFirstAsset<T>() where T : UnityEngine.Object
        {
            string[] paths = AssetDatabase.FindAssets($"t:{typeof(T).Name}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            return paths.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<T>(paths[0]);
        }

        public static CombatAnimationSet[] LoadCombatAnimationSets() =>
            Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets")
                .OrderBy(set => set.name, StringComparer.Ordinal)
                .ToArray();

        public static Dictionary<string, SpellGameplayAuthoringFacts> LoadSpellGameplayByActionId(
            out string warning)
        {
            var result = new Dictionary<string, SpellGameplayAuthoringFacts>(StringComparer.Ordinal);
            warning = string.Empty;
            string path = AbsoluteProgressionCatalogPath;
            if (!File.Exists(path))
            {
                warning = $"Progression catalog not found at '{ProgressionCatalogPath}'.";
                return result;
            }

            try
            {
                CatalogDocument? catalog = JsonUtility.FromJson<CatalogDocument>(File.ReadAllText(path));
                if (catalog?.abilities == null)
                    return result;

                foreach (AbilityRow ability in catalog.abilities)
                {
                    string actionId = WireIdentifier.Normalize(ability.action_id);
                    string deliveryKind = WireIdentifier.Normalize(ability.gameplay?.delivery?.kind);
                    if (actionId.Length == 0 || deliveryKind.Length == 0)
                        continue;

                    result[actionId] = new SpellGameplayAuthoringFacts(
                        (ulong)Math.Max(0L, ability.gameplay!.cast_time_ms),
                        deliveryKind);
                }
            }
            catch (Exception ex)
            {
                warning = $"Progression catalog parse failed: {ex.Message}";
            }

            return result;
        }

        public static Dictionary<string, CombatVfxDisciplineUsage> LoadCombatVfxDisciplineUsage(
            out List<CombatDisciplineAuthoringFacts> disciplines,
            out string warning)
        {
            var result = new Dictionary<string, CombatVfxDisciplineUsage>(StringComparer.Ordinal);
            disciplines = new List<CombatDisciplineAuthoringFacts>();
            warning = string.Empty;
            string path = AbsoluteProgressionCatalogPath;
            if (!File.Exists(path))
            {
                warning = $"Progression catalog not found at '{ProgressionCatalogPath}'.";
                return result;
            }

            try
            {
                CatalogDocument? catalog = JsonUtility.FromJson<CatalogDocument>(File.ReadAllText(path));
                if (catalog == null)
                    return result;

                var disciplineById = new Dictionary<string, CombatDisciplineAuthoringFacts>(StringComparer.Ordinal);
                if (catalog.combat_disciplines != null)
                {
                    foreach (CombatDisciplineRow row in catalog.combat_disciplines)
                    {
                        string disciplineId = WireIdentifier.Normalize(row.discipline_id);
                        if (disciplineId.Length == 0)
                            continue;

                        string displayName = string.IsNullOrWhiteSpace(row.display_name)
                            ? disciplineId
                            : row.display_name.Trim();
                        disciplineById[disciplineId] = new CombatDisciplineAuthoringFacts(
                            disciplineId,
                            displayName,
                            row.sort_order);
                    }
                }

                disciplines.AddRange(disciplineById.Values
                    .OrderBy(row => row.SortOrder)
                    .ThenBy(row => row.DisplayName, StringComparer.Ordinal));

                var abilityById = new Dictionary<string, AbilityRow>(StringComparer.Ordinal);
                var abilitiesByActionId = new Dictionary<string, List<AbilityRow>>(StringComparer.Ordinal);
                if (catalog.abilities != null)
                {
                    foreach (AbilityRow ability in catalog.abilities)
                    {
                        string abilityId = WireIdentifier.Normalize(ability.ability_id);
                        if (abilityId.Length > 0)
                            abilityById[abilityId] = ability;

                        string actionId = WireIdentifier.Normalize(ability.action_id);
                        if (actionId.Length == 0)
                            continue;
                        if (!abilitiesByActionId.TryGetValue(actionId, out List<AbilityRow> actionAbilities))
                        {
                            actionAbilities = new List<AbilityRow>();
                            abilitiesByActionId[actionId] = actionAbilities;
                        }

                        actionAbilities.Add(ability);
                    }
                }

                var disciplineIdsByVfxId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                if (catalog.combat_vfx_cues != null)
                {
                    foreach (CombatVfxCueRow cue in catalog.combat_vfx_cues)
                    {
                        string vfxId = WireIdentifier.Normalize(cue.vfx_id);
                        if (vfxId.Length == 0)
                            continue;
                        if (!disciplineIdsByVfxId.TryGetValue(vfxId, out HashSet<string> disciplineIds))
                        {
                            disciplineIds = new HashSet<string>(StringComparer.Ordinal);
                            disciplineIdsByVfxId[vfxId] = disciplineIds;
                        }

                        string ownerKind = WireIdentifier.Normalize(cue.owner_kind);
                        string ownerId = WireIdentifier.Normalize(cue.owner_id);
                        if (string.Equals(ownerKind, "ABILITY", StringComparison.Ordinal))
                        {
                            if (abilityById.TryGetValue(ownerId, out AbilityRow ability))
                                AddDisciplineId(disciplineIds, ability.discipline_id);
                            continue;
                        }

                        if (!abilitiesByActionId.TryGetValue(ownerId, out List<AbilityRow> ownerAbilities))
                            continue;
                        foreach (AbilityRow ability in ownerAbilities)
                            AddDisciplineId(disciplineIds, ability.discipline_id);
                    }
                }

                foreach ((string vfxId, HashSet<string> disciplineIds) in disciplineIdsByVfxId)
                {
                    var usageDisciplines = new List<CombatDisciplineAuthoringFacts>();
                    foreach (string disciplineId in disciplineIds)
                    {
                        if (!disciplineById.TryGetValue(disciplineId, out CombatDisciplineAuthoringFacts facts))
                            facts = new CombatDisciplineAuthoringFacts(disciplineId, disciplineId, int.MaxValue);
                        usageDisciplines.Add(facts);
                    }

                    usageDisciplines.Sort((left, right) =>
                    {
                        int byOrder = left.SortOrder.CompareTo(right.SortOrder);
                        return byOrder != 0
                            ? byOrder
                            : string.CompareOrdinal(left.DisplayName, right.DisplayName);
                    });
                    result[vfxId] = new CombatVfxDisciplineUsage(usageDisciplines);
                }
            }
            catch (Exception ex)
            {
                warning = $"Progression catalog parse failed: {ex.Message}";
            }

            return result;
        }

        private static void AddDisciplineId(HashSet<string> disciplineIds, string? disciplineId)
        {
            string normalized = WireIdentifier.Normalize(disciplineId);
            if (normalized.Length > 0)
                disciplineIds.Add(normalized);
        }

        [Serializable]
        private sealed class CatalogDocument
        {
            public List<AbilityRow>? abilities;
            public List<CombatDisciplineRow>? combat_disciplines;
            public List<CombatVfxCueRow>? combat_vfx_cues;
        }

        [Serializable]
        private sealed class AbilityRow
        {
            public string ability_id = string.Empty;
            public string? discipline_id;
            public string action_id = string.Empty;
            public GameplayRow? gameplay;
        }

        [Serializable]
        private sealed class CombatDisciplineRow
        {
            public string discipline_id = string.Empty;
            public string display_name = string.Empty;
            public int sort_order;
        }

        [Serializable]
        private sealed class CombatVfxCueRow
        {
            public string owner_kind = string.Empty;
            public string owner_id = string.Empty;
            public string vfx_id = string.Empty;
        }

        [Serializable]
        private sealed class GameplayRow
        {
            public long cast_time_ms;
            public DeliveryRow? delivery;
        }

        [Serializable]
        private sealed class DeliveryRow
        {
            public string kind = string.Empty;
        }
    }
}
