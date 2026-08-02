using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase E of the layered-topology design: a deterministic, derived surface
    // graph exported beside the shared collision payload. This is capability
    // data only; NPC route choice remains deliberately out of scope.
    internal sealed partial class DungeonLabGenerator
    {
        private const float NavigationCellSize = 4f;
        private const float NavigationLevelHeight = 1f;
        private const float ServerBoxStepUpLevels = 0.35f;
        private const float ServerMeshSnapUpLevels = 1.2f;
        private const float ServerMeshMinimumNormalY = 0.35f;
        private const float ServerCollisionEpsilon = 0.0001f;
        private const float NavigationCollisionEpsilon = 0.06f;
        private const float NavigationPlayerRadius = 0.45f;
        private const float NavigationPlayerHeight = 1.8f;
        private const string NavigationCollisionLayerName = "GameplayCollision";
        private const string RelativeServerWorldDataDirectory = "server/src/world_data";
        private const string RelativeBundledWorldDataDirectory =
            "Assets/Arena/Resources/SharedData/Worlds";

        [Serializable]
        private sealed class NavigationSurfaceArtifact
        {
            public int version = 1;
            public int seed;
            public string topology_id = string.Empty;
            public int ceiling_levels;
            public float cell_size = NavigationCellSize;
            public float level_height = NavigationLevelHeight;
            public int source_transition_count;
            public NavigationSurfaceNode[] nodes = Array.Empty<NavigationSurfaceNode>();
            public NavigationSurfaceEdge[] edges = Array.Empty<NavigationSurfaceEdge>();
            public NavigationSurfaceValidation validation = new NavigationSurfaceValidation();
        }

        [Serializable]
        private sealed class NavigationSurfaceNode
        {
            public string id = string.Empty;
            public int[] cell = Array.Empty<int>();
            public int level;
            public string owner = string.Empty;
            public string surface_id = string.Empty;
            public string kind = string.Empty;
            public float planned_world_y;
            public float collision_y_adjustment;
            public float[] world_center = Array.Empty<float>();
        }

        [Serializable]
        private sealed class NavigationSurfaceEdge
        {
            public string id = string.Empty;
            public string from = string.Empty;
            public string to = string.Empty;
            public string kind = string.Empty;
            public bool directed;
            public int rise_levels;
            public float cost;
            public int source_transition_index = -1;
        }

        [Serializable]
        private sealed class NavigationSurfaceValidation
        {
            public bool graph_valid;
            public bool fall_free_connected;
            public bool collision_agreement;
            public int checked_nodes;
            public int checked_edges;
            public int collision_checked_walk_edges;
            public int witnessed_transition_edges;
            public int validated_fall_edges;
            public int omitted_obstructed_surfaces;
            public int omitted_collision_gap_walk_edges;
            public int omitted_unwitnessed_component_surfaces;
            public float max_abs_collision_y_adjustment;
        }

        private sealed class CapturedNavigationSurfacePlan
        {
            public readonly int seed;
            public readonly string topologyId;
            public readonly TieredLevelPlan plan;
            public readonly ElevationEdgeModel.RoomBoundaryContext boundaryContext;
            public readonly Vector3 localOrigin;
            public readonly GameObject root;

            public CapturedNavigationSurfacePlan(
                int seed,
                string topologyId,
                TieredLevelPlan plan,
                ElevationEdgeModel.RoomBoundaryContext boundaryContext,
                Vector3 localOrigin,
                GameObject root)
            {
                this.seed = seed;
                this.topologyId = topologyId ?? string.Empty;
                this.plan = plan;
                this.boundaryContext = boundaryContext;
                this.localOrigin = localOrigin;
                this.root = root;
            }
        }

        private readonly struct NavigationEdgeDraft
        {
            public readonly SurfaceKey from;
            public readonly SurfaceKey to;
            public readonly string kind;
            public readonly bool directed;
            public readonly int riseLevels;
            public readonly float cost;
            public readonly int transitionIndex;

            public NavigationEdgeDraft(
                SurfaceKey from,
                SurfaceKey to,
                string kind,
                bool directed,
                int riseLevels,
                float cost,
                int transitionIndex = -1)
            {
                this.from = from;
                this.to = to;
                this.kind = kind ?? string.Empty;
                this.directed = directed;
                this.riseLevels = riseLevels;
                this.cost = cost;
                this.transitionIndex = transitionIndex;
            }
        }

        private readonly struct ServerCollisionBucket : IEquatable<ServerCollisionBucket>
        {
            public readonly int x;
            public readonly int z;

            public ServerCollisionBucket(int x, int z)
            {
                this.x = x;
                this.z = z;
            }

            public bool Equals(ServerCollisionBucket other)
            {
                return x == other.x && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is ServerCollisionBucket other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (x * 397) ^ z;
                }
            }
        }

        private readonly struct CachedServerCollisionBox
        {
            public readonly Bounds bounds;
            public readonly Matrix4x4 worldToLocal;
            public readonly Vector3 center;
            public readonly Vector3 half;
            public readonly float top;
            public readonly bool tilted;

            public CachedServerCollisionBox(BoxCollider collider)
            {
                bounds = collider.bounds;
                worldToLocal = collider.transform.worldToLocalMatrix;
                center = collider.center;
                half = collider.size * 0.5f;
                top = bounds.max.y;
                Vector3 euler = collider.transform.rotation.eulerAngles;
                tilted = Mathf.Abs(Mathf.DeltaAngle(0f, euler.x)) > 0.01f ||
                    Mathf.Abs(Mathf.DeltaAngle(0f, euler.z)) > 0.01f;
            }

            public bool ContainsXZ(Vector3 point)
            {
                if (tilted)
                {
                    return point.x >= bounds.min.x - NavigationCollisionEpsilon &&
                        point.x <= bounds.max.x + NavigationCollisionEpsilon &&
                        point.z >= bounds.min.z - NavigationCollisionEpsilon &&
                        point.z <= bounds.max.z + NavigationCollisionEpsilon;
                }

                Vector3 local = worldToLocal.MultiplyPoint3x4(point);
                Vector3 delta = local - center;
                return Mathf.Abs(delta.x) <= half.x + NavigationCollisionEpsilon &&
                    Mathf.Abs(delta.z) <= half.z + NavigationCollisionEpsilon;
            }
        }

        private readonly struct CachedServerCollisionTriangle
        {
            public readonly Vector3 a;
            public readonly Vector3 b;
            public readonly Vector3 c;
            public readonly float minX;
            public readonly float maxX;
            public readonly float minZ;
            public readonly float maxZ;

            public CachedServerCollisionTriangle(Vector3 a, Vector3 b, Vector3 c)
            {
                this.a = a;
                this.b = b;
                this.c = c;
                minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
                maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            }
        }

        private sealed class CachedMeshCollisionBuffers
        {
            public readonly Vector3[] vertices;
            public readonly int[] triangles;

            public CachedMeshCollisionBuffers(Mesh mesh)
            {
                // Unity returns copies from both properties. Read each distinct
                // mesh once for the whole export instead of once per sample.
                vertices = mesh.vertices;
                triangles = mesh.triangles;
            }
        }

        /// <summary>
        /// Immutable, per-export view of the collision geometry using the same
        /// box and mesh rules as the authoritative server payload. Validation
        /// asks tens of thousands of point queries; all Unity mesh reads and
        /// local-to-world triangle transforms therefore happen once here.
        /// </summary>
        private sealed class ServerCollisionSampler
        {
            private readonly int collisionLayer;
            private readonly HashSet<Collider> sourceColliders;
            private readonly List<CachedServerCollisionBox> boxes =
                new List<CachedServerCollisionBox>();
            private readonly List<CachedServerCollisionTriangle> triangles =
                new List<CachedServerCollisionTriangle>();
            private readonly Dictionary<ServerCollisionBucket, List<int>> boxBuckets =
                new Dictionary<ServerCollisionBucket, List<int>>();
            private readonly Dictionary<ServerCollisionBucket, List<int>> triangleBuckets =
                new Dictionary<ServerCollisionBucket, List<int>>();

            public int MeshBufferReadCount { get; private set; }
            public int QueryCount { get; private set; }
            public int BoxCandidateCheckCount { get; private set; }
            public int TriangleCandidateCheckCount { get; private set; }
            public int BoxPrimitiveCount => boxes.Count;
            public int TrianglePrimitiveCount => triangles.Count;
            public int CollisionLayer => collisionLayer;

            private ServerCollisionSampler(
                int collisionLayer,
                IReadOnlyList<Collider> colliders)
            {
                this.collisionLayer = collisionLayer;
                sourceColliders = new HashSet<Collider>();
                for (int index = 0; index < colliders.Count; index++)
                {
                    Collider collider = colliders[index];
                    if (collider != null)
                    {
                        sourceColliders.Add(collider);
                    }
                }
            }

            public static ServerCollisionSampler Build(
                IReadOnlyList<Collider> colliders,
                int collisionLayer)
            {
                var sampler = new ServerCollisionSampler(collisionLayer, colliders);
                var meshBuffers = new Dictionary<Mesh, CachedMeshCollisionBuffers>();
                foreach (Collider collider in colliders)
                {
                    if (collider == null ||
                        !collider.enabled ||
                        collider.isTrigger ||
                        collider.gameObject.layer != collisionLayer)
                    {
                        continue;
                    }

                    if (collider is BoxCollider boxCollider)
                    {
                        sampler.AddBox(new CachedServerCollisionBox(boxCollider));
                        continue;
                    }

                    if (!(collider is MeshCollider meshCollider) ||
                        meshCollider.sharedMesh == null ||
                        !meshCollider.sharedMesh.isReadable)
                    {
                        continue;
                    }

                    Mesh mesh = meshCollider.sharedMesh;
                    if (!meshBuffers.TryGetValue(mesh, out CachedMeshCollisionBuffers buffers))
                    {
                        buffers = new CachedMeshCollisionBuffers(mesh);
                        meshBuffers.Add(mesh, buffers);
                        sampler.MeshBufferReadCount++;
                    }

                    Vector3[] worldVertices = new Vector3[buffers.vertices.Length];
                    Matrix4x4 localToWorld = meshCollider.transform.localToWorldMatrix;
                    for (int index = 0; index < buffers.vertices.Length; index++)
                    {
                        worldVertices[index] =
                            localToWorld.MultiplyPoint3x4(buffers.vertices[index]);
                    }

                    int[] meshTriangles = buffers.triangles;
                    for (int index = 0; index + 2 < meshTriangles.Length; index += 3)
                    {
                        Vector3 a = worldVertices[meshTriangles[index]];
                        Vector3 b = worldVertices[meshTriangles[index + 1]];
                        Vector3 c = worldVertices[meshTriangles[index + 2]];
                        Vector3 cross = Vector3.Cross(b - a, c - a);
                        float length = cross.magnitude;
                        if (length <= ServerCollisionEpsilon ||
                            Mathf.Abs(cross.y / length) < ServerMeshMinimumNormalY)
                        {
                            continue;
                        }

                        sampler.AddTriangle(new CachedServerCollisionTriangle(a, b, c));
                    }
                }

                return sampler;
            }

            public bool ContainsCollider(Collider collider)
            {
                return sourceColliders.Contains(collider);
            }

            public void ResetDiagnostics()
            {
                QueryCount = 0;
                BoxCandidateCheckCount = 0;
                TriangleCandidateCheckCount = 0;
            }

            public bool TrySampleSurface(
                Vector3 expected,
                out float sampled,
                out float allowedAdjustment)
            {
                QueryCount++;
                sampled = float.NegativeInfinity;
                allowedAdjustment = 0f;
                bool found = false;
                ServerCollisionBucket bucket = BucketAt(expected.x, expected.z);

                if (boxBuckets.TryGetValue(bucket, out List<int> boxIndices))
                {
                    foreach (int boxIndex in boxIndices)
                    {
                        BoxCandidateCheckCount++;
                        CachedServerCollisionBox box = boxes[boxIndex];
                        if (!box.ContainsXZ(expected) ||
                            box.top > expected.y + ServerBoxStepUpLevels +
                                NavigationCollisionEpsilon)
                        {
                            continue;
                        }

                        if (!found || box.top > sampled)
                        {
                            sampled = box.top;
                            allowedAdjustment = ServerBoxStepUpLevels;
                            found = true;
                        }
                    }
                }

                float ceiling = expected.y + ServerMeshSnapUpLevels;
                if (triangleBuckets.TryGetValue(bucket, out List<int> triangleIndices))
                {
                    foreach (int triangleIndex in triangleIndices)
                    {
                        TriangleCandidateCheckCount++;
                        CachedServerCollisionTriangle triangle = triangles[triangleIndex];
                        if (!TryServerTriangleHeightAtXZ(
                                triangle.a,
                                triangle.b,
                                triangle.c,
                                expected.x,
                                expected.z,
                                out float height) ||
                            height > ceiling + ServerCollisionEpsilon)
                        {
                            continue;
                        }

                        if (!found || height > sampled)
                        {
                            sampled = height;
                            allowedAdjustment = ServerMeshSnapUpLevels;
                            found = true;
                        }
                    }
                }

                return found;
            }

            private void AddBox(CachedServerCollisionBox box)
            {
                int index = boxes.Count;
                boxes.Add(box);
                AddToBuckets(
                    boxBuckets,
                    index,
                    box.bounds.min.x,
                    box.bounds.max.x,
                    box.bounds.min.z,
                    box.bounds.max.z);
            }

            private void AddTriangle(CachedServerCollisionTriangle triangle)
            {
                int index = triangles.Count;
                triangles.Add(triangle);
                AddToBuckets(
                    triangleBuckets,
                    index,
                    triangle.minX,
                    triangle.maxX,
                    triangle.minZ,
                    triangle.maxZ);
            }

            private static void AddToBuckets(
                Dictionary<ServerCollisionBucket, List<int>> buckets,
                int primitiveIndex,
                float minX,
                float maxX,
                float minZ,
                float maxZ)
            {
                int firstX = BucketCoordinate(minX - NavigationCollisionEpsilon);
                int lastX = BucketCoordinate(maxX + NavigationCollisionEpsilon);
                int firstZ = BucketCoordinate(minZ - NavigationCollisionEpsilon);
                int lastZ = BucketCoordinate(maxZ + NavigationCollisionEpsilon);
                for (int x = firstX; x <= lastX; x++)
                {
                    for (int z = firstZ; z <= lastZ; z++)
                    {
                        var key = new ServerCollisionBucket(x, z);
                        if (!buckets.TryGetValue(key, out List<int> indices))
                        {
                            indices = new List<int>();
                            buckets.Add(key, indices);
                        }

                        indices.Add(primitiveIndex);
                    }
                }
            }

            private static ServerCollisionBucket BucketAt(float x, float z)
            {
                return new ServerCollisionBucket(
                    BucketCoordinate(x),
                    BucketCoordinate(z));
            }

            private static int BucketCoordinate(float coordinate)
            {
                return Mathf.FloorToInt(coordinate / NavigationCellSize);
            }
        }

        private static CapturedNavigationSurfacePlan lastNavigationSurfacePlan;

        private static void ResetNavigationSurfaceExport()
        {
            lastNavigationSurfacePlan = null;
        }

        private static void CaptureNavigationSurfacePlan(
            int seed,
            string topologyId,
            TieredLevelPlan plan,
            ElevationEdgeModel.RoomBoundaryContext boundaryContext,
            Vector3 localOrigin,
            GameObject root)
        {
            lastNavigationSurfacePlan = new CapturedNavigationSurfacePlan(
                seed,
                topologyId,
                plan,
                boundaryContext,
                localOrigin,
                root);
        }

        /// <summary>
        /// Export the last successfully rendered plan and prove every node agrees
        /// with the collision geometry that the scene exporter will serialize.
        /// </summary>
        internal static void ExportLastNavigationSurfaces(GameObject dungeonRoot, string dataKey)
        {
            if (dungeonRoot == null)
            {
                throw new ArgumentNullException(nameof(dungeonRoot));
            }

            CapturedNavigationSurfacePlan captured = lastNavigationSurfacePlan;
            if (captured == null || captured.root != dungeonRoot)
            {
                throw new InvalidOperationException(
                    "[NAV_SURFACES] no navigation plan was captured for the generated dungeon root");
            }

            NavigationSurfaceArtifact artifact = BuildNavigationSurfaceArtifact(
                captured.seed,
                captured.topologyId,
                captured.plan,
                captured.boundaryContext,
                captured.localOrigin,
                dungeonRoot.transform);
            if (!ValidateNavigationCollisionAgreement(
                    dungeonRoot,
                    artifact,
                    out string collisionFailure))
            {
                throw new InvalidOperationException(
                    $"[NAV_COLLISION_DISAGREEMENT] {collisionFailure}");
            }

            artifact.validation.collision_agreement = true;
            if (!ValidateNavigationSurfaceGraph(
                    artifact,
                    captured.plan.transitions,
                    out string graphFailure))
            {
                throw new InvalidOperationException($"[NAV_SURFACE_GRAPH] {graphFailure}");
            }

            artifact.validation.graph_valid = true;
            artifact.validation.fall_free_connected = true;
            artifact.validation.checked_nodes = artifact.nodes.Length;
            artifact.validation.checked_edges = artifact.edges.Length;

            string json = JsonUtility.ToJson(artifact, prettyPrint: true);
            WriteNavigationArtifact(
                $"{RelativeServerWorldDataDirectory}/{dataKey}.navsurfaces.shared.json",
                json);
            WriteNavigationArtifact(
                $"{RelativeBundledWorldDataDirectory}/{dataKey}.navsurfaces.shared.json",
                json);
            AssetDatabase.Refresh();
            Debug.Log(
                $"[NAV_SURFACES] exported {artifact.nodes.Length} nodes and " +
                $"{artifact.edges.Length} traversable edges with 100% collision agreement");
        }

        private static NavigationSurfaceArtifact BuildNavigationSurfaceArtifact(
            int seed,
            string topologyId,
            TieredLevelPlan plan,
            ElevationEdgeModel.RoomBoundaryContext boundaryContext,
            Vector3 localOrigin,
            Transform dungeonRoot)
        {
            SurfaceField surfaces = plan.surfaces;
            HashSet<SurfaceKey> stairBodySurfaces =
                BuildTransitionBodySurfaceSet(surfaces, plan.transitions);
            var included = new HashSet<SurfaceKey>();
            var nodes = new List<NavigationSurfaceNode>();
            foreach (SurfaceKey surface in surfaces.Surfaces())
            {
                if (stairBodySurfaces.Contains(surface))
                {
                    continue;
                }

                included.Add(surface);
                Vector3 localCenter = localOrigin + new Vector3(
                    (surface.cell.x + 0.5f) * NavigationCellSize,
                    surface.level * NavigationLevelHeight,
                    (surface.cell.y + 0.5f) * NavigationCellSize);
                Vector3 worldCenter = dungeonRoot.TransformPoint(localCenter);
                nodes.Add(new NavigationSurfaceNode
                {
                    id = NavigationSurfaceId(surface),
                    cell = new[] { surface.cell.x, surface.cell.y },
                    level = surface.level,
                    owner = NavigationSurfaceOwner(plan, surface),
                    surface_id = surface.Token,
                    kind = surfaces.KindAt(surface.cell, surface.level).ToString(),
                    planned_world_y = worldCenter.y,
                    world_center = new[] { worldCenter.x, worldCenter.y, worldCenter.z }
                });
            }

            var drafts = new List<NavigationEdgeDraft>();
            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SurfaceKey surface in surfaces.Surfaces())
            {
                if (!included.Contains(surface))
                {
                    continue;
                }

                // East and north only: undirected adjacency once, in canonical
                // cell order. Same-level is the surface identity rule.
                foreach (Vector2Int neighbor in new[]
                         {
                             surface.cell + Vector2Int.right,
                             surface.cell + Vector2Int.up
                         })
                {
                    var target = new SurfaceKey(neighbor, surface.level);
                    if (included.Contains(target) &&
                        NavigationBoundaryIsTraversable(
                            plan.surfaces,
                            boundaryContext,
                            surface,
                            target))
                    {
                        AddNavigationEdge(
                            drafts,
                            edgeKeys,
                            new NavigationEdgeDraft(
                                surface,
                                target,
                                "Walk",
                                directed: false,
                                riseLevels: 0,
                                cost: NavigationCellSize));
                    }
                }
            }

            for (int transitionIndex = 0;
                 transitionIndex < plan.transitions.Count;
                 transitionIndex++)
            {
                ElevationEdgeModel.TransitionEdge transition = plan.transitions[transitionIndex];
                int lowerLevel = transition.LowerLevel;
                int upperLevel = transition.UpperLevel;
                IReadOnlyList<Vector2Int> lowerLandings = transition.hasLandings
                    ? transition.lowerLandingCells
                    : new[] { transition.LowerCell };
                IReadOnlyList<Vector2Int> upperLandings = transition.hasLandings
                    ? transition.upperLandingCells
                    : new[] { transition.HigherCell };
                string kind = transition.RiseLevels == 0 ? "Bridge" : "Stair";
                int transitionEdgeCountBefore = drafts.Count;
                foreach (Vector2Int lowerCell in lowerLandings)
                {
                    foreach (Vector2Int upperCell in upperLandings)
                    {
                        var lower = new SurfaceKey(lowerCell, lowerLevel);
                        var upper = new SurfaceKey(upperCell, upperLevel);
                        if (!included.Contains(lower) || !included.Contains(upper))
                        {
                            continue;
                        }

                        float horizontal =
                            (Mathf.Abs(lowerCell.x - upperCell.x) +
                             Mathf.Abs(lowerCell.y - upperCell.y)) * NavigationCellSize;
                        AddNavigationEdge(
                            drafts,
                            edgeKeys,
                            new NavigationEdgeDraft(
                                lower,
                                upper,
                                kind,
                                directed: false,
                                riseLevels: transition.RiseLevels,
                                cost: Mathf.Sqrt(
                                    horizontal * horizontal +
                                    transition.RiseLevels * transition.RiseLevels),
                                transitionIndex));
                    }
                }

                if (drafts.Count == transitionEdgeCountBefore)
                {
                    throw new InvalidOperationException(
                        $"[NAV_TRANSITION_ENDPOINT_MISSING] transition {transitionIndex} " +
                        $"{transition.firstCell},L{transition.firstLevel}->" +
                        $"{transition.secondCell},L{transition.secondLevel} emitted no navigation edge");
                }
            }

            AddOpeningNavigationEdges(plan, included, drafts, edgeKeys);
            drafts.Sort(CompareNavigationEdges);
            var edges = new NavigationSurfaceEdge[drafts.Count];
            for (int index = 0; index < drafts.Count; index++)
            {
                NavigationEdgeDraft draft = drafts[index];
                edges[index] = new NavigationSurfaceEdge
                {
                    id = $"edge-{index}",
                    from = NavigationSurfaceId(draft.from),
                    to = NavigationSurfaceId(draft.to),
                    kind = draft.kind,
                    directed = draft.directed,
                    rise_levels = draft.riseLevels,
                    cost = draft.cost,
                    source_transition_index = draft.transitionIndex
                };
            }

            return new NavigationSurfaceArtifact
            {
                seed = seed,
                topology_id = topologyId,
                ceiling_levels = plan.topologyCeilingLevels,
                source_transition_count = plan.transitions.Count,
                nodes = nodes.ToArray(),
                edges = edges
            };
        }

        // Mirrors the renderer's equal-height partition decision. A navigation
        // edge may cross an enclosed-room boundary only where the plan opened a
        // doorway; a declared internal railing also blocks it. Stacked surfaces
        // do not receive ground-floor partitions, matching AddStackedSurfaceRims.
        private static bool NavigationBoundaryIsTraversable(
            SurfaceField surfaces,
            ElevationEdgeModel.RoomBoundaryContext context,
            SurfaceKey first,
            SurfaceKey second)
        {
            if (context == null)
            {
                return true;
            }

            bool firstIsFloor = surfaces.TryGetFloorLevel(first.cell, out int firstFloor) &&
                firstFloor == first.level;
            bool secondIsFloor = surfaces.TryGetFloorLevel(second.cell, out int secondFloor) &&
                secondFloor == second.level;
            if (!firstIsFloor || !secondIsFloor)
            {
                return true;
            }

            foreach (ElevationEdgeModel.InternalPathEdge edge in
                     context.internalPathEdges ?? Array.Empty<ElevationEdgeModel.InternalPathEdge>())
            {
                Vector2Int other = edge.cell + DirectionVectorInt(edge.direction);
                if ((edge.cell == first.cell && other == second.cell) ||
                    (edge.cell == second.cell && other == first.cell))
                {
                    return edge.guard == ElevationEdgeModel.InternalPathEdgeGuard.Bare;
                }
            }

            foreach (ElevationEdgeModel.DoorwayEdge doorway in
                     context.doorwayEdges ?? Array.Empty<ElevationEdgeModel.DoorwayEdge>())
            {
                if ((doorway.firstCell == first.cell && doorway.secondCell == second.cell) ||
                    (doorway.firstCell == second.cell && doorway.secondCell == first.cell))
                {
                    return true;
                }
            }

            if (context.cellRoomIds == null || context.enclosedRooms == null)
            {
                return true;
            }

            int firstRoom = context.cellRoomIds.TryGetValue(first.cell, out int firstOwner)
                ? firstOwner
                : -1;
            int secondRoom = context.cellRoomIds.TryGetValue(second.cell, out int secondOwner)
                ? secondOwner
                : -1;
            if (firstRoom < 0 || secondRoom < 0)
            {
                return true;
            }

            bool firstEnclosed = firstRoom < context.enclosedRooms.Count &&
                context.enclosedRooms[firstRoom];
            bool secondEnclosed = secondRoom < context.enclosedRooms.Count &&
                context.enclosedRooms[secondRoom];
            return (!firstEnclosed && !secondEnclosed) || firstRoom == secondRoom;
        }

        private static void AddOpeningNavigationEdges(
            TieredLevelPlan plan,
            HashSet<SurfaceKey> included,
            List<NavigationEdgeDraft> drafts,
            HashSet<string> edgeKeys)
        {
            AddOpeningNavigationEdges(
                plan.surfaces,
                plan.openings,
                included,
                drafts,
                edgeKeys);
        }

        private static void AddOpeningNavigationEdges(
            SurfaceField surfaces,
            IReadOnlyList<PlanOpening> openings,
            HashSet<SurfaceKey> included,
            List<NavigationEdgeDraft> drafts,
            HashSet<string> edgeKeys)
        {
            foreach (PlanOpening opening in openings ?? Array.Empty<PlanOpening>())
            {
                if (opening.kind != OpeningKind.Aperture)
                {
                    continue;
                }

                Vector2Int hole = opening.cell + DirectionVectorInt(opening.direction);
                if (!surfaces.TryGetHighestSurfaceBelow(
                        hole,
                        opening.level,
                        out int catchLevel))
                {
                    throw new InvalidOperationException(
                        $"[APERTURE_NO_CATCH_SURFACE] aperture rim '{opening.id}' found no " +
                        $"surface below {hole},L{opening.level}");
                }

                var rim = new SurfaceKey(opening.cell, opening.level);
                var caught = new SurfaceKey(hole, catchLevel);
                if (!included.Contains(rim) || !included.Contains(caught))
                {
                    throw new InvalidOperationException(
                        $"[APERTURE_NAV_ENDPOINT_MISSING] aperture rim '{opening.id}' " +
                        $"resolved {rim}->{caught}, but one endpoint was consumed by transition geometry");
                }

                int fallLevels = opening.level - catchLevel;
                if (fallLevels < MinHeadroomLevels)
                {
                    throw new InvalidOperationException(
                        $"[APERTURE_FALL_TOO_SHALLOW] aperture rim '{opening.id}' falls " +
                        $"{fallLevels}u; minimum is {MinHeadroomLevels}u");
                }

                if (fallLevels > MaxSurvivableFallLevels)
                {
                    throw new InvalidOperationException(
                        $"[APERTURE_FALL_UNSURVIVABLE] aperture rim '{opening.id}' falls " +
                        $"{fallLevels}u; maximum is {MaxSurvivableFallLevels}u");
                }

                AddNavigationEdge(
                    drafts,
                    edgeKeys,
                    new NavigationEdgeDraft(
                        rim,
                        caught,
                        "Fall",
                        directed: true,
                        riseLevels: fallLevels,
                        cost: NavigationCellSize + fallLevels));
            }
        }

        private static string NavigationSurfaceOwner(TieredLevelPlan plan, SurfaceKey surface)
        {
            OwnerKey carrier = plan.prisms.SurfaceOwnerAt(surface);
            if (!carrier.Equals(OwnerKey.PlanFloor))
            {
                return carrier.Token;
            }

            foreach (RecipeResolution resolution in
                     plan.recipeResolutions ?? Array.Empty<RecipeResolution>())
            {
                foreach (RecipeZonePlacement zone in
                         resolution.zones ?? Array.Empty<RecipeZonePlacement>())
                {
                    if (!ContainsCell(zone.cells, surface.cell))
                    {
                        continue;
                    }

                    int level = resolution.baseLevel +
                        zone.layerRelativeLevel +
                        ResolvedRecipeLayerRelativeLevel(
                            resolution.zones,
                            surface.cell,
                            zone.layerId);
                    if (level == surface.level)
                    {
                        return $"Recipe:{resolution.id}#{zone.layerId}";
                    }
                }
            }

            return carrier.Token;
        }

        private static string NavigationSurfaceId(SurfaceKey surface)
        {
            return $"surface:{surface.Token}";
        }

        private static void AddNavigationEdge(
            List<NavigationEdgeDraft> drafts,
            HashSet<string> keys,
            NavigationEdgeDraft draft)
        {
            SurfaceKey from = draft.from;
            SurfaceKey to = draft.to;
            if (!draft.directed && SurfaceKey.Compare(from, to) > 0)
            {
                SurfaceKey swap = from;
                from = to;
                to = swap;
                draft = new NavigationEdgeDraft(
                    from,
                    to,
                    draft.kind,
                    directed: false,
                    draft.riseLevels,
                    draft.cost,
                    draft.transitionIndex);
            }

            string key =
                $"{NavigationSurfaceId(from)}|{NavigationSurfaceId(to)}|{draft.kind}|{draft.directed}";
            if (keys.Add(key))
            {
                drafts.Add(draft);
            }
        }

        private static int CompareNavigationEdges(
            NavigationEdgeDraft first,
            NavigationEdgeDraft second)
        {
            int byFrom = SurfaceKey.Compare(first.from, second.from);
            if (byFrom != 0)
            {
                return byFrom;
            }

            int byTo = SurfaceKey.Compare(first.to, second.to);
            return byTo != 0
                ? byTo
                : string.CompareOrdinal(first.kind, second.kind);
        }

        private static bool ValidateNavigationSurfaceGraph(
            NavigationSurfaceArtifact artifact,
            IReadOnlyList<ElevationEdgeModel.TransitionEdge> transitions,
            out string failure)
        {
            transitions = transitions ?? Array.Empty<ElevationEdgeModel.TransitionEdge>();
            if (artifact.source_transition_count != transitions.Count)
            {
                failure =
                    $"artifact declares {artifact.source_transition_count} source transitions, " +
                    $"but the captured plan carries {transitions.Count}";
                return false;
            }

            var nodes = new Dictionary<string, NavigationSurfaceNode>(StringComparer.Ordinal);
            foreach (NavigationSurfaceNode node in artifact.nodes)
            {
                if (string.IsNullOrEmpty(node.id) ||
                    nodes.ContainsKey(node.id) ||
                    node.cell == null ||
                    node.cell.Length != 2)
                {
                    failure = $"invalid or duplicate navigation node '{node.id}'";
                    return false;
                }
                nodes.Add(node.id, node);
            }

            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string nodeId in nodes.Keys)
            {
                adjacency[nodeId] = new List<string>();
            }

            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var witnessedTransitionEdges = new int[transitions.Count];
            int witnessedTransitionEdgeCount = 0;
            int validatedFallEdgeCount = 0;
            foreach (NavigationSurfaceEdge edge in artifact.edges)
            {
                if (string.IsNullOrEmpty(edge.id) || !edgeIds.Add(edge.id))
                {
                    failure = $"invalid or duplicate navigation edge '{edge.id}'";
                    return false;
                }

                if (!nodes.TryGetValue(edge.from, out NavigationSurfaceNode from) ||
                    !nodes.TryGetValue(edge.to, out NavigationSurfaceNode to))
                {
                    failure = $"edge '{edge.id}' references a missing endpoint";
                    return false;
                }

                int dx = Mathf.Abs(from.cell[0] - to.cell[0]);
                int dz = Mathf.Abs(from.cell[1] - to.cell[1]);
                int levelDelta = Mathf.Abs(from.level - to.level);
                if (edge.kind == "Walk" &&
                    (edge.directed ||
                     edge.rise_levels != 0 ||
                     edge.source_transition_index != -1 ||
                     dx + dz != 1 ||
                     from.level != to.level))
                {
                    failure = $"walk edge '{edge.id}' is not one same-level cardinal step";
                    return false;
                }

                if (edge.kind == "Fall" &&
                    (!edge.directed ||
                     edge.source_transition_index != -1 ||
                     edge.rise_levels < MinHeadroomLevels ||
                     edge.rise_levels != levelDelta ||
                     from.level <= to.level))
                {
                    failure = $"fall edge '{edge.id}' is not a directed descent";
                    return false;
                }

                if (edge.kind == "Fall")
                {
                    validatedFallEdgeCount++;
                }

                bool isPhysicalTransition = edge.kind == "Stair" || edge.kind == "Bridge";
                if (isPhysicalTransition &&
                    (edge.directed ||
                     edge.source_transition_index < 0 ||
                     edge.source_transition_index >= artifact.source_transition_count ||
                     edge.rise_levels != levelDelta ||
                     (edge.kind == "Stair" && edge.rise_levels <= 0) ||
                     (edge.kind == "Bridge" && edge.rise_levels != 0)))
                {
                    failure = $"edge '{edge.id}' disagrees with its physical transition witness";
                    return false;
                }

                if (isPhysicalTransition)
                {
                    ElevationEdgeModel.TransitionEdge transition =
                        transitions[edge.source_transition_index];
                    if (!NavigationEdgeMatchesTransitionWitness(edge, transition))
                    {
                        failure =
                            $"edge '{edge.id}' cites transition {edge.source_transition_index}, " +
                            "but its kind or endpoints do not match that transition";
                        return false;
                    }

                    witnessedTransitionEdges[edge.source_transition_index]++;
                    witnessedTransitionEdgeCount++;
                }

                if (edge.kind != "Walk" && edge.kind != "Fall" && !isPhysicalTransition)
                {
                    failure = $"edge '{edge.id}' has unknown kind '{edge.kind}'";
                    return false;
                }

                // The Phase E invariant is the fall-free component, so directed
                // falls deliberately contribute nothing here.
                if (!edge.directed)
                {
                    adjacency[edge.from].Add(edge.to);
                    adjacency[edge.to].Add(edge.from);
                }
            }

            for (int transitionIndex = 0;
                 transitionIndex < witnessedTransitionEdges.Length;
                 transitionIndex++)
            {
                if (witnessedTransitionEdges[transitionIndex] == 0)
                {
                    failure = $"physical transition {transitionIndex} emitted no navigation edge";
                    return false;
                }
            }

            if (adjacency.Count == 0)
            {
                failure = "artifact contains no navigation nodes";
                return false;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (string first in adjacency.Keys)
            {
                visited.Add(first);
                queue.Enqueue(first);
                break;
            }

            while (queue.Count > 0)
            {
                foreach (string neighbor in adjacency[queue.Dequeue()])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            if (visited.Count != adjacency.Count)
            {
                var disconnected = new HashSet<string>(
                    adjacency.Keys,
                    StringComparer.Ordinal);
                disconnected.ExceptWith(visited);
                var disconnectedIds = new List<string>(disconnected);
                disconnectedIds.Sort(StringComparer.Ordinal);
                int disconnectedTransitions = 0;
                int disconnectedFalls = 0;
                foreach (NavigationSurfaceEdge edge in artifact.edges)
                {
                    if (!disconnected.Contains(edge.from) &&
                        !disconnected.Contains(edge.to))
                    {
                        continue;
                    }

                    if (edge.kind == "Stair" || edge.kind == "Bridge")
                    {
                        disconnectedTransitions++;
                    }
                    else if (edge.kind == "Fall")
                    {
                        disconnectedFalls++;
                    }
                }

                int sampleCount = Mathf.Min(12, disconnectedIds.Count);
                failure =
                    $"fall-free graph reached {visited.Count}/{adjacency.Count} nodes; " +
                    $"disconnected transition edges={disconnectedTransitions}, " +
                    $"fall edges={disconnectedFalls}; sample=" +
                    string.Join(", ", disconnectedIds.GetRange(0, sampleCount));
                return false;
            }

            artifact.validation.witnessed_transition_edges = witnessedTransitionEdgeCount;
            artifact.validation.validated_fall_edges = validatedFallEdgeCount;
            failure = string.Empty;
            return true;
        }

        private static bool NavigationEdgeMatchesTransitionWitness(
            NavigationSurfaceEdge edge,
            ElevationEdgeModel.TransitionEdge transition)
        {
            string expectedKind = transition.RiseLevels == 0 ? "Bridge" : "Stair";
            if (!string.Equals(edge.kind, expectedKind, StringComparison.Ordinal) ||
                edge.rise_levels != transition.RiseLevels)
            {
                return false;
            }

            IReadOnlyList<Vector2Int> lowerLandings = transition.hasLandings
                ? transition.lowerLandingCells
                : new[] { transition.LowerCell };
            IReadOnlyList<Vector2Int> upperLandings = transition.hasLandings
                ? transition.upperLandingCells
                : new[] { transition.HigherCell };
            foreach (Vector2Int lowerCell in lowerLandings)
            {
                string lower = NavigationSurfaceId(
                    new SurfaceKey(lowerCell, transition.LowerLevel));
                foreach (Vector2Int upperCell in upperLandings)
                {
                    string upper = NavigationSurfaceId(
                        new SurfaceKey(upperCell, transition.UpperLevel));
                    if ((string.Equals(edge.from, lower, StringComparison.Ordinal) &&
                         string.Equals(edge.to, upper, StringComparison.Ordinal)) ||
                        (string.Equals(edge.from, upper, StringComparison.Ordinal) &&
                         string.Equals(edge.to, lower, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ValidateNavigationCollisionAgreement(
            GameObject dungeonRoot,
            NavigationSurfaceArtifact artifact,
            out string failure)
        {
            int collisionLayer = LayerMask.NameToLayer(NavigationCollisionLayerName);
            if (collisionLayer < 0)
            {
                failure = $"required layer '{NavigationCollisionLayerName}' is missing";
                return false;
            }

            Collider[] colliders = dungeonRoot.GetComponentsInChildren<Collider>(includeInactive: false);
            Physics.SyncTransforms();
            ServerCollisionSampler collisionSampler =
                ServerCollisionSampler.Build(colliders, collisionLayer);
            var requiredNodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (NavigationSurfaceEdge edge in artifact.edges)
            {
                if (edge.kind == "Walk")
                {
                    continue;
                }

                requiredNodes.Add(edge.from);
                requiredNodes.Add(edge.to);
            }

            var omitted = new HashSet<string>(StringComparer.Ordinal);
            float maxAdjustment = 0f;
            foreach (NavigationSurfaceNode node in artifact.nodes)
            {
                Vector3 planned = new Vector3(
                    node.world_center[0],
                    node.world_center[1],
                    node.world_center[2]);
                if (!TryFindExactNavigationSurfacePoint(
                        collisionSampler,
                        dungeonRoot.transform,
                        planned,
                        out Vector3 sampledPoint))
                {
                    if (requiredNodes.Contains(node.id))
                    {
                        failure =
                            $"required node '{node.id}' ({node.kind}, owner {node.owner}) found no " +
                            $"server-sampled point at its planned height {planned.y:0.###}u inside cell " +
                            $"({node.cell[0]},{node.cell[1]})";
                        return false;
                    }

                    // A cosmetic set piece may make the plan floor inaccessible
                    // without becoming a canonical surface itself. Excluding that
                    // node is conservative; moving it up to the collider would
                    // turn a different physical surface into the plan floor and
                    // recreate the drift this gate exists to catch.
                    omitted.Add(node.id);
                    continue;
                }

                float adjustment = sampledPoint.y - planned.y;
                node.collision_y_adjustment = adjustment;
                node.world_center[0] = sampledPoint.x;
                node.world_center[1] = sampledPoint.y;
                node.world_center[2] = sampledPoint.z;
                maxAdjustment = Mathf.Max(maxAdjustment, Mathf.Abs(adjustment));
            }

            if (omitted.Count > 0)
            {
                var omittedIds = new List<string>(omitted);
                omittedIds.Sort(StringComparer.Ordinal);
                Debug.LogWarning(
                    $"[NAV_SURFACES] omitted {omittedIds.Count} collision-covered nodes: " +
                    string.Join(", ", omittedIds));
                artifact.nodes = Array.FindAll(
                    artifact.nodes,
                    node => !omitted.Contains(node.id));
                artifact.edges = Array.FindAll(
                    artifact.edges,
                    edge => !omitted.Contains(edge.from) && !omitted.Contains(edge.to));
            }

            if (!ValidateNavigationWalkEdgesAgainstCollision(
                    collisionSampler,
                    artifact,
                    out failure))
            {
                return false;
            }

            if (!PruneNavigationComponentsWithoutTraversalWitness(
                    artifact,
                    out failure))
            {
                return false;
            }

            artifact.validation.omitted_obstructed_surfaces = omitted.Count;
            artifact.validation.max_abs_collision_y_adjustment = maxAdjustment;

            failure = string.Empty;
            return true;
        }

        private static bool TryFindExactNavigationSurfacePoint(
            ServerCollisionSampler collisionSampler,
            Transform dungeonRoot,
            Vector3 plannedCenter,
            out Vector3 sampledPoint)
        {
            // Cell centre first. The remaining points are a deterministic,
            // radius-safe search inside the 4u cell for a representative point
            // when a transition lip clips the centre. The representative point
            // must also fit the server-sized player capsule; a floor point inside
            // a wall or prop is not a navigable witness. A fully covered plan floor
            // finds no point and is conservatively omitted by the caller.
            // A 0.25u lattice finds narrow but still capsule-safe gaps without
            // leaving the 4u cell (1.5u + 0.45u radius < 2u half extent).
            float[] offsets =
            {
                0f,
                -0.25f, 0.25f,
                -0.5f, 0.5f,
                -0.75f, 0.75f,
                -1f, 1f,
                -1.25f, 1.25f,
                -1.5f, 1.5f
            };
            Vector3 right = dungeonRoot.right;
            Vector3 forward = dungeonRoot.forward;
            right.y = 0f;
            forward.y = 0f;
            right.Normalize();
            forward.Normalize();
            foreach (float xOffset in offsets)
            {
                foreach (float zOffset in offsets)
                {
                    Vector3 candidate =
                        plannedCenter + right * xOffset + forward * zOffset;
                    if (!collisionSampler.TrySampleSurface(
                            candidate,
                            out float sampled,
                            out _))
                    {
                        continue;
                    }

                    Vector3 foot = new Vector3(candidate.x, sampled, candidate.z);
                    if (NavigationCollisionHeightAgrees(plannedCenter.y, sampled) &&
                        !NavigationCapsuleOverlapsBlocker(
                            collisionSampler,
                            foot))
                    {
                        sampledPoint = foot;
                        return true;
                    }
                }
            }

            sampledPoint = default;
            return false;
        }

        private static bool NavigationCollisionHeightAgrees(float planned, float sampled)
        {
            return float.IsFinite(planned) &&
                float.IsFinite(sampled) &&
                Mathf.Abs(sampled - planned) <= NavigationCollisionEpsilon;
        }

        private static bool ValidateNavigationWalkEdgesAgainstCollision(
            ServerCollisionSampler collisionSampler,
            NavigationSurfaceArtifact artifact,
            out string failure)
        {
            var nodes = new Dictionary<string, NavigationSurfaceNode>(StringComparer.Ordinal);
            foreach (NavigationSurfaceNode node in artifact.nodes)
            {
                nodes[node.id] = node;
            }

            var retainedEdges = new List<NavigationSurfaceEdge>(artifact.edges.Length);
            foreach (NavigationSurfaceEdge edge in artifact.edges)
            {
                if (edge.kind != "Walk" ||
                    !nodes.TryGetValue(edge.from, out NavigationSurfaceNode from) ||
                    !nodes.TryGetValue(edge.to, out NavigationSurfaceNode to))
                {
                    retainedEdges.Add(edge);
                    continue;
                }

                Vector3 start = NavigationNodeWorldCenter(from);
                Vector3 end = NavigationNodeWorldCenter(to);
                int steps = Mathf.Max(1, Mathf.CeilToInt(
                    Vector2.Distance(
                        new Vector2(start.x, start.z),
                        new Vector2(end.x, end.z)) / 0.25f));
                bool surfaceContinuous = true;
                for (int step = 0; step <= steps; step++)
                {
                    float t = step / (float)steps;
                    Vector3 expected = Vector3.Lerp(start, end, t);
                    if (!collisionSampler.TrySampleSurface(
                            expected,
                            out float sampled,
                            out _) ||
                        !NavigationCollisionHeightAgrees(expected.y, sampled))
                    {
                        surfaceContinuous = false;
                        break;
                    }
                }

                if (!surfaceContinuous)
                {
                    artifact.validation.omitted_collision_gap_walk_edges++;
                    continue;
                }

                retainedEdges.Add(edge);
                artifact.validation.collision_checked_walk_edges++;
            }

            artifact.edges = retainedEdges.ToArray();
            failure = string.Empty;
            return true;
        }

        private static bool PruneNavigationComponentsWithoutTraversalWitness(
            NavigationSurfaceArtifact artifact,
            out string failure)
        {
            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (NavigationSurfaceNode node in artifact.nodes)
            {
                adjacency[node.id] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (NavigationSurfaceEdge edge in artifact.edges)
            {
                if (edge.directed ||
                    !adjacency.ContainsKey(edge.from) ||
                    !adjacency.ContainsKey(edge.to))
                {
                    continue;
                }

                adjacency[edge.from].Add(edge.to);
                adjacency[edge.to].Add(edge.from);
            }

            var componentByNode = new Dictionary<string, int>(StringComparer.Ordinal);
            var components = new List<List<string>>();
            foreach (NavigationSurfaceNode node in artifact.nodes)
            {
                if (componentByNode.ContainsKey(node.id))
                {
                    continue;
                }

                int componentIndex = components.Count;
                var component = new List<string>();
                var queue = new Queue<string>();
                componentByNode[node.id] = componentIndex;
                queue.Enqueue(node.id);
                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    component.Add(current);
                    foreach (string neighbor in adjacency[current])
                    {
                        if (!componentByNode.ContainsKey(neighbor))
                        {
                            componentByNode[neighbor] = componentIndex;
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                components.Add(component);
            }

            if (components.Count <= 1)
            {
                failure = string.Empty;
                return true;
            }

            int witnessedComponent = -1;
            foreach (NavigationSurfaceEdge edge in artifact.edges)
            {
                // Directed falls are traversal opportunities, not evidence that
                // both endpoints belong to the same fall-free component. A fall
                // into a collision-isolated cosmetic pocket must be pruned with
                // that pocket below; rejecting it here leaves the production
                // scene saved while navigation/collision export is still stale.
                // Only physical Stair/Bridge transitions witness a component
                // that must survive this conservative pruning pass.
                if (edge.kind == "Walk" || edge.kind == "Fall")
                {
                    continue;
                }

                if (!componentByNode.TryGetValue(edge.from, out int fromComponent) ||
                    !componentByNode.TryGetValue(edge.to, out int toComponent))
                {
                    failure = $"edge '{edge.id}' lost an endpoint before component validation";
                    return false;
                }

                if (fromComponent != toComponent)
                {
                    failure =
                        $"{edge.kind} edge '{edge.id}' spans disconnected fall-free " +
                        $"components {fromComponent} and {toComponent}";
                    return false;
                }

                if (witnessedComponent < 0)
                {
                    witnessedComponent = fromComponent;
                }
                else if (witnessedComponent != fromComponent)
                {
                    failure =
                        $"physical traversal witnesses span fall-free components " +
                        $"{witnessedComponent} and {fromComponent}";
                    return false;
                }
            }

            if (witnessedComponent < 0)
            {
                witnessedComponent = 0;
                for (int index = 1; index < components.Count; index++)
                {
                    if (components[index].Count > components[witnessedComponent].Count)
                    {
                        witnessedComponent = index;
                    }
                }
            }

            var retained = new HashSet<string>(
                components[witnessedComponent],
                StringComparer.Ordinal);
            int omittedCount = artifact.nodes.Length - retained.Count;
            artifact.nodes = Array.FindAll(
                artifact.nodes,
                node => retained.Contains(node.id));
            artifact.edges = Array.FindAll(
                artifact.edges,
                edge => retained.Contains(edge.from) && retained.Contains(edge.to));
            artifact.validation.collision_checked_walk_edges = 0;
            foreach (NavigationSurfaceEdge edge in artifact.edges)
            {
                if (edge.kind == "Walk")
                {
                    artifact.validation.collision_checked_walk_edges++;
                }
            }
            artifact.validation.omitted_unwitnessed_component_surfaces = omittedCount;
            Debug.LogWarning(
                $"[NAV_SURFACES] omitted {omittedCount} surfaces in " +
                $"{components.Count - 1} fall-free component(s) with no traversal witness");

            failure = string.Empty;
            return true;
        }

        private static Vector3 NavigationNodeWorldCenter(NavigationSurfaceNode node)
        {
            return new Vector3(
                node.world_center[0],
                node.world_center[1],
                node.world_center[2]);
        }

        private static bool NavigationCapsuleOverlapsBlocker(
            ServerCollisionSampler collisionSampler,
            Vector3 foot)
        {
            float insetRadius = NavigationPlayerRadius - NavigationCollisionEpsilon;
            Vector3 bottom = foot + Vector3.up * (NavigationPlayerRadius + NavigationCollisionEpsilon);
            Vector3 top = foot + Vector3.up *
                (NavigationPlayerHeight - NavigationPlayerRadius - NavigationCollisionEpsilon);
            foreach (Collider collider in Physics.OverlapCapsule(
                         bottom,
                         top,
                         insetRadius,
                         1 << collisionSampler.CollisionLayer,
                         QueryTriggerInteraction.Ignore))
            {
                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    collider.gameObject.layer != collisionSampler.CollisionLayer ||
                    !collisionSampler.ContainsCollider(collider))
                {
                    continue;
                }

                Bounds bounds = collider.bounds;
                if (bounds.max.y <= foot.y + NavigationCollisionEpsilon ||
                    bounds.min.y >= foot.y + NavigationPlayerHeight - NavigationCollisionEpsilon)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool TryServerTriangleHeightAtXZ(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float x,
            float z,
            out float height)
        {
            height = 0f;
            float denominator =
                (b.z - c.z) * (a.x - c.x) +
                (c.x - b.x) * (a.z - c.z);
            if (!float.IsFinite(denominator) ||
                Mathf.Abs(denominator) <= ServerCollisionEpsilon)
            {
                return false;
            }

            float alpha =
                ((b.z - c.z) * (x - c.x) +
                 (c.x - b.x) * (z - c.z)) / denominator;
            float beta =
                ((c.z - a.z) * (x - c.x) +
                 (a.x - c.x) * (z - c.z)) / denominator;
            float gamma = 1f - alpha - beta;
            if (alpha < -ServerCollisionEpsilon ||
                beta < -ServerCollisionEpsilon ||
                gamma < -ServerCollisionEpsilon)
            {
                return false;
            }

            height = alpha * a.y + beta * b.y + gamma * c.y;
            return float.IsFinite(height);
        }

        private static void WriteNavigationArtifact(string relativePath, string json)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string outputPath = Path.Combine(projectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, json);
        }

        // Focused EditMode contract for the production sampler. Two collider
        // instances deliberately share one mesh, and distant primitives occupy
        // other spatial buckets. Repeated hot-loop queries must therefore reuse
        // one mesh-buffer read and visit only the one local triangle.
        private static string BuildNavigationCollisionSamplerContractSnapshot()
        {
            int collisionLayer = LayerMask.NameToLayer(NavigationCollisionLayerName);
            if (collisionLayer < 0)
            {
                return "collision.layerPresent=False";
            }

            var root = new GameObject("Navigation Collision Sampler Contract");
            var mesh = new Mesh { name = "Navigation Collision Sampler Contract Mesh" };
            try
            {
                GameObject nearBoxObject = new GameObject("Near Box");
                nearBoxObject.transform.SetParent(root.transform, worldPositionStays: false);
                nearBoxObject.transform.position = new Vector3(0f, -0.5f, 0f);
                nearBoxObject.layer = collisionLayer;
                BoxCollider nearBox = nearBoxObject.AddComponent<BoxCollider>();
                nearBox.size = new Vector3(4f, 1f, 4f);

                GameObject farBoxObject = new GameObject("Far Box");
                farBoxObject.transform.SetParent(root.transform, worldPositionStays: false);
                farBoxObject.transform.position = new Vector3(64f, -0.5f, 0f);
                farBoxObject.layer = collisionLayer;
                BoxCollider farBox = farBoxObject.AddComponent<BoxCollider>();
                farBox.size = new Vector3(4f, 1f, 4f);

                mesh.vertices = new[]
                {
                    new Vector3(4f, 2f, 0f),
                    new Vector3(4f, 2f, 4f),
                    new Vector3(8f, 2f, 0f)
                };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.RecalculateBounds();

                GameObject nearMeshObject = new GameObject("Near Mesh");
                nearMeshObject.transform.SetParent(root.transform, worldPositionStays: false);
                nearMeshObject.layer = collisionLayer;
                nearMeshObject.AddComponent<MeshCollider>().sharedMesh = mesh;

                GameObject farMeshObject = new GameObject("Far Mesh");
                farMeshObject.transform.SetParent(root.transform, worldPositionStays: false);
                farMeshObject.transform.position = new Vector3(64f, 0f, 0f);
                farMeshObject.layer = collisionLayer;
                farMeshObject.AddComponent<MeshCollider>().sharedMesh = mesh;

                Physics.SyncTransforms();
                Collider[] colliders =
                    root.GetComponentsInChildren<Collider>(includeInactive: false);
                ServerCollisionSampler sampler =
                    ServerCollisionSampler.Build(colliders, collisionLayer);

                bool boxAccepted = sampler.TrySampleSurface(
                    Vector3.zero,
                    out float boxHeight,
                    out _);
                bool triangleAccepted = sampler.TrySampleSurface(
                    new Vector3(6f, 2f, 2f),
                    out float triangleHeight,
                    out _);
                bool outsideTriangleRejected = !sampler.TrySampleSurface(
                    new Vector3(6.25f, 2f, 2.25f),
                    out _,
                    out _);
                bool captureWindowDriftRejected = !sampler.TrySampleSurface(
                    new Vector3(6f, 0f, 2f),
                    out _,
                    out _);

                sampler.ResetDiagnostics();
                bool repeatedQueriesAccepted = true;
                const int RepeatedQueryCount = 128;
                for (int index = 0; index < RepeatedQueryCount; index++)
                {
                    repeatedQueriesAccepted &= sampler.TrySampleSurface(
                        new Vector3(6f, 2f, 2f),
                        out _,
                        out _);
                }

                bool spatiallyPruned =
                    sampler.BoxCandidateCheckCount == 0 &&
                    sampler.TriangleCandidateCheckCount == RepeatedQueryCount;
                return string.Join("\n", new[]
                {
                    "collision.layerPresent=True",
                    $"collision.boxAccepted={boxAccepted}",
                    $"collision.boxHeightExact={Mathf.Abs(boxHeight) <= ServerCollisionEpsilon}",
                    $"collision.triangleAccepted={triangleAccepted}",
                    $"collision.triangleHeightExact={Mathf.Abs(triangleHeight - 2f) <= ServerCollisionEpsilon}",
                    $"collision.outsideTriangleRejected={outsideTriangleRejected}",
                    $"collision.captureWindowDriftRejected={captureWindowDriftRejected}",
                    $"cache.boxPrimitives={sampler.BoxPrimitiveCount}",
                    $"cache.trianglePrimitives={sampler.TrianglePrimitiveCount}",
                    $"cache.meshBufferReads={sampler.MeshBufferReadCount}",
                    $"cache.repeatedQueriesAccepted={repeatedQueriesAccepted}",
                    $"cache.queryCount={sampler.QueryCount}",
                    $"cache.boxCandidateChecks={sampler.BoxCandidateCheckCount}",
                    $"cache.triangleCandidateChecks={sampler.TriangleCandidateCheckCount}",
                    $"cache.spatiallyPruned={spatiallyPruned}"
                });
            }
            finally
            {
                DestroyImmediate(root);
                DestroyImmediate(mesh);
            }
        }

        // Pure headless contract used by EditMode tests: a same-level walk and a
        // witnessed stair must form one fall-free graph without any scene state.
        private static string BuildNavigationSurfaceContractSnapshot()
        {
            var boundarySurfaces = new SurfaceField(new Dictionary<Vector2Int, int>
            {
                [Vector2Int.zero] = 0,
                [Vector2Int.right] = 0
            });
            var sealedBoundary = new ElevationEdgeModel.RoomBoundaryContext(
                new Dictionary<Vector2Int, int>
                {
                    [Vector2Int.zero] = 0,
                    [Vector2Int.right] = 1
                },
                new[] { true, false },
                Array.Empty<ElevationEdgeModel.DoorwayEdge>());
            var doorwayBoundary = new ElevationEdgeModel.RoomBoundaryContext(
                sealedBoundary.cellRoomIds,
                sealedBoundary.enclosedRooms,
                new[]
                {
                    new ElevationEdgeModel.DoorwayEdge(
                        Vector2Int.zero,
                        Vector2Int.right)
                });
            var boundaryFirst = new SurfaceKey(Vector2Int.zero, 0);
            var boundarySecond = new SurfaceKey(Vector2Int.right, 0);
            bool sealedPartitionRejected = !NavigationBoundaryIsTraversable(
                boundarySurfaces,
                sealedBoundary,
                boundaryFirst,
                boundarySecond);
            bool doorwayAccepted = NavigationBoundaryIsTraversable(
                boundarySurfaces,
                doorwayBoundary,
                boundaryFirst,
                boundarySecond);
            bool triangleEdgeAccepted = TryServerTriangleHeightAtXZ(
                new Vector3(0f, 2f, 0f),
                new Vector3(4f, 2f, 0f),
                new Vector3(0f, 2f, 4f),
                2f,
                2f,
                out float triangleEdgeHeight) &&
                Mathf.Abs(triangleEdgeHeight - 2f) <= ServerCollisionEpsilon;
            bool outsideTriangleRejected = !TryServerTriangleHeightAtXZ(
                new Vector3(0f, 2f, 0f),
                new Vector3(4f, 2f, 0f),
                new Vector3(0f, 2f, 4f),
                2.001f,
                2.001f,
                out _);
            bool exactCollisionHeightAccepted = NavigationCollisionHeightAgrees(2f, 2.05f);
            bool captureWindowDriftRejected = !NavigationCollisionHeightAgrees(2f, 3f);

            var nodes = new[]
            {
                new NavigationSurfaceNode { id = "surface:0,0,L0", cell = new[] { 0, 0 }, level = 0 },
                new NavigationSurfaceNode { id = "surface:1,0,L0", cell = new[] { 1, 0 }, level = 0 },
                new NavigationSurfaceNode { id = "surface:2,0,L4", cell = new[] { 2, 0 }, level = 4 }
            };
            var edges = new[]
            {
                new NavigationSurfaceEdge
                {
                    id = "edge-0", from = nodes[0].id, to = nodes[1].id,
                    kind = "Walk", directed = false, rise_levels = 0, cost = 4f
                },
                new NavigationSurfaceEdge
                {
                    id = "edge-1", from = nodes[1].id, to = nodes[2].id,
                    kind = "Stair", directed = false, rise_levels = 4, cost = 5.65f,
                    source_transition_index = 0
                }
            };
            var artifact = new NavigationSurfaceArtifact
            {
                source_transition_count = 1,
                nodes = nodes,
                edges = edges
            };
            var transitions = new[]
            {
                new ElevationEdgeModel.TransitionEdge(
                    Vector2Int.right,
                    0,
                    new Vector2Int(2, 0),
                    4,
                    "probe-stair",
                    EmbeddedStairPlacementClass)
            };
            bool valid = ValidateNavigationSurfaceGraph(
                artifact,
                transitions,
                out string failure);
            int witnessedTransitionEdgeCount =
                artifact.validation.witnessed_transition_edges;
            edges[1].source_transition_index = -1;
            bool unwitnessedRejected = !ValidateNavigationSurfaceGraph(
                artifact,
                transitions,
                out _);
            edges[1].source_transition_index = 0;
            string witnessedEndpoint = edges[1].to;
            edges[1].to = nodes[0].id;
            bool wrongWitnessRejected = !ValidateNavigationSurfaceGraph(
                artifact,
                transitions,
                out _);
            edges[1].to = witnessedEndpoint;

            var isolatedFallArtifact = new NavigationSurfaceArtifact
            {
                nodes = new[]
                {
                    new NavigationSurfaceNode { id = "main-low" },
                    new NavigationSurfaceNode { id = "main-high" },
                    new NavigationSurfaceNode { id = "isolated-fall-target" }
                },
                edges = new[]
                {
                    new NavigationSurfaceEdge
                    {
                        id = "main-stair", from = "main-low", to = "main-high",
                        kind = "Stair", directed = false
                    },
                    new NavigationSurfaceEdge
                    {
                        id = "isolated-fall", from = "main-high", to = "isolated-fall-target",
                        kind = "Fall", directed = true
                    }
                }
            };
            bool isolatedFallPruned = PruneNavigationComponentsWithoutTraversalWitness(
                isolatedFallArtifact,
                out string isolatedFallFailure) &&
                isolatedFallArtifact.nodes.Length == 2 &&
                isolatedFallArtifact.edges.Length == 1 &&
                isolatedFallArtifact.validation.omitted_unwitnessed_component_surfaces == 1;
            return string.Join("\n", new[]
            {
                $"graph.valid={valid}",
                $"graph.failure={failure}",
                $"graph.nodeCount={nodes.Length}",
                $"graph.edgeCount={edges.Length}",
                $"graph.unwitnessedTransitionRejected={unwitnessedRejected}",
                $"graph.wrongTransitionWitnessRejected={wrongWitnessRejected}",
                $"graph.witnessedTransitionEdges={witnessedTransitionEdgeCount}",
                $"graph.sealedPartitionRejected={sealedPartitionRejected}",
                $"graph.doorwayAccepted={doorwayAccepted}",
                $"graph.isolatedFallPruned={isolatedFallPruned}",
                $"graph.isolatedFallFailure={isolatedFallFailure}",
                $"collision.triangleEdgeAccepted={triangleEdgeAccepted}",
                $"collision.outsideTriangleRejected={outsideTriangleRejected}",
                $"collision.exactHeightAccepted={exactCollisionHeightAccepted}",
                $"collision.captureWindowDriftRejected={captureWindowDriftRejected}"
            });
        }
    }
}
