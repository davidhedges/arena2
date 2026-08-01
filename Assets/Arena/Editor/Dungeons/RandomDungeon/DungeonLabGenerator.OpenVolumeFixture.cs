using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase D4 of the layered 3D topology design
    // (docs/dungeon-builder/layered-topology-design-2026-07-29.md §6, §4.1):
    // a recipe can reserve a vertical void, and two rooms can share a plan
    // column when both declare storeys and their bands do not meet.
    //
    // Authored recipes and generated vista volumes now share this reservation
    // mechanism. The fixture keeps the original band, allow-list, and overlap
    // contracts explicit independently of either producer.
    internal sealed partial class DungeonLabGenerator
    {
        private sealed class OpenVolumeFixture
        {
            // ---- the reserved void, against a fill sweep --------------------
            public bool fillBlockedInsideBand;
            public bool fillFreeBelowFloor;
            public bool fillFreeAtBandTop;
            public bool fillFreeOutsideFootprint;

            // ---- the reserved void, against structure -----------------------
            public bool foreignFootprintBlocked;
            public bool foreignLandingBlocked;
            public bool foreignStructureBelowAllowed;
            public bool owningRecipeAdmitted;
            public bool otherRecipeBlocked;
            public bool unboundedForeignClaimBlocked;

            // ---- the two producer sites agree -------------------------------
            public int declaredStageFloor;
            public int declaredStageTop;
            public int elevationStageFloor;
            public int elevationStageTop;
            public bool bothSitesReserveOneBand;

            // ---- the schema gate --------------------------------------------
            public bool heightlessVolumeRejected;
            public bool heightOnPlainZoneRejected;
            public bool authoredVolumeAccepted;

            // ---- the volumetric Overlaps ------------------------------------
            public bool plainRoomsCannotShare;
            public bool oneSidedDeclarationCannotShare;
            public bool meetingBandsCannotShare;
            public bool oneShortOfHeadroomCannotShare;
            public bool exactlyHeadroomApartMayShare;
            public bool disjointBandsMayShare;
            public bool disjointPlansNeverConsultBands;
        }

        /// <summary>
        /// Print the D4 open-volume fixture to the editor log.
        /// </summary>
        [MenuItem("Tools/Dungeon Lab/Print Open Volume Fixture")]
        public static void PrintOpenVolumeSnapshot()
        {
            Debug.Log($"[OPEN_VOLUME_FIXTURE]\n{BuildOpenVolumeSnapshot()}");
        }

        private static string BuildOpenVolumeSnapshot()
        {
            OpenVolumeFixture fixture = BuildOpenVolumeFixture();
            return string.Join("\n", new[]
            {
                $"fill.blockedInsideBand={fixture.fillBlockedInsideBand}",
                $"fill.freeBelowFloor={fixture.fillFreeBelowFloor}",
                $"fill.freeAtBandTop={fixture.fillFreeAtBandTop}",
                $"fill.freeOutsideFootprint={fixture.fillFreeOutsideFootprint}",
                $"structure.foreignFootprintBlocked={fixture.foreignFootprintBlocked}",
                $"structure.foreignLandingBlocked={fixture.foreignLandingBlocked}",
                $"structure.foreignBelowAllowed={fixture.foreignStructureBelowAllowed}",
                $"structure.owningRecipeAdmitted={fixture.owningRecipeAdmitted}",
                $"structure.otherRecipeBlocked={fixture.otherRecipeBlocked}",
                $"structure.unboundedForeignClaimBlocked={fixture.unboundedForeignClaimBlocked}",
                $"producer.bandAtNodeLevel0=[{fixture.declaredStageFloor},{fixture.declaredStageTop})",
                $"producer.bandAtNodeLevel4=[{fixture.elevationStageFloor},{fixture.elevationStageTop})",
                $"producer.bandTracksItsBaseLevel={fixture.bothSitesReserveOneBand}",
                $"schema.heightlessVolumeRejected={fixture.heightlessVolumeRejected}",
                $"schema.heightOnPlainZoneRejected={fixture.heightOnPlainZoneRejected}",
                $"schema.authoredVolumeAccepted={fixture.authoredVolumeAccepted}",
                $"overlap.plainRoomsCannotShare={fixture.plainRoomsCannotShare}",
                $"overlap.oneSidedDeclarationCannotShare={fixture.oneSidedDeclarationCannotShare}",
                $"overlap.meetingBandsCannotShare={fixture.meetingBandsCannotShare}",
                $"overlap.oneShortOfHeadroomCannotShare={fixture.oneShortOfHeadroomCannotShare}",
                $"overlap.exactlyHeadroomApartMayShare={fixture.exactlyHeadroomApartMayShare}",
                $"overlap.disjointBandsMayShare={fixture.disjointBandsMayShare}",
                $"overlap.disjointPlansNeverConsultBands={fixture.disjointPlansNeverConsultBands}"
            });
        }

        // The atrium under test: a 3x3 void over a chamber at level 0, opening
        // through a gallery storey at +4 and reserving 8 levels of air.
        private const int OpenVolumeFixtureChamberLevel = 0;
        private const int OpenVolumeFixtureGalleryLevel = 4;
        private const int OpenVolumeFixtureHeightLevels = 8;
        private const string OpenVolumeFixtureRecipeId = "open_volume_probe_recipe";
        private const string OpenVolumeFixtureZoneId = "atrium_void";

        private static List<Vector2Int> OpenVolumeFixtureCells()
        {
            var cells = new List<Vector2Int>();
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    cells.Add(new Vector2Int(x, y));
                }
            }

            return cells;
        }

        private static PrismLedger BuildOpenVolumeFixtureLedger(out OwnerKey recipeOwner)
        {
            var ledger = new PrismLedger();
            recipeOwner = new OwnerKey(OwnerFamily.Recipe, OpenVolumeFixtureRecipeId);
            ledger.RegisterOpenVolume(
                OpenVolumeFixtureCells(),
                new LevelBand(
                    OpenVolumeFixtureGalleryLevel,
                    OpenVolumeFixtureGalleryLevel + OpenVolumeFixtureHeightLevels),
                RecipeOpenVolumeOwner(OpenVolumeFixtureRecipeId, OpenVolumeFixtureZoneId),
                new[] { recipeOwner });
            return ledger;
        }

        private static OpenVolumeFixture BuildOpenVolumeFixture()
        {
            var fixture = new OpenVolumeFixture();
            var inside = new Vector2Int(0, 0);
            var outside = new Vector2Int(5, 5);
            int floor = OpenVolumeFixtureGalleryLevel;
            int top = OpenVolumeFixtureGalleryLevel + OpenVolumeFixtureHeightLevels;

            // ---- against a fill sweep ---------------------------------------
            // §6 payoff 4 and §11's named failure mode: "the density passes will
            // claim any plan cell they can. If they do not read the prism
            // ledger, density >= 3 packs an atrium and no check fires." A fill
            // claim is an unbounded Footprint from nobody in particular, so it
            // is foreign to the volume and meets it at every level.
            PrismLedger ledger = BuildOpenVolumeFixtureLedger(out OwnerKey recipeOwner);
            fixture.fillBlockedInsideBand = ledger.BlocksFill(inside);
            fixture.fillFreeOutsideFootprint = !ledger.BlocksFill(outside);

            // Banded probes, to show the reservation is a VOLUME and not a plan
            // claim: the chamber floor under the atrium survives, and so does
            // anything resting on the void's ceiling. Half-open at both ends.
            fixture.fillFreeBelowFloor = !ledger.Blocks(
                inside,
                new LevelBand(OpenVolumeFixtureChamberLevel, floor),
                PrismKind.Footprint,
                OwnerKey.CandidateProbe);
            fixture.fillFreeAtBandTop = !ledger.Blocks(
                inside,
                new LevelBand(top, top + 1),
                PrismKind.Footprint,
                OwnerKey.CandidateProbe);

            // ---- against structure -------------------------------------------
            var foreignTransition = new OwnerKey(OwnerFamily.Transition, "some-other-stair");
            fixture.foreignFootprintBlocked = ledger.Blocks(
                inside,
                new LevelBand(floor, floor + 1),
                PrismKind.Footprint,
                foreignTransition);
            // A balcony floor inside an atrium's void is exactly what the
            // reservation exists to stop, and a landing IS a walkable surface —
            // which is why OpenVolume blocks Landing where nothing else does.
            fixture.foreignLandingBlocked = ledger.Blocks(
                inside,
                new LevelBand(floor + 2, floor + 3),
                PrismKind.Landing,
                foreignTransition);
            fixture.foreignStructureBelowAllowed = !ledger.Blocks(
                inside,
                new LevelBand(OpenVolumeFixtureChamberLevel, floor),
                PrismKind.Footprint,
                foreignTransition);

            // The allow-list. The owning recipe's own rim, stairs and balconies
            // ARE the thing the void surrounds, so a reservation that excluded
            // them would forbid the atrium it describes.
            fixture.owningRecipeAdmitted = !ledger.Blocks(
                inside,
                new LevelBand(floor, floor + 1),
                PrismKind.Footprint,
                recipeOwner);
            fixture.otherRecipeBlocked = ledger.Blocks(
                inside,
                new LevelBand(floor, floor + 1),
                PrismKind.Footprint,
                new OwnerKey(OwnerFamily.Recipe, "some_other_recipe"));
            // Today every reservation in the generator carries an unbounded band,
            // so this is the case that actually occurs: an unbounded claim meets
            // the void at every level and is stopped.
            fixture.unboundedForeignClaimBlocked = ledger.Blocks(
                inside,
                LevelBand.Unbounded,
                PrismKind.Footprint,
                foreignTransition);

            // ---- the two producer sites reserve one band ----------------------
            // There are two sites because there are two ledgers (Phase B finding
            // 2): the annex sweep runs during LAYOUT and knows only the
            // topology's DECLARED node level, while the recipe realizes during
            // ELEVATION and knows its RESOLVED base level. Both call the same
            // `OpenVolumeBand`, so the bands agree exactly when those two numbers
            // do — which is `TryAssignRoomLevels` copying `relativeElevationLevels`
            // straight into `zoneLevels`, the property D3's fixture pins.
            //
            // What is worth asserting HERE is the other half: that the band is
            // genuinely derived from the base level rather than authored flat. A
            // band that ignored its argument would agree between the two sites
            // for the wrong reason, and would then reserve the wrong air on
            // every seed whose node does not sit at level 0.
            var zone = new RecipeZonePlacement(
                OpenVolumeFixtureZoneId,
                DungeonRecipeZoneKind.OpenVolume,
                relativeLevel: 0,
                layerRelativeLevel: OpenVolumeFixtureGalleryLevel,
                layerId: "gallery",
                isBaseLayer: false,
                openVolumeHeightLevels: OpenVolumeFixtureHeightLevels,
                cells: OpenVolumeFixtureCells().ToArray());
            LevelBand atChamber = zone.OpenVolumeBand(OpenVolumeFixtureChamberLevel);
            LevelBand raised = zone.OpenVolumeBand(OpenVolumeFixtureChamberLevel + MajorRiseLevels);
            fixture.declaredStageFloor = atChamber.minLevel;
            fixture.declaredStageTop = atChamber.maxLevelExclusive;
            fixture.elevationStageFloor = raised.minLevel;
            fixture.elevationStageTop = raised.maxLevelExclusive;
            fixture.bothSitesReserveOneBand =
                atChamber.minLevel == floor &&
                atChamber.maxLevelExclusive == top &&
                raised.minLevel == floor + MajorRiseLevels &&
                raised.maxLevelExclusive == top + MajorRiseLevels;

            CheckOpenVolumeSchemaRules(fixture);
            CheckVolumetricRoomOverlap(fixture);
            return fixture;
        }

        /// <summary>
        /// The two authoring slips the schema now names, and the shape that is
        /// meant to pass.
        /// </summary>
        private static void CheckOpenVolumeSchemaRules(OpenVolumeFixture fixture)
        {
            bool HasHeightFinding(DungeonRecipeAsset recipe)
            {
                try
                {
                    DungeonRecipeValidationResult result =
                        DungeonRecipeValidator.ValidateContract(recipe);
                    foreach (DungeonRecipeValidationFinding finding in result.Findings)
                    {
                        if (string.Equals(
                                finding.code,
                                "RECIPE_OPEN_VOLUME_HEIGHT",
                                StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }

                    return false;
                }
                finally
                {
                    DestroyImmediate(recipe);
                }
            }

            fixture.heightlessVolumeRejected = HasHeightFinding(
                BuildOpenVolumeProbeRecipe(DungeonRecipeZoneKind.OpenVolume, heightLevels: 0));
            fixture.heightOnPlainZoneRejected = HasHeightFinding(
                BuildOpenVolumeProbeRecipe(DungeonRecipeZoneKind.Walkable, heightLevels: 8));
            fixture.authoredVolumeAccepted = !HasHeightFinding(
                BuildOpenVolumeProbeRecipe(DungeonRecipeZoneKind.OpenVolume, heightLevels: 8));
        }

        private static DungeonRecipeAsset BuildOpenVolumeProbeRecipe(
            DungeonRecipeZoneKind kind,
            int heightLevels)
        {
            var recipe = ScriptableObject.CreateInstance<DungeonRecipeAsset>();
            recipe.recipeId = OpenVolumeFixtureRecipeId;
            recipe.zones = new[]
            {
                new DungeonRecipeZone
                {
                    id = "chamber",
                    kind = DungeonRecipeZoneKind.Walkable,
                    offset = new Vector2Int(-2, -2),
                    size = new Vector2Int(5, 5)
                },
                new DungeonRecipeZone
                {
                    id = OpenVolumeFixtureZoneId,
                    kind = kind,
                    offset = new Vector2Int(-1, -1),
                    size = new Vector2Int(3, 3),
                    openVolumeHeightLevels = heightLevels
                }
            };
            return recipe;
        }

        // ------------------------------------------------------------------
        // The volumetric Overlaps
        // ------------------------------------------------------------------

        private static readonly RoomFootprint OpenVolumeFixtureLowerRoom =
            RoomFootprint.FromRect(new RectInt(-2, -2, 5, 5));
        private static readonly RoomFootprint OpenVolumeFixtureUpperRoom =
            RoomFootprint.FromRect(new RectInt(0, 0, 5, 5));
        private static readonly RoomFootprint OpenVolumeFixtureDistantRoom =
            RoomFootprint.FromRect(new RectInt(20, 20, 5, 5));

        /// <summary>
        /// Two rooms, one variable at a time: what each declares, and how far
        /// apart the declarations put them.
        /// </summary>
        private static bool RoomsMayOverlap(
            int[] lowerDeclaredElevations,
            int[] upperDeclaredElevations,
            RoomFootprint upperRoom = null)
        {
            var placed = new[] { OpenVolumeFixtureLowerRoom, null };
            int[][] elevations = { lowerDeclaredElevations, upperDeclaredElevations };
            return !OverlapsPlacedRoom(
                upperRoom ?? OpenVolumeFixtureUpperRoom,
                1,
                placed,
                elevations);
        }

        private static void CheckVolumetricRoomOverlap(OpenVolumeFixture fixture)
        {
            // A room that declares one elevation declares no storey, and
            // authorizes nothing — which is every room in the shipped corpus,
            // however far apart their levels happen to be.
            fixture.plainRoomsCannotShare = !RoomsMayOverlap(
                new[] { 0 },
                new[] { 24 });
            fixture.oneSidedDeclarationCannotShare = !RoomsMayOverlap(
                new[] { 0 },
                new[] { 24, 28 });

            // Both declare storeys, so the bands decide. `SpanningEndpoints`
            // pads the top by MinHeadroomLevels, so the exact/one-short pair is
            // what proves the half-open boundary is doing the deciding.
            fixture.meetingBandsCannotShare = !RoomsMayOverlap(
                new[] { 0, 4 },
                new[] { 4, 8 });
            fixture.oneShortOfHeadroomCannotShare = !RoomsMayOverlap(
                new[] { 0, 4 },
                new[] { 4 + MinHeadroomLevels - 1, 8 });
            fixture.exactlyHeadroomApartMayShare = RoomsMayOverlap(
                new[] { 0, 4 },
                new[] { 4 + MinHeadroomLevels, 8 + MinHeadroomLevels });
            fixture.disjointBandsMayShare = RoomsMayOverlap(
                new[] { 0, 4 },
                new[] { 16, 20 });

            // Rooms that do not share a plan cell never reach the band test at
            // all, so the relaxation cannot change where an ordinary room goes.
            fixture.disjointPlansNeverConsultBands = RoomsMayOverlap(
                new[] { 0 },
                new[] { 0 },
                OpenVolumeFixtureDistantRoom);
        }
    }
}
