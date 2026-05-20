#nullable enable

using System;
using System.Collections.Generic;
using SpacetimeDB.Types;

namespace Arena.Debugging
{
    internal static class CombatPresentationDebugGate
    {
        private static readonly List<Func<CombatEvent, bool>> CombatEventSuppressors = new();
        private static readonly List<Func<ProjectilePresentationEvent, bool>> ProjectileEventSuppressors = new();

        public static IDisposable RegisterCombatEventSuppressor(Func<CombatEvent, bool> suppressor)
        {
            CombatEventSuppressors.Add(suppressor);
            return new Registration(() => CombatEventSuppressors.Remove(suppressor));
        }

        public static IDisposable RegisterProjectileEventSuppressor(Func<ProjectilePresentationEvent, bool> suppressor)
        {
            ProjectileEventSuppressors.Add(suppressor);
            return new Registration(() => ProjectileEventSuppressors.Remove(suppressor));
        }

        public static bool ShouldSuppress(CombatEvent row)
        {
            for (int i = 0; i < CombatEventSuppressors.Count; i++)
            {
                if (CombatEventSuppressors[i](row))
                    return true;
            }

            return false;
        }

        public static bool ShouldSuppress(ProjectilePresentationEvent row)
        {
            for (int i = 0; i < ProjectileEventSuppressors.Count; i++)
            {
                if (ProjectileEventSuppressors[i](row))
                    return true;
            }

            return false;
        }

        private sealed class Registration : IDisposable
        {
            private Action? _unregister;

            public Registration(Action unregister)
            {
                _unregister = unregister;
            }

            public void Dispose()
            {
                Action? unregister = _unregister;
                if (unregister == null)
                    return;

                _unregister = null;
                unregister();
            }
        }
    }
}
