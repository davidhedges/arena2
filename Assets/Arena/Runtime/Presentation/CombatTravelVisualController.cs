#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Presentation.VFX;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    internal sealed class CombatTravelVisualController : IDisposable
    {
        private readonly Dictionary<string, ISpellVFX> _active = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missingTemplateWarnings = new(StringComparer.Ordinal);
        private readonly List<string> _removeList = new();
        private readonly ProjectileVfxPool _projectilePool = new("CombatTravelVfxPool");

        public void Tick(float dt)
        {
            _removeList.Clear();
            foreach (var (key, visual) in _active)
            {
                bool keepAlive;
                try
                {
                    keepAlive = visual.Tick(dt);
                }
                catch (MissingReferenceException)
                {
                    keepAlive = false;
                }

                if (!keepAlive)
                {
                    DisposeVfx(visual);
                    _removeList.Add(key);
                }
            }

            foreach (string key in _removeList)
                _active.Remove(key);
        }

        public void Start(CombatVfxCueCatalog cue, CombatVFXTemplateContext context)
        {
            CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(cue.VfxId);
            if (template == null)
            {
                WarnMissingTemplate(cue.VfxId);
                return;
            }

            string key = TravelKey(context);
            if (_active.TryGetValue(key, out ISpellVFX old))
                DisposeVfx(old);

            ProjectileVfxPool.Rental? rental = _projectilePool.TryRent(template, context.ActionInstanceId);
            _active[key] = rental != null
                ? new WeaponProjectileVFX(
                    context.ActionInstanceId,
                    context.Origin,
                    context.Direction,
                    ResolveSpeed(context),
                    context.MaxDistance,
                    rental)
                : new WeaponProjectileVFX(
                    context.ActionInstanceId,
                    context.Origin,
                    context.Direction,
                    ResolveSpeed(context),
                    context.MaxDistance,
                    template.Scale,
                    template.Prefab);
        }

        public void Impact(CombatVFXTemplateContext context)
        {
            RouteTerminal(context, fizzle: false);
        }

        public void Fizzle(CombatVFXTemplateContext context)
        {
            RouteTerminal(context, fizzle: true);
        }

        public void Dispose()
        {
            foreach (ISpellVFX visual in _active.Values)
                DisposeVfx(visual);
            _active.Clear();
            _projectilePool.Dispose();
        }

        private void RouteTerminal(CombatVFXTemplateContext context, bool fizzle)
        {
            string key = TravelKey(context);
            if (!_active.TryGetValue(key, out ISpellVFX visual))
                return;

            try
            {
                if (fizzle)
                    visual.OnFizzle(context.Point);
                else
                    visual.OnImpact(context.Point);
            }
            catch (MissingReferenceException)
            {
            }
        }

        private static float ResolveSpeed(CombatVFXTemplateContext context)
        {
            if (context.Speed > 0f)
                return context.Speed;

            if (string.Equals(context.ScalarKind, CombatEventScalarKinds.TravelDurationSeconds, StringComparison.Ordinal)
                && context.ScalarValue > 0f
                && context.MaxDistance > 0f)
            {
                return context.MaxDistance / context.ScalarValue;
            }

            return 0f;
        }

        private static string TravelKey(CombatVFXTemplateContext context)
            => context.ActionInstanceId;

        private void WarnMissingTemplate(string vfxId)
        {
            string normalizedId = WireIdentifier.Normalize(vfxId);
            if (string.IsNullOrWhiteSpace(normalizedId))
                normalizedId = "<missing>";
            if (!_missingTemplateWarnings.Add(normalizedId))
                return;

            Debug.LogWarning(
                $"Combat travel VFX cue references unresolved template id '{normalizedId}'. Register a prefab in CombatVFXRegistry.");
        }

        private static void DisposeVfx(ISpellVFX visual)
        {
            try
            {
                visual.Dispose();
            }
            catch (MissingReferenceException)
            {
            }
        }
    }
}
