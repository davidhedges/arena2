using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal sealed partial class DungeonLabGenerator
    {
        [MenuItem("Tools/Dungeon Lab/Print Phase E Contracts")]
        public static void PrintPhaseEContractSnapshot()
        {
            Debug.Log($"[PHASE_E_CONTRACTS]\n{BuildPhaseEContractSnapshot()}");
        }

        private static string BuildPhaseEContractSnapshot()
        {
            var clearSurfaces = new SurfaceField(new Dictionary<Vector2Int, int>
            {
                [Vector2Int.zero] = 4
            });
            var blockedSurfaces = new SurfaceField(new Dictionary<Vector2Int, int>
            {
                [Vector2Int.zero] = 4,
                [Vector2Int.right] = 0
            });
            var voidOpening = new RecipeOpeningPlacement(
                "void-probe",
                OpeningKind.Void,
                Vector2Int.zero,
                Direction.East,
                layerRelativeLevel: 0,
                layerId: "upper");
            var apertureOpening = new RecipeOpeningPlacement(
                "aperture-probe",
                OpeningKind.Aperture,
                Vector2Int.zero,
                Direction.East,
                layerRelativeLevel: 0,
                layerId: "upper");
            bool clearAccepted = TryValidateVoidOpeningFallColumn(
                clearSurfaces,
                voidOpening,
                rimLevel: 4,
                out string clearFailure);
            bool blockedRejected = !TryValidateVoidOpeningFallColumn(
                blockedSurfaces,
                voidOpening,
                rimLevel: 4,
                out string blockedFailure);
            bool apertureNoCatchRejected = !TryValidateApertureOpeningFallColumn(
                clearSurfaces,
                apertureOpening,
                rimLevel: 4,
                out string apertureNoCatchFailure);
            bool apertureLegalAccepted = TryValidateApertureOpeningFallColumn(
                new SurfaceField(new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 4,
                    [Vector2Int.right] = 0
                }),
                apertureOpening,
                rimLevel: 4,
                out _);
            bool apertureShallowRejected = !TryValidateApertureOpeningFallColumn(
                new SurfaceField(new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 4,
                    [Vector2Int.right] = 2
                }),
                apertureOpening,
                rimLevel: 4,
                out string apertureShallowFailure);
            bool apertureDeepRejected = !TryValidateApertureOpeningFallColumn(
                new SurfaceField(new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 4,
                    [Vector2Int.right] = -5
                }),
                apertureOpening,
                rimLevel: 4,
                out string apertureDeepFailure);

            DungeonRecipeAsset schemaProbe = BuildVoidOpeningSchemaProbe();
            bool voidSchemaAccepted;
            try
            {
                voidSchemaAccepted = true;
                foreach (DungeonRecipeValidationFinding finding in
                         DungeonRecipeValidator.ValidateContract(schemaProbe).Findings)
                {
                    if (string.Equals(finding.code, "RECIPE_OPENING", StringComparison.Ordinal))
                    {
                        voidSchemaAccepted = false;
                    }
                }
            }
            finally
            {
                DestroyImmediate(schemaProbe);
            }

            return string.Join("\n", new[]
            {
                $"ceiling.default={DefaultTopologyCeilingLevels}",
                $"ceiling.globalCap={MaxTopologyCeilingLevels}",
                $"abyss.depth={ElevationEdgeModel.AbyssDepthLevels}",
                $"abyss.baseAtMin0={ElevationEdgeModel.AbyssBaseForMinFloor(0)}",
                $"abyss.baseAtMin12={ElevationEdgeModel.AbyssBaseForMinFloor(12)}",
                $"opening.apertureIsZero={(int)OpeningKind.Aperture == 0}",
                $"opening.voidProducerCarriesKind={voidOpening.kind == OpeningKind.Void}",
                $"opening.voidSchemaAccepted={voidSchemaAccepted}",
                $"opening.clearFallColumnAccepted={clearAccepted}",
                $"opening.clearFailure={clearFailure}",
                $"opening.obstructedFallColumnRejected={blockedRejected}",
                $"opening.obstructedFailureCode={blockedFailure.StartsWith("[VOID_OPENING_OBSTRUCTED]", StringComparison.Ordinal)}",
                $"opening.maxSurvivableFall={MaxSurvivableFallLevels}",
                $"opening.apertureNoCatchRejected={apertureNoCatchRejected}",
                $"opening.apertureNoCatchFailureCode={apertureNoCatchFailure.StartsWith("[APERTURE_NO_CATCH_SURFACE]", StringComparison.Ordinal)}",
                $"opening.apertureLegalAccepted={apertureLegalAccepted}",
                $"opening.apertureShallowRejected={apertureShallowRejected}",
                $"opening.apertureShallowFailureCode={apertureShallowFailure.StartsWith("[APERTURE_FALL_TOO_SHALLOW]", StringComparison.Ordinal)}",
                $"opening.apertureDeepRejected={apertureDeepRejected}",
                $"opening.apertureDeepFailureCode={apertureDeepFailure.StartsWith("[APERTURE_FALL_UNSURVIVABLE]", StringComparison.Ordinal)}"
            });
        }

        private static DungeonRecipeAsset BuildVoidOpeningSchemaProbe()
        {
            var recipe = ScriptableObject.CreateInstance<DungeonRecipeAsset>();
            recipe.recipeId = "void_opening_schema_probe";
            recipe.layers = new[]
            {
                new DungeonRecipeLayer { layerId = "base", relativeLevel = 0, isBase = true },
                new DungeonRecipeLayer { layerId = "upper", relativeLevel = 4, isBase = false }
            };
            recipe.zones = new[]
            {
                new DungeonRecipeZone
                {
                    id = "base-floor",
                    kind = DungeonRecipeZoneKind.Walkable,
                    offset = new Vector2Int(-1, -1),
                    size = new Vector2Int(1, 1),
                    layerId = "base"
                },
                new DungeonRecipeZone
                {
                    id = "upper-rim",
                    kind = DungeonRecipeZoneKind.Walkable,
                    offset = Vector2Int.zero,
                    size = Vector2Int.one,
                    layerId = "upper"
                }
            };
            recipe.openings = new[]
            {
                new DungeonRecipeOpening
                {
                    id = "void-rim",
                    kind = OpeningKind.Void,
                    cell = Vector2Int.zero,
                    outwardDirection = Vector2Int.right,
                    layerId = "upper"
                }
            };
            return recipe;
        }
    }
}
