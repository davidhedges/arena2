#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Arena.Interaction;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace Arena.Editor
{
    /// <summary>
    /// Exports the paired client/server trap payloads.
    ///
    /// Placement (<c>*.traps.shared.json</c>) is regenerated on every dungeon
    /// rebuild; behaviour (<c>world_trap_profiles.shared.json</c>) is authored
    /// and independent, so retuning trap damage or timing never requires a
    /// dungeon rebuild — export the profiles and republish.
    /// </summary>
    public static class WorldTrapManifestExporter
    {
        public const int TrapManifestVersion = 1;
        public const int TrapProfileManifestVersion = 1;

        private const string ClientWorldDataRoot =
            "Assets/Arena/Resources/SharedData/Worlds";
        private const string ClientProfilePath =
            "Assets/Arena/Resources/SharedData/WorldInteractions/world_trap_profiles.shared.json";
        private const string ServerWorldDataRoot = "server/src/world_data";
        private const string ServerProfilePath =
            "server/src/world_data/world_trap_profiles.shared.json";
        private const string ProfileAssetRoot = "Assets/Arena/Content/Settings/Traps";

        [MenuItem("Arena/Dungeons/Export Active World Traps", false, 121)]
        private static void ExportActiveSceneFromMenu()
        {
            Scene scene = SceneManager.GetActiveScene();
            string dataKey = ToDataKey(scene.name);
            ExportActiveScene(dataKey);
            EditorUtility.DisplayDialog(
                "World Traps Exported",
                $"Exported trap definitions for '{dataKey}' and the shared trap profiles.",
                "OK");
        }

        public static void ExportActiveScene(string dataKey)
            => ExportActiveScene(dataKey, dataKey);

        /// <summary>
        /// Exports one logical world's traps under a separate artifact key so
        /// transactional builds do not confuse a staging filename with the
        /// stable world identity authored on each trap.
        /// </summary>
        internal static void ExportActiveScene(
            string worldDefinitionKey,
            string outputDataKey)
        {
            string normalizedWorldDefinitionKey = NormalizeDataKey(worldDefinitionKey);
            string normalizedOutputDataKey = NormalizeDataKey(outputDataKey);
            TrapAuthoring[] traps = UnityEngine.Object.FindObjectsByType<TrapAuthoring>(
                    FindObjectsInactive.Include)
                .Where(trap => !trap.TemplateOnly)
                .OrderBy(trap => trap.TrapDefinitionId, StringComparer.Ordinal)
                .ToArray();
            TrapProfile[] profiles = LoadProfiles();

            string profileJson = BuildProfileManifestJson(profiles);
            HashSet<string> profileIds = profiles
                .Select(profile => profile.ProfileId)
                .ToHashSet(StringComparer.Ordinal);
            string trapJson = BuildTrapManifestJson(
                normalizedWorldDefinitionKey,
                traps,
                profileIds);

            WritePaired(
                ClientTrapManifestPath(normalizedOutputDataKey),
                ServerTrapManifestPath(normalizedOutputDataKey),
                trapJson);
            WritePaired(ClientProfilePath, ServerProfilePath, profileJson);
            AssetDatabase.Refresh();
        }

        internal static string ClientTrapManifestPath(string outputDataKey)
            => $"{ClientWorldDataRoot}/{NormalizeDataKey(outputDataKey)}.traps.shared.json";

        internal static string ServerTrapManifestPath(string outputDataKey)
            => $"{ServerWorldDataRoot}/{NormalizeDataKey(outputDataKey)}.traps.shared.json";

        public static string BuildProfileManifestJson(IReadOnlyCollection<TrapProfile> profiles)
        {
            if (profiles == null)
                throw new ArgumentNullException(nameof(profiles));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var records = new JArray();
            foreach (TrapProfile profile in profiles
                         .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal))
            {
                string id = profile.ProfileId;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"Trap profile '{profile.name}' has no profile ID.");
                if (!seen.Add(id))
                    throw new InvalidOperationException($"Duplicate trap profile ID '{id}'.");
                ValidateProfile(profile);

                records.Add(new JObject
                {
                    ["profile_id"] = id,
                    ["trigger_kind"] = TriggerKindWire(profile.TriggerKind),
                    ["trigger_delay_ms"] = profile.TriggerDelayMs,
                    ["cycle_ms"] = profile.CycleMs,
                    ["hazard_start_ms"] = profile.HazardStartMs,
                    ["hazard_end_ms"] = profile.HazardEndMs,
                    ["rearm_ms"] = profile.RearmMs,
                    ["trigger_volume"] = Volume(profile.TriggerVolume),
                    ["hazard_volume"] = Volume(profile.HazardVolume),
                    ["hazard_track"] = Track(profile.HazardTrack),
                    ["on_hit"] = OnHit(profile.OnHit),
                    ["one_hit_per_activation"] = profile.OneHitPerActivation,
                });
            }

            return Serialize(new JObject
            {
                ["schema_version"] = TrapProfileManifestVersion,
                ["profiles"] = records,
            });
        }

        public static string BuildTrapManifestJson(
            string dataKey,
            IReadOnlyCollection<TrapAuthoring> traps,
            ISet<string> profileIds)
        {
            string normalizedDataKey = NormalizeDataKey(dataKey);
            if (traps == null)
                throw new ArgumentNullException(nameof(traps));
            if (profileIds == null)
                throw new ArgumentNullException(nameof(profileIds));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var records = new JArray();
            foreach (TrapAuthoring trap in traps
                         .OrderBy(trap => trap.TrapDefinitionId, StringComparer.Ordinal))
            {
                ValidateTrap(trap, normalizedDataKey, profileIds, seen);
                records.Add(new JObject
                {
                    ["trap_definition_id"] = trap.TrapDefinitionId,
                    ["world_definition_key"] = normalizedDataKey.ToUpperInvariant(),
                    ["trap_profile_id"] = trap.TrapProfileId,
                    ["origin"] = Vector(trap.Origin),
                    ["yaw_degrees"] = Number(trap.YawDegrees),
                    ["footprint_cells"] = trap.FootprintCells,
                    ["definition_version"] = trap.DefinitionVersion,
                });
            }

            return Serialize(new JObject
            {
                ["schema_version"] = TrapManifestVersion,
                ["world_definition_key"] = normalizedDataKey.ToUpperInvariant(),
                ["traps"] = records,
            });
        }

        private static TrapProfile[] LoadProfiles()
        {
            return AssetDatabase.FindAssets("t:TrapProfile", new[] { ProfileAssetRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<TrapProfile>)
                .Where(profile => profile != null)
                .Cast<TrapProfile>()
                .ToArray();
        }

        private static void ValidateProfile(TrapProfile profile)
        {
            string id = profile.ProfileId;
            if (!profile.TriggerVolume.IsValid || !profile.HazardVolume.IsValid)
            {
                throw new InvalidOperationException(
                    $"Trap profile '{id}' has a non-finite or degenerate volume.");
            }
            if (profile.HazardStartMs > profile.HazardEndMs)
            {
                throw new InvalidOperationException(
                    $"Trap profile '{id}' has hazard_start_ms after hazard_end_ms.");
            }
            if (profile.HazardEndMs > profile.CycleMs)
            {
                throw new InvalidOperationException(
                    $"Trap profile '{id}' has a hazard window that outlives its cycle.");
            }
            if (string.IsNullOrWhiteSpace(profile.AnimatorStateName))
            {
                throw new InvalidOperationException(
                    $"Trap profile '{id}' has no animator state to scrub.");
            }
            if (profile.OnHit.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Trap profile '{id}' applies no effects; a trap that cannot hurt anything is authoring debris.");
            }

            int previousTMs = int.MinValue;
            foreach (TrapHazardTrackKey key in profile.HazardTrack)
            {
                if (key.tMs <= previousTMs)
                {
                    throw new InvalidOperationException(
                        $"Trap profile '{id}' has a hazard track that is not strictly increasing in t_ms.");
                }
                if (!float.IsFinite(key.offset.x)
                    || !float.IsFinite(key.offset.y)
                    || !float.IsFinite(key.offset.z))
                {
                    throw new InvalidOperationException(
                        $"Trap profile '{id}' has a non-finite hazard track offset.");
                }
                previousTMs = key.tMs;
            }

            foreach (TrapOnHitEffect effect in profile.OnHit)
            {
                switch (effect.effect)
                {
                    case TrapOnHitEffectKind.Damage when effect.amount <= 0:
                        throw new InvalidOperationException(
                            $"Trap profile '{id}' has a DAMAGE entry with no damage.");
                    case TrapOnHitEffectKind.Dot
                        when effect.tickAmount <= 0
                            || effect.tickIntervalMs <= 0
                            || effect.durationMs <= 0:
                        throw new InvalidOperationException(
                            $"Trap profile '{id}' has a DOT entry with no tick, interval, or duration.");
                    case TrapOnHitEffectKind.Dot when string.IsNullOrWhiteSpace(effect.stackGroup):
                        throw new InvalidOperationException(
                            $"Trap profile '{id}' has a DOT entry with no stack group.");
                }
            }
        }

        private static void ValidateTrap(
            TrapAuthoring trap,
            string dataKey,
            ISet<string> profileIds,
            ISet<string> seen)
        {
            string id = trap.TrapDefinitionId;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"Trap '{trap.name}' has no stable definition ID.");
            if (!seen.Add(id))
                throw new InvalidOperationException($"Duplicate trap definition ID '{id}'.");
            if (!trap.ProductionEnabled)
                throw new InvalidOperationException($"Production trap '{id}' is not enabled.");
            if (!string.Equals(trap.WorldDefinitionKey, dataKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Trap '{id}' belongs to '{trap.WorldDefinitionKey}', not '{dataKey}'.");
            }
            if (trap.Profile == null || !profileIds.Contains(trap.TrapProfileId))
            {
                throw new InvalidOperationException(
                    $"Trap '{id}' references a trap profile absent from the shared catalog.");
            }
            if (!Finite(trap.Origin) || !float.IsFinite(trap.YawDegrees))
                throw new InvalidOperationException($"Trap '{id}' has non-finite placement.");
            if (trap.GetComponent<TrapPresenter>() == null)
                throw new InvalidOperationException($"Trap '{id}' has no presenter.");

            // The collision contract: traps never block movement, sight, or
            // projectiles, so nothing under a trap may reach the immutable bake.
            Collider[] colliders = trap.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Trap '{id}' carries {colliders.Length} collider(s); traps must never contribute collision.");
            }
            if (trap.GetComponentsInChildren<Animator>(true).Length == 0)
                throw new InvalidOperationException($"Trap '{id}' has no animator to scrub.");
        }

        private static string TriggerKindWire(TrapTriggerKind kind)
            => kind switch
            {
                TrapTriggerKind.Proximity => "PROXIMITY",
                TrapTriggerKind.Always => "ALWAYS",
                _ => throw new InvalidOperationException($"Unknown trap trigger kind {kind}."),
            };

        private static string EffectKindWire(TrapOnHitEffectKind kind)
            => kind switch
            {
                TrapOnHitEffectKind.Damage => "DAMAGE",
                TrapOnHitEffectKind.Dot => "DOT",
                _ => throw new InvalidOperationException($"Unknown trap effect kind {kind}."),
            };

        private static JObject Volume(TrapVolume volume)
        {
            return new JObject
            {
                ["center"] = Vector(volume.center),
                ["size"] = Vector(volume.size),
            };
        }

        private static JArray Track(IReadOnlyList<TrapHazardTrackKey> track)
        {
            var records = new JArray();
            foreach (TrapHazardTrackKey key in track)
            {
                records.Add(new JObject
                {
                    ["t_ms"] = key.tMs,
                    ["offset"] = Vector(key.offset),
                });
            }
            return records;
        }

        // Flat entries, not a tagged union: the server parses this with
        // `deny_unknown_fields`, so one struct with every field present is both
        // simpler to keep in lockstep and impossible to silently mis-tag.
        private static JArray OnHit(IReadOnlyList<TrapOnHitEffect> effects)
        {
            var records = new JArray();
            foreach (TrapOnHitEffect effect in effects)
            {
                records.Add(new JObject
                {
                    ["effect"] = EffectKindWire(effect.effect),
                    ["amount"] = Mathf.Max(0, effect.amount),
                    ["damage_type"] = NormalizeWire(effect.damageType, "PHYSICAL"),
                    ["tick_amount"] = Mathf.Max(0, effect.tickAmount),
                    ["tick_interval_ms"] = Mathf.Max(0, effect.tickIntervalMs),
                    ["duration_ms"] = Mathf.Max(0, effect.durationMs),
                    ["stack_group"] = NormalizeWire(effect.stackGroup, string.Empty),
                    ["max_stacks"] = Mathf.Max(1, effect.maxStacks),
                    ["stack_policy"] = NormalizeWire(effect.stackPolicy, "REFRESH"),
                    ["dispel_types"] = new JArray(
                        effect.dispelTypes
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => NormalizeWire(value, string.Empty))),
                });
            }
            return records;
        }

        private static string NormalizeWire(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToUpperInvariant();

        private static JObject Vector(Vector3 value)
        {
            return new JObject
            {
                ["x"] = Number(value.x),
                ["y"] = Number(value.y),
                ["z"] = Number(value.z),
            };
        }

        private static JValue Number(float value)
        {
            if (!float.IsFinite(value))
                throw new InvalidOperationException("Cannot export a non-finite trap value.");

            string normalized = value.ToString("0.######", CultureInfo.InvariantCulture);
            return new JValue(double.Parse(normalized, CultureInfo.InvariantCulture));
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static string Serialize(JObject root)
            => root.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";

        private static void WritePaired(
            string clientRelativePath,
            string serverRelativePath,
            string content)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string clientPath = Path.Combine(projectRoot, clientRelativePath);
            string serverPath = Path.Combine(projectRoot, serverRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(clientPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
            File.WriteAllText(clientPath, content);
            File.WriteAllText(serverPath, content);

            if (!string.Equals(
                    File.ReadAllText(clientPath),
                    File.ReadAllText(serverPath),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Paired trap exports differ: {clientRelativePath} and {serverRelativePath}.");
            }
        }

        private static string NormalizeDataKey(string dataKey)
        {
            if (string.IsNullOrWhiteSpace(dataKey))
                throw new ArgumentException("World trap data key is required.", nameof(dataKey));

            return dataKey.Trim().ToLowerInvariant();
        }

        private static string ToDataKey(string sceneName)
        {
            if (string.Equals(sceneName, "RandomDungeon", StringComparison.Ordinal))
                return "random_dungeon";

            return sceneName.Trim().ToLowerInvariant();
        }
    }
}
