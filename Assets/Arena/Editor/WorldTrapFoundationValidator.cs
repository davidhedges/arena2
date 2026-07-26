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
    /// <summary>
    /// Contract validation for the checked-in trap foundation: paired exports,
    /// resolvable profiles, template/production component contracts, and the
    /// no-collider rule.
    ///
    /// Spatial coverage questions — kind mix, corridor/room split, distance to
    /// spawn, whether every hazard sample stays over floor — are the audit
    /// script's job (<c>ops/dungeon-trap-audit.py</c>), which reads the built
    /// scene and needs no Unity.
    /// </summary>
    public static class WorldTrapFoundationValidator
    {
        private const string RandomDungeonScenePath =
            "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity";
        private const string ClientTrapManifestPath =
            "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.traps.shared.json";
        private const string ServerTrapManifestPath =
            "server/src/world_data/random_dungeon.traps.shared.json";
        private const string ClientTrapProfileManifestPath =
            "Assets/Arena/Resources/SharedData/WorldInteractions/world_trap_profiles.shared.json";
        private const string ServerTrapProfileManifestPath =
            "server/src/world_data/world_trap_profiles.shared.json";
        private const string ClientDoorManifestPath =
            "Assets/Arena/Resources/SharedData/Worlds/random_dungeon.doors.shared.json";

        /// <summary>
        /// The placement pass excludes a gateway socket cell, the cell it opens
        /// onto, and every orthogonal neighbour of both, so the nearest legal
        /// trap centre is a diagonal cell ~4.47 u from the gateway anchor.
        /// </summary>
        private const float MinimumGatewayClearance = 4f;

        /// <summary>
        /// The spawn floor sits at the world origin after recentering. The
        /// profile's clearance is tunable, so this is only the hard floor below
        /// which the arrival room would be trapped.
        /// </summary>
        private const float MinimumSpawnClearance = 8f;

        [MenuItem("Arena/Interaction/Validate Checked-In Traps", false, 12)]
        private static void ValidateFromMenu()
        {
            ValidationSummary summary = ValidateCheckedInTraps();
            EditorUtility.DisplayDialog(
                "World Trap Validation",
                $"Validated {summary.TrapCount} production traps, "
                + $"{summary.ProfileCount} trap profiles, and "
                + $"{summary.VariantCount} Arena trap variants.",
                "OK");
        }

        public static ValidationSummary ValidateCheckedInTraps()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            JObject trapManifest = JObject.Parse(ReadIdenticalPair(
                projectRoot,
                ClientTrapManifestPath,
                ServerTrapManifestPath));
            JObject profileManifest = JObject.Parse(ReadIdenticalPair(
                projectRoot,
                ClientTrapProfileManifestPath,
                ServerTrapProfileManifestPath));
            RequireSchemaVersion(trapManifest, 1, "trap manifest");
            RequireSchemaVersion(profileManifest, 1, "trap profile manifest");

            HashSet<string> profileIds = ReadRequiredIds(
                profileManifest["profiles"] as JArray,
                "profile_id",
                "trap profile manifest");
            if (profileIds.Count == 0)
                throw new InvalidOperationException("Trap profile manifest contains no profiles.");

            var trapRows = trapManifest["traps"] as JArray
                ?? throw new InvalidOperationException("Trap manifest has no traps array.");
            HashSet<string> manifestTrapIds = ReadRequiredIds(
                trapRows,
                "trap_definition_id",
                "trap manifest");
            foreach (JToken row in trapRows)
            {
                string profileId = row["trap_profile_id"]?.Value<string>() ?? string.Empty;
                if (!profileIds.Contains(profileId))
                {
                    throw new InvalidOperationException(
                        $"Trap '{row["trap_definition_id"]}' references unknown profile '{profileId}'.");
                }
            }

            ValidateTrapClearances(projectRoot, trapRows);
            int variantCount = ValidateTrapVariants(profileIds);
            ValidateProductionScene(manifestTrapIds);
            return new ValidationSummary(manifestTrapIds.Count, profileIds.Count, variantCount);
        }

        private static void ValidateTrapClearances(string projectRoot, JArray trapRows)
        {
            var gatewayAnchors = new List<Vector2>();
            string doorPath = Path.Combine(projectRoot, ClientDoorManifestPath);
            if (File.Exists(doorPath))
            {
                foreach (JToken door in JObject.Parse(File.ReadAllText(doorPath))["doors"] as JArray
                                        ?? new JArray())
                {
                    JToken? anchor = door["interaction_anchor"];
                    if (anchor == null)
                        continue;

                    gatewayAnchors.Add(new Vector2(
                        anchor["x"]?.Value<float>() ?? 0f,
                        anchor["z"]?.Value<float>() ?? 0f));
                }
            }

            foreach (JToken row in trapRows)
            {
                string id = row["trap_definition_id"]?.Value<string>() ?? "<unnamed>";
                JToken origin = row["origin"]
                    ?? throw new InvalidOperationException($"Trap '{id}' has no origin.");
                float x = origin["x"]?.Value<float>() ?? float.NaN;
                float y = origin["y"]?.Value<float>() ?? float.NaN;
                float z = origin["z"]?.Value<float>() ?? float.NaN;
                float yaw = row["yaw_degrees"]?.Value<float>() ?? float.NaN;
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(yaw))
                    throw new InvalidOperationException($"Trap '{id}' has non-finite placement.");

                var horizontal = new Vector2(x, z);
                if (horizontal.magnitude < MinimumSpawnClearance)
                {
                    throw new InvalidOperationException(
                        $"Trap '{id}' is {horizontal.magnitude:0.##} u from the spawn floor; "
                        + $"traps must stay at least {MinimumSpawnClearance} u clear.");
                }

                foreach (Vector2 anchor in gatewayAnchors)
                {
                    float distance = Vector2.Distance(horizontal, anchor);
                    if (distance < MinimumGatewayClearance)
                    {
                        throw new InvalidOperationException(
                            $"Trap '{id}' is {distance:0.##} u from a gateway; a chokepoint with "
                            + "nowhere to dodge must not carry a trap.");
                    }
                }
            }
        }

        private static int ValidateTrapVariants(ISet<string> profileIds)
        {
            IReadOnlyList<string> variantNames = TrapPrefabBuilder.VariantNames;
            foreach (string variantName in variantNames)
            {
                string path = $"{TrapPrefabBuilder.DestinationRoot}/{variantName}.prefab";
                GameObject? prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    throw new InvalidOperationException($"Missing Arena trap variant: {path}");

                TrapAuthoring? authoring = prefab.GetComponent<TrapAuthoring>();
                TrapPresenter? presenter = prefab.GetComponent<TrapPresenter>();
                var violations = new List<string>();
                if (authoring == null)
                    violations.Add("missing TrapAuthoring");
                else
                {
                    if (!authoring.TemplateOnly)
                        violations.Add("TemplateOnly=false");
                    if (authoring.ProductionEnabled)
                        violations.Add("ProductionEnabled=true");
                    if (authoring.Profile == null || !profileIds.Contains(authoring.TrapProfileId))
                        violations.Add("profile is missing from the shared catalog");
                }
                if (presenter == null)
                    violations.Add("missing TrapPresenter (check missing script reference)");
                if (violations.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Trap variant has an invalid template/component contract: {path}. "
                        + string.Join("; ", violations));
                }
                ValidateTrapGeometry(prefab, variantName);
            }
            return variantNames.Count;
        }

        private static void ValidateProductionScene(ISet<string> manifestTrapIds)
        {
            Scene scene = SceneManager.GetSceneByPath(RandomDungeonScenePath);
            bool openedForValidation = !scene.IsValid() || !scene.isLoaded;
            if (openedForValidation)
                scene = EditorSceneManager.OpenScene(RandomDungeonScenePath, OpenSceneMode.Additive);

            try
            {
                TrapAuthoring[] traps = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<TrapAuthoring>(true))
                    .Where(trap => !trap.TemplateOnly)
                    .ToArray();
                var sceneIds = new HashSet<string>(
                    traps.Select(trap => trap.TrapDefinitionId),
                    StringComparer.Ordinal);
                if (!sceneIds.SetEquals(manifestTrapIds))
                {
                    throw new InvalidOperationException(
                        "Production scene trap IDs do not match the paired trap manifest.");
                }

                foreach (TrapAuthoring trap in traps)
                {
                    if (!trap.ProductionEnabled
                        || trap.GetComponent<TrapPresenter>() == null
                        || trap.Profile == null)
                    {
                        throw new InvalidOperationException(
                            $"Production trap '{trap.TrapDefinitionId}' is not runtime-enabled.");
                    }
                    ValidateTrapGeometry(trap.gameObject, trap.TrapDefinitionId);
                }
            }
            finally
            {
                if (openedForValidation && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        private static void ValidateTrapGeometry(GameObject root, string label)
        {
            // The collision contract: query raycasts test authored query
            // geometry only, and a trap authors none. A collider here would
            // silently join the immutable bake.
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Trap '{label}' carries {colliders.Length} collider(s); traps must never contribute collision.");
            }
            if (root.GetComponentsInChildren<Animator>(true).Length == 0)
                throw new InvalidOperationException($"Trap '{label}' has no animator to scrub.");

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (GameObjectUtility.GetStaticEditorFlags(child.gameObject) != 0)
                    throw new InvalidOperationException($"Animated trap part '{child.name}' is still marked static.");
            }
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
                    $"Missing paired trap export: '{clientPath}' or '{serverPath}'.");
            }

            string client = File.ReadAllText(clientAbsolute);
            string server = File.ReadAllText(serverAbsolute);
            if (!string.Equals(client, server, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Paired trap exports differ: '{clientPath}' and '{serverPath}'.");
            }
            return client;
        }

        private static void RequireSchemaVersion(JObject manifest, int expected, string label)
        {
            int actual = manifest["schema_version"]?.Value<int>() ?? 0;
            if (actual != expected)
                throw new InvalidOperationException($"{label} schema is {actual}; expected {expected}.");
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
                string id = row[propertyName]?.Value<string>()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"{label} contains an empty ID.");
                if (!ids.Add(id))
                    throw new InvalidOperationException($"{label} contains duplicate ID '{id}'.");
            }
            return ids;
        }

        public readonly struct ValidationSummary
        {
            public ValidationSummary(int trapCount, int profileCount, int variantCount)
            {
                TrapCount = trapCount;
                ProfileCount = profileCount;
                VariantCount = variantCount;
            }

            public int TrapCount { get; }
            public int ProfileCount { get; }
            public int VariantCount { get; }
        }
    }
}
