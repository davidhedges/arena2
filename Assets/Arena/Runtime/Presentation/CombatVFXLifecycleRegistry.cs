#nullable enable
using System.Collections;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Presentation.VFX;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    internal sealed class CombatVFXLifecycleRegistry
    {
        private const float FallbackDurationSeconds = 3f;
        private const float ParticleSystemTimeoutSeconds = 30f;
        private const float FinishedActionRetentionSeconds = 10f;
        private const string LifecycleParticleSystem = "PARTICLE_SYSTEM";
        private const string LifecycleUntilReleaseEvent = "UNTIL_RELEASE_EVENT";
        private const string LifecycleUntilTerminalEvent = "UNTIL_TERMINAL_EVENT";
        private const string LifecycleUntilCastEnd = "UNTIL_CAST_END";

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static long DebugSpawnedScriptedCount { get; private set; }
        internal static long DebugSpawnedPrefabCount { get; private set; }
        internal static long DebugMissingTemplateCount { get; private set; }
#endif

        private readonly MonoBehaviour _coroutineOwner;
        private readonly Dictionary<string, ScriptedEntry> _scripted = new();
        private readonly Dictionary<string, PrefabEntry> _prefabs = new();
        private readonly Dictionary<string, float> _finishedReleaseBoundActions = new(System.StringComparer.Ordinal);
        private readonly HashSet<string> _missingTemplateWarnings = new(System.StringComparer.Ordinal);
        private readonly List<string> _removeList = new();

        public CombatVFXLifecycleRegistry(MonoBehaviour coroutineOwner)
        {
            _coroutineOwner = coroutineOwner;
        }

        public void Tick(float dt)
        {
            _removeList.Clear();
            foreach (var (key, entry) in _scripted)
            {
                bool keepAlive;
                try
                {
                    keepAlive = entry.Visual.Tick(dt);
                }
                catch (MissingReferenceException)
                {
                    keepAlive = false;
                }

                if (!keepAlive)
                {
                    DisposeVfx(entry.Visual);
                    _removeList.Add(key);
                }
            }

            foreach (string key in _removeList)
                _scripted.Remove(key);

            _removeList.Clear();
            foreach (var (key, entry) in _prefabs)
            {
                if (entry.Instance == null)
                    _removeList.Add(key);
            }

            foreach (string key in _removeList)
                _prefabs.Remove(key);

            _removeList.Clear();
            float finishedActionCutoff = Time.time - FinishedActionRetentionSeconds;
            foreach (var (actionInstanceId, finishedAt) in _finishedReleaseBoundActions)
            {
                if (finishedAt < finishedActionCutoff)
                    _removeList.Add(actionInstanceId);
            }

            foreach (string actionInstanceId in _removeList)
                _finishedReleaseBoundActions.Remove(actionInstanceId);
        }

        public void Spawn(
            CombatVfxCueCatalog cue,
            CombatVFXTemplateContext context,
            Vector3 position,
            Quaternion rotation,
            Transform? followAnchor)
        {
            if (CombatVFXTemplateRegistry.IsScriptedTemplate(cue.VfxId))
            {
                if (cue.StartDelayMs > 0)
                {
                    _coroutineOwner.StartCoroutine(StartScriptedAfterDelay(cue, context));
                    return;
                }

                StartScripted(cue, context);
                return;
            }

            CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(cue.VfxId);
            if (template == null)
            {
                WarnMissingTemplate(cue.VfxId);
                return;
            }

            if (cue.StartDelayMs > 0)
            {
                _coroutineOwner.StartCoroutine(SpawnAfterDelay(template, cue, context, position, rotation, followAnchor));
                return;
            }

            SpawnPrefab(template, cue, context, position, rotation, followAnchor);
        }

        public void RouteUpdate(CombatVFXTemplateContext context)
        {
            foreach (var entry in _scripted.Values)
            {
                if (!string.Equals(entry.ActionInstanceId, context.ActionInstanceId, System.StringComparison.Ordinal))
                    continue;

                RouteToVfx(entry.Visual, () => entry.Visual.OnUpdate(context.Origin, context.Direction, context.Speed));
            }
        }

        public void RouteTerminal(CombatVFXTemplateContext context, bool fizzle)
        {
            foreach (var entry in _scripted.Values)
            {
                if (!string.Equals(entry.ActionInstanceId, context.ActionInstanceId, System.StringComparison.Ordinal))
                    continue;
                if (!string.Equals(entry.Lifecycle, LifecycleUntilTerminalEvent, System.StringComparison.Ordinal))
                    continue;

                if (fizzle)
                    RouteToVfx(entry.Visual, () => entry.Visual.OnFizzle(context.Point));
                else
                    RouteToVfx(entry.Visual, () => entry.Visual.OnImpact(context.Point));
            }

            MarkReleaseBoundActionFinished(context.ActionInstanceId);
            DestroyMatchingScripted(context.ActionInstanceId, LifecycleUntilReleaseEvent);
            DestroyMatchingPrefabs(context.ActionInstanceId, LifecycleUntilReleaseEvent);
        }

        public void RouteRelease(CombatVFXTemplateContext context)
        {
            MarkReleaseBoundActionFinished(context.ActionInstanceId);
            DestroyMatchingScripted(context.ActionInstanceId, LifecycleUntilReleaseEvent);
            DestroyMatchingPrefabs(context.ActionInstanceId, LifecycleUntilReleaseEvent);
        }

        // Ends UNTIL_CAST_END cues when the owning cast/channel's ActiveCast row is deleted.
        public void DestroyForCastEnd(string actionInstanceId)
        {
            if (string.IsNullOrWhiteSpace(actionInstanceId))
                return;

            DestroyMatchingScripted(actionInstanceId, LifecycleUntilCastEnd);
            DestroyMatchingPrefabs(actionInstanceId, LifecycleUntilCastEnd);
        }

        public void Dispose()
        {
            foreach (var entry in _scripted.Values)
                DisposeVfx(entry.Visual);
            _scripted.Clear();

            foreach (var entry in _prefabs.Values)
                DestroyInstance(entry.Instance);
            _prefabs.Clear();
        }

        private IEnumerator StartScriptedAfterDelay(CombatVfxCueCatalog cue, CombatVFXTemplateContext context)
        {
            yield return new WaitForSeconds(cue.StartDelayMs / 1000f);
            if (IsReleaseBoundActionFinished(cue, context))
                yield break;

            StartScripted(cue, context);
        }

        private void StartScripted(CombatVfxCueCatalog cue, CombatVFXTemplateContext context)
        {
            if (!CombatVFXTemplateRegistry.TryCreateScripted(cue.VfxId, context, out ISpellVFX? visual)
                || visual == null)
            {
                WarnMissingTemplate(cue.VfxId);
                return;
            }

            string key = ScriptedKey(context);
            if (_scripted.TryGetValue(key, out ScriptedEntry old))
            {
                DisposeVfx(old.Visual);
                _scripted.Remove(key);
            }

            _scripted[key] = new ScriptedEntry(
                context.ActionInstanceId,
                WireIdentifier.Normalize(cue.Lifecycle),
                visual);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DebugSpawnedScriptedCount++;
#endif
        }

        private IEnumerator SpawnAfterDelay(
            CombatVFXRegistry.Template template,
            CombatVfxCueCatalog cue,
            CombatVFXTemplateContext context,
            Vector3 position,
            Quaternion rotation,
            Transform? followAnchor)
        {
            yield return new WaitForSeconds(cue.StartDelayMs / 1000f);
            if (IsReleaseBoundActionFinished(cue, context))
                yield break;

            SpawnPrefab(template, cue, context, position, rotation, followAnchor);
        }

        private void SpawnPrefab(
            CombatVFXRegistry.Template template,
            CombatVfxCueCatalog cue,
            CombatVFXTemplateContext context,
            Vector3 position,
            Quaternion rotation,
            Transform? followAnchor)
        {
            GameObject prefab = template.Prefab;
            GameObject instance = Object.Instantiate(prefab, position, rotation);
            instance.name = $"{prefab.name}_{cue.Key}";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DebugSpawnedPrefabCount++;
#endif
            if (followAnchor != null)
            {
                instance.transform.SetParent(followAnchor, true);
                instance.transform.localPosition = template.LocalPositionOffset;
            }

            VFXUtils.ApplyPrefabPresentationScale(instance, template.Scale);

            string lifecycle = WireIdentifier.Normalize(cue.Lifecycle);
            if (string.Equals(lifecycle, LifecycleParticleSystem, System.StringComparison.Ordinal))
            {
                PlayParticleSystems(instance);
                _coroutineOwner.StartCoroutine(DestroyWhenParticleSystemsFinish(instance, cue.VfxId));
                return;
            }

            if (string.Equals(lifecycle, LifecycleUntilReleaseEvent, System.StringComparison.Ordinal))
            {
                if (IsReleaseBoundActionFinished(context.ActionInstanceId))
                {
                    DestroyInstance(instance);
                    return;
                }

                ConfigureReleaseBoundParticleSystems(instance);
                string key = PrefabKey(context);
                if (_prefabs.TryGetValue(key, out PrefabEntry old))
                {
                    DestroyInstance(old.Instance);
                    _prefabs.Remove(key);
                }

                _prefabs[key] = new PrefabEntry(context.ActionInstanceId, lifecycle, instance);
                return;
            }

            if (string.Equals(lifecycle, LifecycleUntilCastEnd, System.StringComparison.Ordinal))
            {
                // Loop and hold until the owning ActiveCast row is deleted, so held prefabs last
                // exactly as long as their authoritative cast/channel.
                ConfigureReleaseBoundParticleSystems(instance);
                string key = PrefabKey(context);
                if (_prefabs.TryGetValue(key, out PrefabEntry old))
                {
                    DestroyInstance(old.Instance);
                    _prefabs.Remove(key);
                }

                _prefabs[key] = new PrefabEntry(context.ActionInstanceId, lifecycle, instance);
                return;
            }

            int durationMs = cue.DurationMs > int.MaxValue
                ? int.MaxValue
                : (int)cue.DurationMs;
            float durationSeconds = durationMs > 0
                ? durationMs / 1000f
                : FallbackDurationSeconds;
            Object.Destroy(instance, durationSeconds);
        }

        private static IEnumerator DestroyWhenParticleSystemsFinish(GameObject instance, string vfxId)
        {
            ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length == 0)
            {
                Object.Destroy(instance, FallbackDurationSeconds);
                yield break;
            }

            // Give play-on-awake particle systems one frame to enter their live state after instantiation.
            yield return null;

            float deadline = Time.time + ParticleSystemTimeoutSeconds;
            while (instance != null && Time.time < deadline)
            {
                bool anyAlive = false;
                foreach (ParticleSystem system in systems)
                {
                    if (system != null && system.IsAlive(true))
                    {
                        anyAlive = true;
                        break;
                    }
                }

                if (!anyAlive)
                    break;

                yield return null;
            }

            if (instance != null && Time.time >= deadline)
            {
                Debug.LogWarning(
                    $"Combat VFX prefab '{WireIdentifier.Normalize(vfxId)}' did not finish particle playback within {ParticleSystemTimeoutSeconds:0.#} seconds; destroying instance to avoid a leaked visual.");
            }

            DestroyInstance(instance);
        }

        private static void PlayParticleSystems(GameObject instance)
        {
            ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            foreach (ParticleSystem system in systems)
                system.Play(true);
        }

        private static void ConfigureReleaseBoundParticleSystems(GameObject instance)
        {
            ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            foreach (ParticleSystem system in systems)
            {
                ParticleSystem.MainModule main = system.main;
                main.loop = true;
                system.Play(true);
            }
        }

        private void DestroyMatchingScripted(string actionInstanceId, string lifecycle)
        {
            _removeList.Clear();
            foreach (var (key, entry) in _scripted)
            {
                if (!string.Equals(entry.ActionInstanceId, actionInstanceId, System.StringComparison.Ordinal))
                    continue;
                if (!string.Equals(entry.Lifecycle, lifecycle, System.StringComparison.Ordinal))
                    continue;

                DisposeVfx(entry.Visual);
                _removeList.Add(key);
            }

            foreach (string key in _removeList)
                _scripted.Remove(key);
        }

        private void DestroyMatchingPrefabs(string actionInstanceId, string lifecycle)
        {
            _removeList.Clear();
            foreach (var (key, entry) in _prefabs)
            {
                if (!string.Equals(entry.ActionInstanceId, actionInstanceId, System.StringComparison.Ordinal))
                    continue;
                if (!string.Equals(entry.Lifecycle, lifecycle, System.StringComparison.Ordinal))
                    continue;

                DestroyInstance(entry.Instance);
                _removeList.Add(key);
            }

            foreach (string key in _removeList)
                _prefabs.Remove(key);
        }

        private static void RouteToVfx(ISpellVFX visual, System.Action route)
        {
            try
            {
                route();
            }
            catch (MissingReferenceException)
            {
            }
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

        private static string ScriptedKey(CombatVFXTemplateContext context)
        {
            return $"{context.ActionInstanceId}:{context.CueKey}";
        }

        private static string PrefabKey(CombatVFXTemplateContext context)
        {
            return $"{context.ActionInstanceId}:{context.CueKey}";
        }

        private static bool IsReleaseBoundLifecycle(CombatVfxCueCatalog cue)
        {
            return string.Equals(
                WireIdentifier.Normalize(cue.Lifecycle),
                LifecycleUntilReleaseEvent,
                System.StringComparison.Ordinal);
        }

        private bool IsReleaseBoundActionFinished(CombatVfxCueCatalog cue, CombatVFXTemplateContext context)
        {
            return IsReleaseBoundLifecycle(cue) && IsReleaseBoundActionFinished(context.ActionInstanceId);
        }

        private bool IsReleaseBoundActionFinished(string actionInstanceId)
        {
            return _finishedReleaseBoundActions.ContainsKey(actionInstanceId);
        }

        private void MarkReleaseBoundActionFinished(string actionInstanceId)
        {
            if (!string.IsNullOrWhiteSpace(actionInstanceId))
                _finishedReleaseBoundActions[actionInstanceId] = Time.time;
        }

        private static void DestroyInstance(GameObject? instance)
        {
            if (instance != null)
                Object.Destroy(instance);
        }

        private void WarnMissingTemplate(string vfxId)
        {
            string normalizedId = WireIdentifier.Normalize(vfxId);
            if (string.IsNullOrWhiteSpace(normalizedId))
                normalizedId = "<missing>";
            if (!_missingTemplateWarnings.Add(normalizedId))
                return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DebugMissingTemplateCount++;
#endif

            Debug.LogWarning(
                $"Combat VFX cue references unresolved template id '{normalizedId}'. Register a prefab in CombatVFXRegistry or a scripted template in {nameof(CombatVFXTemplateRegistry)}.");
        }

        private readonly struct ScriptedEntry
        {
            public readonly string ActionInstanceId;
            public readonly string Lifecycle;
            public readonly ISpellVFX Visual;

            public ScriptedEntry(string actionInstanceId, string lifecycle, ISpellVFX visual)
            {
                ActionInstanceId = actionInstanceId;
                Lifecycle = lifecycle;
                Visual = visual;
            }
        }

        private readonly struct PrefabEntry
        {
            public readonly string ActionInstanceId;
            public readonly string Lifecycle;
            public readonly GameObject Instance;

            public PrefabEntry(string actionInstanceId, string lifecycle, GameObject instance)
            {
                ActionInstanceId = actionInstanceId;
                Lifecycle = lifecycle;
                Instance = instance;
            }
        }
    }
}
