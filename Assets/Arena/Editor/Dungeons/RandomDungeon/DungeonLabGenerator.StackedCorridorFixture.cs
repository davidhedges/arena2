using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase D2 of the layered 3D topology design
    // (docs/dungeon-builder/layered-topology-design-2026-07-29.md §8.1): two
    // connections may share a plan cell when BOTH are layer-bound and their
    // absolute bands are disjoint, and the corridor that ends up on top becomes
    // a suspended surface instead of losing an argument with the level field.
    //
    // Nothing in the shipped corpus declares a layer, which is what makes the
    // slice output-neutral — and is also why the capability needs a fixture. No
    // content reaches it until D5, so without this the first exercise of the
    // relaxation would be the first topology to use it, which is the arrangement
    // that cost C2 two rejected corpora.
    internal sealed partial class DungeonLabGenerator
    {
        private sealed class StackedCorridorFixture
        {
            // ---- the claim rule, corridor against corridor ------------------
            // "Layer binding authorizes an attempt. The absolute band decides."
            // Every one of these is a single variable away from its neighbour.
            public bool unboundPairRejected;
            public bool halfBoundPairRejected;
            public bool boundSameBandRejected;
            public bool boundOneShortOfHeadroomRejected;
            public bool boundExactlyHeadroomApartAccepted;
            public bool boundDisjointAccepted;
            // The message an unbound collision produces reaches the seed report,
            // so it is compared verbatim rather than by shape.
            public string unboundFailure = string.Empty;
            public bool unboundFailureUnchanged;

            // ---- the claim rule, corridor against a third room --------------
            public bool thirdRoomUnboundRejected;
            public bool thirdRoomBoundAtRoomLevelRejected;
            public bool thirdRoomBoundBelowRoomRejected;
            public bool thirdRoomBoundIntoDeclaredStoreyRejected;
            public bool thirdRoomBoundAboveAccepted;

            // ---- the producer ----------------------------------------------
            public bool loneCorridorStaysOnTheGround;
            public bool secondCorridorStacks;
            public bool producerIsOrderIndependent;
            public bool sameLevelClaimIsIdempotent;

            // ---- the rendered crossing --------------------------------------
            public Dictionary<Vector2Int, int> levels;
            public List<ElevationEdgeModel.StackedSurface> stacked;
            public List<ElevationEdgeModel.TransitionEdge> transitions;
            public GameObject root;
            public ElevationEdgeModel.BuildReport buildReport;
            public int upperLevel;
            public int crossedRoomCells;
            public int expectedStackedSurfaces;
            public int expectedRailedRims;
            public int expectedBareRims;
            public int reportedStackedSurfaces;
            public int reportedRailedRims;
            public int reportedBareRims;
            // The corridor is stacked only WHERE IT MUST BE: its own cells stay
            // ground-backed floors. Both numbers are the same corridor.
            public int corridorSurfacesOnTheGround;
            public int corridorSurfacesSuspended;
            public bool portGraphBuilt;
            public bool portGraphFallFreeConnected;
            public int portGraphNodes;
            public int expectedPortGraphNodes;
            public string portGraphReachability = string.Empty;
            // Standing under the catwalk is standing on the room floor.
            public bool roomHeadroomOpenUnderCatwalk;
            public bool catwalkSoffitPresent;
        }

        /// <summary>
        /// Print the D2 stacked-corridor fixture to the editor log.
        /// </summary>
        [MenuItem("Tools/Dungeon Lab/Print Stacked Corridor Fixture")]
        public static void PrintStackedCorridorSnapshot()
        {
            Debug.Log($"[STACKED_CORRIDOR_FIXTURE]\n{BuildStackedCorridorSnapshot()}");
        }

        private static string BuildStackedCorridorSnapshot()
        {
            StackedCorridorFixture fixture = null;
            try
            {
                fixture = BuildStackedCorridorFixture();
                return string.Join("\n", new[]
                {
                    $"claim.unboundPairRejected={fixture.unboundPairRejected}",
                    $"claim.unboundFailure=\"{fixture.unboundFailure}\"",
                    $"claim.unboundFailureUnchanged={fixture.unboundFailureUnchanged}",
                    $"claim.halfBoundPairRejected={fixture.halfBoundPairRejected}",
                    $"claim.boundSameBandRejected={fixture.boundSameBandRejected}",
                    $"claim.boundOneShortOfHeadroomRejected={fixture.boundOneShortOfHeadroomRejected}",
                    $"claim.boundExactlyHeadroomApartAccepted={fixture.boundExactlyHeadroomApartAccepted}",
                    $"claim.boundDisjointAccepted={fixture.boundDisjointAccepted}",
                    $"thirdRoom.unboundRejected={fixture.thirdRoomUnboundRejected}",
                    $"thirdRoom.boundAtRoomLevelRejected={fixture.thirdRoomBoundAtRoomLevelRejected}",
                    $"thirdRoom.boundBelowRoomRejected={fixture.thirdRoomBoundBelowRoomRejected}",
                    $"thirdRoom.boundIntoDeclaredStoreyRejected=" +
                        $"{fixture.thirdRoomBoundIntoDeclaredStoreyRejected}",
                    $"thirdRoom.boundAboveAccepted={fixture.thirdRoomBoundAboveAccepted}",
                    $"producer.loneCorridorStaysOnTheGround={fixture.loneCorridorStaysOnTheGround}",
                    $"producer.secondCorridorStacks={fixture.secondCorridorStacks}",
                    $"producer.orderIndependent={fixture.producerIsOrderIndependent}",
                    $"producer.sameLevelClaimIsIdempotent={fixture.sameLevelClaimIsIdempotent}",
                    $"crossing.planCells={fixture.levels.Count}",
                    $"crossing.crossedRoomCells={fixture.crossedRoomCells}",
                    $"crossing.corridorSurfacesOnTheGround={fixture.corridorSurfacesOnTheGround}",
                    $"crossing.corridorSurfacesSuspended={fixture.corridorSurfacesSuspended}",
                    $"crossing.stackedSurfaces={fixture.reportedStackedSurfaces}" +
                        $" (declared {fixture.expectedStackedSurfaces})",
                    $"crossing.stackedSurfacesAgree=" +
                        $"{fixture.reportedStackedSurfaces == fixture.expectedStackedSurfaces}",
                    $"crossing.railedRims={fixture.reportedRailedRims}" +
                        $" (declared {fixture.expectedRailedRims})",
                    $"crossing.bareRims={fixture.reportedBareRims}" +
                        $" (declared {fixture.expectedBareRims})",
                    $"crossing.rimsAgree=" +
                        $"{fixture.reportedRailedRims == fixture.expectedRailedRims && fixture.reportedBareRims == fixture.expectedBareRims}",
                    $"crossing.portGraphBuilt={fixture.portGraphBuilt}",
                    $"crossing.portGraphFallFreeConnected={fixture.portGraphFallFreeConnected}",
                    $"crossing.portGraphNodes={fixture.portGraphNodes}" +
                        $" (expected {fixture.expectedPortGraphNodes})",
                    $"crossing.portGraphSeesEverySurface=" +
                        $"{fixture.portGraphNodes == fixture.expectedPortGraphNodes}",
                    $"crossing.portGraphReachability={fixture.portGraphReachability}",
                    $"crossing.roomHeadroomOpenUnderCatwalk={fixture.roomHeadroomOpenUnderCatwalk}",
                    $"crossing.catwalkSoffitPresent={fixture.catwalkSoffitPresent}",
                    $"crossing.rendererRejected={fixture.buildReport.rejected}"
                });
            }
            finally
            {
                if (fixture?.root != null)
                    DestroyImmediate(fixture.root);
            }
        }

        // ------------------------------------------------------------------
        // The claim rule
        // ------------------------------------------------------------------

        // Four rooms placed so that neither corridor can touch the other pair's,
        // which keeps the third-room rule out of the corridor-sharing cases.
        private static readonly RoomFootprint ClaimWestRoom =
            RoomFootprint.FromRect(new RectInt(-8, -1, 2, 3));
        private static readonly RoomFootprint ClaimEastRoom =
            RoomFootprint.FromRect(new RectInt(7, -1, 2, 3));
        private static readonly RoomFootprint ClaimSouthRoom =
            RoomFootprint.FromRect(new RectInt(-1, -8, 3, 2));
        private static readonly RoomFootprint ClaimNorthRoom =
            RoomFootprint.FromRect(new RectInt(-1, 7, 3, 2));

        private static List<Vector2Int> StraightRun(Vector2Int from, Vector2Int to)
        {
            var path = new List<Vector2Int>();
            Vector2Int step = new Vector2Int(
                Math.Sign(to.x - from.x),
                Math.Sign(to.y - from.y));
            Vector2Int cell = from;
            path.Add(cell);
            while (cell != to)
            {
                cell += step;
                path.Add(cell);
            }

            return path;
        }

        private static RouteTraversalIntent ClaimEdge(string id, int from, int to, bool bound, int level)
        {
            // A base layer — relative level 0 — is the smallest thing that makes
            // an edge layer-bound. It declares no storey; it names an elevation
            // so an edge can bind it, which is the whole of the authorization.
            return new RouteTraversalIntent(
                id,
                from,
                to,
                0,
                RouteTransitionKind.LevelCorridor,
                bound ? "floor" : string.Empty,
                string.Empty,
                level,
                level);
        }

        /// <summary>
        /// Claim an east-west corridor, then try to cross it with a north-south
        /// one. Returns whether the SECOND claim was allowed.
        /// </summary>
        private static bool TryCrossTwoCorridors(
            bool firstBound,
            int firstLevel,
            bool secondBound,
            int secondLevel,
            out string failure)
        {
            var rooms = new List<RoomFootprint>
            {
                ClaimWestRoom, ClaimEastRoom, ClaimSouthRoom, ClaimNorthRoom
            };
            int[][] elevations =
            {
                new[] { firstLevel }, new[] { firstLevel },
                new[] { secondLevel }, new[] { secondLevel }
            };
            var noVista = new HashSet<Vector2Int>();
            var ledger = new CorridorClaimLedger();

            List<Vector2Int> eastWest = StraightRun(new Vector2Int(-6, 0), new Vector2Int(6, 0));
            LevelBand firstBand = LevelBand.SpanningEndpoints(firstLevel, firstLevel);
            if (!TryClaimCorridor(
                    eastWest,
                    rooms,
                    ClaimEdge("east-west", 0, 1, firstBound, firstLevel),
                    ClaimWestRoom,
                    ClaimEastRoom,
                    firstBand,
                    elevations,
                    ledger,
                    noVista,
                    out failure))
            {
                throw new InvalidOperationException(
                    $"D2 fixture expected the FIRST corridor to claim freely; it failed with '{failure}'.");
            }

            foreach (Vector2Int cell in eastWest)
            {
                ledger.Add(cell, firstBand, firstBound);
            }

            return TryClaimCorridor(
                StraightRun(new Vector2Int(0, -6), new Vector2Int(0, 6)),
                rooms,
                ClaimEdge("south-north", 2, 3, secondBound, secondLevel),
                ClaimSouthRoom,
                ClaimNorthRoom,
                LevelBand.SpanningEndpoints(secondLevel, secondLevel),
                elevations,
                ledger,
                noVista,
                out failure);
        }

        /// <summary>
        /// Run a corridor straight through an unrelated room. Returns whether
        /// the crossing was REJECTED, which is what the predicate reports.
        /// </summary>
        private static bool ThirdRoomCrossingRejected(
            bool bound,
            int corridorLevel,
            int[] roomDeclaredElevations)
        {
            var crossed = RoomFootprint.FromRect(new RectInt(-2, -2, 5, 5));
            var rooms = new List<RoomFootprint> { ClaimWestRoom, ClaimEastRoom, crossed };
            int[][] elevations =
            {
                new[] { corridorLevel }, new[] { corridorLevel }, roomDeclaredElevations
            };
            return PathCrossesThirdRoom(
                StraightRun(new Vector2Int(-6, 0), new Vector2Int(6, 0)),
                rooms,
                0,
                1,
                LevelBand.SpanningEndpoints(corridorLevel, corridorLevel),
                bound,
                elevations);
        }

        // ------------------------------------------------------------------
        // The fixture
        // ------------------------------------------------------------------

        private static StackedCorridorFixture BuildStackedCorridorFixture()
        {
            const int upperLevel = 4;
            var fixture = new StackedCorridorFixture { upperLevel = upperLevel };

            // ---- the claim rule, corridor against corridor ------------------
            fixture.unboundPairRejected = !TryCrossTwoCorridors(
                firstBound: false, firstLevel: 0,
                secondBound: false, secondLevel: upperLevel,
                out fixture.unboundFailure);
            fixture.unboundFailureUnchanged = string.Equals(
                fixture.unboundFailure,
                $"another connection already owns {new Vector2Int(0, 0)}",
                StringComparison.Ordinal);
            fixture.halfBoundPairRejected = !TryCrossTwoCorridors(
                firstBound: false, firstLevel: 0,
                secondBound: true, secondLevel: upperLevel,
                out _);
            fixture.boundSameBandRejected = !TryCrossTwoCorridors(
                firstBound: true, firstLevel: 0,
                secondBound: true, secondLevel: 0,
                out _);
            // Half-open bands: MinHeadroomLevels apart passes, one less fails.
            // A closed band gets the first of these wrong, and the separation the
            // band test buys IS the headroom the crossing needs.
            fixture.boundOneShortOfHeadroomRejected = !TryCrossTwoCorridors(
                firstBound: true, firstLevel: 0,
                secondBound: true, secondLevel: MinHeadroomLevels - 1,
                out _);
            fixture.boundExactlyHeadroomApartAccepted = TryCrossTwoCorridors(
                firstBound: true, firstLevel: 0,
                secondBound: true, secondLevel: MinHeadroomLevels,
                out _);
            fixture.boundDisjointAccepted = TryCrossTwoCorridors(
                firstBound: true, firstLevel: 0,
                secondBound: true, secondLevel: upperLevel,
                out _);

            // ---- the claim rule, corridor against a third room --------------
            fixture.thirdRoomUnboundRejected =
                ThirdRoomCrossingRejected(bound: false, upperLevel, new[] { 0 });
            fixture.thirdRoomBoundAtRoomLevelRejected =
                ThirdRoomCrossingRejected(bound: true, 0, new[] { 0 });
            // Upward only. Passing UNDER a room would have to suspend the room's
            // own floor, and a room floor is ground-backed by construction.
            fixture.thirdRoomBoundBelowRoomRejected =
                ThirdRoomCrossingRejected(bound: true, 0, new[] { upperLevel });
            fixture.thirdRoomBoundIntoDeclaredStoreyRejected =
                ThirdRoomCrossingRejected(bound: true, upperLevel, new[] { 0, upperLevel });
            fixture.thirdRoomBoundAboveAccepted =
                !ThirdRoomCrossingRejected(bound: true, upperLevel, new[] { 0 });

            // ---- the producer ----------------------------------------------
            var probeCell = new Vector2Int(0, 0);
            var lone = new SurfaceField(new Dictionary<Vector2Int, int>());
            lone.AddCorridorSurface(probeCell, upperLevel);
            fixture.loneCorridorStaysOnTheGround =
                lone.IsSingleLayer &&
                lone.TryGetFloorLevel(probeCell, out int loneFloor) &&
                loneFloor == upperLevel &&
                lone.IsGroundBacked(probeCell, upperLevel);

            var lowFirst = new SurfaceField(new Dictionary<Vector2Int, int>());
            lowFirst.AddCorridorSurface(probeCell, 0);
            lowFirst.AddCorridorSurface(probeCell, upperLevel);
            fixture.secondCorridorStacks =
                !lowFirst.IsSingleLayer &&
                lowFirst.TryGetFloorLevel(probeCell, out int lowFloor) && lowFloor == 0 &&
                lowFirst.HasSurfaceAt(probeCell, upperLevel) &&
                lowFirst.KindAt(probeCell, 0) == SurfaceKind.Floor &&
                lowFirst.KindAt(probeCell, upperLevel) == SurfaceKind.Ledge &&
                lowFirst.IsGroundBacked(probeCell, 0) &&
                !lowFirst.IsGroundBacked(probeCell, upperLevel);

            // Which of two crossing corridors resolves first is the order the
            // topology author listed their edges in; the geometry may not depend
            // on it.
            var highFirst = new SurfaceField(new Dictionary<Vector2Int, int>());
            highFirst.AddCorridorSurface(probeCell, upperLevel);
            highFirst.AddCorridorSurface(probeCell, 0);
            fixture.producerIsOrderIndependent =
                DescribeColumn(highFirst, probeCell) == DescribeColumn(lowFirst, probeCell);

            var repeated = new SurfaceField(new Dictionary<Vector2Int, int>());
            repeated.AddCorridorSurface(probeCell, upperLevel);
            repeated.AddCorridorSurface(probeCell, upperLevel);
            fixture.sameLevelClaimIsIdempotent =
                repeated.IsSingleLayer && repeated.Count == 1;

            // ---- the rendered crossing --------------------------------------
            // A room at level 0, a layer-bound corridor crossing it at level 4,
            // and a return stair, so the whole thing is one walkable component.
            var surfaces = new SurfaceField(new Dictionary<Vector2Int, int>());

            void Ground(Vector2Int cell, int level)
            {
                if (!surfaces.TrySetFloorLevel(cell, level, out string reason))
                {
                    throw new InvalidOperationException($"D2 fixture ground write failed: {reason}");
                }
            }

            var crossedRoom = new List<Vector2Int>();
            for (int x = -2; x <= 2; x++)
            {
                for (int y = -2; y <= 2; y++)
                {
                    var cell = new Vector2Int(x, y);
                    crossedRoom.Add(cell);
                    Ground(cell, 0);
                }
            }

            // The corridor's endpoint rooms, at the corridor's own elevation.
            for (int x = -1; x <= 1; x++)
            {
                for (int y = 6; y <= 8; y++)
                {
                    Ground(new Vector2Int(x, y), upperLevel);
                    Ground(new Vector2Int(x, -y), upperLevel);
                }
            }

            // The link and the return stair, climbing beside the room.
            Ground(new Vector2Int(-3, -2), 0);
            for (int step = 0; step <= upperLevel; step++)
            {
                Ground(new Vector2Int(-4, -2 + step), step);
            }

            // The terrace joining the stair top to the corridor.
            for (int x = -4; x <= -1; x++)
            {
                Ground(new Vector2Int(x, 3), upperLevel);
            }

            // THE PRODUCER, on real geometry: every cell of one layer-bound
            // corridor, written the way `TryResolveConnectionTransition` writes
            // them. Five of these land over the crossed room and suspend; the
            // rest are the corridor's own ground.
            var corridorCells = new List<Vector2Int>();
            for (int y = -5; y <= 5; y++)
            {
                var cell = new Vector2Int(0, y);
                corridorCells.Add(cell);
                surfaces.AddCorridorSurface(cell, upperLevel);
            }

            foreach (Vector2Int cell in corridorCells)
            {
                if (surfaces.IsGroundBacked(cell, upperLevel))
                {
                    fixture.corridorSurfacesOnTheGround++;
                }
                else
                {
                    fixture.corridorSurfacesSuspended++;
                }
            }

            var transitions = new List<ElevationEdgeModel.TransitionEdge>();
            string seamStairPrefabPath = ResolveSeamStairPrefabPath();
            for (int step = 0; step < upperLevel; step++)
            {
                transitions.Add(new ElevationEdgeModel.TransitionEdge(
                    new Vector2Int(-4, -1 + step),
                    step + 1,
                    new Vector2Int(-4, -2 + step),
                    step,
                    seamStairPrefabPath,
                    SeamStairPlacementClass));
            }

            var levels = new Dictionary<Vector2Int, int>(surfaces.ColumnFloors());
            List<ElevationEdgeModel.StackedSurface> stacked =
                new List<ElevationEdgeModel.StackedSurface>(surfaces.StackedSurfaces());
            var noOpenEdges = new List<ElevationEdgeModel.OpenFloorEdge>();
            DeriveExpectedStackedRims(
                levels,
                stacked,
                noOpenEdges,
                upperLevel,
                out int expectedBareRims,
                out int expectedRailedRims);

            GameObject root = ElevationEdgeModel.BuildLevelField(
                Vector3.zero,
                levels,
                stacked,
                transitions,
                null,
                noOpenEdges,
                null,
                null,
                ElevationEdgeModel.TrapPlacementSettings.Disabled,
                "Stacked Corridor Fixture",
                out ElevationEdgeModel.BuildReport buildReport,
                out _);
            Physics.SyncTransforms();

            float levelHeight = buildReport.levelHeight;
            Collider[] colliders = SolidColliders(root);
            var underCatwalkCell = new Vector2Int(0, 0);
            Vector3 underCatwalk = CellCenterWorld(underCatwalkCell);

            // A player standing in the room under the catwalk stands on the room
            // floor, and the catwalk over them is real collidable geometry
            // rather than a one-sided plane. §7.2's two questions.
            fixture.roomHeadroomOpenUnderCatwalk = !colliders.Any(collider =>
                ColliderSpansCellAtHeight(collider, underCatwalk, 1f));
            fixture.catwalkSoffitPresent = colliders.Any(collider =>
                ColliderSpansCellAtHeight(
                    collider,
                    underCatwalk,
                    upperLevel * levelHeight - 0.25f));

            bool portGraphBuilt = TryBuildFloorStairPortGraph(
                surfaces,
                transitions,
                out FloorStairPortGraph portGraph,
                out _);
            string portGraphReachability = "port graph did not build";
            bool portGraphConnected =
                portGraphBuilt && portGraph.IsFallFreeConnected(out portGraphReachability);

            fixture.levels = levels;
            fixture.stacked = stacked;
            fixture.transitions = transitions;
            fixture.root = root;
            fixture.buildReport = buildReport;
            fixture.crossedRoomCells = crossedRoom.Count;
            fixture.expectedStackedSurfaces = fixture.corridorSurfacesSuspended;
            fixture.expectedBareRims = expectedBareRims;
            fixture.expectedRailedRims = expectedRailedRims;
            fixture.reportedStackedSurfaces = buildReport.stackedSurfaces;
            fixture.reportedBareRims = buildReport.stackedBareRims;
            fixture.reportedRailedRims = buildReport.stackedRailedRims;
            fixture.portGraphBuilt = portGraphBuilt;
            fixture.portGraphFallFreeConnected = portGraphConnected;
            fixture.portGraphNodes = portGraphBuilt ? portGraph.NodeCount : 0;
            // Every surface, plus the two port nodes each transition adds. No
            // transition here carries a footprint, so no column is consumed.
            fixture.expectedPortGraphNodes = surfaces.Count + transitions.Count * 2;
            fixture.portGraphReachability = portGraphReachability;
            return fixture;
        }

        /// <summary>
        /// A column's whole content as one comparable string — levels and kinds,
        /// floor first.
        /// </summary>
        private static string DescribeColumn(SurfaceField surfaces, Vector2Int cell)
        {
            return string.Join(
                ",",
                surfaces.LevelsAt(cell).Select(level => $"L{level}:{surfaces.KindAt(cell, level)}"));
        }

        /// <summary>
        /// Is there solid geometry over this cell centre at this height?
        /// </summary>
        private static bool ColliderSpansCellAtHeight(Collider collider, Vector3 cellCenter, float height)
        {
            Bounds bounds = collider.bounds;
            return bounds.min.x <= cellCenter.x && cellCenter.x <= bounds.max.x &&
                bounds.min.z <= cellCenter.z && cellCenter.z <= bounds.max.z &&
                bounds.min.y <= height && height <= bounds.max.y;
        }
    }
}
