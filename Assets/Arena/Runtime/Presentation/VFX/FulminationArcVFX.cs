#nullable enable
using System;
using Arena.Combat;
using UnityEngine;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Adapts the authored Fulmination impact prefab to the authoritative arc endpoints.
    /// The prefab's non-arc particles stay at the secondary target while the named
    /// connector is centered, aimed, and stretched back to the Fulminated target.
    /// </summary>
    internal static class FulminationArcVFX
    {
        internal const string VfxId = "VFX_FULMINATION_ARC_01";
        internal const string MainArcName = "main arc";

        // The main arc ParticleSystem is authored with a five-unit Y size.
        private const float AuthoredParticleLength = 5f;
        private const float CoincidentPointEpsilon = 0.001f;

        internal static void ConfigureIfNeeded(
            string vfxId,
            GameObject instance,
            CombatVFXTemplateContext context)
        {
            if (!string.Equals(WireIdentifier.Normalize(vfxId), VfxId, StringComparison.Ordinal))
                return;

            TryConfigure(instance, context.Point, context.Origin);
        }

        internal static bool TryConfigure(
            GameObject instance,
            Vector3 impactPoint,
            Vector3 fulminatedTargetPoint)
        {
            if (instance == null || !TryFindDescendant(instance.transform, MainArcName, out Transform mainArc))
                return false;

            Vector3 connector = fulminatedTargetPoint - impactPoint;
            float distance = connector.magnitude;
            if (!float.IsFinite(distance) || distance <= CoincidentPointEpsilon)
            {
                mainArc.gameObject.SetActive(false);
                return false;
            }

            Vector3 direction = connector / distance;
            mainArc.SetPositionAndRotation(
                Vector3.LerpUnclamped(impactPoint, fulminatedTargetPoint, 0.5f),
                Quaternion.FromToRotation(Vector3.up, direction));

            Vector3 localScale = mainArc.localScale;
            float parentYScale = mainArc.parent != null
                ? Mathf.Abs(mainArc.parent.lossyScale.y)
                : 1f;
            if (!float.IsFinite(parentYScale) || parentYScale <= CoincidentPointEpsilon)
                parentYScale = 1f;

            // Stretch billboards apply lengthScale on top of the particle's Y size.
            // Account for that authored multiplier instead of treating the five-unit
            // particle size as the final visible length; otherwise this prefab's -2
            // multiplier makes the centered connector twice as long as its endpoints.
            float authoredVisibleLength = AuthoredParticleLength;
            if (mainArc.TryGetComponent(out ParticleSystemRenderer renderer)
                && renderer.renderMode == ParticleSystemRenderMode.Stretch)
            {
                float stretchMultiplier = Mathf.Abs(renderer.lengthScale);
                if (float.IsFinite(stretchMultiplier)
                    && stretchMultiplier > CoincidentPointEpsilon)
                {
                    authoredVisibleLength *= stretchMultiplier;
                }
            }

            localScale.y = distance / (authoredVisibleLength * parentYScale);
            mainArc.localScale = localScale;

            // Instantiate may have honored playOnAwake before configuration. Clear only
            // the connector so the lifecycle's normal Play call starts it at this layout.
            foreach (ParticleSystem particles in mainArc.GetComponentsInChildren<ParticleSystem>(true))
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            return true;
        }

        private static bool TryFindDescendant(Transform root, string name, out Transform result)
        {
            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(candidate.name, name, StringComparison.Ordinal))
                {
                    result = candidate;
                    return true;
                }
            }

            result = null!;
            return false;
        }
    }
}
