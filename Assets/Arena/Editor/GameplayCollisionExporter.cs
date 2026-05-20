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
        private const string RelativeServerGameplayCollisionPath = "server/src/gameplay_collision.shared.json";
        private const string RelativeServerArenaLayoutPath = "server/src/arena_layout.shared.json";
        private const string RelativeBundledGameplayCollisionPath = "Assets/Arena/Resources/SharedData/gameplay_collision.shared.json";
        private const string RelativeBundledArenaLayoutPath = "Assets/Arena/Resources/SharedData/arena_layout.shared.json";
        private const string RelativeServerWorldDataDirectory = "server/src/world_data";
        private const string RelativeBundledWorldDataDirectory = "Assets/Arena/Resources/SharedData/Worlds";

        [Serializable]
        private sealed class ExportLayout
        {
            public int version = 1;
            public ExportBox[] boxes = Array.Empty<ExportBox>();
        }

        [Serializable]
        private sealed class ExportBox
        {
            public string name = "";
            public string shape = "obb_y";
            public float[] center = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public float rotation_y_deg;
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
            int layer = LayerMask.NameToLayer(GameplayCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayCollisionLayer}' does not exist.");
                return;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                Debug.LogError("[GameplayCollisionExporter] No active scene loaded.");
                return;
            }

            var boxes = new List<ExportBox>();
            var warnings = new List<string>();

            foreach (var collider in UnityEngine.Object.FindObjectsByType<BoxCollider>())
            {
                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy ||
                    collider.gameObject.layer != layer ||
                    collider.gameObject.scene != activeScene)
                {
                    continue;
                }

                Vector3 euler = collider.transform.rotation.eulerAngles;
                float xTilt = DeltaAngleFromZero(euler.x);
                float zTilt = DeltaAngleFromZero(euler.z);
                bool isTilted = Mathf.Abs(xTilt) > 0.01f || Mathf.Abs(zTilt) > 0.01f;
                if (isTilted)
                {
                    warnings.Add(
                        $"{GetHierarchyPath(collider.transform)} at {FormatVector3(collider.bounds.center)} " +
                        $"has X/Z rotation ({euler.x:F2}, {euler.z:F2}); exporting as world AABB.");
                }

                Vector3 center;
                Vector3 size;
                string shape;
                float rotationYDeg;
                if (isTilted)
                {
                    center = collider.bounds.center;
                    size = collider.bounds.size;
                    shape = "aabb";
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

                boxes.Add(new ExportBox
                {
                    name = GetHierarchyPath(collider.transform),
                    shape = shape,
                    center = new[] { center.x, center.y, center.z },
                    size = new[] { size.x, size.y, size.z },
                    rotation_y_deg = rotationYDeg,
                });
            }

            boxes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            var layout = new ExportLayout
            {
                version = 1,
                boxes = boxes.ToArray(),
            };

            string json = JsonUtility.ToJson(layout, true);
            WriteProjectText(RelativeServerGameplayCollisionPath, json);
            WriteProjectText(RelativeBundledGameplayCollisionPath, json);
            SyncArenaLayoutToBundled(logSummary: false);
            AssetDatabase.Refresh();

            Debug.Log(
                $"[GameplayCollisionExporter] Exported {boxes.Count} box colliders to " +
                $"{RelativeServerGameplayCollisionPath} and {RelativeBundledGameplayCollisionPath}");
            foreach (string warning in warnings.Distinct())
                Debug.LogWarning($"[GameplayCollisionExporter] {warning}");
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
            if (logSummary)
            {
                Debug.Log(
                    $"[GameplayCollisionExporter] Synced {RelativeServerGameplayCollisionPath} -> {RelativeBundledGameplayCollisionPath}");
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

        private static void ExportSceneGameplayCollision(Scene activeScene, string dataKey)
        {
            int layer = LayerMask.NameToLayer(GameplayCollisionLayer);
            if (layer < 0)
            {
                Debug.LogError($"[GameplayCollisionExporter] Layer '{GameplayCollisionLayer}' does not exist.");
                return;
            }

            var boxes = new List<ExportBox>();
            var warnings = new List<string>();

            foreach (var collider in UnityEngine.Object.FindObjectsByType<BoxCollider>())
            {
                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy ||
                    collider.gameObject.layer != layer ||
                    collider.gameObject.scene != activeScene)
                {
                    continue;
                }

                Vector3 euler = collider.transform.rotation.eulerAngles;
                float xTilt = DeltaAngleFromZero(euler.x);
                float zTilt = DeltaAngleFromZero(euler.z);
                bool isTilted = Mathf.Abs(xTilt) > 0.01f || Mathf.Abs(zTilt) > 0.01f;
                if (isTilted)
                {
                    warnings.Add(
                        $"{GetHierarchyPath(collider.transform)} at {FormatVector3(collider.bounds.center)} " +
                        $"has X/Z rotation ({euler.x:F2}, {euler.z:F2}); exporting as world AABB.");
                }

                Vector3 center;
                Vector3 size;
                string shape;
                float rotationYDeg;
                if (isTilted)
                {
                    center = collider.bounds.center;
                    size = collider.bounds.size;
                    shape = "aabb";
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

                boxes.Add(new ExportBox
                {
                    name = GetHierarchyPath(collider.transform),
                    shape = shape,
                    center = new[] { center.x, center.y, center.z },
                    size = new[] { size.x, size.y, size.z },
                    rotation_y_deg = rotationYDeg,
                });
            }

            boxes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            var layout = new ExportLayout
            {
                version = 1,
                boxes = boxes.ToArray(),
            };

            string json = JsonUtility.ToJson(layout, true);
            WriteProjectText(SceneServerCollisionPath(dataKey), json);
            WriteProjectText(SceneBundledCollisionPath(dataKey), json);

            Debug.Log(
                $"[GameplayCollisionExporter] Exported {boxes.Count} scene box colliders to " +
                $"{SceneServerCollisionPath(dataKey)} and {SceneBundledCollisionPath(dataKey)}");
            foreach (string warning in warnings.Distinct())
                Debug.LogWarning($"[GameplayCollisionExporter] {warning}");
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
