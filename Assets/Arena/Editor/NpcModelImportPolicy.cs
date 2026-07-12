#nullable enable
using System;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Keeps root-motion extraction settings for ignored third-party NPC models
    /// reproducible across package reimports and developer machines.
    /// </summary>
    public sealed class NpcModelImportPolicy : AssetPostprocessor
    {
        // Vendor hit clips that bake a non-returning root-bone slide (verified
        // against the FBX curves: the root translates backward and never
        // returns, so the mesh walks out from under the replication-pinned NPC
        // root). Models whose root-bone motion is intentional and returning
        // (e.g. the hovering Imp) must NOT be listed here.
        private static readonly (string ModelPath, string ClipName, string RootBonePath)[] InPlaceOverrides =
        {
            ("Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle/Meshes/SlimeMan/SlimeMan.fbx",
                "hit", "Root"),
            ("Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle/Meshes/ForestDemon/ForestDemon.fbx",
                "hit", "Root"),
            ("Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle2/Meshes/Abomination/Abomination.fbx",
                "hit", "Root"),
        };

        // Bump whenever the policy's output changes so the asset database
        // invalidates cached import artifacts instead of serving stale ones.
        public override uint GetVersion() => 2;

        private void OnPostprocessAnimation(GameObject root, AnimationClip clip)
        {
            foreach ((string modelPath, string clipName, string rootBonePath) in InPlaceOverrides)
            {
                if (!string.Equals(assetPath, modelPath, StringComparison.Ordinal)
                    || !string.Equals(clip.name, clipName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int flattenedCurveCount = MakeTransformTranslationInPlace(
                    clip,
                    rootBonePath,
                    horizontalOnly: true);
                if (flattenedCurveCount != 2)
                {
                    Debug.LogWarning(
                        $"Expected to flatten two '{clipName}' root-position curves on {modelPath}, but found {flattenedCurveCount}.",
                        root);
                }
            }
        }

        internal static int MakeTransformTranslationInPlace(
            AnimationClip clip,
            string transformPath,
            bool horizontalOnly)
        {
            int flattenedCurveCount = 0;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (binding.type != typeof(Transform)
                    || !string.Equals(binding.path, transformPath, StringComparison.Ordinal)
                    || !IsPositionCurve(binding.propertyName, horizontalOnly))
                {
                    continue;
                }

                AnimationCurve? source = AnimationUtility.GetEditorCurve(clip, binding);
                if (source == null || source.length == 0)
                    continue;

                float initialValue = source.Evaluate(0f);
                var inPlace = AnimationCurve.Constant(0f, clip.length, initialValue);
                inPlace.preWrapMode = source.preWrapMode;
                inPlace.postWrapMode = source.postWrapMode;
                AnimationUtility.SetEditorCurve(clip, binding, inPlace);
                flattenedCurveCount++;
            }

            return flattenedCurveCount;
        }

        private static bool IsPositionCurve(string propertyName, bool horizontalOnly)
        {
            if (string.Equals(propertyName, "m_LocalPosition.x", StringComparison.Ordinal)
                || string.Equals(propertyName, "m_LocalPosition.z", StringComparison.Ordinal))
            {
                return true;
            }

            return !horizontalOnly
                && string.Equals(propertyName, "m_LocalPosition.y", StringComparison.Ordinal);
        }
    }
}
