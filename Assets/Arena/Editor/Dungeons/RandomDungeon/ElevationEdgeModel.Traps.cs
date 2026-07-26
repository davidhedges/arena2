using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Arena.Interaction;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    /// <summary>
    /// Additive trap placement pass.
    ///
    /// Determinism contract: every draw comes from a subject-keyed stream
    /// (<c>traps:x:z</c>), so the pass reads the finished plan and cannot
    /// perturb any decision that produced it. Nothing here writes to a shared
    /// <see cref="System.Random"/>, mutates the level plan, or emits a collider —
    /// the pass only adds a `Traps` root beside the geometry roots.
    /// </summary>
    public static partial class ElevationEdgeModel
    {
        private const string TrapPrefabRoot =
            "Assets/Arena/Content/Prefabs/Dungeons/ToonDesertedTemples/Traps";
        private const string TrapProfileRoot = "Assets/Arena/Content/Settings/Traps";
        private const string TrapWorldDefinitionKey = "RANDOM_DUNGEON";
        private const string TrapRandomVersion = "traps-v1";

        /// <summary>
        /// Render-stage trap settings. Deliberately NOT part of
        /// <c>DungeonGenerationSettings</c>: that struct is reflected into the
        /// per-seed settings digest, and trap density is a render decision that
        /// must not move a plan hash.
        /// </summary>
        public sealed class TrapPlacementSettings
        {
            public static readonly TrapPlacementSettings Disabled = new(0, false, 25, 3, 1, 3, 5, 2, 2, 1);

            public TrapPlacementSettings(
                int seed,
                bool enabled,
                int floorCellsPerTrap,
                int corridorWeight,
                int roomWeight,
                int spawnClearanceCells,
                int spikesWeight,
                int sawPostWeight,
                int sawSweepWeight,
                int sawArmWeight)
            {
                this.seed = seed;
                this.enabled = enabled;
                this.floorCellsPerTrap = Mathf.Max(1, floorCellsPerTrap);
                this.corridorWeight = Mathf.Max(1, corridorWeight);
                this.roomWeight = Mathf.Max(1, roomWeight);
                this.spawnClearanceCells = Mathf.Max(0, spawnClearanceCells);
                this.spikesWeight = Mathf.Max(0, spikesWeight);
                this.sawPostWeight = Mathf.Max(0, sawPostWeight);
                this.sawSweepWeight = Mathf.Max(0, sawSweepWeight);
                this.sawArmWeight = Mathf.Max(0, sawArmWeight);
            }

            public readonly int seed;
            public readonly bool enabled;
            public readonly int floorCellsPerTrap;
            public readonly int corridorWeight;
            public readonly int roomWeight;
            public readonly int spawnClearanceCells;
            public readonly int spikesWeight;
            public readonly int sawPostWeight;
            public readonly int sawSweepWeight;
            public readonly int sawArmWeight;
        }

        private enum TrapKind
        {
            Spikes,
            SawPost,
            SawSweep,
            SawArm,
        }

        private readonly struct TrapKindSpec
        {
            public TrapKindSpec(TrapKind kind, string idToken, string prefabName, string profileAsset, string profileId)
            {
                this.kind = kind;
                this.idToken = idToken;
                this.prefabPath = $"{TrapPrefabRoot}/{prefabName}.prefab";
                this.profilePath = $"{TrapProfileRoot}/{profileAsset}.asset";
                this.profileId = profileId;
            }

            public readonly TrapKind kind;
            public readonly string idToken;
            public readonly string prefabPath;
            public readonly string profilePath;
            public readonly string profileId;
        }

        private static readonly TrapKindSpec[] TrapKindSpecs =
        {
            new(TrapKind.Spikes, "SPIKES", "TRAP_SPIKES_Arena", "TrapSpikes", "TRAP_SPIKES"),
            new(TrapKind.SawPost, "SAW_POST", "TRAP_SAW_POST_Arena", "TrapSawPost", "TRAP_SAW_POST"),
            new(TrapKind.SawSweep, "SAW_SWEEP", "TRAP_SAW_SWEEP_Arena", "TrapSawSweep", "TRAP_SAW_SWEEP"),
            new(TrapKind.SawArm, "SAW_ARM", "TRAP_SAW_ARM_Arena", "TrapSawArm", "TRAP_SAW_ARM"),
        };

        private readonly struct TrapPlacement
        {
            public TrapPlacement(TrapKindSpec spec, Vector2Int anchor, int level, Vector3 position, float yaw, Vector2Int[] cells)
            {
                this.spec = spec;
                this.anchor = anchor;
                this.level = level;
                this.position = position;
                this.yaw = yaw;
                this.cells = cells;
            }

            public readonly TrapKindSpec spec;
            public readonly Vector2Int anchor;
            public readonly int level;
            public readonly Vector3 position;
            public readonly float yaw;
            public readonly Vector2Int[] cells;
        }

        private sealed class TrapPlacementContext
        {
            public IReadOnlyDictionary<Vector2Int, int> levels;
            public HashSet<Vector2Int> excluded;
            public HashSet<Vector2Int> corridorCells;
            public HashSet<Vector2Int> taken;
        }

        private static int PlaceTraps(
            TrapPlacementSettings settings,
            Transform trapsRoot,
            Vector3 origin,
            float levelHeight,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyCollection<Vector2Int> reservedCells,
            StairReservationSet stairReservations,
            IReadOnlyDictionary<Vector2Int, int> aerialDeckCellLevels,
            IReadOnlyList<TransitionEdge> transitions,
            RoomBoundaryContext roomBoundaryContext,
            GatewaySocketPlan gatewaySocketPlan,
            IReadOnlyCollection<Vector2Int> promontoryCells,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            if (settings == null || !settings.enabled || levels == null || levels.Count == 0)
            {
                return 0;
            }
            if (!TrapVariantAssetsExist())
            {
                // Loud, but not fatal: a rebuild on a checkout that has never run
                // the trap prefab builder still produces a dungeon, and the audit
                // reports zero traps rather than the build throwing.
                Debug.LogWarning(
                    $"[TRAPS] Skipped trap placement: the Arena trap variants under {TrapPrefabRoot} "
                    + "are missing. Run Arena/Dungeons/Build Trap Variants (or ops/rebuild-dungeon-traps.sh).");
                return 0;
            }

            HashSet<Vector2Int> excluded = BuildTrapExclusionSet(
                levels,
                reservedCells,
                stairReservations,
                aerialDeckCellLevels,
                transitions,
                roomBoundaryContext,
                gatewaySocketPlan,
                promontoryCells,
                origin,
                levelHeight,
                settings.spawnClearanceCells);

            var context = new TrapPlacementContext
            {
                levels = levels,
                excluded = excluded,
                corridorCells = BuildCorridorCellSet(levels, roomBoundaryContext),
                taken = new HashSet<Vector2Int>(),
            };

            List<Vector2Int> candidates = levels.Keys
                .Where(cell => !excluded.Contains(cell))
                .OrderBy(cell => cell.x)
                .ThenBy(cell => cell.y)
                .ToList();
            if (candidates.Count == 0)
            {
                return 0;
            }

            int target = levels.Count / settings.floorCellsPerTrap;
            if (target <= 0)
            {
                return 0;
            }

            // Score is roll/weight, so a corridor cell with three times the
            // weight is three times as likely to sort into the leading slice.
            List<(Vector2Int cell, double score)> ordered = candidates
                .Select(cell =>
                {
                    int weight = context.corridorCells.Contains(cell)
                        ? settings.corridorWeight
                        : settings.roomWeight;
                    double roll = TrapRandom(settings.seed, "traps", CellSubject(cell)).NextDouble();
                    return (cell, score: roll / weight);
                })
                .OrderBy(entry => entry.score)
                .ThenBy(entry => entry.cell.x)
                .ThenBy(entry => entry.cell.y)
                .ToList();

            int placed = 0;
            foreach ((Vector2Int cell, double _) in ordered)
            {
                if (placed >= target)
                {
                    break;
                }
                if (context.taken.Contains(cell))
                {
                    continue;
                }
                if (!TryResolveTrapPlacement(settings, context, cell, origin, levelHeight, out TrapPlacement placement))
                {
                    continue;
                }

                InstantiateTrap(placement, trapsRoot, ref bounds, ref hasBounds);
                foreach (Vector2Int footprintCell in placement.cells)
                {
                    context.taken.Add(footprintCell);
                }
                placed++;
            }

            return placed;
        }

        private static bool TryResolveTrapPlacement(
            TrapPlacementSettings settings,
            TrapPlacementContext context,
            Vector2Int cell,
            Vector3 origin,
            float levelHeight,
            out TrapPlacement placement)
        {
            placement = default;
            if (!context.levels.TryGetValue(cell, out int level))
            {
                return false;
            }

            System.Random kindRandom = TrapRandom(settings.seed, "traps-kind", CellSubject(cell));
            foreach (TrapKindSpec spec in WeightedKindOrder(settings, kindRandom))
            {
                if (TryResolveKindPlacement(spec, context, cell, level, origin, levelHeight, out placement))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveKindPlacement(
            TrapKindSpec spec,
            TrapPlacementContext context,
            Vector2Int cell,
            int level,
            Vector3 origin,
            float levelHeight,
            out TrapPlacement placement)
        {
            placement = default;
            Vector3 cellCenter = TrapCellCenter(origin, cell, level, levelHeight);
            switch (spec.kind)
            {
                case TrapKind.Spikes:
                case TrapKind.SawPost:
                    placement = new TrapPlacement(spec, cell, level, cellCenter, 0f, new[] { cell });
                    return true;

                case TrapKind.SawSweep:
                {
                    // The travelling blade spans 6 u along its lane, so it needs
                    // two collinear cells and sits on the edge they share.
                    foreach (int direction in new[] { Direction.North, Direction.East })
                    {
                        Vector2Int partner = Neighbor(cell, direction);
                        if (!TrapCellIsFree(context, partner, level))
                        {
                            continue;
                        }

                        Vector3 partnerCenter = TrapCellCenter(origin, partner, level, levelHeight);
                        placement = new TrapPlacement(
                            spec,
                            cell,
                            level,
                            (cellCenter + partnerCenter) * 0.5f,
                            direction == Direction.North ? 0f : 90f,
                            new[] { cell, partner });
                        return true;
                    }
                    return false;
                }

                case TrapKind.SawArm:
                {
                    // The wall arm sweeps a 3 u horizontal radius into the room,
                    // so it needs a proven wall on one side and a 2x3 clear block
                    // in front of it.
                    foreach (int wallDirection in new[] { Direction.North, Direction.East, Direction.South, Direction.West })
                    {
                        if (!TrapWallFaces(context, cell, level, wallDirection))
                        {
                            continue;
                        }

                        Vector2Int inward = Neighbor(cell, Opposite(wallDirection));
                        Vector2Int left = Neighbor(cell, RotateDirectionClockwise(wallDirection));
                        Vector2Int right = Neighbor(cell, Opposite(RotateDirectionClockwise(wallDirection)));
                        Vector2Int inwardLeft = Neighbor(inward, RotateDirectionClockwise(wallDirection));
                        Vector2Int inwardRight = Neighbor(inward, Opposite(RotateDirectionClockwise(wallDirection)));
                        Vector2Int[] footprint = { cell, inward, left, right, inwardLeft, inwardRight };
                        if (footprint.Skip(1).Any(candidate => !TrapCellIsFree(context, candidate, level)))
                        {
                            continue;
                        }

                        placement = new TrapPlacement(
                            spec,
                            cell,
                            level,
                            cellCenter,
                            TrapYawForLocalXDirection(wallDirection),
                            footprint);
                        return true;
                    }
                    return false;
                }

                default:
                    return false;
            }
        }

        private static void InstantiateTrap(
            TrapPlacement placement,
            Transform trapsRoot,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            string definitionId = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:TRAP:{1}:{2}:{3}:{4}",
                TrapWorldDefinitionKey,
                placement.spec.idToken,
                placement.anchor.x,
                placement.anchor.y,
                placement.level);
            string objectName = string.Format(
                CultureInfo.InvariantCulture,
                "trap_{0}_{1}_{2}_level_{3}",
                placement.spec.idToken.ToLowerInvariant(),
                placement.anchor.x,
                placement.anchor.y,
                placement.level);

            GameObject instance = InstantiatePrefab(
                placement.spec.prefabPath,
                objectName,
                trapsRoot,
                placement.position,
                placement.yaw);

            var authoring = instance.GetComponent<TrapAuthoring>();
            if (authoring == null)
            {
                throw new InvalidOperationException(
                    $"Trap prefab '{placement.spec.prefabPath}' has no TrapAuthoring.");
            }
            var profile = AssetDatabase.LoadAssetAtPath<TrapProfile>(placement.spec.profilePath);
            if (profile == null)
            {
                throw new InvalidOperationException(
                    $"Missing trap profile asset '{placement.spec.profilePath}'.");
            }

            authoring.Configure(
                definitionId,
                TrapWorldDefinitionKey,
                templateOnly: false,
                productionEnabled: true,
                definitionVersion: 1,
                footprintCells: placement.cells.Length,
                profile: profile);
            EditorUtility.SetDirty(authoring);

            // A trap that reached the scene with a collider would leak into the
            // immutable collision bake, which the collision contract forbids.
            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Trap '{definitionId}' carries {colliders.Length} collider(s); traps must never contribute collision.");
            }

            EncapsulateInstance(instance, ref bounds, ref hasBounds);
        }

        private static bool TrapVariantAssetsExist()
        {
            foreach (TrapKindSpec spec in TrapKindSpecs)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(spec.prefabPath) == null
                    || AssetDatabase.LoadAssetAtPath<TrapProfile>(spec.profilePath) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private static HashSet<Vector2Int> BuildTrapExclusionSet(
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyCollection<Vector2Int> reservedCells,
            StairReservationSet stairReservations,
            IReadOnlyDictionary<Vector2Int, int> aerialDeckCellLevels,
            IReadOnlyList<TransitionEdge> transitions,
            RoomBoundaryContext roomBoundaryContext,
            GatewaySocketPlan gatewaySocketPlan,
            IReadOnlyCollection<Vector2Int> promontoryCells,
            Vector3 origin,
            float levelHeight,
            int spawnClearanceCells)
        {
            var excluded = new HashSet<Vector2Int>();
            if (reservedCells != null)
            {
                excluded.UnionWith(reservedCells);
            }
            excluded.UnionWith(stairReservations.floorBlockedCells);
            excluded.UnionWith(stairReservations.bridgeFloorBlockedCells);
            if (aerialDeckCellLevels != null)
            {
                excluded.UnionWith(aerialDeckCellLevels.Keys);
            }
            if (promontoryCells != null)
            {
                excluded.UnionWith(promontoryCells);
            }

            foreach (TransitionEdge transition in transitions ?? Array.Empty<TransitionEdge>())
            {
                excluded.Add(transition.firstCell);
                excluded.Add(transition.secondCell);
                AddCells(excluded, transition.footprintCells);
                AddCells(excluded, transition.lowerLandingCells);
                AddCells(excluded, transition.upperLandingCells);
                excluded.Add(transition.lowerLandingCell);
                excluded.Add(transition.upperLandingCell);
            }

            if (roomBoundaryContext != null)
            {
                foreach (DoorwayEdge doorway in roomBoundaryContext.doorwayEdges ?? (IReadOnlyList<DoorwayEdge>)Array.Empty<DoorwayEdge>())
                {
                    excluded.Add(doorway.firstCell);
                    excluded.Add(doorway.secondCell);
                }
            }

            // A gateway is a chokepoint with nowhere to dodge, so its cells and
            // everything touching them stay bare.
            if (gatewaySocketPlan != null)
            {
                foreach (GatewaySocket socket in gatewaySocketPlan.sockets)
                {
                    var socketCell = new Vector2Int(socket.edge.x, socket.edge.z);
                    ExcludeWithNeighbors(excluded, socketCell);
                    ExcludeWithNeighbors(excluded, Neighbor(socketCell, socket.edge.direction));
                }
            }

            if (TryFindTrapSpawnCell(levels, origin, levelHeight, out Vector2Int spawnCell))
            {
                for (int dx = -spawnClearanceCells; dx <= spawnClearanceCells; dx++)
                {
                    for (int dz = -spawnClearanceCells; dz <= spawnClearanceCells; dz++)
                    {
                        excluded.Add(new Vector2Int(spawnCell.x + dx, spawnCell.y + dz));
                    }
                }

                // The arrival room is where a player materialises with no warning
                // and no context; it never carries a trap.
                if (roomBoundaryContext?.cellRoomIds != null
                    && roomBoundaryContext.cellRoomIds.TryGetValue(spawnCell, out int arrivalRoomId))
                {
                    foreach (KeyValuePair<Vector2Int, int> entry in roomBoundaryContext.cellRoomIds)
                    {
                        if (entry.Value == arrivalRoomId)
                        {
                            excluded.Add(entry.Key);
                        }
                    }
                }
            }

            return excluded;
        }

        /// <summary>
        /// Mirrors <c>RandomDungeonSceneBuilder.CenterDungeonSpawn</c>: the floor
        /// nearest the pre-shift origin becomes the shared spawn, so that is the
        /// cell trap placement must keep clear.
        /// </summary>
        private static bool TryFindTrapSpawnCell(
            IReadOnlyDictionary<Vector2Int, int> levels,
            Vector3 origin,
            float levelHeight,
            out Vector2Int spawnCell)
        {
            spawnCell = default;
            bool found = false;
            double bestDistance = double.MaxValue;
            float bestHeight = float.MaxValue;
            foreach (KeyValuePair<Vector2Int, int> entry in levels)
            {
                Vector3 center = TrapCellCenter(origin, entry.Key, entry.Value, levelHeight);
                double distance = (double)center.x * center.x + (double)center.z * center.z;
                if (!found
                    || distance < bestDistance
                    || (Math.Abs(distance - bestDistance) < 0.0001 && center.y < bestHeight))
                {
                    found = true;
                    bestDistance = distance;
                    bestHeight = center.y;
                    spawnCell = entry.Key;
                }
            }
            return found;
        }

        private static HashSet<Vector2Int> BuildCorridorCellSet(
            IReadOnlyDictionary<Vector2Int, int> levels,
            RoomBoundaryContext roomBoundaryContext)
        {
            var corridors = new HashSet<Vector2Int>();
            IReadOnlyDictionary<Vector2Int, int> roomIds = roomBoundaryContext?.cellRoomIds;
            foreach (Vector2Int cell in levels.Keys)
            {
                if (roomIds == null || !roomIds.ContainsKey(cell))
                {
                    corridors.Add(cell);
                }
            }
            return corridors;
        }

        private static bool TrapCellIsFree(TrapPlacementContext context, Vector2Int cell, int level)
        {
            return context.levels.TryGetValue(cell, out int cellLevel)
                && cellLevel == level
                && !context.excluded.Contains(cell)
                && !context.taken.Contains(cell);
        }

        /// <summary>A missing or higher neighbour means a real wall face on that side.</summary>
        private static bool TrapWallFaces(TrapPlacementContext context, Vector2Int cell, int level, int direction)
        {
            Vector2Int neighbor = Neighbor(cell, direction);
            return !context.levels.TryGetValue(neighbor, out int neighborLevel) || neighborLevel > level;
        }

        private static Vector3 TrapCellCenter(Vector3 origin, Vector2Int cell, int level, float levelHeight)
        {
            Vector3 cellMin = CellMin(origin, cell.x, cell.y, level * levelHeight);
            return cellMin + new Vector3(CellSize * 0.5f, 0f, CellSize * 0.5f);
        }

        /// <summary>Yaw that points the trap's local +X at <paramref name="direction"/>.</summary>
        private static float TrapYawForLocalXDirection(int direction)
        {
            switch (direction)
            {
                case Direction.East:
                    return 0f;
                case Direction.South:
                    return 90f;
                case Direction.West:
                    return 180f;
                case Direction.North:
                    return 270f;
                default:
                    throw new InvalidOperationException($"Unknown trap wall direction {direction}.");
            }
        }

        private static int RotateDirectionClockwise(int direction)
        {
            switch (direction)
            {
                case Direction.North:
                    return Direction.East;
                case Direction.East:
                    return Direction.South;
                case Direction.South:
                    return Direction.West;
                case Direction.West:
                    return Direction.North;
                default:
                    throw new InvalidOperationException($"Unknown direction {direction}.");
            }
        }

        private static int Opposite(int direction)
        {
            return RotateDirectionClockwise(RotateDirectionClockwise(direction));
        }

        private static void ExcludeWithNeighbors(HashSet<Vector2Int> excluded, Vector2Int cell)
        {
            excluded.Add(cell);
            excluded.Add(Neighbor(cell, Direction.North));
            excluded.Add(Neighbor(cell, Direction.East));
            excluded.Add(Neighbor(cell, Direction.South));
            excluded.Add(Neighbor(cell, Direction.West));
        }

        private static void AddCells(HashSet<Vector2Int> excluded, Vector2Int[] cells)
        {
            if (cells == null)
            {
                return;
            }
            foreach (Vector2Int cell in cells)
            {
                excluded.Add(cell);
            }
        }

        private static IEnumerable<TrapKindSpec> WeightedKindOrder(
            TrapPlacementSettings settings,
            System.Random random)
        {
            var remaining = new List<(TrapKindSpec spec, int weight)>();
            foreach (TrapKindSpec spec in TrapKindSpecs)
            {
                int weight = TrapKindWeight(settings, spec.kind);
                if (weight > 0)
                {
                    remaining.Add((spec, weight));
                }
            }

            while (remaining.Count > 0)
            {
                int total = remaining.Sum(entry => entry.weight);
                int roll = random.Next(total);
                int index = 0;
                for (; index < remaining.Count - 1; index++)
                {
                    roll -= remaining[index].weight;
                    if (roll < 0)
                    {
                        break;
                    }
                }

                yield return remaining[index].spec;
                remaining.RemoveAt(index);
            }
        }

        private static int TrapKindWeight(TrapPlacementSettings settings, TrapKind kind)
        {
            switch (kind)
            {
                case TrapKind.Spikes:
                    return settings.spikesWeight;
                case TrapKind.SawPost:
                    return settings.sawPostWeight;
                case TrapKind.SawSweep:
                    return settings.sawSweepWeight;
                case TrapKind.SawArm:
                    return settings.sawArmWeight;
                default:
                    return 0;
            }
        }

        private static string CellSubject(Vector2Int cell)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1}", cell.x, cell.y);
        }

        /// <summary>
        /// Subject-keyed stream. Two traps never share a stream and no other
        /// generator decision draws from one, so enabling the pass cannot move
        /// any existing output.
        /// </summary>
        private static System.Random TrapRandom(int seed, string purpose, string subject)
        {
            unchecked
            {
                uint hash = 2166136261u;
                MixTrapSeedHash(ref hash, seed.ToString(CultureInfo.InvariantCulture));
                MixTrapSeedHash(ref hash, TrapRandomVersion);
                MixTrapSeedHash(ref hash, purpose ?? string.Empty);
                MixTrapSeedHash(ref hash, subject ?? string.Empty);
                return new System.Random((int)hash);
            }
        }

        private static void MixTrapSeedHash(ref uint hash, string value)
        {
            unchecked
            {
                foreach (char character in value)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                hash ^= 0xffu;
                hash *= 16777619u;
            }
        }
    }
}
