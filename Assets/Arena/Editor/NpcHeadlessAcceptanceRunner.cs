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
    /// Batch-mode observing client for the mixed NPC exemplar acceptance run.
    /// A separate websocket owner (ops/npc-mixed-group-probe.py) spawns and
    /// drives the encounter; this client proves that another client receives
    /// the same authored entities, animation requests, combat events,
    /// projectile presentation, and VFX dispatch through the shared runtime.
    /// </summary>
    public static class NpcHeadlessAcceptanceRunner
    {
        private const string ScenePathFormat = "Assets/Arena/Content/Scenes/OpenWorld/{0}.unity";
        private const string DefaultResultPath = "Logs/npc-mixed-acceptance.json";

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

        private static readonly ExpectedNpc[] Expected =
        {
            new(
                "KOBOLD_WARRIOR_RD_SWORD_SHIELD",
                "NPC_KOBOLD_WARRIOR_SWORD_SLASH",
                "Combat_1H_Attack",
                "Combat_Defend_Attack",
                "Combat_Unarmed_Attack"),
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
        private static readonly FieldInfo? ActiveAnimationStateField = typeof(NpcAnimationController).GetField(
            "_activeStateName",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static string _scene = "Desert_Day";
        private static string _resultPath = DefaultResultPath;
        private static double _deadline;
        private static bool _travelRequested;
        private static bool _subscribed;
        private static long _projectileStartedBaseline;
        private static long _projectileMissingBaseline;
        private static long _vfxSpawnedBaseline;
        private static long _vfxMissingBaseline;

        public static void Run()
        {
            ResetState();

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
                $"[NpcHeadlessAcceptanceRunner] scene={_scene} seconds={seconds:F0} "
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
            _deadline = 0d;
            _travelRequested = false;
            _subscribed = false;
            _projectileStartedBaseline = 0L;
            _projectileMissingBaseline = 0L;
            _vfxSpawnedBaseline = 0L;
            _vfxMissingBaseline = 0L;
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup >= _deadline)
            {
                Finish(false, "deadline reached before every mixed-exemplar presentation gate passed");
                return;
            }

            if (!Application.isPlaying)
                return;

            NetworkManager? networkManager = NetworkManager.Instance;
            DbConnection? conn = networkManager?.Conn;
            if (networkManager == null || !networkManager.IsConnected || conn == null)
                return;

            if (!_subscribed)
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

            ObserveNpcEntities(conn);
            if (TryGetFailure(out string failure))
            {
                Finish(false, failure);
                return;
            }

            if (AllGatesPass())
                Finish(true, "all mixed-exemplar presentation gates passed");
        }

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

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}
