#nullable enable
using System;
using UnityEngine;
using SpacetimeDB.Types;

namespace Arena.Simulation
{
    public readonly struct SpecialMovementTrack
    {
        public SpecialMovementTrack(
            string kind,
            string pathMode,
            long startedAtMs,
            long durationMs,
            Vector3 start,
            Vector3 end,
            float arcHeight,
            float facingYawStart,
            string facingPolicy,
            string collisionPolicy,
            string resolvePolicy)
        {
            Kind = kind;
            PathMode = pathMode;
            StartedAtMs = startedAtMs;
            DurationMs = durationMs;
            Start = start;
            End = end;
            ArcHeight = Mathf.Max(0.0f, arcHeight);
            FacingYawStart = facingYawStart;
            FacingPolicy = facingPolicy;
            CollisionPolicy = collisionPolicy;
            ResolvePolicy = resolvePolicy;
        }

        public string Kind { get; }
        public string PathMode { get; }
        public long StartedAtMs { get; }
        public long DurationMs { get; }
        public Vector3 Start { get; }
        public Vector3 End { get; }
        public float ArcHeight { get; }
        public float FacingYawStart { get; }
        public string FacingPolicy { get; }
        public string CollisionPolicy { get; }
        public string ResolvePolicy { get; }
        public long EndAtMs => StartedAtMs + DurationMs;

        public static SpecialMovementTrack FromRow(SpecialMovementRuntime row)
        {
            return new SpecialMovementTrack(
                row.Kind,
                row.PathMode,
                row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                (long)row.DurationMs,
                new Vector3(row.StartX, row.StartY, row.StartZ),
                new Vector3(row.EndX, row.EndY, row.EndZ),
                row.ArcHeight,
                row.FacingYawStart,
                row.FacingPolicy,
                row.CollisionPolicy,
                row.ResolvePolicy);
        }
    }

    public readonly struct SampledSpecialMovementPose
    {
        public SampledSpecialMovementPose(Vector3 position, float facingYawRadians, bool finished)
        {
            Position = position;
            FacingYawRadians = facingYawRadians;
            Finished = finished;
        }

        public Vector3 Position { get; }
        public float FacingYawRadians { get; }
        public bool Finished { get; }
    }

    public static class SpecialMovementRuntimeSampler
    {
        public const string PathModeLinear = "LINEAR";
        public const string PathModeInstant = "INSTANT";
        public const string PathModeParabolicArc = "PARABOLIC_ARC";
        public const string FacingPolicyFacePath = "FACE_PATH";
        public const string CollisionPolicyFixedY = "STOP_AT_BLOCK_FIXED_Y";
        public const string CollisionPolicyKeepHeightLegacy = "STOP_AT_BLOCK_KEEP_HEIGHT";

        public static SampledSpecialMovementPose Sample(
            in SpecialMovementTrack track,
            long nowMs,
            Func<float, float, float, float>? sampleGroundHeight = null)
        {
            if (string.Equals(track.PathMode, PathModeInstant, System.StringComparison.OrdinalIgnoreCase)
                || track.DurationMs <= 0L)
            {
                Vector3 instantPosition = ResolveSpecialMovementY(track, track.End, sampleGroundHeight);
                return new SampledSpecialMovementPose(instantPosition, track.FacingYawStart, true);
            }

            long elapsedMs = nowMs - track.StartedAtMs;
            float progress = track.DurationMs <= 0L
                ? 1.0f
                : Mathf.Clamp01((float)elapsedMs / track.DurationMs);
            Vector3 position = Vector3.Lerp(track.Start, track.End, progress);
            if (string.Equals(track.PathMode, PathModeParabolicArc, System.StringComparison.OrdinalIgnoreCase))
                position.y += 4.0f * track.ArcHeight * progress * (1.0f - progress);
            else
                position = ResolveSpecialMovementY(track, position, sampleGroundHeight);
            float yaw = track.FacingYawStart;
            if (string.Equals(track.FacingPolicy, FacingPolicyFacePath, System.StringComparison.OrdinalIgnoreCase))
            {
                Vector3 path = track.End - track.Start;
                path.y = 0.0f;
                if (path.sqrMagnitude > 0.0001f)
                    yaw = Mathf.Atan2(path.x, path.z);
            }

            return new SampledSpecialMovementPose(position, yaw, nowMs >= track.EndAtMs);
        }

        private static Vector3 ResolveSpecialMovementY(
            in SpecialMovementTrack track,
            Vector3 position,
            Func<float, float, float, float>? sampleGroundHeight)
        {
            bool fixedY = UsesFixedYCollisionPolicy(track.CollisionPolicy);
            if (fixedY)
            {
                position.y = track.Start.y;
                return position;
            }

            if (sampleGroundHeight != null)
            {
                float groundY = sampleGroundHeight(position.x, position.z, position.y);
                position.y = groundY;
                return position;
            }

            return position;
        }

        private static bool UsesFixedYCollisionPolicy(string collisionPolicy)
        {
            return string.Equals(collisionPolicy, CollisionPolicyFixedY, System.StringComparison.OrdinalIgnoreCase)
                   || string.Equals(collisionPolicy, CollisionPolicyKeepHeightLegacy, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
