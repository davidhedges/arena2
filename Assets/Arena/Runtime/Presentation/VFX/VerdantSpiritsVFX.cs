#nullable enable
using UnityEngine;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Reconstructs one or two source-owned nature spirits from the subscribed
    /// StatusEffect stack count by retuning the prefab's authored two-count bursts.
    /// </summary>
    internal sealed class VerdantSpiritsVFX : ISpellVFX
    {
        internal const string VfxId = "VFX_VERDANT_SPIRITS_ACTIVE_01";
        private const int MaxSpirits = 2;

        private readonly GameObject? _instance;
        private bool _disposed;

        internal VerdantSpiritsVFX(CombatVFXTemplateContext context)
        {
            CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(VfxId);
            if (template == null)
                return;

            _instance = Object.Instantiate(template.Prefab);
            _instance.name = $"VFX_VerdantSpirits_{ShortKey(context.ActionInstanceId)}";
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

            int spiritCount = Mathf.Clamp((int)context.SequenceCount, 1, MaxSpirits);
            ConfigureSpiritBursts(_instance, spiritCount);
        }

        public bool Tick(float dt)
        {
            _ = dt;
            return !_disposed && _instance != null;
        }

        public void OnUpdate(Vector3 position, Vector3 direction, float speed) { }
        public void OnImpact(Vector3 point) { }
        public void OnFizzle(Vector3 point) { }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_instance != null)
                Object.Destroy(_instance);
        }

        internal static int ConfigureSpiritBursts(GameObject instance, int spiritCount)
        {
            spiritCount = Mathf.Clamp(spiritCount, 1, MaxSpirits);
            int changedSystems = 0;
            ParticleSystem[] particleSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
            ParticleSystem? rootParticles = instance.GetComponent<ParticleSystem>();
            if (rootParticles != null)
            {
                rootParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            else
            {
                foreach (ParticleSystem particles in particleSystems)
                    particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            foreach (ParticleSystem particles in particleSystems)
            {
                ParticleSystem.MainModule main = particles.main;
                main.loop = true;
                main.duration = Mathf.Max(main.duration, main.startLifetime.constantMax);

                ParticleSystem.EmissionModule emission = particles.emission;
                int burstCount = emission.burstCount;
                if (burstCount <= 0)
                    continue;

                var bursts = new ParticleSystem.Burst[burstCount];
                emission.GetBursts(bursts);
                bool changed = false;
                for (int index = 0; index < bursts.Length; index++)
                {
                    ParticleSystem.MinMaxCurve count = bursts[index].count;
                    if (count.mode != ParticleSystemCurveMode.Constant
                        || !Mathf.Approximately(count.constant, MaxSpirits))
                    {
                        continue;
                    }

                    ParticleSystem.Burst burst = bursts[index];
                    burst.count = new ParticleSystem.MinMaxCurve(spiritCount);
                    bursts[index] = burst;
                    changed = true;
                }

                if (!changed)
                    continue;

                emission.SetBursts(bursts);
                changedSystems++;
            }

            if (rootParticles != null)
            {
                rootParticles.Play(true);
            }
            else
            {
                foreach (ParticleSystem particles in particleSystems)
                {
                    particles.Play(false);
                }
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
