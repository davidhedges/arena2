#nullable enable
using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Replicated dynamic obstacle collision used by local movement prediction.
    /// The authoritative server owns the same fully oriented box and lifetime.
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
                if (!TrySegmentHitFraction(
                        obstacle,
                        startX,
                        startZ,
                        outX,
                        outZ,
                        Mathf.Max(0f, playerRadius),
                        footY,
                        Mathf.Max(0f, playerHeight),
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
                Vector3 startLocal = ToLocal(obstacle, start);
                Vector3 endLocal = ToLocal(obstacle, end);
                float enter = 0f;
                float exit = 1f;
                if (ClipAxis(
                        startLocal.x,
                        endLocal.x,
                        -obstacle.HalfWidth - radius,
                        obstacle.HalfWidth + radius,
                        ref enter,
                        ref exit)
                    && ClipAxis(
                        startLocal.y,
                        endLocal.y,
                        -obstacle.HalfHeight - radius,
                        obstacle.HalfHeight + radius,
                        ref enter,
                        ref exit)
                    && ClipAxis(
                        startLocal.z,
                        endLocal.z,
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
            float footY,
            float height,
            out float fraction)
        {
            float halfHeight = height * 0.5f;
            float actorCenterY = footY + halfHeight;
            Vector3 startLocal = ToLocal(obstacle, new Vector3(startX, actorCenterY, startZ));
            Vector3 endLocal = ToLocal(obstacle, new Vector3(endX, actorCenterY, endZ));

            Quaternion inverseRotation = Quaternion.Inverse(CollisionRotation(obstacle));
            Vector3 worldXLocal = inverseRotation * Vector3.right;
            Vector3 worldYLocal = inverseRotation * Vector3.up;
            Vector3 worldZLocal = inverseRotation * Vector3.forward;
            Vector3 actorExtentLocal = Abs(worldXLocal) * radius
                + Abs(worldYLocal) * halfHeight
                + Abs(worldZLocal) * radius;
            Vector3 halfExtents = new(
                obstacle.HalfWidth + actorExtentLocal.x,
                obstacle.HalfHeight + actorExtentLocal.y,
                obstacle.HalfDepth + actorExtentLocal.z);
            if (Mathf.Abs(startLocal.x) <= halfExtents.x
                && Mathf.Abs(startLocal.y) <= halfExtents.y
                && Mathf.Abs(startLocal.z) <= halfExtents.z)
            {
                fraction = 0f;
                return false;
            }

            float enter = 0f;
            float exit = 1f;
            if (!ClipAxis(startLocal.x, endLocal.x, -halfExtents.x, halfExtents.x, ref enter, ref exit)
                || !ClipAxis(startLocal.y, endLocal.y, -halfExtents.y, halfExtents.y, ref enter, ref exit)
                || !ClipAxis(startLocal.z, endLocal.z, -halfExtents.z, halfExtents.z, ref enter, ref exit))
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

        private static Vector3 ToLocal(ActiveWorldObstacle obstacle, Vector3 world)
        {
            Vector3 center = new(obstacle.CenterX, obstacle.CenterY, obstacle.CenterZ);
            return Quaternion.Inverse(CollisionRotation(obstacle)) * (world - center);
        }

        private static Quaternion CollisionRotation(ActiveWorldObstacle obstacle)
        {
            Quaternion rotation = new(
                obstacle.CollisionRotationX,
                obstacle.CollisionRotationY,
                obstacle.CollisionRotationZ,
                obstacle.CollisionRotationW);
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x
                + rotation.y * rotation.y
                + rotation.z * rotation.z
                + rotation.w * rotation.w);
            if (magnitude <= Mathf.Epsilon)
                return Quaternion.identity;
            return new Quaternion(
                rotation.x / magnitude,
                rotation.y / magnitude,
                rotation.z / magnitude,
                rotation.w / magnitude);
        }

        private static Vector3 Abs(Vector3 value) => new(
            Mathf.Abs(value.x),
            Mathf.Abs(value.y),
            Mathf.Abs(value.z));
    }
}
