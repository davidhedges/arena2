using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

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
            var openingOwner = new OwnerKey(OwnerFamily.Opening, "phase-e-probe");
            var voidOpening = new PlanOpening(
                openingOwner,
                "void-probe",
                OpeningKind.Void,
                Vector2Int.zero,
                Direction.East,
                level: 4);
            var apertureOpening = new PlanOpening(
                openingOwner,
                "aperture-probe",
                OpeningKind.Aperture,
                Vector2Int.zero,
                Direction.East,
                level: 4);
            bool clearAccepted = TryValidateVoidOpeningFallColumn(
                clearSurfaces,
                voidOpening,
                out string clearFailure);
            bool blockedRejected = !TryValidateVoidOpeningFallColumn(
                blockedSurfaces,
                voidOpening,
                out string blockedFailure);
            bool apertureNoCatchRejected = !TryValidateApertureOpeningFallColumn(
                clearSurfaces,
                apertureOpening,
                out string apertureNoCatchFailure);
            bool apertureLegalAccepted = TryValidateApertureOpeningFallColumn(
                new SurfaceField(new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 4,
                    [Vector2Int.right] = 0
                }),
                apertureOpening,
                out _);
            bool apertureShallowRejected = !TryValidateApertureOpeningFallColumn(
                new SurfaceField(new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 4,
                    [Vector2Int.right] = 2
                }),
                apertureOpening,
                out string apertureShallowFailure);
            bool apertureDeepRejected = !TryValidateApertureOpeningFallColumn(
                new SurfaceField(new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 4,
                    [Vector2Int.right] = -5
                }),
                apertureOpening,
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

        private static string BuildSlice2OwnershipContractSnapshot()
        {
            var external = new ExternalConnectorPromontoryResolution(
                "generated-passage",
                Direction.North,
                Vector2Int.zero,
                Vector2Int.right,
                level: 0,
                occupiedCells: Array.Empty<Vector2Int>());
            var recipeOwner = new OwnerKey(OwnerFamily.Recipe, "opening-probe-recipe");
            var recipeAsset = ScriptableObject.CreateInstance<DungeonRecipeAsset>();
            recipeAsset.recipeId = recipeOwner.id;
            recipeAsset.kind = DungeonRecipeKind.Connector;
            var recipeSlot = new RecipeSlotIntent(
                "opening-probe-slot",
                slotNode: 0,
                recipeAsset,
                RecipeOrientationBinding.RouteForward,
                Array.Empty<RecipePortBinding>());
            var recipeOpening = new PlanOpening(
                recipeOwner,
                "gallery-aperture",
                OpeningKind.Aperture,
                new Vector2Int(2, 0),
                Direction.East,
                level: MajorRiseLevels);
            var recipePlacement = new RecipePlacement(
                slot: recipeSlot,
                roomIndex: 0,
                roomCenter: Vector2Int.zero,
                primaryAxis: Vector2Int.right,
                transverseAxis: Vector2Int.up,
                mirrored: false,
                roomCells: Array.Empty<Vector2Int>(),
                zones: Array.Empty<RecipeZonePlacement>(),
                protectedCells: Array.Empty<Vector2Int>(),
                ports: Array.Empty<RecipePortPlacement>(),
                transitions: Array.Empty<RecipeTransitionPlacement>(),
                openings: new[] { recipeOpening },
                selectedVariationId: string.Empty,
                selectedVisualImplementationId: string.Empty,
                showpieceOriginCell: Vector2Int.zero,
                showpieceYawDegrees: 0f,
                showpieceReservation: default);
            PlanOpening[] openings = BuildPlanOpenings(
                new[] { external },
                new[] { recipePlacement });

            var surfaces = new SurfaceField(new Dictionary<Vector2Int, int>
            {
                [Vector2Int.zero] = 0,
                [Vector2Int.right] = 0,
                [new Vector2Int(2, 0)] = 0,
                [new Vector2Int(3, 0)] = 0
            });
            surfaces.AddSurface(
                new Vector2Int(2, 0),
                MajorRiseLevels,
                SurfaceKind.Floor);
            bool combinedOpeningsAccepted = TryValidatePlanOpenings(
                surfaces,
                openings,
                out string combinedFailure);
            List<ElevationEdgeModel.OpenFloorEdge> renderedEdges =
                BuildPlannedOpenEdges(openings);
            bool generatedPassagesAreColumnScoped =
                renderedEdges.Count >= 2 &&
                !renderedEdges[0].IsSurfaceScoped &&
                !renderedEdges[1].IsSurfaceScoped;
            bool recipeApertureIsSurfaceScoped =
                renderedEdges.Count == 3 &&
                renderedEdges[2].IsSurfaceScoped &&
                renderedEdges[2].level == MajorRiseLevels;
            JArray recipeProjection = BuildRecipeResolutionsProjection(
                new[] { new RecipeResolution(recipePlacement, baseLevel: 0, atomicAndValid: true) },
                openings);
            JArray projectedRecipeOpenings =
                recipeProjection[0]?["openings"] as JArray;
            bool recipeReportEquivalentFromPlanList =
                projectedRecipeOpenings?.Count == 1 &&
                projectedRecipeOpenings[0]?.Value<string>("id") == recipeOpening.id &&
                projectedRecipeOpenings[0]?.Value<int>("layerRelativeLevel") == MajorRiseLevels &&
                projectedRecipeOpenings[0]?["kind"] == null;
            DestroyImmediate(recipeAsset);

            var includedFallSurfaces = new HashSet<SurfaceKey>
            {
                new SurfaceKey(recipeOpening.cell, recipeOpening.level),
                new SurfaceKey(new Vector2Int(3, 0), 0)
            };
            var fallDrafts = new List<NavigationEdgeDraft>();
            AddOpeningNavigationEdges(
                surfaces,
                new[] { recipeOpening },
                includedFallSurfaces,
                fallDrafts,
                new HashSet<string>(StringComparer.Ordinal));
            bool fallNavigationUsesPlanList =
                fallDrafts.Count == 1 &&
                fallDrafts[0].kind == "Fall" &&
                fallDrafts[0].directed &&
                fallDrafts[0].riseLevels == MajorRiseLevels;

            var generatedSurfacePassage = new PlanOpening(
                new OwnerKey(OwnerFamily.Opening, "generated-gallery"),
                "gallery-passage",
                new Vector2Int(2, 0),
                Direction.North,
                MajorRiseLevels);
            ElevationEdgeModel.OpenFloorEdge generatedSurfaceEdge =
                BuildPlannedOpenEdges(new[] { generatedSurfacePassage })[0];
            bool generatedSurfacePassagePreserved =
                generatedSurfaceEdge.IsSurfaceScoped &&
                generatedSurfaceEdge.level == MajorRiseLevels;

            var duplicate = new List<PlanOpening>(openings)
            {
                new PlanOpening(
                    new OwnerKey(OwnerFamily.Opening, "duplicate-producer"),
                    "duplicate-anchor",
                    Vector2Int.zero,
                    Direction.North)
            };
            bool duplicateRejected = !TryValidatePlanOpenings(
                surfaces,
                duplicate,
                out string duplicateFailure);
            bool missingRimRejected = !TryValidatePlanOpenings(
                surfaces,
                new[]
                {
                    new PlanOpening(
                        new OwnerKey(OwnerFamily.Opening, "missing-rim"),
                        "missing-rim",
                        new Vector2Int(20, 20),
                        Direction.South)
                },
                out string missingRimFailure);

            bool resolutionHasNoOpeningStorage = typeof(RecipeResolution).GetField(
                    "openings",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null;

            HashSet<Vector2Int> roomCells = RoomFootprint
                .FromRect(new RectInt(0, 0, 16, 8))
                .cells;
            var atriumCells = new HashSet<Vector2Int>();
            for (int x = 7; x <= 8; x++)
            {
                for (int y = 2; y <= 5; y++)
                {
                    atriumCells.Add(new Vector2Int(x, y));
                }
            }

            var ledger = new PrismLedger();
            RegisterPlannedOpenVolume(
                ledger,
                atriumCells,
                new LevelBand(MajorRiseLevels, MajorRiseLevels * 3),
                new OwnerKey(OwnerFamily.Vista, "generated-atrium"),
                Array.Empty<OwnerKey>());
            IReadOnlyList<HashSet<Vector2Int>> volumeGroups =
                ledger.OpenVolumeCellGroups();
            List<HashSet<Vector2Int>> unprotectedChambers = SplitRoomIntoChambers(
                roomCells,
                Array.Empty<HashSet<Vector2Int>>());
            List<HashSet<Vector2Int>> protectedChambers = SplitRoomIntoChambers(
                roomCells,
                volumeGroups);
            int unprotectedAtriumPartitions = CountSetsTouching(
                unprotectedChambers,
                atriumCells);
            int protectedAtriumPartitions = CountSetsTouching(
                protectedChambers,
                atriumCells);

            var clearVolumeSurfaces = new SurfaceField(new Dictionary<Vector2Int, int>
            {
                [new Vector2Int(7, 2)] = 0
            });
            bool generatedVolumeAccepted = ledger.TryValidateOpenVolumes(
                clearVolumeSurfaces,
                out _);
            clearVolumeSurfaces.AddSurface(
                new Vector2Int(7, 2),
                MajorRiseLevels,
                SurfaceKind.Floor);
            bool generatedVolumePenetrationRejected = !ledger.TryValidateOpenVolumes(
                clearVolumeSurfaces,
                out string volumeFailure);

            return string.Join("\n", new[]
            {
                $"opening.singlePlanListCount={openings.Length}",
                $"opening.generatedAndRecipeAccepted={combinedOpeningsAccepted}",
                $"opening.combinedFailure={combinedFailure}",
                $"opening.generatedPassagesColumnScoped={generatedPassagesAreColumnScoped}",
                $"opening.recipeApertureSurfaceScoped={recipeApertureIsSurfaceScoped}",
                $"opening.recipeReportEquivalentFromPlanList={recipeReportEquivalentFromPlanList}",
                $"opening.fallNavigationUsesPlanList={fallNavigationUsesPlanList}",
                $"opening.generatedSurfacePassagePreserved={generatedSurfacePassagePreserved}",
                $"opening.duplicateRejected={duplicateRejected}",
                $"opening.duplicateFailureCode={duplicateFailure.StartsWith("[PLAN_OPENING_DUPLICATE]", StringComparison.Ordinal)}",
                $"opening.missingRimRejected={missingRimRejected}",
                $"opening.missingRimFailureCode={missingRimFailure.StartsWith("[PLAN_OPENING_RIM_MISSING]", StringComparison.Ordinal)}",
                $"opening.recipeResolutionStorageRemoved={resolutionHasNoOpeningStorage}",
                $"volume.generatedOwnerGroupCount={volumeGroups.Count}",
                $"volume.unprotectedAtriumPartitions={unprotectedAtriumPartitions}",
                $"volume.protectedAtriumPartitions={protectedAtriumPartitions}",
                $"volume.chamberSubdivisionContinues={protectedChambers.Count > 1}",
                $"volume.generatedAccepted={generatedVolumeAccepted}",
                $"volume.penetrationRejected={generatedVolumePenetrationRejected}",
                $"volume.penetrationFailureCode={volumeFailure.StartsWith("[OPEN_VOLUME_VIOLATION]", StringComparison.Ordinal)}"
            });
        }

        private static int CountSetsTouching(
            IReadOnlyList<HashSet<Vector2Int>> sets,
            IReadOnlyCollection<Vector2Int> cells)
        {
            int count = 0;
            foreach (HashSet<Vector2Int> set in sets ?? Array.Empty<HashSet<Vector2Int>>())
            {
                foreach (Vector2Int cell in cells ?? Array.Empty<Vector2Int>())
                {
                    if (!set.Contains(cell))
                    {
                        continue;
                    }

                    count++;
                    break;
                }
            }

            return count;
        }
    }
}
