using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // Focused optional-crossing evidence only. Production generation continues
    // to use AddAerialBridges; this fixture supplies deterministic eligible
    // geometry to that existing method and inspects its normal renderer/export.
    internal sealed partial class DungeonLabGenerator
    {
        private sealed class StackedCrossingFixture
        {
            public Dictionary<Vector2Int, int> levels;
            public List<ElevationEdgeModel.TransitionEdge> transitions;
            public PrismLedger prisms;
            public int stackedDeckLevel;
            public Vector2Int stackedCell;
            public Vector2Int lowerStart;
            public Vector2Int lowerEnd;
            public ElevationEdgeModel.TransitionEdge bridge;
            public GameObject root;
            public ElevationEdgeModel.BuildReport buildReport;
            public bool lowerRouteTraversable;
            public bool upperBridgeTraversable;
            public bool positiveHeadroomPassed;
            public bool negativeHeadroomRejected;
            // Design §13 Phase B, negative fixture 2: half-open bands mean a
            // clearance of EXACTLY MinHeadroomLevels passes and one less fails.
            // A closed band gets the first of these wrong.
            public bool exactHeadroomPassed;
            public bool oneShortOfExactRejected;
            // Design §13 Phase B, negative fixture 3: the five cases that ARE the
            // blocksKinds policy. A symmetric conflict matrix flips the first
            // three; merging the two clearance kinds flips the last.
            public bool landingOverLandingLegal;
            public bool landingOverClearanceLegal;
            public bool mouthOverClearanceLegal;
            public bool landingOverFootprintRejected;
            public bool transitionClearanceOverMouthRejected;
            public bool sameOwnerFootprintUnderOwnClearanceLegal;
            public bool openVolumeBlocksForeignFloor;
            public bool openVolumeAdmitsAllowListedFloor;
            public bool openVolumeBlocksItsOwnFloor;
            public bool lowerClearanceOpen;
            public List<string> lowerSurfaceColliderNames = new List<string>();
            public List<string> upperSurfaceColliderNames = new List<string>();
        }

        /// <summary>
        /// Print the stacked-crossing fixture, including the Phase B negative
        /// fixtures, to the editor log.
        /// </summary>
        /// <remarks>
        /// The snapshot was previously reachable only by reflection from an
        /// EditMode test, which meant the three negative fixtures the design
        /// asks for (§13 Phase B) could not be read off a headless run. This is
        /// a diagnostic entry point in the same shape as `BatchValidate200Seeds`,
        /// not a test: it reports, it does not assert.
        /// </remarks>
        [MenuItem("Tools/Dungeon Lab/Print Stacked Crossing Fixture")]
        public static void PrintStackedCrossingSnapshot()
        {
            Debug.Log($"[STACKED_CROSSING_FIXTURE]\n{BuildStackedCrossingSnapshot()}");
        }

        private static string BuildStackedCrossingSnapshot()
        {
            StackedCrossingFixture fixture = null;
            try
            {
                fixture = BuildStackedCrossingFixture();
                return string.Join("\n", new[]
                {
                    $"fixture.transitionCount={fixture.transitions.Count}",
                    $"fixture.placementClass={fixture.bridge.placementClass}",
                    $"fixture.stackedCoordinateCount={fixture.bridge.footprintCells.Count(cell => fixture.levels.ContainsKey(cell))}",
                    $"fixture.lowerTraversable={fixture.lowerRouteTraversable}",
                    $"fixture.upperTraversable={fixture.upperBridgeTraversable}",
                    $"fixture.positiveHeadroom={fixture.positiveHeadroomPassed}",
                    $"fixture.negativeHeadroomRejected={fixture.negativeHeadroomRejected}",
                    $"fixture.exactHeadroomPassed={fixture.exactHeadroomPassed}",
                    $"fixture.oneShortOfExactRejected={fixture.oneShortOfExactRejected}",
                    $"fixture.landingOverLandingLegal={fixture.landingOverLandingLegal}",
                    $"fixture.landingOverClearanceLegal={fixture.landingOverClearanceLegal}",
                    $"fixture.mouthOverClearanceLegal={fixture.mouthOverClearanceLegal}",
                    $"fixture.landingOverFootprintRejected={fixture.landingOverFootprintRejected}",
                    $"fixture.transitionClearanceOverMouthRejected={fixture.transitionClearanceOverMouthRejected}",
                    $"fixture.sameOwnerFootprintUnderOwnClearanceLegal={fixture.sameOwnerFootprintUnderOwnClearanceLegal}",
                    $"fixture.openVolumeBlocksForeignFloor={fixture.openVolumeBlocksForeignFloor}",
                    $"fixture.openVolumeAdmitsAllowListedFloor={fixture.openVolumeAdmitsAllowListedFloor}",
                    $"fixture.openVolumeBlocksItsOwnFloor={fixture.openVolumeBlocksItsOwnFloor}",
                    $"fixture.rendererRejected={fixture.buildReport.rejected}",
                    $"fixture.lowerClearanceOpen={fixture.lowerClearanceOpen}",
                    $"fixture.lowerSurfaceColliders={fixture.lowerSurfaceColliderNames.Count}",
                    $"fixture.upperSurfaceColliders={fixture.upperSurfaceColliderNames.Count}"
                });
            }
            finally
            {
                if (fixture?.root != null)
                    DestroyImmediate(fixture.root);
            }
        }

        // ------------------------------------------------------------------
        // The C1 two-layer episode (design §13 Phase C).
        //
        // The design says "extend BuildStackedCrossingFixture". It is built
        // ALONGSIDE it instead, deliberately: that fixture carries Phase B's
        // three negative fixtures and asserts that its bridge crosses EXACTLY
        // ONE stacked coordinate, and adding a chamber, a gallery and a stair
        // run to the same field would either move that count or force the
        // assertion to be loosened. The Phase B evidence stays exactly as it
        // was measured, and the episode gets a field it can grow into.
        //
        // The episode, in plan (x east, y north; every level is 1u, cells 4u):
        //
        //          -4 -3 -2 -1  0  1  2  3  4
        //    y=10        T  T  T  T  T  T  C          T terrace  (ground, L4)
        //    y= 9        S  g  g  g  .  .  C          C corridor (ground, L4)
        //    y= 8        S  g  g  g  .  .  C          S stair    (ground, L3..L0)
        //    y= 7        S  g  A  g  .  .  C          g gallery  (STACKED, L4)
        //    y= 6        S  g  g  g  .  .  C          A aperture (no L4 surface)
        //    y= 5        S  g  g  g  .  .  C          . chamber  (ground, L0)
        //    y= 4              |                      | lower route (ground, L0)
        //    y= 1  W  W        |        E  E          W/E bridge rooms (ground, L4)
        //
        // (the chamber spans x -2..2, y 5..9 at L0 and is what the gallery
        // stands over; the stair column at x=-3 climbs L0->L1->L2->L3 and meets
        // the terrace at L4.)
        //
        // Every piece the phase names is here: an upper route, an aperture in it
        // that is a hole rather than a room feature, a fall onto a lower chamber
        // that is genuinely playable, a return stair, and a bridge over the
        // lower route.
        // ------------------------------------------------------------------

        private sealed class TwoLayerEpisodeFixture
        {
            public Dictionary<Vector2Int, int> levels;
            public List<ElevationEdgeModel.StackedSurface> stacked;
            public List<ElevationEdgeModel.TransitionEdge> transitions;
            public GameObject root;
            public ElevationEdgeModel.BuildReport buildReport;
            public Vector2Int apertureCell;
            public int upperLevel;
            public float levelHeight;
            public Vector2Int bridgeWestLanding;
            public Vector2Int bridgeEastLanding;
            public Vector2Int bridgeStackedCell;
            public int bridgeDeckLevel;

            // What the fixture DECLARED, derived from its own surface set rather
            // than written down, so a miscounted expectation cannot pass.
            public int expectedStackedSurfaces;
            public int expectedBareRims;
            public int expectedRailedRims;
            // What the RENDERER reported it emitted.
            public int reportedStackedSurfaces;
            public int reportedBareRims;
            public int reportedRailedRims;

            // The invariant that carries the pit design (§3.3): delete every
            // directed Fall edge and the rest must still be one component.
            public bool fallFreeSubgraphConnected;
            public int unreachableSurfaces;

            // Three stacked coordinates, per the phase's evidence item 1.
            public bool apertureRimCarriesBothSurfaces;
            public bool chamberUnderGalleryCarriesBothSurfaces;
            public bool bridgeDeckCarriesBothSurfaces;

            // The aperture is a hole: floor below, nothing at gallery height.
            public bool apertureColumnIsOpen;
            // The `_E_` swap's whole point — a suspended surface has an
            // underside, and it is real collidable geometry.
            public bool gallerySoffitPresent;
            public bool groundFloorHasNoSoffit;
            // A player can stand under the gallery.
            public bool chamberHeadroomOpen;
        }

        /// <summary>
        /// Print the C1 two-layer episode fixture to the editor log.
        /// </summary>
        [MenuItem("Tools/Dungeon Lab/Print Two-Layer Episode Fixture")]
        public static void PrintTwoLayerEpisodeSnapshot()
        {
            Debug.Log($"[TWO_LAYER_EPISODE_FIXTURE]\n{BuildTwoLayerEpisodeSnapshot()}");
        }

        private static string BuildTwoLayerEpisodeSnapshot()
        {
            TwoLayerEpisodeFixture fixture = null;
            try
            {
                fixture = BuildTwoLayerEpisodeFixture();
                return string.Join("\n", new[]
                {
                    $"episode.levelHeight={fixture.levelHeight:0.###}",
                    $"episode.planCells={fixture.levels.Count}",
                    $"episode.stackedSurfaces={fixture.reportedStackedSurfaces}" +
                        $" (declared {fixture.expectedStackedSurfaces})",
                    $"episode.stackedSurfacesAgree=" +
                        $"{fixture.reportedStackedSurfaces == fixture.expectedStackedSurfaces}",
                    $"episode.bareRims={fixture.reportedBareRims} (declared {fixture.expectedBareRims})",
                    $"episode.railedRims={fixture.reportedRailedRims} (declared {fixture.expectedRailedRims})",
                    $"episode.rimsAgree=" +
                        $"{fixture.reportedBareRims == fixture.expectedBareRims && fixture.reportedRailedRims == fixture.expectedRailedRims}",
                    $"episode.transitions={fixture.transitions.Count}",
                    $"episode.fallFreeSubgraphConnected={fixture.fallFreeSubgraphConnected}",
                    $"episode.unreachableSurfaces={fixture.unreachableSurfaces}",
                    $"episode.apertureRimCarriesBothSurfaces={fixture.apertureRimCarriesBothSurfaces}",
                    $"episode.chamberUnderGalleryCarriesBothSurfaces={fixture.chamberUnderGalleryCarriesBothSurfaces}",
                    $"episode.bridgeDeckCarriesBothSurfaces={fixture.bridgeDeckCarriesBothSurfaces}",
                    $"episode.apertureColumnIsOpen={fixture.apertureColumnIsOpen}",
                    $"episode.gallerySoffitPresent={fixture.gallerySoffitPresent}",
                    $"episode.groundFloorHasNoSoffit={fixture.groundFloorHasNoSoffit}",
                    $"episode.chamberHeadroomOpen={fixture.chamberHeadroomOpen}",
                    $"episode.rendererRejected={fixture.buildReport.rejected}"
                });
            }
            finally
            {
                if (fixture?.root != null)
                    DestroyImmediate(fixture.root);
            }
        }

        /// <summary>
        /// Bake the C1 episode into the dungeon scene and its collision payload,
        /// so the live probe (design §13 Phase C, evidence leg 3) has something
        /// on the server to walk around in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It writes the dungeon scene and its client/server collision payloads,
        /// because those are what the server compiles in (`include_str!` in
        /// `open_world_scene.rs`) — there is no runtime world selector to point
        /// at a second payload. The dungeon it replaces is regenerated content;
        /// rebuild one when you want it back.
        /// </para>
        /// <para>
        /// It goes through `RandomDungeonSceneBuilder.BakeDungeonRoot`, the same
        /// tail a real rebuild runs, on purpose. A probe that exercised a
        /// parallel export path would be measuring its own plumbing rather than
        /// the generator's.
        /// </para>
        /// </remarks>
        [MenuItem("Tools/Dungeon Lab/Bake Two-Layer Episode Into The Dungeon Scene (TEMPORARY)")]
        public static void BakeTwoLayerEpisodeIntoDungeonScene()
        {
            UnityEngine.SceneManagement.Scene destination = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);

            TwoLayerEpisodeFixture fixture = BuildTwoLayerEpisodeFixture();
            GameObject root = fixture.root;
            root.name = RandomDungeonSceneBuilder.DungeonRootName;

            long stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            RandomDungeonSceneBuilder.BakeDungeonRoot(
                destination,
                root,
                seed: 0,
                RandomDungeonSceneBuilder.ScenePath,
                RandomDungeonSceneBuilder.DataKey,
                addSceneToBuildSettings: true,
                beforeModelImporterMutation: null,
                stageRecorder: null,
                ref stageStart,
                // The episode owns no gateways and no traps, and the module
                // PANICS every tick on an empty door manifest — measured, it
                // killed `game_tick` on the first live attempt. The production
                // manifests stay; their doors and traps sit far outside this
                // episode's footprint (nearest door x=46 against an episode
                // spanning x -16..16), so nothing they describe is in the way.
                exportInteractionManifests: false);

            // The probe needs FINAL world coordinates, and they are only final
            // after CenterDungeonSpawn has moved the root — which floor it
            // centres on is a tie-break over renderer bounds, not something to
            // re-derive in Python.
            string manifestPath = WriteTwoLayerEpisodeProbeManifest(fixture, root.transform.position);
            Debug.Log(
                $"[TWO_LAYER_EPISODE_BAKE] scene={RandomDungeonSceneBuilder.ScenePath} " +
                $"rootOffset={root.transform.position} manifest={manifestPath}");
        }

        private static string WriteTwoLayerEpisodeProbeManifest(
            TwoLayerEpisodeFixture fixture, Vector3 rootOffset)
        {
            float h = fixture.levelHeight;
            int upper = fixture.upperLevel;

            JObject Point(int cx, int cy, int level, string note)
            {
                return new JObject
                {
                    ["cell"] = new JArray(cx, cy),
                    ["level"] = level,
                    ["x"] = rootOffset.x + (cx + 0.5f) * 4f,
                    ["y"] = rootOffset.y + level * h,
                    ["z"] = rootOffset.z + (cy + 0.5f) * 4f,
                    ["note"] = note
                };
            }

            var manifest = new JObject
            {
                ["levelHeight"] = h,
                ["upperLevel"] = upper,
                ["rootOffset"] = new JArray(rootOffset.x, rootOffset.y, rootOffset.z),
                ["stackedSurfaces"] = fixture.reportedStackedSurfaces,
                ["bareRims"] = fixture.reportedBareRims,
                // Every point the probe walks to or asserts about, in the order
                // the route visits them.
                ["lowerRouteSpawnSide"] = Point(0, 0, 0, "lower route, near the world origin spawn"),
                ["chamberEntry"] = Point(0, 5, 0, "chamber, first cell north of the lower route"),
                ["chamberUnderGallery"] = Point(1, 7, 0, "chamber floor DIRECTLY under a gallery slab"),
                ["stairFoot"] = Point(-3, 6, 0, "return stair, bottom"),
                ["stairTop"] = Point(-3, 10, upper, "return stair, top — terrace level"),
                ["terrace"] = Point(0, 10, upper, "terrace, ground-backed at the gallery's level"),
                ["galleryEntry"] = Point(0, 9, upper, "gallery, first stacked surface off the terrace"),
                ["galleryRim"] = Point(0, 8, upper, "gallery cell whose SOUTH rim is bare — walk off here"),
                ["apertureLanding"] = Point(0, 7, 0, "chamber floor under the aperture — where the fall must land"),
                ["eastCorridor"] = Point(3, 5, upper, "level-4 corridor back toward the bridge rooms"),
                ["bridgeEastLanding"] = Point(
                    fixture.bridgeEastLanding.x, fixture.bridgeEastLanding.y, upper,
                    "east end of the aerial span"),
                ["bridgeDeck"] = Point(
                    fixture.bridgeStackedCell.x, fixture.bridgeStackedCell.y, fixture.bridgeDeckLevel,
                    "the span deck's stacked coordinate — lower route runs underneath at L0"),
                ["bridgeWestLanding"] = Point(
                    fixture.bridgeWestLanding.x, fixture.bridgeWestLanding.y, upper,
                    "west end of the aerial span"),
                ["lowerRouteUnderBridge"] = Point(
                    fixture.bridgeStackedCell.x, fixture.bridgeStackedCell.y, 0,
                    "lower route directly under the span deck")
            };

            string path = System.IO.Path.Combine(BatchReportDirectory, "two_layer_episode_probe.json");
            System.IO.Directory.CreateDirectory(BatchReportDirectory);
            System.IO.File.WriteAllText(path, manifest.ToString(Formatting.Indented));
            return path;
        }

        private static TwoLayerEpisodeFixture BuildTwoLayerEpisodeFixture()
        {
            const int upperLevel = 4;
            var apertureCell = new Vector2Int(0, 7);

            // ---- the lower layer -------------------------------------------
            RoomFootprint west = RoomFootprint.FromRect(new RectInt(-4, -1, 2, 3));
            RoomFootprint east = RoomFootprint.FromRect(new RectInt(3, -1, 2, 3));
            var floorCells = new HashSet<Vector2Int>();
            floorCells.UnionWith(west.cells);
            floorCells.UnionWith(east.cells);
            var levels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in floorCells)
                levels[cell] = upperLevel;

            void AddFloor(Vector2Int cell, int level)
            {
                floorCells.Add(cell);
                levels[cell] = level;
            }

            // The lower playable route the bridge crosses, unchanged in shape
            // from the Phase B fixture so the bridge still forms the same way.
            for (int y = -4; y <= 4; y++)
                AddFloor(new Vector2Int(0, y), 0);

            // The chamber the gallery stands over and the aperture drops into.
            var chamberCells = new List<Vector2Int>();
            for (int x = -2; x <= 2; x++)
            {
                for (int y = 5; y <= 9; y++)
                {
                    var cell = new Vector2Int(x, y);
                    chamberCells.Add(cell);
                    AddFloor(cell, 0);
                }
            }

            // The return stair's ground column, climbing beside the chamber.
            for (int step = 0; step <= 3; step++)
                AddFloor(new Vector2Int(-3, 6 + step), step);

            // The upper route's ground-backed anchor: a terrace across the north
            // end, and a corridor down the east side back to the bridge rooms.
            for (int x = -3; x <= 2; x++)
                AddFloor(new Vector2Int(x, 10), upperLevel);
            for (int y = 2; y <= 10; y++)
                AddFloor(new Vector2Int(3, y), upperLevel);

            // ---- the upper layer, as stacked surfaces ----------------------
            // Hand-constructed, which is what makes this a fixture: nothing in
            // generation calls AddSurface, so production stays single-layer by
            // construction rather than by measurement.
            var surfaces = new SurfaceField(levels);
            var galleryCells = new List<Vector2Int>();
            for (int x = -1; x <= 1; x++)
            {
                for (int y = 5; y <= 9; y++)
                {
                    var cell = new Vector2Int(x, y);
                    if (cell == apertureCell)
                    {
                        continue;
                    }

                    galleryCells.Add(cell);
                    surfaces.AddSurface(cell, upperLevel, SurfaceKind.Ledge);
                }
            }

            List<ElevationEdgeModel.StackedSurface> stacked =
                new List<ElevationEdgeModel.StackedSurface>(surfaces.StackedSurfaces());
            if (surfaces.IsSingleLayer || stacked.Count != galleryCells.Count)
            {
                throw new InvalidOperationException(
                    $"Two-layer fixture expected {galleryCells.Count} stacked surfaces; the field holds {stacked.Count}.");
            }

            // ---- transitions: the bridge, then the return stair -------------
            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { west, east },
                new List<RoomConnection>());
            var transitions = new List<ElevationEdgeModel.TransitionEdge>();
            var prisms = new PrismLedger();
            // The STACKED field, not the bare heightfield it is backed by. The
            // fixture used to hand this pass `levels`, which is the very reader
            // defect C2b-1 closes: the gallery would have been invisible to the
            // deck's clearance test. It does not sit over the bridge line, so the
            // bridge still forms — but it is now checked against, not ignored.
            AddAerialBridges(
                layout,
                surfaces,
                new System.Random(7),
                transitions,
                new HashSet<string>(),
                prisms,
                new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>(),
                new HashSet<Vector2Int>());
            ElevationEdgeModel.TransitionEdge bridge = transitions.Single(transition =>
                string.Equals(
                    transition.placementClass,
                    ExternalSpanStairPlacementClass,
                    StringComparison.Ordinal));
            int bridgeDeckLevel = DeckLevelOf(prisms, bridge.footprintCells);
            Vector2Int bridgeStackedCell = bridge.footprintCells
                .Single(cell => levels.TryGetValue(cell, out int lower) &&
                    bridgeDeckLevel - lower >= MinHeadroomLevels);

            // The return stair is four rise-1 seam strips, which is the strip
            // family the generator itself uses for a corridor step. Four of them
            // climb the gallery's whole 4u without needing a multi-rise contract
            // the catalog may or may not carry.
            string seamStairPrefabPath = ResolveSeamStairPrefabPath();
            for (int step = 0; step < 4; step++)
            {
                transitions.Add(new ElevationEdgeModel.TransitionEdge(
                    new Vector2Int(-3, 7 + step),
                    new Vector2Int(-3, 6 + step),
                    seamStairPrefabPath,
                    SeamStairPlacementClass));
            }

            // ---- the aperture's bare rim -----------------------------------
            // §5: guarding is per (rim surface, direction), so a pit railed on
            // three sides and bare on one is expressible. Here all four are bare.
            var plannedOpenEdges = new List<ElevationEdgeModel.OpenFloorEdge>
            {
                new ElevationEdgeModel.OpenFloorEdge(
                    apertureCell + new Vector2Int(0, -1), upperLevel, Direction.North),
                new ElevationEdgeModel.OpenFloorEdge(
                    apertureCell + new Vector2Int(0, 1), upperLevel, Direction.South),
                new ElevationEdgeModel.OpenFloorEdge(
                    apertureCell + new Vector2Int(-1, 0), upperLevel, Direction.East),
                new ElevationEdgeModel.OpenFloorEdge(
                    apertureCell + new Vector2Int(1, 0), upperLevel, Direction.West)
            };

            DeriveExpectedStackedRims(
                levels,
                stacked,
                plannedOpenEdges,
                upperLevel,
                out int expectedBareRims,
                out int expectedRailedRims);

            GameObject root = ElevationEdgeModel.BuildLevelField(
                Vector3.zero,
                levels,
                stacked,
                transitions,
                null,
                plannedOpenEdges,
                null,
                null,
                ElevationEdgeModel.TrapPlacementSettings.Disabled,
                "Two Layer Episode Fixture",
                out ElevationEdgeModel.BuildReport buildReport,
                out _);
            Physics.SyncTransforms();

            float levelHeight = buildReport.levelHeight;
            Collider[] colliders = SolidColliders(root);
            float galleryY = upperLevel * levelHeight;
            var rimCell = new Vector2Int(0, 6);
            var underGalleryCell = new Vector2Int(1, 7);

            return new TwoLayerEpisodeFixture
            {
                levels = levels,
                stacked = stacked,
                transitions = transitions,
                root = root,
                buildReport = buildReport,
                apertureCell = apertureCell,
                upperLevel = upperLevel,
                levelHeight = levelHeight,
                bridgeWestLanding =
                    bridge.firstCell.x <= bridge.secondCell.x ? bridge.firstCell : bridge.secondCell,
                bridgeEastLanding =
                    bridge.firstCell.x <= bridge.secondCell.x ? bridge.secondCell : bridge.firstCell,
                bridgeStackedCell = bridgeStackedCell,
                bridgeDeckLevel = bridgeDeckLevel,
                expectedStackedSurfaces = galleryCells.Count,
                expectedBareRims = expectedBareRims,
                expectedRailedRims = expectedRailedRims,
                reportedStackedSurfaces = buildReport.stackedSurfaces,
                reportedBareRims = buildReport.stackedBareRims,
                reportedRailedRims = buildReport.stackedRailedRims,
                fallFreeSubgraphConnected = FallFreeSubgraphIsConnected(
                    levels,
                    stacked,
                    transitions,
                    out int unreachable),
                unreachableSurfaces = unreachable,

                // Stacked coordinate 1 — an aperture rim: gallery slab above,
                // chamber floor below, in the same plan column.
                apertureRimCarriesBothSurfaces =
                    HasWalkSurfaceAt(colliders, rimCell, 0f) &&
                    HasWalkSurfaceAt(colliders, rimCell, galleryY),
                // Stacked coordinate 2 — chamber floor under the gallery.
                chamberUnderGalleryCarriesBothSurfaces =
                    HasWalkSurfaceAt(colliders, underGalleryCell, 0f) &&
                    HasWalkSurfaceAt(colliders, underGalleryCell, galleryY),
                // Stacked coordinate 3 — the bridge deck over the lower route.
                bridgeDeckCarriesBothSurfaces =
                    HasWalkSurfaceAt(colliders, bridgeStackedCell, 0f) &&
                    HasAnyColliderInBand(
                        colliders,
                        bridgeStackedCell,
                        bridgeDeckLevel * levelHeight - 0.6f,
                        bridgeDeckLevel * levelHeight + 0.6f),

                // The hole: chamber floor present, gallery height empty.
                apertureColumnIsOpen =
                    HasWalkSurfaceAt(colliders, apertureCell, 0f) &&
                    !HasAnyColliderInBand(
                        colliders,
                        apertureCell,
                        galleryY - 0.6f,
                        galleryY + 0.4f),

                // The `_E_` slab hangs in [level - 0.5, level). A ground-backed
                // `_O_` floor is a plane at the walk surface with nothing under
                // it, which is the control that proves the swap is what put the
                // soffit there rather than some other pass.
                gallerySoffitPresent = HasAnyColliderInBand(
                    colliders,
                    underGalleryCell,
                    galleryY - 0.45f,
                    galleryY - 0.05f),
                groundFloorHasNoSoffit = !HasAnyColliderInBand(
                    colliders,
                    new Vector2Int(3, 6),
                    upperLevel * levelHeight - 0.45f,
                    upperLevel * levelHeight - 0.05f),

                // A 1.8u player fits under a 4u gallery with a 0.5u slab.
                chamberHeadroomOpen = VolumeIsClear(
                    colliders,
                    underGalleryCell,
                    0.2f,
                    galleryY - 0.6f)
            };
        }

        /// <summary>
        /// Derive the rim counts from the fixture's OWN surface set, so the
        /// snapshot compares two independent derivations rather than the
        /// renderer against a number somebody typed.
        /// </summary>
        private static void DeriveExpectedStackedRims(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<ElevationEdgeModel.StackedSurface> stacked,
            IReadOnlyList<ElevationEdgeModel.OpenFloorEdge> plannedOpenEdges,
            int upperLevel,
            out int bareRims,
            out int railedRims)
        {
            var surfacesAtUpper = new HashSet<Vector2Int>();
            foreach (KeyValuePair<Vector2Int, int> item in levels)
            {
                if (item.Value == upperLevel)
                {
                    surfacesAtUpper.Add(item.Key);
                }
            }

            foreach (ElevationEdgeModel.StackedSurface surface in stacked)
            {
                if (surface.level == upperLevel)
                {
                    surfacesAtUpper.Add(surface.cell);
                }
            }

            var bare = new HashSet<(Vector2Int, int, int)>();
            foreach (ElevationEdgeModel.OpenFloorEdge edge in plannedOpenEdges)
            {
                if (edge.IsSurfaceScoped)
                {
                    bare.Add((edge.cell, edge.level, edge.direction));
                }
            }

            bareRims = 0;
            railedRims = 0;
            foreach (ElevationEdgeModel.StackedSurface surface in stacked)
            {
                foreach (int direction in Direction.Cardinals)
                {
                    Vector2Int neighbor = NeighborCell(surface.cell, direction);
                    if (surface.level == upperLevel && surfacesAtUpper.Contains(neighbor))
                    {
                        continue;
                    }

                    if (bare.Contains((surface.cell, surface.level, direction)))
                    {
                        bareRims++;
                    }
                    else
                    {
                        railedRims++;
                    }
                }
            }
        }

        private static Vector2Int NeighborCell(Vector2Int cell, int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return new Vector2Int(cell.x, cell.y + 1);
                case Direction.East:
                    return new Vector2Int(cell.x + 1, cell.y);
                case Direction.South:
                    return new Vector2Int(cell.x, cell.y - 1);
                default:
                    return new Vector2Int(cell.x - 1, cell.y);
            }
        }

        /// <summary>
        /// Design §3.3: delete every directed <c>Fall</c> edge and what remains
        /// must be one component.
        /// </summary>
        /// <remarks>
        /// Walked over the fixture's own surfaces rather than through
        /// <c>TryBuildFloorStairPortGraph</c>, and that is a finding rather than
        /// a shortcut: the production port graph keys its nodes on the level
        /// FIELD, so it cannot see a stacked surface at all. Teaching it to is
        /// the traversal-graph work of §3.2, which C1 does not do — C1 proves
        /// the RENDERER. The invariant is still worth stating here, because a
        /// two-layer episode whose return stair does not actually return is a
        /// design failure the geometry probes would happily pass.
        /// </remarks>
        private static bool FallFreeSubgraphIsConnected(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<ElevationEdgeModel.StackedSurface> stacked,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            out int unreachable)
        {
            var nodes = new HashSet<(Vector2Int cell, int level)>();
            foreach (KeyValuePair<Vector2Int, int> item in levels)
            {
                nodes.Add((item.Key, item.Value));
            }

            foreach (ElevationEdgeModel.StackedSurface surface in stacked)
            {
                nodes.Add((surface.cell, surface.level));
            }

            var adjacency = new Dictionary<(Vector2Int, int), List<(Vector2Int, int)>>();
            void Link((Vector2Int, int) a, (Vector2Int, int) b)
            {
                if (!nodes.Contains(a) || !nodes.Contains(b))
                {
                    return;
                }

                if (!adjacency.TryGetValue(a, out List<(Vector2Int, int)> fromA))
                {
                    fromA = new List<(Vector2Int, int)>();
                    adjacency[a] = fromA;
                }

                fromA.Add(b);
                if (!adjacency.TryGetValue(b, out List<(Vector2Int, int)> fromB))
                {
                    fromB = new List<(Vector2Int, int)>();
                    adjacency[b] = fromB;
                }

                fromB.Add(a);
            }

            foreach ((Vector2Int cell, int level) node in nodes)
            {
                foreach (int direction in Direction.Cardinals)
                {
                    Link(node, (NeighborCell(node.cell, direction), node.level));
                }
            }

            // A transition is bidirectional by definition here: Fall is the only
            // directed kind and this episode declares none as a transition.
            foreach (ElevationEdgeModel.TransitionEdge transition in transitions)
            {
                if (levels.TryGetValue(transition.firstCell, out int firstLevel) &&
                    levels.TryGetValue(transition.secondCell, out int secondLevel))
                {
                    Link((transition.firstCell, firstLevel), (transition.secondCell, secondLevel));
                }
            }

            var visited = new HashSet<(Vector2Int, int)>();
            var queue = new Queue<(Vector2Int, int)>();
            (Vector2Int, int) start = nodes.First();
            visited.Add(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                (Vector2Int, int) node = queue.Dequeue();
                if (!adjacency.TryGetValue(node, out List<(Vector2Int, int)> neighbors))
                {
                    continue;
                }

                foreach ((Vector2Int, int) neighbor in neighbors)
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            unreachable = nodes.Count - visited.Count;
            return unreachable == 0;
        }

        private static Collider[] SolidColliders(GameObject root)
        {
            return root
                .GetComponentsInChildren<Collider>(includeInactive: false)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                .ToArray();
        }

        private static Vector3 CellCenterWorld(Vector2Int cell)
        {
            return new Vector3((cell.x + 0.5f) * 4f, 0f, (cell.y + 0.5f) * 4f);
        }

        private static bool CoversCellCenter(Collider collider, Vector2Int cell)
        {
            Vector3 center = CellCenterWorld(cell);
            Bounds bounds = collider.bounds;
            return center.x >= bounds.min.x && center.x <= bounds.max.x &&
                center.z >= bounds.min.z && center.z <= bounds.max.z;
        }

        /// <summary>Is there collidable geometry whose extent brackets this height?</summary>
        private static bool HasWalkSurfaceAt(Collider[] colliders, Vector2Int cell, float y)
        {
            return colliders.Any(collider =>
                CoversCellCenter(collider, cell) &&
                collider.bounds.min.y <= y + 0.1f &&
                collider.bounds.max.y >= y - 0.1f);
        }

        private static bool HasAnyColliderInBand(Collider[] colliders, Vector2Int cell, float minY, float maxY)
        {
            return colliders.Any(collider =>
                CoversCellCenter(collider, cell) &&
                collider.bounds.max.y >= minY &&
                collider.bounds.min.y <= maxY);
        }

        private static bool VolumeIsClear(Collider[] colliders, Vector2Int cell, float minY, float maxY)
        {
            var volume = new Bounds(
                CellCenterWorld(cell) + Vector3.up * (minY + maxY) * 0.5f,
                new Vector3(1f, Mathf.Max(0.01f, maxY - minY), 1f));
            return !colliders.Any(collider => collider.bounds.Intersects(volume));
        }

        private static StackedCrossingFixture BuildStackedCrossingFixture()
        {
            RoomFootprint west = RoomFootprint.FromRect(new RectInt(-4, -1, 2, 3));
            RoomFootprint east = RoomFootprint.FromRect(new RectInt(3, -1, 2, 3));
            var floorCells = new HashSet<Vector2Int>();
            floorCells.UnionWith(west.cells);
            floorCells.UnionWith(east.cells);
            var levels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in floorCells)
                levels[cell] = 4;

            Vector2Int lowerStart = new Vector2Int(0, -4);
            Vector2Int lowerEnd = new Vector2Int(0, 4);
            for (int y = lowerStart.y; y <= lowerEnd.y; y++)
            {
                var cell = new Vector2Int(0, y);
                floorCells.Add(cell);
                levels[cell] = 0;
            }

            var layout = new DungeonLayout(
                floorCells,
                new List<RoomFootprint> { west, east },
                new List<RoomConnection>());
            var transitions = new List<ElevationEdgeModel.TransitionEdge>();
            var prisms = new PrismLedger();
            // Single-layer by construction here (Phase B's crossing fixture), so
            // wrapping the heightfield is exactly what this pass saw before.
            AddAerialBridges(
                layout,
                new SurfaceField(levels),
                new System.Random(7),
                transitions,
                new HashSet<string>(),
                prisms,
                new List<(string gapId, ElevationEdgeModel.SynthesizedStairSetPiece setPiece)>(),
                new HashSet<Vector2Int>());
            ElevationEdgeModel.TransitionEdge bridge = transitions.Single(transition =>
                string.Equals(
                    transition.placementClass,
                    ExternalSpanStairPlacementClass,
                    StringComparison.Ordinal));
            // The deck level is no longer a side table the fixture is handed —
            // the ledger holds it, and the probe reads it back the same way the
            // headroom rule does.
            int deckLevel = DeckLevelOf(prisms, bridge.footprintCells);
            List<Vector2Int> stackedCells = bridge.footprintCells
                .Where(cell => levels.TryGetValue(cell, out int lowerLevel) &&
                    deckLevel - lowerLevel >= MinHeadroomLevels)
                .ToList();
            if (stackedCells.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Focused fixture expected one exact stacked coordinate; found {stackedCells.Count}.");
            }

            // Negative fixture 1 (design §13 Phase B): the SAME probe as before,
            // retargeted at the general ledger rule rather than duplicated. An
            // artificially raised floor under the deck is still rejected.
            bool positiveHeadroom = prisms.TryValidateSurfaceHeadroom(new SurfaceField(levels), out _);
            var negativeLevels = new Dictionary<Vector2Int, int>(levels)
            {
                [stackedCells[0]] = deckLevel - 2
            };
            bool negativeRejected = !prisms.TryValidateSurfaceHeadroom(new SurfaceField(negativeLevels), out _);
            bool lowerTraversable = EqualLevelPathExists(
                levels,
                lowerStart,
                lowerEnd,
                0);
            var upperLevels = new Dictionary<Vector2Int, int>();
            foreach (Vector2Int cell in west.cells)
                upperLevels[cell] = 4;
            foreach (Vector2Int cell in east.cells)
                upperLevels[cell] = 4;
            bool upperGraphBuilt = TryBuildFloorStairPortGraph(
                upperLevels,
                transitions,
                out FloorStairPortGraph upperGraph,
                out _);
            bool upperTraversable = upperGraphBuilt && upperGraph.IsFallFreeConnected(out _);

            GameObject root = ElevationEdgeModel.BuildLevelField(
                Vector3.zero,
                levels,
                transitions,
                null,
                null,
                null,
                null,
                "Corrective Stacked Crossing Fixture",
                out ElevationEdgeModel.BuildReport buildReport,
                out _);
            Physics.SyncTransforms();
            CollectStackedSurfaceEvidence(
                root,
                stackedCells[0],
                deckLevel,
                buildReport.levelHeight,
                out bool lowerClearanceOpen,
                out List<string> lowerSurfaceColliders,
                out List<string> upperSurfaceColliders);

            ProbeHalfOpenHeadroomEndpoint(
                out bool exactPassed,
                out bool oneShortRejected);
            ProbeConflictPolicy(
                out bool landingOverLanding,
                out bool landingOverClearance,
                out bool mouthOverClearance,
                out bool landingOverFootprint,
                out bool transitionClearanceOverMouth,
                out bool sameOwnerLegal);
            ProbeOpenVolumePenetration(
                out bool volumeBlocksForeign,
                out bool volumeAdmitsAllowListed,
                out bool volumeBlocksItsOwn);

            return new StackedCrossingFixture
            {
                levels = levels,
                transitions = transitions,
                prisms = prisms,
                stackedDeckLevel = deckLevel,
                stackedCell = stackedCells[0],
                lowerStart = lowerStart,
                lowerEnd = lowerEnd,
                bridge = bridge,
                root = root,
                buildReport = buildReport,
                lowerRouteTraversable = lowerTraversable,
                upperBridgeTraversable = upperTraversable,
                positiveHeadroomPassed = positiveHeadroom,
                negativeHeadroomRejected = negativeRejected,
                exactHeadroomPassed = exactPassed,
                oneShortOfExactRejected = oneShortRejected,
                landingOverLandingLegal = landingOverLanding,
                landingOverClearanceLegal = landingOverClearance,
                mouthOverClearanceLegal = mouthOverClearance,
                landingOverFootprintRejected = landingOverFootprint,
                transitionClearanceOverMouthRejected = transitionClearanceOverMouth,
                sameOwnerFootprintUnderOwnClearanceLegal = sameOwnerLegal,
                openVolumeBlocksForeignFloor = volumeBlocksForeign,
                openVolumeAdmitsAllowListedFloor = volumeAdmitsAllowListed,
                openVolumeBlocksItsOwnFloor = volumeBlocksItsOwn,
                lowerClearanceOpen = lowerClearanceOpen,
                lowerSurfaceColliderNames = lowerSurfaceColliders,
                upperSurfaceColliderNames = upperSurfaceColliders
            };
        }

        /// <summary>The base level of the deck the ledger recorded for these cells.</summary>
        private static int DeckLevelOf(PrismLedger prisms, IReadOnlyList<Vector2Int> deckCells)
        {
            foreach (Vector2Int cell in deckCells)
            {
                if (prisms.TryGetLowestStructureBase(cell, out int deckLevel))
                {
                    return deckLevel;
                }
            }

            throw new InvalidOperationException(
                "Focused fixture expected the ledger to hold a deck over the bridge footprint.");
        }

        /// <summary>
        /// Negative fixture 2 (design §13 Phase B): the half-open endpoint.
        /// </summary>
        /// <remarks>
        /// A clearance of EXACTLY <c>MinHeadroomLevels</c> passes and one less
        /// fails. This is the case the draft's closed `[level, level + 3]` band
        /// would have wrongly rejected, and it is the value the arithmetic gate
        /// (`clearance &lt; MinHeadroomLevels`) accepted, so getting it wrong
        /// silently narrows what the generator will build.
        /// </remarks>
        private static void ProbeHalfOpenHeadroomEndpoint(
            out bool exactPassed,
            out bool oneShortRejected)
        {
            var cell = new Vector2Int(0, 0);
            var owner = new OwnerKey(OwnerFamily.Transition, "half-open-probe");
            var ledger = new PrismLedger();
            ledger.RegisterSpanDeck(new[] { cell }, MinHeadroomLevels, owner);

            exactPassed = ledger.TryValidateSurfaceHeadroom(
                new SurfaceField(new Dictionary<Vector2Int, int> { [cell] = 0 }),
                out _);
            oneShortRejected = !ledger.TryValidateSurfaceHeadroom(
                new SurfaceField(new Dictionary<Vector2Int, int> { [cell] = 1 }),
                out _);
        }

        /// <summary>
        /// Negative fixture 3 (design §13 Phase B): the whole content of the
        /// asymmetric `blocksKinds` policy, in five cases plus the same-owner rule.
        /// </summary>
        /// <remarks>
        /// Landing-landing, landing-clearance and mouth-clearance are LEGAL
        /// today, and a symmetric conflict matrix rejects all three. A landing
        /// over another owner's footprint and a transition clearance over
        /// another owner's mouth are REJECTED, and merging the two clearance
        /// kinds into one loses the second. If any of the five flips, the port
        /// is not faithful.
        /// </remarks>
        private static void ProbeConflictPolicy(
            out bool landingOverLandingLegal,
            out bool landingOverClearanceLegal,
            out bool mouthOverClearanceLegal,
            out bool landingOverFootprintRejected,
            out bool transitionClearanceOverMouthRejected,
            out bool sameOwnerFootprintUnderOwnClearanceLegal)
        {
            var cell = new Vector2Int(0, 0);
            var incoming = new OwnerKey(OwnerFamily.Transition, "incoming");
            Vector2Int[] one = { cell };
            Vector2Int[] none = Array.Empty<Vector2Int>();

            bool Legal(Action<PrismLedger> register, PrismKind kind)
            {
                var ledger = new PrismLedger();
                register(ledger);
                return !ledger.Blocks(cell, LevelBand.Unbounded, kind, incoming);
            }

            void RegisterAs(PrismLedger ledger, PrismKind kind)
            {
                ledger.Register(
                    new OwnerKey(OwnerFamily.Transition, "registered"),
                    kind == PrismKind.Footprint ? one : none,
                    kind == PrismKind.Landing ? one : none,
                    none,
                    kind == PrismKind.Mouth ? one : none,
                    kind == PrismKind.FootprintClearance ? one : none,
                    kind == PrismKind.TransitionClearance ? one : none);
            }

            landingOverLandingLegal = Legal(l => RegisterAs(l, PrismKind.Landing), PrismKind.Landing);
            landingOverClearanceLegal =
                Legal(l => RegisterAs(l, PrismKind.FootprintClearance), PrismKind.Landing);
            mouthOverClearanceLegal =
                Legal(l => RegisterAs(l, PrismKind.FootprintClearance), PrismKind.Mouth);
            landingOverFootprintRejected =
                !Legal(l => RegisterAs(l, PrismKind.Footprint), PrismKind.Landing);
            transitionClearanceOverMouthRejected =
                !Legal(l => RegisterAs(l, PrismKind.Mouth), PrismKind.TransitionClearance);

            // Correction (b): without an owner a transition's own footprint
            // violates its own clearance, and clearance stops being expressible.
            var sameOwner = new PrismLedger();
            sameOwner.Register(incoming, one, none, none, none, one, none);
            sameOwnerFootprintUnderOwnClearanceLegal =
                !sameOwner.Blocks(cell, LevelBand.Unbounded, PrismKind.Footprint, incoming);
        }

        /// <summary>
        /// The <see cref="PrismKind.OpenVolume"/> mechanism (design §6): a
        /// reserved void blocks every solid kind except the owners its authored
        /// allow-list names — including its OWN.
        /// </summary>
        /// <remarks>
        /// Phase B ships the kind, the allow-list and the enforcement, and no
        /// producer. This probe is what keeps the mechanism honest until one
        /// exists: an atrium that forbade everything would forbid its own
        /// balconies, and the plain same-owner exemption would let the atrium's
        /// own floor fill its own void, which is worse.
        /// </remarks>
        private static void ProbeOpenVolumePenetration(
            out bool blocksForeignFloor,
            out bool admitsAllowListedFloor,
            out bool blocksItsOwnFloor)
        {
            var cell = new Vector2Int(0, 0);
            var atrium = new OwnerKey(OwnerFamily.Room, "great-atrium");
            var balcony = new OwnerKey(OwnerFamily.Room, "great-atrium#gallery");
            var stranger = new OwnerKey(OwnerFamily.Transition, "unrelated-stair");

            var ledger = new PrismLedger();
            ledger.RegisterOpenVolume(
                new[] { cell },
                new LevelBand(0, 12),
                atrium,
                new[] { balcony });

            blocksForeignFloor = ledger.Blocks(cell, LevelBand.From(4), PrismKind.Footprint, stranger);
            admitsAllowListedFloor = !ledger.Blocks(cell, LevelBand.From(4), PrismKind.Footprint, balcony);
            blocksItsOwnFloor = ledger.Blocks(cell, LevelBand.From(4), PrismKind.Footprint, atrium);
        }

        private static void CollectStackedSurfaceEvidence(
            GameObject root,
            Vector2Int stackedCell,
            int deckLevel,
            float levelHeight,
            out bool lowerClearanceOpen,
            out List<string> lowerSurfaceColliderNames,
            out List<string> upperSurfaceColliderNames)
        {
            Vector3 center = new Vector3(
                (stackedCell.x + 0.5f) * 4f,
                0f,
                (stackedCell.y + 0.5f) * 4f);
            Collider[] colliders = root
                .GetComponentsInChildren<Collider>(includeInactive: false)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                .ToArray();
            var clearance = new Bounds(
                center + Vector3.up * 1.5f * levelHeight,
                new Vector3(1f, 2.6f * levelHeight, 1f));
            lowerClearanceOpen = !colliders.Any(collider =>
                collider.bounds.Intersects(clearance));

            bool CoversHorizontalPoint(Collider collider)
            {
                Bounds bounds = collider.bounds;
                return center.x >= bounds.min.x && center.x <= bounds.max.x &&
                    center.z >= bounds.min.z && center.z <= bounds.max.z;
            }

            lowerSurfaceColliderNames = colliders
                .Where(collider => CoversHorizontalPoint(collider) &&
                    collider.bounds.min.y <= 0.1f && collider.bounds.max.y >= -0.1f)
                .Select(collider => ColliderHierarchyPath(collider.transform))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            float deckY = deckLevel * levelHeight;
            upperSurfaceColliderNames = colliders
                .Where(collider => CoversHorizontalPoint(collider) &&
                    collider.bounds.min.y <= deckY + 0.1f &&
                    collider.bounds.max.y >= deckY - 0.1f &&
                    ColliderHierarchyPath(collider.transform)
                        .Contains("Transition Stairs"))
                .Select(collider => ColliderHierarchyPath(collider.transform))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        private static bool EqualLevelPathExists(
            IReadOnlyDictionary<Vector2Int, int> levels,
            Vector2Int start,
            Vector2Int end,
            int level)
        {
            var visited = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                if (cell == end)
                    return true;
                foreach (Vector2Int neighbor in CardinalNeighbors(cell))
                {
                    if (levels.TryGetValue(neighbor, out int neighborLevel) &&
                        neighborLevel == level &&
                        visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return false;
        }


        private static string ColliderHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = $"{transform.name}/{path}";
            }

            return path;
        }

    }
}
