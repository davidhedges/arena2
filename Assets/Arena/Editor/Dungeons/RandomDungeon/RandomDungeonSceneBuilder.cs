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
using Stopwatch = System.Diagnostics.Stopwatch;

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
            RebuildWithSeed(
                seed,
                ScenePath,
                DataKey,
                addSceneToBuildSettings: true,
                beforeModelImporterMutation: null,
                stageRecorder: null);
        }

        // Phase 7 validation uses the exact production rebuild core with unique
        // temporary destinations so evidence cannot overwrite the baked scene
        // or its checked-in client/server collision payloads.
        internal static void RebuildWithSeedForValidation(
            int seed,
            string destinationScenePath,
            string dataKey,
            Action<string>? beforeModelImporterMutation = null,
            Action<string, double>? stageRecorder = null)
        {
            if (string.IsNullOrWhiteSpace(destinationScenePath) ||
                !destinationScenePath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !destinationScenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Validation destination must be a Unity scene asset path.",
                    nameof(destinationScenePath));
            }

            if (string.IsNullOrWhiteSpace(dataKey))
                throw new ArgumentException("Validation collision data key is required.", nameof(dataKey));
            if (string.Equals(destinationScenePath, ScenePath, StringComparison.Ordinal) ||
                string.Equals(dataKey, DataKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Phase 7 validation must not target the checked-in production scene or collision key.");
            }

            RebuildWithSeed(
                seed,
                destinationScenePath,
                dataKey,
                addSceneToBuildSettings: false,
                beforeModelImporterMutation,
                stageRecorder);
        }

        private static void RebuildWithSeed(
            int seed,
            string destinationScenePath,
            string dataKey,
            bool addSceneToBuildSettings,
            Action<string>? beforeModelImporterMutation,
            Action<string, double>? stageRecorder)
        {
            long stageStart = Stopwatch.GetTimestamp();
            EnsureDestinationFolder();

            Scene destination = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            RecordValidationStage(stageRecorder, "newScene", ref stageStart);
            DungeonLabGenerator.GenerateWithSeed(seed);
            RecordValidationStage(stageRecorder, "planAndRender", ref stageStart);
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
            RecordValidationStage(stageRecorder, "resolveGeneratedRoot", ref stageStart);

            CenterDungeonSpawn(dungeonRoot);
            RecordValidationStage(stageRecorder, "centerDungeonSpawn", ref stageStart);
            WorldInteractionManifestExporter.ExportActiveScene(dataKey);
            RecordValidationStage(stageRecorder, "exportWorldInteractions", ref stageStart);
            // After CenterDungeonSpawn, so exported trap coordinates are final
            // world space — the same ordering the door manifest depends on.
            WorldTrapManifestExporter.ExportActiveScene(dataKey);
            RecordValidationStage(stageRecorder, "exportWorldTraps", ref stageStart);
            EnsureCollisionMeshesReadable(dungeonRoot, beforeModelImporterMutation);
            RecordValidationStage(stageRecorder, "normalizeCollisionMeshImporters", ref stageStart);
            MarkDungeonCollision(dungeonRoot);
            RecordValidationStage(stageRecorder, "markDungeonCollision", ref stageStart);
            CreateSceneMetadata(seed);
            CloneGameplayCameraRig(destination);
            CreateLighting();
            RecordValidationStage(stageRecorder, "sceneMetadataCameraAndLighting", ref stageStart);

            EditorSceneManager.MarkSceneDirty(destination);
            if (!EditorSceneManager.SaveScene(destination, destinationScenePath))
            {
                throw new InvalidOperationException(
                    $"Failed to save generated dungeon scene '{destinationScenePath}'.");
            }
            RecordValidationStage(stageRecorder, "saveSceneBeforeExport", ref stageStart);

            if (addSceneToBuildSettings)
                AddSceneToBuildSettings(destinationScenePath);
            SceneManager.SetActiveScene(destination);
            RecordValidationStage(stageRecorder, "activateAndRegisterScene", ref stageStart);
            GameplayCollisionExporter.ExportActiveSceneSharedCollisionData(dataKey);
            RecordValidationStage(stageRecorder, "exportSharedCollision", ref stageStart);
            EditorSceneManager.SaveScene(destination, destinationScenePath);
            AssetDatabase.SaveAssets();
            RecordValidationStage(stageRecorder, "saveSceneAndAssetsAfterExport", ref stageStart);

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

        // Focused validation reuses the exact production preparation steps
        // without creating a second collision path.
        internal static void PrepareGeneratedDungeonCollisionForValidation(
            GameObject dungeonRoot,
            Action<string>? beforeModelImporterMutation = null)
        {
            if (dungeonRoot == null)
                throw new ArgumentNullException(nameof(dungeonRoot));

            EnsureCollisionMeshesReadable(dungeonRoot, beforeModelImporterMutation);
            MarkDungeonCollision(dungeonRoot);
        }

        private static void EnsureCollisionMeshesReadable(
            GameObject dungeonRoot,
            Action<string>? beforeModelImporterMutation)
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

                beforeModelImporterMutation?.Invoke(modelPath);
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

        private static void AddSceneToBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            int existingIndex = Array.FindIndex(
                scenes,
                scene => string.Equals(scene.path, scenePath, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                if (!scenes[existingIndex].enabled)
                    scenes[existingIndex] = new EditorBuildSettingsScene(scenePath, enabled: true);
            }
            else
            {
                Array.Resize(ref scenes, scenes.Length + 1);
                scenes[^1] = new EditorBuildSettingsScene(scenePath, enabled: true);
            }

            EditorBuildSettings.scenes = scenes;
        }

        private static void RecordValidationStage(
            Action<string, double>? stageRecorder,
            string stage,
            ref long stageStart)
        {
            long end = Stopwatch.GetTimestamp();
            if (stageRecorder != null)
            {
                stageRecorder(
                    stage,
                    (end - stageStart) * 1000d / Stopwatch.Frequency);
            }

            stageStart = end;
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
