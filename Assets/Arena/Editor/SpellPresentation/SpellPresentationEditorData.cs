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

        [Serializable]
        private sealed class CatalogDocument
        {
            public List<AbilityRow>? abilities;
        }

        [Serializable]
        private sealed class AbilityRow
        {
            public string action_id = string.Empty;
            public GameplayRow? gameplay;
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
