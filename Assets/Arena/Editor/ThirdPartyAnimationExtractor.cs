#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    public static class ThirdPartyAnimationExtractor
    {
        private const string DefaultSourceRoot = "Assets/ThirdParty/AssetStore/Animation";
        private const string DefaultDestRoot = "Assets/Arena/Content/Animation/Extracted";

        [MenuItem("Arena/Animation/Extract Third-Party Clips")]
        public static void ExtractAllFromMenu()
        {
            ExtractFromRoot(DefaultSourceRoot, DefaultDestRoot);
        }

        public static void ExtractFromRoot(string sourceRoot, string destRoot)
        {
            if (!AssetDatabase.IsValidFolder(sourceRoot))
            {
                Debug.LogError($"[ThirdPartyAnimationExtractor] Source folder not found: {sourceRoot}");
                return;
            }

            string[] modelGuids = AssetDatabase.FindAssets("t:Model", new[] { sourceRoot });
            string[] clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { sourceRoot });
            int extractedFromFbx = 0;
            int copiedStandalone = 0;
            int skippedExisting = 0;
            int fbxWithNoClips = 0;
            int fbxWithClips = 0;
            int totalSteps = modelGuids.Length + clipGuids.Length;
            int step = 0;

            try
            {
                AssetDatabase.StartAssetEditing();

                // Pass 1: FBX-embedded clips. Use Object.Instantiate + CreateAsset so each
                // standalone copy is independent of the source FBX (events authored later
                // are stored on the .anim, not the FBX's .meta).
                for (int i = 0; i < modelGuids.Length; i++)
                {
                    string fbxPath = AssetDatabase.GUIDToAssetPath(modelGuids[i]).Replace('\\', '/');
                    step++;
                    if (!fbxPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Extracting third-party animation clips",
                            $"{step}/{totalSteps}: {Path.GetFileName(fbxPath)}",
                            step / (float)totalSteps))
                    {
                        Debug.LogWarning("[ThirdPartyAnimationExtractor] Canceled by user.");
                        return;
                    }

                    UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                    bool fbxYieldedClip = false;

                    foreach (UnityEngine.Object asset in subAssets)
                    {
                        if (asset is not AnimationClip clip)
                            continue;
                        if (clip.name.StartsWith("__", StringComparison.Ordinal))
                            continue; // Unity preview/internal clips
                        if ((clip.hideFlags & HideFlags.HideInHierarchy) != 0)
                            continue;

                        fbxYieldedClip = true;

                        string destFolder = ResolveDestFolder(fbxPath, sourceRoot, destRoot);
                        string destPath = $"{destFolder}/{SanitizeFileName(clip.name)}.anim";

                        // Never overwrite an existing extracted clip: it may carry authored
                        // animation events that the source does not. If you intentionally
                        // want to refresh a clip from source, delete the .anim manually first.
                        if (File.Exists(destPath))
                        {
                            skippedExisting++;
                            continue;
                        }

                        EnsureFolder(destFolder);

                        AnimationClip copy = UnityEngine.Object.Instantiate(clip);
                        copy.name = clip.name;
                        AssetDatabase.CreateAsset(copy, destPath);
                        extractedFromFbx++;
                    }

                    if (fbxYieldedClip)
                        fbxWithClips++;
                    else
                        fbxWithNoClips++;
                }

                // Pass 2: standalone .anim files (e.g. ArcherAnimationPack, GreatSwordAnimations,
                // SwordAndShieldAnimationPack ship pre-extracted clips, not FBX-embedded). Copy
                // them to the destination so authored events live in our tree, not the third
                // party's. AssetDatabase.FindAssets("t:AnimationClip") returns both FBX-embedded
                // and standalone; we filter to standalone by extension here.
                for (int i = 0; i < clipGuids.Length; i++)
                {
                    string srcPath = AssetDatabase.GUIDToAssetPath(clipGuids[i]).Replace('\\', '/');
                    step++;
                    if (!srcPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Extracting third-party animation clips",
                            $"{step}/{totalSteps}: {Path.GetFileName(srcPath)}",
                            step / (float)totalSteps))
                    {
                        Debug.LogWarning("[ThirdPartyAnimationExtractor] Canceled by user.");
                        return;
                    }

                    string destFolder = ResolveDestFolder(srcPath, sourceRoot, destRoot);
                    string destPath = $"{destFolder}/{Path.GetFileName(srcPath)}";

                    if (File.Exists(destPath))
                    {
                        skippedExisting++;
                        continue;
                    }

                    EnsureFolder(destFolder);

                    if (AssetDatabase.CopyAsset(srcPath, destPath))
                        copiedStandalone++;
                    else
                        Debug.LogWarning($"[ThirdPartyAnimationExtractor] CopyAsset failed: {srcPath} -> {destPath}");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log(
                $"[ThirdPartyAnimationExtractor] Done. " +
                $"FBXs scanned={modelGuids.Length} withClips={fbxWithClips} noClips={fbxWithNoClips}. " +
                $"Clips extractedFromFbx={extractedFromFbx} copiedStandalone={copiedStandalone} skippedExisting={skippedExisting}. " +
                $"Source={sourceRoot} Destination={destRoot}");
        }

        private static string ResolveDestFolder(string fbxPath, string sourceRoot, string destRoot)
        {
            string normalizedSource = sourceRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string relative = fbxPath.StartsWith(normalizedSource, StringComparison.Ordinal)
                ? fbxPath.Substring(normalizedSource.Length)
                : Path.GetFileName(fbxPath);

            string relativeFolder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
            string destBase = destRoot.Replace('\\', '/').TrimEnd('/');
            return string.IsNullOrEmpty(relativeFolder)
                ? destBase
                : $"{destBase}/{relativeFolder}";
        }

        private static void EnsureFolder(string folderPath)
        {
            folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
            // Use Directory.Exists rather than AssetDatabase.IsValidFolder: the latter is
            // stale during a StartAssetEditing batch, which causes folders created earlier
            // in the same batch to be re-created with Unity's auto-rename collision suffix
            // (" 1", " 2", ...). Directory.Exists hits the real filesystem and is accurate.
            if (Directory.Exists(folderPath))
                return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/') ?? string.Empty;
            string leaf = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
                return;

            EnsureFolder(parent);
            if (!Directory.Exists(folderPath))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name;
        }
    }
}
