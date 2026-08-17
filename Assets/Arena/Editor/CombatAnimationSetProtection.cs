#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Arena.Presentation;

namespace Arena.Editor
{
    [InitializeOnLoad]
    internal static class CombatAnimationSetProtection
    {
        [Serializable]
        private struct CombatAnimationSetSummary
        {
            public string assetPath;
            public string animationSetId;
            public string combatProfileId;
            public int clipAssignments;
            public int strikeIds;
            public int authoredHitWindows;
            public int visualBindings;
            public string jsonHash;
        }

        private static readonly Dictionary<string, double> TrustedMutationDeadlines =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RestoreInProgress =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RestoreDialogsShown =
            new(StringComparer.OrdinalIgnoreCase);

        private const double TrustedMutationWindowSeconds = 8.0;
        private static readonly string MutationLogPath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "animation-set-mutations.log"));

        static CombatAnimationSetProtection()
        {
            EditorApplication.delayCall += SeedTrustedStateFromCurrentAssets;
        }

        internal static void MarkTrustedMutation(CombatAnimationSet set, string reason)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            TrustedMutationDeadlines[assetPath] =
                EditorApplication.timeSinceStartup + TrustedMutationWindowSeconds;
            LogMutation(reason, ComputeSummary(set));
        }

        internal static void RecordTrustedState(CombatAnimationSet set, string reason)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            var summary = ComputeSummary(set);
            PersistTrustedSummary(assetPath, summary);
            LogMutation($"trusted-state:{reason}", summary);
        }

        internal static void HandleImportedAssets(string[] importedAssets)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            for (int i = 0; i < importedAssets.Length; i++)
            {
                string assetPath = importedAssets[i];
                if (!IsCombatAnimationSetAsset(assetPath))
                    continue;

                if (RestoreInProgress.Contains(assetPath))
                    continue;

                var set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(assetPath);
                if (set == null)
                    continue;

                PruneAndPersistDanglingMeleeReferences(set, assetPath);

                var current = ComputeSummary(set);
                CombatAnimationSetSummary? trusted = LoadTrustedSummary(assetPath);

                if (trusted.HasValue && !IsTrustedMutation(assetPath) && IsSuspiciousLoss(trusted.Value, current))
                {
                    AutoRestoreLatestBackup(assetPath, trusted.Value, current);
                    continue;
                }

                RecordTrustedState(set, IsTrustedMutation(assetPath) ? "trusted-import" : "post-import");
            }
        }

        private static void SeedTrustedStateFromCurrentAssets()
        {
            string[] guids = AssetDatabase.FindAssets("t:CombatAnimationSet");
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                var set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(assetPath);
                if (set == null)
                    continue;

                PruneAndPersistDanglingMeleeReferences(set, assetPath);

                var current = ComputeSummary(set);
                CombatAnimationSetSummary? trusted = LoadTrustedSummary(assetPath);
                if (trusted.HasValue && IsSuspiciousLoss(trusted.Value, current))
                {
                    AutoRestoreLatestBackup(assetPath, trusted.Value, current);
                    continue;
                }

                RecordTrustedState(set, trusted.HasValue ? "seed-refresh" : "seed");
            }
        }

        private static void PruneAndPersistDanglingMeleeReferences(
            CombatAnimationSet set,
            string assetPath)
        {
            int prunedReferenceCount = CombatAnimationSetEditor.PruneDanglingMeleeReferences(set);
            if (prunedReferenceCount <= 0)
                return;

            MarkTrustedMutation(set, "prune-dangling-melee-references");
            CombatAnimationSetEditor.PersistAnimationSetEdit(
                set,
                "prune-dangling-melee-references");
            Debug.Log(
                $"[{nameof(CombatAnimationSetProtection)}] Pruned {prunedReferenceCount} dangling melee reference(s) from '{assetPath}'.",
                set);
        }

        private static CombatAnimationSetSummary ComputeSummary(CombatAnimationSet set)
        {
            var serializedObject = new SerializedObject(set);
            var iterator = serializedObject.GetIterator();
            int clipAssignments = 0;
            while (iterator.NextVisible(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    continue;
                if (iterator.objectReferenceValue is AnimationClip)
                    clipAssignments += 1;
            }

            int strikeIds = 0;
            int authoredHitWindows = 0;
            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                var authored = set.GetStrikeCombat(strikeIndex);
                if (!string.IsNullOrWhiteSpace(authored.id))
                    strikeIds += 1;
                authoredHitWindows += authored.AuthoredHitWindowCount;
            }

            int visualBindings = set.VisualBindings.Length;

            return new CombatAnimationSetSummary
            {
                assetPath = AssetDatabase.GetAssetPath(set),
                animationSetId = set.AnimationSetIdOrDefault,
                combatProfileId = set.CombatProfileIdOrDefault,
                clipAssignments = clipAssignments,
                strikeIds = strikeIds,
                authoredHitWindows = authoredHitWindows,
                visualBindings = visualBindings,
                jsonHash = ComputeStableHash(EditorJsonUtility.ToJson(set)),
            };
        }

        private static CombatAnimationSetSummary? LoadTrustedSummary(string assetPath)
        {
            string json = SessionState.GetString(SessionKey(assetPath), string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                string summaryPath = GetPersistentSummaryPath(assetPath);
                if (!File.Exists(summaryPath))
                    return null;

                json = File.ReadAllText(summaryPath);
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonUtility.FromJson<CombatAnimationSetSummary>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void PersistTrustedSummary(string assetPath, CombatAnimationSetSummary summary)
        {
            string json = JsonUtility.ToJson(summary);
            SessionState.SetString(SessionKey(assetPath), json);

            string summaryPath = GetPersistentSummaryPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
            File.WriteAllText(summaryPath, json);
        }

        private static bool IsSuspiciousLoss(
            CombatAnimationSetSummary trusted,
            CombatAnimationSetSummary current)
        {
            bool clipsCollapsed =
                trusted.clipAssignments >= 8 &&
                current.clipAssignments <= trusted.clipAssignments / 2;
            bool strikeIdsLost =
                trusted.strikeIds >= 8 &&
                current.strikeIds <= trusted.strikeIds / 2;
            bool hitWindowsCollapsed =
                trusted.authoredHitWindows >= 8 &&
                current.authoredHitWindows <= trusted.authoredHitWindows / 2;
            bool visualsLost =
                trusted.visualBindings > 0 &&
                current.visualBindings == 0;

            return clipsCollapsed || strikeIdsLost || hitWindowsCollapsed || visualsLost;
        }

        private static void AutoRestoreLatestBackup(
            string assetPath,
            CombatAnimationSetSummary trusted,
            CombatAnimationSetSummary current)
        {
            string? backupPath = CombatAnimationSetEditor.GetLatestAnimationSetBackupPath(assetPath);
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                LogMutation(
                    "suspicious-loss-without-backup",
                    current,
                    $"trusted clips={trusted.clipAssignments}, current clips={current.clipAssignments}");
                return;
            }

            string absoluteAssetPath = CombatAnimationSetEditor.ToAbsoluteProjectPath(assetPath);
            if (!File.Exists(absoluteAssetPath))
                return;

            RestoreInProgress.Add(assetPath);
            try
            {
                CombatAnimationSetEditor.BackupAnimationSetAsset(assetPath, "pre-auto-restore-current");
                File.Copy(backupPath, absoluteAssetPath, overwrite: true);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                var restored = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(assetPath);
                if (restored != null)
                    RecordTrustedState(restored, "auto-restore");

                if (RestoreDialogsShown.Add(assetPath))
                {
                    EditorUtility.DisplayDialog(
                        "Animation Set Auto-Restored",
                        $"Unity imported a suspiciously truncated animation set and the editor restored the latest backup automatically:\n{assetPath}",
                        "OK");
                }

                LogMutation(
                    "auto-restored-from-backup",
                    current,
                    $"trusted clips={trusted.clipAssignments}, current clips={current.clipAssignments}, backup={backupPath}");
            }
            finally
            {
                RestoreInProgress.Remove(assetPath);
            }
        }

        private static bool IsCombatAnimationSetAsset(string assetPath)
        {
            return string.Equals(
                AssetDatabase.GetMainAssetTypeAtPath(assetPath)?.Name,
                nameof(CombatAnimationSet),
                StringComparison.Ordinal);
        }

        private static bool IsTrustedMutation(string assetPath)
        {
            if (!TrustedMutationDeadlines.TryGetValue(assetPath, out double deadline))
                return false;

            if (EditorApplication.timeSinceStartup <= deadline)
                return true;

            TrustedMutationDeadlines.Remove(assetPath);
            return false;
        }

        private static string SessionKey(string assetPath)
        {
            return $"Arena.CombatAnimationSetProtection.{AssetDatabase.AssetPathToGUID(assetPath)}";
        }

        private static string GetPersistentSummaryPath(string assetPath)
        {
            string backupDirectory = CombatAnimationSetEditor.GetAnimationSetBackupDirectory(assetPath);
            return Path.Combine(backupDirectory, "trusted-state.json");
        }

        private static string ComputeStableHash(string value)
        {
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("x2"));

            return builder.ToString();
        }

        private static void LogMutation(string reason, CombatAnimationSetSummary summary, string extra = "")
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MutationLogPath)!);
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" | ")
                    .Append(reason)
                    .Append(" | ")
                    .Append(summary.assetPath)
                    .Append(" | animationSet=")
                    .Append(summary.animationSetId)
                    .Append(" | combatProfile=")
                    .Append(summary.combatProfileId)
                    .Append(" | clips=")
                    .Append(summary.clipAssignments)
                    .Append(" | strikeIds=")
                    .Append(summary.strikeIds)
                    .Append(" | hitWindows=")
                    .Append(summary.authoredHitWindows)
                    .Append(" | visuals=")
                    .Append(summary.visualBindings)
                    .Append(" | hash=")
                    .Append(summary.jsonHash);

                if (!string.IsNullOrWhiteSpace(extra))
                    line.Append(" | ").Append(extra);

                line.AppendLine();
                File.AppendAllText(MutationLogPath, line.ToString());
            }
            catch
            {
                // Logging must never block editor authoring.
            }
        }
    }

    internal sealed class CombatAnimationSetProtectionPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            CombatAnimationSetProtection.HandleImportedAssets(importedAssets);
        }
    }
}
