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
    public static class WorldInteractionManifestExporter
    {
        public const int DoorManifestVersion = 1;
        public const int ProfileManifestVersion = 1;

        private const string ClientWorldDataRoot =
            "Assets/Arena/Resources/SharedData/Worlds";
        private const string ClientProfilePath =
            "Assets/Arena/Resources/SharedData/WorldInteractions/world_interaction_profiles.shared.json";
        private const string ServerWorldDataRoot = "server/src/world_data";
        private const string ServerProfilePath =
            "server/src/world_data/world_interaction_profiles.shared.json";
        private const string ProfileAssetRoot =
            "Assets/Arena/Content/Settings/Interaction";

        [MenuItem("Arena/Dungeons/Export Active World Interactions", false, 120)]
        private static void ExportActiveSceneFromMenu()
        {
            Scene scene = SceneManager.GetActiveScene();
            string dataKey = ToDataKey(scene.name);
            ExportActiveScene(dataKey);
            EditorUtility.DisplayDialog(
                "World Interactions Exported",
                $"Exported door definitions for '{dataKey}' and the shared interaction profiles.",
                "OK");
        }

        public static void ExportActiveScene(string dataKey)
            => ExportActiveScene(dataKey, dataKey);

        /// <summary>
        /// Exports one logical world's doors under a separate artifact key.
        /// Transactional scene builders use this to validate and serialize the
        /// production world identity while keeping incomplete files isolated
        /// under a staging filename until promotion.
        /// </summary>
        internal static void ExportActiveScene(
            string worldDefinitionKey,
            string outputDataKey)
        {
            string normalizedWorldDefinitionKey = NormalizeDataKey(worldDefinitionKey);
            string normalizedOutputDataKey = NormalizeDataKey(outputDataKey);
            DoorAuthoring[] doors = UnityEngine.Object.FindObjectsByType<DoorAuthoring>(
                    FindObjectsInactive.Include)
                .Where(door => !door.TemplateOnly)
                .OrderBy(door => door.DoorDefinitionId, StringComparer.Ordinal)
                .ToArray();
            WorldInteractionProfile[] profiles = LoadProfiles();

            string profileJson = BuildProfileManifestJson(profiles);
            HashSet<string> profileIds = profiles
                .Select(profile => profile.ProfileId)
                .ToHashSet(StringComparer.Ordinal);
            string doorJson = BuildDoorManifestJson(
                normalizedWorldDefinitionKey,
                doors,
                profileIds);

            WritePaired(
                ClientDoorManifestPath(normalizedOutputDataKey),
                ServerDoorManifestPath(normalizedOutputDataKey),
                doorJson);
            WritePaired(ClientProfilePath, ServerProfilePath, profileJson);
            AssetDatabase.Refresh();
        }

        internal static string ClientDoorManifestPath(string outputDataKey)
            => $"{ClientWorldDataRoot}/{NormalizeDataKey(outputDataKey)}.doors.shared.json";

        internal static string ServerDoorManifestPath(string outputDataKey)
            => $"{ServerWorldDataRoot}/{NormalizeDataKey(outputDataKey)}.doors.shared.json";

        public static string BuildProfileManifestJson(
            IReadOnlyCollection<WorldInteractionProfile> profiles)
        {
            if (profiles == null)
                throw new ArgumentNullException(nameof(profiles));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var records = new JArray();
            foreach (WorldInteractionProfile profile in profiles
                         .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal))
            {
                string id = profile.ProfileId;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"Interaction profile '{profile.name}' has no profile ID.");
                if (!seen.Add(id))
                    throw new InvalidOperationException($"Duplicate interaction profile ID '{id}'.");
                if (profile.DurationMs > 0
                    && (profile.CancelConditions & WorldInteractionCancelCondition.Death) == 0)
                {
                    throw new InvalidOperationException(
                        $"Timed interaction profile '{id}' must cancel on death.");
                }

                records.Add(new JObject
                {
                    ["profile_id"] = id,
                    ["progress_label_key"] = profile.ProgressLabelKey,
                    ["duration_ms"] = profile.DurationMs,
                    ["animation_profile_id"] = profile.AnimationProfileId,
                    ["requires_grounded"] = profile.RequiresGrounded,
                    ["requires_stationary"] = profile.RequiresStationary,
                    ["cancel_conditions"] = (int)profile.CancelConditions,
                });
            }

            return Serialize(new JObject
            {
                ["schema_version"] = ProfileManifestVersion,
                ["profiles"] = records,
            });
        }

        public static string BuildDoorManifestJson(
            string dataKey,
            IReadOnlyCollection<DoorAuthoring> doors,
            ISet<string> profileIds)
        {
            string normalizedDataKey = NormalizeDataKey(dataKey);
            if (doors == null)
                throw new ArgumentNullException(nameof(doors));
            if (profileIds == null)
                throw new ArgumentNullException(nameof(profileIds));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var records = new JArray();
            foreach (DoorAuthoring door in doors
                         .OrderBy(door => door.DoorDefinitionId, StringComparer.Ordinal))
            {
                ValidateDoor(door, normalizedDataKey, profileIds, seen);
                Vector3 anchor = door.InteractionPoint;
                Vector3 blockerCenter = door.ClosedBlockerCenter;
                Vector3 blockerSize = door.ClosedBlockerSize;
                records.Add(new JObject
                {
                    ["door_definition_id"] = door.DoorDefinitionId,
                    ["world_definition_key"] = normalizedDataKey.ToUpperInvariant(),
                    ["interaction_anchor"] = Vector(anchor),
                    ["max_interaction_distance"] = Number(door.MaxInteractionDistance),
                    ["closed_blocker"] = new JObject
                    {
                        ["center"] = Vector(blockerCenter),
                        ["size"] = Vector(blockerSize),
                        ["yaw_degrees"] = Number(door.ClosedBlockerYaw),
                    },
                    ["default_open"] = door.DefaultOpen,
                    ["open_interaction_profile_id"] = door.OpenInteractionProfileId,
                    ["close_interaction_profile_id"] = door.CloseInteractionProfileId,
                    ["definition_version"] = door.DefinitionVersion,
                });
            }

            return Serialize(new JObject
            {
                ["schema_version"] = DoorManifestVersion,
                ["world_definition_key"] = normalizedDataKey.ToUpperInvariant(),
                ["doors"] = records,
            });
        }

        private static WorldInteractionProfile[] LoadProfiles()
        {
            return AssetDatabase.FindAssets(
                    "t:WorldInteractionProfile",
                    new[] { ProfileAssetRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<WorldInteractionProfile>)
                .Where(profile => profile != null)
                .Cast<WorldInteractionProfile>()
                .ToArray();
        }

        private static void ValidateDoor(
            DoorAuthoring door,
            string dataKey,
            ISet<string> profileIds,
            ISet<string> seen)
        {
            string id = door.DoorDefinitionId;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"Door '{door.name}' has no stable definition ID.");
            if (!seen.Add(id))
                throw new InvalidOperationException($"Duplicate door definition ID '{id}'.");
            if (!door.ProductionEnabled)
            {
                throw new InvalidOperationException(
                    $"Production door '{id}' is not interaction-enabled.");
            }
            if (!string.Equals(
                    door.WorldDefinitionKey,
                    dataKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Door '{id}' belongs to '{door.WorldDefinitionKey}', not '{dataKey}'.");
            }
            if (!profileIds.Contains(door.OpenInteractionProfileId)
                || !profileIds.Contains(door.CloseInteractionProfileId))
            {
                throw new InvalidOperationException(
                    $"Door '{id}' references an interaction profile absent from the shared catalog.");
            }
            if (!Finite(door.InteractionPoint)
                || !Finite(door.ClosedBlockerCenter)
                || !Finite(door.ClosedBlockerSize)
                || door.ClosedBlockerSize.x <= 0.01f
                || door.ClosedBlockerSize.y <= 0.01f
                || door.ClosedBlockerSize.z <= 0.01f)
            {
                throw new InvalidOperationException(
                    $"Door '{id}' has non-finite or degenerate authority geometry.");
            }
            if (door.Leaves.Length == 0)
                throw new InvalidOperationException($"Door '{id}' has no authored leaves.");

            foreach (DoorAuthoring.LeafPose pose in door.Leaves)
            {
                Transform? leaf = pose.Leaf;
                if (leaf == null)
                    throw new InvalidOperationException($"Door '{id}' has a missing leaf reference.");
                if (GameObjectUtility.GetStaticEditorFlags(leaf.gameObject) != 0)
                    throw new InvalidOperationException($"Door '{id}' leaf '{leaf.name}' is still marked static.");
                foreach (Collider collider in leaf.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.enabled && !collider.isTrigger)
                    {
                        throw new InvalidOperationException(
                            $"Door '{id}' leaf collider '{collider.name}' would leak into immutable collision.");
                    }
                }
            }
        }

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
                throw new InvalidOperationException("Cannot export a non-finite interaction value.");

            string normalized = value.ToString("0.######", CultureInfo.InvariantCulture);
            return new JValue(double.Parse(normalized, CultureInfo.InvariantCulture));
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x)
                && float.IsFinite(value.y)
                && float.IsFinite(value.z);

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
                    $"Paired interaction exports differ: {clientRelativePath} and {serverRelativePath}.");
            }
        }

        private static string NormalizeDataKey(string dataKey)
        {
            if (string.IsNullOrWhiteSpace(dataKey))
                throw new ArgumentException("World interaction data key is required.", nameof(dataKey));

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
