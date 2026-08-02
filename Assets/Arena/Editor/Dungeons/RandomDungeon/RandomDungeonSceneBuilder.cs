#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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
        private const string StagingScenePath =
            "Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.__staging.unity";
        private const string StagingDataKey = "random_dungeon_staging";

        private const string CameraTemplateScenePath =
            "Assets/Arena/Content/Scenes/OpenWorld/Great_Hall_Day.unity";
        private const string DungeonVolumeProfilePath =
            "Assets/Arena/Content/Settings/Rendering/OpenWorldProfiles/Arena_RandomDungeon_Profile.asset";
        private const string SeedEnvironmentVariable = "ARENA_RANDOM_DUNGEON_SEED";
        private const string AutoPublishEnvironmentVariable = "ARENA_AUTO_PUBLISH_SHARED_DATA";
        private const string GeneratedRootName = "Generated Dungeon";
        private const string GameplayCollisionLayerName = "GameplayCollision";
        private const string CollisionRevisionNamePrefix = "Collision Revision ";
        private static readonly string[] GeneratedDataSuffixes =
        {
            "doors.shared.json",
            "navsurfaces.shared.json",
            "traps.shared.json",
            "collision.shared.json",
            "query_collision.shared.json"
        };

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

        [MenuItem("Arena/Dungeons/Rebuild Random Dungeon (Specific Topology)", false, 120)]
        private static void RebuildSpecificTopology()
        {
            ScriptableWizard.DisplayWizard<TopologyWizard>(
                "Rebuild Random Dungeon On One Topology",
                "Rebuild");
        }

        /// <summary>Entry point used by command-line validation and CI.</summary>
        public static void RebuildRandomDungeonBatch()
        {
            RebuildWithSeed(CreateSeed());
        }

        internal static void RebuildWithSeed(int seed)
        {
            using LocalSpacetimeDbSharedDataPublisher.SharedDataTransaction transaction =
                LocalSpacetimeDbSharedDataPublisher.BeginSharedDataTransaction();
            CleanupStagingArtifacts();
            try
            {
                RebuildWithSeed(
                    seed,
                    StagingScenePath,
                    StagingDataKey,
                    addSceneToBuildSettings: false,
                    beforeModelImporterMutation: null,
                    stageRecorder: null);
                PromoteStagedProductionBuild();
                transaction.Complete();
            }
            catch
            {
                RestoreProductionSceneAfterFailedStagingBuild();
                throw;
            }
            finally
            {
                CleanupStagingArtifacts();
            }
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
            // This build owns a fresh staging scene and closes it wholesale on
            // failure, so thousands of per-object Undo records provide no
            // recovery value and make prefab-heavy generation substantially
            // slower. Direct interactive Dungeon Lab generation keeps Undo.
            using (DungeonLabGenerator.SuppressGenerationUndo())
            {
                DungeonLabGenerator.GenerateWithSeed(seed);
            }
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

            BakeDungeonRoot(
                destination,
                dungeonRoot,
                seed,
                destinationScenePath,
                dataKey,
                addSceneToBuildSettings,
                beforeModelImporterMutation,
                stageRecorder,
                ref stageStart);

            Debug.Log(
                $"[RandomDungeonSceneBuilder] Rebuilt {SceneName} with seed {seed}. " +
                "The generated floor at world origin is the shared player/minion spawn, and collision was exported for client and server.");
        }

        /// <summary>
        /// Everything a built dungeon root goes through on its way to a saved
        /// scene and an exported collision payload.
        /// </summary>
        /// <remarks>
        /// Extracted so a hand-built root — the layered-topology C1 episode — can
        /// travel the EXACT production path to the server rather than a parallel
        /// one. A live probe that exercised a second export path would be
        /// measuring the probe's own plumbing, not the generator's.
        /// </remarks>
        internal static void BakeDungeonRoot(
            Scene destination,
            GameObject dungeonRoot,
            int seed,
            string destinationScenePath,
            string dataKey,
            bool addSceneToBuildSettings,
            Action<string>? beforeModelImporterMutation,
            Action<string, double>? stageRecorder,
            ref long stageStart,
            bool exportInteractionManifests = true,
            bool exportNavigationSurfaces = true)
        {
            CenterDungeonSpawn(dungeonRoot);
            RecordValidationStage(stageRecorder, "centerDungeonSpawn", ref stageStart);
            // MEASURED 2026-07-31, and it cost a dead server: the module hard-
            // validates the dungeon door manifest as NON-EMPTY
            // (`world_interactions.rs:930`, inside a OnceLock that `game_tick`
            // touches every tick), so a root with no gateways exports an empty
            // manifest and every single tick panics. A caller baking geometry
            // that owns no doors or traps must keep the existing manifests
            // rather than replace them with empty ones.
            if (exportInteractionManifests)
            {
                // Generated authoring always belongs to RANDOM_DUNGEON. During
                // a transaction only the artifact filename changes; treating
                // the staging filename as a new logical world rejects every
                // authored door and trap before the build can be promoted.
                WorldInteractionManifestExporter.ExportActiveScene(DataKey, dataKey);
                RecordValidationStage(stageRecorder, "exportWorldInteractions", ref stageStart);
                // After CenterDungeonSpawn, so exported trap coordinates are final
                // world space — the same ordering the door manifest depends on.
                WorldTrapManifestExporter.ExportActiveScene(DataKey, dataKey);
                RecordValidationStage(stageRecorder, "exportWorldTraps", ref stageStart);
            }
            EnsureCollisionMeshesReadable(dungeonRoot, beforeModelImporterMutation);
            RecordValidationStage(stageRecorder, "normalizeCollisionMeshImporters", ref stageStart);
            MarkDungeonCollision(dungeonRoot);
            RecordValidationStage(stageRecorder, "markDungeonCollision", ref stageStart);
            CloneGameplayCameraRig(destination);
            CreateLighting();
            GameplayCollisionExporter.PreparedSharedCollisionBake collisionBake =
                GameplayCollisionExporter.PrepareSceneSharedCollisionBake(destination);
            string collisionRevision = collisionBake.Revision;
            RecordValidationStage(stageRecorder, "computeCollisionRevision", ref stageStart);
            CreateSceneMetadata(seed, collisionRevision);
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
            if (exportNavigationSurfaces)
            {
                DungeonLabGenerator.ExportLastNavigationSurfaces(dungeonRoot, dataKey);
                RecordValidationStage(stageRecorder, "exportNavigationSurfaces", ref stageStart);
            }
            GameplayCollisionExporter.ExportPreparedActiveSceneSharedCollisionData(
                dataKey,
                collisionBake);
            RecordValidationStage(stageRecorder, "exportSharedCollision", ref stageStart);
            AssetDatabase.SaveAssets();
            RecordValidationStage(stageRecorder, "saveAssetsAfterExport", ref stageStart);
        }

        internal const string DungeonRootName = GeneratedRootName;

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

        private static void CreateSceneMetadata(
            int seed,
            string collisionRevision)
        {
            GameObject metadata = new($"Random Dungeon Seed {seed}");
            metadata.transform.position = Vector3.zero;

            GameObject spawn = new("RandomDungeonSpawn");
            spawn.transform.SetParent(metadata.transform, worldPositionStays: false);
            spawn.transform.localPosition = Vector3.zero;
            spawn.transform.localRotation = Quaternion.identity;


            GameObject revision = new($"{CollisionRevisionNamePrefix}{collisionRevision}");
            revision.transform.SetParent(metadata.transform, worldPositionStays: false);
            revision.transform.localPosition = Vector3.zero;
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

                GameObject mainCamera = CloneIntoScene(sourceMainCamera, destination);
                CloneIntoScene(sourceFollowCamera, destination);
                ConfigureDungeonCameraRendering(mainCamera);
            }
            finally
            {
                EditorSceneManager.CloseScene(template, removeScene: true);
                SceneManager.SetActiveScene(destination);
            }
        }

        private static GameObject CloneIntoScene(GameObject source, Scene destination)
        {
            GameObject clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name;
            SceneManager.MoveGameObjectToScene(clone, destination);
            return clone;
        }

        private static void ConfigureDungeonCameraRendering(GameObject mainCamera)
        {
            VolumeProfile? profile =
                AssetDatabase.LoadAssetAtPath<VolumeProfile>(DungeonVolumeProfilePath);
            if (profile == null)
            {
                throw new InvalidOperationException(
                    $"Required random-dungeon Volume profile '{DungeonVolumeProfilePath}' is missing.");
            }

            Camera camera = mainCamera.GetComponent<Camera>()
                ?? throw new InvalidOperationException(
                    "The cloned random-dungeon MainCamera has no Camera component.");
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.04111782f, 0.073195f, 0.11320752f, 1f);
            camera.allowHDR = true;
            camera.allowMSAA = true;

            UniversalAdditionalCameraData cameraData =
                mainCamera.GetComponent<UniversalAdditionalCameraData>()
                ?? throw new InvalidOperationException(
                    "The cloned random-dungeon MainCamera has no URP camera data.");
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.None;

            Volume volume = mainCamera.GetComponent<Volume>() ?? mainCamera.AddComponent<Volume>();
            volume.enabled = true;
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.blendDistance = 0f;
            volume.weight = 1f;
            volume.sharedProfile = profile;
        }

        private static void CreateLighting()
        {
            GameObject lightObject = new("Directional Light");
            lightObject.transform.position = new Vector3(0f, 3f, 0f);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.77265036f, 0.9150943f, 0.9032317f);
            light.intensity = 0.25f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.278f;
            light.bounceIntensity = 0f;
            light.shadowAngle = 10f;

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.41295835f, 0.51094455f, 0.6037736f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.ambientEquatorColor = Color.black;
            RenderSettings.ambientGroundColor = Color.black;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.28635636f, 0.35730952f, 0.4245283f);
            RenderSettings.fogDensity = 0.01f;
            RenderSettings.fogStartDistance = 18f;
            RenderSettings.fogEndDistance = 39.6f;
        }

        [InitializeOnLoadMethod]
        private static void RepairStaleCollisionAfterScriptReload()
        {
            if (Application.isBatchMode ||
                string.Equals(
                    Environment.GetEnvironmentVariable(AutoPublishEnvironmentVariable),
                    "0",
                    StringComparison.Ordinal))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling ||
                    EditorApplication.isUpdating ||
                    EditorApplication.isPlayingOrWillChangePlaymode ||
                    IsCollisionRevisionCurrent(out _))
                {
                    return;
                }

                TryRepairCollisionFromSavedScene();
            };
        }

        internal static bool IsCollisionRevisionCurrent(out string failure)
        {
            string scenePath = ProjectAbsolutePath(ScenePath);
            if (!File.Exists(scenePath))
            {
                failure = $"scene '{ScenePath}' is missing";
                return false;
            }

            string? sceneRevision = File.ReadLines(scenePath)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("m_Name: ", StringComparison.Ordinal))
                .Select(line => line.Substring("m_Name: ".Length))
                .FirstOrDefault(name => name.StartsWith(CollisionRevisionNamePrefix, StringComparison.Ordinal))?
                .Substring(CollisionRevisionNamePrefix.Length);
            if (string.IsNullOrWhiteSpace(sceneRevision))
            {
                failure = "the saved scene has no collision revision marker";
                return false;
            }

            foreach (string suffix in new[] { "collision.shared.json", "query_collision.shared.json" })
            {
                string relativePath = BundledWorldDataPath(DataKey, suffix);
                string absolutePath = ProjectAbsolutePath(relativePath);
                if (!File.Exists(absolutePath))
                {
                    failure = $"'{relativePath}' is missing";
                    return false;
                }

                CollisionRevisionLayout? layout =
                    JsonUtility.FromJson<CollisionRevisionLayout>(File.ReadAllText(absolutePath));
                if (layout == null ||
                    !string.Equals(layout.source_revision, sceneRevision, StringComparison.Ordinal))
                {
                    failure = $"'{relativePath}' does not match scene revision {sceneRevision}";
                    return false;
                }
            }

            failure = string.Empty;
            return true;
        }

        [MenuItem("Arena/Dungeons/Repair Random Dungeon Collision From Saved Scene", false, 190)]
        private static void RepairCollisionFromSavedSceneMenu()
        {
            TryRepairCollisionFromSavedScene();
        }

        internal static bool TryRepairCollisionFromSavedScene()
        {
            if (Application.isBatchMode ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError(
                    "[RandomDungeonSceneBuilder] Cannot repair saved collision while " +
                    "Unity is compiling, importing, or changing Play mode.");
                return false;
            }

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene dungeonScene = SceneManager.GetSceneByPath(ScenePath);
            bool openedForRepair = !dungeonScene.IsValid() || !dungeonScene.isLoaded;
            using LocalSpacetimeDbSharedDataPublisher.SharedDataTransaction transaction =
                LocalSpacetimeDbSharedDataPublisher.BeginSharedDataTransaction();
            try
            {
                if (openedForRepair)
                    dungeonScene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                if (!dungeonScene.IsValid() || !dungeonScene.isLoaded ||
                    !SceneManager.SetActiveScene(dungeonScene))
                {
                    throw new InvalidOperationException(
                        $"Unable to load '{ScenePath}' for collision recovery.");
                }

                GameObject dungeonRoot = dungeonScene.GetRootGameObjects()
                    .FirstOrDefault(root => string.Equals(root.name, GeneratedRootName, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"Saved scene '{ScenePath}' has no '{GeneratedRootName}' root.");
                EnsureCollisionMeshesReadable(dungeonRoot, beforeModelImporterMutation: null);
                MarkDungeonCollision(dungeonRoot);
                GameplayCollisionExporter.PreparedSharedCollisionBake collisionBake =
                    GameplayCollisionExporter.PrepareSceneSharedCollisionBake(dungeonScene);
                string revision = collisionBake.Revision;
                SetCollisionRevisionMetadata(dungeonScene, revision);
                GameplayCollisionExporter.ExportPreparedActiveSceneSharedCollisionData(
                    DataKey,
                    collisionBake);
                EditorSceneManager.MarkSceneDirty(dungeonScene);
                if (!EditorSceneManager.SaveScene(dungeonScene, ScenePath))
                    throw new InvalidOperationException($"Failed to save repaired scene '{ScenePath}'.");
                AssetDatabase.SaveAssets();
                transaction.Complete();
                Debug.Log(
                    "[RandomDungeonSceneBuilder] Recovered RandomDungeon collision " +
                    $"from the saved scene at revision {revision}; local publish queued.");
                return true;
            }
            catch (Exception error)
            {
                Debug.LogError(
                    "[RandomDungeonSceneBuilder] Collision recovery failed: " + error);
                return false;
            }
            finally
            {
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
                if (openedForRepair && dungeonScene.IsValid() && dungeonScene.isLoaded)
                    EditorSceneManager.CloseScene(dungeonScene, removeScene: true);
            }
        }

        private static void SetCollisionRevisionMetadata(Scene scene, string revision)
        {
            GameObject metadata = scene.GetRootGameObjects()
                .FirstOrDefault(root => root.name.StartsWith("Random Dungeon Seed ", StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "Saved RandomDungeon scene has no seed metadata root.");
            foreach (Transform child in metadata.transform.Cast<Transform>()
                         .Where(child => child.name.StartsWith(CollisionRevisionNamePrefix, StringComparison.Ordinal))
                         .ToArray())
            {
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            GameObject marker = new($"{CollisionRevisionNamePrefix}{revision}");
            marker.transform.SetParent(metadata.transform, worldPositionStays: false);
            marker.transform.localPosition = Vector3.zero;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void PromoteStagedProductionBuild()
        {
            Scene stagedScene = SceneManager.GetSceneByPath(StagingScenePath);
            if (!stagedScene.IsValid() || !stagedScene.isLoaded)
                throw new InvalidOperationException("The completed staging dungeon scene is not loaded.");

            string[] productionPaths = ProductionArtifactPaths().ToArray();
            var snapshot = new ProductionFileSnapshot(productionPaths);
            try
            {
                foreach (string suffix in GeneratedDataSuffixes)
                {
                    CopyRequiredProjectFile(
                        ServerWorldDataPath(StagingDataKey, suffix),
                        ServerWorldDataPath(DataKey, suffix));
                    CopyRequiredProjectFile(
                        BundledWorldDataPath(StagingDataKey, suffix),
                        BundledWorldDataPath(DataKey, suffix));
                }

                EditorSceneManager.MarkSceneDirty(stagedScene);
                if (!EditorSceneManager.SaveScene(stagedScene, ScenePath))
                    throw new InvalidOperationException($"Failed to promote staged scene to '{ScenePath}'.");
                AddSceneToBuildSettings(ScenePath);
                AssetDatabase.Refresh();
                AssetDatabase.SaveAssets();
            }
            catch
            {
                snapshot.Restore();
                AssetDatabase.Refresh();
                throw;
            }
        }

        private static void RestoreProductionSceneAfterFailedStagingBuild()
        {
            if (!File.Exists(ProjectAbsolutePath(ScenePath)))
                return;

            try
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            catch (Exception error)
            {
                Debug.LogError(
                    $"[RandomDungeonSceneBuilder] Failed to reopen last good '{ScenePath}': {error}");
            }
        }

        private static void CleanupStagingArtifacts()
        {
            Scene stagedScene = SceneManager.GetSceneByPath(StagingScenePath);
            if (stagedScene.IsValid() && stagedScene.isLoaded)
                EditorSceneManager.CloseScene(stagedScene, removeScene: true);

            AssetDatabase.DeleteAsset(StagingScenePath);
            foreach (string suffix in GeneratedDataSuffixes)
            {
                AssetDatabase.DeleteAsset(BundledWorldDataPath(StagingDataKey, suffix));
                DeleteProjectFileIfPresent(ServerWorldDataPath(StagingDataKey, suffix));
            }
            AssetDatabase.Refresh();
        }

        private static IEnumerable<string> ProductionArtifactPaths()
        {
            yield return ScenePath;
            foreach (string suffix in GeneratedDataSuffixes)
            {
                yield return ServerWorldDataPath(DataKey, suffix);
                yield return BundledWorldDataPath(DataKey, suffix);
            }
        }

        private static string ServerWorldDataPath(string dataKey, string suffix)
            => $"server/src/world_data/{dataKey}.{suffix}";

        private static string BundledWorldDataPath(string dataKey, string suffix)
            => $"Assets/Arena/Resources/SharedData/Worlds/{dataKey}.{suffix}";

        private static string ProjectAbsolutePath(string relativePath)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Unable to resolve Unity project root.");
            return Path.Combine(root, relativePath);
        }

        private static void CopyRequiredProjectFile(string sourceRelativePath, string destinationRelativePath)
        {
            string sourcePath = ProjectAbsolutePath(sourceRelativePath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Staged artifact '{sourceRelativePath}' is missing.", sourcePath);

            string destinationPath = ProjectAbsolutePath(destinationRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (FilesHaveIdenticalContents(sourcePath, destinationPath))
                return;

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static bool FilesHaveIdenticalContents(string firstPath, string secondPath)
        {
            if (!File.Exists(firstPath) || !File.Exists(secondPath))
                return false;

            var firstInfo = new FileInfo(firstPath);
            var secondInfo = new FileInfo(secondPath);
            if (firstInfo.Length != secondInfo.Length)
                return false;

            const int bufferSize = 128 * 1024;
            byte[] firstBuffer = new byte[bufferSize];
            byte[] secondBuffer = new byte[bufferSize];
            using FileStream first = File.OpenRead(firstPath);
            using FileStream second = File.OpenRead(secondPath);
            while (true)
            {
                int firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
                int secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
                if (firstRead != secondRead)
                    return false;
                if (firstRead == 0)
                    return true;

                for (int i = 0; i < firstRead; i++)
                {
                    if (firstBuffer[i] != secondBuffer[i])
                        return false;
                }
            }
        }

        private static void DeleteProjectFileIfPresent(string relativePath)
        {
            string path = ProjectAbsolutePath(relativePath);
            if (File.Exists(path))
                File.Delete(path);
        }

        [Serializable]
        private sealed class CollisionRevisionLayout
        {
            public string source_revision = string.Empty;
        }

        private sealed class ProductionFileSnapshot
        {
            private readonly Dictionary<string, byte[]?> files = new(StringComparer.Ordinal);

            internal ProductionFileSnapshot(IEnumerable<string> relativePaths)
            {
                foreach (string relativePath in relativePaths)
                {
                    string path = ProjectAbsolutePath(relativePath);
                    files.Add(relativePath, File.Exists(path) ? File.ReadAllBytes(path) : null);
                }
            }

            internal void Restore()
            {
                foreach ((string relativePath, byte[]? contents) in files)
                {
                    string path = ProjectAbsolutePath(relativePath);
                    if (contents == null)
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, contents);
                }
            }
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

        /// <summary>
        /// Rebuild on ONE named non-deprecated route topology, whatever its selection weight.
        /// </summary>
        /// <remarks>
        /// A weight-0 authoring draft is invisible to `SelectRouteTopologyId`,
        /// so this remains its preview path. Deprecated graphs are blocked.
        /// Headless equivalent: set `ARENA_DUNGEON_TOPOLOGY` and run the ordinary
        /// rebuild.
        /// </remarks>
        private sealed class TopologyWizard : ScriptableWizard
        {
            public string topologyId = string.Empty;
            public int seed;

            private void OnWizardCreate()
            {
                using (System.IDisposable scope =
                       DungeonLabGenerator.ForceRouteTopology(topologyId))
                {
                    RebuildWithSeed(seed);
                }
            }
        }
    }
}
