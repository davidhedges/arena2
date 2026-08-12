#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Arena.World;

namespace Arena.Input
{
    /// <summary>
    /// Seeded arena movement environment mirroring the server's arena terrain,
    /// walkable surfaces, horizontal blockers, and shared gameplay collision.
    /// </summary>
    public sealed class ArenaMovementEnvironment : IMovementEnvironment
    {
        private const float CollisionEpsilon = 0.0001f;
        private const float SurfaceSnapUp = 1.2f;
        private const ulong MixMulA = 0xff51_afd7_ed55_8ccdul;
        private const ulong MixMulB = 0xc4ce_b9fe_1a85_ec53ul;
        private const ulong HashXMul = 0x9e37_79b9_7f4a_7c15ul;
        private const ulong HashZMul = 0xc2b2_ae3d_27d4_eb4ful;
        private const ulong JsSafeSeedMask = (1ul << 53) - 1ul;
        private const float Tau = Mathf.PI * 2.0f;

        private const double NoiseLargeScale = 38.0;
        private const double NoiseMediumScale = 19.0;
        private const double NoiseSmallScale = 9.0;
        private const double NoiseLargeWeight = 3.2;
        private const double NoiseMediumWeight = 1.4;
        private const double NoiseSmallWeight = 0.6;

#pragma warning disable CS0649
        [Serializable]
        private sealed class ArenaLayoutWallSegmentFile
        {
            public float x;
            public float z;
            public float half_x;
            public float half_z;
        }

        [Serializable]
        private sealed class ArenaLayoutPointFile
        {
            public float x;
            public float z;
        }

        [Serializable]
        private sealed class ArenaLayoutRampFile
        {
            public float x;
            public float z;
            public float side;
        }

        [Serializable]
        private sealed class ArenaLayoutFile
        {
            public string map_id = string.Empty;
            public float ground_y;
            public float center_flatten_radius;
            public float ruin_wall_height;
            public ArenaLayoutWallSegmentFile[] ruin_wall_segments = Array.Empty<ArenaLayoutWallSegmentFile>();
            public float platform_half_x;
            public float platform_half_z;
            public float platform_center_height_offset;
            public float platform_half_height;
            public ArenaLayoutPointFile[] platforms = Array.Empty<ArenaLayoutPointFile>();
            public float ramp_half_x;
            public float ramp_half_z;
            public float ramp_center_height_offset;
            public float ramp_half_height;
            public float ramp_platform_link_x_offset;
            public float ramp_rot_z_abs;
            public float ramp_top_center_offset;
            public float ramp_top_slope_delta;
            public ArenaLayoutRampFile[] ramps = Array.Empty<ArenaLayoutRampFile>();
            public float pillar_collider_radius;
            public int pillar_count;
            public ulong pillar_hash_step;
        }

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
#pragma warning restore CS0649

        private readonly struct ArenaWall
        {
            public ArenaWall(float centerX, float centerY, float centerZ, float halfX, float halfY, float halfZ)
            {
                CenterX = centerX;
                CenterY = centerY;
                CenterZ = centerZ;
                HalfX = halfX;
                HalfY = halfY;
                HalfZ = halfZ;
            }

            public float CenterX { get; }
            public float CenterY { get; }
            public float CenterZ { get; }
            public float HalfX { get; }
            public float HalfY { get; }
            public float HalfZ { get; }
            public float BaseY => CenterY - HalfY;
            public float TopY => CenterY + HalfY;
        }

        private readonly struct ArenaPlatform
        {
            public ArenaPlatform(float centerX, float centerY, float centerZ, float halfX, float halfY, float halfZ)
            {
                CenterX = centerX;
                CenterY = centerY;
                CenterZ = centerZ;
                HalfX = halfX;
                HalfY = halfY;
                HalfZ = halfZ;
            }

            public float CenterX { get; }
            public float CenterY { get; }
            public float CenterZ { get; }
            public float HalfX { get; }
            public float HalfY { get; }
            public float HalfZ { get; }
            public float TopY => CenterY + HalfY;
        }

        private readonly struct ArenaRamp
        {
            public ArenaRamp(
                float centerX,
                float centerY,
                float centerZ,
                float side,
                float halfX,
                float halfZ,
                float topCenterOffset,
                float topSlopeDelta)
            {
                CenterX = centerX;
                CenterY = centerY;
                CenterZ = centerZ;
                Side = side;
                HalfX = halfX;
                HalfZ = halfZ;
                TopCenterOffset = topCenterOffset;
                TopSlopeDelta = topSlopeDelta;
            }

            public float CenterX { get; }
            public float CenterY { get; }
            public float CenterZ { get; }
            public float Side { get; }
            public float HalfX { get; }
            public float HalfZ { get; }
            public float TopCenterOffset { get; }
            public float TopSlopeDelta { get; }

            public float TopYAtX(float sampleX)
            {
                float t = Mathf.Clamp((sampleX - CenterX) / HalfX, -1.0f, 1.0f) * Side;
                return CenterY + TopCenterOffset + t * TopSlopeDelta;
            }
        }

        private readonly struct ArenaPillar
        {
            public ArenaPillar(float centerX, float centerY, float centerZ, float halfY, float colliderRadius)
            {
                CenterX = centerX;
                CenterY = centerY;
                CenterZ = centerZ;
                HalfY = halfY;
                ColliderRadius = colliderRadius;
            }

            public float CenterX { get; }
            public float CenterY { get; }
            public float CenterZ { get; }
            public float HalfY { get; }
            public float ColliderRadius { get; }
            public float BaseY => CenterY - HalfY;
            public float TopY => CenterY + HalfY;
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

        private static readonly Dictionary<string, Dictionary<ulong, ArenaMovementEnvironment>> SharedByMapAndSeed =
            new(StringComparer.Ordinal);

        private readonly ulong _seed;
        private readonly ArenaLayoutFile _layout;
        private readonly GameplayCollisionBox[] _gameplayBoxes;
        private readonly ArenaWall[] _walls;
        private readonly ArenaPlatform[] _platforms;
        private readonly ArenaRamp[] _ramps;
        private readonly ArenaPillar[] _pillars;

        public static ArenaMovementEnvironment Shared(ulong seed)
            => Shared(ArenaMapCatalog.DefaultMapId, seed);

        public static ArenaMovementEnvironment Shared(string mapId, ulong seed)
        {
            if (!ArenaMapCatalog.TryResolve(mapId, out ArenaMapProfile profile))
                throw new InvalidOperationException($"Unknown authored arena map '{mapId}'.");

            if (!SharedByMapAndSeed.TryGetValue(profile.MapId, out Dictionary<ulong, ArenaMovementEnvironment>? bySeed))
            {
                bySeed = new Dictionary<ulong, ArenaMovementEnvironment>();
                SharedByMapAndSeed[profile.MapId] = bySeed;
            }

            if (!bySeed.TryGetValue(seed, out ArenaMovementEnvironment? environment))
            {
                environment = new ArenaMovementEnvironment(profile, seed);
                bySeed[seed] = environment;
            }

            return environment;
        }

        private ArenaMovementEnvironment(ArenaMapProfile profile, ulong seed)
        {
            _seed = seed;
            _layout = LoadArenaLayout(profile);
            _gameplayBoxes = LoadGameplayCollisionBoxes(profile);
            _walls = BuildWalls(seed);
            _platforms = BuildPlatforms(seed);
            _ramps = BuildRamps(seed);
            _pillars = BuildPillars(seed);
        }

        public float SampleGroundHeight(float x, float z, float probeY)
        {
            float surface = HeightAt(_seed, x, z);
            float ceiling = probeY + SurfaceSnapUp;

            for (int i = 0; i < _platforms.Length; i++)
            {
                ArenaPlatform platform = _platforms[i];
                if (!InsideAabb2D(x, z, platform.CenterX, platform.CenterZ, platform.HalfX, platform.HalfZ))
                    continue;

                float top = platform.TopY;
                if (top <= ceiling && top > surface)
                    surface = top;
            }

            for (int i = 0; i < _ramps.Length; i++)
            {
                ArenaRamp ramp = _ramps[i];
                if (!InsideAabb2D(x, z, ramp.CenterX, ramp.CenterZ, ramp.HalfX, ramp.HalfZ))
                    continue;

                float top = ramp.TopYAtX(x);
                if (top <= ceiling && top > surface)
                    surface = top;
            }

            return surface;
        }

        public bool TrySampleGroundHeight(float x, float z, float probeY, out float groundY)
        {
            groundY = SampleGroundHeight(x, z, probeY);
            return true;
        }

        public Vector2 ResolveHorizontalCollision(
            float startX,
            float startZ,
            float desiredX,
            float desiredZ,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            float outX = desiredX;
            float outZ = desiredZ;

            for (int iter = 0; iter < 2; iter++)
            {
                for (int i = 0; i < _walls.Length; i++)
                {
                    ArenaWall wall = _walls[i];
                    if (!ColliderOverlapsPlayerBand(wall.BaseY, wall.TopY, currentY, playerHeight))
                        continue;

                    (outX, outZ) = PushOutAabb2D(
                        outX,
                        outZ,
                        wall.CenterX,
                        wall.CenterZ,
                        wall.HalfX,
                        wall.HalfZ,
                        playerRadius);
                }

                for (int i = 0; i < _pillars.Length; i++)
                {
                    ArenaPillar pillar = _pillars[i];
                    if (!ColliderOverlapsPlayerBand(pillar.BaseY, pillar.TopY, currentY, playerHeight))
                        continue;

                    (outX, outZ) = PushOutCircle2D(
                        outX,
                        outZ,
                        pillar.CenterX,
                        pillar.CenterZ,
                        pillar.ColliderRadius + playerRadius);
                }
            }

            Vector2 staticResolved = ResolveGameplayHorizontalCollision(
                outX, outZ, playerRadius, playerHeight, currentY);
            return ActiveWorldObstacleRuntime.ResolveHorizontalCollision(
                startX,
                startZ,
                staticResolved.x,
                staticResolved.y,
                playerRadius,
                playerHeight,
                currentY);
        }

        public Vector2 ResolveHorizontalCollision(
            float desiredX,
            float desiredZ,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            return ResolveHorizontalCollision(desiredX, desiredZ, desiredX, desiredZ, playerRadius, playerHeight, currentY);
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

            for (int iter = 0; iter < 2; iter++)
            {
                for (int i = 0; i < _gameplayBoxes.Length; i++)
                {
                    GameplayCollisionBox collider = _gameplayBoxes[i];
                    if (!GameplayBoxOverlapsPlayerBand(collider, currentY, playerHeight))
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

        private ArenaWall[] BuildWalls(ulong seed)
        {
            float wallHalfHeight = _layout.ruin_wall_height * 0.5f;
            ArenaWall[] walls = new ArenaWall[_layout.ruin_wall_segments.Length];
            for (int i = 0; i < _layout.ruin_wall_segments.Length; i++)
            {
                ArenaLayoutWallSegmentFile segment = _layout.ruin_wall_segments[i];
                float baseY = HeightAt(seed, segment.x, segment.z);
                walls[i] = new ArenaWall(
                    segment.x,
                    baseY + wallHalfHeight,
                    segment.z,
                    segment.half_x,
                    wallHalfHeight,
                    segment.half_z);
            }
            return walls;
        }

        private ArenaPlatform[] BuildPlatforms(ulong seed)
        {
            ArenaPlatform[] platforms = new ArenaPlatform[_layout.platforms.Length];
            for (int i = 0; i < _layout.platforms.Length; i++)
            {
                ArenaLayoutPointFile platform = _layout.platforms[i];
                float centerY = HeightAt(seed, platform.x, platform.z) + _layout.platform_center_height_offset;
                platforms[i] = new ArenaPlatform(
                    platform.x,
                    centerY,
                    platform.z,
                    _layout.platform_half_x,
                    _layout.platform_half_height,
                    _layout.platform_half_z);
            }
            return platforms;
        }

        private ArenaRamp[] BuildRamps(ulong seed)
        {
            ArenaRamp[] ramps = new ArenaRamp[_layout.ramps.Length];
            for (int i = 0; i < _layout.ramps.Length; i++)
            {
                ArenaLayoutRampFile ramp = _layout.ramps[i];
                float centerY = RampCenterY(seed, ramp.x, ramp.z, ramp.side);
                ramps[i] = new ArenaRamp(
                    ramp.x,
                    centerY,
                    ramp.z,
                    ramp.side,
                    _layout.ramp_half_x,
                    _layout.ramp_half_z,
                    _layout.ramp_top_center_offset,
                    _layout.ramp_top_slope_delta);
            }
            return ramps;
        }

        private ArenaPillar[] BuildPillars(ulong seed)
        {
            ArenaPillar[] pillars = new ArenaPillar[_layout.pillar_count];
            for (int i = 0; i < _layout.pillar_count; i++)
            {
                float jitter = SeededUnit(seed, (ulong)i * 2ul + 1ul);
                float angle = ((float)i / _layout.pillar_count) * Tau + jitter * 0.35f;
                float ringRadius = 7.0f + SeededUnit(seed, (ulong)i * 2ul + 2ul) * 5.0f;
                float columnScale = 0.7f + SeededUnit(seed, (ulong)i * 3ul + 7ul) * 0.9f;
                float x = Mathf.Cos(angle) * ringRadius;
                float z = Mathf.Sin(angle) * ringRadius;
                float height = 6.0f * columnScale;
                float baseY = HeightAt(seed, x, z);

                pillars[i] = new ArenaPillar(
                    x,
                    baseY + height * 0.5f,
                    z,
                    height * 0.5f,
                    _layout.pillar_collider_radius);
            }
            return pillars;
        }

        private float RampCenterY(ulong seed, float rampX, float rampZ, float side)
        {
            (float anchorX, float anchorZ) = LinkedPlatformAnchor(rampX, rampZ, side);
            return HeightAt(seed, anchorX, anchorZ) + _layout.ramp_center_height_offset;
        }

        private (float, float) LinkedPlatformAnchor(float rampX, float rampZ, float side)
        {
            float targetDx = side * _layout.ramp_platform_link_x_offset;
            bool hasBest = false;
            float bestScore = 0.0f;
            float bestX = rampX + targetDx;
            float bestZ = rampZ;

            for (int i = 0; i < _layout.platforms.Length; i++)
            {
                ArenaLayoutPointFile platform = _layout.platforms[i];
                float dx = platform.x - rampX;
                if (dx * side <= 0.0f)
                    continue;

                float dz = Mathf.Abs(platform.z - rampZ);
                float score = Mathf.Abs(dx - targetDx) + dz * 2.0f;
                if (!hasBest || score < bestScore)
                {
                    hasBest = true;
                    bestScore = score;
                    bestX = platform.x;
                    bestZ = platform.z;
                }
            }

            return (bestX, bestZ);
        }

        private static ArenaLayoutFile LoadArenaLayout(ArenaMapProfile profile)
        {
            ArenaLayoutFile layout = MovementSharedDataLoader.LoadRequiredJson<ArenaLayoutFile>(
                profile.LayoutResourcePath,
                $"{profile.MapId} arena layout");
            if (!string.Equals(layout.map_id, profile.MapId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"[ArenaMovementEnvironment] Invalid authored arena layout for {profile.MapId}.");
            }

            return layout;
        }

        private static GameplayCollisionBox[] LoadGameplayCollisionBoxes(ArenaMapProfile profile)
        {
            GameplayCollisionLayoutFile file =
                MovementSharedDataLoader.LoadRequiredJson<GameplayCollisionLayoutFile>(
                    profile.MovementCollisionResourcePath,
                    $"{profile.MapId} movement collision");
            GameplayCollisionBoxFile[] files = file.boxes ?? Array.Empty<GameplayCollisionBoxFile>();
            GameplayCollisionBox[] boxes = new GameplayCollisionBox[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                GameplayCollisionBoxFile boxFile = files[i];
                if (boxFile.center == null || boxFile.center.Length != 3 ||
                    boxFile.size == null || boxFile.size.Length != 3)
                {
                    throw new InvalidOperationException(
                        "[ArenaMovementEnvironment] gameplay collision asset contains a box with an invalid center/size payload.");
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

        private float HeightAt(ulong seed, float x, float z)
        {
            // Instance scenes currently share a flat authoritative movement surface.
            // Keep prediction and visibility aligned with that server contract.
            _ = seed;
            _ = x;
            _ = z;
            return _layout.ground_y;
        }

        private static double ValueNoise(ulong seed, double x, double z, double scale)
        {
            double fx = x / scale;
            double fz = z / scale;

            int x0 = (int)Math.Floor(fx);
            int z0 = (int)Math.Floor(fz);
            int x1 = x0 + 1;
            int z1 = z0 + 1;

            double tx = Smoothstep(fx - x0);
            double tz = Smoothstep(fz - z0);

            double n00 = HashToSignedUnit(HashGrid(seed, x0, z0));
            double n10 = HashToSignedUnit(HashGrid(seed, x1, z0));
            double n01 = HashToSignedUnit(HashGrid(seed, x0, z1));
            double n11 = HashToSignedUnit(HashGrid(seed, x1, z1));

            double nx0 = Lerp(n00, n10, tx);
            double nx1 = Lerp(n01, n11, tx);
            return Lerp(nx0, nx1, tz);
        }

        private static ulong HashGrid(ulong seed, int x, int z)
        {
            ulong xBits = unchecked((ulong)(long)x);
            ulong zBits = unchecked((ulong)(long)z);
            ulong value;
            unchecked
            {
                value = seed + xBits * HashXMul + zBits * HashZMul;
            }
            return Mix64(value);
        }

        private static ulong Mix64(ulong value)
        {
            unchecked
            {
                value ^= value >> 33;
                value *= MixMulA;
                value ^= value >> 33;
                value *= MixMulB;
                value ^= value >> 33;
                return value;
            }
        }

        private static double HashToSignedUnit(ulong hash)
        {
            ulong upper53 = hash >> 11;
            double unit = upper53 / (double)((1ul << 53) - 1ul);
            return unit * 2.0 - 1.0;
        }

        private float SeededUnit(ulong seed, ulong offset)
        {
            ulong mixed;
            unchecked
            {
                mixed = ((seed & JsSafeSeedMask) + offset * _layout.pillar_hash_step) & JsSafeSeedMask;
            }
            mixed = Math.Max(mixed, 1ul);
            return (mixed % 1000ul) / 999.0f;
        }

        private static double Smoothstep(double t)
        {
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        private static bool InsideAabb2D(float x, float z, float centerX, float centerZ, float halfX, float halfZ)
        {
            return Mathf.Abs(x - centerX) <= halfX && Mathf.Abs(z - centerZ) <= halfZ;
        }

        private static bool ColliderOverlapsPlayerBand(float colliderYMin, float colliderYMax, float currentY, float playerHeight)
        {
            if (!float.IsFinite(currentY) || !float.IsFinite(playerHeight))
                return true;

            float footY = currentY;
            float headY = currentY + Mathf.Max(playerHeight, 0.1f);
            return headY > colliderYMin + CollisionEpsilon &&
                   footY < colliderYMax - CollisionEpsilon;
        }

        private static bool GameplayBoxOverlapsPlayerBand(GameplayCollisionBox collider, float footY, float playerHeight)
        {
            float headY = footY + Mathf.Max(playerHeight, 0.1f);
            return headY > collider.CenterY - collider.HalfY + CollisionEpsilon &&
                   footY < collider.CenterY + collider.HalfY - CollisionEpsilon;
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
                    (ulong)FloatToUInt32Bits(cx) * HashXMul +
                    (ulong)FloatToUInt32Bits(cz) * HashZMul);
                hash = Math.Max(hash, 1ul);
                float angle = ((hash % 65_536ul) / 65_536.0f) * Tau;
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

            float signZ = dz >= 0.0f ? 1.0f : -1.0f;
            return (x, z + signZ * penZ);
        }

        private static uint FloatToUInt32Bits(float value)
        {
            return unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        }
    }

    internal static class MovementSharedDataLoader
    {
        public const string ArenaLayoutResourcePath = ArenaMapCatalog.ArenaMap01LayoutResourcePath;
        public const string GameplayCollisionResourcePath = ArenaMapCatalog.ArenaMap01MovementCollisionResourcePath;
        private const string SyncMenuPath = "Arena/Sync Shared Movement Data";

        private static bool _validated;

        public static void ValidateBundledDataAvailable()
        {
            if (_validated)
                return;

            _ = LoadRequiredTextAsset(ArenaLayoutResourcePath, "arena layout");
            _ = LoadRequiredTextAsset(GameplayCollisionResourcePath, "gameplay collision");
            _validated = true;
        }

        public static T LoadRequiredJson<T>(string resourcePath, string displayName)
        {
            TextAsset asset = LoadRequiredTextAsset(resourcePath, displayName);
            T parsed = JsonUtility.FromJson<T>(asset.text);
            if (parsed == null)
            {
                throw new InvalidOperationException(
                    $"[MovementSharedDataLoader] Failed to parse bundled {displayName} asset at Resources/{resourcePath}.json.");
            }

            return parsed;
        }

        public static bool TryLoadJson<T>(string resourcePath, out T? parsed) where T : class
        {
            TextAsset? asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                parsed = null;
                return false;
            }

            parsed = JsonUtility.FromJson<T>(asset.text);
            return parsed != null;
        }

        private static TextAsset LoadRequiredTextAsset(string resourcePath, string displayName)
        {
            TextAsset? asset = Resources.Load<TextAsset>(resourcePath);
            if (asset != null)
                return asset;

            throw new InvalidOperationException(
                $"[MovementSharedDataLoader] Missing bundled {displayName} asset at Resources/{resourcePath}.json. " +
                $"Run '{SyncMenuPath}' in the Unity editor to refresh shared movement data before shipping.");
        }
    }
}
