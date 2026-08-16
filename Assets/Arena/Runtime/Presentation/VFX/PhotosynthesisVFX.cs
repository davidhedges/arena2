#nullable enable
using UnityEngine;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Keeps the authored nature aura alive for the passive status lifetime and
    /// caps its orbiting leaf flecks at one particle per replicated
    /// Photosynthesis stack.
    /// </summary>
    internal sealed class PhotosynthesisVFX : ISpellVFX, IStackScaledVFX
    {
        internal const string VfxId = "VFX_PHOTOSYNTHESIS_ACTIVE_01";
        private const string LeafParticleName = "Flecks_Shiny_Additive";
        private const int MaxStacks = 5;
        private const int LeavesPerStack = 1;
        private const float RefillFactor = 12f;
        private const float MinRefillRatePerSecond = 240f;
        private const float ShortestLeafLifeFloorSeconds = 0.25f;

        private readonly GameObject? _instance;
        private bool _disposed;

        internal PhotosynthesisVFX(CombatVFXTemplateContext context)
        {
            CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(VfxId);
            if (template == null)
                return;

            _instance = Object.Instantiate(template.Prefab);
            _instance.name = $"VFX_Photosynthesis_{ShortKey(context.ActionInstanceId)}";
            if (context.FollowAnchor != null)
            {
                _instance.transform.SetParent(context.FollowAnchor, false);
                _instance.transform.localPosition = template.LocalPositionOffset;
                _instance.transform.localRotation = template.LocalRotation;
            }
            else
            {
                _instance.transform.SetPositionAndRotation(
                    context.Point + template.LocalPositionOffset,
                    template.LocalRotation);
            }
            _instance.transform.localScale *= template.Scale;

            int stacks = Mathf.Clamp((int)context.SequenceCount, 1, MaxStacks);
            ConfigureLeafCount(_instance, stacks);
        }

        public bool Tick(float dt)
        {
            _ = dt;
            return !_disposed && _instance != null;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed) { }
        public void OnImpact(Vector3 point) { }
        public void OnFizzle(Vector3 point) { }

        // The leaf count is a live particle cap, so a stack tick just raises the ceiling and
        // the already-orbiting leaves keep their flight instead of being cleared and re-emitted.
        public bool TrySetStackCount(uint stacks)
        {
            if (_disposed || _instance == null)
                return false;

            return ConfigureLeafCount(_instance, stacks > MaxStacks ? MaxStacks : (int)stacks) > 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_instance != null)
                Object.Destroy(_instance);
        }

        // The live particle cap is the whole dial. On its own it is only a ceiling the
        // system drifts under, because the authored 25/s rate takes ~40ms to refill the slot
        // a dead leaf freed; emitting faster than leaves can possibly die pins the population
        // at the cap instead. The realized spawn rate still equals the death rate - the cap
        // discards the surplus - so the authored orbit, lifetime and look are untouched and
        // only the replacement of a dead leaf gets prompter. The rate scales with the cap, but
        // a small cap derives a rate below one particle per frame and cannot refill within the
        // frame a leaf died, so the floor - not the derived term - is what governs the low
        // tiers. Measured at 60fps over 20s: with both, every tier holds exactly
        // LeavesPerStack*stacks; with the derived term alone a lone leaf blinked out entirely.
        internal static int ConfigureLeafCount(GameObject instance, int stacks)
        {
            stacks = Mathf.Clamp(stacks, 1, MaxStacks);
            int leaves = LeavesPerStack * stacks;
            ParticleSystem[] particleSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
            int changedSystems = 0;
            foreach (ParticleSystem particles in particleSystems)
            {
                if (!string.Equals(particles.gameObject.name, LeafParticleName, System.StringComparison.Ordinal))
                    continue;

                ParticleSystem.MainModule main = particles.main;
                main.maxParticles = leaves;

                float shortestLeafLife = Mathf.Max(
                    main.startLifetime.constantMin,
                    ShortestLeafLifeFloorSeconds);
                ParticleSystem.EmissionModule emission = particles.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(
                    Mathf.Max(
                        RefillFactor * leaves / shortestLeafLife,
                        MinRefillRatePerSecond));
                changedSystems++;
            }

            return changedSystems;
        }

        private static string ShortKey(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";
            return value.Substring(0, Mathf.Min(6, value.Length));
        }
    }
}
