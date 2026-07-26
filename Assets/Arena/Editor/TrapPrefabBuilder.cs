#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Interaction;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Builds the Arena-owned trap prefabs that wrap the ToonDesertedTemples
    /// vendor traps. The wrapper exists so the manifest only ever needs an
    /// origin and a yaw: every vendor-specific offset and roll (the spike
    /// plate's 2 u tiling, the wall arm's Z=90 mount) is baked into the child.
    ///
    /// The vendor prefabs carry no colliders and the wrapper adds none: traps
    /// never block movement, sight, or projectiles.
    /// </summary>
    public static class TrapPrefabBuilder
    {
        public const string DestinationRoot =
            "Assets/Arena/Content/Prefabs/Dungeons/ToonDesertedTemples/Traps";

        private const string VendorRoot =
            "Assets/ThirdParty/AssetStore/Environments/ToonDesertedTemples/Prefabs/Buildings/Traps";
        private const string ProfileRoot = "Assets/Arena/Content/Settings/Traps";

        private readonly struct VendorChild
        {
            public VendorChild(Vector3 localPosition, Vector3 localEuler)
            {
                LocalPosition = localPosition;
                LocalEuler = localEuler;
            }

            public Vector3 LocalPosition { get; }
            public Vector3 LocalEuler { get; }
        }

        private readonly struct TrapVariant
        {
            public TrapVariant(
                string name,
                string vendorPrefab,
                string profileAsset,
                int footprintCells,
                VendorChild[] children)
            {
                Name = name;
                VendorPrefabPath = $"{VendorRoot}/{vendorPrefab}.prefab";
                ProfileAssetPath = $"{ProfileRoot}/{profileAsset}.asset";
                FootprintCells = footprintCells;
                Children = children;
            }

            public string Name { get; }
            public string VendorPrefabPath { get; }
            public string ProfileAssetPath { get; }
            public int FootprintCells { get; }
            public VendorChild[] Children { get; }
        }

        /// <summary>
        /// The spike plate measures 2 u square and the diorama tiles it on a 2 u
        /// pitch, so one 4 u dungeon cell holds a 2x2 field of four plates. All
        /// four share the phase, which is what makes the whole cell one hazard.
        /// </summary>
        private static readonly VendorChild[] SpikeField =
        {
            new(new Vector3(-1f, 0f, -1f), Vector3.zero),
            new(new Vector3(1f, 0f, -1f), Vector3.zero),
            new(new Vector3(-1f, 0f, 1f), Vector3.zero),
            new(new Vector3(1f, 0f, 1f), Vector3.zero),
        };

        /// <summary>
        /// Mount height for the wall arm, from the diorama placement (y = 1.393
        /// with a Z=90 roll). The roll turns the arm's local X spin axis into
        /// world up, so the blade sweeps a horizontal circle of radius 2 u at
        /// this height — chest height for a 1.8 u capsule.
        /// </summary>
        private const float WallArmMountHeight = 1.39f;

        private static readonly TrapVariant[] Variants =
        {
            new("TRAP_SPIKES_Arena", "TFD_Floor_Trap_01A", "TrapSpikes", 1, SpikeField),
            new(
                "TRAP_SAW_SWEEP_Arena",
                "TFD_Trap_01A",
                "TrapSawSweep",
                2,
                new[] { new VendorChild(Vector3.zero, Vector3.zero) }),
            new(
                "TRAP_SAW_POST_Arena",
                "TFD_Trap_02A",
                "TrapSawPost",
                1,
                new[] { new VendorChild(Vector3.zero, Vector3.zero) }),
            new(
                "TRAP_SAW_ARM_Arena",
                "TFD_Trap_03A",
                "TrapSawArm",
                6,
                new[]
                {
                    new VendorChild(
                        new Vector3(0f, WallArmMountHeight, 0f),
                        new Vector3(0f, 0f, 90f)),
                }),
        };

        public static IReadOnlyList<string> VariantNames =>
            Variants.Select(variant => variant.Name).ToArray();

        [MenuItem("Arena/Dungeons/Build Trap Variants", false, 116)]
        public static void BuildAllFromMenu()
        {
            IReadOnlyList<string> paths = BuildAll();
            EditorUtility.DisplayDialog(
                "Trap Variants",
                $"Built {paths.Count} Arena-owned trap variants under {DestinationRoot}.",
                "OK");
        }

        public static IReadOnlyList<string> BuildAll()
        {
            EnsureFolder(DestinationRoot);
            var output = new List<string>(Variants.Length);
            foreach (TrapVariant variant in Variants)
                output.Add(Build(variant));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return output;
        }

        public static string PrefabPathFor(string profileId)
        {
            foreach (TrapVariant variant in Variants)
            {
                TrapProfile? profile =
                    AssetDatabase.LoadAssetAtPath<TrapProfile>(variant.ProfileAssetPath);
                if (profile != null
                    && string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal))
                {
                    return $"{DestinationRoot}/{variant.Name}.prefab";
                }
            }

            throw new InvalidOperationException($"No Arena trap variant for profile '{profileId}'.");
        }

        private static string Build(TrapVariant variant)
        {
            GameObject vendor = AssetDatabase.LoadAssetAtPath<GameObject>(variant.VendorPrefabPath);
            if (vendor == null)
                throw new InvalidOperationException($"Missing vendor trap prefab: {variant.VendorPrefabPath}");

            TrapProfile? profile =
                AssetDatabase.LoadAssetAtPath<TrapProfile>(variant.ProfileAssetPath);
            if (profile == null)
                throw new InvalidOperationException($"Missing trap profile: {variant.ProfileAssetPath}");

            var root = new GameObject(variant.Name);
            try
            {
                var animators = new List<Animator>(variant.Children.Length);
                foreach (VendorChild child in variant.Children)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(vendor);
                    instance.transform.SetParent(root.transform, worldPositionStays: false);
                    instance.transform.localPosition = child.LocalPosition;
                    instance.transform.localRotation = Quaternion.Euler(child.LocalEuler);
                    instance.transform.localScale = Vector3.one;

                    foreach (Transform descendant in instance.GetComponentsInChildren<Transform>(true))
                        GameObjectUtility.SetStaticEditorFlags(descendant.gameObject, 0);

                    Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
                    if (colliders.Length > 0)
                    {
                        throw new InvalidOperationException(
                            $"Vendor trap '{variant.VendorPrefabPath}' grew {colliders.Length} collider(s); "
                            + "traps must never contribute collision.");
                    }

                    Animator[] childAnimators = instance.GetComponentsInChildren<Animator>(true);
                    if (childAnimators.Length != 1)
                    {
                        throw new InvalidOperationException(
                            $"Vendor trap '{variant.VendorPrefabPath}' has {childAnimators.Length} animators; expected exactly 1.");
                    }
                    animators.Add(childAnimators[0]);
                }

                TrapAuthoring authoring = root.AddComponent<TrapAuthoring>();
                authoring.Configure(
                    trapDefinitionId: string.Empty,
                    worldDefinitionKey: "RANDOM_DUNGEON",
                    templateOnly: true,
                    productionEnabled: false,
                    definitionVersion: 1,
                    footprintCells: variant.FootprintCells,
                    profile: profile);

                TrapPresenter presenter = root.AddComponent<TrapPresenter>();
                presenter.Configure(authoring, animators.ToArray());

                string destination = $"{DestinationRoot}/{variant.Name}.prefab";
                PrefabUtility.SaveAsPrefabAsset(root, destination);
                return destination;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
