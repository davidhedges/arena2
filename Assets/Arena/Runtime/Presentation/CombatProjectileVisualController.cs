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
        private const string ProjectileMotionCurvedTarget = "CURVED_TARGET";
        private const float OrbitRetargetSeconds = 0.2f;

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
        private readonly Dictionary<string, CurvedTargetProjectileMotion> _curvedTargetProjectiles = new();
        private readonly Dictionary<string, DelayedProjectileStart> _delayedProjectileStarts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _adoptedPredictedProjectiles = new(StringComparer.Ordinal);
        // Visual-only launch offset: spawn the projectile body at a caster socket (e.g. the
        // left hand, per its PROJECTILE_BODY cue anchor) and decay the offset to zero over
        // LaunchBlendSeconds so it converges onto the authoritative curve. Gameplay/collision
        // stay authoritative — this only nudges the rendered position for the first frames.
        private readonly Dictionary<string, LaunchOffset> _launchOffsets = new(StringComparer.Ordinal);
        private const float LaunchBlendSeconds = 0.15f;
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

        private readonly struct LaunchOffset
        {
            public LaunchOffset(Vector3 offset, float spawnTimeSeconds)
            {
                Offset = offset;
                SpawnTimeSeconds = spawnTimeSeconds;
            }

            public Vector3 Offset { get; }
            public float SpawnTimeSeconds { get; }
        }

        // Adds the decaying launch offset (if any) to an authoritative position, and clears
        // the entry once the blend completes. Returns basePosition unchanged when none.
        private Vector3 ApplyLaunchOffset(string projectileKey, Vector3 basePosition)
        {
            if (!_launchOffsets.TryGetValue(projectileKey, out LaunchOffset launch))
                return basePosition;

            float t = LaunchBlendSeconds > 0f
                ? Mathf.Clamp01((Time.time - launch.SpawnTimeSeconds) / LaunchBlendSeconds)
                : 1f;
            if (t >= 1f)
            {
                _launchOffsets.Remove(projectileKey);
                return basePosition;
            }

            return basePosition + launch.Offset * (1f - t);
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
                        ApplyOrbitMotion(projectileId, weaponProjectile, orbit);
                    }
                    else if (_curvedTargetProjectiles.TryGetValue(projectileId, out var curve)
                        && vfx is WeaponProjectileVFX curvedProjectile)
                    {
                        curve.Advance(dt);
                        ApplyCurvedTargetMotion(projectileId, curvedProjectile, curve);
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
                _curvedTargetProjectiles.Remove(id);
                _adoptedPredictedProjectiles.Remove(id);
                _launchOffsets.Remove(id);
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
            Start(row, handLaunchOrigin: null, allowAnimatedPropDelay: true);
        }

        public void Start(ProjectilePresentationEvent row, Vector3? handLaunchOrigin)
        {
            Start(row, handLaunchOrigin, allowAnimatedPropDelay: true);
        }

        private void Start(ProjectilePresentationEvent row, Vector3? handLaunchOrigin, bool allowAnimatedPropDelay)
        {
            string projectileKey = ProjectileKey(row);
            if (allowAnimatedPropDelay
                && TryDelayAnimatedPropProjectileStart(projectileKey, row))
            {
                return;
            }

            ReleaseAnimatedPropHandoff(row);
            if (handLaunchOrigin.HasValue)
                _launchOffsets[projectileKey] = new LaunchOffset(
                    handLaunchOrigin.Value - ResolvePresentationPosition(row),
                    Time.time);
            else
                _launchOffsets.Remove(projectileKey);
            ReplaceProjectile(projectileKey, row.ProjectileId, row.ProjectileTrailVfxId ?? string.Empty, (template, trailTemplate) =>
            {
                ProjectileVfxPool.Rental? rental = _projectilePool.TryRent(template, trailTemplate, projectileKey);
                return rental != null
                    ? new WeaponProjectileVFX(
                        projectileKey,
                        ApplyLaunchOffset(projectileKey, ResolvePresentationPosition(row)),
                        new Vector3(row.DirX, row.DirY, row.DirZ),
                        PresentationSpeed(row),
                        row.MaxDistance,
                        rental,
                        template.ScaleMultiplierAtLifetimeEnd,
                        authoritativeLifetime: true)
                    : new WeaponProjectileVFX(
                        projectileKey,
                        ApplyLaunchOffset(projectileKey, ResolvePresentationPosition(row)),
                        new Vector3(row.DirX, row.DirY, row.DirZ),
                        PresentationSpeed(row),
                        row.MaxDistance,
                        template.Scale,
                        template.Prefab,
                        trailTemplate,
                        template.ScaleMultiplierAtLifetimeEnd,
                        authoritativeLifetime: true,
                        followAuthoritativeProjectileMotion: template.FollowAuthoritativeProjectileMotion);
            });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_activeProjectiles.ContainsKey(projectileKey))
                DebugStartedProjectileVisualCount++;
            RefreshDebugCounts();
#endif
            if (IsOrbitCasterProjectile(row))
            {
                _orbitProjectiles[projectileKey] = OrbitProjectileMotion.From(row);
                _curvedTargetProjectiles.Remove(projectileKey);
            }
            else if (IsCurvedTargetProjectile(row))
            {
                _curvedTargetProjectiles[projectileKey] = CurvedTargetProjectileMotion.From(row);
                _orbitProjectiles.Remove(projectileKey);
            }
            else
            {
                _orbitProjectiles.Remove(projectileKey);
                _curvedTargetProjectiles.Remove(projectileKey);
            }
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
            if (_curvedTargetProjectiles.TryGetValue(predictedProjectileKey, out CurvedTargetProjectileMotion curve))
            {
                _curvedTargetProjectiles.Remove(predictedProjectileKey);
                _curvedTargetProjectiles[authoritativeKey] = curve;
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
                if (IsOrbitCasterProjectile(row)
                    && _orbitProjectiles.TryGetValue(projectileKey, out OrbitProjectileMotion existingOrbit))
                {
                    // Orbit updates can re-space an accumulated ring. Retarget the local orbit
                    // simulation instead of snapping the rendered orb across the caster.
                    existingOrbit.Retarget(row);
                    _curvedTargetProjectiles.Remove(projectileKey);
                    return;
                }

                var position = ApplyLaunchOffset(projectileKey, ResolvePresentationPosition(row));
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
                {
                    _orbitProjectiles[projectileKey] = OrbitProjectileMotion.From(row);
                    _curvedTargetProjectiles.Remove(projectileKey);
                }
                else if (IsCurvedTargetProjectile(row))
                {
                    _curvedTargetProjectiles[projectileKey] = CurvedTargetProjectileMotion.From(row);
                    _orbitProjectiles.Remove(projectileKey);
                }
                else
                {
                    _orbitProjectiles.Remove(projectileKey);
                    _curvedTargetProjectiles.Remove(projectileKey);
                }
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
            _curvedTargetProjectiles.Clear();
            _delayedProjectileStarts.Clear();
            _adoptedPredictedProjectiles.Clear();
            _launchOffsets.Clear();
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

        private static bool IsCurvedTargetProjectile(ProjectilePresentationEvent row)
        {
            return string.Equals(
                WireIdentifier.Normalize(row.MotionKind),
                ProjectileMotionCurvedTarget,
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
                || string.Equals(motion, ProjectileMotionBoomerangCaster, StringComparison.Ordinal)
                || string.Equals(motion, ProjectileMotionCurvedTarget, StringComparison.Ordinal);
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

        private void ApplyOrbitMotion(string projectileKey, WeaponProjectileVFX vfx, OrbitProjectileMotion orbit)
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
            vfx.OnUpdate(ApplyLaunchOffset(projectileKey, casterPosition + offset), direction, 0f, snapToAuthoritative: true);
        }

        private void ApplyCurvedTargetMotion(string projectileKey, WeaponProjectileVFX vfx, CurvedTargetProjectileMotion curve)
        {
            vfx.OnUpdate(ApplyLaunchOffset(projectileKey, curve.Position), curve.Direction, 0f, snapToAuthoritative: true);
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
                Start(delayed.Row, handLaunchOrigin: null, allowAnimatedPropDelay: false);
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

        private void ReplaceProjectile(
            string projectileKey,
            string projectileId,
            string projectileTrailVfxId,
            Func<CombatVFXRegistry.Template, CombatVFXRegistry.Template?, ISpellVFX> create)
        {
            if (_activeProjectiles.TryGetValue(projectileKey, out var old))
            {
                DisposeVfx(old);
                _activeProjectiles.Remove(projectileKey);
                _orbitProjectiles.Remove(projectileKey);
                _curvedTargetProjectiles.Remove(projectileKey);
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

            CombatVFXRegistry.Template? trailTemplate = null;
            string normalizedTrailId = WireIdentifier.Normalize(projectileTrailVfxId);
            if (!string.IsNullOrWhiteSpace(normalizedTrailId))
            {
                trailTemplate = CombatVFXTemplateRegistry.ResolveTemplate(normalizedTrailId);
                if (trailTemplate == null && _missingProjectilePrefabWarnings.Add($"trail:{normalizedTrailId}"))
                    Debug.LogWarning($"No CombatVFXRegistry prefab registered for projectile trail id '{normalizedTrailId}'. Continuing with the projectile body only.");
            }

            _activeProjectiles[projectileKey] = create(template, trailTemplate);
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
            _curvedTargetProjectiles.Remove(projectileKey);
            _adoptedPredictedProjectiles.Remove(projectileKey);
            _launchOffsets.Remove(projectileKey);
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
            public float Radius { get; private set; }
            public float Height { get; private set; }
            private float AngularSpeedRadiansPerSecond { get; set; }
            private float TargetAngleRadians { get; set; }
            private float RetargetRemainingSeconds { get; set; }

            private OrbitProjectileMotion(
                SpacetimeDB.Identity caster,
                float angleRadians,
                float radius,
                float height,
                float angularSpeedRadiansPerSecond)
            {
                Caster = caster;
                AngleRadians = angleRadians;
                TargetAngleRadians = angleRadians;
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
                float safeDt = Mathf.Max(0f, dt);
                float angularAdvance = AngularSpeedRadiansPerSecond * safeDt;
                AngleRadians += angularAdvance;
                TargetAngleRadians += angularAdvance;

                if (RetargetRemainingSeconds <= 0f)
                {
                    TargetAngleRadians = AngleRadians;
                    return;
                }

                float blend = Mathf.Clamp01(safeDt / RetargetRemainingSeconds);
                AngleRadians = Mathf.Lerp(AngleRadians, TargetAngleRadians, blend);
                RetargetRemainingSeconds = Mathf.Max(0f, RetargetRemainingSeconds - safeDt);
            }

            public void Retarget(ProjectilePresentationEvent row)
            {
                float authoritativeAngle = ResolveAngleRadians(row);
                float shortestDelta = Mathf.DeltaAngle(
                    AngleRadians * Mathf.Rad2Deg,
                    authoritativeAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                TargetAngleRadians = AngleRadians + shortestDelta;
                RetargetRemainingSeconds = OrbitRetargetSeconds;
                Radius = row.OrbitRadius;
                Height = row.OrbitHeight;
                AngularSpeedRadiansPerSecond = row.OrbitAngularSpeedDegPerSec * Mathf.Deg2Rad;
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

        private sealed class CurvedTargetProjectileMotion
        {
            private Vector3 Origin { get; }
            private Vector3 Control { get; }
            private Vector3 End { get; }
            private float Speed { get; }
            private float MaxDistance { get; }
            private float Progress { get; set; }

            public Vector3 Position => Evaluate(Progress);
            public Vector3 Direction
            {
                get
                {
                    Vector3 tangent = EvaluateDerivative(Progress);
                    return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.forward;
                }
            }

            private CurvedTargetProjectileMotion(
                Vector3 origin,
                Vector3 control,
                Vector3 end,
                float speed,
                float maxDistance,
                float progress)
            {
                Origin = origin;
                Control = control;
                End = end;
                Speed = Mathf.Max(0f, speed);
                MaxDistance = Mathf.Max(0.001f, maxDistance);
                Progress = Mathf.Clamp01(progress);
            }

            public static CurvedTargetProjectileMotion From(ProjectilePresentationEvent row)
            {
                return new CurvedTargetProjectileMotion(
                    new Vector3(row.OriginX, row.OriginY, row.OriginZ),
                    new Vector3(row.CurveControlX, row.CurveControlY, row.CurveControlZ),
                    new Vector3(row.CurveEndX, row.CurveEndY, row.CurveEndZ),
                    row.Speed,
                    row.MaxDistance,
                    row.CurveProgress);
            }

            public void Advance(float dt)
            {
                Progress = Mathf.Clamp01(Progress + Speed / MaxDistance * Mathf.Max(0f, dt));
            }

            private Vector3 Evaluate(float progress)
            {
                float t = Mathf.Clamp01(progress);
                float oneMinus = 1f - t;
                return oneMinus * oneMinus * Origin
                    + 2f * oneMinus * t * Control
                    + t * t * End;
            }

            private Vector3 EvaluateDerivative(float progress)
            {
                float t = Mathf.Clamp01(progress);
                return 2f * (1f - t) * (Control - Origin)
                    + 2f * t * (End - Control);
            }
        }
    }
}
