#nullable enable

using System;
using System.Collections.Generic;
using Arena.World;
using UnityEngine;

namespace Arena.Combat
{
    /// <summary>
    /// A probe-ray hit against the bundled server collision data.
    /// </summary>
    internal readonly struct ServerLosProbeHit
    {
        public ServerLosProbeHit(Vector3 point, float distance, string blocker)
        {
            Point = point;
            Distance = distance;
            Blocker = blocker;
        }

        public Vector3 Point { get; }
        public float Distance { get; }
        public string Blocker { get; }
    }

    /// <summary>
    /// Client-side raycast index over the bundled shared collision data — the
    /// same heightfield, exported collision boxes, and query meshes the server
    /// raycasts for line-of-sight (contract-stamped, see ContractVersionGuard).
    /// Consumed by the S4 advisory LOS pre-check and the LOS debug guide.
    /// </summary>
    internal sealed class ServerLosCollisionData
    {
        private const float CollisionEpsilon = 0.0001f;
        private const float OpenWorldRaycastStep = 0.25f;
        private const int OpenWorldRaycastRefineIters = 7;

        private readonly ServerHeightfield? _heightfield;
        private readonly ServerCollisionBox[] _boxes;
        private readonly ServerQueryMeshInstance[] _queryMeshInstances;

        private ServerLosCollisionData(
            ServerHeightfield? heightfield,
            ServerCollisionBox[] boxes,
            ServerQueryMeshInstance[] queryMeshInstances)
        {
            _heightfield = heightfield;
            _boxes = boxes;
            _queryMeshInstances = queryMeshInstances;
        }

        public static ServerLosCollisionData? Load(OpenWorldSceneProfile profile)
        {
            string heightfieldPath = $"SharedData/Worlds/{profile.DataKey}.heightfield.shared";
            TextAsset? heightfieldAsset = Resources.Load<TextAsset>(heightfieldPath);
            ServerHeightfield? heightfield = null;
            if (heightfieldAsset != null)
            {
                HeightfieldFile? file = JsonUtility.FromJson<HeightfieldFile>(heightfieldAsset.text);
                if (file != null &&
                    file.origin != null &&
                    file.origin.Length == 3 &&
                    file.size != null &&
                    file.size.Length == 3)
                {
                    var parsed = new ServerHeightfield(
                        file.origin[0],
                        file.origin[1],
                        file.origin[2],
                        file.size[0],
                        file.size[1],
                        file.size[2],
                        file.resolution_x,
                        file.resolution_z,
                        file.heights ?? Array.Empty<float>());
                    if (parsed.IsValid)
                        heightfield = parsed;
                }
            }

            // Query geometry only (owner ruling, S4): movement collision is
            // authored oversized to keep capsules out and never blocks sight —
            // mirror the server's raycast set exactly (terrain + query boxes +
            // query meshes).
            List<ServerCollisionBox> boxes = new();
            GameplayCollisionLayoutFile? queryLayout =
                LoadLayout($"SharedData/Worlds/{profile.DataKey}.query_collision.shared");
            if (queryLayout != null)
            {
                AddBoxes(queryLayout, boxes);
            }

            ServerQueryMeshInstance[] queryMeshInstances = queryLayout != null
                ? BuildQueryMeshInstances(queryLayout)
                : Array.Empty<ServerQueryMeshInstance>();

            if (heightfield == null && boxes.Count == 0 && queryMeshInstances.Length == 0)
                return null;

            return new ServerLosCollisionData(heightfield, boxes.ToArray(), queryMeshInstances);
        }

        public ServerLosProbeHit? FindFirstHit(Vector3 origin, Vector3 end, float radius)
        {
            Vector3 delta = end - origin;
            float distance = delta.magnitude;
            if (distance <= CollisionEpsilon)
                return null;

            Vector3 direction = delta / distance;
            ServerLosProbeHit? terrainHit = RaycastHeightfield(origin, direction, distance, radius);
            ServerLosProbeHit? best = terrainHit;

            for (int i = 0; i < _boxes.Length; i++)
            {
                if (_boxes[i].TryRaycast(origin, direction, distance, radius, out float t) &&
                    IsCloser(t, best))
                {
                    best = new ServerLosProbeHit(origin + direction * t, t, _boxes[i].Name);
                }
            }

            for (int i = 0; i < _queryMeshInstances.Length; i++)
            {
                if (_queryMeshInstances[i].TryRaycast(origin, direction, distance, radius, best, out float t) &&
                    IsCloser(t, best))
                {
                    best = new ServerLosProbeHit(origin + direction * t, t, _queryMeshInstances[i].Name);
                }
            }

            return best;
        }

        private static bool IsCloser(float t, ServerLosProbeHit? best)
        {
            return t >= 0f && (!best.HasValue || t < best.Value.Distance);
        }

        private static GameplayCollisionLayoutFile? LoadLayout(string resourcePath)
        {
            TextAsset? asset = Resources.Load<TextAsset>(resourcePath);
            return asset == null ? null : JsonUtility.FromJson<GameplayCollisionLayoutFile>(asset.text);
        }

        private static void AddBoxes(GameplayCollisionLayoutFile layout, List<ServerCollisionBox> boxes)
        {
            GameplayCollisionBoxFile[] files = layout.boxes ?? Array.Empty<GameplayCollisionBoxFile>();
            for (int i = 0; i < files.Length; i++)
            {
                if (ServerCollisionBox.TryCreate(files[i], out ServerCollisionBox box))
                    boxes.Add(box);
            }
        }

        private static ServerQueryMeshInstance[] BuildQueryMeshInstances(GameplayCollisionLayoutFile layout)
        {
            GameplayQueryMeshGeometryFile[] geometryFiles =
                layout.mesh_geometries ?? Array.Empty<GameplayQueryMeshGeometryFile>();
            GameplayQueryMeshInstanceFile[] instanceFiles =
                layout.mesh_instances ?? Array.Empty<GameplayQueryMeshInstanceFile>();
            if (geometryFiles.Length == 0 || instanceFiles.Length == 0)
                return Array.Empty<ServerQueryMeshInstance>();

            var geometries = new Dictionary<string, ServerQueryMeshGeometry>(geometryFiles.Length);
            for (int i = 0; i < geometryFiles.Length; i++)
            {
                if (ServerQueryMeshGeometry.TryCreate(geometryFiles[i], out ServerQueryMeshGeometry geometry))
                    geometries[geometry.Id] = geometry;
            }

            var instances = new List<ServerQueryMeshInstance>(instanceFiles.Length);
            for (int i = 0; i < instanceFiles.Length; i++)
            {
                GameplayQueryMeshInstanceFile file = instanceFiles[i];
                if (string.IsNullOrEmpty(file.geometry_id) ||
                    !geometries.TryGetValue(file.geometry_id, out ServerQueryMeshGeometry geometry))
                {
                    continue;
                }

                if (ServerQueryMeshInstance.TryCreate(file, geometry, out ServerQueryMeshInstance instance))
                    instances.Add(instance);
            }

            return instances.ToArray();
        }

        private ServerLosProbeHit? RaycastHeightfield(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            float radius)
        {
            if (_heightfield == null)
                return null;

            bool IntersectsAt(float t)
            {
                Vector3 point = origin + direction * t;
                return point.y <= _heightfield.Value.SampleHeight(point.x, point.z) + radius;
            }

            if (IntersectsAt(0f))
                return new ServerLosProbeHit(origin, 0f, "server heightfield terrain");

            float previousT = 0f;
            float tCursor = Mathf.Max(OpenWorldRaycastStep, CollisionEpsilon);
            while (tCursor <= maxDistance + CollisionEpsilon)
            {
                float clampedT = Mathf.Min(tCursor, maxDistance);
                if (IntersectsAt(clampedT))
                {
                    float lo = previousT;
                    float hi = clampedT;
                    for (int i = 0; i < OpenWorldRaycastRefineIters; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (IntersectsAt(mid))
                            hi = mid;
                        else
                            lo = mid;
                    }

                    return new ServerLosProbeHit(origin + direction * hi, hi, "server heightfield terrain");
                }

                previousT = clampedT;
                if (clampedT >= maxDistance)
                    break;
                tCursor += OpenWorldRaycastStep;
            }

            return null;
        }

#pragma warning disable CS0649 // JsonUtility populates these fields by name.
        [Serializable]
        private sealed class HeightfieldFile
        {
            public float[] origin = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public int resolution_x;
            public int resolution_z;
            public float[] heights = Array.Empty<float>();
        }

        [Serializable]
        private sealed class GameplayCollisionLayoutFile
        {
            public GameplayCollisionBoxFile[] boxes = Array.Empty<GameplayCollisionBoxFile>();
            public GameplayQueryMeshGeometryFile[] mesh_geometries = Array.Empty<GameplayQueryMeshGeometryFile>();
            public GameplayQueryMeshInstanceFile[] mesh_instances = Array.Empty<GameplayQueryMeshInstanceFile>();
        }

        [Serializable]
        private sealed class GameplayCollisionBoxFile
        {
            public string name = string.Empty;
            public string shape = string.Empty;
            public float[] center = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public float[] rotation = Array.Empty<float>();
            public float rotation_y_deg;
        }

        [Serializable]
        private sealed class GameplayQueryMeshGeometryFile
        {
            public string id = string.Empty;
            public string source = string.Empty;
            public int vertex_count;
            public int triangle_count;
            public float[] vertices = Array.Empty<float>();
            public int[] indices = Array.Empty<int>();
        }

        [Serializable]
        private sealed class GameplayQueryMeshInstanceFile
        {
            public string name = string.Empty;
            public string geometry_id = string.Empty;
            public float[] transform = Array.Empty<float>();
        }
#pragma warning restore CS0649

        private readonly struct ServerHeightfield
        {
            public ServerHeightfield(
                float originX,
                float originY,
                float originZ,
                float sizeX,
                float sizeY,
                float sizeZ,
                int resolutionX,
                int resolutionZ,
                float[] heights)
            {
                OriginX = originX;
                OriginY = originY;
                OriginZ = originZ;
                SizeX = sizeX;
                SizeY = sizeY;
                SizeZ = sizeZ;
                ResolutionX = resolutionX;
                ResolutionZ = resolutionZ;
                Heights = heights;
            }

            private float OriginX { get; }
            private float OriginY { get; }
            private float OriginZ { get; }
            private float SizeX { get; }
            private float SizeY { get; }
            private float SizeZ { get; }
            private int ResolutionX { get; }
            private int ResolutionZ { get; }
            private float[] Heights { get; }

            public bool IsValid =>
                ResolutionX >= 2 &&
                ResolutionZ >= 2 &&
                Heights.Length == ResolutionX * ResolutionZ;

            public float SampleHeight(float x, float z)
            {
                if (!IsValid)
                    return OriginY;

                float normalizedX = Mathf.Clamp01((x - OriginX) / Mathf.Max(SizeX, CollisionEpsilon));
                float normalizedZ = Mathf.Clamp01((z - OriginZ) / Mathf.Max(SizeZ, CollisionEpsilon));
                float sampleX = normalizedX * (ResolutionX - 1);
                float sampleZ = normalizedZ * (ResolutionZ - 1);

                int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, ResolutionX - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt(sampleZ), 0, ResolutionZ - 1);
                int x1 = Mathf.Min(x0 + 1, ResolutionX - 1);
                int z1 = Mathf.Min(z0 + 1, ResolutionZ - 1);

                float tx = sampleX - x0;
                float tz = sampleZ - z0;
                float h00 = Heights[z0 * ResolutionX + x0];
                float h10 = Heights[z0 * ResolutionX + x1];
                float h01 = Heights[z1 * ResolutionX + x0];
                float h11 = Heights[z1 * ResolutionX + x1];

                float hx0 = Mathf.Lerp(h00, h10, tx);
                float hx1 = Mathf.Lerp(h01, h11, tx);
                return Mathf.Lerp(hx0, hx1, tz);
            }
        }

        private readonly struct ServerCollisionBox
        {
            private enum BoxKind
            {
                Aabb,
                ObbY,
                ObbXyz,
            }

            private readonly BoxKind _kind;
            private readonly Vector3 _center;
            private readonly Vector3 _half;
            private readonly float _sinY;
            private readonly float _cosY;
            private readonly Vector3 _axisX;
            private readonly Vector3 _axisY;
            private readonly Vector3 _axisZ;

            private ServerCollisionBox(
                string name,
                BoxKind kind,
                Vector3 center,
                Vector3 half,
                float sinY,
                float cosY,
                Vector3 axisX,
                Vector3 axisY,
                Vector3 axisZ)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "server collision box" : name;
                _kind = kind;
                _center = center;
                _half = half;
                _sinY = sinY;
                _cosY = cosY;
                _axisX = axisX;
                _axisY = axisY;
                _axisZ = axisZ;
            }

            public string Name { get; }

            public static bool TryCreate(GameplayCollisionBoxFile file, out ServerCollisionBox box)
            {
                box = default;
                if (file.center == null || file.center.Length < 3 || file.size == null || file.size.Length < 3)
                    return false;

                Vector3 center = new(file.center[0], file.center[1], file.center[2]);
                Vector3 half = new(
                    Mathf.Abs(file.size[0]) * 0.5f,
                    Mathf.Abs(file.size[1]) * 0.5f,
                    Mathf.Abs(file.size[2]) * 0.5f);
                string shape = file.shape ?? string.Empty;
                if (string.Equals(shape, "aabb", StringComparison.Ordinal))
                {
                    box = new ServerCollisionBox(
                        file.name,
                        BoxKind.Aabb,
                        center,
                        half,
                        0f,
                        1f,
                        Vector3.right,
                        Vector3.up,
                        Vector3.forward);
                    return true;
                }

                if (string.Equals(shape, "obb_xyz", StringComparison.Ordinal))
                {
                    if (!QuaternionToAxes(file.rotation, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ))
                        return false;
                    box = new ServerCollisionBox(file.name, BoxKind.ObbXyz, center, half, 0f, 1f, axisX, axisY, axisZ);
                    return true;
                }

                float yaw = file.rotation_y_deg * Mathf.Deg2Rad;
                box = new ServerCollisionBox(
                    file.name,
                    BoxKind.ObbY,
                    center,
                    half,
                    Mathf.Sin(yaw),
                    Mathf.Cos(yaw),
                    Vector3.right,
                    Vector3.up,
                    Vector3.forward);
                return true;
            }

            public bool TryRaycast(Vector3 origin, Vector3 direction, float maxDistance, float radius, out float t)
            {
                Vector3 expandedHalf = _half + Vector3.one * Mathf.Max(radius, 0f);
                switch (_kind)
                {
                    case BoxKind.Aabb:
                        return RaycastCenteredAabb(origin - _center, direction, expandedHalf, maxDistance, out t);
                    case BoxKind.ObbY:
                    {
                        Vector3 rel = origin - _center;
                        Vector3 localOrigin = new(
                            rel.x * _cosY - rel.z * _sinY,
                            rel.y,
                            rel.x * _sinY + rel.z * _cosY);
                        Vector3 localDir = new(
                            direction.x * _cosY - direction.z * _sinY,
                            direction.y,
                            direction.x * _sinY + direction.z * _cosY);
                        return RaycastCenteredAabb(localOrigin, localDir, expandedHalf, maxDistance, out t);
                    }
                    case BoxKind.ObbXyz:
                    {
                        Vector3 rel = origin - _center;
                        Vector3 localOrigin = new(Vector3.Dot(rel, _axisX), Vector3.Dot(rel, _axisY), Vector3.Dot(rel, _axisZ));
                        Vector3 localDir = new(
                            Vector3.Dot(direction, _axisX),
                            Vector3.Dot(direction, _axisY),
                            Vector3.Dot(direction, _axisZ));
                        return RaycastCenteredAabb(localOrigin, localDir, expandedHalf, maxDistance, out t);
                    }
                    default:
                        t = 0f;
                        return false;
                }
            }

            private static bool QuaternionToAxes(float[]? rotation, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ)
            {
                axisX = Vector3.right;
                axisY = Vector3.up;
                axisZ = Vector3.forward;
                if (rotation == null || rotation.Length < 4)
                    return false;

                float x = rotation[0];
                float y = rotation[1];
                float z = rotation[2];
                float w = rotation[3];
                float lengthSquared = x * x + y * y + z * z + w * w;
                if (!float.IsFinite(lengthSquared) || lengthSquared <= CollisionEpsilon)
                    return false;

                float invLength = 1f / Mathf.Sqrt(lengthSquared);
                x *= invLength;
                y *= invLength;
                z *= invLength;
                w *= invLength;

                float xx = x * x;
                float yy = y * y;
                float zz = z * z;
                float xy = x * y;
                float xz = x * z;
                float yz = y * z;
                float wx = w * x;
                float wy = w * y;
                float wz = w * z;

                axisX = new Vector3(1f - 2f * (yy + zz), 2f * (xy + wz), 2f * (xz - wy));
                axisY = new Vector3(2f * (xy - wz), 1f - 2f * (xx + zz), 2f * (yz + wx));
                axisZ = new Vector3(2f * (xz + wy), 2f * (yz - wx), 1f - 2f * (xx + yy));
                return true;
            }
        }

        private sealed class ServerQueryMeshGeometry
        {
            private ServerQueryMeshGeometry(string id, Vector3[] vertices, int[] indices)
            {
                Id = id;
                Vertices = vertices;
                Indices = indices;
            }

            public string Id { get; }
            public Vector3[] Vertices { get; }
            public int[] Indices { get; }

            public static bool TryCreate(GameplayQueryMeshGeometryFile file, out ServerQueryMeshGeometry geometry)
            {
                geometry = null!;
                if (string.IsNullOrEmpty(file.id) ||
                    file.vertices == null ||
                    file.indices == null ||
                    file.vertices.Length < 9 ||
                    file.indices.Length < 3 ||
                    file.vertices.Length % 3 != 0 ||
                    file.indices.Length % 3 != 0)
                {
                    return false;
                }

                Vector3[] vertices = new Vector3[file.vertices.Length / 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    int offset = i * 3;
                    vertices[i] = new Vector3(file.vertices[offset], file.vertices[offset + 1], file.vertices[offset + 2]);
                }

                geometry = new ServerQueryMeshGeometry(file.id, vertices, file.indices);
                return true;
            }
        }

        private sealed class ServerQueryMeshInstance
        {
            private ServerQueryMeshInstance(string name, Vector3[] worldVertices, int[] indices, ServerBounds bounds)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "server query mesh" : name;
                _worldVertices = worldVertices;
                _indices = indices;
                _bounds = bounds;
            }

            private readonly Vector3[] _worldVertices;
            private readonly int[] _indices;
            private readonly ServerBounds _bounds;

            public string Name { get; }

            public static bool TryCreate(
                GameplayQueryMeshInstanceFile file,
                ServerQueryMeshGeometry geometry,
                out ServerQueryMeshInstance instance)
            {
                instance = null!;
                if (file.transform == null || file.transform.Length != 16)
                    return false;

                Vector3[] worldVertices = new Vector3[geometry.Vertices.Length];
                ServerBounds bounds = default;
                for (int i = 0; i < geometry.Vertices.Length; i++)
                {
                    Vector3 world = TransformPoint(file.transform, geometry.Vertices[i]);
                    worldVertices[i] = world;
                    bounds = i == 0 ? new ServerBounds(world, world) : bounds.Encapsulate(world);
                }

                instance = new ServerQueryMeshInstance(file.name, worldVertices, geometry.Indices, bounds);
                return true;
            }

            public bool TryRaycast(
                Vector3 origin,
                Vector3 direction,
                float maxDistance,
                float radius,
                ServerLosProbeHit? best,
                out float hitT)
            {
                hitT = 0f;
                float closest = best.HasValue ? Mathf.Min(best.Value.Distance, maxDistance) : maxDistance;
                if (!_bounds.Expanded(radius).Raycast(origin, direction, closest, out _))
                    return false;

                bool hit = false;
                for (int i = 0; i < _indices.Length; i += 3)
                {
                    int ia = _indices[i];
                    int ib = _indices[i + 1];
                    int ic = _indices[i + 2];
                    if ((uint)ia >= _worldVertices.Length ||
                        (uint)ib >= _worldVertices.Length ||
                        (uint)ic >= _worldVertices.Length)
                    {
                        continue;
                    }

                    float? t = RaycastSweptSphereTriangle(
                        origin,
                        direction,
                        Mathf.Max(radius, 0f),
                        _worldVertices[ia],
                        _worldVertices[ib],
                        _worldVertices[ic],
                        closest);
                    if (t.HasValue && t.Value >= 0f && t.Value <= closest)
                    {
                        closest = t.Value;
                        hitT = t.Value;
                        hit = true;
                    }
                }

                return hit;
            }
        }

        private readonly struct ServerBounds
        {
            public ServerBounds(Vector3 min, Vector3 max)
            {
                Min = min;
                Max = max;
            }

            private Vector3 Min { get; }
            private Vector3 Max { get; }

            public ServerBounds Encapsulate(Vector3 point)
            {
                return new ServerBounds(Vector3.Min(Min, point), Vector3.Max(Max, point));
            }

            public ServerBounds Expanded(float amount)
            {
                Vector3 expansion = Vector3.one * Mathf.Max(amount, 0f);
                return new ServerBounds(Min - expansion, Max + expansion);
            }

            public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float t)
            {
                return RaycastAabb(origin, direction, Min, Max, maxDistance, out t);
            }
        }

        private static Vector3 TransformPoint(float[] transform, Vector3 point)
        {
            return new Vector3(
                transform[0] * point.x + transform[1] * point.y + transform[2] * point.z + transform[3],
                transform[4] * point.x + transform[5] * point.y + transform[6] * point.z + transform[7],
                transform[8] * point.x + transform[9] * point.y + transform[10] * point.z + transform[11]);
        }

        private static bool RaycastCenteredAabb(
            Vector3 localOrigin,
            Vector3 localDirection,
            Vector3 half,
            float maxDistance,
            out float t)
        {
            return RaycastAabb(localOrigin, localDirection, -half, half, maxDistance, out t);
        }

        private static bool RaycastAabb(
            Vector3 origin,
            Vector3 direction,
            Vector3 min,
            Vector3 max,
            float maxDistance,
            out float t)
        {
            float tMin = 0f;
            float tMax = maxDistance;
            if (!RaycastAabbAxis(origin.x, direction.x, min.x, max.x, ref tMin, ref tMax) ||
                !RaycastAabbAxis(origin.y, direction.y, min.y, max.y, ref tMin, ref tMax) ||
                !RaycastAabbAxis(origin.z, direction.z, min.z, max.z, ref tMin, ref tMax))
            {
                t = 0f;
                return false;
            }

            t = tMin;
            return t <= maxDistance + CollisionEpsilon;
        }

        private static bool RaycastAabbAxis(
            float origin,
            float direction,
            float min,
            float max,
            ref float tMin,
            ref float tMax)
        {
            if (Mathf.Abs(direction) <= CollisionEpsilon)
                return origin >= min && origin <= max;

            float inv = 1f / direction;
            float t1 = (min - origin) * inv;
            float t2 = (max - origin) * inv;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            return tMin <= tMax + CollisionEpsilon;
        }

        private static float? RaycastSweptSphereTriangle(
            Vector3 origin,
            Vector3 direction,
            float radius,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float maxDistance)
        {
            if (!float.IsFinite(radius) || radius <= CollisionEpsilon)
                return RaycastTriangle(origin, direction, a, b, c, maxDistance);

            float? best = null;
            ConsiderHit(ref best, RaycastSweptSphereTriangleFace(origin, direction, radius, a, b, c, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastCapsuleSegment(origin, direction, a, b, radius, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastCapsuleSegment(origin, direction, b, c, radius, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastCapsuleSegment(origin, direction, c, a, radius, maxDistance), maxDistance);
            return best;
        }

        private static float? RaycastTriangle(
            Vector3 origin,
            Vector3 direction,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float maxDistance)
        {
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 pvec = Vector3.Cross(direction, edge2);
            float det = Vector3.Dot(edge1, pvec);
            if (!float.IsFinite(det) || Mathf.Abs(det) <= CollisionEpsilon)
                return null;

            float invDet = 1f / det;
            Vector3 tvec = origin - a;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0f || u > 1f)
                return null;

            Vector3 qvec = Vector3.Cross(tvec, edge1);
            float v = Vector3.Dot(direction, qvec) * invDet;
            if (v < 0f || u + v > 1f)
                return null;

            float t = Vector3.Dot(edge2, qvec) * invDet;
            return t >= 0f && t <= maxDistance ? t : null;
        }

        private static float? RaycastSweptSphereTriangleFace(
            Vector3 origin,
            Vector3 direction,
            float radius,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float maxDistance)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float normalLength = normal.magnitude;
            if (normalLength <= CollisionEpsilon)
                return null;

            Vector3 n = normal / normalLength;
            float startDistance = Vector3.Dot(origin - a, n);
            float denom = Vector3.Dot(direction, n);
            float? best = null;

            if (Mathf.Abs(startDistance) <= radius + CollisionEpsilon)
            {
                // Match the server exactly (world_collision.rs face test): a
                // plane-proximity start only counts when the projected point
                // lies INSIDE the triangle. Without this check, every large
                // triangle's infinite plane reads as a t=0 block from anywhere
                // in the world — the "open sand says no line of sight" bug.
                Vector3 projectedStart = origin - n * startDistance;
                if (PointInTriangle(projectedStart, a, b, c))
                    ConsiderHit(ref best, 0f, maxDistance);
            }

            if (Mathf.Abs(denom) > CollisionEpsilon)
            {
                ConsiderFaceCandidate(ref best, origin, direction, radius, a, b, c, n, startDistance, denom, -radius, maxDistance);
                ConsiderFaceCandidate(ref best, origin, direction, radius, a, b, c, n, startDistance, denom, radius, maxDistance);
            }

            return best;
        }

        private static void ConsiderFaceCandidate(
            ref float? best,
            Vector3 origin,
            Vector3 direction,
            float radius,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 normal,
            float startDistance,
            float denom,
            float targetDistance,
            float maxDistance)
        {
            _ = radius;
            float t = (targetDistance - startDistance) / denom;
            if (t < -CollisionEpsilon || t > maxDistance + CollisionEpsilon)
                return;

            t = Mathf.Max(0f, t);
            Vector3 center = origin + direction * t;
            float signedDistance = Vector3.Dot(center - a, normal);
            Vector3 projected = center - normal * signedDistance;
            if (PointInTriangle(projected, a, b, c))
                ConsiderHit(ref best, t, maxDistance);
        }

        private static float? RaycastCapsuleSegment(
            Vector3 origin,
            Vector3 direction,
            Vector3 a,
            Vector3 b,
            float radius,
            float maxDistance)
        {
            Vector3 ba = b - a;
            Vector3 oa = origin - a;
            float baba = Vector3.Dot(ba, ba);
            if (baba <= CollisionEpsilon)
                return RaycastSphere(origin, direction, a, radius, maxDistance);

            float bard = Vector3.Dot(ba, direction);
            float baoa = Vector3.Dot(ba, oa);
            float rdoa = Vector3.Dot(direction, oa);
            float oaoa = Vector3.Dot(oa, oa);
            float cylA = baba - bard * bard;
            float cylB = baba * rdoa - baoa * bard;
            float cylC = baba * oaoa - baoa * baoa - radius * radius * baba;

            float? best = null;
            if (Mathf.Abs(cylA) > CollisionEpsilon)
            {
                float h = cylB * cylB - cylA * cylC;
                if (h >= 0f)
                {
                    float t = (-cylB - Mathf.Sqrt(h)) / cylA;
                    float y = baoa + t * bard;
                    if (y > 0f && y < baba)
                        ConsiderHit(ref best, t, maxDistance);
                }
            }

            ConsiderHit(ref best, RaycastSphere(origin, direction, a, radius, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastSphere(origin, direction, b, radius, maxDistance), maxDistance);
            return best;
        }

        private static float? RaycastSphere(
            Vector3 origin,
            Vector3 direction,
            Vector3 center,
            float radius,
            float maxDistance)
        {
            Vector3 oc = origin - center;
            float b = Vector3.Dot(oc, direction);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            if (c <= 0f)
                return 0f;

            float h = b * b - c;
            if (h < 0f)
                return null;

            float t = -b - Mathf.Sqrt(h);
            return t >= 0f && t <= maxDistance ? t : null;
        }

        private static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = c - a;
            Vector3 v1 = b - a;
            Vector3 v2 = p - a;
            float dot00 = Vector3.Dot(v0, v0);
            float dot01 = Vector3.Dot(v0, v1);
            float dot02 = Vector3.Dot(v0, v2);
            float dot11 = Vector3.Dot(v1, v1);
            float dot12 = Vector3.Dot(v1, v2);
            float denom = dot00 * dot11 - dot01 * dot01;
            if (Mathf.Abs(denom) <= CollisionEpsilon)
                return false;

            float invDenom = 1f / denom;
            float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
            float v = (dot00 * dot12 - dot01 * dot02) * invDenom;
            return u >= -CollisionEpsilon && v >= -CollisionEpsilon && u + v <= 1f + CollisionEpsilon;
        }

        private static void ConsiderHit(ref float? best, float? candidate, float maxDistance)
        {
            if (!candidate.HasValue)
                return;

            ConsiderHit(ref best, candidate.Value, maxDistance);
        }

        private static void ConsiderHit(ref float? best, float candidate, float maxDistance)
        {
            if (!float.IsFinite(candidate) || candidate < -CollisionEpsilon || candidate > maxDistance + CollisionEpsilon)
                return;

            candidate = Mathf.Max(0f, candidate);
            if (!best.HasValue || candidate < best.Value)
                best = candidate;
        }
    }
}
