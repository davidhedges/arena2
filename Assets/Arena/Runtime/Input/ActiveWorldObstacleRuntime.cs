#nullable enable
using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Replicated dynamic obstacle collision used by local movement prediction.
    /// The authoritative server owns the same yaw-aligned box and lifetime.
    /// </summary>
    public static class ActiveWorldObstacleRuntime
    {
        private const float CollisionEpsilon = 0.001f;
        private static readonly Dictionary<ulong, ActiveWorldObstacle> Rows = new();

        public static void Upsert(ActiveWorldObstacle row) => Rows[row.ObstacleId] = row;

        public static void Remove(ulong obstacleId) => Rows.Remove(obstacleId);

        public static void Clear() => Rows.Clear();

        public static Vector2 ResolveHorizontalCollision(
            float startX,
            float startZ,
            float targetX,
            float targetZ,
            float playerRadius,
            float playerHeight,
            float footY)
        {
            float outX = targetX;
            float outZ = targetZ;
            foreach (ActiveWorldObstacle obstacle in Rows.Values)
            {
                float obstacleMinY = obstacle.CenterY - obstacle.HalfHeight;
                float obstacleMaxY = obstacle.CenterY + obstacle.HalfHeight;
                if (footY + playerHeight <= obstacleMinY || footY >= obstacleMaxY)
                    continue;

                if (!TrySegmentHitFraction(
                        obstacle,
                        startX,
                        startZ,
                        outX,
                        outZ,
                        Mathf.Max(0f, playerRadius),
                        out float hitFraction))
                {
                    continue;
                }

                float safeFraction = Mathf.Max(0f, hitFraction - CollisionEpsilon);
                outX = Mathf.Lerp(startX, outX, safeFraction);
                outZ = Mathf.Lerp(startZ, outZ, safeFraction);
            }
            return new Vector2(outX, outZ);
        }

        public static bool TryFindFirstLineHitDistance(
            Vector3 start,
            Vector3 end,
            float radius,
            out float hitDistance)
        {
            Vector3 delta = end - start;
            float distance = delta.magnitude;
            float bestFraction = float.PositiveInfinity;
            foreach (ActiveWorldObstacle obstacle in Rows.Values)
            {
                ToLocal(obstacle, start.x, start.z, out float startLocalX, out float startLocalZ);
                ToLocal(obstacle, end.x, end.z, out float endLocalX, out float endLocalZ);
                float enter = 0f;
                float exit = 1f;
                if (ClipAxis(
                        startLocalX,
                        endLocalX,
                        -obstacle.HalfWidth - radius,
                        obstacle.HalfWidth + radius,
                        ref enter,
                        ref exit)
                    && ClipAxis(
                        start.y - obstacle.CenterY,
                        end.y - obstacle.CenterY,
                        -obstacle.HalfHeight - radius,
                        obstacle.HalfHeight + radius,
                        ref enter,
                        ref exit)
                    && ClipAxis(
                        startLocalZ,
                        endLocalZ,
                        -obstacle.HalfDepth - radius,
                        obstacle.HalfDepth + radius,
                        ref enter,
                        ref exit)
                    && enter <= 1f
                    && exit >= 0f)
                {
                    bestFraction = Mathf.Min(bestFraction, Mathf.Max(0f, enter));
                }
            }

            if (float.IsPositiveInfinity(bestFraction))
            {
                hitDistance = 0f;
                return false;
            }

            hitDistance = distance * bestFraction;
            return true;
        }

        private static bool TrySegmentHitFraction(
            ActiveWorldObstacle obstacle,
            float startX,
            float startZ,
            float endX,
            float endZ,
            float radius,
            out float fraction)
        {
            ToLocal(obstacle, startX, startZ, out float startLocalX, out float startLocalZ);
            ToLocal(obstacle, endX, endZ, out float endLocalX, out float endLocalZ);
            float halfX = obstacle.HalfWidth + radius;
            float halfZ = obstacle.HalfDepth + radius;
            if (Mathf.Abs(startLocalX) <= halfX && Mathf.Abs(startLocalZ) <= halfZ)
            {
                fraction = 0f;
                return false;
            }

            float enter = 0f;
            float exit = 1f;
            if (!ClipAxis(startLocalX, endLocalX, -halfX, halfX, ref enter, ref exit)
                || !ClipAxis(startLocalZ, endLocalZ, -halfZ, halfZ, ref enter, ref exit))
            {
                fraction = 0f;
                return false;
            }

            fraction = Mathf.Max(0f, enter);
            return enter <= 1f && exit >= 0f;
        }

        private static bool ClipAxis(
            float start,
            float end,
            float min,
            float max,
            ref float enter,
            ref float exit)
        {
            float delta = end - start;
            if (Mathf.Abs(delta) <= Mathf.Epsilon)
                return start >= min && start <= max;

            float near = (min - start) / delta;
            float far = (max - start) / delta;
            if (near > far)
                (near, far) = (far, near);
            enter = Mathf.Max(enter, near);
            exit = Mathf.Min(exit, far);
            return enter <= exit;
        }

        private static void ToLocal(
            ActiveWorldObstacle obstacle,
            float worldX,
            float worldZ,
            out float localX,
            out float localZ)
        {
            float dx = worldX - obstacle.CenterX;
            float dz = worldZ - obstacle.CenterZ;
            float sinYaw = Mathf.Sin(obstacle.Yaw);
            float cosYaw = Mathf.Cos(obstacle.Yaw);
            localX = dx * cosYaw - dz * sinYaw;
            localZ = dx * sinYaw + dz * cosYaw;
        }
    }
}
