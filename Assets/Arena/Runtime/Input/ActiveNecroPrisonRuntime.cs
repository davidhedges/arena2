#nullable enable
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Client prediction mirror for authoritative Necro Prison zones. It uses
    /// Sanctuary's established movement-boundary clipping policy, but only for
    /// hostile actors attempting to leave the triangular prison.
    /// </summary>
    public static class ActiveNecroPrisonRuntime
    {
        private const float EquilateralHalfWidth = 0.8660254f;
        private static readonly Dictionary<ulong, ActiveNecroPrison> Rows = new();

        public static void Upsert(ActiveNecroPrison row) => Rows[row.PrisonId] = row;

        public static void Remove(ulong prisonId) => Rows.Remove(prisonId);

        public static void Clear() => Rows.Clear();

        public static Vector2 ResolveHorizontalCollision(
            float startX,
            float startZ,
            float targetX,
            float targetZ,
            float actorRadius)
        {
            float outX = targetX;
            float outZ = targetZ;
            foreach (ActiveNecroPrison prison in Rows.Values)
            {
                if (!IsHostileToLocal(prison)
                    || !TryExitFraction(
                        startX,
                        startZ,
                        outX,
                        outZ,
                        prison.CenterX,
                        prison.CenterZ,
                        prison.FacingYaw,
                        prison.Radius,
                        Mathf.Max(0f, actorRadius),
                        out float fraction))
                {
                    continue;
                }

                Vector2 clipped = ActiveSanctuaryZoneRuntime.ClipMovementBeforeBoundary(
                    startX,
                    startZ,
                    outX,
                    outZ,
                    fraction);
                outX = clipped.x;
                outZ = clipped.y;
            }
            return new Vector2(outX, outZ);
        }

        private static bool IsHostileToLocal(ActiveNecroPrison prison)
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local != null
                && PartyRelationship.Relation(prison.Owner, local.Identity, targetIsDummy: false)
                    == ClientCombatRelation.Hostile;
        }

        private static bool TryExitFraction(
            float startX,
            float startZ,
            float endX,
            float endZ,
            float centerX,
            float centerZ,
            float yaw,
            float circumradius,
            float padding,
            out float fraction)
        {
            fraction = 0f;
            float radius = Mathf.Max(0f, circumradius);
            if (radius <= Mathf.Epsilon)
                return false;

            float sin = Mathf.Sin(yaw);
            float cos = Mathf.Cos(yaw);
            Vector2[] localVertices =
            {
                new(0f, -radius),
                new(radius * EquilateralHalfWidth, radius * 0.5f),
                new(-radius * EquilateralHalfWidth, radius * 0.5f),
            };
            Vector2[] vertices = new Vector2[3];
            for (int index = 0; index < vertices.Length; index++)
            {
                Vector2 local = localVertices[index];
                vertices[index] = new Vector2(
                    centerX + local.x * cos + local.y * sin,
                    centerZ - local.x * sin + local.y * cos);
            }

            bool found = false;
            float earliest = float.PositiveInfinity;
            for (int index = 0; index < vertices.Length; index++)
            {
                Vector2 a = vertices[index];
                Vector2 b = vertices[(index + 1) % vertices.Length];
                Vector2 edge = b - a;
                float threshold = padding * edge.magnitude;
                float startSide = edge.x * (startZ - a.y) - edge.y * (startX - a.x);
                if (startSide < threshold)
                    return false;

                float endSide = edge.x * (endZ - a.y) - edge.y * (endX - a.x);
                if (endSide >= threshold)
                    continue;

                float denominator = startSide - endSide;
                if (denominator <= Mathf.Epsilon)
                    continue;

                float candidate = (startSide - threshold) / denominator;
                if (candidate < 0f || candidate > 1f || candidate >= earliest)
                    continue;
                earliest = candidate;
                found = true;
            }

            if (!found)
                return false;
            fraction = earliest;
            return true;
        }
    }
}
