#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Arena.Debugging;
#endif
using Arena.Entity;
using Arena.Input;
using Arena.Network;
using Arena.Simulation;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    public sealed class CombatVFXDispatcher : MonoBehaviour
    {
        private const string TriggerMeleeCast = "MELEE_CAST";
        private const string TriggerMeleeImpact = "MELEE_IMPACT";
        private const string TriggerMeleeBlock = "MELEE_BLOCK";
        private const string TriggerMeleeParry = "MELEE_PARRY";
        private const string TriggerAreaImpact = "AREA_IMPACT";
        private const string TriggerSpellCast = "SPELL_CAST";
        private const string TriggerSpellRelease = "SPELL_RELEASE";
        private const string TriggerSpellImpact = "SPELL_IMPACT";
        private const string TriggerSpellBlock = "SPELL_BLOCK";
        private const string TriggerSpellParry = "SPELL_PARRY";
        private const string TriggerSpellFizzle = "SPELL_FIZZLE";
        private const string AttachModeSpawnWorld = "SPAWN_WORLD";
        private const string AttachModeFollowAnchor = "FOLLOW_ANCHOR";
        private const string AttachModeWorldAlignedToFacing = "WORLD_ALIGNED_TO_FACING";
        private const string VfxRoleProjectileBody = "PROJECTILE_BODY";
        private const string VfxRoleTravelBody = "TRAVEL_BODY";
        private const string SpellBehaviorProjectile = "PROJECTILE";
        private const string SpellBehaviorArea = "AREA";
        private const string ProjectileMotionBoomerangCaster = "BOOMERANG_CASTER";
        private const long PredictedSpellVfxTtlMs = 5000L;

        private static CombatVFXDispatcher? _instance;

        private List<CombatVfxCueCatalog>? _matchingCues;
        private CombatVfxCueResolver.Index? _cueResolver;
        private CombatProjectileVisualController? _projectileVisuals;
        private CombatTravelVisualController? _travelVisuals;
        private CombatVFXLifecycleRegistry? _lifecycle;
        private DbConnection? _subscribedConnection;
        private readonly Dictionary<string, PendingPredictedSpellVfx> _pendingSpellVfxByToken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _spellVfxTokenByActionInstance = new(StringComparer.Ordinal);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            CombatVFXDispatcher? existing = FindAnyObjectByType<CombatVFXDispatcher>();
            if (existing != null)
            {
                _instance = existing;
                DontDestroyOnLoad(existing.gameObject);
                return;
            }

            var go = new GameObject("CombatVFXDispatcher");
            DontDestroyOnLoad(go);
            go.AddComponent<CombatVFXDispatcher>();
        }

        private List<CombatVfxCueCatalog> MatchingCues => _matchingCues ??= new List<CombatVfxCueCatalog>();
        private CombatVfxCueResolver.Index CueResolver => _cueResolver ??= new CombatVfxCueResolver.Index();
        private CombatProjectileVisualController ProjectileVisuals => _projectileVisuals ??= new CombatProjectileVisualController();
        private CombatTravelVisualController TravelVisuals => _travelVisuals ??= new CombatTravelVisualController();

        private readonly struct PendingPredictedSpellVfx
        {
            public PendingPredictedSpellVfx(
                string tokenKey,
                string spellId,
                string predictedActionInstanceId,
                string predictedProjectileKey,
                bool suppressAuthoritativeRelease,
                bool suppressAuthoritativeAreaImpact,
                long expiresAtMs)
            {
                TokenKey = tokenKey;
                SpellId = WireIdentifier.Normalize(spellId);
                PredictedActionInstanceId = predictedActionInstanceId;
                PredictedProjectileKey = predictedProjectileKey;
                SuppressAuthoritativeRelease = suppressAuthoritativeRelease;
                SuppressAuthoritativeAreaImpact = suppressAuthoritativeAreaImpact;
                ExpiresAtMs = expiresAtMs;
            }

            public string TokenKey { get; }
            public string SpellId { get; }
            public string PredictedActionInstanceId { get; }
            public string PredictedProjectileKey { get; }
            public bool SuppressAuthoritativeRelease { get; }
            public bool SuppressAuthoritativeAreaImpact { get; }
            public long ExpiresAtMs { get; }

            public PendingPredictedSpellVfx WithProjectileKey(string predictedProjectileKey)
            {
                return new PendingPredictedSpellVfx(
                    TokenKey,
                    SpellId,
                    PredictedActionInstanceId,
                    predictedProjectileKey,
                    SuppressAuthoritativeRelease,
                    SuppressAuthoritativeAreaImpact,
                    ExpiresAtMs);
            }

            public PendingPredictedSpellVfx WithSuppressedAuthoritativeTrigger(string trigger)
            {
                return new PendingPredictedSpellVfx(
                    TokenKey,
                    SpellId,
                    PredictedActionInstanceId,
                    PredictedProjectileKey,
                    SuppressAuthoritativeRelease
                        || string.Equals(trigger, TriggerSpellRelease, StringComparison.Ordinal),
                    SuppressAuthoritativeAreaImpact
                        || string.Equals(trigger, TriggerAreaImpact, StringComparison.Ordinal),
                    ExpiresAtMs);
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            _lifecycle.Tick(Time.deltaTime);
            TravelVisuals.Tick(Time.deltaTime);
            ProjectileVisuals.Tick(Time.deltaTime);
            PrunePredictedSpellVfx(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var conn = NetworkManager.Instance?.Conn;
            if (_subscribedConnection != null && !ReferenceEquals(_subscribedConnection, conn))
                UnsubscribeFromConnection();
            if (_subscribedConnection != null)
                return;
            if (conn == null)
                return;

            _subscribedConnection = conn;
            CueResolver.MarkDirty();
            conn.Db.CombatEvent.OnInsert += OnCombatEventInsert;
            conn.Db.ProjectilePresentationEvent.OnInsert += OnProjectilePresentationEventInsert;
            conn.Db.PredictedActionResult.OnInsert += OnPredictedActionResultInsert;
            conn.Db.CombatVfxCueCatalog.OnInsert += OnCombatVfxCueCatalogInsert;
            conn.Db.CombatVfxCueCatalog.OnUpdate += OnCombatVfxCueCatalogUpdate;
            conn.Db.CombatVfxCueCatalog.OnDelete += OnCombatVfxCueCatalogDelete;
        }

        public static void PredictLocalInstantSpellRelease(
            DbConnection conn,
            string spellId,
            SpellDefinition spellDef,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            _instance?.PredictLocalInstantSpellReleaseInternal(conn, spellId, spellDef, targetId, aimPoint, token);
        }

        private void PredictLocalInstantSpellReleaseInternal(
            DbConnection conn,
            string spellId,
            SpellDefinition spellDef,
            string targetId,
            Vector3? aimPoint,
            CastActionToken token)
        {
            if (!token.IsPredicted
                || spellDef.CastTimeMs > 0
                || SpellDefinitionContracts.CastsOnRelease(spellDef))
            {
                return;
            }

            PlayerEntity? caster = EntityRegistry.Instance?.LocalPlayerEntity;
            if (caster == null || caster.IsDestroyed || !caster.IsAlive)
                return;

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (string.IsNullOrWhiteSpace(normalizedSpellId))
                return;

            string tokenKey = SpellTokenKey(token.PredictedCastId, token.ClientActionSeq);
            string predictedActionInstanceId = PredictedActionInstanceId(tokenKey);
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string abilityId = ResolveLocalAbilityId(conn, caster.Identity, normalizedSpellId);

            var pending = new PendingPredictedSpellVfx(
                tokenKey,
                normalizedSpellId,
                predictedActionInstanceId,
                string.Empty,
                suppressAuthoritativeRelease: false,
                suppressAuthoritativeAreaImpact: false,
                nowMs + PredictedSpellVfxTtlMs);

            if (TryBuildPredictedReleaseFact(
                    caster,
                    normalizedSpellId,
                    abilityId,
                    spellDef,
                    targetId,
                    aimPoint,
                    predictedActionInstanceId,
                    out CombatVfxFact releaseFact))
            {
                DispatchPredictedSpellFact(conn, spellDef, releaseFact, ref pending);
            }

            if (TryBuildPredictedAreaImpactFact(
                    caster,
                    normalizedSpellId,
                    abilityId,
                    spellDef,
                    aimPoint,
                    predictedActionInstanceId,
                    out CombatVfxFact areaImpactFact))
            {
                DispatchPredictedSpellFact(conn, spellDef, areaImpactFact, ref pending);
            }

            if (pending.SuppressAuthoritativeRelease
                || pending.SuppressAuthoritativeAreaImpact
                || !string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                _pendingSpellVfxByToken[tokenKey] = pending;
        }

        private void DispatchPredictedSpellFact(
            DbConnection conn,
            SpellDefinition spellDef,
            CombatVfxFact fact,
            ref PendingPredictedSpellVfx pending)
        {
            List<CombatVfxCueCatalog> matchingCues = MatchingCues;
            CueResolver.Resolve(conn.Db.CombatVfxCueCatalog.Iter(), fact.ToResolutionFact(), matchingCues);
            if (matchingCues.Count == 0)
                return;

            foreach (CombatVfxCueCatalog cue in matchingCues)
            {
                string role = WireIdentifier.Normalize(cue.VfxRole);
                if (string.Equals(role, VfxRoleProjectileBody, StringComparison.Ordinal))
                {
                    if (TryStartPredictedProjectile(cue, fact, pending, spellDef, out string predictedProjectileKey))
                        pending = pending.WithProjectileKey(predictedProjectileKey);
                    continue;
                }
                if (string.Equals(role, VfxRoleTravelBody, StringComparison.Ordinal))
                    continue;

                DispatchCue(fact, cue);
                pending = pending.WithSuppressedAuthoritativeTrigger(fact.Trigger);
            }
        }

        private void OnCombatVfxCueCatalogInsert(EventContext ctx, CombatVfxCueCatalog row)
        {
            _ = ctx;
            _ = row;
            CueResolver.MarkDirty();
        }

        private void OnCombatVfxCueCatalogUpdate(EventContext ctx, CombatVfxCueCatalog oldRow, CombatVfxCueCatalog newRow)
        {
            _ = ctx;
            _ = oldRow;
            _ = newRow;
            CueResolver.MarkDirty();
        }

        private void OnCombatVfxCueCatalogDelete(EventContext ctx, CombatVfxCueCatalog row)
        {
            _ = ctx;
            _ = row;
            CueResolver.MarkDirty();
        }

        private void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            _ = ctx;
            if (row.Family != PredictedActionFamily.SpellCast)
                return;

            string tokenKey = SpellTokenKey(row.PredictedActionId, row.ClientActionSeq);
            if (!_pendingSpellVfxByToken.TryGetValue(tokenKey, out PendingPredictedSpellVfx pending))
                return;

            if (row.Result == ActionResultKind.Accepted)
            {
                if (!string.IsNullOrWhiteSpace(row.ActionInstanceId))
                    _spellVfxTokenByActionInstance[row.ActionInstanceId] = tokenKey;
                return;
            }

            if (!string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                ProjectileVisuals.RemovePredicted(pending.PredictedProjectileKey);
            _pendingSpellVfxByToken.Remove(tokenKey);
        }

        private void OnCombatEventInsert(EventContext ctx, CombatEvent row)
        {
            _ = ctx;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CombatPresentationDebugGate.ShouldSuppress(row))
                return;
#endif

            if (EntityRegistry.Instance == null)
                return;

            if (!EntityRegistry.Instance.IsIdentityVisible(row.Caster)
                && !EntityRegistry.Instance.IsIdentityVisible(row.Hit))
                return;

            if (row.EventType == CombatEventTypes.Update)
                RouteScriptedUpdate(row);

            if (ShouldSuppressPredictedLocalSpellEvent(row))
                return;

            CombatVfxFact? fact = BuildFact(row);
            if (fact == null)
                return;

            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            if (IsTerminalEvent(row))
            {
                bool fizzled =
                    string.Equals(row.EventType, CombatEventTypes.Fizzle, StringComparison.Ordinal)
                    || string.Equals(row.EventType, CombatEventTypes.Miss, StringComparison.Ordinal);
                if (fizzled)
                    TravelVisuals.Fizzle(fact.Value.ToTemplateContext(string.Empty));
                else
                    TravelVisuals.Impact(fact.Value.ToTemplateContext(string.Empty));

                _lifecycle.RouteTerminal(
                    fact.Value.ToTemplateContext(string.Empty),
                    fizzled);
            }
            else if (row.EventType == CombatEventTypes.Release)
            {
                _lifecycle.RouteRelease(fact.Value.ToTemplateContext(string.Empty));
            }

            DispatchFact(fact.Value);
        }

        private void OnProjectilePresentationEventInsert(EventContext ctx, ProjectilePresentationEvent row)
        {
            _ = ctx;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (CombatPresentationDebugGate.ShouldSuppress(row))
                return;
#endif

            if (EntityRegistry.Instance == null)
                return;

            if (!EntityRegistry.Instance.IsIdentityVisible(row.Caster)
                && !EntityRegistry.Instance.IsIdentityVisible(row.Hit))
                return;

            if (TryAdoptPredictedLocalProjectileRelease(row))
                return;

            DispatchProjectileEvent(row);
        }

        private void RouteScriptedUpdate(CombatEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return;

            CombatVfxFact? fact = BuildFact(row, allowMissingTrigger: true);
            if (fact == null)
                return;

            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            _lifecycle.RouteUpdate(fact.Value.ToTemplateContext(string.Empty));
        }

        private void DispatchProjectileEvent(ProjectilePresentationEvent row)
        {
            switch (row.EventType)
            {
                case CombatEventTypes.Cast:
                case CombatEventTypes.Release:
                    ProjectileVisuals.Start(row);
                    break;
                case CombatEventTypes.Update:
                    ProjectileVisuals.Update(row);
                    break;
                case CombatEventTypes.Contact:
                    DispatchProjectileContactCue(row);
                    break;
                case CombatEventTypes.Impact:
                case CombatEventTypes.Block:
                case CombatEventTypes.Parry:
                    if (row.Terminal)
                        ProjectileVisuals.Impact(row);
                    break;
                case CombatEventTypes.Miss:
                case CombatEventTypes.Fizzle:
                    ProjectileVisuals.Fizzle(row);
                    break;
            }
        }

        private void DispatchProjectileContactCue(ProjectilePresentationEvent row)
        {
            if (!string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal))
                return;

            CombatVfxFact? fact = BuildProjectileContactFact(row);
            if (fact == null)
                return;

            DispatchFact(fact.Value);
        }

        private void DispatchFact(CombatVfxFact fact)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            List<CombatVfxCueCatalog> matchingCues = MatchingCues;
            CueResolver.Resolve(conn.Db.CombatVfxCueCatalog.Iter(), fact.ToResolutionFact(), matchingCues);

            if (matchingCues.Count == 0)
                return;

            foreach (CombatVfxCueCatalog cue in matchingCues)
                DispatchCue(fact, cue);
        }

        private static CombatVfxFact? BuildFact(CombatEvent row, bool allowMissingTrigger = false)
        {
            bool isSpell = string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal);
            string trigger = ResolveTrigger(row.EventType, isSpell);
            if (string.IsNullOrWhiteSpace(trigger) && !allowMissingTrigger)
                return null;

            var conn = NetworkManager.Instance?.Conn;
            string spellId = isSpell ? WireIdentifier.Normalize(row.ActionKind) : string.Empty;
            string abilityId = WireIdentifier.Normalize(row.AbilityId);
            string strikeId = !isSpell && conn != null
                ? ResolveAuthoredStrikeId(conn, row)
                : string.Empty;
            if (isSpell && string.IsNullOrWhiteSpace(spellId))
                return null;
            if (!isSpell && string.IsNullOrWhiteSpace(strikeId))
                return null;

            return new CombatVfxFact(
                row,
                trigger,
                spellId,
                abilityId,
                strikeId,
                isSpell ? -1 : row.HitIndex);
        }

        private static CombatVfxFact? BuildProjectileContactFact(ProjectilePresentationEvent row)
        {
            string spellId = WireIdentifier.Normalize(row.ActionKind);
            string abilityId = WireIdentifier.Normalize(row.AbilityId);
            if (string.IsNullOrWhiteSpace(spellId))
                return null;

            return new CombatVfxFact(
                row,
                TriggerSpellImpact,
                spellId,
                abilityId,
                string.Empty,
                -1);
        }

        private bool ShouldSuppressPredictedLocalSpellEvent(CombatEvent row)
        {
            if (!IsLocalAuthoritativePredictedSpellEvent(row))
                return false;

            if (!TryResolvePredictedSpellVfx(row.ActionInstanceId, row.ActionKind, out PendingPredictedSpellVfx pending))
                return false;

            if (!string.IsNullOrWhiteSpace(row.ActionInstanceId))
                _spellVfxTokenByActionInstance[row.ActionInstanceId] = pending.TokenKey;

            if (string.Equals(row.EventType, CombatEventTypes.AreaImpact, StringComparison.Ordinal))
                return pending.SuppressAuthoritativeAreaImpact;

            return pending.SuppressAuthoritativeRelease;
        }

        private bool TryAdoptPredictedLocalProjectileRelease(ProjectilePresentationEvent row)
        {
            if (!IsLocalAuthoritativeSpellProjectileRelease(row))
                return false;

            if (!TryResolvePredictedSpellVfx(row.ActionInstanceId, row.ActionKind, out PendingPredictedSpellVfx pending))
                return false;

            if (string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                return false;

            if (!ProjectileVisuals.TryAdoptPredictedRelease(pending.PredictedProjectileKey, row))
                return false;

            if (!string.IsNullOrWhiteSpace(row.ActionInstanceId))
                _spellVfxTokenByActionInstance[row.ActionInstanceId] = pending.TokenKey;

            _pendingSpellVfxByToken.Remove(pending.TokenKey);
            return true;
        }

        private bool TryResolvePredictedSpellVfx(
            string actionInstanceId,
            string spellId,
            out PendingPredictedSpellVfx pending)
        {
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrWhiteSpace(actionInstanceId)
                && _spellVfxTokenByActionInstance.TryGetValue(actionInstanceId, out string tokenKey)
                && _pendingSpellVfxByToken.TryGetValue(tokenKey, out pending)
                && nowMs <= pending.ExpiresAtMs)
            {
                return true;
            }

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            foreach (PendingPredictedSpellVfx candidate in _pendingSpellVfxByToken.Values)
            {
                if (nowMs > candidate.ExpiresAtMs)
                    continue;
                if (!string.Equals(candidate.SpellId, normalizedSpellId, StringComparison.Ordinal))
                    continue;

                pending = candidate;
                return true;
            }

            pending = default;
            return false;
        }

        private static bool IsLocalAuthoritativePredictedSpellEvent(CombatEvent row)
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local != null
                && row.Caster == local.Identity
                && string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal)
                && (string.Equals(row.EventType, CombatEventTypes.Release, StringComparison.Ordinal)
                    || string.Equals(row.EventType, CombatEventTypes.AreaImpact, StringComparison.Ordinal));
        }

        private static bool IsLocalAuthoritativeSpellProjectileRelease(ProjectilePresentationEvent row)
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local != null
                && row.Caster == local.Identity
                && string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal)
                && string.Equals(row.EventType, CombatEventTypes.Release, StringComparison.Ordinal);
        }

        private static bool TryBuildPredictedReleaseFact(
            PlayerEntity caster,
            string spellId,
            string abilityId,
            SpellDefinition spellDef,
            string targetId,
            Vector3? aimPoint,
            string predictedActionInstanceId,
            out CombatVfxFact fact)
        {
            Vector3 origin = ResolveLocalCasterPosition(caster);
            Vector3 point = aimPoint ?? origin;
            Vector3 direction = ResolveLocalCasterForward(caster);

            if (string.Equals(WireIdentifier.Normalize(spellDef.Behavior), SpellBehaviorProjectile, StringComparison.Ordinal))
            {
                if (!TryResolvePredictedProjectileVectors(caster, spellDef, targetId, out origin, out direction, out point))
                {
                    fact = default;
                    return false;
                }
            }
            else if (string.Equals(WireIdentifier.Normalize(spellDef.Behavior), SpellBehaviorArea, StringComparison.Ordinal))
            {
                if (!aimPoint.HasValue && !SpellDefinitionContracts.UsesSelfTargeting(spellDef))
                {
                    fact = default;
                    return false;
                }
            }

            fact = new CombatVfxFact(
                TriggerSpellRelease,
                spellId,
                abilityId,
                string.Empty,
                -1,
                caster.Identity,
                default,
                predictedActionInstanceId,
                spellId,
                origin,
                direction,
                point,
                spellDef.Speed,
                ResolvePredictedMaxDistance(spellDef),
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: true);
            return true;
        }

        private static bool TryBuildPredictedAreaImpactFact(
            PlayerEntity caster,
            string spellId,
            string abilityId,
            SpellDefinition spellDef,
            Vector3? aimPoint,
            string predictedActionInstanceId,
            out CombatVfxFact fact)
        {
            if (!string.Equals(WireIdentifier.Normalize(spellDef.Behavior), SpellBehaviorArea, StringComparison.Ordinal)
                || !SpellDefinitionContracts.UsesSelfTargeting(spellDef))
            {
                fact = default;
                return false;
            }

            Vector3 origin = ResolveLocalCasterGroundPosition(caster);
            Vector3 direction = ResolveLocalCasterForward(caster);
            Vector3 point = aimPoint ?? origin;

            fact = new CombatVfxFact(
                TriggerAreaImpact,
                spellId,
                abilityId,
                string.Empty,
                -1,
                caster.Identity,
                default,
                predictedActionInstanceId,
                spellId,
                origin,
                direction,
                point,
                0f,
                ResolvePredictedMaxDistance(spellDef),
                CombatEventScalarKinds.None,
                0f,
                0,
                1,
                isSpell: true);
            return true;
        }

        private bool TryStartPredictedProjectile(
            CombatVfxCueCatalog cue,
            CombatVfxFact fact,
            PendingPredictedSpellVfx pending,
            SpellDefinition spellDef,
            out string predictedProjectileKey)
        {
            predictedProjectileKey = $"{pending.PredictedActionInstanceId}:p{Mathf.Max(0, cue.ProjectileSequenceIndex)}";
            var row = new ProjectilePresentationEvent
            {
                ActionInstanceId = pending.PredictedActionInstanceId,
                ActionKind = fact.ActionKind,
                AbilityId = fact.AbilityId,
                SourceKind = CombatEventSources.Spell,
                ProjectileId = cue.VfxId,
                ProjectileInstanceId = predictedProjectileKey,
                HitIndex = -1,
                EventType = CombatEventTypes.Release,
                Caster = fact.Caster,
                Hit = default,
                IntendedTarget = fact.Hit,
                OriginX = fact.Origin.x,
                OriginY = fact.Origin.y,
                OriginZ = fact.Origin.z,
                DirX = fact.Direction.x,
                DirY = fact.Direction.y,
                DirZ = fact.Direction.z,
                PointX = fact.Point.x,
                PointY = fact.Point.y,
                PointZ = fact.Point.z,
                Speed = spellDef.Speed,
                MaxDistance = ResolvePredictedMaxDistance(spellDef),
                Radius = spellDef.Radius,
                MotionKind = ResolvePredictedProjectileMotionKind(fact.ActionKind),
                UpdateIntervalSeconds = spellDef.UpdateInterval,
                SequenceIndex = (uint)Mathf.Max(0, cue.ProjectileSequenceIndex),
                SequenceCount = 1,
                Terminal = false,
            };

            ProjectileVisuals.Start(row);
            return true;
        }

        private static Vector3 ResolveLocalCasterPosition(PlayerEntity caster)
        {
            LocalPlayerStateProvider? stateProvider = caster.GetLocalStateProvider();
            if (stateProvider != null && stateProvider.HasPredictedState)
                return stateProvider.PredictedPosition;

            return caster.GetPresentationRoot().position;
        }

        private static Vector3 ResolveLocalCasterGroundPosition(PlayerEntity caster)
        {
            Vector3 position = ResolveLocalCasterPosition(caster);
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetLocalPredictionEnvironment(out IMovementEnvironment? environment)
                && environment != null)
            {
                position.y = environment.SampleGroundHeight(position.x, position.z, position.y);
            }

            return position;
        }

        private static Vector3 ResolveLocalCasterForward(PlayerEntity caster)
        {
            Vector3 forward = caster.GameObject.transform.forward;
            var horizontal = new Vector3(forward.x, 0f, forward.z);
            return horizontal.sqrMagnitude > 0.000001f
                ? horizontal.normalized
                : Vector3.forward;
        }

        private static bool TryResolvePredictedProjectileVectors(
            PlayerEntity caster,
            SpellDefinition spellDef,
            string targetId,
            out Vector3 origin,
            out Vector3 direction,
            out Vector3 point)
        {
            Vector3 casterPosition = ResolveLocalCasterPosition(caster);
            Vector3 basePosition = casterPosition + Vector3.up * spellDef.SpawnHeight;
            ICombatTargetEntity? target = TargetSelector.Instance?.SelectedTarget;
            if (target == null
                || (!string.IsNullOrWhiteSpace(targetId)
                    && !string.Equals(target.TargetIdentity.ToString(), targetId, StringComparison.Ordinal)))
            {
                origin = default;
                direction = default;
                point = default;
                return false;
            }

            Vector3 targetPosition = target.GetPresentationRoot().position + Vector3.up * spellDef.SpawnHeight;
            direction = targetPosition - basePosition;
            if (direction.sqrMagnitude <= 0.0001f)
                direction = caster.GameObject.transform.forward;
            direction = direction.normalized;
            origin = basePosition + direction * spellDef.SpawnForward;
            point = origin;
            return true;
        }

        private static float ResolvePredictedMaxDistance(SpellDefinition spellDef)
            => spellDef.MaxDistance > 0f ? spellDef.MaxDistance : spellDef.Speed;

        private static string ResolvePredictedProjectileMotionKind(string spellId)
        {
            string normalized = WireIdentifier.Normalize(spellId);
            return normalized.IndexOf("BOOMERANG", StringComparison.Ordinal) >= 0
                ? ProjectileMotionBoomerangCaster
                : string.Empty;
        }

        private static string ResolveLocalAbilityId(DbConnection conn, Identity caster, string spellId)
        {
            ActiveActionBarAction action =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, caster, spellId);
            return WireIdentifier.Normalize(action.AbilityId);
        }

        private void PrunePredictedSpellVfx(long nowMs)
        {
            var staleTokens = new List<string>();
            foreach (var entry in _pendingSpellVfxByToken)
            {
                if (nowMs > entry.Value.ExpiresAtMs)
                    staleTokens.Add(entry.Key);
            }

            foreach (string tokenKey in staleTokens)
            {
                if (_pendingSpellVfxByToken.TryGetValue(tokenKey, out PendingPredictedSpellVfx pending)
                    && !string.IsNullOrWhiteSpace(pending.PredictedProjectileKey))
                {
                    ProjectileVisuals.RemovePredicted(pending.PredictedProjectileKey);
                }
                _pendingSpellVfxByToken.Remove(tokenKey);
            }

            var staleInstances = new List<string>();
            foreach (var entry in _spellVfxTokenByActionInstance)
            {
                if (!_pendingSpellVfxByToken.ContainsKey(entry.Value))
                    staleInstances.Add(entry.Key);
            }
            foreach (string actionInstanceId in staleInstances)
                _spellVfxTokenByActionInstance.Remove(actionInstanceId);
        }

        private static string SpellTokenKey(string predictedActionId, ulong clientActionSeq)
            => $"{predictedActionId}:{clientActionSeq}";

        private static string PredictedActionInstanceId(string tokenKey)
            => $"predicted_spell_vfx:{tokenKey}";

        private static string ResolveTrigger(string eventType, bool isSpell)
        {
            if (string.Equals(eventType, CombatEventTypes.AreaImpact, StringComparison.Ordinal))
                return TriggerAreaImpact;

            if (isSpell)
            {
                return eventType switch
                {
                    CombatEventTypes.Cast => TriggerSpellCast,
                    CombatEventTypes.Release => TriggerSpellRelease,
                    CombatEventTypes.Impact => TriggerSpellImpact,
                    CombatEventTypes.Block => TriggerSpellBlock,
                    CombatEventTypes.Parry => TriggerSpellParry,
                    CombatEventTypes.Miss => TriggerSpellFizzle,
                    CombatEventTypes.Fizzle => TriggerSpellFizzle,
                    _ => string.Empty,
                };
            }

            return eventType switch
            {
                CombatEventTypes.Cast => TriggerMeleeCast,
                CombatEventTypes.Impact => TriggerMeleeImpact,
                CombatEventTypes.Block => TriggerMeleeBlock,
                CombatEventTypes.Parry => TriggerMeleeParry,
                CombatEventTypes.Miss => string.Empty,
                _ => string.Empty,
            };
        }

        private static string ResolveAuthoredStrikeId(DbConnection conn, CombatEvent row)
        {
            string direct = WireIdentifier.Normalize(row.ActionKind);
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetEntity(row.Caster, out PlayerEntity caster))
            {
                string combatProfile = CombatProfileResolver.ResolveForEntity(conn, caster);
                string authored = CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, row.ActionKind);
                if (!string.IsNullOrWhiteSpace(authored))
                    return WireIdentifier.Normalize(authored);
            }

            return direct;
        }

        private static bool IsTerminalEvent(CombatEvent row)
        {
            return row.EventType == CombatEventTypes.Impact
                || row.EventType == CombatEventTypes.Block
                || row.EventType == CombatEventTypes.Parry
                || row.EventType == CombatEventTypes.Miss
                || row.EventType == CombatEventTypes.Fizzle;
        }

        private void DispatchCue(CombatVfxFact fact, CombatVfxCueCatalog cue)
        {
            string vfxRole = WireIdentifier.Normalize(cue.VfxRole);
            if (string.Equals(vfxRole, VfxRoleProjectileBody, StringComparison.Ordinal))
                return;
            if (string.Equals(vfxRole, VfxRoleTravelBody, StringComparison.Ordinal))
            {
                TravelVisuals.Start(cue, fact.ToTemplateContext(cue.Key));
                return;
            }

            string attachMode = WireIdentifier.Normalize(cue.AttachMode);
            if (string.IsNullOrWhiteSpace(attachMode))
                attachMode = AttachModeSpawnWorld;
            if (!string.Equals(attachMode, AttachModeSpawnWorld, StringComparison.Ordinal)
                && !string.Equals(attachMode, AttachModeFollowAnchor, StringComparison.Ordinal)
                && !string.Equals(attachMode, AttachModeWorldAlignedToFacing, StringComparison.Ordinal))
                return;

            Vector3 position = CombatVFXAnchorResolver.ResolvePosition(fact.ToAnchorFact(), cue);
            Transform? followAnchor = string.Equals(attachMode, AttachModeFollowAnchor, StringComparison.Ordinal)
                ? CombatVFXAnchorResolver.ResolveFollowAnchor(fact.ToAnchorFact(), cue)
                : null;
            if (string.Equals(attachMode, AttachModeFollowAnchor, StringComparison.Ordinal)
                && followAnchor == null)
                return;

            Quaternion rotation = string.Equals(attachMode, AttachModeWorldAlignedToFacing, StringComparison.Ordinal)
                ? ResolveWorldAlignedFacingRotation(fact.Direction)
                : Quaternion.identity;

            _lifecycle ??= new CombatVFXLifecycleRegistry(this);
            _lifecycle.Spawn(cue, fact.ToTemplateContext(cue.Key, followAnchor), position, rotation, followAnchor);
        }

        private static Quaternion ResolveWorldAlignedFacingRotation(Vector3 direction)
        {
            var horizontal = new Vector3(direction.x, 0f, direction.z);
            return horizontal.sqrMagnitude > 0.000001f
                ? Quaternion.LookRotation(horizontal.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private void OnDestroy()
        {
            UnsubscribeFromConnection();
            _lifecycle?.Dispose();
            _lifecycle = null;
            _travelVisuals?.Dispose();
            _travelVisuals = null;
            _projectileVisuals?.Dispose();
            _projectileVisuals = null;
            _matchingCues?.Clear();
            _cueResolver = null;
            if (_instance == this)
                _instance = null;
        }

        private void UnsubscribeFromConnection()
        {
            var conn = _subscribedConnection;
            if (conn == null)
                return;

            conn.Db.CombatEvent.OnInsert -= OnCombatEventInsert;
            conn.Db.ProjectilePresentationEvent.OnInsert -= OnProjectilePresentationEventInsert;
            conn.Db.PredictedActionResult.OnInsert -= OnPredictedActionResultInsert;
            conn.Db.CombatVfxCueCatalog.OnInsert -= OnCombatVfxCueCatalogInsert;
            conn.Db.CombatVfxCueCatalog.OnUpdate -= OnCombatVfxCueCatalogUpdate;
            conn.Db.CombatVfxCueCatalog.OnDelete -= OnCombatVfxCueCatalogDelete;
            _subscribedConnection = null;
            _cueResolver?.MarkDirty();
        }

        private readonly struct CombatVfxFact
        {
            public readonly string Trigger;
            public readonly string SpellId;
            public readonly string AbilityId;
            public readonly string StrikeId;
            public readonly int HitIndex;
            public readonly Identity Caster;
            public readonly Identity Hit;
            public readonly string ActionInstanceId;
            public readonly string ActionKind;
            public readonly Vector3 Origin;
            public readonly Vector3 Direction;
            public readonly Vector3 Point;
            public readonly float Speed;
            public readonly float MaxDistance;
            public readonly string ScalarKind;
            public readonly float ScalarValue;
            public readonly uint SequenceIndex;
            public readonly uint SequenceCount;
            public readonly bool IsSpell;

            public CombatVfxFact(
                string trigger,
                string spellId,
                string abilityId,
                string strikeId,
                int hitIndex,
                Identity caster,
                Identity hit,
                string actionInstanceId,
                string actionKind,
                Vector3 origin,
                Vector3 direction,
                Vector3 point,
                float speed,
                float maxDistance,
                string scalarKind,
                float scalarValue,
                uint sequenceIndex,
                uint sequenceCount,
                bool isSpell)
            {
                Trigger = trigger;
                SpellId = spellId;
                AbilityId = abilityId;
                StrikeId = strikeId;
                HitIndex = hitIndex;
                Caster = caster;
                Hit = hit;
                ActionInstanceId = actionInstanceId;
                ActionKind = actionKind;
                Origin = origin;
                Direction = direction;
                Point = point;
                Speed = speed;
                MaxDistance = maxDistance;
                ScalarKind = scalarKind;
                ScalarValue = scalarValue;
                SequenceIndex = sequenceIndex;
                SequenceCount = sequenceCount;
                IsSpell = isSpell;
            }

            public CombatVfxFact(
                CombatEvent row,
                string trigger,
                string spellId,
                string abilityId,
                string strikeId,
                int hitIndex)
            {
                Trigger = trigger;
                SpellId = spellId;
                AbilityId = abilityId;
                StrikeId = strikeId;
                HitIndex = hitIndex;
                Caster = row.Caster;
                Hit = row.Hit;
                ActionInstanceId = row.ActionInstanceId;
                ActionKind = row.ActionKind;
                Origin = new Vector3(row.OriginX, row.OriginY, row.OriginZ);
                Direction = new Vector3(row.DirX, row.DirY, row.DirZ);
                Point = new Vector3(row.PointX, row.PointY, row.PointZ);
                Speed = row.Speed;
                MaxDistance = row.MaxDistance;
                ScalarKind = row.ScalarKind;
                ScalarValue = row.ScalarValue;
                SequenceIndex = row.SequenceIndex;
                SequenceCount = row.SequenceCount;
                IsSpell = string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal);
            }

            public CombatVfxFact(
                ProjectilePresentationEvent row,
                string trigger,
                string spellId,
                string abilityId,
                string strikeId,
                int hitIndex)
            {
                Trigger = trigger;
                SpellId = spellId;
                AbilityId = abilityId;
                StrikeId = strikeId;
                HitIndex = hitIndex;
                Caster = row.Caster;
                Hit = row.Hit;
                ActionInstanceId = row.ActionInstanceId;
                ActionKind = row.ActionKind;
                Origin = new Vector3(row.OriginX, row.OriginY, row.OriginZ);
                Direction = new Vector3(row.DirX, row.DirY, row.DirZ);
                Point = new Vector3(row.PointX, row.PointY, row.PointZ);
                Speed = row.Speed;
                MaxDistance = row.MaxDistance;
                ScalarKind = CombatEventScalarKinds.None;
                ScalarValue = 0f;
                SequenceIndex = row.SequenceIndex;
                SequenceCount = row.SequenceCount;
                IsSpell = string.Equals(row.SourceKind, CombatEventSources.Spell, StringComparison.Ordinal);
            }

            public CombatVfxResolutionFact ToResolutionFact()
            {
                return new CombatVfxResolutionFact(IsSpell, Trigger, SpellId, AbilityId, StrikeId, HitIndex);
            }

            public CombatVfxAnchorFact ToAnchorFact()
            {
                return new CombatVfxAnchorFact(Caster, Hit, Origin, Point);
            }

            public CombatVFXTemplateContext ToTemplateContext(string cueKey, Transform? followAnchor = null)
            {
                return new CombatVFXTemplateContext(
                    cueKey,
                    ActionInstanceId,
                    ActionKind,
                    AbilityId,
                    Trigger,
                    Caster,
                    Hit,
                    Origin,
                    Direction,
                    Point,
                    Speed,
                    MaxDistance,
                    ScalarKind,
                    ScalarValue,
                    SequenceIndex,
                    SequenceCount,
                    followAnchor);
            }
        }
    }
}
