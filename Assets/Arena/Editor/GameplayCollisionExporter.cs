#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Editor
{
    public static class GameplayCollisionExporter
    {
        private const string GameplayCollisionLayer = "GameplayCollision";
        private const string GameplayQueryCollisionLayer = "GameplayQueryCollision";
        private const string GameplayCollisionChildName = "ArenaGameplayCollision";
        private const string GameplayQueryCollisionChildName = "ArenaGameplayQueryCollision";
        private const float TransformCompatibilityScaleEpsilon = 0.001f;
        private const float TransformCompatibilityBasisEpsilon = 0.001f;
        private const int MaxExportDiagnosticsToLog = 25;
        private const int MaxQueryMeshTrianglesPerCollider = 4096;
        private const int MaxQueryMeshTrianglesPerScene = 200000;
        private const int MaxMovementMeshTrianglesPerCollider = 4096;
        private const int MaxMovementMeshTrianglesPerScene = 200000;
        private const float QueryMeshDegenerateTriangleAreaSquaredEpsilon = 0.000000000001f;
        private const string ArenaEnvironmentVariantPrefix = "Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants/";
        private static readonly string[] DecorativeNoCollisionPathContains =
        {
            "Rocks/TFD_Rock_Small",
            "Rocks/TFD_Stone",
            "TFD_Rock_Small",
            "TFD_Stone",
            "Particles/",
            "TFD_Fog_",
            "TGV_Fog_",
        };
        private const string RelativeServerGameplayCollisionPath = "server/src/gameplay_collision.shared.json";
        private const string RelativeServerGameplayQueryCollisionPath = "server/src/gameplay_query_collision.shared.json";
        private const string RelativeServerArenaLayoutPath = "server/src/arena_layout.shared.json";
        private const string RelativeBundledGameplayCollisionPath = "Assets/Arena/Resources/SharedData/gameplay_collision.shared.json";
        private const string RelativeBundledGameplayQueryCollisionPath = "Assets/Arena/Resources/SharedData/gameplay_query_collision.shared.json";
        private const string RelativeBundledArenaLayoutPath = "Assets/Arena/Resources/SharedData/arena_layout.shared.json";
        private const string RelativeServerWorldDataDirectory = "server/src/world_data";
        private const string RelativeBundledWorldDataDirectory = "Assets/Arena/Resources/SharedData/Worlds";

        [Serializable]
        private sealed class ExportLayout
        {
            public int version = 1;
            public ExportBox[] boxes = Array.Empty<ExportBox>();
            public ExportMeshGeometry[] mesh_geometries = Array.Empty<ExportMeshGeometry>();
            public ExportMeshInstance[] mesh_instances = Array.Empty<ExportMeshInstance>();
        }

        [Serializable]
        private sealed class ExportBox
        {
            public string name = "";
            public string shape = "obb_y";
            public float[] center = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public float rotation_y_deg;
            public float[] rotation = Array.Empty<float>();
        }

        [Serializable]
        private sealed class ExportMeshGeometry
        {
            public string id = "";
            public string source = "";
            public int vertex_count;
            public int triangle_count;
            public float[] vertices = Array.Empty<float>();
            public int[] indices = Array.Empty<int>();
        }

        [Serializable]
        private sealed class ExportMeshInstance
        {
            public string name = "";
            public string geometry_id = "";
            public float[] transform = Array.Empty<float>();
        }

        private sealed class ExportCollisionData
        {
            public List<ExportBox> Boxes { get; } = new();
            public List<ExportMeshGeometry> MeshGeometries { get; } = new();
            public List<ExportMeshInstance> MeshInstances { get; } = new();
        }

        private enum TiltedBoxExportMode
        {
            WorldAabb,
            FullRotation,
            Reject,
        }

        private enum MeshCollisionExportMode
        {
            Query,
            Movement,
        }

        [Serializable]
        private sealed class ExportHeightfield
        {
            public int version = 1;
            public float[] origin = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public int resolution_x;
            public int resolution_z;
            public float[] heights = Array.Empty<float>();
        }

        public static void Export()
        {
            int gameplayLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            int queryLayer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (gameplayLayer < 0 || queryLayer < 0)
            {
                Debug.LogError(
                    $"[GameplayCollisionExporter] Required layers are missing: " +
                    $"{GameplayCollisionLayer}={gameplayLayer}, {GameplayQueryCollisionLayer}={queryLayer}.");
                return;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[GameplayCollisionExporter] No active scene loaded.");
                return;
            }

            List<string> gameplayWarnings = new();
            List<string> gameplayErrors = new();
            ExportCollisionData gameplayCollision = CollectMovementCollisionDataForLayer(
                activeScene,
                gameplayLayer,
                gameplayWarnings,
                gameplayErrors,
                requireArenaEnvironmentVariantSource: false,
                tiltedBoxExportMode: TiltedBoxExportMode.WorldAabb);
            WriteExportLayout(RelativeServerGameplayCollisionPath, RelativeBundledGameplayCollisionPath, gameplayCollision);

            List<string> queryWarnings = new();
            List<string> queryErrors = new();
            ExportCollisionData queryCollision = CollectQueryCollisionDataForLayer(
                activeScene,
                queryLayer,
                queryWarnings,
                queryErrors,
                requireArenaEnvironmentVariantSource: false,
                tiltedBoxExportMode: TiltedBoxExportMode.FullRotation);
            WriteExportLayout(
                RelativeServerGameplayQueryCollisionPath,
                RelativeBundledGameplayQueryCollisionPath,
                queryCollision);

            SyncArenaLayoutToBundled(logSummary: false);
            AssetDatabase.Refresh();

            Debug.Log(
                $"[GameplayCollisionExporter] Exported arena collision: " +
                $"{gameplayCollision.Boxes.Count} {GameplayCollisionLayer} box collider(s), " +
                $"{gameplayCollision.MeshInstances.Count} {GameplayCollisionLayer} mesh instance(s), " +
                $"{queryCollision.Boxes.Count} {GameplayQueryCollisionLayer} box collider(s), " +
                $"{queryCollision.MeshInstances.Count} {GameplayQueryCollisionLayer} mesh instance(s) " +
                $"using {queryCollision.MeshGeometries.Count} shared mesh geometries.");
            LogExportWarnings(gameplayWarnings.Concat(queryWarnings));
            LogExportErrors(gameplayErrors.Concat(queryErrors));
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/4 Export Active Scene World Data", false, 400)]
        public static void ExportActiveSceneWorldData()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[GameplayCollisionExporter] No active scene loaded.");
                return;
            }

            string dataKey = BuildSceneDataKey(activeScene.name);
            ExportSceneGameplayCollision(activeScene, dataKey);
            ExportSceneGameplayQueryCollision(activeScene, dataKey);
            ExportSelectedTerrainHeightfieldInternal(activeScene, dataKey);
            AssetDatabase.Refresh();
            Debug.Log($"[GameplayCollisionExporter] Exported scene world data for '{activeScene.name}' (key '{dataKey}').");
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/2 Audit Selected BoxColliders", false, 200)]
        public static void AuditSelectedHierarchyBoxColliders()
        {
            if (!TryGetSelectedRoots(out GameObject[] roots))
            {
                Debug.LogError("[GameplayCollisionExporter] Select one or more root GameObjects to audit.");
                return;
            }

            List<BoxCollider> candidates = CollectCandidateBoxColliders(roots);
            int alreadyGameplay = candidates.Count(c => c.gameObject.layer == LayerMask.NameToLayer(GameplayCollisionLayer));
            int untagged = candidates.Count - alreadyGameplay;

            Debug.Log(
                $"[GameplayCollisionExporter] Audited {roots.Length} selected root(s): " +
                $"{candidates.Count} candidate BoxColliders, {alreadyGameplay} already on {GameplayCollisionLayer}, {untagged} not yet tagged.");

            foreach (BoxCollider collider in candidates.Take(25))
            {
                Debug.Log(
                    $"[GameplayCollisionExporter] Candidate: {GetHierarchyPath(collider.transform)} " +
                    $"layer={LayerMask.LayerToName(collider.gameObject.layer)}");
            }

            if (candidates.Count > 25)
            {
                Debug.Log($"[GameplayCollisionExporter] ...and {candidates.Count - 25} more candidate BoxColliders.");
            }
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/2b Audit Selected Query Colliders", false, 250)]
        public static void AuditSelectedHierarchyQueryColliders()
        {
            if (!TryGetSelectedRoots(out GameObject[] roots))
            {
                Debug.LogError("[GameplayCollisionExporter] Select one or more root GameObjects to audit.");
                return;
            }

            int layer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayQueryCollisionLayer}' does not exist.");
                return;
            }

            List<Collider> candidates = CollectCandidateQueryColliders(roots);
            int alreadyQuery = candidates.Count(c => c.gameObject.layer == layer);
            int gameplayMovement = candidates.Count(c => c.gameObject.layer == LayerMask.NameToLayer(GameplayCollisionLayer));
            int untagged = candidates.Count - alreadyQuery;
            int boxes = candidates.Count(c => c is BoxCollider);
            int meshes = candidates.Count(c => c is MeshCollider);
            int fullTransformCompatible = candidates.Count(c => IsSharedPrototypeTransformCompatible(c.transform, out _));
            int transformReviewRequired = candidates.Count - fullTransformCompatible;

            Debug.Log(
                $"[GameplayCollisionExporter] Audited {roots.Length} selected root(s): " +
                $"{candidates.Count} candidate query colliders ({boxes} BoxCollider, {meshes} MeshCollider), " +
                $"{alreadyQuery} already on {GameplayQueryCollisionLayer}, {gameplayMovement} currently on {GameplayCollisionLayer}, " +
                $"{untagged} not yet tagged. " +
                $"Full-transform shared-prototype compatible: {fullTransformCompatible}; transform review required: {transformReviewRequired}.");

            foreach (Collider collider in candidates.Take(25))
            {
                bool shared = IsSharedPrototypeTransformCompatible(collider.transform, out string reason);
                Debug.Log(
                    $"[GameplayCollisionExporter] Query candidate: {GetHierarchyPath(collider.transform)} " +
                    $"type={collider.GetType().Name} layer={LayerMask.LayerToName(collider.gameObject.layer)} " +
                    $"prototype_transform={(shared ? "full_transform_shared" : "review")} reason={reason}");
            }

            if (candidates.Count > 25)
            {
                Debug.Log($"[GameplayCollisionExporter] ...and {candidates.Count - 25} more candidate query colliders.");
            }
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/3 Mark Selected BoxColliders As GameplayCollision", false, 300)]
        public static void MarkSelectedHierarchyAsGameplayCollision()
        {
            int layer = LayerMask.NameToLayer(GameplayCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayCollisionLayer}' does not exist.");
                return;
            }

            if (!TryGetSelectedRoots(out GameObject[] roots))
            {
                Debug.LogError("[GameplayCollisionExporter] Select one or more root GameObjects to tag.");
                return;
            }

            List<BoxCollider> candidates = CollectCandidateBoxColliders(roots);
            int changed = 0;
            Undo.SetCurrentGroupName("Mark Gameplay Collision");
            foreach (BoxCollider collider in candidates)
            {
                if (collider.gameObject.layer == layer)
                    continue;

                Undo.RecordObject(collider.gameObject, "Mark Gameplay Collision");
                collider.gameObject.layer = layer;
                EditorUtility.SetDirty(collider.gameObject);
                changed++;
            }

            Debug.Log(
                $"[GameplayCollisionExporter] Tagged {changed} GameObjects as {GameplayCollisionLayer} " +
                $"from {candidates.Count} candidate BoxColliders across {roots.Length} selected root(s).");
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/3b Mark Selected Query Colliders As GameplayQueryCollision", false, 350)]
        public static void MarkSelectedHierarchyAsGameplayQueryCollision()
        {
            int layer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayQueryCollisionLayer}' does not exist.");
                return;
            }

            if (!TryGetSelectedRoots(out GameObject[] roots))
            {
                Debug.LogError("[GameplayCollisionExporter] Select one or more root GameObjects to tag.");
                return;
            }

            List<Collider> candidates = CollectCandidateQueryColliders(roots);
            int gameplayCollisionLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            int changed = 0;
            int skippedMovement = 0;
            Undo.SetCurrentGroupName("Mark Gameplay Query Collision");
            foreach (Collider collider in candidates)
            {
                if (collider.gameObject.layer == layer)
                    continue;
                if (collider.gameObject.layer == gameplayCollisionLayer)
                {
                    skippedMovement++;
                    continue;
                }

                Undo.RecordObject(collider.gameObject, "Mark Gameplay Query Collision");
                collider.gameObject.layer = layer;
                EditorUtility.SetDirty(collider.gameObject);
                changed++;
            }

            Debug.Log(
                $"[GameplayCollisionExporter] Tagged {changed} GameObjects as {GameplayQueryCollisionLayer} " +
                $"from {candidates.Count} candidate query colliders across {roots.Length} selected root(s). " +
                $"Skipped {skippedMovement} existing {GameplayCollisionLayer} collider(s) so movement blockers are not moved to the query layer.");
        }

        [MenuItem("Arena/OpenWorld/Scene Prep/3c Prepare Selected Variant Collision Roles", false, 360)]
        public static void PrepareSelectedVariantCollisionRoles()
        {
            int gameplayCollisionLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            int gameplayQueryCollisionLayer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (gameplayCollisionLayer < 0 || gameplayQueryCollisionLayer < 0)
            {
                Debug.LogError(
                    $"[GameplayCollisionExporter] Required layers are missing: " +
                    $"{GameplayCollisionLayer}={gameplayCollisionLayer}, {GameplayQueryCollisionLayer}={gameplayQueryCollisionLayer}.");
                return;
            }

            GameObject[] sceneRoots = GetCollisionPreparationSceneRoots();
            List<string> prefabAssetPaths = GetSelectedPrefabAssetPaths();
            if (sceneRoots.Length == 0 && prefabAssetPaths.Count == 0)
            {
                Debug.LogError(
                    "[GameplayCollisionExporter] Select one or more prefab assets, Project folders, prefab/scene roots, " +
                    $"scene containers, or prefab instances to prepare. Current selection: {DescribeSelection()}");
                return;
            }

            int movementObjectsTagged = 0;
            int visualBoxCollidersMoved = 0;
            int queryObjectsCreated = 0;
            int queryObjectsUpdated = 0;
            int visualCollisionLayersCleared = 0;
            int unsupportedCapsuleColliders = 0;
            int preparedRoots = 0;

            Undo.SetCurrentGroupName("Prepare Variant Collision Roles");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (GameObject root in sceneRoots)
            {
                PrepareCollisionRolesForRoot(
                    root,
                    gameplayCollisionLayer,
                    gameplayQueryCollisionLayer,
                    ref movementObjectsTagged,
                    ref visualBoxCollidersMoved,
                    ref queryObjectsCreated,
                    ref queryObjectsUpdated,
                    ref visualCollisionLayersCleared,
                    ref unsupportedCapsuleColliders);
                preparedRoots++;
            }

            foreach (string prefabAssetPath in prefabAssetPaths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
                try
                {
                    PrepareCollisionRolesForRoot(
                        root,
                        gameplayCollisionLayer,
                        gameplayQueryCollisionLayer,
                        ref movementObjectsTagged,
                        ref visualBoxCollidersMoved,
                        ref queryObjectsCreated,
                        ref queryObjectsUpdated,
                        ref visualCollisionLayersCleared,
                        ref unsupportedCapsuleColliders);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
                    preparedRoots++;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[GameplayCollisionExporter] Prepared collision roles for {preparedRoots} root(s): " +
                $"{movementObjectsTagged} movement object(s) on {GameplayCollisionLayer}, " +
                $"{visualBoxCollidersMoved} visual/root box collider(s) copied to dedicated collision children, " +
                $"{queryObjectsCreated} query object(s) created, {queryObjectsUpdated} query object(s) updated, " +
                $"{visualCollisionLayersCleared} visual/root object(s) moved back to Default, " +
                $"{unsupportedCapsuleColliders} capsule collider(s) preserved as source data but not exported by current server collision.");
        }

        public static int PreparePrefabCollisionRoles(IReadOnlyCollection<string> prefabAssetPaths)
        {
            int gameplayCollisionLayer = LayerMask.NameToLayer(GameplayCollisionLayer);
            int gameplayQueryCollisionLayer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (gameplayCollisionLayer < 0 || gameplayQueryCollisionLayer < 0)
            {
                Debug.LogError(
                    $"[GameplayCollisionExporter] Required layers are missing: " +
                    $"{GameplayCollisionLayer}={gameplayCollisionLayer}, {GameplayQueryCollisionLayer}={gameplayQueryCollisionLayer}.");
                return 0;
            }

            List<string> paths = prefabAssetPaths
                .Where(path => !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            if (paths.Count == 0)
                return 0;

            int movementObjectsTagged = 0;
            int visualBoxCollidersMoved = 0;
            int queryObjectsCreated = 0;
            int queryObjectsUpdated = 0;
            int visualCollisionLayersCleared = 0;
            int unsupportedCapsuleColliders = 0;
            int preparedRoots = 0;

            foreach (string prefabAssetPath in paths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
                try
                {
                    PrepareCollisionRolesForRoot(
                        root,
                        gameplayCollisionLayer,
                        gameplayQueryCollisionLayer,
                        ref movementObjectsTagged,
                        ref visualBoxCollidersMoved,
                        ref queryObjectsCreated,
                        ref queryObjectsUpdated,
                        ref visualCollisionLayersCleared,
                        ref unsupportedCapsuleColliders);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
                    preparedRoots++;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                $"[GameplayCollisionExporter] Prepared collision roles for {preparedRoots} prefab asset(s): " +
                $"{movementObjectsTagged} movement object(s) on {GameplayCollisionLayer}, " +
                $"{visualBoxCollidersMoved} visual/root box collider(s) copied to dedicated collision children, " +
                $"{queryObjectsCreated} query object(s) created, {queryObjectsUpdated} query object(s) updated, " +
                $"{visualCollisionLayersCleared} visual/root object(s) moved back to Default, " +
                $"{unsupportedCapsuleColliders} capsule collider(s) preserved as source data but not exported by current server collision.");
            return preparedRoots;
        }

        public static void ExportSelectedTerrainHeightfield()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[GameplayCollisionExporter] No active scene loaded.");
                return;
            }

            string dataKey = BuildSceneDataKey(activeScene.name);
            ExportSelectedTerrainHeightfieldInternal(activeScene, dataKey);
            AssetDatabase.Refresh();
        }

        private static void ExportSelectedTerrainHeightfieldInternal(Scene activeScene, string dataKey)
        {
            Terrain? terrain = ResolveSelectedTerrain();
            if (terrain == null)
            {
                Debug.LogError("[GameplayCollisionExporter] Select a Terrain object before exporting a heightfield.");
                return;
            }

            TerrainData terrainData = terrain.terrainData;
            if (terrainData == null)
            {
                Debug.LogError("[GameplayCollisionExporter] Selected terrain is missing TerrainData.");
                return;
            }

            int resolution = terrainData.heightmapResolution;
            float[,] sourceHeights = terrainData.GetHeights(0, 0, resolution, resolution);
            float[] heights = new float[resolution * resolution];
            int index = 0;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float normalizedHeight = sourceHeights[z, x];
                    heights[index++] = terrain.transform.position.y + normalizedHeight * terrainData.size.y;
                }
            }

            var layout = new ExportHeightfield
            {
                version = 1,
                origin = new[] { terrain.transform.position.x, terrain.transform.position.y, terrain.transform.position.z },
                size = new[] { terrainData.size.x, terrainData.size.y, terrainData.size.z },
                resolution_x = resolution,
                resolution_z = resolution,
                heights = heights,
            };

            string json = JsonUtility.ToJson(layout, true);
            WriteProjectText(SceneServerHeightfieldPath(dataKey), json);
            WriteProjectText(SceneBundledHeightfieldPath(dataKey), json);

            Debug.Log(
                $"[GameplayCollisionExporter] Exported terrain heightfield {resolution}x{resolution} from {terrain.name} to " +
                $"{SceneServerHeightfieldPath(dataKey)} and {SceneBundledHeightfieldPath(dataKey)}");
        }

        public static void SyncSharedMovementData()
        {
            SyncArenaLayoutToBundled(logSummary: false);
            SyncGameplayCollisionToBundled(logSummary: false);
            SyncSceneWorldDataToBundled(logSummary: false);
            AssetDatabase.Refresh();
            Debug.Log("[GameplayCollisionExporter] Synced arena layout, gameplay collision, and scene world data into bundled Resources assets.");
        }

        private static float DeltaAngleFromZero(float angle) => Mathf.DeltaAngle(0f, angle);

        private static string FormatVector3(Vector3 value)
            => $"({value.x:F2}, {value.y:F2}, {value.z:F2})";

        private static string GetHierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = $"{transform.name}/{path}";
            }
            return path;
        }

        private static bool ShouldExcludeFromGameplayCollision(GameObject gameObject)
        {
            string hierarchyPath = GetHierarchyPath(gameObject.transform);
            if (MatchesDecorativeNoCollisionPath(hierarchyPath) ||
                MatchesDecorativeNoCollisionPath(gameObject.name))
            {
                return true;
            }

            GameObject? prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            string prefabPath = prefabRoot != null
                ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot)
                : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            return MatchesDecorativeNoCollisionPath(prefabPath);
        }

        private static bool MatchesDecorativeNoCollisionPath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string normalized = value.Replace('\\', '/');
            return DecorativeNoCollisionPathContains.Any(pattern =>
                normalized.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int RemoveExcludedCollisionChildren(
            GameObject root,
            int gameplayCollisionLayer,
            int gameplayQueryCollisionLayer)
        {
            int changed = 0;
            List<Transform> excludedRoots = root.GetComponentsInChildren<Transform>(includeInactive: true)
                .Where(transform =>
                    ShouldExcludeFromGameplayCollision(transform.gameObject) &&
                    (transform.parent == null || !ShouldExcludeFromGameplayCollision(transform.parent.gameObject)))
                .ToList();

            foreach (Transform excludedRoot in excludedRoots)
            {
                foreach (GameObject child in CollectDirectCollisionChildren(excludedRoot.gameObject, GameplayCollisionChildName)
                             .Concat(CollectDirectCollisionChildren(excludedRoot.gameObject, GameplayQueryCollisionChildName))
                             .ToList())
                {
                    Undo.DestroyObjectImmediate(child);
                    changed++;
                }

                foreach (Transform transform in excludedRoot.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    GameObject gameObject = transform.gameObject;
                    if (gameObject.layer != gameplayCollisionLayer && gameObject.layer != gameplayQueryCollisionLayer)
                        continue;

                    if (SetLayer(gameObject, 0, "Clear Decorative Collision Layer"))
                        changed++;
                }
            }

            return changed;
        }

        private static void SyncArenaLayoutToBundled(bool logSummary)
        {
            CopyProjectFile(RelativeServerArenaLayoutPath, RelativeBundledArenaLayoutPath);
            if (logSummary)
            {
                Debug.Log(
                    $"[GameplayCollisionExporter] Synced {RelativeServerArenaLayoutPath} -> {RelativeBundledArenaLayoutPath}");
            }
        }

        private static void SyncGameplayCollisionToBundled(bool logSummary)
        {
            CopyProjectFile(RelativeServerGameplayCollisionPath, RelativeBundledGameplayCollisionPath);
            CopyProjectFile(RelativeServerGameplayQueryCollisionPath, RelativeBundledGameplayQueryCollisionPath);
            if (logSummary)
            {
                Debug.Log(
                    $"[GameplayCollisionExporter] Synced arena collision JSON into bundled Resources.");
            }
        }

        private static void SyncSceneWorldDataToBundled(bool logSummary)
        {
            CopyProjectDirectory(RelativeServerWorldDataDirectory, RelativeBundledWorldDataDirectory);
            if (logSummary)
            {
                Debug.Log(
                    $"[GameplayCollisionExporter] Synced {RelativeServerWorldDataDirectory} -> {RelativeBundledWorldDataDirectory}");
            }
        }

        private static Terrain? ResolveSelectedTerrain()
        {
            if (Selection.activeGameObject != null)
            {
                Terrain? selectedTerrain = Selection.activeGameObject.GetComponent<Terrain>();
                if (selectedTerrain != null)
                    return selectedTerrain;
            }

            Terrain? activeTerrain = Terrain.activeTerrain;
            if (activeTerrain != null && activeTerrain.gameObject.scene == SceneManager.GetActiveScene())
                return activeTerrain;

            return null;
        }

        private static bool TryGetSelectedRoots(out GameObject[] roots)
        {
            roots = Selection.gameObjects
                .Where(go => go != null && go.scene == SceneManager.GetActiveScene())
                .Distinct()
                .ToArray();
            return roots.Length > 0;
        }

        private static GameObject[] GetCollisionPreparationSceneRoots()
        {
            GameObject[] selected = Selection.gameObjects
                .Where(go => go != null && !EditorUtility.IsPersistent(go) && go.scene.IsValid())
                .Distinct()
                .ToArray();
            if (selected.Length == 0)
            {
                return Array.Empty<GameObject>();
            }

            var prefabRoots = new HashSet<GameObject>();
            foreach (GameObject selection in selected)
            {
                foreach (Transform transform in selection.GetComponentsInChildren<Transform>(includeInactive: false))
                {
                    GameObject? prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(transform.gameObject);
                    if (prefabRoot == null ||
                        prefabRoot.scene != selection.scene ||
                        !prefabRoot.transform.IsChildOf(selection.transform))
                    {
                        continue;
                    }

                    prefabRoots.Add(prefabRoot);
                }
            }

            if (prefabRoots.Count > 0)
            {
                return prefabRoots
                    .OrderBy(root => GetHierarchyPath(root.transform), StringComparer.Ordinal)
                    .ToArray();
            }

            return selected;
        }

        private static List<string> GetSelectedPrefabAssetPaths()
        {
            var prefabPaths = new HashSet<string>(StringComparer.Ordinal);
            var selectedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                if (string.IsNullOrEmpty(path))
                    continue;

                selectedPaths.Add(path);
            }

            foreach (string guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    selectedPaths.Add(path);
            }

            foreach (string path in selectedPaths)
            {
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabPaths.Add(path);
                    continue;
                }

                if (!AssetDatabase.IsValidFolder(path))
                    continue;

                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                {
                    string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(prefabPath) &&
                        prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        prefabPaths.Add(prefabPath);
                    }
                }
            }

            return prefabPaths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static string DescribeSelection()
        {
            var objectDescriptions = Selection.objects
                .Select(selected =>
                {
                    string path = AssetDatabase.GetAssetPath(selected);
                    string name = selected != null ? selected.name : "<null>";
                    string type = selected != null ? selected.GetType().Name : "<null>";
                    return string.IsNullOrEmpty(path) ? $"{name} ({type}, no asset path)" : $"{path} ({type})";
                });
            var guidDescriptions = Selection.assetGUIDs
                .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => $"{path} (assetGUID)");
            string description = string.Join(", ", objectDescriptions.Concat(guidDescriptions).Distinct());
            return string.IsNullOrEmpty(description) ? "<empty>" : description;
        }

        private static void PrepareCollisionRolesForRoot(
            GameObject root,
            int gameplayCollisionLayer,
            int gameplayQueryCollisionLayer,
            ref int movementObjectsTagged,
            ref int visualBoxCollidersMoved,
            ref int queryObjectsCreated,
            ref int queryObjectsUpdated,
            ref int visualCollisionLayersCleared,
            ref int unsupportedCapsuleColliders)
        {
            RemoveExcludedCollisionChildren(root, gameplayCollisionLayer, gameplayQueryCollisionLayer);

            unsupportedCapsuleColliders += CountUnsupportedCapsuleColliders(root);
            movementObjectsTagged += PrepareMovementCollision(
                root,
                gameplayCollisionLayer,
                gameplayQueryCollisionLayer,
                ref visualBoxCollidersMoved,
                ref queryObjectsCreated,
                ref queryObjectsUpdated);
            PrepareQueryCollision(
                root,
                gameplayCollisionLayer,
                gameplayQueryCollisionLayer,
                ref queryObjectsCreated,
                ref queryObjectsUpdated);
            visualCollisionLayersCleared += ClearCollisionLayersFromVisualOnlyObjects(
                root,
                gameplayCollisionLayer,
                gameplayQueryCollisionLayer);

            EditorUtility.SetDirty(root);
            PrefabUtility.RecordPrefabInstancePropertyModifications(root);
        }

        private static List<BoxCollider> CollectCandidateBoxColliders(IEnumerable<GameObject> roots)
        {
            var candidates = new List<BoxCollider>();
            foreach (GameObject root in roots)
            {
                foreach (BoxCollider collider in root.GetComponentsInChildren<BoxCollider>(includeInactive: false))
                {
                    if (collider == null ||
                        !collider.enabled ||
                        collider.isTrigger ||
                        !collider.gameObject.activeInHierarchy ||
                        ShouldExcludeFromGameplayCollision(collider.gameObject) ||
                        collider.gameObject.scene != SceneManager.GetActiveScene())
                    {
                        continue;
                    }

                    candidates.Add(collider);
                }
            }

            return candidates
                .Distinct()
                .OrderBy(collider => GetHierarchyPath(collider.transform), StringComparer.Ordinal)
                .ToList();
        }

        private static List<Collider> CollectCandidateQueryColliders(IEnumerable<GameObject> roots)
        {
            var candidates = new List<Collider>();
            foreach (GameObject root in roots)
            {
                foreach (Collider collider in root.GetComponentsInChildren<Collider>(includeInactive: false))
                {
                    if (collider == null ||
                        !collider.enabled ||
                        collider.isTrigger ||
                        !collider.gameObject.activeInHierarchy ||
                        collider.gameObject.scene != SceneManager.GetActiveScene() ||
                        ShouldExcludeFromGameplayCollision(collider.gameObject) ||
                        !IsSupportedQueryCollider(collider))
                    {
                        continue;
                    }

                    candidates.Add(collider);
                }
            }

            return candidates
                .Distinct()
                .OrderBy(collider => GetHierarchyPath(collider.transform), StringComparer.Ordinal)
                .ThenBy(collider => collider.GetType().Name, StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsSupportedQueryCollider(Collider collider)
            => collider is BoxCollider || collider is MeshCollider;

        private static int PrepareMovementCollision(
            GameObject root,
            int gameplayCollisionLayer,
            int gameplayQueryCollisionLayer,
            ref int visualBoxCollidersMoved,
            ref int queryObjectsCreated,
            ref int queryObjectsUpdated)
        {
            int changed = 0;
            Scene rootScene = root.scene;
            var clearedMovementChildren = new HashSet<GameObject>();
            var clearedQueryChildren = new HashSet<GameObject>();
            foreach (BoxCollider collider in root.GetComponentsInChildren<BoxCollider>(includeInactive: false)
                         .OrderBy(collider => GetHierarchyPath(collider.transform), StringComparer.Ordinal))
            {
                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy ||
                    ShouldExcludeFromGameplayCollision(collider.gameObject) ||
                    collider.gameObject.scene != rootScene)
                {
                    continue;
                }

                GameObject owner = collider.gameObject;
                if (owner.layer == gameplayQueryCollisionLayer ||
                    owner.name.StartsWith(GameplayQueryCollisionChildName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (owner.name == GameplayCollisionChildName || IsDedicatedColliderObject(owner))
                {
                    if (SetLayer(owner, gameplayCollisionLayer, "Prepare Movement Collision"))
                        changed++;
                    continue;
                }

                string childName = owner == root
                    ? GameplayCollisionChildName
                    : $"{GameplayCollisionChildName}_{SanitizeObjectName(owner.name)}";
                GameObject movementChild = GetOrCreateColliderChild(root, childName, gameplayCollisionLayer);
                CopyWorldTransformIntoRootChild(collider.transform, root.transform, movementChild.transform);
                CopyBoxColliderToQueryChild(
                    root,
                    collider,
                    gameplayQueryCollisionLayer,
                    ref queryObjectsCreated,
                    ref queryObjectsUpdated,
                    clearedQueryChildren);
                if (clearedMovementChildren.Add(movementChild))
                    RemoveBoxColliders(movementChild);
                if (!HasEquivalentBoxCollider(movementChild, collider))
                {
                    BoxCollider copy = Undo.AddComponent<BoxCollider>(movementChild);
                    CopyBoxCollider(collider, copy);
                    changed++;
                }

                if (SetLayer(movementChild, gameplayCollisionLayer, "Prepare Movement Collision"))
                    changed++;

                SetLayer(owner, 0, "Clear Source Collision Layer");
                visualBoxCollidersMoved++;
            }

            Transform? existingMovementRootTransform = GetExistingChild(root, GameplayCollisionChildName)?.transform;
            Transform? existingQueryRootTransform = GetExistingChild(root, GameplayQueryCollisionChildName)?.transform;
            List<MeshCollider> meshColliders = root.GetComponentsInChildren<MeshCollider>(includeInactive: true)
                .Where(collider =>
                    collider != null &&
                    !collider.isTrigger &&
                    collider.sharedMesh != null &&
                    collider.gameObject.scene == rootScene &&
                    !ShouldExcludeFromGameplayCollision(collider.gameObject) &&
                    collider.gameObject.layer != gameplayCollisionLayer &&
                    collider.gameObject.layer != gameplayQueryCollisionLayer &&
                    !collider.gameObject.name.StartsWith(GameplayCollisionChildName, StringComparison.Ordinal) &&
                    !collider.gameObject.name.StartsWith(GameplayQueryCollisionChildName, StringComparison.Ordinal) &&
                    (existingMovementRootTransform == null || !collider.transform.IsChildOf(existingMovementRootTransform)) &&
                    (existingQueryRootTransform == null || !collider.transform.IsChildOf(existingQueryRootTransform)))
                .OrderBy(collider => GetHierarchyPath(collider.transform), StringComparer.Ordinal)
                .ToList();
            List<MeshCollider> explicitMeshColliders = meshColliders
                .Where(collider => collider.gameObject.name.Contains("MeshCollider", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (explicitMeshColliders.Count > 0)
                meshColliders = explicitMeshColliders;

            if (meshColliders.Count > 0)
            {
                foreach (GameObject movementRoot in CollectDirectCollisionChildren(root, GameplayCollisionChildName))
                {
                    Undo.DestroyObjectImmediate(movementRoot);
                    changed++;
                }
                clearedMovementChildren.Clear();
            }

            int meshIndex = 0;
            foreach (MeshCollider source in meshColliders)
            {
                string childName = meshColliders.Count == 1
                    ? GameplayCollisionChildName
                    : $"{GameplayCollisionChildName}_{SanitizeObjectName(source.gameObject.name)}_{meshIndex + 1}";
                GameObject movementChild = GetOrCreateColliderChild(root, childName, gameplayCollisionLayer);
                CopyWorldTransformIntoRootChild(source.transform, root.transform, movementChild.transform);
                if (clearedMovementChildren.Add(movementChild))
                {
                    RemoveBoxColliders(movementChild);
                    RemoveMeshColliders(movementChild);
                }

                MeshCollider? target = movementChild.GetComponent<MeshCollider>();
                if (target == null)
                    target = Undo.AddComponent<MeshCollider>(movementChild);
                CopyMeshCollider(source, target);
                if (SetLayer(movementChild, gameplayCollisionLayer, "Prepare Movement Mesh Collision"))
                    changed++;
                EditorUtility.SetDirty(movementChild);
                changed++;
                meshIndex++;
            }

            changed += EnableDirectCollisionMeshChildren(
                root,
                GameplayCollisionChildName,
                gameplayCollisionLayer,
                "Repair Movement Mesh Collision");

            return changed;
        }

        private static void PrepareQueryCollision(
            GameObject root,
            int gameplayCollisionLayer,
            int gameplayQueryCollisionLayer,
            ref int queryObjectsCreated,
            ref int queryObjectsUpdated)
        {
            Transform? existingMovementRootTransform = GetExistingChild(root, GameplayCollisionChildName)?.transform;
            GameObject? existingQueryRoot = GetExistingChild(root, GameplayQueryCollisionChildName);
            Transform? existingQueryRootTransform = existingQueryRoot != null ? existingQueryRoot.transform : null;
            Scene rootScene = root.scene;

            List<MeshCollider> meshColliders = root.GetComponentsInChildren<MeshCollider>(includeInactive: true)
                .Where(collider =>
                    collider != null &&
                    !collider.isTrigger &&
                    collider.sharedMesh != null &&
                    collider.gameObject.scene == rootScene &&
                    !ShouldExcludeFromGameplayCollision(collider.gameObject) &&
                    collider.gameObject.layer != gameplayCollisionLayer &&
                    collider.gameObject.layer != gameplayQueryCollisionLayer &&
                    !collider.gameObject.name.StartsWith(GameplayCollisionChildName, StringComparison.Ordinal) &&
                    !collider.gameObject.name.StartsWith(GameplayQueryCollisionChildName, StringComparison.Ordinal) &&
                    (existingMovementRootTransform == null || !collider.transform.IsChildOf(existingMovementRootTransform)) &&
                    (existingQueryRootTransform == null || !collider.transform.IsChildOf(existingQueryRootTransform)))
                .OrderBy(collider => GetHierarchyPath(collider.transform), StringComparer.Ordinal)
                .ToList();
            List<MeshCollider> explicitMeshColliders = meshColliders
                .Where(collider => collider.gameObject.name.Contains("MeshCollider", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (explicitMeshColliders.Count > 0)
                meshColliders = explicitMeshColliders;

            if (meshColliders.Count == 0 && HasAnyQueryCollider(root))
                return;

            if (meshColliders.Count > 0)
            {
                foreach (GameObject queryRoot in CollectDirectCollisionChildren(root, GameplayQueryCollisionChildName))
                {
                    Undo.DestroyObjectImmediate(queryRoot);
                    queryObjectsUpdated++;
                }
            }

            int meshIndex = 0;
            foreach (MeshCollider source in meshColliders)
            {
                string childName = meshColliders.Count == 1
                    ? GameplayQueryCollisionChildName
                    : $"{GameplayQueryCollisionChildName}_{SanitizeObjectName(source.gameObject.name)}_{meshIndex + 1}";
                GameObject? existing = GetExistingChild(root, childName);
                GameObject queryChild;
                if (existing == null)
                {
                    queryChild = new GameObject(childName);
                    Undo.RegisterCreatedObjectUndo(queryChild, "Create Query Collision");
                    queryChild.transform.SetParent(root.transform, false);
                    queryObjectsCreated++;
                }
                else
                {
                    queryChild = existing;
                    queryObjectsUpdated++;
                }

                Undo.RecordObject(queryChild, "Prepare Query Collision");
                queryChild.layer = gameplayQueryCollisionLayer;
                CopyWorldTransformIntoRootChild(source.transform, root.transform, queryChild.transform);

                MeshCollider? target = queryChild.GetComponent<MeshCollider>();
                if (target == null)
                    target = Undo.AddComponent<MeshCollider>(queryChild);
                CopyMeshCollider(source, target);
                EditorUtility.SetDirty(queryChild);
                meshIndex++;
            }

            int repairedExistingQueryMeshes = EnableDirectCollisionMeshChildren(
                root,
                GameplayQueryCollisionChildName,
                gameplayQueryCollisionLayer,
                "Repair Query Mesh Collision");
            if (meshColliders.Count == 0 && repairedExistingQueryMeshes > 0)
            {
                queryObjectsUpdated += repairedExistingQueryMeshes;
                return;
            }

            List<BoxCollider> movementBoxes = root.GetComponentsInChildren<BoxCollider>(includeInactive: false)
                .Where(collider =>
                    collider != null &&
                    collider.enabled &&
                    !collider.isTrigger &&
                    collider.gameObject.activeInHierarchy &&
                    !ShouldExcludeFromGameplayCollision(collider.gameObject) &&
                    collider.gameObject.scene == rootScene &&
                    collider.gameObject.layer != gameplayQueryCollisionLayer &&
                    (collider.gameObject.name == GameplayCollisionChildName ||
                     collider.gameObject.name.StartsWith($"{GameplayCollisionChildName}_", StringComparison.Ordinal)))
                .OrderBy(collider => GetHierarchyPath(collider.transform), StringComparer.Ordinal)
                .ToList();

            int boxIndex = 0;
            var clearedBoxQueryChildren = new HashSet<GameObject>();
            foreach (BoxCollider source in movementBoxes)
            {
                if (meshColliders.Count > 0 && HasAnyQueryCollider(root))
                    break;

                string childName = movementBoxes.Count == 1
                    ? GameplayQueryCollisionChildName
                    : $"{GameplayQueryCollisionChildName}_{SanitizeObjectName(source.gameObject.name)}_{boxIndex + 1}";
                CopyBoxColliderToQueryChild(
                    root,
                    source,
                    gameplayQueryCollisionLayer,
                    childName,
                    ref queryObjectsCreated,
                    ref queryObjectsUpdated,
                    clearedBoxQueryChildren);
                boxIndex++;
            }
        }

        private static void CopyBoxColliderToQueryChild(
            GameObject root,
            BoxCollider source,
            int gameplayQueryCollisionLayer,
            ref int queryObjectsCreated,
            ref int queryObjectsUpdated,
            HashSet<GameObject>? clearedQueryChildren = null)
            => CopyBoxColliderToQueryChild(
                root,
                source,
                gameplayQueryCollisionLayer,
                GameplayQueryCollisionChildName,
                ref queryObjectsCreated,
                ref queryObjectsUpdated,
                clearedQueryChildren);

        private static void CopyBoxColliderToQueryChild(
            GameObject root,
            BoxCollider source,
            int gameplayQueryCollisionLayer,
            string childName,
            ref int queryObjectsCreated,
            ref int queryObjectsUpdated,
            HashSet<GameObject>? clearedQueryChildren = null)
        {
            GameObject? existing = GetExistingChild(root, childName);
            GameObject queryChild;
            if (existing == null)
            {
                queryChild = new GameObject(childName);
                Undo.RegisterCreatedObjectUndo(queryChild, "Create Query Collision");
                queryChild.transform.SetParent(root.transform, false);
                queryObjectsCreated++;
            }
            else
            {
                queryChild = existing;
                queryObjectsUpdated++;
            }

            Undo.RecordObject(queryChild, "Prepare Query Collision");
            queryChild.layer = gameplayQueryCollisionLayer;
            CopyWorldTransformIntoRootChild(source.transform, root.transform, queryChild.transform);
            if (clearedQueryChildren == null || clearedQueryChildren.Add(queryChild))
            {
                RemoveMeshColliders(queryChild);
                RemoveBoxColliders(queryChild);
            }
            if (!HasEquivalentBoxCollider(queryChild, source))
            {
                BoxCollider target = Undo.AddComponent<BoxCollider>(queryChild);
                CopyBoxCollider(source, target);
            }

            EditorUtility.SetDirty(queryChild);
        }

        private static int ClearCollisionLayersFromVisualOnlyObjects(
            GameObject root,
            int gameplayCollisionLayer,
            int gameplayQueryCollisionLayer)
        {
            int changed = 0;
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                GameObject gameObject = transform.gameObject;
                if (gameObject.layer != gameplayCollisionLayer && gameObject.layer != gameplayQueryCollisionLayer)
                    continue;
                if (HasEnabledNonTriggerSupportedCollider(gameObject))
                    continue;
                if (gameObject.name == GameplayCollisionChildName || gameObject.name.StartsWith(GameplayQueryCollisionChildName, StringComparison.Ordinal))
                    continue;

                if (SetLayer(gameObject, 0, "Clear Visual Collision Layer"))
                    changed++;
            }

            return changed;
        }

        private static GameObject GetOrCreateColliderChild(GameObject root, string childName, int layer)
        {
            GameObject? child = GetExistingChild(root, childName);
            if (child != null)
            {
                if (!child.activeSelf)
                {
                    Undo.RecordObject(child, "Prepare Collider Child");
                    child.SetActive(true);
                    EditorUtility.SetDirty(child);
                }

                SetLayer(child, layer, "Prepare Collider Child");
                return child;
            }

            child = new GameObject(childName);
            Undo.RegisterCreatedObjectUndo(child, "Create Collider Child");
            child.transform.SetParent(root.transform, false);
            child.layer = layer;
            return child;
        }

        private static GameObject? GetExistingChild(GameObject root, string childName)
        {
            foreach (Transform child in root.transform)
            {
                if (child.name == childName)
                    return child.gameObject;
            }

            return null;
        }

        private static bool IsDedicatedColliderObject(GameObject gameObject)
            => gameObject.GetComponents<Collider>().Length > 0 &&
               gameObject.GetComponentsInChildren<Renderer>(includeInactive: false).Length == 0;

        private static bool HasEnabledNonTriggerSupportedCollider(GameObject gameObject)
            => gameObject.GetComponents<Collider>()
                .Any(collider => collider.enabled && !collider.isTrigger && IsSupportedQueryCollider(collider));

        private static int CountUnsupportedCapsuleColliders(GameObject root)
            => root.GetComponentsInChildren<CapsuleCollider>(includeInactive: false)
                .Count(collider =>
                    collider.enabled &&
                    !collider.isTrigger &&
                    collider.gameObject.activeInHierarchy);

        private static bool HasAnyQueryCollider(GameObject root)
            => root.GetComponentsInChildren<Collider>(includeInactive: false)
                .Any(collider =>
                    collider.enabled &&
                    !collider.isTrigger &&
                    collider.gameObject.name.StartsWith(GameplayQueryCollisionChildName, StringComparison.Ordinal));

        private static List<GameObject> CollectDirectCollisionChildren(GameObject root, string childNamePrefix)
        {
            var children = new List<GameObject>();
            foreach (Transform child in root.transform)
            {
                if (child.name == childNamePrefix ||
                    child.name.StartsWith(childNamePrefix + "_", StringComparison.Ordinal))
                {
                    children.Add(child.gameObject);
                }
            }

            return children;
        }

        private static int EnableDirectCollisionMeshChildren(
            GameObject root,
            string childNamePrefix,
            int layer,
            string undoName)
        {
            int changed = 0;
            foreach (GameObject child in CollectDirectCollisionChildren(root, childNamePrefix))
            {
                if (!child.activeSelf)
                {
                    Undo.RecordObject(child, undoName);
                    child.SetActive(true);
                    EditorUtility.SetDirty(child);
                    changed++;
                }

                if (SetLayer(child, layer, undoName))
                    changed++;

                foreach (MeshCollider collider in child.GetComponentsInChildren<MeshCollider>(includeInactive: true))
                {
                    if (collider == null || collider.sharedMesh == null)
                    {
                        continue;
                    }

                    if (!collider.enabled || collider.isTrigger)
                    {
                        Undo.RecordObject(collider, undoName);
                        collider.enabled = true;
                        collider.isTrigger = false;
                        EditorUtility.SetDirty(collider);
                        changed++;
                    }
                }
            }

            return changed;
        }

        private static bool HasEquivalentBoxCollider(GameObject targetObject, BoxCollider source)
            => targetObject.GetComponents<BoxCollider>().Any(target =>
                target.enabled == source.enabled &&
                target.isTrigger == source.isTrigger &&
                Approximately(target.center, source.center) &&
                Approximately(target.size, source.size));

        private static void RemoveBoxColliders(GameObject targetObject)
        {
            foreach (BoxCollider target in targetObject.GetComponents<BoxCollider>())
                Undo.DestroyObjectImmediate(target);
        }

        private static void RemoveMeshColliders(GameObject targetObject)
        {
            foreach (MeshCollider target in targetObject.GetComponents<MeshCollider>())
                Undo.DestroyObjectImmediate(target);
        }

        private static void CopyBoxCollider(BoxCollider source, BoxCollider target)
        {
            target.enabled = source.enabled;
            target.isTrigger = false;
            target.sharedMaterial = source.sharedMaterial;
            target.center = source.center;
            target.size = source.size;
        }

        private static void CopyMeshCollider(MeshCollider source, MeshCollider target)
        {
            target.enabled = true;
            target.isTrigger = false;
            target.sharedMaterial = source.sharedMaterial;
            target.convex = source.convex;
            target.cookingOptions = source.cookingOptions;
            target.sharedMesh = source.sharedMesh;
        }

        private static void CopyWorldTransformIntoRootChild(Transform source, Transform root, Transform target)
        {
            target.SetParent(root, worldPositionStays: false);
            target.SetPositionAndRotation(source.position, source.rotation);
            Vector3 rootScale = root.lossyScale;
            Vector3 sourceScale = source.lossyScale;
            target.localScale = new Vector3(
                SafeScaleDivide(sourceScale.x, rootScale.x),
                SafeScaleDivide(sourceScale.y, rootScale.y),
                SafeScaleDivide(sourceScale.z, rootScale.z));
        }

        private static float SafeScaleDivide(float numerator, float denominator)
            => Mathf.Abs(denominator) <= 0.0001f ? numerator : numerator / denominator;

        private static bool SetLayer(GameObject gameObject, int layer, string undoName)
        {
            if (gameObject.layer == layer)
                return false;

            Undo.RecordObject(gameObject, undoName);
            gameObject.layer = layer;
            EditorUtility.SetDirty(gameObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(gameObject);
            return true;
        }

        private static string SanitizeObjectName(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            string sanitized = new string(chars);
            while (sanitized.Contains("__", StringComparison.Ordinal))
                sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
            return sanitized.Trim('_');
        }

        private static bool Approximately(Vector3 a, Vector3 b)
            => Mathf.Approximately(a.x, b.x) &&
               Mathf.Approximately(a.y, b.y) &&
               Mathf.Approximately(a.z, b.z);

        private static bool IsSharedPrototypeTransformCompatible(Transform transform, out string reason)
        {
            Matrix4x4 matrix = transform.localToWorldMatrix;
            if (matrix.determinant < 0f)
            {
                reason = "negative_or_mirrored_scale";
                return false;
            }

            Vector3 basisX = new(matrix.m00, matrix.m10, matrix.m20);
            Vector3 basisY = new(matrix.m01, matrix.m11, matrix.m21);
            Vector3 basisZ = new(matrix.m02, matrix.m12, matrix.m22);
            float scaleX = basisX.magnitude;
            float scaleY = basisY.magnitude;
            float scaleZ = basisZ.magnitude;
            if (scaleX <= TransformCompatibilityScaleEpsilon ||
                scaleY <= TransformCompatibilityScaleEpsilon ||
                scaleZ <= TransformCompatibilityScaleEpsilon)
            {
                reason = $"zero_or_near_zero_scale({scaleX:F3},{scaleY:F3},{scaleZ:F3})";
                return false;
            }

            float xyDot = Mathf.Abs(Vector3.Dot(basisX / scaleX, basisY / scaleY));
            float xzDot = Mathf.Abs(Vector3.Dot(basisX / scaleX, basisZ / scaleZ));
            float yzDot = Mathf.Abs(Vector3.Dot(basisY / scaleY, basisZ / scaleZ));
            if (xyDot > TransformCompatibilityBasisEpsilon ||
                xzDot > TransformCompatibilityBasisEpsilon ||
                yzDot > TransformCompatibilityBasisEpsilon)
            {
                reason = $"non_orthogonal_basis({xyDot:F4},{xzDot:F4},{yzDot:F4})";
                return false;
            }

            if (Mathf.Abs(scaleX - scaleY) > TransformCompatibilityScaleEpsilon ||
                Mathf.Abs(scaleX - scaleZ) > TransformCompatibilityScaleEpsilon)
            {
                reason = $"non_uniform_scale({scaleX:F3},{scaleY:F3},{scaleZ:F3})";
                return false;
            }

            Vector3 euler = transform.rotation.eulerAngles;
            reason = $"translation_uniform_scale_full_rotation({euler.x:F2},{euler.y:F2},{euler.z:F2})";
            return true;
        }

        private static void ExportSceneGameplayCollision(Scene activeScene, string dataKey)
        {
            int layer = LayerMask.NameToLayer(GameplayCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayCollisionLayer}' does not exist.");
                return;
            }

            List<string> warnings = new();
            List<string> errors = new();
            ExportCollisionData movementCollision = CollectMovementCollisionDataForLayer(
                activeScene,
                layer,
                warnings,
                errors,
                requireArenaEnvironmentVariantSource: false,
                tiltedBoxExportMode: TiltedBoxExportMode.WorldAabb);
            WriteExportLayout(SceneServerCollisionPath(dataKey), SceneBundledCollisionPath(dataKey), movementCollision);

            Debug.Log(
                $"[GameplayCollisionExporter] Exported {movementCollision.Boxes.Count} scene movement box collider(s) and " +
                $"{movementCollision.MeshInstances.Count} scene movement mesh instance(s) using " +
                $"{movementCollision.MeshGeometries.Count} shared mesh geometries to " +
                $"{SceneServerCollisionPath(dataKey)} and {SceneBundledCollisionPath(dataKey)}");
            LogExportWarnings(warnings);
            LogExportErrors(errors);
        }

        private static void ExportSceneGameplayQueryCollision(Scene activeScene, string dataKey)
        {
            int layer = LayerMask.NameToLayer(GameplayQueryCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayQueryCollisionLayer}' does not exist.");
                return;
            }

            List<string> warnings = new();
            List<string> errors = new();
            ExportCollisionData queryCollision = CollectQueryCollisionDataForLayer(
                activeScene,
                layer,
                warnings,
                errors,
                requireArenaEnvironmentVariantSource: true,
                tiltedBoxExportMode: TiltedBoxExportMode.FullRotation);
            WriteExportLayout(SceneServerQueryCollisionPath(dataKey), SceneBundledQueryCollisionPath(dataKey), queryCollision);

            Debug.Log(
                $"[GameplayCollisionExporter] Exported {queryCollision.Boxes.Count} scene query box collider(s) and " +
                $"{queryCollision.MeshInstances.Count} scene query mesh instance(s) using " +
                $"{queryCollision.MeshGeometries.Count} shared mesh geometries to " +
                $"{SceneServerQueryCollisionPath(dataKey)} and {SceneBundledQueryCollisionPath(dataKey)}");
            LogExportWarnings(warnings);
            LogExportErrors(errors);
        }

        private static List<ExportBox> CollectExportBoxesForLayer(
            Scene activeScene,
            int layer,
            List<string> warnings,
            List<string>? errors = null,
            bool requireArenaEnvironmentVariantSource = false,
            TiltedBoxExportMode tiltedBoxExportMode = TiltedBoxExportMode.WorldAabb)
        {
            var boxes = new List<ExportBox>();
            foreach (var collider in UnityEngine.Object.FindObjectsByType<BoxCollider>())
            {
                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy ||
                    collider.gameObject.layer != layer ||
                    ShouldExcludeFromGameplayCollision(collider.gameObject) ||
                    collider.gameObject.scene != activeScene)
                {
                    continue;
                }

                if (requireArenaEnvironmentVariantSource &&
                    !IsAllowedArenaEnvironmentVariantCollider(collider, out string sourcePath))
                {
                    warnings.Add(
                        $"Skipping {GetHierarchyPath(collider.transform)} on {GameplayQueryCollisionLayer}: " +
                        $"source prefab is outside {ArenaEnvironmentVariantPrefix} (source='{sourcePath}').");
                    continue;
                }

                if (TryBuildExportBox(collider, warnings, errors, tiltedBoxExportMode, out ExportBox? exportBox))
                    boxes.Add(exportBox!);
            }

            boxes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return boxes;
        }

        private static ExportCollisionData CollectQueryCollisionDataForLayer(
            Scene activeScene,
            int layer,
            List<string> warnings,
            List<string> errors,
            bool requireArenaEnvironmentVariantSource,
            TiltedBoxExportMode tiltedBoxExportMode)
        {
            var data = new ExportCollisionData();
            data.Boxes.AddRange(CollectExportBoxesForLayer(
                activeScene,
                layer,
                warnings,
                errors,
                requireArenaEnvironmentVariantSource,
                tiltedBoxExportMode));

            CollectMeshCollisionDataForLayer(
                activeScene,
                layer,
                warnings,
                errors,
                requireArenaEnvironmentVariantSource,
                MeshCollisionExportMode.Query,
                data);

            return data;
        }

        private static ExportCollisionData CollectMovementCollisionDataForLayer(
            Scene activeScene,
            int layer,
            List<string> warnings,
            List<string> errors,
            bool requireArenaEnvironmentVariantSource,
            TiltedBoxExportMode tiltedBoxExportMode)
        {
            var data = new ExportCollisionData();
            data.Boxes.AddRange(CollectExportBoxesForLayer(
                activeScene,
                layer,
                warnings,
                errors,
                requireArenaEnvironmentVariantSource,
                tiltedBoxExportMode));

            CollectMeshCollisionDataForLayer(
                activeScene,
                layer,
                warnings,
                errors,
                requireArenaEnvironmentVariantSource,
                MeshCollisionExportMode.Movement,
                data);

            return data;
        }

        private static void CollectMeshCollisionDataForLayer(
            Scene activeScene,
            int layer,
            List<string> warnings,
            List<string> errors,
            bool requireArenaEnvironmentVariantSource,
            MeshCollisionExportMode mode,
            ExportCollisionData data)
        {
            int sceneUniqueTriangleCount = 0;
            var meshGeometriesById = new Dictionary<string, ExportMeshGeometry>(StringComparer.Ordinal);
            foreach (var collider in UnityEngine.Object.FindObjectsByType<MeshCollider>())
            {
                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy ||
                    collider.gameObject.layer != layer ||
                    ShouldExcludeFromGameplayCollision(collider.gameObject) ||
                    collider.gameObject.scene != activeScene)
                {
                    continue;
                }

                if (requireArenaEnvironmentVariantSource &&
                    !IsAllowedArenaEnvironmentVariantCollider(collider, out string sourcePath))
                {
                    warnings.Add(
                        $"Skipping {GetHierarchyPath(collider.transform)} mesh on {GameplayQueryCollisionLayer}: " +
                        $"source prefab is outside {ArenaEnvironmentVariantPrefix} (source='{sourcePath}').");
                    continue;
                }

                if (TryBuildExportMeshInstance(
                        collider,
                        warnings,
                        errors,
                        mode,
                        meshGeometriesById,
                        ref sceneUniqueTriangleCount,
                        out ExportMeshInstance? exportMeshInstance))
                {
                    data.MeshInstances.Add(exportMeshInstance!);
                }
            }

            data.MeshGeometries.AddRange(meshGeometriesById.Values);
            data.MeshGeometries.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            data.MeshInstances.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        }

        private static bool TryBuildExportBox(
            BoxCollider collider,
            List<string> warnings,
            List<string>? errors,
            TiltedBoxExportMode tiltedBoxExportMode,
            out ExportBox? exportBox)
        {
            Vector3 euler = collider.transform.rotation.eulerAngles;
            float xTilt = DeltaAngleFromZero(euler.x);
            float zTilt = DeltaAngleFromZero(euler.z);
            bool isTilted = Mathf.Abs(xTilt) > 0.01f || Mathf.Abs(zTilt) > 0.01f;
            if (isTilted)
            {
                string message =
                    $"{GetHierarchyPath(collider.transform)} at {FormatVector3(collider.bounds.center)} " +
                    $"has X/Z rotation ({euler.x:F2}, {euler.z:F2}).";
                if (tiltedBoxExportMode == TiltedBoxExportMode.Reject)
                {
                    errors?.Add($"{message} Skipping query export until full-rotation query boxes or mesh collision are supported.");
                    exportBox = null;
                    return false;
                }

                if (tiltedBoxExportMode == TiltedBoxExportMode.WorldAabb)
                    warnings.Add($"{message} Exporting as world AABB.");
            }

            Vector3 center;
            Vector3 size;
            string shape;
            float rotationYDeg;
            Quaternion rotation = Quaternion.identity;
            if (isTilted && tiltedBoxExportMode == TiltedBoxExportMode.WorldAabb)
            {
                center = collider.bounds.center;
                size = collider.bounds.size;
                shape = "aabb";
                rotationYDeg = 0f;
            }
            else if (isTilted)
            {
                center = collider.transform.TransformPoint(collider.center);
                size = Vector3.Scale(collider.size, collider.transform.lossyScale);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                rotation = collider.transform.rotation;
                shape = "obb_xyz";
                rotationYDeg = 0f;
            }
            else
            {
                center = collider.transform.TransformPoint(collider.center);
                size = Vector3.Scale(collider.size, collider.transform.lossyScale);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                shape = "obb_y";
                rotationYDeg = euler.y;
            }

            exportBox = new ExportBox
            {
                name = GetHierarchyPath(collider.transform),
                shape = shape,
                center = new[] { center.x, center.y, center.z },
                size = new[] { size.x, size.y, size.z },
                rotation_y_deg = rotationYDeg,
                rotation = shape == "obb_xyz"
                    ? new[] { rotation.x, rotation.y, rotation.z, rotation.w }
                    : Array.Empty<float>(),
            };
            return true;
        }

        private static bool TryBuildExportMeshInstance(
            MeshCollider collider,
            List<string> warnings,
            List<string> errors,
            MeshCollisionExportMode mode,
            Dictionary<string, ExportMeshGeometry> meshGeometriesById,
            ref int sceneUniqueTriangleCount,
            out ExportMeshInstance? exportMeshInstance)
        {
            exportMeshInstance = null;
            Mesh? mesh = collider.sharedMesh;
            string path = GetHierarchyPath(collider.transform);
            string label = mode == MeshCollisionExportMode.Query ? "mesh query collision" : "movement mesh collision";
            if (mesh == null)
            {
                errors.Add($"Skipping {path} {label}: MeshCollider has no shared mesh.");
                return false;
            }
            if (!mesh.isReadable)
            {
                errors.Add($"Skipping {path} {label}: mesh asset is not readable.");
                return false;
            }

            string meshAssetPath = AssetDatabase.GetAssetPath(mesh);
            string meshAssetGuid = string.IsNullOrEmpty(meshAssetPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(meshAssetPath);
            if (string.IsNullOrEmpty(meshAssetGuid))
            {
                errors.Add($"Skipping {path} {label}: mesh asset has no stable AssetDatabase GUID.");
                return false;
            }

            int[] indices;
            Vector3[] localVertices;
            try
            {
                indices = mesh.triangles;
                localVertices = mesh.vertices;
            }
            catch (Exception ex)
            {
                errors.Add($"Skipping {path} {label}: failed to read mesh data ({ex.GetType().Name}).");
                return false;
            }

            if (indices.Length == 0 || localVertices.Length == 0)
            {
                warnings.Add($"Skipping {path} {label}: mesh is empty.");
                return false;
            }
            if (indices.Length % 3 != 0)
            {
                errors.Add($"Skipping {path} {label}: mesh index count {indices.Length} is not divisible by 3.");
                return false;
            }

            var cleanedIndices = new List<int>(indices.Length);
            int degenerateTriangleCount = 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= localVertices.Length || b >= localVertices.Length || c >= localVertices.Length)
                {
                    errors.Add($"Skipping {path} {label}: mesh contains an out-of-range index.");
                    return false;
                }

                if (IsDegenerateTriangle(localVertices[a], localVertices[b], localVertices[c]))
                {
                    degenerateTriangleCount++;
                    continue;
                }

                cleanedIndices.Add(a);
                cleanedIndices.Add(b);
                cleanedIndices.Add(c);
            }

            int triangleCount = cleanedIndices.Count / 3;
            if (triangleCount == 0)
            {
                warnings.Add($"Skipping {path} {label}: mesh has no non-degenerate triangles.");
                return false;
            }
            if (degenerateTriangleCount > 0)
            {
                warnings.Add(
                    $"{path} {label} removed {degenerateTriangleCount} degenerate triangle(s) before export.");
            }
            int maxTrianglesPerCollider = mode == MeshCollisionExportMode.Query
                ? MaxQueryMeshTrianglesPerCollider
                : MaxMovementMeshTrianglesPerCollider;
            int maxTrianglesPerScene = mode == MeshCollisionExportMode.Query
                ? MaxQueryMeshTrianglesPerScene
                : MaxMovementMeshTrianglesPerScene;
            if (triangleCount > maxTrianglesPerCollider)
            {
                errors.Add(
                    $"Skipping {path} {label}: {triangleCount} triangle(s) exceeds per-collider budget " +
                    $"{maxTrianglesPerCollider}.");
                return false;
            }

            string geometryId = $"{meshAssetGuid}:{mesh.name}:{localVertices.Length}:{triangleCount}";
            if (!meshGeometriesById.ContainsKey(geometryId) &&
                sceneUniqueTriangleCount + triangleCount > maxTrianglesPerScene)
            {
                errors.Add(
                    $"Skipping {path} {label}: scene unique mesh triangle budget would exceed " +
                    $"{maxTrianglesPerScene}.");
                return false;
            }

            if (!meshGeometriesById.ContainsKey(geometryId))
            {
                var vertices = new float[localVertices.Length * 3];
                for (int i = 0; i < localVertices.Length; i++)
                {
                    Vector3 local = localVertices[i];
                    if (!IsFinite(local))
                    {
                        errors.Add($"Skipping {path} {label}: mesh contains non-finite vertex data.");
                        return false;
                    }

                    int output = i * 3;
                    vertices[output] = local.x;
                    vertices[output + 1] = local.y;
                    vertices[output + 2] = local.z;
                }

                meshGeometriesById.Add(geometryId, new ExportMeshGeometry
                {
                    id = geometryId,
                    source = string.IsNullOrEmpty(meshAssetPath) ? mesh.name : meshAssetPath,
                    vertex_count = localVertices.Length,
                    triangle_count = triangleCount,
                    vertices = vertices,
                    indices = cleanedIndices.ToArray(),
                });
                sceneUniqueTriangleCount += triangleCount;
            }

            exportMeshInstance = new ExportMeshInstance
            {
                name = path,
                geometry_id = geometryId,
                transform = MatrixToRowMajorArray(collider.transform.localToWorldMatrix),
            };
            return true;
        }

        private static bool IsDegenerateTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                return false;
            return Vector3.Cross(b - a, c - a).sqrMagnitude <= QueryMeshDegenerateTriangleAreaSquaredEpsilon;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static float[] MatrixToRowMajorArray(Matrix4x4 matrix)
            => new[]
            {
                matrix.m00, matrix.m01, matrix.m02, matrix.m03,
                matrix.m10, matrix.m11, matrix.m12, matrix.m13,
                matrix.m20, matrix.m21, matrix.m22, matrix.m23,
                matrix.m30, matrix.m31, matrix.m32, matrix.m33,
            };

        private static bool IsAllowedArenaEnvironmentVariantCollider(Collider collider, out string sourcePath)
        {
            GameObject? prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(collider.gameObject);
            sourcePath = prefabRoot != null
                ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot)
                : string.Empty;

            return sourcePath.StartsWith(ArenaEnvironmentVariantPrefix, StringComparison.Ordinal) &&
                   sourcePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static void LogExportWarnings(IEnumerable<string> warnings)
        {
            List<string> distinct = warnings.Distinct(StringComparer.Ordinal).ToList();
            foreach (string warning in distinct.Take(MaxExportDiagnosticsToLog))
                Debug.LogWarning($"[GameplayCollisionExporter] {warning}");
            if (distinct.Count > MaxExportDiagnosticsToLog)
            {
                Debug.LogWarning(
                    $"[GameplayCollisionExporter] ...and {distinct.Count - MaxExportDiagnosticsToLog} more warning(s).");
            }
        }

        private static void LogExportErrors(IEnumerable<string> errors)
        {
            List<string> distinct = errors.Distinct(StringComparer.Ordinal).ToList();
            foreach (string error in distinct.Take(MaxExportDiagnosticsToLog))
                Debug.LogError($"[GameplayCollisionExporter] {error}");
            if (distinct.Count > MaxExportDiagnosticsToLog)
            {
                Debug.LogError(
                    $"[GameplayCollisionExporter] ...and {distinct.Count - MaxExportDiagnosticsToLog} more error(s).");
            }
        }

        private static void WriteExportLayout(string serverPath, string bundledPath, List<ExportBox> boxes)
            => WriteExportLayout(
                serverPath,
                bundledPath,
                boxes,
                Array.Empty<ExportMeshGeometry>(),
                Array.Empty<ExportMeshInstance>());

        private static void WriteExportLayout(string serverPath, string bundledPath, ExportCollisionData data)
            => WriteExportLayout(serverPath, bundledPath, data.Boxes, data.MeshGeometries, data.MeshInstances);

        private static void WriteExportLayout(
            string serverPath,
            string bundledPath,
            IReadOnlyCollection<ExportBox> boxes,
            IReadOnlyCollection<ExportMeshGeometry> meshGeometries,
            IReadOnlyCollection<ExportMeshInstance> meshInstances)
        {
            var layout = new ExportLayout
            {
                version = 1,
                boxes = boxes.ToArray(),
                mesh_geometries = meshGeometries.ToArray(),
                mesh_instances = meshInstances.ToArray(),
            };

            string json = JsonUtility.ToJson(layout, true);
            WriteProjectText(serverPath, json);
            WriteProjectText(bundledPath, json);
        }

        private static string BuildSceneDataKey(string sceneName)
        {
            var chars = sceneName
                .Trim()
                .ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            string key = new string(chars);
            while (key.Contains("__", StringComparison.Ordinal))
                key = key.Replace("__", "_", StringComparison.Ordinal);
            return key.Trim('_');
        }

        private static string SceneServerCollisionPath(string dataKey)
            => $"{RelativeServerWorldDataDirectory}/{dataKey}.collision.shared.json";

        private static string SceneBundledCollisionPath(string dataKey)
            => $"{RelativeBundledWorldDataDirectory}/{dataKey}.collision.shared.json";

        private static string SceneServerQueryCollisionPath(string dataKey)
            => $"{RelativeServerWorldDataDirectory}/{dataKey}.query_collision.shared.json";

        private static string SceneBundledQueryCollisionPath(string dataKey)
            => $"{RelativeBundledWorldDataDirectory}/{dataKey}.query_collision.shared.json";

        private static string SceneServerHeightfieldPath(string dataKey)
            => $"{RelativeServerWorldDataDirectory}/{dataKey}.heightfield.shared.json";

        private static string SceneBundledHeightfieldPath(string dataKey)
            => $"{RelativeBundledWorldDataDirectory}/{dataKey}.heightfield.shared.json";

        private static void CopyProjectFile(string relativeSourcePath, string relativeDestinationPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string sourcePath = Path.Combine(projectRoot, relativeSourcePath);
            string destinationPath = Path.Combine(projectRoot, relativeDestinationPath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Required shared data file not found: {relativeSourcePath}",
                    sourcePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static void CopyProjectDirectory(string relativeSourceDirectory, string relativeDestinationDirectory)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string sourceDirectory = Path.Combine(projectRoot, relativeSourceDirectory);
            string destinationDirectory = Path.Combine(projectRoot, relativeDestinationDirectory);
            if (!Directory.Exists(sourceDirectory))
                return;

            Directory.CreateDirectory(destinationDirectory);
            foreach (string sourcePath in Directory.GetFiles(sourceDirectory))
            {
                string fileName = Path.GetFileName(sourcePath);
                string destinationPath = Path.Combine(destinationDirectory, fileName);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        private static void WriteProjectText(string relativePath, string contents)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string outputPath = Path.Combine(projectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, contents);
        }
    }
}
#endif
