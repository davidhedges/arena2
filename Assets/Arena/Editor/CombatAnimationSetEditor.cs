#nullable enable

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditorInternal;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine;
using Arena.Combat;
using Arena.Input;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using Arena.Presentation.VFX;
using PresentationAerialExecutionMode = Arena.Presentation.AerialExecutionMode;

namespace Arena.Editor
{
    [CustomEditor(typeof(CombatAnimationSet))]
    public sealed class CombatAnimationSetEditor : UnityEditor.Editor
    {
        private static readonly string[] DefaultInspectorExclusions = BuildDefaultInspectorExclusions();
        private static readonly string[] LocomotionPropertyNames =
        {
            "locomotionIdle",
            "locomotionIdleCombat",
            "walk",
            "walkCombat",
            "run",
            "runCombat",
            "walkStop",
            "walkStopCombat",
            "runStop",
            "runStopCombat",
            "locomotionModeOverrides",
        };
        private const float PreviewHeight = 280f;
        private const float PreviewMinDistance = 0.5f;
        private const float PreviewMaxDistance = 2.5f;

        private sealed class PendingAnimationSetPersist
        {
            public PendingAnimationSetPersist(CombatAnimationSet set, string reason, double dueTime)
            {
                Set = set;
                Reason = reason;
                DueTime = dueTime;
            }

            public CombatAnimationSet Set { get; }
            public string Reason { get; set; }
            public double DueTime { get; set; }
        }

        private sealed class PendingStartupTrimSynchronization
        {
            public PendingStartupTrimSynchronization(CombatAnimationSet set, double dueTime)
            {
                Set = set;
                DueTime = dueTime;
            }

            public CombatAnimationSet Set { get; }
            public HashSet<AnimationClip> Clips { get; } = new();
            public double DueTime { get; set; }
        }

        internal readonly struct StartupTrimTarget
        {
            public StartupTrimTarget(
                CombatAnimationSet set,
                int attackIndex,
                bool supportsStartupTrim,
                float authoredTrimSeconds)
            {
                Set = set;
                AttackIndex = attackIndex;
                SupportsStartupTrim = supportsStartupTrim;
                AuthoredTrimSeconds = authoredTrimSeconds;
            }

            public CombatAnimationSet Set { get; }
            public int AttackIndex { get; }
            public bool SupportsStartupTrim { get; }
            public float AuthoredTrimSeconds { get; }
        }

        private sealed class CachedAvatarValidation
        {
            public string Signature { get; set; } = string.Empty;
            public List<string> Warnings { get; set; } = new();
        }

        private readonly struct AttackPreviewClipOption
        {
            public AttackPreviewClipOption(string label, AnimationClip clip)
            {
                Label = label;
                Clip = clip;
            }

            public string Label { get; }
            public AnimationClip Clip { get; }
        }

        private static readonly string ExportPath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "server", "src", "melee_manifest.shared.json"));
        private static readonly string BackupRootPath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Backups", "melee-authoring"));
        internal static readonly string AnimationSetBackupRootPath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Backups", "combat-animation-sets"));
        private static readonly Dictionary<string, PendingAnimationSetPersist> PendingPersists =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PendingStartupTrimSynchronization> PendingStartupTrimSynchronizations =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CachedAvatarValidation> CachedAvatarValidations =
            new(StringComparer.OrdinalIgnoreCase);
        private const string StrictRigValidationEditorPrefKey = "Arena.CombatAnimationSetEditor.StrictRigValidation";
        private const double AutoPersistDelaySeconds = 5.0;
        private const double AutoStartupTrimSyncDelaySeconds = 0.75;
        internal const int MaxAnimationSetBackupsPerAsset = 40;
        private PreviewRenderUtility? _attackPreviewUtility;
        private GameObject? _attackPreviewInstance;
        private Animator? _attackPreviewAnimator;
        private PlayableGraph _attackPreviewGraph;
        private AnimationClipPlayable _attackPreviewPlayable;
        private bool _attackPreviewGraphCreated;
        private CombatAnimationSet? _attackPreviewSet;
        private AnimationClip? _attackPreviewClip;
        private string _attackPreviewError = string.Empty;
        private int _attackPreviewStrikeIndex;
        private float _attackPreviewTime;
        private bool _attackPreviewPlaying;
        private double _attackPreviewLastEditorTime;
        private Vector2 _attackPreviewOrbit = new(25f, -12f);
        private float _attackPreviewDistanceMultiplier = 1f;
        private readonly List<AnimationClip> _startupTrimChangedClips = new();

        private sealed class AnimatedPropRequirement
        {
            public AnimatedPropRequirement(string displayName, string path, string mountId)
            {
                DisplayName = displayName;
                Path = path;
                MountId = mountId;
            }

            public string DisplayName { get; }
            public string Path { get; }
            public string MountId { get; }
        }

        private static readonly AnimatedPropRequirement[] AnimatedPropRequirements =
        {
            new("Sword", "root/pelvis/spine_01/spine_02/spine_03/clavicle_r/upperarm_r/lowerarm_r/hand_r/Sword", AvatarWeaponMounts.MainHandMountId),
            new("Shield", "root/pelvis/spine_01/spine_02/spine_03/clavicle_l/upperarm_l/lowerarm_l/hand_l/Shield", AvatarWeaponMounts.OffHandMountId),
            new("Sword_Holder", "root/pelvis/spine_01/spine_02/spine_03/Sword_Holder", AvatarWeaponMounts.MainStowedMountId),
            new("Shield_Holder", "root/pelvis/spine_01/spine_02/spine_03/Shield_Holder", AvatarWeaponMounts.OffStowedMountId),
            new("weapon_r", "root/pelvis/spine_01/spine_02/spine_03/clavicle_r/upperarm_r/lowerarm_r/hand_r/weapon_r", AvatarWeaponMounts.GreatswordHandMountId),
        };

        static CombatAnimationSetEditor()
        {
            EditorApplication.update -= FlushPendingAnimationSetPersists;
            EditorApplication.update += FlushPendingAnimationSetPersists;
        }

        private void OnDisable()
        {
            DestroyAttackPreview();
        }

        private void OnDestroy()
        {
            DestroyAttackPreview();
        }

        public override void OnInspectorGUI()
        {
            var set = (CombatAnimationSet)target;
            _startupTrimChangedClips.Clear();
            bool initializedIdentity = set.EnsureAnimationSetIdentityInitialized();
            bool initializedMelee = EnsureMeleeAttackListInitialized(set);
            bool initializedPresentation = set.EnsureWeaponPresentationProfileInitialized();
            if (initializedIdentity || initializedMelee || initializedPresentation)
            {
                CombatAnimationSetProtection.MarkTrustedMutation(set, initializedIdentity
                    ? "animation-set-identity-normalize"
                    : initializedPresentation
                        ? "weapon-presentation-init"
                    : "melee-list-init");
                EditorUtility.SetDirty(set);
                serializedObject.UpdateIfRequiredOrScript();
            }

            serializedObject.UpdateIfRequiredOrScript();
            EditorGUI.BeginChangeCheck();
            DrawAnimationSetProperties();
            DrawMeleeAttackAuthoringSection(set);
            bool inspectorChanged = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            if (inspectorChanged)
            {
                CombatAnimationSetProtection.MarkTrustedMutation(set, "inspector-edit");
                EditorUtility.SetDirty(set);
                ScheduleAnimationSetPersist(set, "auto-save");
                InvalidateAvatarValidationCache(set);
                for (int clipIndex = 0; clipIndex < _startupTrimChangedClips.Count; clipIndex++)
                    ScheduleStartupTrimSynchronization(set, _startupTrimChangedClips[clipIndex]);
            }

            DrawOptionalMeleeAnimationPreviewPane(set);
            DrawDiagnosticsAndExportSection(set);
        }

        private void DrawDiagnosticsAndExportSection(CombatAnimationSet set)
        {
            EditorGUILayout.Space(12);
            string foldoutKey = BuildSessionKey("diagnostics-export-foldout");
            bool expanded = SessionState.GetBool(foldoutKey, false);
            expanded = EditorGUILayout.Foldout(expanded, "Diagnostics, Backups, and Manifest Export", true, EditorStyles.foldoutHeader);
            SessionState.SetBool(foldoutKey, expanded);
            if (!expanded)
            {
                EditorGUILayout.HelpBox(
                    "Collapsed for inspector responsiveness. Expand to run rig validation, melee validation, backup lookup, and manifest comparison/export.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.HelpBox(
                "This animation set owns melee animation clips and melee combat tuning for its combat profile. " +
                "Tune the strike combat fields above, then export the shared manifest for the server.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Animation set edits auto-save and snapshot to disk. If Unity or a bad import wipes values, use the restore button below instead of re-entering the asset by hand.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Clips that animate authored weapon props require the runtime avatar rig to expose matching prop nodes. " +
                "The resolved runtime avatar prefab is validated below against the active strike clips.",
                MessageType.None);
            string? resolvedRuntimeAvatarPath = RuntimeAvatarPrefabResolver.ResolveRuntimePlayerPrefabAssetPath();
            EditorGUILayout.HelpBox(
                string.IsNullOrWhiteSpace(resolvedRuntimeAvatarPath)
                    ? "Resolved runtime avatar prefab: <missing>"
                    : $"Resolved runtime avatar prefab: {resolvedRuntimeAvatarPath}",
                MessageType.None);
            bool strictRigValidationEnabled = EditorGUILayout.ToggleLeft(
                "Strict Rig Validation",
                EditorPrefs.GetBool(StrictRigValidationEditorPrefKey, false));
            EditorPrefs.SetBool(StrictRigValidationEditorPrefKey, strictRigValidationEnabled);
            EditorGUILayout.HelpBox(
                "Author against the strike id shown below. Runtime slot ids are exported plumbing for cooldowns, combo routing, and input dispatch. " +
                "Player-facing abilities should point at the authored strike id, not the runtime slot id.",
                MessageType.None);

            var avatarPropWarnings = GetAnimatedPropWarningsCached(set);
            if (strictRigValidationEnabled)
            {
                for (int warningIndex = 0; warningIndex < avatarPropWarnings.Count; warningIndex++)
                    EditorGUILayout.HelpBox(avatarPropWarnings[warningIndex], MessageType.Warning);
            }
            else if (avatarPropWarnings.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    avatarPropWarnings.Count == 1
                        ? "Strict rig validation found 1 animated-prop mismatch on the resolved runtime avatar prefab. Runtime weapon attachment can still be correct. Enable Strict Rig Validation to inspect it."
                        : $"Strict rig validation found {avatarPropWarnings.Count} animated-prop mismatches on the resolved runtime avatar prefab. Runtime weapon attachment can still be correct. Enable Strict Rig Validation to inspect them.",
                    MessageType.Info);
            }

            List<(MessageType type, string message)> strikeValidationMessages =
                CollectStrikeValidationMessages(set);
            bool hasErrors = false;
            if (strikeValidationMessages.Count > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Melee Validation", EditorStyles.boldLabel);
                foreach ((MessageType type, string message) in strikeValidationMessages)
                {
                    EditorGUILayout.HelpBox(message, type);
                    if (type == MessageType.Error)
                        hasErrors = true;
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Phased Presentation Preview", EditorStyles.boldLabel);
            bool anyInlinePhasedAttack = false;
            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                WeaponMeleeAttackAuthoring attack = set.meleeAttacks[strikeIndex - 1];
                if (!attack.UsesPhasedPresentation)
                    continue;

                anyInlinePhasedAttack = true;
                string authoredActionId = attack.combat.AuthoredStrikeIdOrDefault;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    $"Attack {strikeIndex}: {(string.IsNullOrWhiteSpace(authoredActionId) ? "<missing authored id>" : authoredActionId)}",
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Authored Id", string.IsNullOrWhiteSpace(authoredActionId) ? "<missing>" : authoredActionId);
                EditorGUILayout.LabelField("Ground Clips", DescribePhasedClipSet(attack.phasedGround));
                EditorGUILayout.LabelField("Air Clips", DescribePhasedClipSet(attack.phasedAir));
                EditorGUILayout.EndVertical();
            }

            if (!anyInlinePhasedAttack)
            {
                EditorGUILayout.HelpBox(
                    "No melee attacks are using inline phased presentation on this animation set.",
                    MessageType.None);
            }

            EditorGUILayout.Space(8);
            string exportedJson = string.Empty;
            bool canExportManifest = TryBuildMergedExportDocument(set, out MeleeManifestDocument? exportDoc, out string exportError);
            if (canExportManifest && exportDoc != null)
            {
                exportedJson = SerializeMeleeManifestDocument(exportDoc);
                string currentJson = File.Exists(ExportPath) ? File.ReadAllText(ExportPath) : string.Empty;
                bool isStale = NormalizeJson(exportedJson) != NormalizeJson(currentJson);

                if (isStale)
                {
                    EditorGUILayout.HelpBox(
                        $"Server melee manifest export is stale vs {ExportPath}. This only tracks server-consumed melee timing and gameplay fields.",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Server melee manifest export matches. Presentation-only fields such as Lower Body Unlock, Lower Body Blend Out, and Visual Interruptible are not exported to the server manifest.",
                        MessageType.Info);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Melee manifest export is unavailable for this animation set: {exportError}",
                    MessageType.Error);
            }

            string? latestBackupPath = GetLatestAnimationSetBackupPath(set);
            if (!string.IsNullOrWhiteSpace(latestBackupPath))
            {
                string latestBackupTimestamp = Path.GetFileNameWithoutExtension(latestBackupPath);
                EditorGUILayout.HelpBox(
                    $"Latest animation-set backup: {latestBackupTimestamp}",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No animation-set backups exist yet for this asset. The next edit will create one automatically.",
                    MessageType.Warning);
            }

            if (GUILayout.Button("Import Current Shared Manifest Into This Animation Set"))
            {
                ImportCurrentManifest(set);
                GUIUtility.ExitGUI();
            }

            if (GUILayout.Button("Save Animation Set Snapshot Now"))
            {
                CombatAnimationSetProtection.MarkTrustedMutation(set, "manual-save");
                PersistAnimationSetEdit(set, "manual-save");
                CombatAnimationSetProtection.RecordTrustedState(set, "manual-save");
                GUIUtility.ExitGUI();
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(latestBackupPath)))
            {
                if (GUILayout.Button("Restore Latest Animation Set Backup"))
                {
                    RestoreLatestAnimationSetBackup(set, latestBackupPath!);
                    GUIUtility.ExitGUI();
                }
            }

            using (new EditorGUI.DisabledScope(hasErrors || !canExportManifest))
            {
                if (GUILayout.Button("Synchronize All Authored Hit Events & Export Profile"))
                {
                    bool synchronized = SynchronizeAllHitEventsForSet(set, out string syncSummary);
                    EditorUtility.DisplayDialog(
                        synchronized ? "Hit Windows Synchronized" : "Hit Window Sync Failed",
                        syncSummary,
                        "OK");
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button("Export Shared Melee Manifest"))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ExportPath)!);
                    BackupFile(ExportPath, "pre-export-manifest");
                    File.WriteAllText(ExportPath, exportedJson);
                    AssetDatabase.Refresh();
                }
            }
        }

        private void DrawOptionalMeleeAnimationPreviewPane(CombatAnimationSet set)
        {
            EditorGUILayout.Space(8);
            string foldoutKey = BuildSessionKey("animation-preview-foldout");
            bool expanded = SessionState.GetBool(foldoutKey, false);
            expanded = EditorGUILayout.Foldout(expanded, "Animation Preview", true, EditorStyles.foldoutHeader);
            SessionState.SetBool(foldoutKey, expanded);

            if (!expanded)
            {
                DestroyAttackPreview();
                EditorGUILayout.HelpBox(
                    "Collapsed for inspector responsiveness. Expand only when you need to scrub or play clips.",
                    MessageType.None);
                return;
            }

            DrawNonAssetChanging(() => DrawMeleeAnimationPreviewPane(set));
        }

        internal static bool IsReferencedByMeleeAttack(AnimationClip? clip)
        {
            if (clip == null)
                return false;

            IReadOnlyList<CombatAnimationSet> sets = CombatAnimationSetAssetIndex.LoadAll();
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                CombatAnimationSet set = sets[setIndex];
                if (set?.meleeAttacks == null)
                    continue;

                for (int attackIndex = 0; attackIndex < set.meleeAttacks.Count; attackIndex++)
                {
                    if (set.meleeAttacks[attackIndex].ReferencesClip(clip))
                        return true;
                }
            }

            return false;
        }

        internal static List<StartupTrimTarget> FindStartupTrimTargets(AnimationClip? clip)
        {
            var targets = new List<StartupTrimTarget>();
            if (clip == null)
                return targets;

            IReadOnlyList<CombatAnimationSet> sets = CombatAnimationSetAssetIndex.LoadAll();
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                CombatAnimationSet set = sets[setIndex];
                if (set?.meleeAttacks == null)
                    continue;

                for (int attackIndex = 0; attackIndex < set.meleeAttacks.Count; attackIndex++)
                {
                    WeaponMeleeAttackAuthoring attack = set.meleeAttacks[attackIndex];
                    if (!attack.ReferencesClip(clip))
                        continue;

                    bool supportsStartupTrim =
                        !attack.UsesPhasedPresentation && ReferenceEquals(attack.clip, clip);
                    targets.Add(new StartupTrimTarget(
                        set,
                        attackIndex,
                        supportsStartupTrim,
                        attack.startupTrimSeconds));
                }
            }

            targets.Sort((left, right) =>
            {
                int pathComparison = string.Compare(
                    AssetDatabase.GetAssetPath(left.Set),
                    AssetDatabase.GetAssetPath(right.Set),
                    StringComparison.OrdinalIgnoreCase);
                return pathComparison != 0
                    ? pathComparison
                    : left.AttackIndex.CompareTo(right.AttackIndex);
            });
            return targets;
        }

        internal static bool SetStartupTrimForClip(
            AnimationClip? clip,
            float requestedTrimSeconds,
            out string summary)
        {
            if (clip == null)
            {
                summary = "No animation clip is selected.";
                return false;
            }

            List<StartupTrimTarget> targets = FindStartupTrimTargets(clip);
            var supportedTargets = new List<StartupTrimTarget>();
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                if (targets[targetIndex].SupportsStartupTrim)
                    supportedTargets.Add(targets[targetIndex]);
            }

            if (supportedTargets.Count == 0)
            {
                summary = targets.Count == 0
                    ? $"'{clip.name}' is not assigned to a CombatAnimationSet melee attack."
                    : $"'{clip.name}' is used only by phased melee. Startup trim is supported only for single-clip melee.";
                return false;
            }

            WeaponMeleeAttackAuthoring firstAttack =
                supportedTargets[0].Set.meleeAttacks[supportedTargets[0].AttackIndex];
            if (!firstAttack.TryGetStrikeHitEventTimesSeconds(out float[] authoredHitTimes)
                || authoredHitTimes.Length == 0)
            {
                summary =
                    $"Stamp {CombatAnimationEvents.OnStrikeHit} at the physical contact pose before setting startup trim.";
                return false;
            }

            float firstContactSeconds = authoredHitTimes[0];
            float resolvedTrimSeconds = Mathf.Clamp(
                requestedTrimSeconds,
                0f,
                firstContactSeconds);

            var affectedSets = new HashSet<CombatAnimationSet>();
            for (int targetIndex = 0; targetIndex < supportedTargets.Count; targetIndex++)
                affectedSets.Add(supportedTargets[targetIndex].Set);

            var undoTargets = new List<UnityEngine.Object>(affectedSets.Count);
            foreach (CombatAnimationSet set in affectedSets)
                undoTargets.Add(set);
            Undo.RecordObjects(undoTargets.ToArray(), "Set melee startup trim");
            foreach (CombatAnimationSet set in affectedSets)
                CombatAnimationSetProtection.MarkTrustedMutation(set, "event-stamper-startup-trim");

            for (int targetIndex = 0; targetIndex < supportedTargets.Count; targetIndex++)
            {
                StartupTrimTarget target = supportedTargets[targetIndex];
                WeaponMeleeAttackAuthoring attack = target.Set.meleeAttacks[target.AttackIndex];
                attack.startupTrimSeconds = resolvedTrimSeconds;
                target.Set.meleeAttacks[target.AttackIndex] = attack;
                EditorUtility.SetDirty(target.Set);
            }

            bool synchronized = SynchronizeHitEventsForClip(clip, out string syncSummary);
            summary =
                $"Set '{clip.name}' startup to {resolvedTrimSeconds:0.000}s for " +
                $"{supportedTargets.Count} melee assignment(s); first contact now occurs " +
                $"{Mathf.Max(0f, firstContactSeconds - resolvedTrimSeconds):0.000}s after input.\n\n{syncSummary}";
            return synchronized;
        }

        internal static bool SynchronizeHitEventsForClip(
            AnimationClip? clip,
            out string summary)
        {
            if (clip == null)
            {
                summary = "No animation clip is selected.";
                return false;
            }

            var affectedBySet = new Dictionary<CombatAnimationSet, List<int>>();
            IReadOnlyList<CombatAnimationSet> sets = CombatAnimationSetAssetIndex.LoadAll();
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                CombatAnimationSet set = sets[setIndex];
                if (set?.meleeAttacks == null)
                    continue;

                for (int attackIndex = 0; attackIndex < set.meleeAttacks.Count; attackIndex++)
                {
                    WeaponMeleeAttackAuthoring attack = set.meleeAttacks[attackIndex];
                    if (!attack.ReferencesClip(clip))
                        continue;

                    if (!attack.TryBuildHitWindowMirrorFromEvents(out _))
                    {
                        summary =
                            $"Cannot synchronize '{attack.combat.AuthoredStrikeIdOrDefault}': its resolved presentation has no {CombatAnimationEvents.OnStrikeHit} event. " +
                            "Every assigned melee attack must retain at least one hit event.";
                        return false;
                    }

                    if (!affectedBySet.TryGetValue(set, out List<int>? attackIndices))
                    {
                        attackIndices = new List<int>();
                        affectedBySet.Add(set, attackIndices);
                    }
                    attackIndices.Add(attackIndex);
                }
            }

            if (affectedBySet.Count == 0)
            {
                summary = $"'{clip.name}' is not assigned to a CombatAnimationSet melee attack; only the clip event was saved.";
                return true;
            }

            MeleeManifestDocument manifest;
            try
            {
                manifest = File.Exists(ExportPath)
                    ? DeserializeMeleeManifestDocument(File.ReadAllText(ExportPath))
                    : new MeleeManifestDocument();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                summary = $"Cannot synchronize the shared melee manifest: {ex.Message}";
                return false;
            }

            var affectedStrikeIdsBySet = new Dictionary<CombatAnimationSet, HashSet<string>>();
            int affectedStrikeCount = 0;
            foreach (KeyValuePair<CombatAnimationSet, List<int>> pair in affectedBySet)
            {
                CombatAnimationSet set = pair.Key;
                Undo.RecordObject(set, "Synchronize melee hit windows from animation events");
                CombatAnimationSetProtection.MarkTrustedMutation(set, "hit-event-sync");

                var affectedStrikeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    int attackIndex = pair.Value[i];
                    WeaponMeleeAttackAuthoring attack = set.meleeAttacks[attackIndex];
                    attack.TryBuildHitWindowMirrorFromEvents(
                        out WeaponStrikeHitWindowAuthoring[] mirroredHitWindows);

                    WeaponStrikeCombatAuthoring combat = attack.combat;
                    combat.hitWindows = mirroredHitWindows;
                    combat.impactNormalized = mirroredHitWindows.Length > 0
                        ? mirroredHitWindows[0].timeNormalized
                        : 0f;
                    attack.combat = combat;
                    set.meleeAttacks[attackIndex] = attack;

                    affectedStrikeIds.Add(combat.AuthoredStrikeIdOrDefault);
                    if (set.IsAutoAttackVisualSourceStrike(attackIndex + 1))
                        affectedStrikeIds.Add(set.AutoAttackAuthoredStrikeIdOrDefault);
                    affectedStrikeCount += 1;
                }

                PersistAnimationSetEdit(set, "hit-event-sync");
                CombatAnimationSetProtection.RecordTrustedState(set, "hit-event-sync");
                affectedStrikeIdsBySet.Add(set, affectedStrikeIds);
            }

            foreach (KeyValuePair<CombatAnimationSet, HashSet<string>> pair in affectedStrikeIdsBySet)
            {
                MeleeManifestProfile[] generatedProfiles = pair.Key.BuildMeleeExport().profiles;
                if (generatedProfiles == null || generatedProfiles.Length == 0)
                    continue;
                MergeSelectedStrikes(manifest, generatedProfiles[0], pair.Value);
            }

            string exportedJson = SerializeMeleeManifestDocument(manifest);
            string currentJson = File.Exists(ExportPath) ? File.ReadAllText(ExportPath) : string.Empty;
            bool manifestChanged = NormalizeJson(exportedJson) != NormalizeJson(currentJson);
            if (manifestChanged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExportPath)!);
                BackupFile(ExportPath, "pre-hit-event-sync");
                File.WriteAllText(ExportPath, exportedJson);
            }

            InternalEditorUtility.RepaintAllViews();
            summary = manifestChanged
                ? $"Synchronized {affectedStrikeCount} melee strike(s) from '{clip.name}' and updated the shared server manifest. Republish the server to make the timing live."
                : $"Synchronized {affectedStrikeCount} melee strike(s) from '{clip.name}'; the shared server manifest already matched.";
            return true;
        }

        internal static bool SynchronizeAllHitEventsForSet(
            CombatAnimationSet? set,
            out string summary)
        {
            if (set == null || set.meleeAttacks == null)
            {
                summary = "No CombatAnimationSet is selected.";
                return false;
            }

            try
            {
                Undo.RecordObject(set, "Synchronize all melee hit windows from animation events");
                CombatAnimationSetProtection.MarkTrustedMutation(set, "bulk-hit-event-sync");

                int synchronizedCount = 0;
                var legacyFallbackIds = new List<string>();
                for (int attackIndex = 0; attackIndex < set.meleeAttacks.Count; attackIndex++)
                {
                    WeaponMeleeAttackAuthoring attack = set.meleeAttacks[attackIndex];
                    if (!attack.TryBuildHitWindowMirrorFromEvents(
                            out WeaponStrikeHitWindowAuthoring[] mirroredHitWindows))
                    {
                        legacyFallbackIds.Add(attack.combat.AuthoredStrikeIdOrDefault);
                        continue;
                    }

                    WeaponStrikeCombatAuthoring combat = attack.combat;
                    combat.hitWindows = mirroredHitWindows;
                    combat.impactNormalized = mirroredHitWindows.Length > 0
                        ? mirroredHitWindows[0].timeNormalized
                        : 0f;
                    attack.combat = combat;
                    set.meleeAttacks[attackIndex] = attack;
                    synchronizedCount += 1;
                }

                PersistAnimationSetEdit(set, "bulk-hit-event-sync");
                CombatAnimationSetProtection.RecordTrustedState(set, "bulk-hit-event-sync");

                MeleeManifestDocument manifest = BuildMergedExportDocument(set);
                string exportedJson = SerializeMeleeManifestDocument(manifest);
                string currentJson = File.Exists(ExportPath) ? File.ReadAllText(ExportPath) : string.Empty;
                if (NormalizeJson(exportedJson) != NormalizeJson(currentJson))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ExportPath)!);
                    BackupFile(ExportPath, "pre-bulk-hit-event-sync");
                    File.WriteAllText(ExportPath, exportedJson);
                }

                InternalEditorUtility.RepaintAllViews();
                summary = $"Synchronized {synchronizedCount} authored melee attack(s) in '{set.name}' and exported the complete '{set.CombatProfileIdOrDefault}' profile.";
                if (legacyFallbackIds.Count > 0)
                {
                    summary +=
                        $"\n\nKept {legacyFallbackIds.Count} attack(s) on their existing serialized fallback because no {CombatAnimationEvents.OnStrikeHit} event is authored:\n- "
                        + string.Join("\n- ", legacyFallbackIds);
                }
                summary += "\n\nRepublish the server module to make these timings live.";
                return true;
            }
            catch (Exception ex) when (ex is IOException
                                       || ex is UnauthorizedAccessException
                                       || ex is InvalidOperationException
                                       || ex is ArgumentException)
            {
                summary = $"Could not synchronize '{set.name}': {ex.Message}";
                return false;
            }
        }

        private static void MergeSelectedStrikes(
            MeleeManifestDocument manifest,
            MeleeManifestProfile generatedProfile,
            HashSet<string> affectedStrikeIds)
        {
            var profiles = new List<MeleeManifestProfile>(manifest.profiles ?? Array.Empty<MeleeManifestProfile>());
            int profileIndex = profiles.FindIndex(profile =>
                profile != null
                && string.Equals(
                    profile.combat_profile,
                    generatedProfile.combat_profile,
                    StringComparison.OrdinalIgnoreCase));
            if (profileIndex < 0)
            {
                profiles.Add(generatedProfile);
                manifest.profiles = profiles.ToArray();
                return;
            }

            MeleeManifestProfile existingProfile = profiles[profileIndex];
            var strikes = new List<MeleeManifestStrike>(existingProfile.strikes ?? Array.Empty<MeleeManifestStrike>());
            foreach (MeleeManifestStrike generatedStrike in generatedProfile.strikes ?? Array.Empty<MeleeManifestStrike>())
            {
                if (generatedStrike == null || !affectedStrikeIds.Contains(generatedStrike.id))
                    continue;

                int strikeIndex = strikes.FindIndex(strike =>
                    strike != null
                    && string.Equals(strike.id, generatedStrike.id, StringComparison.OrdinalIgnoreCase));
                if (strikeIndex >= 0)
                    strikes[strikeIndex] = generatedStrike;
                else
                    strikes.Add(generatedStrike);
            }

            existingProfile.auto_attack_strike_id = generatedProfile.auto_attack_strike_id;
            existingProfile.auto_attack_sequence = generatedProfile.auto_attack_sequence;
            existingProfile.auto_attack_sequence_interval_ms = generatedProfile.auto_attack_sequence_interval_ms;
            existingProfile.strikes = strikes.ToArray();
            profiles[profileIndex] = existingProfile;
            manifest.profiles = profiles.ToArray();
        }

        private static string[] BuildDefaultInspectorExclusions()
        {
            return new[] { "m_Script", "meleeAttacks" };
        }

        private void DrawAnimationSetProperties()
        {
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            bool drewLocomotionSection = false;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                string propertyName = iterator.name;
                if (IsDefaultInspectorExcluded(propertyName))
                    continue;

                if (IsLocomotionProperty(propertyName))
                {
                    if (!drewLocomotionSection)
                    {
                        DrawLocomotionSection();
                        drewLocomotionSection = true;
                    }

                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        private void DrawLocomotionSection()
        {
            string sessionKey = BuildSessionKey("locomotion-foldout");
            bool expanded = SessionState.GetBool(sessionKey, false);
            expanded = EditorGUILayout.Foldout(expanded, "Locomotion", true, EditorStyles.foldoutHeader);
            SessionState.SetBool(sessionKey, expanded);
            if (!expanded)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Idle", EditorStyles.boldLabel);
                DrawRequiredProperty("locomotionIdle", "Idle");
                DrawRequiredProperty("locomotionIdleCombat", "Combat Idle");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Directional", EditorStyles.boldLabel);
                DrawRequiredProperty("walk", "Walk");
                DrawRequiredProperty("walkCombat", "Combat Walk");
                DrawRequiredProperty("run", "Run");
                DrawRequiredProperty("runCombat", "Combat Run");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Directional Stops", EditorStyles.boldLabel);
                DrawRequiredProperty("walkStop", "Walk Stop");
                DrawRequiredProperty("walkStopCombat", "Combat Walk Stop");
                DrawRequiredProperty("runStop", "Run Stop");
                DrawRequiredProperty("runStopCombat", "Combat Run Stop");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Combat Mode Overrides", EditorStyles.boldLabel);
                DrawRequiredProperty("locomotionModeOverrides", "Mode Overrides");
            }
        }

        private void DrawRequiredProperty(string propertyName, string label)
        {
            SerializedProperty? property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                EditorGUILayout.HelpBox($"Missing serialized property: {propertyName}", MessageType.Error);
                return;
            }

            EditorGUILayout.PropertyField(property, new GUIContent(label), true);
        }

        private static bool IsDefaultInspectorExcluded(string propertyName)
        {
            for (int i = 0; i < DefaultInspectorExclusions.Length; i++)
            {
                if (string.Equals(DefaultInspectorExclusions[i], propertyName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool IsLocomotionProperty(string propertyName)
        {
            for (int i = 0; i < LocomotionPropertyNames.Length; i++)
            {
                if (string.Equals(LocomotionPropertyNames[i], propertyName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void DrawNonAssetChanging(Action draw)
        {
            bool previousChanged = GUI.changed;
            GUI.changed = false;
            draw();
            GUI.changed = previousChanged;
        }

        private string BuildSessionKey(string suffix)
        {
            return $"{GlobalObjectId.GetGlobalObjectIdSlow(target)}.{suffix}";
        }

        private void DrawMeleeAttackAuthoringSection(CombatAnimationSet set)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Melee Attacks", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each authored melee attack lives here as one unit: combat plus presentation. Use Presentation Mode = Phased for stitched start/loop/end attacks like Skyfall.",
                MessageType.None);

            SerializedProperty? meleeAttacksProperty = serializedObject.FindProperty("meleeAttacks");
            if (meleeAttacksProperty == null)
            {
                EditorGUILayout.HelpBox("Could not resolve the melee attack list on this asset.", MessageType.Error);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Attack", GUILayout.Width(120f)))
                {
                    Undo.RecordObject(set, "Add Melee Attack");
                    set.EnsureMeleeAttackListSize(set.MeleeAttackCount + 1);
                    OnDirectMeleeAttackListMutation(set, "add-melee-attack");
                    GUIUtility.ExitGUI();
                }
            }

            if (meleeAttacksProperty.arraySize == 0)
            {
                EditorGUILayout.HelpBox("No melee attacks are authored on this animation set yet.", MessageType.Warning);
                return;
            }

            for (int attackIndex = 0; attackIndex < meleeAttacksProperty.arraySize; attackIndex++)
            {
                SerializedProperty attackProperty = meleeAttacksProperty.GetArrayElementAtIndex(attackIndex);
                SerializedProperty clipProperty = attackProperty.FindPropertyRelative("clip");
                SerializedProperty combatProperty = attackProperty.FindPropertyRelative("combat");
                SerializedProperty presentationModeProperty = attackProperty.FindPropertyRelative("presentationMode");
                SerializedProperty startupTrimSecondsProperty = attackProperty.FindPropertyRelative("startupTrimSeconds");
                SerializedProperty phasedGroundProperty = attackProperty.FindPropertyRelative("phasedGround");
                SerializedProperty phasedAirProperty = attackProperty.FindPropertyRelative("phasedAir");
                SerializedProperty drivePhasesFromSpecialMovementProperty = attackProperty.FindPropertyRelative("drivePhasesFromSpecialMovement");

                int strikeIndex = attackIndex + 1;
                var strike = set.GetStrikeCombat(strikeIndex);
                string authoredId = string.IsNullOrWhiteSpace(strike.AuthoredStrikeIdOrDefault)
                    ? "<missing id>"
                    : strike.AuthoredStrikeIdOrDefault;
                string clipName = set.GetStrikeClip(strikeIndex) != null
                    ? set.GetStrikeClip(strikeIndex)!.name
                    : "<missing clip>";
                string foldoutKey = BuildSessionKey($"melee-strike.{strikeIndex}");
                bool expanded = SessionState.GetBool(foldoutKey, strikeIndex == 1);

                using (new EditorGUILayout.HorizontalScope())
                {
                    string header = $"Attack {strikeIndex}: {authoredId} [{clipName}]";
                    expanded = EditorGUILayout.Foldout(expanded, header, true, EditorStyles.foldoutHeader);
                    SessionState.SetBool(foldoutKey, expanded);

                    using (new EditorGUI.DisabledScope(attackIndex == 0))
                    {
                        if (GUILayout.Button("+", GUILayout.Width(24f)))
                        {
                            Undo.RecordObject(set, "Move Melee Attack");
                            WeaponMeleeAttackAuthoring attack = set.meleeAttacks[attackIndex];
                            set.meleeAttacks.RemoveAt(attackIndex);
                            set.meleeAttacks.Insert(attackIndex - 1, attack);
                            OnDirectMeleeAttackListMutation(set, "move-melee-attack-up");
                            GUIUtility.ExitGUI();
                        }
                    }

                    using (new EditorGUI.DisabledScope(attackIndex == meleeAttacksProperty.arraySize - 1))
                    {
                        if (GUILayout.Button("-", GUILayout.Width(24f)))
                        {
                            Undo.RecordObject(set, "Move Melee Attack");
                            WeaponMeleeAttackAuthoring attack = set.meleeAttacks[attackIndex];
                            set.meleeAttacks.RemoveAt(attackIndex);
                            set.meleeAttacks.Insert(attackIndex + 1, attack);
                            OnDirectMeleeAttackListMutation(set, "move-melee-attack-down");
                            GUIUtility.ExitGUI();
                        }
                    }

                    if (GUILayout.Button("x", GUILayout.Width(24f)))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Remove Melee Attack",
                                $"Remove authored melee attack '{authoredId}'?",
                                "Remove",
                                "Cancel"))
                        {
                            Undo.RecordObject(set, "Remove Melee Attack");
                            set.meleeAttacks.RemoveAt(attackIndex);
                            OnDirectMeleeAttackListMutation(set, "remove-melee-attack");
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                if (!expanded)
                    continue;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Presentation", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(presentationModeProperty, new GUIContent("Presentation Mode"));
                bool usesPhasedPresentation =
                    (WeaponMeleePresentationMode)presentationModeProperty.enumValueIndex == WeaponMeleePresentationMode.Phased;
                if (usesPhasedPresentation)
                {
                    EditorGUILayout.PropertyField(
                        clipProperty,
                        new GUIContent("Optional Single Clip", "Optional single clip. Phased timing/export derives from the phased clip sets directly."));
                    EditorGUILayout.HelpBox(
                        "Phased melee uses the same runtime action layer and lower-body unlock rules as single-clip melee, with start/loop/end clips advanced as segments.",
                        MessageType.None);
                    EditorGUILayout.PropertyField(phasedGroundProperty, new GUIContent("Ground Phased Clips"), true);
                    EditorGUILayout.PropertyField(phasedAirProperty, new GUIContent("Air Phased Clips"), true);
                    EditorGUILayout.PropertyField(
                        drivePhasesFromSpecialMovementProperty,
                        new GUIContent(
                            "Drive Phases From Special Movement",
                            "Use only for movement-coupled phased attacks such as Charge. Start plays once, Loop holds while special movement is active, and End plays when special movement ends."));
                }
                else
                {
                    EditorGUILayout.PropertyField(clipProperty, new GUIContent("Clip"));
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(
                        startupTrimSecondsProperty,
                        new GUIContent(
                            "Startup Trim",
                            "Seconds skipped from the clip start. Keep OnStrikeHit on the physical contact pose; effective gameplay timing subtracts this trim."));
                    bool startupTrimChanged = EditorGUI.EndChangeCheck();

                    AnimationClip? assignedClip = clipProperty.objectReferenceValue as AnimationClip;
                    if (startupTrimChanged && assignedClip != null)
                        _startupTrimChangedClips.Add(assignedClip);
                    if (assignedClip != null
                        && CombatAnimationEvents.TryGetEventTime(
                            assignedClip,
                            CombatAnimationEvents.OnStrikeHit,
                            out float firstHitSeconds))
                    {
                        float resolvedTrim = Mathf.Clamp(
                            startupTrimSecondsProperty.floatValue,
                            0f,
                            firstHitSeconds);
                        EditorGUILayout.HelpBox(
                            $"Playback begins at {resolvedTrim:0.000}s. First contact is {Mathf.Max(0f, firstHitSeconds - resolvedTrim):0.000}s after input. Hit windows and the server manifest synchronize automatically after editing stops; republishing remains explicit.",
                            MessageType.None);
                    }
                }

                EditorGUILayout.HelpBox(
                    usesPhasedPresentation
                        ? "Presentation timing is read from stamped events on the phased clips: OnLowerBodyUnlock and OnVisualInterruptible. Lower-body blend-out uses the runtime default."
                        : "Presentation timing is read from stamped events on the clip: OnStrikeHit, OnLowerBodyUnlock, and OnVisualInterruptible. Lower-body blend-out uses the runtime default.",
                    MessageType.None);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Combat", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(combatProperty, new GUIContent("Combat"), true);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawMeleeAnimationPreviewPane(CombatAnimationSet set)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Animation Preview", EditorStyles.boldLabel);

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                DestroyAttackPreview();
                EditorGUILayout.HelpBox("Animation preview is disabled while entering or running Play Mode.", MessageType.None);
                return;
            }

            bool hasMeleeAttacks = set.MeleeAttackCount > 0;
            bool hasSpellAnimations = set.spells != null && set.spells.Length > 0;
            if (!hasMeleeAttacks && !hasSpellAnimations)
            {
                EditorGUILayout.HelpBox("Add a melee attack or spell animation to preview it.", MessageType.None);
                DestroyAttackPreview();
                return;
            }

            string sourceKey = BuildSessionKey("animation-preview.source");
            int selectedSource = Mathf.Clamp(SessionState.GetInt(sourceKey, 0), 0, 1);
            if (!hasMeleeAttacks)
                selectedSource = 1;
            else if (!hasSpellAnimations)
                selectedSource = 0;

            if (hasMeleeAttacks && hasSpellAnimations)
            {
                int newSelectedSource = EditorGUILayout.Popup(
                    "Source",
                    selectedSource,
                    new[] { "Melee Attacks", "Spell Actions" });
                if (newSelectedSource != selectedSource)
                {
                    selectedSource = newSelectedSource;
                    SessionState.SetInt(sourceKey, selectedSource);
                    _attackPreviewTime = 0f;
                    _attackPreviewPlaying = false;
                }
            }

            List<AttackPreviewClipOption> clipOptions;
            int previewSelectionKey;
            string clipKey;
            if (selectedSource == 1)
            {
                if (!TryDrawSpellPreviewSelection(set, out clipOptions, out previewSelectionKey, out clipKey))
                    return;
            }
            else
            {
                if (!TryDrawMeleePreviewSelection(set, out clipOptions, out previewSelectionKey, out clipKey))
                    return;
            }

            if (clipOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("The selected entry has no animation clip assigned yet.", MessageType.Warning);
                DestroyAttackPreview();
                return;
            }

            int selectedClipIndex = Mathf.Clamp(SessionState.GetInt(clipKey, 0), 0, clipOptions.Count - 1);
            string[] clipLabels = new string[clipOptions.Count];
            for (int i = 0; i < clipOptions.Count; i++)
                clipLabels[i] = clipOptions[i].Label;

            int newSelectedClipIndex = EditorGUILayout.Popup("Clip", selectedClipIndex, clipLabels);
            if (newSelectedClipIndex != selectedClipIndex)
            {
                selectedClipIndex = newSelectedClipIndex;
                SessionState.SetInt(clipKey, selectedClipIndex);
                _attackPreviewTime = 0f;
                _attackPreviewPlaying = false;
            }

            AnimationClip previewClip = clipOptions[selectedClipIndex].Clip;
            EnsureAttackPreview(set, previewSelectionKey, previewClip);
            UpdateAttackPreviewPlayback(previewClip);

            Rect previewRect = GUILayoutUtility.GetRect(
                10f,
                PreviewHeight,
                GUILayout.ExpandWidth(true),
                GUILayout.MinHeight(PreviewHeight));
            DrawAttackPreview(previewRect);
            HandleAttackPreviewCameraInput(previewRect);

            using (new EditorGUILayout.HorizontalScope())
            {
                string playLabel = _attackPreviewPlaying ? "Pause" : "Play";
                if (GUILayout.Button(playLabel, GUILayout.Width(64f)))
                {
                    _attackPreviewPlaying = !_attackPreviewPlaying;
                    _attackPreviewLastEditorTime = EditorApplication.timeSinceStartup;
                }

                if (GUILayout.Button("Restart", GUILayout.Width(70f)))
                {
                    _attackPreviewTime = ResolveAttackPreviewStartTime();
                    SampleAttackPreview();
                }

                GUILayout.Label(
                    $"{_attackPreviewTime:0.000}s / {previewClip.length:0.000}s",
                    GUILayout.Width(130f));
            }

            EditorGUI.BeginChangeCheck();
            float scrubbedTime = EditorGUILayout.Slider(
                "Timeline",
                _attackPreviewTime,
                0f,
                Mathf.Max(0.001f, previewClip.length));
            if (EditorGUI.EndChangeCheck())
            {
                _attackPreviewTime = scrubbedTime;
                _attackPreviewPlaying = false;
                SampleAttackPreview();
            }

            if (_attackPreviewPlaying)
                Repaint();
        }

        private bool TryDrawMeleePreviewSelection(
            CombatAnimationSet set,
            out List<AttackPreviewClipOption> clipOptions,
            out int previewSelectionKey,
            out string clipKey)
        {
            clipOptions = new List<AttackPreviewClipOption>();
            previewSelectionKey = 0;
            clipKey = string.Empty;

            if (set.MeleeAttackCount <= 0)
            {
                EditorGUILayout.HelpBox("Add a melee attack to preview its animation.", MessageType.None);
                DestroyAttackPreview();
                return false;
            }

            string previewStrikeKey = BuildSessionKey("attack-preview.strike");
            int selectedStrikeIndex = Mathf.Clamp(
                SessionState.GetInt(previewStrikeKey, 1),
                1,
                set.MeleeAttackCount);

            string[] attackLabels = new string[set.MeleeAttackCount];
            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                WeaponStrikeCombatAuthoring combat = set.GetStrikeCombat(strikeIndex);
                AnimationClip? strikeClip = set.GetStrikeClip(strikeIndex);
                attackLabels[strikeIndex - 1] =
                    $"Attack {strikeIndex}: {combat.AuthoredStrikeIdOrDefault} [{(strikeClip != null ? strikeClip.name : "no clip")}]";
            }

            int newSelectedStrike = EditorGUILayout.Popup("Attack", selectedStrikeIndex - 1, attackLabels) + 1;
            if (newSelectedStrike != selectedStrikeIndex)
            {
                selectedStrikeIndex = newSelectedStrike;
                SessionState.SetInt(previewStrikeKey, selectedStrikeIndex);
                _attackPreviewTime = 0f;
                _attackPreviewPlaying = false;
            }

            WeaponMeleeAttackAuthoring attack = set.meleeAttacks[selectedStrikeIndex - 1];
            clipOptions = BuildAttackPreviewClipOptions(attack);
            previewSelectionKey = selectedStrikeIndex;
            clipKey = BuildSessionKey($"attack-preview.clip.{selectedStrikeIndex}");
            return true;
        }

        private bool TryDrawSpellPreviewSelection(
            CombatAnimationSet set,
            out List<AttackPreviewClipOption> clipOptions,
            out int previewSelectionKey,
            out string clipKey)
        {
            clipOptions = new List<AttackPreviewClipOption>();
            previewSelectionKey = 0;
            clipKey = string.Empty;

            if (set.spells == null || set.spells.Length == 0)
            {
                EditorGUILayout.HelpBox("Add a spell animation entry to preview its animation.", MessageType.None);
                DestroyAttackPreview();
                return false;
            }

            string previewSpellKey = BuildSessionKey("attack-preview.spell");
            int selectedSpellIndex = Mathf.Clamp(
                SessionState.GetInt(previewSpellKey, 0),
                0,
                set.spells.Length - 1);

            string[] spellLabels = new string[set.spells.Length];
            for (int spellIndex = 0; spellIndex < set.spells.Length; spellIndex++)
            {
                WeaponSpellAnimationEntry spell = set.spells[spellIndex];
                string spellId = string.IsNullOrWhiteSpace(spell.SpellIdOrEmpty)
                    ? "<missing spell id>"
                    : spell.SpellIdOrEmpty;
                spellLabels[spellIndex] =
                    $"{spellId} [{DescribeSpellPreviewClips(spell)}]";
            }

            int newSelectedSpellIndex = EditorGUILayout.Popup("Spell", selectedSpellIndex, spellLabels);
            if (newSelectedSpellIndex != selectedSpellIndex)
            {
                selectedSpellIndex = newSelectedSpellIndex;
                SessionState.SetInt(previewSpellKey, selectedSpellIndex);
                _attackPreviewTime = 0f;
                _attackPreviewPlaying = false;
            }

            WeaponSpellAnimationEntry selectedSpell = set.spells[selectedSpellIndex];
            clipOptions = BuildSpellPreviewClipOptions(selectedSpell);
            previewSelectionKey = -(selectedSpellIndex + 1);
            clipKey = BuildSessionKey($"attack-preview.spellClip.{selectedSpellIndex}");
            return true;
        }

        private static List<AttackPreviewClipOption> BuildAttackPreviewClipOptions(WeaponMeleeAttackAuthoring attack)
        {
            var options = new List<AttackPreviewClipOption>();
            AddAttackPreviewClipOption(options, "Clip", attack.clip);

            if (attack.UsesPhasedPresentation)
            {
                AddAttackPreviewClipOption(options, "Ground Start", attack.phasedGround.start);
                AddAttackPreviewClipOption(options, "Ground Loop", attack.phasedGround.loop);
                AddAttackPreviewClipOption(options, "Ground End", attack.phasedGround.end);
                AddAttackPreviewClipOption(options, "Air Start", attack.phasedAir.start);
                AddAttackPreviewClipOption(options, "Air Loop", attack.phasedAir.loop);
                AddAttackPreviewClipOption(options, "Air End", attack.phasedAir.end);
            }

            return options;
        }

        private static List<AttackPreviewClipOption> BuildSpellPreviewClipOptions(WeaponSpellAnimationEntry spell)
        {
            var options = new List<AttackPreviewClipOption>();
            AddAttackPreviewClipOption(options, "Ground", spell.ground);
            AddAttackPreviewClipOption(options, "Air", spell.air);
            AddAttackPreviewClipOption(options, "Hold Enter", spell.holdOverride.enter);
            AddAttackPreviewClipOption(options, "Hold Loop", spell.holdOverride.idleLoop);
            return options;
        }

        private static string DescribeSpellPreviewClips(WeaponSpellAnimationEntry spell)
        {
            bool hasGround = spell.ground != null;
            bool hasAir = spell.air != null;
            if (hasGround && hasAir)
                return $"{spell.ground!.name} / {spell.air!.name}";
            if (hasGround)
                return spell.ground!.name;
            if (hasAir)
                return $"air: {spell.air!.name}";
            if (spell.holdOverride.IsPlayable)
                return $"{spell.holdOverride.EnterOrIdle!.name} / {spell.holdOverride.IdleOrEnter!.name}";
            if (spell.UsesHoldPresentation)
                return "default hold";
            return "no clip";
        }

        private static void AddAttackPreviewClipOption(
            List<AttackPreviewClipOption> options,
            string label,
            AnimationClip? clip)
        {
            if (clip == null)
                return;

            options.Add(new AttackPreviewClipOption($"{label}: {clip.name}", clip));
        }

        private void EnsureAttackPreview(CombatAnimationSet set, int strikeIndex, AnimationClip previewClip)
        {
            if (_attackPreviewUtility != null
                && _attackPreviewInstance != null
                && ReferenceEquals(_attackPreviewSet, set)
                && _attackPreviewStrikeIndex == strikeIndex
                && ReferenceEquals(_attackPreviewClip, previewClip))
            {
                return;
            }

            DestroyAttackPreview();

            _attackPreviewSet = set;
            _attackPreviewStrikeIndex = strikeIndex;
            _attackPreviewClip = previewClip;
            _attackPreviewError = string.Empty;
            _attackPreviewTime = Mathf.Clamp(
                _attackPreviewTime > 0.001f ? _attackPreviewTime : ResolveAttackPreviewStartTime(),
                0f,
                previewClip.length);
            _attackPreviewLastEditorTime = EditorApplication.timeSinceStartup;

            GameObject? prefab = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            if (prefab == null)
                return;

            _attackPreviewUtility = new PreviewRenderUtility();
            _attackPreviewUtility.cameraFieldOfView = 30f;
            _attackPreviewUtility.lights[0].intensity = 1.35f;
            _attackPreviewUtility.lights[0].transform.rotation = Quaternion.Euler(45f, 35f, 0f);
            _attackPreviewUtility.lights[1].intensity = 0.65f;
            _attackPreviewUtility.lights[1].transform.rotation = Quaternion.Euler(340f, 220f, 0f);

            _attackPreviewInstance = Instantiate(prefab);
            _attackPreviewInstance.name = $"{prefab.name}_AnimationSetPreview";
            StarterAssetsRuntimeStripper.StripFrom(_attackPreviewInstance);
            _attackPreviewInstance.transform.position = Vector3.zero;
            _attackPreviewInstance.transform.rotation = Quaternion.identity;

            RuntimeAvatarController? avatarController =
                _attackPreviewInstance.GetComponent<RuntimeAvatarController>();
            if (avatarController == null)
                avatarController = _attackPreviewInstance.AddComponent<RuntimeAvatarController>();
            avatarController.SetVisualRootParent(_attackPreviewInstance.transform);

            CharacterAppearanceSelection previewAppearance =
                CharacterAppearanceSelection.DefaultHumanMale();
            previewAppearance.outfitId = string.Empty;
            string previewAppearanceSignature = RuntimeAvatarController.SignatureFor(previewAppearance);
            if (!avatarController.Apply(
                    previewAppearance,
                    previewAppearanceSignature,
                    out RuntimeAvatarBinding binding,
                    out string appearanceError))
            {
                _attackPreviewError = $"Runtime avatar appearance could not be assembled: {appearanceError}";
                Debug.LogWarning($"[{nameof(CombatAnimationSetEditor)}] {_attackPreviewError}", set);
                DestroyImmediate(_attackPreviewInstance);
                _attackPreviewInstance = null;
                return;
            }

            WeaponAttachmentController? attachments =
                _attackPreviewInstance.GetComponentInChildren<WeaponAttachmentController>(true);
            if (attachments != null)
            {
                attachments.Initialize();
                attachments.BindMounts(binding.Mounts);
                attachments.ApplyAnimationSet(set);
                attachments.SetInCombat(true);
            }

            SetHideFlagsRecursive(_attackPreviewInstance, HideFlags.HideAndDontSave);
            _attackPreviewAnimator = binding.Animator;
            CreateAttackPreviewGraph(previewClip);
            _attackPreviewUtility.AddSingleGO(_attackPreviewInstance);
            SampleAttackPreview();
        }

        private void DestroyAttackPreview()
        {
            DestroyAttackPreviewGraph();

            if (_attackPreviewUtility != null)
            {
                _attackPreviewUtility.Cleanup();
                _attackPreviewUtility = null;
            }

            if (_attackPreviewInstance != null)
            {
                DestroyImmediate(_attackPreviewInstance);
            }

            _attackPreviewInstance = null;
            _attackPreviewAnimator = null;
            _attackPreviewSet = null;
            _attackPreviewClip = null;
            _attackPreviewError = string.Empty;
            _attackPreviewStrikeIndex = 0;
            _attackPreviewPlaying = false;
        }

        private void CreateAttackPreviewGraph(AnimationClip previewClip)
        {
            if (_attackPreviewAnimator == null)
                return;

            _attackPreviewGraph = PlayableGraph.Create("CombatAnimationSetEditorPreview");
            _attackPreviewGraphCreated = _attackPreviewGraph.IsValid();
            _attackPreviewGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _attackPreviewPlayable = AnimationClipPlayable.Create(_attackPreviewGraph, previewClip);
            _attackPreviewPlayable.SetApplyFootIK(false);
            _attackPreviewPlayable.SetApplyPlayableIK(false);

            AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                _attackPreviewGraph,
                "PreviewAnimation",
                _attackPreviewAnimator);
            output.SetSourcePlayable(_attackPreviewPlayable);
            _attackPreviewGraph.Play();
        }

        private void DestroyAttackPreviewGraph()
        {
            if (_attackPreviewGraph.IsValid())
                _attackPreviewGraph.Destroy();

            _attackPreviewGraphCreated = false;
        }

        private static void SetHideFlagsRecursive(GameObject root, HideFlags flags)
        {
            root.hideFlags = flags;
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
                children[i].gameObject.hideFlags = flags;
        }

        private void UpdateAttackPreviewPlayback(AnimationClip previewClip)
        {
            if (!_attackPreviewPlaying)
                return;

            if (previewClip.length <= 0f)
            {
                _attackPreviewTime = 0f;
                _attackPreviewPlaying = false;
                SampleAttackPreview();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float delta = Mathf.Max(0f, (float)(now - _attackPreviewLastEditorTime));
            _attackPreviewLastEditorTime = now;
            _attackPreviewTime += delta;
            if (_attackPreviewTime > previewClip.length)
                _attackPreviewTime = previewClip.isLooping ? Mathf.Repeat(_attackPreviewTime, previewClip.length) : previewClip.length;

            if (!previewClip.isLooping && Mathf.Approximately(_attackPreviewTime, previewClip.length))
                _attackPreviewPlaying = false;

            SampleAttackPreview();
        }

        private void SampleAttackPreview()
        {
            if (_attackPreviewInstance == null || _attackPreviewClip == null)
                return;

            _attackPreviewTime = Mathf.Clamp(_attackPreviewTime, 0f, Mathf.Max(0f, _attackPreviewClip.length));
            if (_attackPreviewGraphCreated && _attackPreviewPlayable.IsValid())
            {
                _attackPreviewPlayable.SetTime(_attackPreviewTime);
                _attackPreviewGraph.Evaluate(0f);
                return;
            }

            _attackPreviewClip.SampleAnimation(_attackPreviewInstance, _attackPreviewTime);
        }

        private float ResolveAttackPreviewStartTime()
        {
            if (_attackPreviewSet == null
                || _attackPreviewClip == null
                || _attackPreviewStrikeIndex <= 0
                || _attackPreviewStrikeIndex > _attackPreviewSet.MeleeAttackCount)
            {
                return 0f;
            }

            WeaponMeleeAttackAuthoring attack =
                _attackPreviewSet.meleeAttacks[_attackPreviewStrikeIndex - 1];
            return !attack.UsesPhasedPresentation && ReferenceEquals(attack.clip, _attackPreviewClip)
                ? attack.ResolveStartupTrimSeconds()
                : 0f;
        }

        private void DrawAttackPreview(Rect previewRect)
        {
            if (_attackPreviewUtility == null || _attackPreviewInstance == null)
            {
                string message = string.IsNullOrWhiteSpace(_attackPreviewError)
                    ? "Runtime avatar prefab could not be loaded."
                    : _attackPreviewError;
                EditorGUI.HelpBox(previewRect, message, MessageType.Warning);
                return;
            }

            if (Event.current.type != EventType.Repaint)
                return;

            Bounds bounds = CalculateAttackPreviewBounds(_attackPreviewInstance);
            Camera camera = _attackPreviewUtility.camera;
            camera.clearFlags = CameraClearFlags.Color;
            camera.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1f);

            float radius = Mathf.Max(0.75f, bounds.extents.magnitude);
            float distance = radius * 2.8f * Mathf.Clamp(_attackPreviewDistanceMultiplier, PreviewMinDistance, PreviewMaxDistance);
            Quaternion orbit = Quaternion.Euler(_attackPreviewOrbit.y, _attackPreviewOrbit.x, 0f);
            Vector3 center = bounds.center + Vector3.up * Mathf.Min(0.4f, radius * 0.2f);
            Vector3 forward = orbit * Vector3.forward;
            camera.transform.position = center - forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = Mathf.Max(50f, distance + radius * 4f);

            _attackPreviewUtility.BeginPreview(previewRect, GUIStyle.none);
            _attackPreviewUtility.Render(true);
            Texture texture = _attackPreviewUtility.EndPreview();
            GUI.DrawTexture(previewRect, texture, ScaleMode.StretchToFill, false);
        }

        private static Bounds CalculateAttackPreviewBounds(GameObject previewInstance)
        {
            Renderer[] renderers = previewInstance.GetComponentsInChildren<Renderer>(false);
            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds ? bounds : new Bounds(Vector3.up, Vector3.one * 2f);
        }

        private void HandleAttackPreviewCameraInput(Rect previewRect)
        {
            Event current = Event.current;
            if (!previewRect.Contains(current.mousePosition))
                return;

            if (current.type == EventType.MouseDrag && current.button == 0)
            {
                _attackPreviewOrbit.x += current.delta.x;
                _attackPreviewOrbit.y = Mathf.Clamp(_attackPreviewOrbit.y + current.delta.y, -80f, 80f);
                current.Use();
                Repaint();
            }
            else if (current.type == EventType.ScrollWheel)
            {
                _attackPreviewDistanceMultiplier = Mathf.Clamp(
                    _attackPreviewDistanceMultiplier + current.delta.y * 0.05f,
                    PreviewMinDistance,
                    PreviewMaxDistance);
                current.Use();
                Repaint();
            }
        }

        private static List<(MessageType type, string message)> CollectStrikeValidationMessages(
            CombatAnimationSet set)
        {
            var messages = new List<(MessageType type, string message)>();
            var strikeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= set.MeleeAttackCount; i++)
            {
                var authored = set.GetStrikeCombat(i);
                if (!string.IsNullOrWhiteSpace(authored.AuthoredStrikeIdOrDefault))
                    strikeIds.Add(authored.AuthoredStrikeIdOrDefault);
            }

            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                var clip = set.GetStrikeClip(strikeIndex);
                var strike = set.GetStrikeCombat(strikeIndex);
                WeaponMeleeAttackAuthoring attack = set.meleeAttacks[strikeIndex - 1];
                bool hasHitEvents = attack.TryBuildHitWindowMirrorFromEvents(
                    out WeaponStrikeHitWindowAuthoring[] eventHitWindows);
                WeaponStrikeHitWindowAuthoring[] hitWindows = hasHitEvents
                    ? eventHitWindows
                    : strike.GetResolvedHitWindows();
                string strikeLabel = $"Strike {strikeIndex} ({strike.AuthoredStrikeIdOrDefault})";

                if (!hasHitEvents)
                {
                    messages.Add((
                        MessageType.Warning,
                        $"{strikeLabel}: no {CombatAnimationEvents.OnStrikeHit} event is authored; this attack is still using its legacy serialized hit-window fallback. Stamp the clip to migrate it."));
                }
                else if (!HitWindowMirrorsMatch(strike.hitWindows, eventHitWindows))
                {
                    messages.Add((
                        MessageType.Warning,
                        $"{strikeLabel}: its compatibility hit-window mirror is stale. Open the assigned clip in Event Stamper and use Synchronize This Clip Now."));
                }

                if (attack.startupTrimSeconds < 0f)
                {
                    messages.Add((MessageType.Error, $"{strikeLabel}: startup trim must be non-negative."));
                }
                else if (attack.UsesPhasedPresentation && attack.startupTrimSeconds > 0f)
                {
                    messages.Add((MessageType.Error, $"{strikeLabel}: startup trim is supported only for single-clip melee."));
                }
                else if (attack.startupTrimSeconds > 0f)
                {
                    if (!attack.TryGetStrikeHitEventTimesSeconds(out float[] authoredHitTimes)
                        || authoredHitTimes.Length == 0)
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: startup trim requires an authored {CombatAnimationEvents.OnStrikeHit} event."));
                    }
                    else if (attack.startupTrimSeconds > authoredHitTimes[0] + 0.0001f)
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: startup trim {attack.startupTrimSeconds:0.000}s exceeds first contact at {authoredHitTimes[0]:0.000}s. Trim to the contact pose or earlier."));
                    }
                }

                if (string.IsNullOrWhiteSpace(strike.id))
                    messages.Add((MessageType.Error, $"{strikeLabel}: strike id is required."));
                else if (!IsUpperSnakeIdentifier(strike.AuthoredStrikeIdOrDefault))
                    messages.Add((MessageType.Error, $"{strikeLabel}: authored strike id must use uppercase snake case, e.g. SKYFALL_1."));

                string runtimeSlotId = strike.RuntimeSlotIdOrDefault;
                if (string.IsNullOrWhiteSpace(runtimeSlotId))
                {
                    messages.Add((MessageType.Error, $"{strikeLabel}: runtime slot id could not be resolved."));
                }
                else
                {
                    if (IsPlaceholderRuntimeSlotId(runtimeSlotId))
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: runtime slot id '{runtimeSlotId}' is a placeholder. Choose a deliberate internal runtime slot id."));
                    }

                    if (!IsLowerSnakeIdentifier(runtimeSlotId))
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: runtime slot id must use lowercase snake case, e.g. skyfall_1 or utility_1."));
                    }
                }

                float timingReferenceLengthSeconds = set.GetStrikeTimingReferenceLengthSeconds(strikeIndex);
                bool usesPhasedPresentation = attack.UsesPhasedPresentation;
                if (!usesPhasedPresentation && clip == null)
                    messages.Add((MessageType.Error, $"{strikeLabel}: strike clip is missing."));
                if (timingReferenceLengthSeconds <= 0f)
                    messages.Add((MessageType.Error, $"{strikeLabel}: attack timing could not be derived from its presentation clips."));
                if (hitWindows.Length == 0)
                    messages.Add((MessageType.Error, $"{strikeLabel}: at least one hit window is required."));

                if (usesPhasedPresentation)
                {
                    bool groundPlayable = attack.phasedGround.IsPlayable;
                    bool airPlayable = attack.phasedAir.IsPlayable;
                    bool groundPartial = attack.phasedGround.HasAny && !groundPlayable;
                    bool airPartial = attack.phasedAir.HasAny && !airPlayable;

                    if (!groundPlayable && !airPlayable)
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: phased presentation requires at least one playable clip set using any two of start/loop/end."));
                    }

                    if (groundPartial)
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: ground phased clips need at least two of start/loop/end, or leave the set empty."));
                    }

                    if (airPartial)
                    {
                        messages.Add((
                            MessageType.Error,
                            $"{strikeLabel}: air phased clips need at least two of start/loop/end, or leave the set empty."));
                    }
                }

                if (hitWindows != null)
                {
                    for (int hitIndex = 0; hitIndex < hitWindows.Length; hitIndex++)
                    {
                        var hitWindow = hitWindows[hitIndex];
                        if (hitWindow.timeNormalized < 0f || hitWindow.timeNormalized > 1f)
                        {
                            messages.Add((
                                MessageType.Error,
                                $"{strikeLabel}: hit {hitIndex + 1} timeNormalized must stay inside [0, 1]."));
                        }
                    }
                }

                if (strike.recoveryMs < 0f)
                    messages.Add((MessageType.Error, $"{strikeLabel}: recovery must be non-negative."));

                if (strike.usesProjectileDelivery)
                {
                    string projectileId = strike.ProjectileIdOrDefault;
                    if (CombatVFXRegistry.ResolveSharedPrefab(projectileId) == null)
                    {
                        messages.Add((
                            MessageType.Warning,
                            $"{strikeLabel}: projectile id '{projectileId}' has no prefab entry in CombatVFXRegistry."));
                    }
                }

                string comboFrom = strike.ComboFromOrEmpty;
                if (!string.IsNullOrEmpty(comboFrom))
                {
                    if (string.Equals(comboFrom, strike.AuthoredStrikeIdOrDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add((MessageType.Error, $"{strikeLabel}: Combo From cannot reference this strike itself."));
                    }
                    else if (!strikeIds.Contains(comboFrom))
                    {
                        messages.Add((MessageType.Error, $"{strikeLabel}: Combo From '{comboFrom}' does not match any strike id in this animation set."));
                    }

                    if (strike.ComboOpenMsInt <= 0)
                    {
                        messages.Add((MessageType.Error, $"{strikeLabel}: Combo Chain Time must be greater than 0 when Combo From is set."));
                    }
                }
            }

            string autoAttackStrikeId = set.AutoAttackAuthoredStrikeIdOrDefault;
            if (!string.IsNullOrWhiteSpace(autoAttackStrikeId) && !IsUpperSnakeIdentifier(autoAttackStrikeId))
            {
                messages.Add((
                    MessageType.Error,
                    $"Auto Attack: authored strike id must use uppercase snake case, e.g. AUTO_ATTACK_1. Current value: {autoAttackStrikeId}."));
            }

            string autoAttackRuntimeSlotId = CombatActionIds.NormalizeRuntimeActionReference(set.AutoAttackAuthoredStrikeIdOrDefault);
            if (string.IsNullOrWhiteSpace(autoAttackRuntimeSlotId))
            {
                messages.Add((MessageType.Error, "Auto Attack: runtime slot id could not be resolved."));
            }
            else
            {
                if (IsPlaceholderRuntimeSlotId(autoAttackRuntimeSlotId))
                {
                    messages.Add((
                        MessageType.Error,
                        $"Auto Attack: runtime slot id '{autoAttackRuntimeSlotId}' is a placeholder. Choose a deliberate internal runtime slot id."));
                }

                if (!IsLowerSnakeIdentifier(autoAttackRuntimeSlotId))
                {
                    messages.Add((
                        MessageType.Error,
                        $"Auto Attack: runtime slot id must use lowercase snake case, e.g. auto_attack_1. Current value: {autoAttackRuntimeSlotId}."));
                }
            }

            if (set.autoAttackVisualSequenceActionIds == null || set.autoAttackVisualSequenceActionIds.Count == 0)
            {
                messages.Add((MessageType.Error, "Auto Attack: visual sequence must contain at least one authored strike id."));
            }
            else
            {
                if (set.autoAttackVisualSequenceActionIds.Count > 1 && set.autoAttackSequenceIntervalMs <= 0)
                {
                    messages.Add((
                        MessageType.Error,
                        "Auto Attack: Sequence Interval Ms must be positive when the visual sequence contains multiple strikes."));
                }

                var sequenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string previousActionId = string.Empty;
                for (int sequenceIndex = 0; sequenceIndex < set.autoAttackVisualSequenceActionIds.Count; sequenceIndex++)
                {
                    string actionId = set.autoAttackVisualSequenceActionIds[sequenceIndex]?.Trim() ?? string.Empty;
                    string sequenceLabel = $"Auto Attack: visual sequence element {sequenceIndex}";
                    if (string.IsNullOrWhiteSpace(actionId))
                    {
                        messages.Add((MessageType.Error, $"{sequenceLabel} must not be empty."));
                        continue;
                    }
                    if (!strikeIds.Contains(actionId))
                    {
                        messages.Add((MessageType.Error, $"{sequenceLabel} '{actionId}' does not match any strike id in this animation set."));
                        continue;
                    }
                    if (!sequenceIds.Add(actionId))
                    {
                        messages.Add((MessageType.Error, $"{sequenceLabel} repeats '{actionId}'. Auto-attack sequence entries must be unique."));
                        continue;
                    }

                    if (sequenceIndex > 0)
                    {
                        int strikeIndex = set.GetStrikeIndexForActionId(actionId);
                        WeaponStrikeCombatAuthoring strike = set.GetStrikeCombat(strikeIndex);
                        if (!string.Equals(strike.ComboFromOrEmpty, previousActionId, StringComparison.OrdinalIgnoreCase))
                        {
                            messages.Add((
                                MessageType.Error,
                                $"{sequenceLabel} '{actionId}' must author Combo From '{previousActionId}' so it remains part of the same authored attack chain."));
                        }
                    }

                    previousActionId = actionId;
                }
            }

            return messages;
        }

        private static bool HitWindowMirrorsMatch(
            WeaponStrikeHitWindowAuthoring[]? authored,
            WeaponStrikeHitWindowAuthoring[] resolved)
        {
            if (authored == null || authored.Length != resolved.Length)
                return false;

            for (int i = 0; i < authored.Length; i++)
            {
                if (!Mathf.Approximately(authored[i].timeNormalized, resolved[i].timeNormalized))
                    return false;
            }

            return true;
        }

        private static bool IsUpperSnakeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            if (value.StartsWith("_", StringComparison.Ordinal) || value.EndsWith("_", StringComparison.Ordinal))
                return false;

            bool previousUnderscore = false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                bool valid = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!valid)
                    return false;

                if (c == '_')
                {
                    if (previousUnderscore)
                        return false;
                    previousUnderscore = true;
                }
                else
                {
                    previousUnderscore = false;
                }
            }

            return true;
        }

        private static bool IsLowerSnakeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            if (value.StartsWith("_", StringComparison.Ordinal) || value.EndsWith("_", StringComparison.Ordinal))
                return false;

            bool previousUnderscore = false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!valid)
                    return false;

                if (c == '_')
                {
                    if (previousUnderscore)
                        return false;
                    previousUnderscore = true;
                }
                else
                {
                    previousUnderscore = false;
                }
            }

            return true;
        }

        private static bool IsPlaceholderRuntimeSlotId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Trim().ToLowerInvariant();
            return normalized is "this_id_doesnt_matter"
                or "todo"
                or "placeholder"
                or "temp"
                or "temporary";
        }

        private static void ImportCurrentManifest(CombatAnimationSet set)
        {
            if (!File.Exists(ExportPath))
            {
                EditorUtility.DisplayDialog("Melee Manifest Missing", $"Could not find:\n{ExportPath}", "OK");
                return;
            }

            var doc = DeserializeMeleeManifestDocument(File.ReadAllText(ExportPath));
            if (!TryResolveImportedStrikes(doc, set, out MeleeManifestStrike[] strikes, out string importError))
            {
                EditorUtility.DisplayDialog("Manifest Invalid", importError, "OK");
                return;
            }
            MeleeManifestProfile? importedProfile = FindImportedProfile(doc, set);
            MeleeManifestStrike[] importedMeleeStrikes = GetImportedAuthoredMeleeStrikes(importedProfile, strikes);

            BackupFile(AssetDatabase.GetAssetPath(set), "pre-import-animation-set");
            CombatAnimationSetProtection.MarkTrustedMutation(set, "manifest-import");
            Undo.RecordObject(set, "Import Shared Melee Manifest");
            set.EnsureMeleeAttackListSize(importedMeleeStrikes.Length);
            for (int i = 0; i < importedMeleeStrikes.Length; i++)
            {
                int strikeIndex = i + 1;
                var manifestStrike = importedMeleeStrikes[i];
                var authored = set.GetStrikeCombat(strikeIndex);
                var clip = set.GetStrikeClip(strikeIndex);
                float clipLengthMs = clip != null ? clip.length * 1000f : 0f;

                authored.id = string.IsNullOrWhiteSpace(manifestStrike.id)
                    ? (manifestStrike.slot_id ?? string.Empty)
                    : manifestStrike.id;
                authored.slotId = CombatActionIds.NormalizeRuntimeActionReference(
                    string.IsNullOrWhiteSpace(manifestStrike.slot_id) ? authored.id : manifestStrike.slot_id);
                authored.hitWindows = BuildImportedHitWindows(manifestStrike, clipLengthMs);
                authored.impactNormalized = authored.hitWindows.Length > 0
                    ? authored.hitWindows[0].timeNormalized
                    : 0f;
                authored.recoveryMs = manifestStrike.recovery_ms;
                authored.isGapCloser = false;
                authored.comboFrom = set.ResolveAuthoredStrikeIdForRuntimeAction(manifestStrike.combo_from ?? string.Empty);
                authored.comboOpenMs = manifestStrike.combo_open_ms;
                authored.comboGraceMs = manifestStrike.combo_grace_ms;
                set.SetStrikeCombat(strikeIndex, authored);
            }

            if (importedProfile != null)
            {
                set.autoAttackAuthoredStrikeId = importedProfile.auto_attack_strike_id ?? string.Empty;
                if (importedProfile.auto_attack_sequence != null && importedProfile.auto_attack_sequence.Length > 0)
                    set.autoAttackVisualSequenceActionIds = new List<string>(importedProfile.auto_attack_sequence);
                set.autoAttackSequenceIntervalMs = Mathf.Max(0, importedProfile.auto_attack_sequence_interval_ms);
            }

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            CombatAnimationSetProtection.RecordTrustedState(set, "manifest-import");
            CancelPendingAnimationSetPersist(set);
            InvalidateAvatarValidationCache(set);
            string importedAssetPath = AssetDatabase.GetAssetPath(set);
            if (!string.IsNullOrWhiteSpace(importedAssetPath))
                AssetDatabase.ImportAsset(importedAssetPath, ImportAssetOptions.ForceUpdate);
            InternalEditorUtility.RepaintAllViews();

            EditorUtility.DisplayDialog(
                "Import Complete",
                "Shared melee manifest import completed and the animation set asset was refreshed.",
                "OK");
        }

        private static MeleeManifestDocument BuildMergedExportDocument(CombatAnimationSet set)
        {
            string combatProfile = set.CombatProfileIdOrDefault;
            var replacement = set.BuildMeleeExport().profiles;
            if (replacement == null || replacement.Length == 0)
                return set.BuildMeleeExport();

            MeleeManifestDocument existing = File.Exists(ExportPath)
                ? DeserializeMeleeManifestDocument(File.ReadAllText(ExportPath))
                : new MeleeManifestDocument();

            var mergedProfiles = new System.Collections.Generic.List<MeleeManifestProfile>();
            bool replaced = false;

            if (existing.profiles != null)
            {
                foreach (var profile in existing.profiles)
                {
                    if (profile == null)
                        continue;

                    if (string.Equals(profile.combat_profile, combatProfile, System.StringComparison.OrdinalIgnoreCase))
                    {
                        mergedProfiles.Add(replacement[0]);
                        replaced = true;
                    }
                    else
                    {
                        mergedProfiles.Add(profile);
                    }
                }
            }

            if (!replaced)
                mergedProfiles.Add(replacement[0]);

            existing.profiles = mergedProfiles.ToArray();
            return existing;
        }

        /// <summary>
        /// JsonUtility materializes missing nullable nested objects on newer Unity
        /// versions. Preserve the JSON contract's optional projectile field explicitly
        /// so a one-strike timing sync cannot add projectile delivery to unrelated attacks.
        /// </summary>
        private static MeleeManifestDocument DeserializeMeleeManifestDocument(string json)
        {
            MeleeManifestDocument document = JsonUtility.FromJson<MeleeManifestDocument>(json)
                ?? new MeleeManifestDocument();
            int searchStart = 0;
            foreach (MeleeManifestProfile profile in document.profiles ?? Array.Empty<MeleeManifestProfile>())
            {
                if (profile == null)
                    continue;

                foreach (MeleeManifestStrike strike in profile.strikes ?? Array.Empty<MeleeManifestStrike>())
                {
                    if (strike == null)
                        continue;

                    if (!TryFindJsonObjectForStrike(json, strike.id, searchStart, out int objectStart, out int objectEnd))
                    {
                        throw new InvalidOperationException(
                            $"Could not preserve optional fields while reading melee strike '{strike.id}'.");
                    }

                    int projectileProperty = json.IndexOf(
                        "\"projectile\"",
                        objectStart,
                        objectEnd - objectStart + 1,
                        StringComparison.Ordinal);
                    if (projectileProperty < 0)
                        strike.projectile = null;

                    searchStart = objectEnd + 1;
                }
            }

            return document;
        }

        private static bool TryFindJsonObjectForStrike(
            string json,
            string strikeId,
            int searchStart,
            out int objectStart,
            out int objectEnd)
        {
            objectStart = -1;
            objectEnd = -1;
            System.Text.RegularExpressions.Match idMatch = Regex.Match(
                json,
                $"\"id\"\\s*:\\s*\"{Regex.Escape(strikeId)}\"",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            while (idMatch.Success && idMatch.Index < searchStart)
                idMatch = idMatch.NextMatch();
            if (!idMatch.Success)
                return false;

            objectStart = json.LastIndexOf('{', idMatch.Index);
            if (objectStart < 0)
                return false;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int index = objectStart; index < json.Length; index++)
            {
                char character = json[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '{')
                {
                    depth += 1;
                }
                else if (character == '}' && --depth == 0)
                {
                    objectEnd = index;
                    return true;
                }
            }

            return false;
        }

        private static string SerializeMeleeManifestDocument(MeleeManifestDocument document)
        {
            var builder = new StringBuilder(4096);
            builder.Append("{\n");
            AppendIndent(builder, 1);
            builder.Append("\"profiles\": [\n");
            for (int profileIndex = 0; profileIndex < document.profiles.Length; profileIndex++)
            {
                if (profileIndex > 0)
                    builder.Append(",\n");
                AppendMeleeManifestProfile(builder, document.profiles[profileIndex], 2);
            }

            builder.Append('\n');
            AppendIndent(builder, 1);
            builder.Append("]\n");
            builder.Append("}\n");
            return builder.ToString();
        }

        private static void AppendMeleeManifestProfile(StringBuilder builder, MeleeManifestProfile profile, int indent)
        {
            AppendIndent(builder, indent);
            builder.Append("{\n");
            AppendJsonProperty(builder, indent + 1, "combat_profile", profile.combat_profile, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "stagger_duration_f_ms", profile.stagger_duration_f_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "stagger_duration_b_ms", profile.stagger_duration_b_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "stagger_duration_l_ms", profile.stagger_duration_l_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "stagger_duration_r_ms", profile.stagger_duration_r_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "auto_attack_strike_id", profile.auto_attack_strike_id, trailingComma: true);
            AppendJsonStringArrayProperty(builder, indent + 1, "auto_attack_sequence", profile.auto_attack_sequence, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "auto_attack_sequence_interval_ms", profile.auto_attack_sequence_interval_ms, trailingComma: true);
            AppendIndent(builder, indent + 1);
            builder.Append("\"strikes\": [\n");
            for (int strikeIndex = 0; strikeIndex < profile.strikes.Length; strikeIndex++)
            {
                if (strikeIndex > 0)
                    builder.Append(",\n");
                AppendMeleeManifestStrike(builder, profile.strikes[strikeIndex], indent + 2);
            }

            builder.Append('\n');
            AppendIndent(builder, indent + 1);
            builder.Append("]\n");
            AppendIndent(builder, indent);
            builder.Append('}');
        }

        private static void AppendMeleeManifestStrike(StringBuilder builder, MeleeManifestStrike strike, int indent)
        {
            AppendIndent(builder, indent);
            builder.Append("{\n");
            AppendJsonProperty(builder, indent + 1, "id", strike.id, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "slot_id", strike.slot_id, trailingComma: true);
            AppendIndent(builder, indent + 1);
            builder.Append("\"hit_windows\": [\n");
            for (int hitIndex = 0; hitIndex < strike.hit_windows.Length; hitIndex++)
            {
                if (hitIndex > 0)
                    builder.Append(",\n");
                AppendMeleeManifestHitWindow(builder, strike.hit_windows[hitIndex], indent + 2);
            }

            builder.Append('\n');
            AppendIndent(builder, indent + 1);
            builder.Append("],\n");
            AppendJsonProperty(builder, indent + 1, "recovery_ms", strike.recovery_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "is_gap_closer", strike.is_gap_closer, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "combo_from", strike.combo_from, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "combo_open_ms", strike.combo_open_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "combo_grace_ms", strike.combo_grace_ms, trailingComma: true);
            AppendJsonProperty(builder, indent + 1, "aerial_execution_mode", strike.aerial_execution_mode, trailingComma: strike.projectile != null);
            if (strike.projectile != null)
            {
                AppendIndent(builder, indent + 1);
                builder.Append("\"projectile\": {\n");
                AppendMeleeManifestProjectile(builder, strike.projectile, indent + 2);
                AppendIndent(builder, indent + 1);
                builder.Append("}\n");
            }

            AppendIndent(builder, indent);
            builder.Append('}');
        }

        private static void AppendMeleeManifestHitWindow(StringBuilder builder, MeleeManifestHitWindow hitWindow, int indent)
        {
            AppendIndent(builder, indent);
            builder.Append("{\n");
            AppendJsonProperty(builder, indent + 1, "impact_delay_ms", hitWindow.impact_delay_ms, trailingComma: false);
            AppendIndent(builder, indent);
            builder.Append('}');
        }

        private static void AppendMeleeManifestProjectile(StringBuilder builder, MeleeManifestProjectile projectile, int indent)
        {
            AppendJsonProperty(builder, indent, "projectile_id", projectile.projectile_id, trailingComma: true);
            AppendJsonProperty(builder, indent, "speed", projectile.speed, trailingComma: true);
            AppendJsonProperty(builder, indent, "max_distance", projectile.max_distance, trailingComma: true);
            AppendJsonProperty(builder, indent, "radius", projectile.radius, trailingComma: true);
            AppendJsonProperty(builder, indent, "spawn_forward", projectile.spawn_forward, trailingComma: true);
            AppendJsonProperty(builder, indent, "spawn_height", projectile.spawn_height, trailingComma: true);
            AppendJsonProperty(builder, indent, "aim_height_scale", projectile.aim_height_scale, trailingComma: true);
            AppendJsonProperty(builder, indent, "requires_initial_line_of_sight", projectile.requires_initial_line_of_sight, trailingComma: true);
            AppendJsonProperty(builder, indent, "update_interval_seconds", projectile.update_interval_seconds, trailingComma: false);
        }

        private static void AppendJsonProperty(StringBuilder builder, int indent, string name, string value, bool trailingComma)
        {
            AppendIndent(builder, indent);
            AppendJsonString(builder, name);
            builder.Append(": ");
            AppendJsonString(builder, value ?? string.Empty);
            AppendPropertyTerminator(builder, trailingComma);
        }

        private static void AppendJsonProperty(StringBuilder builder, int indent, string name, int value, bool trailingComma)
        {
            AppendIndent(builder, indent);
            AppendJsonString(builder, name);
            builder.Append(": ");
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            AppendPropertyTerminator(builder, trailingComma);
        }

        private static void AppendJsonProperty(StringBuilder builder, int indent, string name, float value, bool trailingComma)
        {
            AppendIndent(builder, indent);
            AppendJsonString(builder, name);
            builder.Append(": ");
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            AppendPropertyTerminator(builder, trailingComma);
        }

        private static void AppendJsonProperty(StringBuilder builder, int indent, string name, bool value, bool trailingComma)
        {
            AppendIndent(builder, indent);
            AppendJsonString(builder, name);
            builder.Append(": ");
            builder.Append(value ? "true" : "false");
            AppendPropertyTerminator(builder, trailingComma);
        }

        private static void AppendJsonStringArrayProperty(
            StringBuilder builder,
            int indent,
            string name,
            string[]? values,
            bool trailingComma)
        {
            AppendIndent(builder, indent);
            AppendJsonString(builder, name);
            builder.Append(": [");
            string[] resolved = values ?? Array.Empty<string>();
            for (int index = 0; index < resolved.Length; index++)
            {
                if (index > 0)
                    builder.Append(", ");
                AppendJsonString(builder, resolved[index] ?? string.Empty);
            }
            builder.Append(']');
            AppendPropertyTerminator(builder, trailingComma);
        }

        private static void AppendPropertyTerminator(StringBuilder builder, bool trailingComma)
        {
            if (trailingComma)
                builder.Append(',');
            builder.Append('\n');
        }

        private static void AppendIndent(StringBuilder builder, int indent)
        {
            builder.Append(' ', indent * 4);
        }

        private static void AppendJsonString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(c))
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        private static bool TryBuildMergedExportDocument(
            CombatAnimationSet set,
            out MeleeManifestDocument? document,
            out string error)
        {
            try
            {
                document = BuildMergedExportDocument(set);
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                document = null;
                error = ex.Message;
                return false;
            }
        }

        private static List<string> BuildAnimatedPropWarnings(
            CombatAnimationSet set,
            string? runtimeAvatarPrefabPath,
            GameObject? runtimeAvatar,
            AvatarWeaponMounts? runtimeAvatarMounts)
        {
            var warnings = new List<string>();
            if (runtimeAvatar == null)
            {
                warnings.Add(
                    string.IsNullOrWhiteSpace(runtimeAvatarPrefabPath)
                        ? "No runtime avatar prefab could be resolved from Resources/PlayerArmature or Resources/PlayerArmature 1."
                        : $"Runtime avatar prefab is missing: {runtimeAvatarPrefabPath}");
                return warnings;
            }

            if (runtimeAvatarMounts == null)
            {
                warnings.Add(
                    $"Runtime avatar '{runtimeAvatar.name}' is missing {nameof(AvatarWeaponMounts)}; animated prop clips cannot be verified.");
                return warnings;
            }

            Transform avatarRoot = runtimeAvatar.transform;
            var requiredProps = CollectAnimatedPropRequirements(set);
            for (int requirementIndex = 0; requirementIndex < requiredProps.Count; requirementIndex++)
            {
                var requirement = requiredProps[requirementIndex];
                var animatedProp = avatarRoot.Find(requirement.Path);
                if (animatedProp == null)
                {
                    warnings.Add(
                        $"Runtime avatar '{runtimeAvatar.name}' is missing animated prop path '{requirement.Path}' required by clip '{requirement.DisplayName}'.");
                    continue;
                }

                if (!runtimeAvatarMounts.TryGetMount(requirement.MountId, out var resolvedMount))
                {
                    warnings.Add(
                        $"Runtime avatar '{runtimeAvatar.name}' is missing mount '{requirement.MountId}' required for animated prop '{requirement.DisplayName}'.");
                    continue;
                }

                if (!IsSameOrDescendant(animatedProp, resolvedMount))
                {
                    string mountPath = GetRelativePath(avatarRoot, resolvedMount);
                    warnings.Add(
                        $"Mount '{requirement.MountId}' resolves to '{mountPath}', but clips that animate '{requirement.DisplayName}' expect that mount to live on or under '{requirement.Path}'.");
                }
            }

            return warnings;
        }

        private static List<string> GetAnimatedPropWarningsCached(CombatAnimationSet set)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return BuildAnimatedPropWarningsUncached(set);

            string signature = BuildAnimatedPropValidationSignature(set);
            if (CachedAvatarValidations.TryGetValue(assetPath, out var cached) &&
                string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                return cached.Warnings;
            }

            var warnings = BuildAnimatedPropWarningsUncached(set);
            CachedAvatarValidations[assetPath] = new CachedAvatarValidation
            {
                Signature = signature,
                Warnings = warnings,
            };
            return warnings;
        }

        private static List<string> BuildAnimatedPropWarningsUncached(CombatAnimationSet set)
        {
            GameObject? runtimeAvatar = null;
            string? runtimeAvatarPrefabPath = RuntimeAvatarPrefabResolver.ResolveRuntimePlayerPrefabAssetPath();
            try
            {
                if (!string.IsNullOrWhiteSpace(runtimeAvatarPrefabPath) &&
                    AssetDatabase.LoadAssetAtPath<GameObject>(runtimeAvatarPrefabPath) != null)
                {
                    runtimeAvatar = PrefabUtility.LoadPrefabContents(runtimeAvatarPrefabPath);
                }

                var runtimeAvatarMounts = runtimeAvatar != null ? runtimeAvatar.GetComponent<AvatarWeaponMounts>() : null;
                return BuildAnimatedPropWarnings(set, runtimeAvatarPrefabPath, runtimeAvatar, runtimeAvatarMounts);
            }
            finally
            {
                if (runtimeAvatar != null)
                    PrefabUtility.UnloadPrefabContents(runtimeAvatar);
            }
        }

        private static string BuildAnimatedPropValidationSignature(CombatAnimationSet set)
        {
            var signature = new System.Text.StringBuilder();
            signature.Append(set.AnimationSetIdOrDefault);
            signature.Append("|combat-profile=");
            signature.Append(set.CombatProfileIdOrDefault);
            signature.Append("|runtime-avatar=");
            signature.Append(RuntimeAvatarPrefabResolver.ResolveRuntimePlayerPrefabAssetPath() ?? "<missing>");
            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                AppendClipSignature(signature, strikeIndex, "clip", set.GetStrikeClip(strikeIndex));
                WeaponMeleeAttackAuthoring attack = set.meleeAttacks[strikeIndex - 1];
                if (attack.UsesPhasedPresentation)
                {
                    AppendClipSignature(signature, strikeIndex, "ground-start", attack.phasedGround.start);
                    AppendClipSignature(signature, strikeIndex, "ground-loop", attack.phasedGround.loop);
                    AppendClipSignature(signature, strikeIndex, "ground-end", attack.phasedGround.end);
                    AppendClipSignature(signature, strikeIndex, "air-start", attack.phasedAir.start);
                    AppendClipSignature(signature, strikeIndex, "air-loop", attack.phasedAir.loop);
                    AppendClipSignature(signature, strikeIndex, "air-end", attack.phasedAir.end);
                }
            }

            return signature.ToString();
        }

        private static List<AnimatedPropRequirement> CollectAnimatedPropRequirements(CombatAnimationSet set)
        {
            var required = new List<AnimatedPropRequirement>();
            var seenPaths = new HashSet<string>(System.StringComparer.Ordinal);

            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                foreach (AnimationClip clip in EnumerateAttackPresentationClips(set, strikeIndex))
                {
                    var animatedPaths = GetAnimatedPaths(clip);
                    for (int requirementIndex = 0; requirementIndex < AnimatedPropRequirements.Length; requirementIndex++)
                    {
                        var requirement = AnimatedPropRequirements[requirementIndex];
                        if (!animatedPaths.Contains(requirement.Path) || !seenPaths.Add(requirement.Path))
                            continue;

                        required.Add(requirement);
                    }
                }
            }

            return required;
        }

        private static bool EnsureMeleeAttackListInitialized(CombatAnimationSet set)
        {
            bool changed = false;
            if (set.EnsureMeleeAttackListInitialized())
                changed = true;
            if (!changed)
                return false;

            CombatAnimationSetProtection.MarkTrustedMutation(set, "melee-attack-list-init");
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssetIfDirty(set);
            CombatAnimationSetProtection.RecordTrustedState(set, "melee-attack-list-init");
            InvalidateAvatarValidationCache(set);
            return true;
        }

        private void OnDirectMeleeAttackListMutation(CombatAnimationSet set, string reason)
        {
            serializedObject.UpdateIfRequiredOrScript();
            CombatAnimationSetProtection.MarkTrustedMutation(set, reason);
            EditorUtility.SetDirty(set);
            ScheduleAnimationSetPersist(set, reason);
            InvalidateAvatarValidationCache(set);
        }

        private static MeleeManifestStrike[] GetImportedAuthoredMeleeStrikes(
            MeleeManifestProfile? importedProfile,
            MeleeManifestStrike[] existingStrikes)
        {
            if (importedProfile?.strikes == null || importedProfile.strikes.Length == 0)
                return existingStrikes;

            string autoAttackStrikeId = importedProfile.auto_attack_strike_id ?? string.Empty;
            var filtered = new List<MeleeManifestStrike>(importedProfile.strikes.Length);
            foreach (var strike in importedProfile.strikes)
            {
                if (strike == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(autoAttackStrikeId) &&
                    string.Equals(strike.id, autoAttackStrikeId, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                filtered.Add(strike);
            }

            return filtered.ToArray();
        }

        private static HashSet<string> GetAnimatedPaths(AnimationClip clip)
        {
            var paths = new HashSet<string>(System.StringComparer.Ordinal);
            var curveBindings = AnimationUtility.GetCurveBindings(clip);
            for (int bindingIndex = 0; bindingIndex < curveBindings.Length; bindingIndex++)
            {
                string path = curveBindings[bindingIndex].path;
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int bindingIndex = 0; bindingIndex < objectBindings.Length; bindingIndex++)
            {
                string path = objectBindings[bindingIndex].path;
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths;
        }

        private static IEnumerable<AnimationClip> EnumerateAttackPresentationClips(
            CombatAnimationSet set,
            int strikeIndex)
        {
            AnimationClip? clip = set.GetStrikeClip(strikeIndex);
            if (clip != null)
                yield return clip;

            WeaponMeleeAttackAuthoring attack = set.meleeAttacks[strikeIndex - 1];
            if (!attack.UsesPhasedPresentation)
                yield break;

            foreach (AnimationClip phasedClip in EnumeratePhasedClipSet(attack.phasedGround))
                yield return phasedClip;
            foreach (AnimationClip phasedClip in EnumeratePhasedClipSet(attack.phasedAir))
                yield return phasedClip;
        }

        private static IEnumerable<AnimationClip> EnumeratePhasedClipSet(WeaponPhasedActionClipSet clipSet)
        {
            if (clipSet.start != null)
                yield return clipSet.start;
            if (clipSet.loop != null)
                yield return clipSet.loop;
            if (clipSet.end != null)
                yield return clipSet.end;
        }

        private static void AppendClipSignature(
            System.Text.StringBuilder signature,
            int strikeIndex,
            string label,
            AnimationClip? clip)
        {
            signature.Append('|');
            signature.Append(strikeIndex);
            signature.Append(':');
            signature.Append(label);
            signature.Append('=');
            signature.Append(clip != null ? AssetDatabase.GetAssetPath(clip) : "<none>");
        }

        private static bool IsSameOrDescendant(Transform ancestor, Transform candidate)
        {
            var current = candidate;
            while (current != null)
            {
                if (current == ancestor)
                    return true;
                current = current.parent;
            }

            return false;
        }

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (target == root)
                return string.Empty;

            var segments = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments);
        }

        private static bool TryResolveImportedStrikes(
            MeleeManifestDocument? doc,
            CombatAnimationSet set,
            out MeleeManifestStrike[] strikes,
            out string error)
        {
            if (doc == null)
            {
                strikes = System.Array.Empty<MeleeManifestStrike>();
                error = "Shared melee manifest could not be parsed.";
                return false;
            }

            if (doc.profiles == null || doc.profiles.Length == 0)
            {
                strikes = System.Array.Empty<MeleeManifestStrike>();
                error = "Shared melee manifest is missing profiled strike data.";
                return false;
            }

            string combatProfile = set.CombatProfileIdOrDefault;
            foreach (var profile in doc.profiles)
            {
                if (profile == null)
                    continue;
                if (string.Equals(profile.combat_profile, combatProfile, System.StringComparison.OrdinalIgnoreCase))
                {
                    strikes = profile.strikes ?? System.Array.Empty<MeleeManifestStrike>();
                    error = string.Empty;
                    return true;
                }
            }

            strikes = System.Array.Empty<MeleeManifestStrike>();
            error =
                $"Shared melee manifest does not contain a '{combatProfile}' profile. " +
                "Import requires an exact combat-profile match.";
            return false;
        }

        private static MeleeManifestProfile? FindImportedProfile(
            MeleeManifestDocument? doc,
            CombatAnimationSet set)
        {
            if (doc?.profiles == null)
                return null;

            string combatProfile = set.CombatProfileIdOrDefault;
            foreach (var profile in doc.profiles)
            {
                if (profile == null)
                    continue;
                if (string.Equals(profile.combat_profile, combatProfile, System.StringComparison.OrdinalIgnoreCase))
                    return profile;
            }

            return null;
        }

        private static MeleeManifestStrike? FindImportedAutoAttackStrike(MeleeManifestProfile profile)
        {
            if (profile.strikes == null || profile.strikes.Length == 0)
                return null;

            string authoredAutoAttackId = profile.auto_attack_strike_id ?? string.Empty;
            foreach (var strike in profile.strikes)
            {
                if (strike == null)
                    continue;
                if (string.Equals(strike.id, authoredAutoAttackId, System.StringComparison.OrdinalIgnoreCase))
                    return strike;
            }

            return null;
        }

        private static void BackupFile(string sourcePath, string prefix)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return;

            Directory.CreateDirectory(BackupRootPath);
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string fileName = Path.GetFileName(sourcePath);
            string backupPath = Path.Combine(BackupRootPath, $"{timestamp}-{prefix}-{fileName}.bak");
            File.Copy(sourcePath, backupPath, overwrite: false);
        }

        private static WeaponStrikeHitWindowAuthoring[] BuildImportedHitWindows(
            MeleeManifestStrike manifestStrike,
            float clipLengthMs)
        {
            if (manifestStrike.hit_windows == null || manifestStrike.hit_windows.Length == 0)
                return System.Array.Empty<WeaponStrikeHitWindowAuthoring>();

            var imported = new WeaponStrikeHitWindowAuthoring[manifestStrike.hit_windows.Length];
            for (int hitIndex = 0; hitIndex < manifestStrike.hit_windows.Length; hitIndex++)
            {
                var hitWindow = manifestStrike.hit_windows[hitIndex];
                imported[hitIndex] = new WeaponStrikeHitWindowAuthoring
                {
                    timeNormalized = clipLengthMs > 0.001f
                        ? Mathf.Clamp01(hitWindow.impact_delay_ms / clipLengthMs)
                        : 0f,
                };
            }

            return imported;
        }

        private static string NormalizeJson(string json) => json.Replace("\r\n", "\n").Trim();

        private static string DescribePhasedClipSet(WeaponPhasedActionClipSet clipSet)
        {
            if (!clipSet.HasAny)
                return "<empty>";
            if (clipSet.IsComplete)
                return "complete";
            if (clipSet.IsPlayable)
                return $"playable ({clipSet.ClipCount} clips)";

            var parts = new List<string>(3);
            if (clipSet.start != null) parts.Add("start");
            if (clipSet.loop != null) parts.Add("loop");
            if (clipSet.end != null) parts.Add("end");
            return $"partial ({string.Join(", ", parts)})";
        }

        internal static void PersistAnimationSetEdit(CombatAnimationSet set, string reason)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssetIfDirty(set);
            BackupAnimationSetAsset(assetPath, reason);
        }

        private static void ScheduleAnimationSetPersist(CombatAnimationSet set, string reason)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            if (PendingPersists.TryGetValue(assetPath, out var pending))
            {
                pending.Reason = reason;
                pending.DueTime = EditorApplication.timeSinceStartup + AutoPersistDelaySeconds;
                return;
            }

            PendingPersists[assetPath] = new PendingAnimationSetPersist(
                set,
                reason,
                EditorApplication.timeSinceStartup + AutoPersistDelaySeconds);
        }

        private static void FlushPendingAnimationSetPersists()
        {
            FlushPendingStartupTrimSynchronizations();

            if (PendingPersists.Count == 0)
                return;

            // Avoid writing the asset on every keystroke while the user is still editing
            // an Inspector text field. Persist once editing focus leaves the field.
            if (EditorGUIUtility.editingTextField)
                return;

            var readyAssetPaths = new List<string>();
            foreach (var kvp in PendingPersists)
            {
                if (EditorApplication.timeSinceStartup >= kvp.Value.DueTime)
                    readyAssetPaths.Add(kvp.Key);
            }

            for (int i = 0; i < readyAssetPaths.Count; i++)
            {
                string assetPath = readyAssetPaths[i];
                if (!PendingPersists.TryGetValue(assetPath, out var pending))
                    continue;

                PendingPersists.Remove(assetPath);
                if (pending.Set == null)
                    continue;

                PersistAnimationSetEdit(pending.Set, pending.Reason);
                CombatAnimationSetProtection.RecordTrustedState(pending.Set, "inspector-edit");
            }
        }

        private static void ScheduleStartupTrimSynchronization(
            CombatAnimationSet set,
            AnimationClip clip)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath) || clip == null)
                return;

            if (!PendingStartupTrimSynchronizations.TryGetValue(
                    assetPath,
                    out PendingStartupTrimSynchronization? pending))
            {
                pending = new PendingStartupTrimSynchronization(
                    set,
                    EditorApplication.timeSinceStartup + AutoStartupTrimSyncDelaySeconds);
                PendingStartupTrimSynchronizations.Add(assetPath, pending);
            }

            pending.Clips.Add(clip);
            pending.DueTime = EditorApplication.timeSinceStartup + AutoStartupTrimSyncDelaySeconds;
        }

        private static void FlushPendingStartupTrimSynchronizations()
        {
            if (PendingStartupTrimSynchronizations.Count == 0 || EditorGUIUtility.editingTextField)
                return;

            var readyAssetPaths = new List<string>();
            foreach (KeyValuePair<string, PendingStartupTrimSynchronization> pair in PendingStartupTrimSynchronizations)
            {
                if (EditorApplication.timeSinceStartup >= pair.Value.DueTime)
                    readyAssetPaths.Add(pair.Key);
            }

            for (int pathIndex = 0; pathIndex < readyAssetPaths.Count; pathIndex++)
            {
                string assetPath = readyAssetPaths[pathIndex];
                if (!PendingStartupTrimSynchronizations.TryGetValue(
                        assetPath,
                        out PendingStartupTrimSynchronization? pending))
                {
                    continue;
                }

                PendingStartupTrimSynchronizations.Remove(assetPath);
                if (pending.Set == null)
                    continue;

                bool synchronizedAll = true;
                foreach (AnimationClip clip in pending.Clips)
                {
                    if (!SynchronizeHitEventsForClip(clip, out string summary))
                    {
                        synchronizedAll = false;
                        Debug.LogWarning(
                            $"[{nameof(CombatAnimationSetEditor)}] Startup Trim auto-sync failed for '{pending.Set.name}': {summary}",
                            pending.Set);
                        continue;
                    }

                    Debug.Log(
                        $"[{nameof(CombatAnimationSetEditor)}] Startup Trim auto-sync: {summary}",
                        pending.Set);
                }

                if (synchronizedAll)
                    CancelPendingAnimationSetPersist(pending.Set);
            }
        }

        private static void InvalidateAvatarValidationCache(CombatAnimationSet set)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            CachedAvatarValidations.Remove(assetPath);
        }

        private static void CancelPendingAnimationSetPersist(CombatAnimationSet set)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            PendingPersists.Remove(assetPath);
        }

        private static void RestoreLatestAnimationSetBackup(CombatAnimationSet set, string backupPath)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                EditorUtility.DisplayDialog("Restore Failed", "Animation set asset path could not be resolved.", "OK");
                return;
            }

            string absoluteAssetPath = ToAbsoluteProjectPath(assetPath);
            if (!File.Exists(backupPath) || !File.Exists(absoluteAssetPath))
            {
                EditorUtility.DisplayDialog("Restore Failed", "The animation set backup or target asset no longer exists.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Restore Animation Set Backup",
                    $"Restore the latest backup over:\n{assetPath}\n\nCurrent file will be snapshotted first.",
                    "Restore",
                    "Cancel"))
            {
                return;
            }

            BackupAnimationSetAsset(assetPath, "pre-restore-current");
            CombatAnimationSetProtection.MarkTrustedMutation(set, "manual-restore");
            CancelPendingAnimationSetPersist(set);
            File.Copy(backupPath, absoluteAssetPath, overwrite: true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            var restored = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(assetPath);
            if (restored != null)
            {
                CombatAnimationSetProtection.RecordTrustedState(restored, "manual-restore");
                InvalidateAvatarValidationCache(restored);
            }
            InternalEditorUtility.RepaintAllViews();

            EditorUtility.DisplayDialog(
                "Restore Complete",
                $"Restored the latest backup for:\n{assetPath}",
                "OK");
        }

        internal static void BackupAnimationSetAsset(string assetPath, string reason)
        {
            string absoluteAssetPath = ToAbsoluteProjectPath(assetPath);
            if (!File.Exists(absoluteAssetPath))
                return;

            string backupDirectory = GetAnimationSetBackupDirectory(assetPath);
            Directory.CreateDirectory(backupDirectory);

            string fileName = Path.GetFileName(assetPath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string backupPath = Path.Combine(backupDirectory, $"{timestamp}-{reason}-{fileName}.bak");
            File.Copy(absoluteAssetPath, backupPath, overwrite: false);
            PruneAnimationSetBackups(backupDirectory, fileName);
        }

        private static string? GetLatestAnimationSetBackupPath(CombatAnimationSet set)
        {
            string assetPath = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            string backupDirectory = GetAnimationSetBackupDirectory(assetPath);
            if (!Directory.Exists(backupDirectory))
                return null;

            string fileName = Path.GetFileName(assetPath);
            string[] backups = Directory.GetFiles(backupDirectory, $"*-{fileName}.bak");
            if (backups.Length == 0)
                return null;

            Array.Sort(backups, StringComparer.Ordinal);
            return backups[^1];
        }

        internal static string? GetLatestAnimationSetBackupPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            string backupDirectory = GetAnimationSetBackupDirectory(assetPath);
            if (!Directory.Exists(backupDirectory))
                return null;

            string fileName = Path.GetFileName(assetPath);
            string[] backups = Directory.GetFiles(backupDirectory, $"*-{fileName}.bak");
            if (backups.Length == 0)
                return null;

            Array.Sort(backups, StringComparer.Ordinal);
            return backups[^1];
        }

        internal static string GetAnimationSetBackupDirectory(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            string assetStem = Path.GetFileNameWithoutExtension(assetPath);
            return Path.Combine(AnimationSetBackupRootPath, $"{assetStem}-{guid}");
        }

        internal static string ToAbsoluteProjectPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        private static void PruneAnimationSetBackups(string backupDirectory, string fileName)
        {
            string[] backups = Directory.GetFiles(backupDirectory, $"*-{fileName}.bak");
            if (backups.Length <= MaxAnimationSetBackupsPerAsset)
                return;

            Array.Sort(backups, StringComparer.Ordinal);
            int removeCount = backups.Length - MaxAnimationSetBackupsPerAsset;
            for (int i = 0; i < removeCount; i++)
                File.Delete(backups[i]);
        }
    }
}
