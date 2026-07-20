#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DungeonLab.Editor
{
    /// <summary>
    /// Bridges the imported Dungeon Lab authoring tool into Arena's authored
    /// open-world workflow. A rebuild produces one deterministic scene and the
    /// matching client/server collision payloads from the same seed.
    /// </summary>
    public static class RandomDungeonSceneBuilder
    {
        internal const string SceneName = "RandomDungeon";
        internal const string ScenePath = "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity";
        internal const string DataKey = "random_dungeon";

        private const string CameraTemplateScenePath =
            "Assets/Arena/Content/Scenes/OpenWorld/Great_Hall_Day.unity";
        private const string SeedEnvironmentVariable = "ARENA_RANDOM_DUNGEON_SEED";
        private const string GeneratedRootName = "Generated Dungeon";
        private const string GameplayCollisionLayerName = "GameplayCollision";

        [MenuItem("Arena/Dungeons/Rebuild Random Dungeon", false, 100)]
        private static void RebuildFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            RebuildWithSeed(CreateSeed());
        }

        [MenuItem("Arena/Dungeons/Rebuild Random Dungeon (Specific Seed)", false, 110)]
        private static void RebuildSpecificSeed()
        {
            ScriptableWizard.DisplayWizard<SeedWizard>(
                "Rebuild Random Dungeon",
                "Rebuild");
        }

        /// <summary>Entry point used by command-line validation and CI.</summary>
        public static void RebuildRandomDungeonBatch()
        {
            RebuildWithSeed(CreateSeed());
        }

        internal static void RebuildWithSeed(int seed)
        {
            EnsureDestinationFolder();

            Scene destination = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            DungeonLabGenerator.GenerateWithSeed(seed);
            GameObject? dungeonRoot = destination.GetRootGameObjects()
                .FirstOrDefault(root => string.Equals(
                    root.name,
                    GeneratedRootName,
                    StringComparison.Ordinal));
            if (dungeonRoot == null)
            {
                throw new InvalidOperationException(
                    $"Dungeon generation for seed {seed} did not create '{GeneratedRootName}'.");
            }

            CenterDungeonSpawn(dungeonRoot);
            EnsureCollisionMeshesReadable(dungeonRoot);
            MarkDungeonCollision(dungeonRoot);
            CreateSceneMetadata(seed);
            CloneGameplayCameraRig(destination);
            CreateLighting();

            EditorSceneManager.MarkSceneDirty(destination);
            if (!EditorSceneManager.SaveScene(destination, ScenePath))
                throw new InvalidOperationException($"Failed to save generated dungeon scene '{ScenePath}'.");

            AddSceneToBuildSettings();
            SceneManager.SetActiveScene(destination);
            GameplayCollisionExporter.ExportActiveSceneSharedCollisionData(DataKey);
            EditorSceneManager.SaveScene(destination, ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[RandomDungeonSceneBuilder] Rebuilt {SceneName} with seed {seed}. " +
                "The generated floor at world origin is the shared player/minion spawn, and collision was exported for client and server.");
        }

        private static void CenterDungeonSpawn(GameObject dungeonRoot)
        {
            Transform? floorRoot = dungeonRoot.transform.Find("Floors");
            if (floorRoot == null)
                throw new InvalidOperationException("Generated dungeon has no Floors hierarchy.");

            Renderer? spawnFloor = floorRoot
                .GetComponentsInChildren<Renderer>(includeInactive: false)
                .Where(renderer => renderer.bounds.size.x > 0.5f && renderer.bounds.size.z > 0.5f)
                .OrderBy(renderer =>
                {
                    Vector3 center = renderer.bounds.center;
                    return center.x * center.x + center.z * center.z;
                })
                .ThenBy(renderer => renderer.bounds.max.y)
                .FirstOrDefault();
            if (spawnFloor == null)
                throw new InvalidOperationException("Generated dungeon has no renderable floor suitable for spawning.");

            Bounds floorBounds = spawnFloor.bounds;
            Vector3 surfaceCenter = new(floorBounds.center.x, floorBounds.max.y, floorBounds.center.z);
            dungeonRoot.transform.position -= surfaceCenter;
            EditorUtility.SetDirty(dungeonRoot.transform);
        }

        private static void MarkDungeonCollision(GameObject dungeonRoot)
        {
            int collisionLayer = LayerMask.NameToLayer(GameplayCollisionLayerName);
            if (collisionLayer < 0)
                throw new InvalidOperationException($"Required layer '{GameplayCollisionLayerName}' is missing.");

            int colliderCount = 0;
            foreach (Collider collider in dungeonRoot.GetComponentsInChildren<Collider>(includeInactive: false))
            {
                if (collider == null || !collider.enabled || collider.isTrigger)
                    continue;

                collider.gameObject.layer = collisionLayer;
                EditorUtility.SetDirty(collider.gameObject);
                colliderCount++;
            }

            if (colliderCount == 0)
                throw new InvalidOperationException("Generated dungeon contains no enabled collision geometry.");
        }

        private static void EnsureCollisionMeshesReadable(GameObject dungeonRoot)
        {
            string[] modelPaths = dungeonRoot
                .GetComponentsInChildren<MeshCollider>(includeInactive: false)
                .Where(collider => collider != null && collider.enabled && !collider.isTrigger)
                .Select(collider => collider.sharedMesh)
                .Where(mesh => mesh != null && !mesh.isReadable)
                .Select(AssetDatabase.GetAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            int updatedImporterCount = 0;
            foreach (string modelPath in modelPaths)
            {
                if (AssetImporter.GetAtPath(modelPath) is not ModelImporter importer || importer.isReadable)
                    continue;

                importer.isReadable = true;
                importer.SaveAndReimport();
                updatedImporterCount++;
            }

            List<string> unreadablePaths = dungeonRoot
                .GetComponentsInChildren<MeshCollider>(includeInactive: false)
                .Where(collider =>
                    collider != null &&
                    collider.enabled &&
                    !collider.isTrigger &&
                    collider.sharedMesh != null &&
                    !collider.sharedMesh.isReadable)
                .Select(collider => AssetDatabase.GetAssetPath(collider.sharedMesh))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (unreadablePaths.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Generated dungeon still has {unreadablePaths.Count} unreadable collision mesh asset(s): " +
                    string.Join(", ", unreadablePaths.Take(8)));
            }

            if (updatedImporterCount > 0)
            {
                Debug.Log(
                    $"[RandomDungeonSceneBuilder] Enabled Read/Write on {updatedImporterCount} " +
                    "dungeon model importer(s) required by the shared collision bake.");
            }
        }

        private static void CreateSceneMetadata(int seed)
        {
            GameObject metadata = new($"Random Dungeon Seed {seed}");
            metadata.transform.position = Vector3.zero;

            GameObject spawn = new("RandomDungeonSpawn");
            spawn.transform.SetParent(metadata.transform, worldPositionStays: false);
            spawn.transform.localPosition = Vector3.zero;
            spawn.transform.localRotation = Quaternion.identity;
        }

        private static void CloneGameplayCameraRig(Scene destination)
        {
            Scene template = EditorSceneManager.OpenScene(CameraTemplateScenePath, OpenSceneMode.Additive);
            try
            {
                GameObject? sourceMainCamera = template
                    .GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Camera>(includeInactive: true))
                    .Where(camera => camera.CompareTag("MainCamera"))
                    .Select(camera => camera.gameObject)
                    .FirstOrDefault();
                GameObject? sourceFollowCamera = template
                    .GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(includeInactive: true))
                    .Where(transform => string.Equals(
                        transform.name,
                        "PlayerFollowCamera",
                        StringComparison.Ordinal))
                    .Select(transform => transform.gameObject)
                    .FirstOrDefault();

                if (sourceMainCamera == null || sourceFollowCamera == null)
                {
                    throw new InvalidOperationException(
                        $"Camera template '{CameraTemplateScenePath}' does not contain the Arena open-world camera rig.");
                }

                CloneIntoScene(sourceMainCamera, destination);
                CloneIntoScene(sourceFollowCamera, destination);
            }
            finally
            {
                EditorSceneManager.CloseScene(template, removeScene: true);
                SceneManager.SetActiveScene(destination);
            }
        }

        private static void CloneIntoScene(GameObject source, Scene destination)
        {
            GameObject clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name;
            SceneManager.MoveGameObjectToScene(clone, destination);
        }

        private static void CreateLighting()
        {
            GameObject lightObject = new("Directional Light");
            lightObject.transform.rotation = Quaternion.Euler(42f, -32f, 0f);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.78f, 0.58f);
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.12f, 0.13f, 0.18f);
            RenderSettings.ambientEquatorColor = new Color(0.07f, 0.065f, 0.08f);
            RenderSettings.ambientGroundColor = new Color(0.025f, 0.02f, 0.025f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.025f, 0.028f, 0.04f);
            RenderSettings.fogDensity = 0.008f;
        }

        private static void EnsureDestinationFolder()
        {
            const string folder = "Assets/Arena/Content/Scenes/OpenWorld";
            if (!AssetDatabase.IsValidFolder(folder))
                throw new InvalidOperationException($"Required Arena scene folder '{folder}' is missing.");
        }

        private static void AddSceneToBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            int existingIndex = Array.FindIndex(
                scenes,
                scene => string.Equals(scene.path, ScenePath, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                if (!scenes[existingIndex].enabled)
                    scenes[existingIndex] = new EditorBuildSettingsScene(ScenePath, enabled: true);
            }
            else
            {
                Array.Resize(ref scenes, scenes.Length + 1);
                scenes[^1] = new EditorBuildSettingsScene(ScenePath, enabled: true);
            }

            EditorBuildSettings.scenes = scenes;
        }

        private static int CreateSeed()
        {
            string? configured = Environment.GetEnvironmentVariable(SeedEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && int.TryParse(configured, out int parsed))
                return parsed;

            return Guid.NewGuid().GetHashCode();
        }

        private sealed class SeedWizard : ScriptableWizard
        {
            public int seed;

            private void OnWizardCreate()
            {
                RebuildWithSeed(seed);
            }
        }
    }
}
