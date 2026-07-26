#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Arena.Interaction;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    public static class InteractiveGatewayPrefabBuilder
    {
        private const string SourceRoot =
            "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs";
        private const string DestinationRoot =
            "Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Interactables/Gateways";

        private readonly struct VariantDefinition
        {
            public VariantDefinition(
                string name,
                string sourceRelativePath,
                string[] leafNames,
                float[] openYaws)
            {
                Name = name;
                SourcePath = $"{SourceRoot}/{sourceRelativePath}";
                LeafNames = leafNames;
                OpenYaws = openYaws;
            }

            public string Name { get; }
            public string SourcePath { get; }
            public string[] LeafNames { get; }
            public float[] OpenYaws { get; }
        }

        private static readonly VariantDefinition[] Definitions =
        {
            new(
                "COMP_Door_01_med_01_Arena",
                "MODULAR/02_COMPS/Gateway/COMP_Door_01_med_01.prefab",
                new[] { "MOD_Gateway_Door_01_med_01_door" },
                new[] { 95f }),
            new(
                "COMP_Door_01_med_02_Arena",
                "MODULAR/02_COMPS/Gateway/COMP_Door_01_med_02.prefab",
                new[] { "MOD_Gateway_Door_01_med_02_door" },
                new[] { 95f }),
            new(
                "COMP_Door_01_large_Arena",
                "MODULAR/02_COMPS/Gateway/COMP_Door_01_large.prefab",
                new[]
                {
                    "MOD_Gateway_Door_01_large_door_L",
                    "MOD_Gateway_Door_01_large_door_R",
                },
                new[] { 100f, -100f }),
            new(
                "P_PROP_bars_doorway_dungeon_01_Arena",
                "PROPS/construction/P_PROP_bars_doorway_dungeon_01.prefab",
                new[] { "SM_PROP_bars_door_01_dungeon" },
                new[] { -75f }),
        };

        [MenuItem("Arena/Dungeons/Build Interactive Gateway Variants", false, 115)]
        public static void BuildAllFromMenu()
        {
            IReadOnlyList<string> paths = BuildAll();
            EditorUtility.DisplayDialog(
                "Interactive Gateway Variants",
                $"Built {paths.Count} Arena-owned variants under {DestinationRoot}.",
                "OK");
        }

        public static IReadOnlyList<string> BuildAll()
        {
            EnsureFolder(DestinationRoot);
            var output = new List<string>(Definitions.Length);
            foreach (VariantDefinition definition in Definitions)
                output.Add(Build(definition));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return output;
        }

        private static string Build(VariantDefinition definition)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(
                definition.SourcePath);
            if (source == null)
                throw new InvalidOperationException($"Missing gateway prefab: {definition.SourcePath}");

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            try
            {
                instance.name = definition.Name;
                var poses = new DoorAuthoring.LeafPose[definition.LeafNames.Length];
                Bounds localBounds = default;
                bool hasBounds = false;
                for (int i = 0; i < definition.LeafNames.Length; i++)
                {
                    Transform leaf = FindRequiredDescendant(
                        instance.transform,
                        definition.LeafNames[i]);
                    Quaternion closed = leaf.localRotation;
                    Quaternion open = Quaternion.Euler(0f, definition.OpenYaws[i], 0f);
                    EncapsulateRenderersInRootSpace(
                        leaf,
                        instance.transform,
                        ref localBounds,
                        ref hasBounds);
                    leaf.localRotation = open;
                    foreach (Collider collider in leaf.GetComponentsInChildren<Collider>(true))
                        collider.enabled = false;
                    foreach (Transform child in leaf.GetComponentsInChildren<Transform>(true))
                        GameObjectUtility.SetStaticEditorFlags(child.gameObject, 0);
                    poses[i] = new DoorAuthoring.LeafPose(leaf, closed, open);
                }

                if (!hasBounds)
                    throw new InvalidOperationException($"{definition.Name} has no renderable leaf bounds.");

                bool widthAlongX = localBounds.size.x >= localBounds.size.z;
                float width = widthAlongX ? localBounds.size.x : localBounds.size.z;
                float thickness = Mathf.Max(
                    0.2f,
                    widthAlongX ? localBounds.size.z : localBounds.size.x);
                float blockerYaw = widthAlongX ? 0f : 90f;
                Vector3 blockerSize = new(
                    Mathf.Max(0.5f, width),
                    Mathf.Max(1f, localBounds.size.y),
                    Mathf.Min(0.6f, thickness));

                DoorAuthoring authoring = instance.AddComponent<DoorAuthoring>();
                authoring.Configure(
                    $"TEMPLATE:{definition.Name}",
                    "TEMPLATE",
                    templateOnly: true,
                    productionEnabled: false,
                    defaultOpen: true,
                    definitionVersion: 1,
                    openInteractionProfileId: "WORLD_DOOR_INSTANT",
                    closeInteractionProfileId: "WORLD_DOOR_INSTANT",
                    interactionAnchorLocal: localBounds.center,
                    maxInteractionDistance: 3.25f,
                    closedBlockerCenterLocal: localBounds.center,
                    closedBlockerSize: blockerSize,
                    closedBlockerLocalYaw: blockerYaw,
                    poses);
                DoorMotor motor = instance.AddComponent<DoorMotor>();
                motor.Configure(authoring);
                DoorInteractable interactable = instance.AddComponent<DoorInteractable>();
                interactable.Configure(authoring, motor);

                var hitboxObject = new GameObject("InteractionHitbox");
                hitboxObject.transform.SetParent(instance.transform, false);
                hitboxObject.transform.localPosition = localBounds.center;
                hitboxObject.transform.localRotation =
                    Quaternion.Euler(0f, blockerYaw, 0f);
                BoxCollider hitboxCollider = hitboxObject.AddComponent<BoxCollider>();
                hitboxCollider.isTrigger = true;
                hitboxCollider.size = new Vector3(
                    blockerSize.x,
                    blockerSize.y,
                    Mathf.Max(0.6f, blockerSize.z));
                WorldInteractionHitbox hitbox =
                    hitboxObject.AddComponent<WorldInteractionHitbox>();
                hitbox.Configure(interactable);

                string destination = $"{DestinationRoot}/{definition.Name}.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, destination, out bool success);
                if (!success)
                    throw new InvalidOperationException($"Failed to save gateway variant: {destination}");
                return destination;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static Transform FindRequiredDescendant(Transform root, string exactName)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, exactName, StringComparison.Ordinal))
                    return child;
            }

            throw new InvalidOperationException(
                $"Gateway '{root.name}' is missing leaf '{exactName}'.");
        }

        private static void EncapsulateRenderersInRootSpace(
            Transform leaf,
            Transform root,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            foreach (Renderer renderer in leaf.GetComponentsInChildren<Renderer>(true))
            {
                Bounds world = renderer.bounds;
                Vector3 min = world.min;
                Vector3 max = world.max;
                for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                {
                    Vector3 corner = new(
                        x == 0 ? min.x : max.x,
                        y == 0 ? min.y : max.y,
                        z == 0 ? min.z : max.z);
                    Vector3 local = root.InverseTransformPoint(corner);
                    if (!hasBounds)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }
        }

        private static void EnsureFolder(string folderPath)
        {
            folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
            if (Directory.Exists(folderPath))
                return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/')
                ?? string.Empty;
            string leaf = Path.GetFileName(folderPath);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
