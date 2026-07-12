#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Arena.Entity;
using Arena.Network;
using Arena.Presentation;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>
    /// Batch-mode client for NPC presentation acceptance. MIXED mode observes
    /// an encounter driven by ops/npc-mixed-group-probe.py. APPEARANCE_SWEEP
    /// mode sequentially spawns every synchronized visual through the existing
    /// reducer and validates the existing NPC presentation adapter and cleanup.
    /// </summary>
    public static class NpcHeadlessAcceptanceRunner
    {
        private const string ScenePathFormat = "Assets/Arena/Content/Scenes/OpenWorld/{0}.unity";
        private const string DefaultResultPath = "Logs/npc-mixed-acceptance.json";
        private const string SweepResultPath = "Logs/npc-appearance-sweep.json";
        private const double SweepPhaseTimeoutSeconds = 10d;
        private const double SweepPresentationHoldSeconds = 0.12d;

        private enum AcceptanceMode
        {
            Mixed,
            AppearanceSweep,
        }

        private enum SweepPhase
        {
            Uninitialized,
            AwaitInitialCleanup,
            AwaitSpawn,
            HoldLocomotion,
            HoldReady,
            HoldHit,
            HoldDeath,
            AwaitCleanup,
        }

        private sealed class ExpectedNpc
        {
            public ExpectedNpc(string templateId, string abilityId, params string[] actionStates)
            {
                TemplateId = templateId;
                AbilityId = abilityId;
                ActionStates = actionStates;
            }

            public string TemplateId { get; }
            public string AbilityId { get; }
            public string[] ActionStates { get; }
        }

        private sealed class NpcEvidence
        {
            public bool Spawned;
            public bool ExternalOwner;
            public bool AnimatorResolved;
            public readonly HashSet<string> AnimationStates = new(StringComparer.Ordinal);
            public readonly HashSet<string> CombatEvents = new(StringComparer.Ordinal);
            public readonly HashSet<string> ProjectileEvents = new(StringComparer.Ordinal);
        }

        private sealed class SweepEntry
        {
            public SweepEntry(string templateId, string visualId)
            {
                TemplateId = templateId;
                VisualId = visualId;
            }

            public string TemplateId { get; }
            public string VisualId { get; }
        }

        private sealed class SweepEvidence
        {
            public SweepEvidence(SweepEntry entry)
            {
                Entry = entry;
            }

            public SweepEntry Entry { get; }
            public bool CatalogEntryResolved;
            public bool PrefabResolved;
            public bool ProfileAuthored;
            public bool AnimatorResolved;
            public bool LocomotionResolved;
            public bool ReadyResolved;
            public bool HitResolved;
            public bool DeathResolved;
            public bool DeathVisible;
            public bool AuthoritativeCleanup;
            public string LocomotionState = string.Empty;
            public string ReadyState = string.Empty;
            public string HitState = string.Empty;
            public string DeathState = string.Empty;
        }

        private static readonly ExpectedNpc[] Expected =
        {
            new(
                "KOBOLD_WARRIOR_RD_SWORD_SHIELD",
                "NPC_KOBOLD_SHIELD_STRIKE",
                "Combat_Defend_Attack"),
            new(
                "SKELETON_ARCHER",
                "NPC_SKELETON_ARCHER_SHOT",
                "load",
                "attack"),
            new(
                "SKELETON_WIZARD",
                "NPC_SKELETON_WIZARD_FROST_BOLT",
                "SpellReady",
                "SpellCast"),
            new(
                "LICH_SUPPORT",
                "NPC_LICH_BONE_WARD",
                "SpellA_Ready",
                "SpellA"),
        };

        private static readonly Dictionary<string, ExpectedNpc> ExpectedByTemplate = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ExpectedNpc> ExpectedByAbility = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, NpcEvidence> EvidenceByTemplate = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> TemplateByIdentity = new(StringComparer.Ordinal);
        private static readonly List<SweepEntry> SweepPlan = new();
        private static readonly List<SweepEvidence> SweepEvidenceRows = new();
        private static readonly FieldInfo? ActiveAnimationStateField = typeof(NpcAnimationController).GetField(
            "_activeStateName",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static string _scene = "Desert_Day";
        private static string _resultPath = DefaultResultPath;
        private static AcceptanceMode _mode = AcceptanceMode.Mixed;
        private static double _deadline;
        private static bool _travelRequested;
        private static bool _subscribed;
        private static long _projectileStartedBaseline;
        private static long _projectileMissingBaseline;
        private static long _vfxSpawnedBaseline;
        private static long _vfxMissingBaseline;
        private static SweepPhase _sweepPhase;
        private static int _sweepIndex;
        private static double _sweepPhaseDeadline;
        private static double _sweepHoldUntil;
        private static Identity _sweepIdentity;
        private static bool _hasSweepIdentity;

        public static void Run()
        {
            ResetState();

            string mode = Normalize(Environment.GetEnvironmentVariable("ARENA_NPC_ACCEPTANCE_MODE") ?? string.Empty);
            if (string.Equals(mode, "APPEARANCE_SWEEP", StringComparison.Ordinal))
            {
                _mode = AcceptanceMode.AppearanceSweep;
                _resultPath = SweepResultPath;
            }
            else if (!string.IsNullOrEmpty(mode) && !string.Equals(mode, "MIXED", StringComparison.Ordinal))
            {
                Finish(false, $"unknown ARENA_NPC_ACCEPTANCE_MODE '{mode}'");
                return;
            }

            string? sceneEnv = Environment.GetEnvironmentVariable("ARENA_NPC_ACCEPTANCE_SCENE");
            if (!string.IsNullOrWhiteSpace(sceneEnv))
                _scene = sceneEnv.Trim();

            string? resultEnv = Environment.GetEnvironmentVariable("ARENA_NPC_ACCEPTANCE_RESULT");
            if (!string.IsNullOrWhiteSpace(resultEnv))
                _resultPath = resultEnv.Trim();

            float seconds = 90f;
            string? secondsEnv = Environment.GetEnvironmentVariable("ARENA_NPC_ACCEPTANCE_SECONDS");
            if (!string.IsNullOrWhiteSpace(secondsEnv)
                && float.TryParse(secondsEnv, out float parsed)
                && parsed > 0f)
            {
                seconds = parsed;
            }

            string module = Environment.GetEnvironmentVariable("ARENA_HEADLESS_MODULE") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(module))
            {
                Finish(false, "ARENA_HEADLESS_MODULE is required so acceptance cannot connect to the normal arena database.");
                return;
            }

            Debug.Log(
                $"[NpcHeadlessAcceptanceRunner] mode={ModeName()} scene={_scene} seconds={seconds:F0} "
                + $"module={module} result={_resultPath}");

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.OpenScene(string.Format(ScenePathFormat, _scene));
            Application.runInBackground = true;

            _deadline = EditorApplication.timeSinceStartup + seconds;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void ResetState()
        {
            ExpectedByTemplate.Clear();
            ExpectedByAbility.Clear();
            EvidenceByTemplate.Clear();
            TemplateByIdentity.Clear();
            foreach (ExpectedNpc expected in Expected)
            {
                ExpectedByTemplate[expected.TemplateId] = expected;
                ExpectedByAbility[expected.AbilityId] = expected;
                EvidenceByTemplate[expected.TemplateId] = new NpcEvidence();
            }

            _scene = "Desert_Day";
            _resultPath = DefaultResultPath;
            _mode = AcceptanceMode.Mixed;
            _deadline = 0d;
            _travelRequested = false;
            _subscribed = false;
            _projectileStartedBaseline = 0L;
            _projectileMissingBaseline = 0L;
            _vfxSpawnedBaseline = 0L;
            _vfxMissingBaseline = 0L;
            SweepPlan.Clear();
            SweepEvidenceRows.Clear();
            _sweepPhase = SweepPhase.Uninitialized;
            _sweepIndex = 0;
            _sweepPhaseDeadline = 0d;
            _sweepHoldUntil = 0d;
            _sweepIdentity = default;
            _hasSweepIdentity = false;
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Finish(
                    false,
                    _mode == AcceptanceMode.AppearanceSweep
                        ? $"deadline reached during the appearance sweep at {CurrentSweepLabel()}"
                        : "deadline reached before every mixed-exemplar presentation gate passed");
                return;
            }

            if (!Application.isPlaying)
                return;

            NetworkManager? networkManager = NetworkManager.Instance;
            DbConnection? conn = networkManager?.Conn;
            if (networkManager == null || !networkManager.IsConnected || conn == null)
                return;

            if (_mode == AcceptanceMode.Mixed && !_subscribed)
            {
                conn.Db.CombatEvent.OnInsert += OnCombatEventInsert;
                conn.Db.ProjectilePresentationEvent.OnInsert += OnProjectilePresentationEventInsert;
                _projectileStartedBaseline = ReadStaticCounter(
                    "Arena.Presentation.CombatProjectileVisualController",
                    "DebugStartedProjectileVisualCount");
                _projectileMissingBaseline = ReadStaticCounter(
                    "Arena.Presentation.CombatProjectileVisualController",
                    "DebugMissingProjectilePrefabCount");
                _vfxSpawnedBaseline = ReadVfxSpawnedCount();
                _vfxMissingBaseline = ReadStaticCounter(
                    "Arena.Presentation.CombatVFXLifecycleRegistry",
                    "DebugMissingTemplateCount");
                _subscribed = true;
            }

            if (!_travelRequested)
            {
                _travelRequested = true;
                Debug.Log($"[NpcHeadlessAcceptanceRunner] connected — traveling to {_scene}");
                Arena.World.OpenWorldTravelCatalog.SetCurrentScene(_scene);
                conn.Reducers.SetOpenWorldScene(_scene);
            }

            if (_mode == AcceptanceMode.AppearanceSweep)
            {
                TickAppearanceSweep(conn);
                return;
            }

            ObserveNpcEntities(conn);
            if (TryGetFailure(out string failure))
            {
                Finish(false, failure);
                return;
            }

            if (AllGatesPass())
                Finish(true, "all mixed-exemplar presentation gates passed");
        }

        private static void TickAppearanceSweep(DbConnection conn)
        {
            if (!conn.Identity.HasValue)
                return;

            Identity owner = conn.Identity.Value;
            PlayerWorld? world = conn.Db.PlayerWorld.Identity.Find(owner);
            if (world == null
                || !string.Equals(world.WorldKind, "OPEN", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(world.OpenWorldSceneName, _scene, StringComparison.Ordinal))
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_sweepPhaseDeadline > 0d && now >= _sweepPhaseDeadline)
            {
                Finish(false, $"appearance sweep timed out in {_sweepPhase} for {CurrentSweepLabel()}");
                return;
            }

            switch (_sweepPhase)
            {
                case SweepPhase.Uninitialized:
                    if (!TryInitializeAppearanceSweep(conn, out string initializationFailure))
                    {
                        if (!string.IsNullOrEmpty(initializationFailure))
                            Finish(false, initializationFailure);
                        return;
                    }

                    conn.Reducers.DespawnAllNpcs();
                    SetSweepPhase(SweepPhase.AwaitInitialCleanup);
                    break;

                case SweepPhase.AwaitInitialCleanup:
                    if (HasOwnedNpc(conn, owner))
                        return;
                    SpawnCurrentSweepEntry(conn);
                    break;

                case SweepPhase.AwaitSpawn:
                    TickAwaitSweepSpawn(conn, owner);
                    break;

                case SweepPhase.HoldLocomotion:
                    if (now < _sweepHoldUntil)
                        return;
                    if (!TryGetCurrentSweepPresentation(out NpcEntity locomotionEntity, out NpcAnimationController locomotion))
                        return;
                    locomotion.StopLocomotion();
                    SweepEvidence readyEvidence = SweepEvidenceRows[_sweepIndex];
                    readyEvidence.ReadyState = ReadActiveAnimationState(locomotion);
                    readyEvidence.ReadyResolved = !string.IsNullOrEmpty(readyEvidence.ReadyState);
                    if (!readyEvidence.ReadyResolved)
                    {
                        Finish(false, $"{CurrentSweepLabel()} did not resolve an idle/ready state after locomotion");
                        return;
                    }
                    _ = locomotionEntity;
                    HoldSweepPhase(SweepPhase.HoldReady);
                    break;

                case SweepPhase.HoldReady:
                    if (now < _sweepHoldUntil)
                        return;
                    if (!TryGetCurrentSweepPresentation(out _, out NpcAnimationController hitAnimation))
                        return;
                    SweepEvidence hitEvidence = SweepEvidenceRows[_sweepIndex];
                    bool hitSuppressed = CurrentSweepExplicitlySuppressesHit();
                    hitAnimation.PlayHit();
                    hitEvidence.HitState = hitSuppressed
                        ? "SUPPRESSED"
                        : ReadActiveAnimationState(hitAnimation);
                    hitEvidence.HitResolved = hitSuppressed
                        || (!string.IsNullOrEmpty(hitEvidence.HitState)
                            && !string.Equals(hitEvidence.HitState, hitEvidence.ReadyState, StringComparison.Ordinal));
                    if (!hitEvidence.HitResolved)
                    {
                        Finish(false, $"{CurrentSweepLabel()} did not resolve a distinct hit/impact-response state");
                        return;
                    }
                    HoldSweepPhase(SweepPhase.HoldHit);
                    break;

                case SweepPhase.HoldHit:
                    if (now < _sweepHoldUntil)
                        return;
                    if (!TryGetCurrentSweepPresentation(out NpcEntity deathEntity, out NpcAnimationController deathAnimation))
                        return;
                    SweepEvidence deathEvidence = SweepEvidenceRows[_sweepIndex];
                    deathAnimation.PlayDeath();
                    deathEvidence.DeathState = ReadActiveAnimationState(deathAnimation);
                    deathEvidence.DeathResolved = !string.IsNullOrEmpty(deathEvidence.DeathState)
                        && !string.Equals(deathEvidence.DeathState, deathEvidence.HitState, StringComparison.Ordinal);
                    deathEvidence.DeathVisible = deathEntity.GameObject.activeInHierarchy;
                    if (!deathEvidence.DeathResolved || !deathEvidence.DeathVisible)
                    {
                        Finish(false, $"{CurrentSweepLabel()} did not resolve a visible death presentation");
                        return;
                    }
                    HoldSweepPhase(SweepPhase.HoldDeath);
                    break;

                case SweepPhase.HoldDeath:
                    if (now < _sweepHoldUntil)
                        return;
                    conn.Reducers.DespawnNpc(_sweepIdentity);
                    SetSweepPhase(SweepPhase.AwaitCleanup);
                    break;

                case SweepPhase.AwaitCleanup:
                    bool rowRemoved = conn.Db.NpcInstance.Identity.Find(_sweepIdentity) == null;
                    bool entityRemoved = EntityRegistry.Instance == null
                        || !EntityRegistry.Instance.TryGetNpc(_sweepIdentity, out _);
                    if (!rowRemoved || !entityRemoved)
                        return;

                    SweepEvidenceRows[_sweepIndex].AuthoritativeCleanup = true;
                    _hasSweepIdentity = false;
                    _sweepIndex++;
                    if (_sweepIndex >= SweepPlan.Count)
                    {
                        Finish(true, $"all {SweepPlan.Count} synchronized NPC appearances passed sequential presentation and cleanup");
                        return;
                    }
                    SpawnCurrentSweepEntry(conn);
                    break;
            }
        }

        private static bool TryInitializeAppearanceSweep(DbConnection conn, out string failure)
        {
            var rows = new List<SpacetimeDB.Types.NpcVisualCatalog>();
            foreach (SpacetimeDB.Types.NpcVisualCatalog row in conn.Db.NpcVisualCatalog.Iter())
                rows.Add(row);

            if (rows.Count == 0)
            {
                failure = string.Empty;
                return false;
            }

            rows.Sort((left, right) =>
            {
                int visual = string.Compare(left.VisualId, right.VisualId, StringComparison.Ordinal);
                return visual != 0
                    ? visual
                    : string.Compare(left.TemplateId, right.TemplateId, StringComparison.Ordinal);
            });

            if (!Arena.Entity.NpcVisualCatalog.TryLoadDefault(out Arena.Entity.NpcVisualCatalog catalog, out string catalogError))
            {
                failure = $"Unity NPC visual catalog could not load: {catalogError}";
                return false;
            }

            IReadOnlyList<string> catalogErrors = catalog.ValidateEntries();
            if (catalogErrors.Count > 0)
            {
                failure = $"Unity NPC visual catalog validation failed: {string.Join("; ", catalogErrors)}";
                return false;
            }

            var seenVisuals = new HashSet<string>(StringComparer.Ordinal);
            int authoredProfiles = 0;
            foreach (SpacetimeDB.Types.NpcVisualCatalog row in rows)
            {
                string templateId = Normalize(row.TemplateId);
                string visualId = Normalize(row.VisualId);
                if (string.IsNullOrEmpty(templateId) || string.IsNullOrEmpty(visualId))
                {
                    failure = "synchronized NPC visual catalog contains an empty template or visual ID";
                    return false;
                }
                if (!seenVisuals.Add(visualId))
                {
                    failure = $"synchronized NPC visual catalog duplicates visual '{visualId}'";
                    return false;
                }

                var entry = new SweepEntry(templateId, visualId);
                var evidence = new SweepEvidence(entry);
                evidence.CatalogEntryResolved = catalog.TryGetEntry(visualId, out NpcVisualCatalogEntry unityEntry);
                evidence.PrefabResolved = catalog.TryGetPrefab(visualId, out _);
                evidence.ProfileAuthored = evidence.CatalogEntryResolved && unityEntry.profile != null;
                if (!evidence.CatalogEntryResolved || !evidence.PrefabResolved)
                {
                    failure = $"synchronized visual '{visualId}' has no resolvable Unity catalog entry/prefab";
                    return false;
                }

                if (evidence.ProfileAuthored)
                    authoredProfiles++;
                SweepPlan.Add(entry);
                SweepEvidenceRows.Add(evidence);
            }

            Debug.Log(
                $"[NpcHeadlessAcceptanceRunner] appearance sweep catalog rows={SweepPlan.Count} "
                + $"authored_profiles={authoredProfiles}");
            failure = string.Empty;
            return true;
        }

        private static void SpawnCurrentSweepEntry(DbConnection conn)
        {
            SweepEntry entry = SweepPlan[_sweepIndex];
            _hasSweepIdentity = false;
            Debug.Log(
                $"[NpcHeadlessAcceptanceRunner] appearance {_sweepIndex + 1}/{SweepPlan.Count} "
                + $"template={entry.TemplateId} visual={entry.VisualId}");
            conn.Reducers.SpawnNpc(entry.TemplateId, entry.VisualId, "NEUTRAL");
            SetSweepPhase(SweepPhase.AwaitSpawn);
        }

        private static void TickAwaitSweepSpawn(DbConnection conn, Identity owner)
        {
            SweepEntry entry = SweepPlan[_sweepIndex];
            if (!_hasSweepIdentity)
            {
                foreach (NpcInstance row in conn.Db.NpcInstance.Iter())
                {
                    if (row.SpawnedBy == owner
                        && string.Equals(row.TemplateId, entry.TemplateId, StringComparison.Ordinal)
                        && string.Equals(row.VisualId, entry.VisualId, StringComparison.Ordinal)
                        && string.Equals(row.OpenWorldSceneName, _scene, StringComparison.Ordinal))
                    {
                        _sweepIdentity = row.Identity;
                        _hasSweepIdentity = true;
                        break;
                    }
                }
            }

            if (!_hasSweepIdentity
                || EntityRegistry.Instance == null
                || !EntityRegistry.Instance.TryGetNpc(_sweepIdentity, out NpcEntity entity))
            {
                return;
            }

            NpcAnimationController? animation = entity.GameObject.GetComponent<NpcAnimationController>();
            SweepEvidence evidence = SweepEvidenceRows[_sweepIndex];
            Animator? animator = null;
            if (Arena.Entity.NpcVisualCatalog.TryLoadDefault(out Arena.Entity.NpcVisualCatalog catalog, out _)
                && catalog.TryGetEntry(entry.VisualId, out NpcVisualCatalogEntry unityEntry)
                && unityEntry.profile != null)
            {
                unityEntry.profile.TryResolvePrimaryAnimator(entity.GameObject, out animator!);
            }
            else
            {
                animator = entity.GameObject.GetComponentInChildren<Animator>(includeInactive: true);
            }

            evidence.AnimatorResolved = animation != null
                && animator != null
                && animator.runtimeAnimatorController != null;
            if (!evidence.AnimatorResolved || animation == null)
            {
                Finish(false, $"{CurrentSweepLabel()} did not resolve its primary Animator/controller");
                return;
            }

            animation.SetLocomotionSpeed(1f, forceRun: false);
            evidence.LocomotionState = ReadActiveAnimationState(animation);
            evidence.LocomotionResolved = !string.IsNullOrEmpty(evidence.LocomotionState);
            if (!evidence.LocomotionResolved)
            {
                Finish(false, $"{CurrentSweepLabel()} did not resolve an authored/fallback locomotion state");
                return;
            }

            HoldSweepPhase(SweepPhase.HoldLocomotion);
        }

        private static bool TryGetCurrentSweepPresentation(
            out NpcEntity entity,
            out NpcAnimationController animation)
        {
            if (_hasSweepIdentity
                && EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetNpc(_sweepIdentity, out entity!))
            {
                animation = entity.GameObject.GetComponent<NpcAnimationController>();
                if (animation != null)
                    return true;
            }

            entity = null!;
            animation = null!;
            return false;
        }

        private static bool HasOwnedNpc(DbConnection conn, Identity owner)
        {
            foreach (NpcInstance row in conn.Db.NpcInstance.Iter())
            {
                if (row.SpawnedBy == owner)
                    return true;
            }
            return false;
        }

        private static void SetSweepPhase(SweepPhase phase)
        {
            _sweepPhase = phase;
            _sweepPhaseDeadline = EditorApplication.timeSinceStartup + SweepPhaseTimeoutSeconds;
            _sweepHoldUntil = 0d;
        }

        private static void HoldSweepPhase(SweepPhase phase)
        {
            SetSweepPhase(phase);
            _sweepHoldUntil = EditorApplication.timeSinceStartup + SweepPresentationHoldSeconds;
        }

        private static string CurrentSweepLabel()
        {
            if (_sweepIndex < 0 || _sweepIndex >= SweepPlan.Count)
                return "uninitialized appearance";
            SweepEntry entry = SweepPlan[_sweepIndex];
            return $"template '{entry.TemplateId}' visual '{entry.VisualId}'";
        }

        private static bool CurrentSweepExplicitlySuppressesHit()
        {
            if (_sweepIndex < 0 || _sweepIndex >= SweepPlan.Count)
                return false;

            string visualId = SweepPlan[_sweepIndex].VisualId;
            return Arena.Entity.NpcVisualCatalog.TryLoadDefault(out Arena.Entity.NpcVisualCatalog catalog, out _)
                && catalog.TryGetEntry(visualId, out NpcVisualCatalogEntry entry)
                && entry.profile != null
                && entry.profile.Animations.hit.Count == 0;
        }

        private static string ReadActiveAnimationState(NpcAnimationController animation)
            => ActiveAnimationStateField?.GetValue(animation) as string ?? string.Empty;

        private static void ObserveNpcEntities(DbConnection conn)
        {
            foreach (NpcInstance row in conn.Db.NpcInstance.Iter())
            {
                if (!ExpectedByTemplate.TryGetValue(row.TemplateId, out ExpectedNpc? expected))
                    continue;
                if (!string.Equals(row.OpenWorldSceneName, _scene, StringComparison.Ordinal))
                    continue;

                string identity = row.Identity.ToString();
                TemplateByIdentity[identity] = expected.TemplateId;
                NpcEvidence evidence = EvidenceByTemplate[expected.TemplateId];
                evidence.Spawned = true;
                evidence.ExternalOwner = conn.Identity.HasValue && row.SpawnedBy != conn.Identity.Value;

                EntityRegistry? registry = EntityRegistry.Instance;
                if (registry == null || !registry.TryGetNpc(row.Identity, out NpcEntity entity))
                    continue;

                NpcAnimationController? animation = entity.GameObject.GetComponent<NpcAnimationController>();
                Animator? animator = entity.GameObject.GetComponentInChildren<Animator>(includeInactive: true);
                evidence.AnimatorResolved |= animation != null
                    && animator != null
                    && animator.runtimeAnimatorController != null;

                if (animation != null
                    && ActiveAnimationStateField?.GetValue(animation) is string activeState
                    && !string.IsNullOrWhiteSpace(activeState))
                {
                    evidence.AnimationStates.Add(activeState);
                }
            }
        }

        private static void OnCombatEventInsert(EventContext ctx, CombatEvent row)
        {
            _ = ctx;
            string abilityId = Normalize(row.AbilityId);
            if (!ExpectedByAbility.TryGetValue(abilityId, out ExpectedNpc? expected))
                return;

            if (!CasterMatchesExpected(row.Caster, expected.TemplateId))
                return;

            EvidenceByTemplate[expected.TemplateId].CombatEvents.Add(row.EventType);
        }

        private static void OnProjectilePresentationEventInsert(EventContext ctx, ProjectilePresentationEvent row)
        {
            _ = ctx;
            string abilityId = Normalize(row.AbilityId);
            if (!ExpectedByAbility.TryGetValue(abilityId, out ExpectedNpc? expected))
                return;

            if (!CasterMatchesExpected(row.Caster, expected.TemplateId))
                return;

            EvidenceByTemplate[expected.TemplateId].ProjectileEvents.Add(row.EventType);
        }

        private static bool CasterMatchesExpected(Identity caster, string expectedTemplate)
        {
            string key = caster.ToString();
            if (TemplateByIdentity.TryGetValue(key, out string? mapped))
                return string.Equals(mapped, expectedTemplate, StringComparison.Ordinal);

            NpcInstance? row = NetworkManager.Instance?.Conn?.Db.NpcInstance.Identity.Find(caster);
            if (row == null)
                return false;

            TemplateByIdentity[key] = row.TemplateId;
            return string.Equals(row.TemplateId, expectedTemplate, StringComparison.Ordinal);
        }

        private static bool AllGatesPass()
        {
            foreach (ExpectedNpc expected in Expected)
            {
                NpcEvidence evidence = EvidenceByTemplate[expected.TemplateId];
                if (!evidence.Spawned || !evidence.ExternalOwner || !evidence.AnimatorResolved)
                    return false;
                if (!ContainsAny(evidence.AnimationStates, expected.ActionStates))
                    return false;
                if (!evidence.CombatEvents.Contains("COMBAT_CAST")
                    || !evidence.CombatEvents.Contains("COMBAT_IMPACT"))
                {
                    return false;
                }
            }

            foreach (string template in new[] { "SKELETON_ARCHER", "SKELETON_WIZARD" })
            {
                NpcEvidence evidence = EvidenceByTemplate[template];
                if (!evidence.CombatEvents.Contains("COMBAT_RELEASE")
                    || !evidence.ProjectileEvents.Contains("COMBAT_RELEASE")
                    || !evidence.ProjectileEvents.Contains("COMBAT_IMPACT"))
                {
                    return false;
                }
            }

            long startedProjectiles = ReadStaticCounter(
                "Arena.Presentation.CombatProjectileVisualController",
                "DebugStartedProjectileVisualCount") - _projectileStartedBaseline;
            long spawnedVfx = ReadVfxSpawnedCount() - _vfxSpawnedBaseline;
            return startedProjectiles >= 2L && spawnedVfx >= 4L;
        }

        private static bool TryGetFailure(out string failure)
        {
            long missingProjectiles = ReadStaticCounter(
                "Arena.Presentation.CombatProjectileVisualController",
                "DebugMissingProjectilePrefabCount") - _projectileMissingBaseline;
            if (missingProjectiles > 0L)
            {
                failure = $"shared projectile presentation reported {missingProjectiles} missing prefab(s)";
                return true;
            }

            long missingVfx = ReadStaticCounter(
                "Arena.Presentation.CombatVFXLifecycleRegistry",
                "DebugMissingTemplateCount") - _vfxMissingBaseline;
            if (missingVfx > 0L)
            {
                failure = $"shared VFX presentation reported {missingVfx} missing template(s)";
                return true;
            }

            failure = string.Empty;
            return false;
        }

        private static bool ContainsAny(HashSet<string> actual, string[] expected)
        {
            foreach (string value in expected)
            {
                if (actual.Contains(value))
                    return true;
            }

            return false;
        }

        private static long ReadVfxSpawnedCount()
        {
            return ReadStaticCounter(
                       "Arena.Presentation.CombatVFXLifecycleRegistry",
                       "DebugSpawnedScriptedCount")
                   + ReadStaticCounter(
                       "Arena.Presentation.CombatVFXLifecycleRegistry",
                       "DebugSpawnedPrefabCount");
        }

        private static long ReadStaticCounter(string typeName, string propertyName)
        {
            Type? type = Type.GetType($"{typeName}, Assembly-CSharp");
            PropertyInfo? property = type?.GetProperty(
                propertyName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object? value = property?.GetValue(null);
            return value == null ? 0L : Convert.ToInt64(value);
        }

        private static void Finish(bool passed, string summary)
        {
            EditorApplication.update -= Tick;
            WriteResult(passed, summary);
            Debug.Log(
                $"[NpcHeadlessAcceptanceRunner] {(passed ? "PASS" : "FAIL")}: {summary}; "
                + $"result={_resultPath}");
            EditorApplication.Exit(passed ? 0 : 1);
        }

        private static void WriteResult(bool passed, string summary)
        {
            if (_mode == AcceptanceMode.AppearanceSweep)
                WriteSweepResult(passed, summary);
            else
                WriteMixedResult(passed, summary);
        }

        private static void WriteMixedResult(bool passed, string summary)
        {
            string fullPath = Path.IsPathRooted(_resultPath)
                ? _resultPath
                : Path.Combine(Directory.GetCurrentDirectory(), _resultPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine($"  \"passed\": {passed.ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"summary\": \"{EscapeJson(summary)}\",");
            json.AppendLine("  \"mode\": \"MIXED\",");
            json.AppendLine($"  \"scene\": \"{EscapeJson(_scene)}\",");
            json.AppendLine($"  \"projectile_visuals_started\": {ReadStaticCounter("Arena.Presentation.CombatProjectileVisualController", "DebugStartedProjectileVisualCount") - _projectileStartedBaseline},");
            json.AppendLine($"  \"vfx_instances_spawned\": {ReadVfxSpawnedCount() - _vfxSpawnedBaseline},");
            json.AppendLine("  \"npcs\": [");
            for (int i = 0; i < Expected.Length; i++)
            {
                ExpectedNpc expected = Expected[i];
                NpcEvidence evidence = EvidenceByTemplate[expected.TemplateId];
                json.AppendLine("    {");
                json.AppendLine($"      \"template_id\": \"{expected.TemplateId}\",");
                json.AppendLine($"      \"ability_id\": \"{expected.AbilityId}\",");
                json.AppendLine($"      \"spawned\": {evidence.Spawned.ToString().ToLowerInvariant()},");
                json.AppendLine($"      \"external_owner\": {evidence.ExternalOwner.ToString().ToLowerInvariant()},");
                json.AppendLine($"      \"animator_resolved\": {evidence.AnimatorResolved.ToString().ToLowerInvariant()},");
                json.AppendLine($"      \"animation_states\": {JsonArray(evidence.AnimationStates)},");
                json.AppendLine($"      \"combat_events\": {JsonArray(evidence.CombatEvents)},");
                json.AppendLine($"      \"projectile_events\": {JsonArray(evidence.ProjectileEvents)}");
                json.Append("    }");
                json.AppendLine(i == Expected.Length - 1 ? string.Empty : ",");
            }
            json.AppendLine("  ]");
            json.AppendLine("}");
            File.WriteAllText(fullPath, json.ToString());
        }

        private static void WriteSweepResult(bool passed, string summary)
        {
            string fullPath = Path.IsPathRooted(_resultPath)
                ? _resultPath
                : Path.Combine(Directory.GetCurrentDirectory(), _resultPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            int authoredProfiles = 0;
            int cleanedAppearances = 0;
            foreach (SweepEvidence evidence in SweepEvidenceRows)
            {
                if (evidence.ProfileAuthored)
                    authoredProfiles++;
                if (evidence.AuthoritativeCleanup)
                    cleanedAppearances++;
            }

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine($"  \"passed\": {passed.ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"summary\": \"{EscapeJson(summary)}\",");
            json.AppendLine("  \"mode\": \"APPEARANCE_SWEEP\",");
            json.AppendLine($"  \"scene\": \"{EscapeJson(_scene)}\",");
            json.AppendLine($"  \"catalog_appearances\": {SweepPlan.Count},");
            json.AppendLine($"  \"authored_profiles\": {authoredProfiles},");
            json.AppendLine($"  \"cleaned_appearances\": {cleanedAppearances},");
            json.AppendLine("  \"appearances\": [");
            for (int i = 0; i < SweepEvidenceRows.Count; i++)
            {
                SweepEvidence evidence = SweepEvidenceRows[i];
                json.AppendLine("    {");
                json.AppendLine($"      \"template_id\": \"{EscapeJson(evidence.Entry.TemplateId)}\",");
                json.AppendLine($"      \"visual_id\": \"{EscapeJson(evidence.Entry.VisualId)}\",");
                json.AppendLine($"      \"catalog_entry_resolved\": {JsonBool(evidence.CatalogEntryResolved)},");
                json.AppendLine($"      \"prefab_resolved\": {JsonBool(evidence.PrefabResolved)},");
                json.AppendLine($"      \"profile_authored\": {JsonBool(evidence.ProfileAuthored)},");
                json.AppendLine($"      \"animator_resolved\": {JsonBool(evidence.AnimatorResolved)},");
                json.AppendLine($"      \"locomotion_state\": \"{EscapeJson(evidence.LocomotionState)}\",");
                json.AppendLine($"      \"ready_state\": \"{EscapeJson(evidence.ReadyState)}\",");
                json.AppendLine($"      \"hit_state\": \"{EscapeJson(evidence.HitState)}\",");
                json.AppendLine($"      \"death_state\": \"{EscapeJson(evidence.DeathState)}\",");
                json.AppendLine($"      \"death_visible\": {JsonBool(evidence.DeathVisible)},");
                json.AppendLine($"      \"authoritative_cleanup\": {JsonBool(evidence.AuthoritativeCleanup)}");
                json.Append("    }");
                json.AppendLine(i == SweepEvidenceRows.Count - 1 ? string.Empty : ",");
            }
            json.AppendLine("  ]");
            json.AppendLine("}");
            File.WriteAllText(fullPath, json.ToString());
        }

        private static string JsonArray(HashSet<string> values)
        {
            var sorted = new List<string>(values);
            sorted.Sort(StringComparer.Ordinal);
            var json = new StringBuilder("[");
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                    json.Append(", ");
                json.Append('"').Append(EscapeJson(sorted[i])).Append('"');
            }
            return json.Append(']').ToString();
        }

        private static string EscapeJson(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string JsonBool(bool value)
            => value ? "true" : "false";

        private static string ModeName()
            => _mode == AcceptanceMode.AppearanceSweep ? "APPEARANCE_SWEEP" : "MIXED";

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}
