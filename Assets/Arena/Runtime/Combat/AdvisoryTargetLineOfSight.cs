#nullable enable

using System;
using Arena.Entity;
using Arena.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Combat
{
    /// <summary>
    /// Advisory client-side mirror of the server's requires_target_los
    /// targeting rule (netcode design review S4). Raycasts the bundled shared
    /// collision data with the exact server probe layout (caster 85% capsule
    /// height to target 75%/60% center and ±side points) so an illegal press
    /// can gray out or deny instantly with the server's own reason text.
    ///
    /// The server stays authoritative. This check must never be stricter than
    /// the server: it treats missing collision data as clear, and a probe only
    /// counts as blocked when its hit stops clearly short of the target point
    /// (AdvisoryClearMarginMeters), so position skew between the predicted
    /// local pose and the server's present-time pose may false-allow but not
    /// false-block.
    /// </summary>
    public static class AdvisoryTargetLineOfSight
    {
        // Mirrors server scene_query.rs: LOS_PROBE_RADIUS, the 0.85 caster eye
        // height, the 0.75/0.60 target heights, LOS_TARGET_SIDE_PROBE_FRACTION.
        private const float ProbeRadius = 0.05f;
        private const float CasterEyeHeightFraction = 0.85f;
        private const float TargetSideProbeFraction = 0.75f;
        private const float MinCapsuleHeight = 0.5f;
        private const float GeometryEpsilon = 0.0001f;
        private const float AdvisoryClearMarginMeters = 0.25f;
        private static readonly float[] TargetHeightFractions = { 0.75f, 0.60f };

        private const double CachedVerdictTtlSeconds = 0.15;

        private static ServerLosCollisionData? _collision;
        private static string _collisionScene = string.Empty;
        private static bool _collisionLoadAttempted;
        private static double _cachedVerdictAt = double.NegativeInfinity;
        private static bool _cachedVerdict;

        /// <summary>
        /// Fresh advisory verdict for a press: true only when every server
        /// probe ray to the target is clearly blocked by bundled collision.
        /// </summary>
        public static bool IsPressBlocked(PlayerEntity local, ICombatTargetEntity target)
        {
            if (local.IsDestroyed || target.IsDestroyed)
                return false;

            ServerLosCollisionData? collision = ResolveCollisionData();
            if (collision == null)
                return false;

            Vector3 casterBase = local.SimState.GetServerPosition();
            Vector3 targetBase = target.GetRenderPosition();
            float casterHeight = Mathf.Max(local.SimState.HitHeight, MinCapsuleHeight);
            float targetHeight = Mathf.Max(target.HitHeight, MinCapsuleHeight);
            float targetRadius = Mathf.Max(target.HitRadius, 0f);
            Vector3 origin = casterBase + Vector3.up * (casterHeight * CasterEyeHeightFraction);

            Vector3 toTarget = targetBase - casterBase;
            toTarget.y = 0f;
            float sideOffset = targetRadius * TargetSideProbeFraction;
            Vector3 side = Vector3.zero;
            if (toTarget.sqrMagnitude > GeometryEpsilon && sideOffset > GeometryEpsilon)
            {
                Vector3 dir = toTarget.normalized;
                side = new Vector3(-dir.z, 0f, dir.x) * sideOffset;
            }

            for (int heightIndex = 0; heightIndex < TargetHeightFractions.Length; heightIndex++)
            {
                Vector3 baseEnd = targetBase + Vector3.up * (targetHeight * TargetHeightFractions[heightIndex]);
                if (ProbeIsClear(collision, origin, baseEnd))
                    return false;
                if (side != Vector3.zero)
                {
                    if (ProbeIsClear(collision, origin, baseEnd + side))
                        return false;
                    if (ProbeIsClear(collision, origin, baseEnd - side))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Throttled verdict against the currently selected target, for the
        /// action-bar gray-out. False whenever there is no local player or no
        /// live selected target.
        /// </summary>
        public static bool IsSelectedTargetBlockedCached()
        {
            double now = Time.unscaledTimeAsDouble;
            if (now - _cachedVerdictAt < CachedVerdictTtlSeconds)
                return _cachedVerdict;

            _cachedVerdictAt = now;
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            ICombatTargetEntity? target = TargetSelector.Instance?.SelectedTarget;
            _cachedVerdict = local != null
                && target != null
                && !target.IsDestroyed
                && target.IsAlive
                && IsPressBlocked(local, target);
            return _cachedVerdict;
        }

        private static bool ProbeIsClear(ServerLosCollisionData collision, Vector3 origin, Vector3 end)
        {
            ServerLosProbeHit? hit = collision.FindFirstHit(origin, end, ProbeRadius);
            if (!hit.HasValue)
                return true;

            float probeDistance = Vector3.Distance(origin, end);
            return hit.Value.Distance >= probeDistance - AdvisoryClearMarginMeters;
        }

        private static ServerLosCollisionData? ResolveCollisionData()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (_collisionLoadAttempted && string.Equals(_collisionScene, sceneName, StringComparison.Ordinal))
                return _collision;

            _collisionScene = sceneName;
            _collisionLoadAttempted = true;
            _collision = ServerLosCollisionData.Load(OpenWorldSceneProfile.ForSceneName(sceneName));
            return _collision;
        }
    }
}
