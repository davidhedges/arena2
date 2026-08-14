#nullable enable
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Client prediction mirror for authoritative Sanctuary zones. These zones
    /// block only actors hostile to their owner; they never participate in LOS.
    /// </summary>
    public static class ActiveSanctuaryZoneRuntime
    {
        private const float CollisionEpsilon = 0.001f;
        private static readonly Dictionary<ulong, ActiveSanctuaryZone> Rows = new();

        public static void Upsert(ActiveSanctuaryZone row) => Rows[row.ZoneId] = row;

        public static void Remove(ulong zoneId) => Rows.Remove(zoneId);

        public static void Clear() => Rows.Clear();

        public static bool OverlapsHostile(Vector3 center, float areaRadius)
        {
            foreach (ActiveSanctuaryZone zone in Rows.Values)
            {
                if (!IsHostileToLocal(zone))
                    continue;

                float combinedRadius = Mathf.Max(0f, zone.Radius) + Mathf.Max(0f, areaRadius);
                float dx = center.x - zone.CenterX;
                float dz = center.z - zone.CenterZ;
                if (dx * dx + dz * dz <= combinedRadius * combinedRadius)
                    return true;
            }
            return false;
        }

        public static Vector2 ResolveHorizontalCollision(
            float startX,
            float startZ,
            float targetX,
            float targetZ,
            float actorRadius)
        {
            float outX = targetX;
            float outZ = targetZ;
            foreach (ActiveSanctuaryZone zone in Rows.Values)
            {
                if (!IsHostileToLocal(zone)
                    || !TryEntryFraction(
                        startX,
                        startZ,
                        outX,
                        outZ,
                        zone.CenterX,
                        zone.CenterZ,
                        Mathf.Max(0f, zone.Radius) + Mathf.Max(0f, actorRadius),
                        out float fraction))
                {
                    continue;
                }

                Vector2 clipped = ClipMovementBeforeBoundary(
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

        internal static Vector2 ClipMovementBeforeBoundary(
            float startX,
            float startZ,
            float targetX,
            float targetZ,
            float fraction)
        {
            float safeFraction = Mathf.Max(0f, fraction - CollisionEpsilon);
            return new Vector2(
                Mathf.Lerp(startX, targetX, safeFraction),
                Mathf.Lerp(startZ, targetZ, safeFraction));
        }

        private static bool IsHostileToLocal(ActiveSanctuaryZone zone)
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local != null
                && PartyRelationship.Relation(zone.Owner, local.Identity, targetIsDummy: false)
                    == ClientCombatRelation.Hostile;
        }

        private static bool TryEntryFraction(
            float startX,
            float startZ,
            float endX,
            float endZ,
            float centerX,
            float centerZ,
            float radius,
            out float fraction)
        {
            fraction = 0f;
            float startDx = startX - centerX;
            float startDz = startZ - centerZ;
            if (startDx * startDx + startDz * startDz <= radius * radius)
                return false;

            float dx = endX - startX;
            float dz = endZ - startZ;
            float a = dx * dx + dz * dz;
            if (a <= Mathf.Epsilon)
                return false;

            float b = 2f * (startDx * dx + startDz * dz);
            float c = startDx * startDx + startDz * startDz - radius * radius;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
                return false;

            float entry = (-b - Mathf.Sqrt(discriminant)) / (2f * a);
            if (entry < 0f || entry > 1f)
                return false;

            fraction = entry;
            return true;
        }
    }
}
