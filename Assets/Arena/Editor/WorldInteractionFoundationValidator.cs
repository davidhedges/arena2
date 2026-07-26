#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace Arena.Editor
{
    public static class WorldInteractionFoundationValidator
    {
        private const string RandomDungeonScenePath =
            "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity";
        private const string ClientDoorManifestPath =
            "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.doors.shared.json";
        private const string ServerDoorManifestPath =
            "server/src/world_data/random_dungeon.doors.shared.json";
        private const string ClientProfileManifestPath =
            "Assets/Arena/Resources/SharedData/WorldInteractions/world_interaction_profiles.shared.json";
        private const string ServerProfileManifestPath =
            "server/src/world_data/world_interaction_profiles.shared.json";
        private const string HumanoidUseProfilePath =
            "Assets/Arena/Resources/InteractionAnimations/HumanoidUseAnimation.asset";
        private const string ExtractedClipRoot =
            "Assets/Arena/Content/Animation/Extracted/StylizedCharacter/Human/Male/BasePack";
        private const string GatewayVariantRoot =
            "Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Interactables/Gateways";

        private static readonly string[] RequiredGatewayVariants =
        {
            "COMP_Door_01_med_01_Arena.prefab",
            "COMP_Door_01_med_02_Arena.prefab",
            "COMP_Door_01_large_Arena.prefab",
            "P_PROP_bars_doorway_dungeon_01_Arena.prefab",
        };

        private static readonly string[] RequiredUseClips =
        {
            "Emote_Use_Start.anim",
            "Emote_Use_Loop.anim",
            "Emote_Use_End.anim",
        };

        [MenuItem("Arena/Interaction/Validate Checked-In Foundation", false, 11)]
        private static void ValidateFromMenu()
        {
            ValidationSummary summary = ValidateCheckedInFoundation();
            EditorUtility.DisplayDialog(
                "World Interaction Validation",
                $"Validated {summary.DoorCount} production doors, "
                + $"{summary.GatewayVariantCount} Arena gateway variants, "
                + "paired manifests, and the humanoid use profile.",
                "OK");
        }

        public static ValidationSummary ValidateCheckedInFoundation()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, ".."));
            string doorJson = ReadIdenticalPair(
                projectRoot,
                ClientDoorManifestPath,
                ServerDoorManifestPath);
            string profileJson = ReadIdenticalPair(
                projectRoot,
                ClientProfileManifestPath,
                ServerProfileManifestPath);

            JObject doorManifest = JObject.Parse(doorJson);
            JObject profileManifest = JObject.Parse(profileJson);
            RequireSchemaVersion(doorManifest, 1, "door manifest");
            RequireSchemaVersion(profileManifest, 1, "interaction profile manifest");

            HashSet<string> manifestDoorIds = ReadRequiredIds(
                doorManifest["doors"] as JArray,
                "door_definition_id",
                "door manifest");
            if (manifestDoorIds.Count == 0)
                throw new InvalidOperationException("Door manifest contains no doors.");

            HashSet<string> profileIds = ReadRequiredIds(
                profileManifest["profiles"] as JArray,
                "profile_id",
                "interaction profile manifest");
            RequireProfile(profileManifest, profileIds, "WORLD_DOOR_INSTANT", 0);
            RequireProfile(profileManifest, profileIds, "TIMED_HUMANOID_USE", 1500);

            ValidateHumanoidUseAssets();
            int variantCount = ValidateGatewayVariants();
            ValidateProductionScene(manifestDoorIds);
            return new ValidationSummary(manifestDoorIds.Count, variantCount);
        }

        private static string ReadIdenticalPair(
            string projectRoot,
            string clientPath,
            string serverPath)
        {
            string clientAbsolute = Path.Combine(projectRoot, clientPath);
            string serverAbsolute = Path.Combine(projectRoot, serverPath);
            if (!File.Exists(clientAbsolute) || !File.Exists(serverAbsolute))
            {
                throw new InvalidOperationException(
                    $"Missing paired interaction export: '{clientPath}' or '{serverPath}'.");
            }

            string client = File.ReadAllText(clientAbsolute);
            string server = File.ReadAllText(serverAbsolute);
            if (!string.Equals(client, server, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Paired interaction exports differ: '{clientPath}' and '{serverPath}'.");
            }
            return client;
        }

        private static void RequireSchemaVersion(
            JObject manifest,
            int expected,
            string label)
        {
            int actual = manifest["schema_version"]?.Value<int>() ?? 0;
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{label} schema is {actual}; expected {expected}.");
            }
        }

        private static HashSet<string> ReadRequiredIds(
            JArray? rows,
            string propertyName,
            string label)
        {
            if (rows == null)
                throw new InvalidOperationException($"{label} has no rows.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken row in rows)
            {
                string id = row[propertyName]?.Value<string>()?.Trim()
                    ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"{label} contains an empty ID.");
                if (!ids.Add(id))
                    throw new InvalidOperationException($"{label} contains duplicate ID '{id}'.");
            }
            return ids;
        }

        private static void RequireProfile(
            JObject manifest,
            ISet<string> profileIds,
            string profileId,
            int durationMs)
        {
            if (!profileIds.Contains(profileId))
                throw new InvalidOperationException($"Missing interaction profile '{profileId}'.");

            JToken row = ((JArray)manifest["profiles"]!)
                .First(entry => string.Equals(
                    entry["profile_id"]?.Value<string>(),
                    profileId,
                    StringComparison.Ordinal));
            if (row["duration_ms"]?.Value<int>() != durationMs)
            {
                throw new InvalidOperationException(
                    $"Interaction profile '{profileId}' has an unexpected duration.");
            }
        }

        private static void ValidateHumanoidUseAssets()
        {
            WorldInteractionAnimationProfile? profile =
                AssetDatabase.LoadAssetAtPath<WorldInteractionAnimationProfile>(
                    HumanoidUseProfilePath);
            if (profile == null
                || !string.Equals(
                    profile.ProfileId,
                    "HUMANOID_USE",
                    StringComparison.Ordinal)
                || profile.StartClip == null
                || profile.LoopClip == null
                || profile.EndClip == null)
            {
                throw new InvalidOperationException(
                    $"Humanoid use animation profile is missing or incomplete: {HumanoidUseProfilePath}");
            }

            foreach (string clipName in RequiredUseClips)
            {
                string path = $"{ExtractedClipRoot}/{clipName}";
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) == null)
                    throw new InvalidOperationException($"Missing extracted use clip: {path}");
            }

            if (!profile.LoopClip.isLooping)
            {
                Debug.LogWarning(
                    $"[{nameof(WorldInteractionFoundationValidator)}] "
                    + $"{profile.LoopClip.name} does not report isLooping=true; "
                    + "verify its FBX clip import loop setting during the visual gate.");
            }
        }

        private static int ValidateGatewayVariants()
        {
            foreach (string fileName in RequiredGatewayVariants)
            {
                string path = $"{GatewayVariantRoot}/{fileName}";
                GameObject? prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    throw new InvalidOperationException($"Missing Arena gateway variant: {path}");

                DoorAuthoring? authoring = prefab.GetComponent<DoorAuthoring>();
                DoorMotor? motor = prefab.GetComponent<DoorMotor>();
                DoorInteractable? interactable = prefab.GetComponent<DoorInteractable>();
                var violations = new List<string>();
                if (authoring == null)
                    violations.Add("missing DoorAuthoring");
                else
                {
                    if (!authoring.TemplateOnly)
                        violations.Add("TemplateOnly=false");
                    if (authoring.ProductionEnabled)
                        violations.Add("ProductionEnabled=true");
                }
                if (motor == null)
                    violations.Add("missing DoorMotor (check missing script reference)");
                if (interactable == null)
                    violations.Add("missing DoorInteractable (check missing script reference)");
                if (violations.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Gateway variant has an invalid template/component contract: {path}. "
                        + string.Join("; ", violations));
                }
                ValidateLeaves(authoring!);
                ValidateHitbox(prefab, authoring!.DoorDefinitionId);
            }
            return RequiredGatewayVariants.Length;
        }

        private static void ValidateProductionScene(ISet<string> manifestDoorIds)
        {
            Scene scene = SceneManager.GetSceneByPath(RandomDungeonScenePath);
            bool openedForValidation = !scene.IsValid() || !scene.isLoaded;
            if (openedForValidation)
            {
                scene = EditorSceneManager.OpenScene(
                    RandomDungeonScenePath,
                    OpenSceneMode.Additive);
            }

            try
            {
                DoorAuthoring[] doors = scene.GetRootGameObjects()
                    .SelectMany(root =>
                        root.GetComponentsInChildren<DoorAuthoring>(true))
                    .Where(door => !door.TemplateOnly)
                    .ToArray();
                var sceneIds = new HashSet<string>(
                    doors.Select(door => door.DoorDefinitionId),
                    StringComparer.Ordinal);
                if (!sceneIds.SetEquals(manifestDoorIds))
                {
                    throw new InvalidOperationException(
                        "Production scene door IDs do not match the paired door manifest.");
                }

                foreach (DoorAuthoring door in doors)
                {
                    if (!door.ProductionEnabled
                        || door.GetComponent<DoorMotor>() == null
                        || door.GetComponent<DoorInteractable>() == null)
                    {
                        throw new InvalidOperationException(
                            $"Production door '{door.DoorDefinitionId}' is not runtime-enabled.");
                    }
                    ValidateLeaves(door);
                    ValidateHitbox(door.gameObject, door.DoorDefinitionId);
                }
            }
            finally
            {
                if (openedForValidation && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        private static void ValidateLeaves(DoorAuthoring authoring)
        {
            if (authoring.Leaves.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Door '{authoring.DoorDefinitionId}' has no leaves.");
            }

            foreach (DoorAuthoring.LeafPose pose in authoring.Leaves)
            {
                Transform? leaf = pose.Leaf;
                if (leaf == null)
                {
                    throw new InvalidOperationException(
                        $"Door '{authoring.DoorDefinitionId}' has a missing leaf.");
                }
                foreach (Transform child in leaf.GetComponentsInChildren<Transform>(true))
                {
                    if (GameObjectUtility.GetStaticEditorFlags(child.gameObject) != 0)
                    {
                        throw new InvalidOperationException(
                            $"Animated leaf '{child.name}' is still marked static.");
                    }
                }
                foreach (Collider collider in leaf.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.enabled && !collider.isTrigger)
                    {
                        throw new InvalidOperationException(
                            $"Animated leaf collider '{collider.name}' is immutable.");
                    }
                }
            }
        }

        private static void ValidateHitbox(GameObject root, string doorId)
        {
            DoorInteractable? interactable = root.GetComponent<DoorInteractable>();
            WorldInteractionHitbox? hitbox =
                root.GetComponentInChildren<WorldInteractionHitbox>(true);
            if (hitbox == null
                || hitbox.GetComponent<Collider>() is not Collider collider
                || !collider.isTrigger
                || interactable == null
                || !ReferenceEquals(hitbox.Interactable, interactable))
            {
                throw new InvalidOperationException(
                    $"Door '{doorId}' has no trigger hitbox bound to its interactable.");
            }
        }

        public readonly struct ValidationSummary
        {
            public ValidationSummary(int doorCount, int gatewayVariantCount)
            {
                DoorCount = doorCount;
                GatewayVariantCount = gatewayVariantCount;
            }

            public int DoorCount { get; }
            public int GatewayVariantCount { get; }
        }
    }
}
