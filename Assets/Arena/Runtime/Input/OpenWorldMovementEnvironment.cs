#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Arena.World;

namespace Arena.World
{
    public readonly struct OpenWorldSceneProfile
    {
        private const string OasisDaySceneName = "Oasis_Day";
        private const string AdventureIslandSceneName = "Adventure_Island";
        private const string DesertDaySceneName = "Desert_Day";
        private const string DocksDaySceneName = "Docks_Day";
        private const string GiantSkeletonSceneName = "Giant_Skeleton";
        private const string GoldenValleyOvercastSceneName = "Golden_Valley_Overcast";
        private const string GoldenValleySunnySceneName = "Golden_Valley_Sunny";
        private const string GreatHallDaySceneName = "Great_Hall_Day";
        private const string IdolDaySceneName = "Idol_Day";
        private const string TempleGardensSceneName = "Temple_Gardens";

        private OpenWorldSceneProfile(
            string sceneName,
            string dataKey,
            float groundY,
            float spawnYawDegrees,
            Vector3 spawnPosition,
            bool useProceduralFallbackColliders = true)
        {
            SceneName = sceneName;
            DataKey = dataKey;
            GroundY = groundY;
            SpawnYawDegrees = spawnYawDegrees;
            SpawnPosition = spawnPosition;
            UseProceduralFallbackColliders = useProceduralFallbackColliders;
            HeightfieldResourcePath = $"SharedData/Worlds/{dataKey}.heightfield.shared";
            CollisionResourcePath = $"SharedData/Worlds/{dataKey}.collision.shared";
        }

        public string SceneName { get; }
        public string DataKey { get; }
        public float GroundY { get; }
        public float SpawnYawDegrees { get; }
        public string HeightfieldResourcePath { get; }
        public string CollisionResourcePath { get; }
        public Vector3 SpawnPosition { get; }
        public bool UseProceduralFallbackColliders { get; }

        public static OpenWorldSceneProfile OasisDay { get; } = new(
            OasisDaySceneName,
            "oasis_day",
            12.358f,
            0.0f,
            new Vector3(62.22f, 12.358f, 79.47f));

        public static OpenWorldSceneProfile AdventureIsland { get; } = new(
            AdventureIslandSceneName,
            "adventure_island",
            0.0f,
            0.0f,
            new Vector3(-176.3932f, 0.0f, 77.95966f));

        public static OpenWorldSceneProfile DesertDay { get; } = new(
            DesertDaySceneName,
            "desert_day",
            1.8f,
            0.0f,
            new Vector3(-35.535f, 1.8f, 11.47f));

        public static OpenWorldSceneProfile DocksDay { get; } = new(
            DocksDaySceneName,
            "docks_day",
            52.608f,
            0.0f,
            new Vector3(413.772f, 52.608f, 370.805f));

        public static OpenWorldSceneProfile GiantSkeleton { get; } = new(
            GiantSkeletonSceneName,
            "giant_skeleton",
            8.996f,
            0.0f,
            new Vector3(24.946f, 8.996f, -87.789f));

        public static OpenWorldSceneProfile GoldenValleyOvercast { get; } = new(
            GoldenValleyOvercastSceneName,
            "golden_valley_sunny",
            86.013f,
            0.0f,
            new Vector3(356.842f, 86.013f, 330.988f));

        public static OpenWorldSceneProfile GoldenValleySunny { get; } = new(
            GoldenValleySunnySceneName,
            "golden_valley_sunny",
            86.013f,
            0.0f,
            new Vector3(356.842f, 86.013f, 330.988f));

        public static OpenWorldSceneProfile GreatHallDay { get; } = new(
            GreatHallDaySceneName,
            "great_hall_day",
            -5.94f,
            0.0f,
            new Vector3(27.34f, -5.94f, -3.661f),
            useProceduralFallbackColliders: false);

        public static OpenWorldSceneProfile IdolDay { get; } = new(
            IdolDaySceneName,
            "idol_day",
            70.01f,
            0.0f,
            new Vector3(328.09f, 70.01f, 233.949f));

        public static OpenWorldSceneProfile TempleGardens { get; } = new(
            TempleGardensSceneName,
            "temple_gardens",
            10.0709f,
            0.0f,
            new Vector3(-1.06860f, 10.0709f, 129.2195f),
            useProceduralFallbackColliders: false);

        public static OpenWorldSceneProfile ForSceneName(string? sceneName)
            => sceneName switch
            {
                AdventureIslandSceneName => AdventureIsland,
                DesertDaySceneName => DesertDay,
                DocksDaySceneName => DocksDay,
                GiantSkeletonSceneName => GiantSkeleton,
                GoldenValleyOvercastSceneName => GoldenValleyOvercast,
                GoldenValleySunnySceneName => GoldenValleySunny,
                GreatHallDaySceneName => GreatHallDay,
                IdolDaySceneName => IdolDay,
                TempleGardensSceneName => TempleGardens,
                _ => OasisDay,
            };
    }
}

namespace Arena.Input
{
    /// <summary>
    /// Client-side open-world movement environment intended to match the current
    /// server-side flat-ground + deterministic obstacle rules closely enough for
    /// local prediction and replay.
    /// </summary>
    public sealed class OpenWorldMovementEnvironment : IMovementEnvironment
    {
        private const float CollisionEpsilon = 0.0001f;
        private const float SurfaceSnapUp = 1.2f;
        private const float GameplayBoxStepUpHeight = 0.35f;
        private const float WalkableTopEpsilon = 0.05f;

        private const uint DefaultSeed = 614670171;
        private const float LegacyWorldSize = 320.0f;
        private const float OpenWorldDecorMargin = 8.0f;
        private const int OpenWorldCollisionIters = 2;

        private const int OpenWorldTreeCount = 64;
        private const int OpenWorldTreeMaxAttempts = 2200;
        private const float OpenWorldTreeClearingRadius = 32.0f;
        private const float OpenWorldTreeRadiusMin = 0.88f;
        private const float OpenWorldTreeRadiusMax = 1.48f;
        private const float OpenWorldTreeMinSpacing = 1.4f;
        private const float OpenWorldTreeOccupancyPadding = 0.5f;
        private const float OpenWorldTreeHeightMin = 7.0f;
        private const float OpenWorldTreeHeightMax = 13.8f;

        private const int OpenWorldRockCount = 46;
        private const int OpenWorldRockMaxAttempts = 1800;
        private const float OpenWorldRockClearingRadius = 20.0f;
        private const float OpenWorldRockRadiusMin = 0.9f;
        private const float OpenWorldRockRadiusMax = 1.9f;
        private const float OpenWorldRockHeightMin = 0.72f;
        private const float OpenWorldRockHeightMax = 1.78f;
        private const float OpenWorldRockMinSpacing = 0.9f;
        private const float OpenWorldRockOccupancyPadding = 0.3f;

        private const float OpenWorldTreeOccupiedRadiusMax =
            OpenWorldTreeRadiusMax + OpenWorldTreeOccupancyPadding;
        private const float OpenWorldRockOccupiedRadiusMax =
            OpenWorldRockRadiusMax + OpenWorldRockOccupancyPadding;
        private const float OpenWorldOccupancyMaxRadius =
            OpenWorldTreeOccupiedRadiusMax > OpenWorldRockOccupiedRadiusMax
                ? OpenWorldTreeOccupiedRadiusMax
                : OpenWorldRockOccupiedRadiusMax;
        private const float OpenWorldOccupancyCellSize = 4.0f;

        private const ulong HashA = 0x9e37_79b9_7f4a_7c15;
        private const ulong HashB = 0xc2b2_ae3d_27d4_eb4f;

#pragma warning disable CS0649
        [Serializable]
        private sealed class GameplayCollisionLayoutFile
        {
            public int version = 1;
            public GameplayCollisionBoxFile[] boxes = Array.Empty<GameplayCollisionBoxFile>();
        }

        [Serializable]
        private sealed class GameplayCollisionBoxFile
        {
            public string shape = "obb_y";
            public float[] center = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public float rotation_y_deg;
        }

        [Serializable]
        private sealed class OpenWorldHeightfieldFile
        {
            public int version = 1;
            public float[] origin = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public int resolution_x;
            public int resolution_z;
            public float[] heights = Array.Empty<float>();
        }
#pragma warning restore CS0649

        private readonly struct OpenWorldHeightfield
        {
            public OpenWorldHeightfield(
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

            public float OriginX { get; }
            public float OriginY { get; }
            public float OriginZ { get; }
            public float SizeX { get; }
            public float SizeY { get; }
            public float SizeZ { get; }
            public int ResolutionX { get; }
            public int ResolutionZ { get; }
            public float[] Heights { get; }
            public float MinX => OriginX;
            public float MaxX => OriginX + SizeX;
            public float MinZ => OriginZ;
            public float MaxZ => OriginZ + SizeZ;
            public bool IsValid =>
                ResolutionX >= 2 &&
                ResolutionZ >= 2 &&
                Heights != null &&
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

        private enum OpenWorldColliderShape
        {
            Circle,
            RadialSlope,
        }

        private readonly struct OpenWorldCollider
        {
            public OpenWorldCollider(
                OpenWorldColliderShape shape,
                float centerX,
                float centerZ,
                float radius,
                float radiusTop,
                float yMin,
                float yMax,
                bool walkableTop)
            {
                Shape = shape;
                CenterX = centerX;
                CenterZ = centerZ;
                Radius = radius;
                RadiusTop = radiusTop;
                YMin = yMin;
                YMax = yMax;
                WalkableTop = walkableTop;
            }

            public OpenWorldColliderShape Shape { get; }
            public float CenterX { get; }
            public float CenterZ { get; }
            public float Radius { get; }
            public float RadiusTop { get; }
            public float YMin { get; }
            public float YMax { get; }
            public bool WalkableTop { get; }
        }

        private readonly struct GameplayCollisionBox
        {
            public GameplayCollisionBox(
                bool isAabb,
                float centerX,
                float centerY,
                float centerZ,
                float halfX,
                float halfY,
                float halfZ,
                float sinY,
                float cosY)
            {
                IsAabb = isAabb;
                CenterX = centerX;
                CenterY = centerY;
                CenterZ = centerZ;
                HalfX = halfX;
                HalfY = halfY;
                HalfZ = halfZ;
                SinY = sinY;
                CosY = cosY;
            }

            public bool IsAabb { get; }
            public float CenterX { get; }
            public float CenterY { get; }
            public float CenterZ { get; }
            public float HalfX { get; }
            public float HalfY { get; }
            public float HalfZ { get; }
            public float SinY { get; }
            public float CosY { get; }
        }

        private readonly struct OpenWorldDisc
        {
            public OpenWorldDisc(float x, float z, float radius)
            {
                X = x;
                Z = z;
                Radius = radius;
            }

            public float X { get; }
            public float Z { get; }
            public float Radius { get; }
        }

        private sealed class OpenWorldOccupancy
        {
            private readonly List<OpenWorldDisc> _discs;
            private readonly Dictionary<(int, int), List<int>> _buckets;

            public OpenWorldOccupancy(int capacity)
            {
                _discs = new List<OpenWorldDisc>(capacity);
                _buckets = new Dictionary<(int, int), List<int>>(capacity * 2);
            }

            public void Insert(OpenWorldDisc disc)
            {
                int index = _discs.Count;
                (int cellX, int cellZ) = GridCell(disc.X, disc.Z);
                _discs.Add(disc);
                if (!_buckets.TryGetValue((cellX, cellZ), out List<int>? indices))
                {
                    indices = new List<int>();
                    _buckets[(cellX, cellZ)] = indices;
                }
                indices.Add(index);
            }

            public bool IsFree(float x, float z, float radius)
            {
                (int cellX, int cellZ) = GridCell(x, z);
                float searchRadius = radius + OpenWorldOccupancyMaxRadius;
                int cellSteps = Mathf.CeilToInt(searchRadius / OpenWorldOccupancyCellSize);

                for (int cx = cellX - cellSteps; cx <= cellX + cellSteps; cx++)
                {
                    for (int cz = cellZ - cellSteps; cz <= cellZ + cellSteps; cz++)
                    {
                        if (!_buckets.TryGetValue((cx, cz), out List<int>? indices))
                            continue;

                        foreach (int index in indices)
                        {
                            OpenWorldDisc disc = _discs[index];
                            float dx = x - disc.X;
                            float dz = z - disc.Z;
                            float minDistance = radius + disc.Radius;
                            if (dx * dx + dz * dz < minDistance * minDistance)
                                return false;
                        }
                    }
                }

                return true;
            }
        }

        private sealed class OpenWorldRng
        {
            private uint _state;

            public OpenWorldRng(uint seed)
            {
                _state = seed;
            }

            public float NextF32()
            {
                unchecked
                {
                    _state = _state * 1_664_525u + 1_013_904_223u;
                }
                return (float)((double)_state / 4_294_967_296.0);
            }

            public float RandomRange(float min, float max)
            {
                return min + (max - min) * NextF32();
            }
        }

        private static readonly Lazy<OpenWorldMovementEnvironment> SharedLazy =
            new(() => CreateForProfile(OpenWorldSceneProfile.OasisDay));

        private static readonly Dictionary<string, OpenWorldMovementEnvironment> EnvironmentsBySceneName =
            new(StringComparer.Ordinal);

        private readonly OpenWorldSceneProfile _profile;
        private readonly OpenWorldCollider[] _openWorldColliders;
        private readonly GameplayCollisionBox[] _gameplayBoxes;
        private readonly OpenWorldHeightfield? _heightfield;

        public static OpenWorldMovementEnvironment Shared => SharedLazy.Value;

        public static OpenWorldMovementEnvironment SharedForScene(string? sceneName)
        {
            OpenWorldSceneProfile profile = OpenWorldSceneProfile.ForSceneName(sceneName);
            if (string.Equals(profile.SceneName, OpenWorldSceneProfile.OasisDay.SceneName, StringComparison.Ordinal))
                return Shared;

            if (EnvironmentsBySceneName.TryGetValue(profile.SceneName, out OpenWorldMovementEnvironment? environment))
                return environment;

            environment = CreateForProfile(profile);
            EnvironmentsBySceneName[profile.SceneName] = environment;
            return environment;
        }

        private OpenWorldMovementEnvironment(
            OpenWorldSceneProfile profile,
            OpenWorldCollider[] openWorldColliders,
            GameplayCollisionBox[] gameplayBoxes,
            OpenWorldHeightfield? heightfield)
        {
            _profile = profile;
            _openWorldColliders = openWorldColliders;
            _gameplayBoxes = gameplayBoxes;
            _heightfield = heightfield;
        }

        public float SampleGroundHeight(float x, float z, float probeY)
        {
            float surface = _heightfield?.SampleHeight(x, z) ?? _profile.GroundY;
            float ceiling = probeY + SurfaceSnapUp;
            float gameplayStepCeiling = probeY + GameplayBoxStepUpHeight;

            foreach (OpenWorldCollider collider in _openWorldColliders)
            {
                if (!collider.WalkableTop || collider.YMax > ceiling)
                    continue;

                switch (collider.Shape)
                {
                    case OpenWorldColliderShape.Circle:
                    {
                        float dx = x - collider.CenterX;
                        float dz = z - collider.CenterZ;
                        if (dx * dx + dz * dz <= collider.Radius * collider.Radius && collider.YMax > surface)
                            surface = collider.YMax;
                        break;
                    }
                    case OpenWorldColliderShape.RadialSlope:
                    {
                        float dx = x - collider.CenterX;
                        float dz = z - collider.CenterZ;
                        float radius = Mathf.Sqrt(dx * dx + dz * dz);
                        float? y = SlopeSurfaceHeightFromRadius(
                            collider.YMin,
                            collider.YMax,
                            collider.Radius,
                            collider.RadiusTop,
                            radius);
                        if (y.HasValue && y.Value > surface)
                            surface = y.Value;
                        break;
                    }
                }
            }

            foreach (GameplayCollisionBox collider in _gameplayBoxes)
            {
                float topY = collider.CenterY + collider.HalfY;
                if (topY > gameplayStepCeiling)
                    continue;
                if (!GameplayBoxContainsPoint2D(collider, x, z))
                    continue;
                if (topY > surface)
                    surface = topY;
            }

            return surface;
        }

        public Vector2 ResolveHorizontalCollision(
            float desiredX,
            float desiredZ,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            float outX = desiredX;
            float outZ = desiredZ;
            GetPlayableBounds(playerRadius, out float minX, out float maxX, out float minZ, out float maxZ);
            outX = Mathf.Clamp(outX, minX, maxX);
            outZ = Mathf.Clamp(outZ, minZ, maxZ);

            for (int i = 0; i < OpenWorldCollisionIters; i++)
            {
                foreach (OpenWorldCollider collider in _openWorldColliders)
                {
                    if (!ColliderOverlapsPlayerBand(collider, currentY, playerHeight))
                        continue;

                    switch (collider.Shape)
                    {
                        case OpenWorldColliderShape.Circle:
                            (outX, outZ) = PushOutCircle2D(
                                outX,
                                outZ,
                                collider.CenterX,
                                collider.CenterZ,
                                collider.Radius + playerRadius);
                            break;

                        case OpenWorldColliderShape.RadialSlope:
                        {
                            float dx = outX - collider.CenterX;
                            float dz = outZ - collider.CenterZ;
                            float planarRadius = Mathf.Sqrt(dx * dx + dz * dz);
                            float? surfaceY = SlopeSurfaceHeightFromRadius(
                                collider.YMin,
                                collider.YMax,
                                collider.Radius,
                                collider.RadiusTop,
                                planarRadius);
                            if (surfaceY.HasValue && surfaceY.Value <= currentY + SurfaceSnapUp)
                                break;
                            if (!surfaceY.HasValue)
                                break;

                            float blockRadius = SlopeRadiusAtY(
                                collider.YMin,
                                collider.YMax,
                                collider.Radius,
                                collider.RadiusTop,
                                currentY);

                            (outX, outZ) = PushOutCircle2D(
                                outX,
                                outZ,
                                collider.CenterX,
                                collider.CenterZ,
                                blockRadius + playerRadius);
                            break;
                        }
                    }
                }

                outX = Mathf.Clamp(outX, minX, maxX);
                outZ = Mathf.Clamp(outZ, minZ, maxZ);
            }

            return ResolveGameplayHorizontalCollision(outX, outZ, playerRadius, playerHeight, currentY);
        }

        private Vector2 ResolveGameplayHorizontalCollision(
            float x,
            float z,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            float outX = x;
            float outZ = z;

            for (int i = 0; i < OpenWorldCollisionIters; i++)
            {
                foreach (GameplayCollisionBox collider in _gameplayBoxes)
                {
                    if (!GameplayBoxOverlapsPlayerBand(collider, currentY, playerHeight))
                        continue;
                    if (GameplayBoxCanStepUp(collider, currentY))
                        continue;

                    (outX, outZ) = collider.IsAabb
                        ? PushOutAabb2D(
                            outX,
                            outZ,
                            collider.CenterX,
                            collider.CenterZ,
                            collider.HalfX,
                            collider.HalfZ,
                            playerRadius)
                        : PushOutObbY2D(
                            outX,
                            outZ,
                            collider.CenterX,
                            collider.CenterZ,
                            collider.HalfX,
                            collider.HalfZ,
                            playerRadius,
                            collider.SinY,
                            collider.CosY);
                }
            }

            return new Vector2(outX, outZ);
        }

        private void GetPlayableBounds(float playerRadius, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            if (_heightfield.HasValue && _heightfield.Value.IsValid)
            {
                OpenWorldHeightfield heightfield = _heightfield.Value;
                minX = heightfield.MinX + playerRadius;
                maxX = heightfield.MaxX - playerRadius;
                minZ = heightfield.MinZ + playerRadius;
                maxZ = heightfield.MaxZ - playerRadius;
                return;
            }

            float maxExtent = Mathf.Max(LegacyWorldSize * 0.5f - playerRadius, 0.0f);
            minX = -maxExtent;
            maxX = maxExtent;
            minZ = -maxExtent;
            maxZ = maxExtent;
        }

        private static OpenWorldMovementEnvironment CreateForProfile(OpenWorldSceneProfile profile)
        {
            OpenWorldHeightfield? heightfield = LoadOpenWorldHeightfield(profile);
            return new OpenWorldMovementEnvironment(
                profile,
                GenerateOpenWorldColliders(profile, heightfield),
                LoadGameplayCollisionBoxes(profile),
                heightfield);
        }

        private static OpenWorldCollider[] GenerateOpenWorldColliders(
            OpenWorldSceneProfile profile,
            OpenWorldHeightfield? heightfield)
        {
            if (heightfield.HasValue && heightfield.Value.IsValid)
                return Array.Empty<OpenWorldCollider>();
            if (!profile.UseProceduralFallbackColliders)
                return Array.Empty<OpenWorldCollider>();

            OpenWorldRng rng = new(DefaultSeed);
            OpenWorldOccupancy occupied = new(OpenWorldTreeCount + OpenWorldRockCount);
            List<OpenWorldCollider> colliders = new(OpenWorldTreeCount + OpenWorldRockCount);

            EmitOpenWorldTreeColliders(profile, colliders, rng, occupied);
            EmitOpenWorldRockColliders(profile, colliders, rng, occupied);

            return colliders.ToArray();
        }

        private static OpenWorldHeightfield? LoadOpenWorldHeightfield(OpenWorldSceneProfile profile)
        {
            if (!MovementSharedDataLoader.TryLoadJson<OpenWorldHeightfieldFile>(
                profile.HeightfieldResourcePath,
                out OpenWorldHeightfieldFile? file) ||
                file == null ||
                file.origin == null ||
                file.origin.Length != 3 ||
                file.size == null ||
                file.size.Length != 3)
            {
                return null;
            }

            var heightfield = new OpenWorldHeightfield(
                file.origin[0],
                file.origin[1],
                file.origin[2],
                file.size[0],
                file.size[1],
                file.size[2],
                file.resolution_x,
                file.resolution_z,
                file.heights ?? Array.Empty<float>());

            return heightfield.IsValid ? heightfield : null;
        }

        private static GameplayCollisionBox[] LoadGameplayCollisionBoxes(OpenWorldSceneProfile profile)
        {
            GameplayCollisionLayoutFile file =
                MovementSharedDataLoader.LoadRequiredJson<GameplayCollisionLayoutFile>(
                    profile.CollisionResourcePath,
                    $"{profile.DataKey} gameplay collision");
            GameplayCollisionBoxFile[] files = file.boxes ?? Array.Empty<GameplayCollisionBoxFile>();
            var boxes = new GameplayCollisionBox[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                GameplayCollisionBoxFile boxFile = files[i];

                if (boxFile.center == null || boxFile.center.Length != 3 ||
                    boxFile.size == null || boxFile.size.Length != 3)
                {
                    throw new InvalidOperationException(
                        "[OpenWorldMovementEnvironment] gameplay collision asset contains a box with an invalid center/size payload.");
                }

                float centerX = boxFile.center[0];
                float centerY = boxFile.center[1];
                float centerZ = boxFile.center[2];
                float halfX = Mathf.Abs(boxFile.size[0]) * 0.5f;
                float halfY = Mathf.Abs(boxFile.size[1]) * 0.5f;
                float halfZ = Mathf.Abs(boxFile.size[2]) * 0.5f;

                if (boxFile.shape == "aabb")
                {
                    boxes[i] = new GameplayCollisionBox(
                        true,
                        centerX,
                        centerY,
                        centerZ,
                        halfX,
                        halfY,
                        halfZ,
                        0.0f,
                        1.0f);
                }
                else
                {
                    float yaw = boxFile.rotation_y_deg * Mathf.Deg2Rad;
                    boxes[i] = new GameplayCollisionBox(
                        false,
                        centerX,
                        centerY,
                        centerZ,
                        halfX,
                        halfY,
                        halfZ,
                        Mathf.Sin(yaw),
                        Mathf.Cos(yaw));
                }
            }

            return boxes;
        }

        private static void EmitOpenWorldTreeColliders(
            OpenWorldSceneProfile profile,
            List<OpenWorldCollider> colliders,
            OpenWorldRng rng,
            OpenWorldOccupancy occupied)
        {
            int placed = 0;
            int attempts = 0;

            while (placed < OpenWorldTreeCount && attempts < OpenWorldTreeMaxAttempts)
            {
                attempts++;
                float halfSize = LegacyWorldSize * 0.5f;
                float x = rng.RandomRange(-halfSize + OpenWorldDecorMargin, halfSize - OpenWorldDecorMargin);
                float z = rng.RandomRange(-halfSize + OpenWorldDecorMargin, halfSize - OpenWorldDecorMargin);
                float treeRadius = rng.RandomRange(OpenWorldTreeRadiusMin, OpenWorldTreeRadiusMax);

                if (Mathf.Sqrt(x * x + z * z) < OpenWorldTreeClearingRadius)
                    continue;
                if (!occupied.IsFree(x, z, treeRadius + OpenWorldTreeMinSpacing))
                    continue;

                float normalized = InverseLerp(OpenWorldTreeRadiusMin, OpenWorldTreeRadiusMax, treeRadius);
                float treeHeight = OpenWorldTreeHeightMin +
                    (OpenWorldTreeHeightMax - OpenWorldTreeHeightMin) * normalized;

                colliders.Add(new OpenWorldCollider(
                    OpenWorldColliderShape.Circle,
                    x,
                    z,
                    treeRadius,
                    treeRadius,
                    profile.GroundY,
                    profile.GroundY + treeHeight,
                    false));
                occupied.Insert(new OpenWorldDisc(x, z, treeRadius + OpenWorldTreeOccupancyPadding));
                placed++;
            }
        }

        private static void EmitOpenWorldRockColliders(
            OpenWorldSceneProfile profile,
            List<OpenWorldCollider> colliders,
            OpenWorldRng rng,
            OpenWorldOccupancy occupied)
        {
            int placed = 0;
            int attempts = 0;

            while (placed < OpenWorldRockCount && attempts < OpenWorldRockMaxAttempts)
            {
                attempts++;
                float halfSize = LegacyWorldSize * 0.5f;
                float x = rng.RandomRange(-halfSize + OpenWorldDecorMargin, halfSize - OpenWorldDecorMargin);
                float z = rng.RandomRange(-halfSize + OpenWorldDecorMargin, halfSize - OpenWorldDecorMargin);
                float rockRadius = rng.RandomRange(OpenWorldRockRadiusMin, OpenWorldRockRadiusMax);
                float rockHeight = rng.RandomRange(OpenWorldRockHeightMin, OpenWorldRockHeightMax);

                if (Mathf.Sqrt(x * x + z * z) < OpenWorldRockClearingRadius)
                    continue;
                if (!occupied.IsFree(x, z, rockRadius + OpenWorldRockMinSpacing))
                    continue;

                colliders.Add(new OpenWorldCollider(
                    OpenWorldColliderShape.RadialSlope,
                    x,
                    z,
                    rockRadius,
                    Mathf.Max(rockRadius * 0.24f, 0.18f),
                    profile.GroundY,
                    profile.GroundY + rockHeight,
                    true));
                occupied.Insert(new OpenWorldDisc(x, z, rockRadius + OpenWorldRockOccupancyPadding));
                placed++;
            }
        }

        private static float InverseLerp(float min, float max, float value)
        {
            if (Mathf.Abs(max - min) <= CollisionEpsilon)
                return 0.0f;
            return Mathf.Clamp01((value - min) / (max - min));
        }

        private static (int, int) GridCell(float x, float z)
        {
            return (
                Mathf.FloorToInt(x / OpenWorldOccupancyCellSize),
                Mathf.FloorToInt(z / OpenWorldOccupancyCellSize));
        }

        private static bool ColliderOverlapsPlayerBand(OpenWorldCollider collider, float footY, float playerHeight)
        {
            if (collider.WalkableTop && footY >= collider.YMax - WalkableTopEpsilon)
                return false;

            float headY = footY + Mathf.Max(playerHeight, 0.1f);
            return headY > collider.YMin + CollisionEpsilon &&
                   footY < collider.YMax - CollisionEpsilon;
        }

        private static bool GameplayBoxOverlapsPlayerBand(GameplayCollisionBox collider, float footY, float playerHeight)
        {
            float headY = footY + Mathf.Max(playerHeight, 0.1f);
            return headY > collider.CenterY - collider.HalfY + CollisionEpsilon &&
                   footY < collider.CenterY + collider.HalfY - CollisionEpsilon;
        }

        private static bool GameplayBoxCanStepUp(GameplayCollisionBox collider, float footY)
        {
            float topY = collider.CenterY + collider.HalfY;
            return topY <= footY + GameplayBoxStepUpHeight;
        }

        private static bool GameplayBoxContainsPoint2D(GameplayCollisionBox collider, float x, float z)
        {
            if (collider.IsAabb)
            {
                return Mathf.Abs(x - collider.CenterX) <= collider.HalfX + CollisionEpsilon &&
                       Mathf.Abs(z - collider.CenterZ) <= collider.HalfZ + CollisionEpsilon;
            }

            float relX = x - collider.CenterX;
            float relZ = z - collider.CenterZ;
            float localX = relX * collider.CosY - relZ * collider.SinY;
            float localZ = relX * collider.SinY + relZ * collider.CosY;
            return Mathf.Abs(localX) <= collider.HalfX + CollisionEpsilon &&
                   Mathf.Abs(localZ) <= collider.HalfZ + CollisionEpsilon;
        }

        private static float SlopeRadiusAtY(
            float yMin,
            float yMax,
            float radiusBottom,
            float radiusTop,
            float y)
        {
            float height = Mathf.Abs(yMax - yMin);
            if (height <= CollisionEpsilon)
                return Mathf.Max(radiusBottom, radiusTop);
            if (y <= yMin)
                return radiusBottom;
            if (y >= yMax)
                return radiusTop;

            float t = (y - yMin) / (yMax - yMin);
            return radiusBottom + (radiusTop - radiusBottom) * t;
        }

        private static float? SlopeSurfaceHeightFromRadius(
            float yMin,
            float yMax,
            float radiusBottom,
            float radiusTop,
            float radius)
        {
            float radialDelta = radiusTop - radiusBottom;
            if (Mathf.Abs(radialDelta) <= CollisionEpsilon)
                return radius <= radiusTop ? yMax : null;

            if (radialDelta < 0.0f)
            {
                if (radius > radiusBottom)
                    return null;
                if (radius <= radiusTop)
                    return yMax;

                float t = (radiusBottom - radius) / (radiusBottom - radiusTop);
                return yMin + (yMax - yMin) * t;
            }

            if (radius > radiusTop)
                return null;
            if (radius <= radiusBottom)
                return yMax;

            float positiveT = (radius - radiusBottom) / (radiusTop - radiusBottom);
            return yMin + (yMax - yMin) * positiveT;
        }

        private static (float, float) PushOutCircle2D(float x, float z, float cx, float cz, float radius)
        {
            float dx = x - cx;
            float dz = z - cz;
            float distSq = dx * dx + dz * dz;
            float radiusSq = radius * radius;
            if (distSq >= radiusSq)
                return (x, z);

            if (distSq <= CollisionEpsilon)
            {
                ulong hash = unchecked(
                    (ulong)FloatToUInt32Bits(cx) * HashA +
                    (ulong)FloatToUInt32Bits(cz) * HashB);
                hash = Math.Max(hash, 1UL);
                float angle = ((hash % 65_536UL) / 65_536.0f) * Mathf.PI * 2.0f;
                dx = Mathf.Cos(angle) * radius;
                dz = Mathf.Sin(angle) * radius;
            }

            float dist = Mathf.Max(Mathf.Sqrt(dx * dx + dz * dz), CollisionEpsilon);
            float push = Mathf.Max(radius - dist, 0.0f);
            float nx = dx / dist;
            float nz = dz / dist;
            return (x + nx * push, z + nz * push);
        }

        private static (float, float) PushOutObbY2D(
            float x,
            float z,
            float cx,
            float cz,
            float halfX,
            float halfZ,
            float padding,
            float sinY,
            float cosY)
        {
            float relX = x - cx;
            float relZ = z - cz;
            float localX = relX * cosY - relZ * sinY;
            float localZ = relX * sinY + relZ * cosY;

            (float pushedLocalX, float pushedLocalZ) =
                PushOutAabb2D(localX, localZ, 0.0f, 0.0f, halfX, halfZ, padding);

            float worldX = pushedLocalX * cosY + pushedLocalZ * sinY + cx;
            float worldZ = -pushedLocalX * sinY + pushedLocalZ * cosY + cz;
            return (worldX, worldZ);
        }

        private static (float, float) PushOutAabb2D(
            float x,
            float z,
            float cx,
            float cz,
            float halfX,
            float halfZ,
            float padding)
        {
            float expandedHalfX = halfX + padding;
            float expandedHalfZ = halfZ + padding;

            float dx = x - cx;
            float dz = z - cz;
            float absDx = Mathf.Abs(dx);
            float absDz = Mathf.Abs(dz);

            if (absDx >= expandedHalfX || absDz >= expandedHalfZ)
                return (x, z);

            float penX = expandedHalfX - absDx;
            float penZ = expandedHalfZ - absDz;

            if (penX < penZ)
            {
                float sign = dx >= 0.0f ? 1.0f : -1.0f;
                return (x + sign * penX, z);
            }

            float zSign = dz >= 0.0f ? 1.0f : -1.0f;
            return (x, z + zSign * penZ);
        }

        private static uint FloatToUInt32Bits(float value)
        {
            return unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        }
    }
}
