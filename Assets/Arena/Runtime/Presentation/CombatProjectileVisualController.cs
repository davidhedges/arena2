#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Network;
using Arena.Presentation.VFX;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    internal sealed class CombatProjectileVisualController : IDisposable
    {
        private const string ProjectileMotionOrbitCaster = "ORBIT_CASTER";
        private const string ProjectileMotionBoomerangCaster = "BOOMERANG_CASTER";

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static int DebugActiveProjectileVisualCount { get; private set; }
        internal static int DebugMissingProjectilePrefabCount { get; private set; }
        internal static long DebugStartedProjectileVisualCount { get; private set; }
        internal static long DebugUpdatedProjectileVisualCount { get; private set; }
        internal static long DebugTerminalProjectileVisualCount { get; private set; }
        internal static long DebugAutoDisposedProjectileVisualCount { get; private set; }
#endif

        // Key projectile visuals by projectile_instance_id, not action_instance_id.
        // V1 spell delivery emits only :p0, but this keeps p1/p2 multi-projectile
        // rows independent when gameplay delivery grows beyond one projectile.
        private readonly Dictionary<string, ISpellVFX> _activeProjectiles = new();
        private readonly Dictionary<string, OrbitProjectileMotion> _orbitProjectiles = new();
        private readonly Dictionary<string, DelayedProjectileStart> _delayedProjectileStarts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _adoptedPredictedProjectiles = new(StringComparer.Ordinal);
        private readonly List<string> _removeList = new();
        private readonly HashSet<string> _missingProjectilePrefabWarnings = new();
        private readonly ProjectileVfxPool _projectilePool = new("CombatProjectileVfxPool");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly HashSet<string> _terminalProjectileKeys = new(StringComparer.Ordinal);
#endif

        private readonly struct DelayedProjectileStart
        {
            public DelayedProjectileStart(ProjectilePresentationEvent row, float startAtSeconds)
            {
                Row = row;
                StartAtSeconds = startAtSeconds;
            }

            public ProjectilePresentationEvent Row { get; }
            public float StartAtSeconds { get; }
        }

        public void Tick(float dt)
        {
            TickDelayedProjectileStarts();

            _removeList.Clear();
            foreach (var (projectileId, vfx) in _activeProjectiles)
            {
                bool keepAlive;
                try
                {
                    if (_orbitProjectiles.TryGetValue(projectileId, out var orbit)
                        && vfx is WeaponProjectileVFX weaponProjectile)
                    {
                        orbit.Advance(dt);
                        ApplyOrbitMotion(weaponProjectile, orbit);
                    }

                    keepAlive = vfx.Tick(dt);
                }
                catch (MissingReferenceException)
                {
                    keepAlive = false;
                }

                if (!keepAlive)
                {
                    DisposeVfx(vfx);
                    _removeList.Add(projectileId);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (!_terminalProjectileKeys.Contains(projectileId))
                        DebugAutoDisposedProjectileVisualCount++;
#endif
                }
            }

            foreach (string id in _removeList)
            {
                _activeProjectiles.Remove(id);
                _orbitProjectiles.Remove(id);
                _adoptedPredictedProjectiles.Remove(id);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _terminalProjectileKeys.Remove(id);
#endif
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RefreshDebugCounts();
#endif
        }

        public void Start(ProjectilePresentationEvent row)
        {
            Start(row, allowAnimatedPropDelay: true);
        }

        private void Start(ProjectilePresentationEvent row, bool allowAnimatedPropDelay)
        {
            string projectileKey = ProjectileKey(row);
            if (allowAnimatedPropDelay
                && TryDelayAnimatedPropProjectileStart(projectileKey, row))
            {
                return;
            }

            ReleaseAnimatedPropHandoff(row);
            ReplaceProjectile(projectileKey, row.ProjectileId, template =>
            {
                ProjectileVfxPool.Rental? rental = _projectilePool.TryRent(template, projectileKey);
                return rental != null
                    ? new WeaponProjectileVFX(
                        projectileKey,
                        ResolvePresentationPosition(row),
                        new Vector3(row.DirX, row.DirY, row.DirZ),
                        PresentationSpeed(row),
                        row.MaxDistance,
                        rental,
                        authoritativeLifetime: true)
                    : new WeaponProjectileVFX(
                        projectileKey,
                        ResolvePresentationPosition(row),
                        new Vector3(row.DirX, row.DirY, row.DirZ),
                        PresentationSpeed(row),
                        row.MaxDistance,
                        template.Scale,
                        template.Prefab,
                        authoritativeLifetime: true);
            });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_activeProjectiles.ContainsKey(projectileKey))
                DebugStartedProjectileVisualCount++;
            RefreshDebugCounts();
#endif
            if (IsOrbitCasterProjectile(row))
                _orbitProjectiles[projectileKey] = OrbitProjectileMotion.From(row);
            else
                _orbitProjectiles.Remove(projectileKey);
            _adoptedPredictedProjectiles.Remove(projectileKey);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terminalProjectileKeys.Remove(projectileKey);
#endif
        }

        public bool TryAdoptPredictedRelease(string predictedProjectileKey, ProjectilePresentationEvent authoritative)
        {
            if (string.IsNullOrWhiteSpace(predictedProjectileKey))
                return false;

            string authoritativeKey = ProjectileKey(authoritative);
            if (string.IsNullOrWhiteSpace(authoritativeKey)
                || string.Equals(predictedProjectileKey, authoritativeKey, StringComparison.Ordinal))
            {
                return _activeProjectiles.ContainsKey(authoritativeKey)
                    || _delayedProjectileStarts.ContainsKey(authoritativeKey);
            }

            if (!_activeProjectiles.TryGetValue(predictedProjectileKey, out ISpellVFX predicted))
            {
                if (!_delayedProjectileStarts.TryGetValue(predictedProjectileKey, out DelayedProjectileStart delayed))
                    return false;

                _delayedProjectileStarts.Remove(predictedProjectileKey);
                _delayedProjectileStarts[authoritativeKey] = new DelayedProjectileStart(
                    authoritative,
                    delayed.StartAtSeconds);
                _adoptedPredictedProjectiles.Remove(predictedProjectileKey);
                _adoptedPredictedProjectiles.Add(authoritativeKey);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _terminalProjectileKeys.Remove(predictedProjectileKey);
                RefreshDebugCounts();
#endif
                return true;
            }

            if (_activeProjectiles.TryGetValue(authoritativeKey, out ISpellVFX oldAuthoritative))
                DisposeVfx(oldAuthoritative);

            _activeProjectiles.Remove(predictedProjectileKey);
            _activeProjectiles[authoritativeKey] = predicted;
            _adoptedPredictedProjectiles.Remove(predictedProjectileKey);
            _adoptedPredictedProjectiles.Add(authoritativeKey);

            if (_orbitProjectiles.TryGetValue(predictedProjectileKey, out OrbitProjectileMotion orbit))
            {
                _orbitProjectiles.Remove(predictedProjectileKey);
                _orbitProjectiles[authoritativeKey] = orbit;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terminalProjectileKeys.Remove(predictedProjectileKey);
            RefreshDebugCounts();
#endif
            return true;
        }

        public void RemovePredicted(string predictedProjectileKey)
        {
            if (string.IsNullOrWhiteSpace(predictedProjectileKey))
                return;

            RemoveProjectile(predictedProjectileKey);
        }

        public void Update(ProjectilePresentationEvent row)
        {
            string projectileKey = ProjectileKey(row);
            if (!_activeProjectiles.TryGetValue(projectileKey, out var vfx))
            {
                Start(row);
                if (!_activeProjectiles.TryGetValue(projectileKey, out vfx))
                    return;
            }

            RouteToVfx(projectileKey, vfx, () =>
            {
                var position = ResolvePresentationPosition(row);
                var direction = new Vector3(row.DirX, row.DirY, row.DirZ);
                if (vfx is WeaponProjectileVFX weaponProjectile
                    && ShouldSnapAuthoritativeVisualUpdate(
                        row.MotionKind,
                        _adoptedPredictedProjectiles.Contains(projectileKey)))
                {
                    weaponProjectile.OnUpdate(position, direction, PresentationSpeed(row), snapToAuthoritative: true);
                }
                else
                {
                    vfx.OnUpdate(position, direction, PresentationSpeed(row));
                }

                if (IsOrbitCasterProjectile(row))
                    _orbitProjectiles[projectileKey] = OrbitProjectileMotion.From(row);
            });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DebugUpdatedProjectileVisualCount++;
            RefreshDebugCounts();
#endif
        }

        public void Impact(ProjectilePresentationEvent row)
        {
            string projectileKey = ProjectileKey(row);
            if (_delayedProjectileStarts.Remove(projectileKey))
            {
                ReleaseAnimatedPropHandoff(row);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                RefreshDebugCounts();
#endif
                return;
            }

            if (_activeProjectiles.TryGetValue(projectileKey, out var vfx))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                DebugTerminalProjectileVisualCount++;
                _terminalProjectileKeys.Add(projectileKey);
#endif
                RouteToVfx(projectileKey, vfx, () =>
                    vfx.OnImpact(new Vector3(row.PointX, row.PointY, row.PointZ)));
            }
        }

        public void Fizzle(ProjectilePresentationEvent row)
        {
            string projectileKey = ProjectileKey(row);
            if (_delayedProjectileStarts.Remove(projectileKey))
            {
                ReleaseAnimatedPropHandoff(row);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                DebugTerminalProjectileVisualCount++;
                _terminalProjectileKeys.Add(projectileKey);
                RefreshDebugCounts();
#endif
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_activeProjectiles.ContainsKey(projectileKey))
            {
                DebugTerminalProjectileVisualCount++;
                _terminalProjectileKeys.Add(projectileKey);
            }
#endif
            RemoveProjectile(projectileKey);
        }

        public void Dispose()
        {
            foreach (var vfx in _activeProjectiles.Values)
                DisposeVfx(vfx);
            _activeProjectiles.Clear();
            _orbitProjectiles.Clear();
            _delayedProjectileStarts.Clear();
            _adoptedPredictedProjectiles.Clear();
            _projectilePool.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terminalProjectileKeys.Clear();
            RefreshDebugCounts();
#endif
        }

        private static Vector3 ResolvePresentationPosition(ProjectilePresentationEvent row)
        {
            var serverProjectilePosition = new Vector3(row.PointX, row.PointY, row.PointZ);
            if (!IsOrbitCasterProjectile(row))
                return serverProjectilePosition;

            var registry = EntityRegistry.Instance;
            var conn = NetworkManager.Instance?.Conn;
            if (registry == null || conn == null)
                return serverProjectilePosition;
            if (!registry.TryGetEntity(row.Caster, out var casterEntity))
                return serverProjectilePosition;

            PlayerPhysics? casterPhysics = conn.Db.PlayerPhysics.Identity.Find(row.Caster);
            if (casterPhysics == null)
                return serverProjectilePosition;

            var serverCasterPosition = new Vector3(casterPhysics.PosX, casterPhysics.PosY, casterPhysics.PosZ);
            Vector3 orbitOffset = serverProjectilePosition - serverCasterPosition;
            return casterEntity.GetPresentationRoot().position + orbitOffset;
        }

        private static bool IsOrbitCasterProjectile(ProjectilePresentationEvent row)
        {
            return string.Equals(
                WireIdentifier.Normalize(row.MotionKind),
                ProjectileMotionOrbitCaster,
                StringComparison.Ordinal);
        }

        internal static bool ShouldSnapAuthoritativeVisualUpdate(string motionKind, bool adoptedPredictedProjectile)
        {
            return !adoptedPredictedProjectile && UsesAuthoritativeVisualPosition(motionKind);
        }

        private static bool UsesAuthoritativeVisualPosition(string motionKind)
        {
            string motion = WireIdentifier.Normalize(motionKind);
            return string.Equals(motion, ProjectileMotionOrbitCaster, StringComparison.Ordinal)
                || string.Equals(motion, ProjectileMotionBoomerangCaster, StringComparison.Ordinal);
        }

        private static float PresentationSpeed(ProjectilePresentationEvent row)
        {
            if (string.Equals(
                    WireIdentifier.Normalize(row.MotionKind),
                    ProjectileMotionBoomerangCaster,
                    StringComparison.Ordinal)
                && row.BoomerangReturning
                && row.BoomerangReturnSpeed > 0f)
            {
                return row.BoomerangReturnSpeed;
            }

            return row.Speed;
        }

        private static void ApplyOrbitMotion(WeaponProjectileVFX vfx, OrbitProjectileMotion orbit)
        {
            var registry = EntityRegistry.Instance;
            if (registry == null || !registry.TryGetEntity(orbit.Caster, out var casterEntity))
                return;

            Vector3 casterPosition = casterEntity.GetPresentationRoot().position;
            float radius = Mathf.Max(0f, orbit.Radius);
            var offset = new Vector3(
                Mathf.Sin(orbit.AngleRadians) * radius,
                orbit.Height,
                Mathf.Cos(orbit.AngleRadians) * radius);
            var direction = new Vector3(
                Mathf.Cos(orbit.AngleRadians),
                0f,
                -Mathf.Sin(orbit.AngleRadians));
            vfx.OnUpdate(casterPosition + offset, direction, 0f, snapToAuthoritative: true);
        }

        private void TickDelayedProjectileStarts()
        {
            if (_delayedProjectileStarts.Count == 0)
                return;

            float now = Time.time;
            _removeList.Clear();
            foreach (var (projectileKey, delayed) in _delayedProjectileStarts)
            {
                if (now >= delayed.StartAtSeconds)
                    _removeList.Add(projectileKey);
            }

            for (int i = 0; i < _removeList.Count; i++)
            {
                string projectileKey = _removeList[i];
                if (!_delayedProjectileStarts.TryGetValue(projectileKey, out DelayedProjectileStart delayed))
                    continue;

                _delayedProjectileStarts.Remove(projectileKey);
                Start(delayed.Row, allowAnimatedPropDelay: false);
            }
        }

        private bool TryDelayAnimatedPropProjectileStart(
            string projectileKey,
            ProjectilePresentationEvent row)
        {
            if (string.IsNullOrWhiteSpace(projectileKey)
                || !string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal)
                || !string.Equals(row.EventType, CombatEventTypes.Release, StringComparison.Ordinal)
                || !TryResolveCasterWeaponAttachments(row.Caster, out WeaponAttachmentController attachments)
                || !attachments.TryGetTemporaryAnimatedPropReleaseDelaySeconds(row.ActionKind, out float delaySeconds))
            {
                return false;
            }

            const float ImmediateHandoffThresholdSeconds = 0.025f;
            if (delaySeconds <= ImmediateHandoffThresholdSeconds)
                return false;

            float startAt = Time.time + delaySeconds;
            if (_delayedProjectileStarts.TryGetValue(projectileKey, out DelayedProjectileStart existing))
                startAt = Mathf.Min(startAt, existing.StartAtSeconds);

            _delayedProjectileStarts[projectileKey] = new DelayedProjectileStart(row, startAt);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RefreshDebugCounts();
#endif
            return true;
        }

        private static void ReleaseAnimatedPropHandoff(ProjectilePresentationEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return;
            if (TryResolveCasterWeaponAttachments(row.Caster, out WeaponAttachmentController attachments))
                attachments.ReleaseTemporaryAnimatedProp(row.ActionKind);
        }

        private static bool TryResolveCasterWeaponAttachments(
            SpacetimeDB.Identity caster,
            out WeaponAttachmentController attachments)
        {
            attachments = null!;
            var registry = EntityRegistry.Instance;
            if (registry == null || !registry.TryGetEntity(caster, out var casterEntity))
                return false;

            attachments = casterEntity.GameObject.GetComponent<WeaponAttachmentController>();
            return attachments != null;
        }

        private void ReplaceProjectile(string projectileKey, string projectileId, Func<CombatVFXRegistry.Template, ISpellVFX> create)
        {
            if (_activeProjectiles.TryGetValue(projectileKey, out var old))
            {
                DisposeVfx(old);
                _activeProjectiles.Remove(projectileKey);
                _orbitProjectiles.Remove(projectileKey);
                _adoptedPredictedProjectiles.Remove(projectileKey);
            }

            CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(projectileId);
            if (template == null)
            {
                string normalizedProjectileId = WireIdentifier.Normalize(projectileId);
                if (string.IsNullOrWhiteSpace(normalizedProjectileId))
                    normalizedProjectileId = "<missing>";
                if (_missingProjectilePrefabWarnings.Add(normalizedProjectileId))
                    Debug.LogWarning($"No CombatVFXRegistry prefab registered for combat projectile id '{normalizedProjectileId}'. Skipping projectile visual.");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                RefreshDebugCounts();
#endif
                return;
            }

            _activeProjectiles[projectileKey] = create(template);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            RefreshDebugCounts();
#endif
        }

        private void RouteToVfx(string projectileId, ISpellVFX vfx, Action route)
        {
            try
            {
                route();
            }
            catch (MissingReferenceException)
            {
                DisposeVfx(vfx);
                _activeProjectiles.Remove(projectileId);
            }
        }

        private void RemoveProjectile(string projectileKey)
        {
            if (_delayedProjectileStarts.Remove(projectileKey))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _terminalProjectileKeys.Remove(projectileKey);
                RefreshDebugCounts();
#endif
                return;
            }

            if (!_activeProjectiles.TryGetValue(projectileKey, out var vfx))
                return;

            DisposeVfx(vfx);
            _activeProjectiles.Remove(projectileKey);
            _orbitProjectiles.Remove(projectileKey);
            _adoptedPredictedProjectiles.Remove(projectileKey);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _terminalProjectileKeys.Remove(projectileKey);
            RefreshDebugCounts();
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void RefreshDebugCounts()
        {
            DebugActiveProjectileVisualCount = _activeProjectiles.Count;
            DebugMissingProjectilePrefabCount = _missingProjectilePrefabWarnings.Count;
        }
#endif

        private static string ProjectileKey(ProjectilePresentationEvent row)
        {
            return string.IsNullOrWhiteSpace(row.ProjectileInstanceId)
                ? row.ActionInstanceId
                : row.ProjectileInstanceId;
        }

        private static void DisposeVfx(ISpellVFX vfx)
        {
            try
            {
                vfx.Dispose();
            }
            catch (MissingReferenceException)
            {
            }
        }

        private sealed class OrbitProjectileMotion
        {
            public SpacetimeDB.Identity Caster { get; }
            public float AngleRadians { get; private set; }
            public float Radius { get; }
            public float Height { get; }
            private float AngularSpeedRadiansPerSecond { get; }

            private OrbitProjectileMotion(
                SpacetimeDB.Identity caster,
                float angleRadians,
                float radius,
                float height,
                float angularSpeedRadiansPerSecond)
            {
                Caster = caster;
                AngleRadians = angleRadians;
                Radius = radius;
                Height = height;
                AngularSpeedRadiansPerSecond = angularSpeedRadiansPerSecond;
            }

            public static OrbitProjectileMotion From(ProjectilePresentationEvent row)
            {
                return new OrbitProjectileMotion(
                    row.Caster,
                    ResolveAngleRadians(row),
                    row.OrbitRadius,
                    row.OrbitHeight,
                    row.OrbitAngularSpeedDegPerSec * Mathf.Deg2Rad);
            }

            public void Advance(float dt)
            {
                AngleRadians += AngularSpeedRadiansPerSecond * Mathf.Max(0f, dt);
            }

            private static float ResolveAngleRadians(ProjectilePresentationEvent row)
            {
                var conn = NetworkManager.Instance?.Conn;
                PlayerPhysics? casterPhysics = conn?.Db.PlayerPhysics.Identity.Find(row.Caster);
                if (casterPhysics != null)
                {
                    float dx = row.PointX - casterPhysics.PosX;
                    float dz = row.PointZ - casterPhysics.PosZ;
                    if (dx * dx + dz * dz > 0.0001f)
                        return Mathf.Atan2(dx, dz);
                }

                return row.OrbitInitialYaw + row.OrbitPhaseOffsetDeg * Mathf.Deg2Rad;
            }
        }
    }
}
