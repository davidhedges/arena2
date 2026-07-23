using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace DungeonLab.Editor
{
    internal static class DungeonRecipeIds
    {
        internal const string ProcessionalLandmark = "episode_throne_twin_stairs_01";
        internal const string CompressionConnector = "connector_flexible_vestibule_01";
        internal const string CornerReturnConnector = "connector_corner_return_01";
    }

    internal sealed class ActiveDungeonRecipeCatalog
    {
        private readonly Dictionary<string, DungeonRecipeAsset> byId;

        public readonly DungeonRecipeAsset[] recipes;
        public readonly string digest;

        public ActiveDungeonRecipeCatalog(DungeonRecipeAsset[] recipes, string digest)
        {
            this.recipes = recipes ?? Array.Empty<DungeonRecipeAsset>();
            this.digest = digest ?? string.Empty;
            byId = new Dictionary<string, DungeonRecipeAsset>(StringComparer.Ordinal);
            foreach (DungeonRecipeAsset recipe in this.recipes)
            {
                byId.Add(recipe.recipeId, recipe);
            }
        }

        public bool TryGet(string recipeId, out DungeonRecipeAsset recipe)
        {
            return byId.TryGetValue(recipeId ?? string.Empty, out recipe);
        }
    }

    internal static class DungeonRecipeCatalogService
    {
        internal const string CatalogPath =
            "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Catalog/dungeon_recipe_catalog.asset";
        private static ActiveDungeonRecipeCatalog previewCatalog;

        internal static bool TryLoadActiveCatalog(
            out ActiveDungeonRecipeCatalog activeCatalog,
            out string rejectionReason)
        {
            activeCatalog = null;
            rejectionReason = string.Empty;
            if (previewCatalog != null)
            {
                activeCatalog = previewCatalog;
                return true;
            }

            DungeonRecipeCatalog source = AssetDatabase.LoadAssetAtPath<DungeonRecipeCatalog>(CatalogPath);
            if (source == null)
            {
                rejectionReason = $"[RECIPE_CATALOG] missing catalog at {CatalogPath}";
                return false;
            }

            if (source.schemaVersion != DungeonRecipeAsset.CurrentSchemaVersion)
            {
                rejectionReason = $"[RECIPE_CATALOG] unsupported catalog schema {source.schemaVersion}";
                return false;
            }

            return TryBuildActiveCatalog(source.recipes, out activeCatalog, out rejectionReason);
        }

        internal static bool TryBuildActiveCatalog(
            IEnumerable<DungeonRecipeAsset> catalogMembers,
            out ActiveDungeonRecipeCatalog activeCatalog,
            out string rejectionReason)
        {
            activeCatalog = null;
            rejectionReason = string.Empty;
            var active = new List<DungeonRecipeAsset>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (DungeonRecipeAsset recipe in catalogMembers ?? Array.Empty<DungeonRecipeAsset>())
            {
                if (recipe == null)
                {
                    rejectionReason = "[RECIPE_CATALOG] catalog contained a null asset";
                    return false;
                }

                if (!ids.Add(recipe.recipeId))
                {
                    rejectionReason = $"[RECIPE_CATALOG] duplicate recipe ID '{recipe.recipeId}'";
                    return false;
                }

                if (recipe.disabledForGeneration)
                {
                    continue;
                }

                DungeonRecipeValidationResult validation = DungeonRecipeValidator.ValidateContract(recipe);
                if (!validation.Passed)
                {
                    DungeonRecipeValidationFinding finding = validation.Findings.Count > 0
                        ? validation.Findings[0]
                        : default;
                    rejectionReason =
                        $"[RECIPE_CATALOG] enabled recipe '{recipe.recipeId}' failed current validation" +
                        (string.IsNullOrEmpty(finding.code)
                            ? string.Empty
                            : $": {finding.code}: {finding.message}");
                    return false;
                }

                active.Add(recipe);
            }

            active.Sort((first, second) => string.CompareOrdinal(first.recipeId, second.recipeId));
            activeCatalog = new ActiveDungeonRecipeCatalog(active.ToArray(), ComputeCatalogDigest(active));
            return true;
        }

        internal static IDisposable BeginAuthoringPreview(
            DungeonRecipeAsset previewRecipe,
            out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (previewCatalog != null)
            {
                rejectionReason = "[RECIPE_CATALOG] nested authoring preview catalogs are not supported";
                return null;
            }

            DungeonRecipeCatalog source = AssetDatabase.LoadAssetAtPath<DungeonRecipeCatalog>(CatalogPath);
            if (source == null || previewRecipe == null)
            {
                rejectionReason = "[RECIPE_CATALOG] authoring preview lacked a source catalog or recipe";
                return null;
            }

            DungeonRecipeValidationResult previewValidation = DungeonRecipeValidator.ValidateContract(previewRecipe);
            if (!previewValidation.Passed)
            {
                rejectionReason = $"[RECIPE_CATALOG] preview recipe '{previewRecipe.recipeId}' failed contract validation";
                return null;
            }

            var remainingMembers = new List<DungeonRecipeAsset>();
            int matchingMemberCount = 0;
            foreach (DungeonRecipeAsset recipe in source.recipes ?? Array.Empty<DungeonRecipeAsset>())
            {
                if (recipe != null && string.Equals(recipe.recipeId, previewRecipe.recipeId, StringComparison.Ordinal))
                {
                    matchingMemberCount++;
                }
                else
                {
                    remainingMembers.Add(recipe);
                }
            }

            if (matchingMemberCount > 1)
            {
                rejectionReason = $"[RECIPE_CATALOG] duplicate recipe ID '{previewRecipe.recipeId}'";
                return null;
            }

            if (!TryBuildActiveCatalog(
                    remainingMembers,
                    out ActiveDungeonRecipeCatalog currentCatalog,
                    out rejectionReason))
            {
                return null;
            }

            var active = new List<DungeonRecipeAsset>(currentCatalog.recipes) { previewRecipe };
            active.Sort((first, second) => string.CompareOrdinal(first.recipeId, second.recipeId));
            previewCatalog = new ActiveDungeonRecipeCatalog(active.ToArray(), ComputeCatalogDigest(active));
            return new PreviewCatalogScope();
        }

        internal static string ComputeCatalogDigest(IEnumerable<DungeonRecipeAsset> recipes)
        {
            var canonical = new StringBuilder();
            foreach (DungeonRecipeAsset recipe in recipes ?? Array.Empty<DungeonRecipeAsset>())
            {
                canonical.Append(recipe.recipeId)
                    .Append('|')
                    .Append(DungeonRecipeValidator.ComputeContentDigest(recipe))
                    .Append('\n');
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes)
                {
                    result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return result.ToString();
            }
        }

        private sealed class PreviewCatalogScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (!disposed)
                {
                    disposed = true;
                    previewCatalog = null;
                }
            }
        }
    }

}
