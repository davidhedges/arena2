#nullable enable

using System;
using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Client-predicted collision for the same binary door blocker definitions
    /// used by the authoritative server. Door swing transforms are presentation
    /// only and never drive collision.
    /// </summary>
    public static class WorldDoorCollisionRuntime
    {
        private const string ManifestResourcePath =
            "SharedData/Worlds/random_dungeon.doors.shared";
        private const string RandomDungeonSceneName = "RandomDungeon";
        private const float CollisionEpsilon = 0.001f;

        [Serializable]
        private sealed class DoorManifest
        {
            public int schema_version;
            public string world_definition_key = string.Empty;
            public DoorDefinition[] doors = Array.Empty<DoorDefinition>();
        }

        [Serializable]
        private sealed class DoorDefinition
        {
            public string door_definition_id = string.Empty;
            public VectorRecord interaction_anchor = new();
            public float max_interaction_distance;
            public DoorBlocker closed_blocker = new();
            public bool default_open = true;
            public string open_interaction_profile_id = string.Empty;
            public string close_interaction_profile_id = string.Empty;
        }

        [Serializable]
        private sealed class DoorBlocker
        {
            public VectorRecord center = new();
            public VectorRecord size = new();
            public float yaw_degrees;
        }

        [Serializable]
        private sealed class VectorRecord
        {
            public float x;
            public float y;
            public float z;

            public Vector3 ToVector3() => new(x, y, z);
        }

        private readonly struct ReplicatedState
        {
            public ReplicatedState(bool isOpen, ulong revision)
            {
                IsOpen = isOpen;
                Revision = revision;
            }

            public bool IsOpen { get; }
            public ulong Revision { get; }
        }

        private static readonly Dictionary<string, ReplicatedState> ReplicatedStates =
            new(StringComparer.Ordinal);
        private static DoorDefinition[]? _definitions;
        private static bool _scopeIsRandomDungeon;

        public static void SetScope(string? worldKind, string? openWorldSceneName)
        {
            _scopeIsRandomDungeon =
                string.Equals(worldKind, "OPEN", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    openWorldSceneName,
                    RandomDungeonSceneName,
                    StringComparison.Ordinal);
        }

        public static void Upsert(WorldDoorState row)
        {
            if (!string.Equals(row.WorldKind, "OPEN", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    row.OpenWorldSceneName,
                    RandomDungeonSceneName,
                    StringComparison.Ordinal))
            {
                return;
            }

            ReplicatedStates[NormalizeId(row.DoorDefinitionId)] =
                new ReplicatedState(row.IsOpen, row.Revision);
        }

        public static void Remove(WorldDoorState row)
            => ReplicatedStates.Remove(NormalizeId(row.DoorDefinitionId));

        public static void Clear()
        {
            ReplicatedStates.Clear();
            _scopeIsRandomDungeon = false;
        }

        public static bool TryGetEffectiveState(
            string doorDefinitionId,
            out bool isOpen,
            out ulong revision)
        {
            string normalized = NormalizeId(doorDefinitionId);
            if (ReplicatedStates.TryGetValue(normalized, out ReplicatedState replicated))
            {
                isOpen = replicated.IsOpen;
                revision = replicated.Revision;
                return true;
            }

            foreach (DoorDefinition definition in Definitions)
            {
                if (!string.Equals(
                        definition.door_definition_id,
                        normalized,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                isOpen = definition.default_open;
                revision = 0;
                return true;
            }

            isOpen = true;
            revision = 0;
            return false;
        }

        public static Vector2 ResolveHorizontalCollision(
            float startX,
            float startZ,
            float targetX,
            float targetZ,
            float playerRadius,
            float playerHeight,
            float footY)
        {
            if (!_scopeIsRandomDungeon)
                return new Vector2(targetX, targetZ);

            float outX = targetX;
            float outZ = targetZ;
            foreach (DoorDefinition door in Definitions)
            {
                if (IsOpen(door))
                    continue;

                if (!TrySegmentHitFraction(
                        door,
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
            if (!_scopeIsRandomDungeon)
            {
                hitDistance = 0f;
                return false;
            }

            float distance = Vector3.Distance(start, end);
            float bestFraction = float.PositiveInfinity;
            foreach (DoorDefinition door in Definitions)
            {
                if (IsOpen(door))
                    continue;

                Vector3 startLocal = ToLocal(door, start);
                Vector3 endLocal = ToLocal(door, end);
                Vector3 halfExtents = door.closed_blocker.size.ToVector3() * 0.5f;
                float enter = 0f;
                float exit = 1f;
                if (ClipAxis(
                        startLocal.x,
                        endLocal.x,
                        -halfExtents.x - radius,
                        halfExtents.x + radius,
                        ref enter,
                        ref exit)
                    && ClipAxis(
                        startLocal.y,
                        endLocal.y,
                        -halfExtents.y - radius,
                        halfExtents.y + radius,
                        ref enter,
                        ref exit)
                    && ClipAxis(
                        startLocal.z,
                        endLocal.z,
                        -halfExtents.z - radius,
                        halfExtents.z + radius,
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

        private static DoorDefinition[] Definitions
        {
            get
            {
                if (_definitions != null)
                    return _definitions;

                TextAsset? manifestAsset = Resources.Load<TextAsset>(ManifestResourcePath);
                if (manifestAsset == null)
                {
                    Debug.LogError(
                        $"Missing door manifest at Resources/{ManifestResourcePath}.json");
                    _definitions = Array.Empty<DoorDefinition>();
                    return _definitions;
                }

                DoorManifest? manifest =
                    JsonUtility.FromJson<DoorManifest>(manifestAsset.text);
                if (manifest == null || manifest.schema_version != 1 || manifest.doors == null)
                {
                    Debug.LogError("The random-dungeon door manifest is invalid.");
                    _definitions = Array.Empty<DoorDefinition>();
                    return _definitions;
                }

                foreach (DoorDefinition definition in manifest.doors)
                    definition.door_definition_id = NormalizeId(definition.door_definition_id);
                _definitions = manifest.doors;
                return _definitions;
            }
        }

        private static bool IsOpen(DoorDefinition door)
            => ReplicatedStates.TryGetValue(
                    door.door_definition_id,
                    out ReplicatedState replicated)
                ? replicated.IsOpen
                : door.default_open;

        private static bool TrySegmentHitFraction(
            DoorDefinition door,
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
            Vector3 startLocal = ToLocal(
                door,
                new Vector3(startX, actorCenterY, startZ));
            Vector3 endLocal = ToLocal(
                door,
                new Vector3(endX, actorCenterY, endZ));
            Vector3 doorHalfExtents = door.closed_blocker.size.ToVector3() * 0.5f;
            Vector3 halfExtents = new(
                doorHalfExtents.x + radius,
                doorHalfExtents.y + halfHeight,
                doorHalfExtents.z + radius);
            if (Mathf.Abs(startLocal.x) <= halfExtents.x
                && Mathf.Abs(startLocal.y) <= halfExtents.y
                && Mathf.Abs(startLocal.z) <= halfExtents.z)
            {
                fraction = 0f;
                return false;
            }

            float enter = 0f;
            float exit = 1f;
            if (!ClipAxis(
                    startLocal.x,
                    endLocal.x,
                    -halfExtents.x,
                    halfExtents.x,
                    ref enter,
                    ref exit)
                || !ClipAxis(
                    startLocal.y,
                    endLocal.y,
                    -halfExtents.y,
                    halfExtents.y,
                    ref enter,
                    ref exit)
                || !ClipAxis(
                    startLocal.z,
                    endLocal.z,
                    -halfExtents.z,
                    halfExtents.z,
                    ref enter,
                    ref exit))
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

        private static Vector3 ToLocal(DoorDefinition door, Vector3 world)
        {
            Quaternion inverse = Quaternion.Inverse(
                Quaternion.Euler(0f, door.closed_blocker.yaw_degrees, 0f));
            return inverse * (world - door.closed_blocker.center.ToVector3());
        }

        private static string NormalizeId(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }
}
