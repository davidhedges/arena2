using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal enum ElevationArchetype
    {
        Basin,
        Mesa,
        Ridge,
        Canyon,
        AscendingSpine,
        Descent,
        SplitPlateau,
        Crater,
        Helix,
        Terraces,
        Atrium
    }

    /// <summary>
    /// Produces a target elevation level per room for a chosen archetype. Targets are a
    /// desired field, not final levels: the generator repairs them along the room BFS tree
    /// onto the 4u-major grammar (magnificence decision A — each corridor hop is one 4u
    /// major or an 8u double-major, plus a single optional 2u bridge per dungeon).
    /// </summary>
    internal static class ElevationArchetypePlanner
    {
        private static readonly ElevationArchetype[] AllArchetypes =
            (ElevationArchetype[])Enum.GetValues(typeof(ElevationArchetype));

        internal static ElevationArchetype Choose(System.Random random)
        {
            return AllArchetypes[random.Next(AllArchetypes.Length)];
        }

        // Rooms are sampled at their plan-space center positions; the caller
        // owns the room representation (footprints since magnificence B.2).
        internal static int[] BuildTargetLevels(
            IReadOnlyList<Vector2> roomPositions,
            IReadOnlyList<int> depths,
            int maxDepth,
            int spineTargetRoom,
            int amplitude,
            ElevationArchetype archetype,
            System.Random random)
        {
            int roomCount = roomPositions.Count;
            var positions = new Vector2[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                positions[i] = roomPositions[i];
            }

            Vector2 min = positions[0];
            Vector2 max = positions[0];
            Vector2 centroid = Vector2.zero;
            foreach (Vector2 position in positions)
            {
                min = Vector2.Min(min, position);
                max = Vector2.Max(max, position);
                centroid += position;
            }

            centroid /= roomCount;
            Vector2 extent = Vector2.Max(max - min, new Vector2(1f, 1f));
            float mapRadius = Mathf.Max(1f, 0.5f * extent.magnitude);
            float depthScale = Mathf.Max(1, maxDepth);

            var field = new double[roomCount];
            switch (archetype)
            {
                case ElevationArchetype.Basin:
                    SampleRadialBlend(field, positions, positions[0], mapRadius, depths, depthScale, invert: false);
                    break;
                case ElevationArchetype.Mesa:
                    // Flat summit: the hall and its nearest ring share the top tier, so the
                    // descending stair transitions spread outward instead of all crowding
                    // around the hub room's corridors.
                    SampleMesaPlateau(field, positions, positions[0], mapRadius, depths, depthScale);
                    break;
                case ElevationArchetype.Ridge:
                    SampleBandDistance(field, positions, centroid, mapRadius, random, invert: true);
                    break;
                case ElevationArchetype.Canyon:
                    SampleBandDistance(field, positions, centroid, mapRadius, random, invert: false);
                    break;
                case ElevationArchetype.AscendingSpine:
                    SampleSpineProjection(field, positions, positions[0], positions[spineTargetRoom], depths, depthScale);
                    AddJitter(field, random, 0.1);
                    break;
                case ElevationArchetype.Descent:
                    SampleDescentProfile(field, positions, positions[0], positions[spineTargetRoom]);
                    break;
                case ElevationArchetype.SplitPlateau:
                    SampleSplitPlateau(field, positions, mapRadius, random);
                    break;
                case ElevationArchetype.Crater:
                    SampleCrater(field, positions, centroid, mapRadius);
                    break;
                case ElevationArchetype.Helix:
                    SampleHelix(field, positions, centroid, mapRadius, random);
                    break;
                case ElevationArchetype.Terraces:
                    SampleTerraces(field, positions, min, extent, random);
                    break;
                case ElevationArchetype.Atrium:
                    SampleAtrium(field, depths, random);
                    break;
                default:
                    SampleRadialBlend(field, positions, positions[0], mapRadius, depths, depthScale, invert: false);
                    break;
            }

            return QuantizeField(field, amplitude, depths, depthScale);
        }

        private static void SampleRadialBlend(
            double[] field,
            Vector2[] positions,
            Vector2 anchor,
            float mapRadius,
            IReadOnlyList<int> depths,
            float depthScale,
            bool invert)
        {
            for (int i = 0; i < field.Length; i++)
            {
                double radial = Vector2.Distance(positions[i], anchor) / mapRadius;
                double depth = depths[i] / depthScale;
                double value = 0.55 * radial + 0.45 * depth;
                field[i] = invert ? 1.0 - value : value;
            }
        }

        private static void SampleMesaPlateau(
            double[] field,
            Vector2[] positions,
            Vector2 anchor,
            float mapRadius,
            IReadOnlyList<int> depths,
            float depthScale)
        {
            const double plateau = 0.4;
            for (int i = 0; i < field.Length; i++)
            {
                double radial = Vector2.Distance(positions[i], anchor) / mapRadius;
                double depth = depths[i] / depthScale;
                double value = 0.55 * radial + 0.45 * depth;
                field[i] = 1.0 - Math.Max(0.0, value - plateau) / (1.0 - plateau);
            }
        }

        private static void SampleBandDistance(
            double[] field,
            Vector2[] positions,
            Vector2 centroid,
            float mapRadius,
            System.Random random,
            bool invert)
        {
            Vector2 normal = NextUnitDirection(random);
            for (int i = 0; i < field.Length; i++)
            {
                double band = Mathf.Abs(Vector2.Dot(positions[i] - centroid, normal)) / mapRadius;
                field[i] = invert ? 1.0 - band : band;
            }
        }

        private static void SampleSpineProjection(
            double[] field,
            Vector2[] positions,
            Vector2 rootPosition,
            Vector2 peakPosition,
            IReadOnlyList<int> depths,
            float depthScale)
        {
            for (int i = 0; i < field.Length; i++)
            {
                double t = ProjectOntoSpine(positions[i], rootPosition, peakPosition);
                field[i] = 0.8 * t + 0.2 * (depths[i] / depthScale);
            }
        }

        private static void SampleDescentProfile(
            double[] field,
            Vector2[] positions,
            Vector2 rootPosition,
            Vector2 peakPosition)
        {
            for (int i = 0; i < field.Length; i++)
            {
                double t = ProjectOntoSpine(positions[i], rootPosition, peakPosition);
                double falling = 1.0 - t / 0.65;
                double finalClimb = 0.9 * (t - 0.65) / 0.35;
                field[i] = Math.Max(falling, finalClimb);
            }
        }

        private static void SampleSplitPlateau(
            double[] field,
            Vector2[] positions,
            float mapRadius,
            System.Random random)
        {
            int firstAnchor = random.Next(positions.Length);
            int secondAnchor = firstAnchor;
            float bestDistance = -1f;
            for (int candidateAttempt = 0; candidateAttempt < 8; candidateAttempt++)
            {
                int candidate = random.Next(positions.Length);
                float distance = Vector2.Distance(positions[firstAnchor], positions[candidate]);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    secondAnchor = candidate;
                }
            }

            float plateauRadius = Mathf.Max(1f, mapRadius * 0.75f);
            for (int i = 0; i < field.Length; i++)
            {
                double first = 1.0 - Vector2.Distance(positions[i], positions[firstAnchor]) / plateauRadius;
                double second = 1.0 - Vector2.Distance(positions[i], positions[secondAnchor]) / plateauRadius;
                field[i] = Math.Max(first, second);
            }
        }

        private static void SampleCrater(double[] field, Vector2[] positions, Vector2 centroid, float mapRadius)
        {
            for (int i = 0; i < field.Length; i++)
            {
                double radial = Mathf.Clamp01(Vector2.Distance(positions[i], centroid) / mapRadius);
                field[i] = Math.Pow(radial, 2.2);
            }
        }

        private static void SampleHelix(
            double[] field,
            Vector2[] positions,
            Vector2 centroid,
            float mapRadius,
            System.Random random)
        {
            double phase = random.NextDouble();
            for (int i = 0; i < field.Length; i++)
            {
                Vector2 offset = positions[i] - centroid;
                double angle = (Mathf.Atan2(offset.y, offset.x) / (2f * Mathf.PI) + 0.5 + phase) % 1.0;
                double radial = Vector2.Distance(positions[i], centroid) / mapRadius;
                field[i] = angle + 0.1 * radial;
            }
        }

        private static void SampleTerraces(
            double[] field,
            Vector2[] positions,
            Vector2 min,
            Vector2 extent,
            System.Random random)
        {
            Vector2 direction = NextUnitDirection(random);
            for (int i = 0; i < field.Length; i++)
            {
                Vector2 normalized = new Vector2(
                    (positions[i].x - min.x) / extent.x,
                    (positions[i].y - min.y) / extent.y);
                field[i] = Vector2.Dot(normalized, direction);
            }
        }

        private static void SampleAtrium(double[] field, IReadOnlyList<int> depths, System.Random random)
        {
            for (int i = 0; i < field.Length; i++)
            {
                field[i] = depths[i] == 0 ? 0.0 : 0.55 + 0.45 * random.NextDouble();
            }
        }

        private static double ProjectOntoSpine(Vector2 position, Vector2 rootPosition, Vector2 peakPosition)
        {
            Vector2 axis = peakPosition - rootPosition;
            float lengthSquared = Mathf.Max(1f, axis.sqrMagnitude);
            return Mathf.Clamp01(Vector2.Dot(position - rootPosition, axis) / lengthSquared);
        }

        private static void AddJitter(double[] field, System.Random random, double strength)
        {
            for (int i = 0; i < field.Length; i++)
            {
                field[i] += (random.NextDouble() - 0.5) * strength;
            }
        }

        private static Vector2 NextUnitDirection(System.Random random)
        {
            double angle = random.NextDouble() * 2.0 * Math.PI;
            return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }

        private static int[] QuantizeField(double[] field, int amplitude, IReadOnlyList<int> depths, float depthScale)
        {
            double minValue = double.MaxValue;
            double maxValue = double.MinValue;
            int minIndex = 0;
            int maxIndex = 0;
            for (int i = 0; i < field.Length; i++)
            {
                if (field[i] < minValue)
                {
                    minValue = field[i];
                    minIndex = i;
                }

                if (field[i] > maxValue)
                {
                    maxValue = field[i];
                    maxIndex = i;
                }
            }

            double range = maxValue - minValue;
            var targets = new int[field.Length];
            if (range < 1e-6)
            {
                for (int i = 0; i < field.Length; i++)
                {
                    targets[i] = Mathf.RoundToInt(depths[i] / depthScale * amplitude);
                }

                return targets;
            }

            for (int i = 0; i < field.Length; i++)
            {
                double normalized = (field[i] - minValue) / range;
                targets[i] = Mathf.Clamp(Mathf.RoundToInt((float)(normalized * amplitude)), 0, amplitude);
            }

            targets[minIndex] = 0;
            targets[maxIndex] = amplitude;
            return targets;
        }
    }
}
