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
            AddAerialBridges(
                layout,
                levels,
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
            bool positiveHeadroom = prisms.TryValidateSurfaceHeadroom(levels, out _);
            var negativeLevels = new Dictionary<Vector2Int, int>(levels)
            {
                [stackedCells[0]] = deckLevel - 2
            };
            bool negativeRejected = !prisms.TryValidateSurfaceHeadroom(negativeLevels, out _);
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
                new Dictionary<Vector2Int, int> { [cell] = 0 },
                out _);
            oneShortRejected = !ledger.TryValidateSurfaceHeadroom(
                new Dictionary<Vector2Int, int> { [cell] = 1 },
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
