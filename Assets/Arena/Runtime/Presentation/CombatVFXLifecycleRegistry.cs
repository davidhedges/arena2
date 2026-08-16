#nullable enable
using System.Collections;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Input;
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
        private const string LifecycleUntilRadialEffectEnd = "UNTIL_RADIAL_EFFECT_END";
        private const string LifecycleUntilStatusEnd = "UNTIL_STATUS_END";
        private const string AnchorTargetBack = "TARGET_BACK";
        private const float GroundYOffset = 0.03f;

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
                {
                    _removeList.Add(key);
                    continue;
                }

                Transform? groundFollowAnchor = entry.GroundFollowAnchor;
                if (groundFollowAnchor == null)
                    continue;

                Vector3 anchorPosition = groundFollowAnchor.position;
                bool hasGroundHeight = TrySampleGroundHeight(anchorPosition, out float groundHeight);
                entry.Instance.transform.SetPositionAndRotation(
                    ResolveGroundFollowPosition(
                        entry.Instance.transform.position,
                        anchorPosition,
                        entry.LocalPositionOffset,
                        hasGroundHeight,
                        groundHeight),
                    Quaternion.identity);
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
            Transform? followAnchor,
            bool followsGroundPosition = false)
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
                _coroutineOwner.StartCoroutine(SpawnAfterDelay(
                    template,
                    cue,
                    context,
                    position,
                    rotation,
                    followAnchor,
                    followsGroundPosition));
                return;
            }

            SpawnPrefab(template, cue, context, position, rotation, followAnchor, followsGroundPosition);
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

        // Ends persistent emanation cues when the authoritative radial-effect row is deleted.
        public void DestroyForRadialEffectEnd(string radialEffectKey)
        {
            if (string.IsNullOrWhiteSpace(radialEffectKey))
                return;

            DestroyMatchingScripted(radialEffectKey, LifecycleUntilRadialEffectEnd);
            DestroyMatchingPrefabs(radialEffectKey, LifecycleUntilRadialEffectEnd);
        }

        public void DestroyAllRadialEffects()
        {
            DestroyAllMatchingScripted(LifecycleUntilRadialEffectEnd);
            DestroyAllMatchingPrefabs(LifecycleUntilRadialEffectEnd);
        }

        // Ends persistent status cues when the authoritative StatusEffect row is deleted.
        public void DestroyForStatusEnd(string statusEffectKey)
        {
            if (string.IsNullOrWhiteSpace(statusEffectKey))
                return;

            DestroyMatchingScripted(statusEffectKey, LifecycleUntilStatusEnd);
            DestroyMatchingPrefabs(statusEffectKey, LifecycleUntilStatusEnd);
        }

        public void DestroyAllStatusEffects()
        {
            DestroyAllMatchingScripted(LifecycleUntilStatusEnd);
            DestroyAllMatchingPrefabs(LifecycleUntilStatusEnd);
        }

        // Retunes live status visuals when only the replicated stack count moved. Returns
        // false when nothing live can absorb the change, which leaves the caller on the
        // rebuild path it would have taken anyway.
        public bool TryRouteStackCount(string statusEffectKey, uint stacks)
        {
            if (string.IsNullOrWhiteSpace(statusEffectKey))
                return false;

            bool routed = false;
            foreach (var entry in _scripted.Values)
            {
                if (!string.Equals(entry.ActionInstanceId, statusEffectKey, System.StringComparison.Ordinal))
                    continue;
                if (!string.Equals(entry.Lifecycle, LifecycleUntilStatusEnd, System.StringComparison.Ordinal))
                    continue;
                if (entry.Visual is not IStackScaledVFX scaled)
                    continue;

                bool accepted = false;
                RouteToVfx(entry.Visual, () => accepted = scaled.TrySetStackCount(stacks));
                routed |= accepted;
            }

            return routed;
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

        /// <summary>
        /// Anchors backed by an authored <see cref="Appearance.AvatarVfxSockets"/> marker. Those
        /// sockets own their placement, so attached VFX inherit the socket basis instead of
        /// freezing the world rotation they spawned with.
        /// </summary>
        private static bool IsAvatarSocketAnchor(string anchor)
        {
            return string.Equals(
                WireIdentifier.Normalize(anchor),
                AnchorTargetBack,
                System.StringComparison.Ordinal);
        }

        private IEnumerator SpawnAfterDelay(
            CombatVFXRegistry.Template template,
            CombatVfxCueCatalog cue,
            CombatVFXTemplateContext context,
            Vector3 position,
            Quaternion rotation,
            Transform? followAnchor,
            bool followsGroundPosition)
        {
            yield return new WaitForSeconds(cue.StartDelayMs / 1000f);
            if (IsReleaseBoundActionFinished(cue, context))
                yield break;

            SpawnPrefab(template, cue, context, position, rotation, followAnchor, followsGroundPosition);
        }

        private void SpawnPrefab(
            CombatVFXRegistry.Template template,
            CombatVfxCueCatalog cue,
            CombatVFXTemplateContext context,
            Vector3 position,
            Quaternion rotation,
            Transform? followAnchor,
            bool followsGroundPosition)
        {
            if (followsGroundPosition && followAnchor != null)
            {
                bool hasGroundHeight = TrySampleGroundHeight(followAnchor.position, out float groundHeight);
                position = ResolveGroundFollowPosition(
                    position,
                    followAnchor.position,
                    template.LocalPositionOffset,
                    hasGroundHeight,
                    groundHeight);
                rotation = Quaternion.identity;
            }

            GameObject prefab = template.Prefab;
            GameObject instance = Object.Instantiate(
                prefab,
                position,
                rotation * template.LocalRotation);
            instance.name = $"{prefab.name}_{cue.Key}";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DebugSpawnedPrefabCount++;
#endif
            if (followAnchor != null && !followsGroundPosition)
            {
                if (IsAvatarSocketAnchor(cue.Anchor))
                {
                    // An authored avatar socket already carries its own calibration, so the
                    // instance adopts the socket's basis outright and then bends and turns with
                    // the bone it hangs off.
                    instance.transform.SetParent(followAnchor, false);
                    instance.transform.SetLocalPositionAndRotation(
                        template.LocalPositionOffset,
                        template.LocalRotation);
                }
                else
                {
                    // Every other anchor keeps the world rotation it spawned with and rides the
                    // anchor from there; hand and weapon cues are tuned against that basis.
                    instance.transform.SetParent(followAnchor, true);
                    instance.transform.localPosition = template.LocalPositionOffset;
                }
            }

            VFXUtils.ApplyPrefabPresentationScale(instance, template.Scale);
            FulminationArcVFX.ConfigureIfNeeded(cue.VfxId, instance, context);

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

                _prefabs[key] = new PrefabEntry(
                    context.ActionInstanceId,
                    lifecycle,
                    instance,
                    followsGroundPosition ? followAnchor : null,
                    template.LocalPositionOffset);
                return;
            }

            if (string.Equals(lifecycle, LifecycleUntilCastEnd, System.StringComparison.Ordinal)
                || string.Equals(lifecycle, LifecycleUntilRadialEffectEnd, System.StringComparison.Ordinal)
                || string.Equals(lifecycle, LifecycleUntilStatusEnd, System.StringComparison.Ordinal))
            {
                // Loop and hold until the owning authoritative state row is deleted.
                ConfigureReleaseBoundParticleSystems(instance);
                string key = PrefabKey(context);
                if (_prefabs.TryGetValue(key, out PrefabEntry old))
                {
                    DestroyInstance(old.Instance);
                    _prefabs.Remove(key);
                }

                _prefabs[key] = new PrefabEntry(
                    context.ActionInstanceId,
                    lifecycle,
                    instance,
                    followsGroundPosition ? followAnchor : null,
                    template.LocalPositionOffset);
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

        internal static IEnumerator DestroyWhenParticleSystemsFinish(GameObject instance, string vfxId)
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

        private void DestroyAllMatchingScripted(string lifecycle)
        {
            _removeList.Clear();
            foreach (var (key, entry) in _scripted)
            {
                if (!string.Equals(entry.Lifecycle, lifecycle, System.StringComparison.Ordinal))
                    continue;

                DisposeVfx(entry.Visual);
                _removeList.Add(key);
            }

            foreach (string key in _removeList)
                _scripted.Remove(key);
        }

        private void DestroyAllMatchingPrefabs(string lifecycle)
        {
            _removeList.Clear();
            foreach (var (key, entry) in _prefabs)
            {
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

        internal static Vector3 ResolveGroundFollowPosition(
            Vector3 currentPosition,
            Vector3 anchorPosition,
            Vector3 localPositionOffset,
            bool hasGroundHeight,
            float groundHeight)
        {
            return new Vector3(
                anchorPosition.x + localPositionOffset.x,
                hasGroundHeight
                    ? groundHeight + GroundYOffset + localPositionOffset.y
                    : currentPosition.y,
                anchorPosition.z + localPositionOffset.z);
        }

        private static bool TrySampleGroundHeight(Vector3 anchorPosition, out float groundHeight)
        {
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetLocalPredictionEnvironment(out IMovementEnvironment? environment)
                && environment != null)
            {
                groundHeight = environment.SampleGroundHeight(
                    anchorPosition.x,
                    anchorPosition.z,
                    anchorPosition.y);
                return true;
            }

            groundHeight = 0f;
            return false;
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
            public readonly Transform? GroundFollowAnchor;
            public readonly Vector3 LocalPositionOffset;

            public PrefabEntry(
                string actionInstanceId,
                string lifecycle,
                GameObject instance,
                Transform? groundFollowAnchor,
                Vector3 localPositionOffset)
            {
                ActionInstanceId = actionInstanceId;
                Lifecycle = lifecycle;
                Instance = instance;
                GroundFollowAnchor = groundFollowAnchor;
                LocalPositionOffset = localPositionOffset;
            }
        }
    }
}
