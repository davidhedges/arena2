#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Combat;
using Arena.Presentation;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>Normal-Editor verification and read-only inventory. Never writes authored assets.</summary>
    public static class CombatAuthoringVerification
    {
        [Serializable]
        internal sealed class Report
        {
            public string capturedUtc = DateTime.UtcNow.ToString("O");
            public string unityVersion = Application.unityVersion;
            public bool testsCompleted;
            public int testsPassed;
            public int testsFailed;
            public List<string> failures = new();
            public List<string> inventoryErrors = new();
            public int selectableMeleeAbilitiesChecked;
            public List<string> selectableMeleeErrors = new();
            public List<MeleeRow> melee = new();
            public List<VfxRow> vfx = new();
        }

        [Serializable]
        internal sealed class MeleeRow
        {
            public string asset = "";
            public string profile = "";
            public string strike = "";
            public string presentation = "";
            public string[] clips = Array.Empty<string>();
            public bool hasEffectiveEvents;
            public bool canBuildEventMirror;
            public bool eventMirrorMatches;
            public float startupTrimSeconds;
            public float[] effectiveEventSeconds = Array.Empty<float>();
            public int[] exportedHitMs = Array.Empty<int>();
            public int[] manifestHitMs = Array.Empty<int>();
            public int exportedRecoveryMs;
            public int manifestRecoveryMs;
            public bool manifestStrikeFound;
            public bool manifestTimingMatches;
        }

        [Serializable]
        internal sealed class VfxRow
        {
            public string ability = "";
            public string action = "";
            public string profile = "";
            public bool animationResolved;
            public string animationAssignment = "";
            public string archetype = "";
            public string school = "";
            public string castHand = "";
            public int generated;
            public int authored;
            public int matches;
            public List<string> changed = new();
            public List<string> generatedOnly = new();
            public List<string> catalogOnly = new();
            public List<string> ambiguous = new();
            public List<string> uninferrable = new();
            public List<string> notes = new();
        }

        [MenuItem("Arena/Animation/Verify Combat Authoring and Export Inventory")]
        public static void Run()
        {
            if (Application.isBatchMode)
                throw new InvalidOperationException("Run this verification in the normal Unity Editor.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Leave Play Mode before running authoring verification.");

            string output = Path.GetFullPath("Logs/CombatAuthoringVerification");
            string[] args = Environment.GetCommandLineArgs();
            int argument = Array.IndexOf(args, "-arenaVerificationOutput");
            if (argument >= 0 && argument + 1 < args.Length)
                output = Path.GetFullPath(args[argument + 1]);
            Directory.CreateDirectory(output);
            var report = new Report();
            var runner = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new Callbacks(report, Path.Combine(output, "tests.xml"));
            runner.RegisterCallbacks(callbacks);
            try
            {
                const string movement = "Arena.Tests.Editor.MovementRegressionTests.";
                runner.Execute(new ExecutionSettings(new Filter
                {
                    testMode = TestMode.EditMode,
                    assemblyNames = new[] { "Arena.EditModeTests" },
                    testNames = new[]
                    {
                        movement + "CombatAnimationSetEditor_ManifestImportUsesEventsOrLegacyFallback",
                        movement + "CombatAnimationSetEditor_ReadsLegacyProjectileLosButDoesNotExportIt",
                        movement + "CombatAnimationSet_MeleeExportUsesStrikeHitEventBeforeAuthoredHitWindows",
                        movement + "CombatAnimationSet_StartupTrimShiftsPlaybackAndExportOntoSameTimeline",
                        movement + "CombatAnimationSet_StartupTrimAtContactProducesValidZeroDelayHit",
                        movement + "CombatAnimationSet_StartupTrimUpdatesCompatibilityHitWindowMirror",
                        movement + "CombatAnimationSet_HitWindowMirrorTracksEveryStrikeHitEvent",
                        movement + "CombatAnimationSet_PhasedMeleeExportOffsetsStrikeHitEventByStartAndLoop",
                        movement + "CombatAnimationSet_PhasedMeleeExportReadsStrikeHitEventsFromAllPhases",
                        movement + "CombatAnimationSet_PhasedMeleeExportHonorsLoopPhaseReadyMarker",
                        movement + "CombatAnimationSet_StartEndPhasedMeleeExportSkipsMissingLoop",
                        movement + "CombatAnimationSet_LoopEndPhasedMeleeExportUsesResolvedStartAndLoop",
                        "Arena.Tests.Editor.CombatVfxCueResolverTests.IceSpikes_LegacyCueIsOnlyRedundantWhenAbilityIdentityIsPresent",
                        "Arena.Tests.Editor.SpellVfxGeneratorTests.GeneratedCastHand_FollowsAnimationOriginMirroringAndLegacyInference",
                        "Arena.Tests.Editor.SpellVfxGeneratorTests.SchoolVfxSets_AreEditorOnlyAuthoringAssets",
                        "Arena.Tests.Editor.SpellVfxGeneratorTests.SpellVfxOverrides_AreAssetAuthoredUniqueAndOutsideSource",
                        "Arena.Tests.Editor.SpellCueCatalogWriterTests",
                        "Arena.Tests.Editor.MeleeAuthoringDriftTests",
                    },
                }) { runSynchronously = true });
            }
            finally
            {
                runner.UnregisterCallbacks(callbacks);
                UnityEngine.Object.DestroyImmediate(runner);
            }

            CaptureMelee(report);
            try { report.selectableMeleeErrors = CheckSelectableMeleeTiming(out report.selectableMeleeAbilitiesChecked); }
            catch (Exception error) { report.selectableMeleeErrors.Add(error.ToString()); }
            var window = ScriptableObject.CreateInstance<SpellAuthoringWindow>();
            try { window.CaptureVfxInventory(report); }
            catch (Exception error) { report.inventoryErrors.Add("VFX inventory: " + error); }
            finally { UnityEngine.Object.DestroyImmediate(window); }
            File.WriteAllText(Path.Combine(output, "inventory.json"), JsonUtility.ToJson(report, true));
            string summary = $"[CombatAuthoringVerification] Tests {report.testsPassed} passed / {report.testsFailed} failed; "
                + $"{report.selectableMeleeAbilitiesChecked} selectable melee abilities checked, "
                + $"{report.selectableMeleeErrors.Count} timing errors; {report.melee.Count} melee attacks, "
                + $"{report.vfx.Count} VFX comparisons. Report: {output}";
            if (!report.testsCompleted || report.testsFailed > 0 || report.inventoryErrors.Count > 0
                || report.selectableMeleeErrors.Count > 0)
                Debug.LogError(summary);
            else
                Debug.Log(summary);
        }

        internal static List<string> CheckSelectableMeleeTiming(out int checkedAbilities)
        {
            checkedAbilities = 0;
            var errors = new List<string>();
            var actions = SpellPresentationEditorData.ReadSelectableMeleeActions(
                File.ReadAllText(SpellPresentationEditorData.AbsoluteProgressionCatalogPath),
                File.ReadAllText(SpellPresentationEditorData.AbsoluteCombatBuildV2CatalogPath));
            if (actions.Count == 0)
                throw new InvalidDataException("The selectable melee check resolved no abilities.");
            var sets = SpellPresentationEditorData.LoadCombatAnimationSets()
                .ToDictionary(set => set.CombatProfileIdOrDefault, StringComparer.Ordinal);
            var manifest = CombatAnimationSetEditor.DeserializeMeleeManifestDocument(
                File.ReadAllText("server/src/melee_manifest.shared.json"));
            var committed = manifest.profiles.ToDictionary(profile => profile.combat_profile, StringComparer.Ordinal);
            var exported = sets.ToDictionary(pair => pair.Key, pair => pair.Value.BuildMeleeExport().profiles.Single(), StringComparer.Ordinal);
            foreach (var action in actions)
            {
                string label = $"{action.Profile}/{action.AbilityId} ({action.ActionId})";
                if (!exported.TryGetValue(action.Profile, out var profile))
                {
                    errors.Add(label + ": missing CombatAnimationSet.");
                    continue;
                }
                var expected = profile.strikes.SingleOrDefault(strike => strike.id == action.ActionId);
                if (expected == null)
                {
                    errors.Add(label + ": missing authored action ID in the Unity export.");
                    continue;
                }
                var actual = committed.TryGetValue(action.Profile, out var serverProfile)
                    ? serverProfile.strikes.SingleOrDefault(strike => strike.id == action.ActionId) : null;
                errors.AddRange(CompareMeleeTiming(expected, actual).Select(error => label + ": " + error));
                checkedAbilities++;
            }
            return errors;
        }

        internal static List<string> CompareMeleeTiming(MeleeManifestStrike expected, MeleeManifestStrike? actual)
        {
            var errors = new List<string>();
            if (actual == null)
            {
                errors.Add("missing committed manifest strike.");
                return errors;
            }
            void Compare(string field, int exported, int committed)
            {
                if (exported != committed)
                    errors.Add($"{field}: export={exported}, manifest={committed}.");
            }
            Compare("startup_trim_ms", expected.startup_trim_ms, actual.startup_trim_ms);
            Compare("recovery_ms", expected.recovery_ms, actual.recovery_ms);
            Compare("combo_open_ms", expected.combo_open_ms, actual.combo_open_ms);
            Compare("combo_grace_ms", expected.combo_grace_ms, actual.combo_grace_ms);
            Compare("hit_windows.length", expected.hit_windows.Length, actual.hit_windows.Length);
            for (int index = 0; index < Math.Min(expected.hit_windows.Length, actual.hit_windows.Length); index++)
            {
                var left = expected.hit_windows[index];
                var right = actual.hit_windows[index];
                Compare($"hit_windows[{index}].impact_delay_ms", left.impact_delay_ms, right.impact_delay_ms);
                if (!string.Equals(left.impact_phase ?? "", right.impact_phase ?? "", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"hit_windows[{index}].impact_phase: export='{left.impact_phase}', manifest='{right.impact_phase}'.");
                Compare($"hit_windows[{index}].phase_delay_ms", left.phase_delay_ms, right.phase_delay_ms);
            }
            var leftTiming = expected.phased_gap_close_timing;
            var rightTiming = actual.phased_gap_close_timing;
            if ((leftTiming == null) != (rightTiming == null))
                errors.Add("phased_gap_close_timing presence differs.");
            else if (leftTiming != null && rightTiming != null)
            {
                Compare("phased_gap_close_timing.start_duration_ms", leftTiming.start_duration_ms, rightTiming.start_duration_ms);
                Compare("phased_gap_close_timing.loop_duration_ms", leftTiming.loop_duration_ms, rightTiming.loop_duration_ms);
            }
            return errors;
        }

        private static void CaptureMelee(Report report)
        {
            var manifest = JsonUtility.FromJson<MeleeManifestDocument>(File.ReadAllText("server/src/melee_manifest.shared.json"));
            foreach (CombatAnimationSet set in SpellPresentationEditorData.LoadCombatAnimationSets()
                         .OrderBy(set => set.CombatProfileIdOrDefault, StringComparer.Ordinal))
            {
                try
                {
                    MeleeManifestProfile exported = set.BuildMeleeExport().profiles[0];
                    MeleeManifestProfile? committed = manifest.profiles.FirstOrDefault(
                        profile => profile.combat_profile == exported.combat_profile);
                    foreach (WeaponMeleeAttackAuthoring attack in set.meleeAttacks)
                    {
                        string id = attack.combat.AuthoredStrikeIdOrDefault;
                        MeleeManifestStrike strike = exported.strikes.First(row => row.id == id);
                        MeleeManifestStrike? old = committed?.strikes.FirstOrDefault(row => row.id == id);
                        bool hasEvents = attack.TryGetEffectiveStrikeHitTimesSeconds(out float[] times);
                        bool hasMirror = attack.TryBuildHitWindowMirrorFromEvents(out var mirror);
                        var authoredMirror = attack.combat.hitWindows;
                        report.melee.Add(new MeleeRow
                        {
                            asset = AssetDatabase.GetAssetPath(set), profile = exported.combat_profile,
                            strike = id, presentation = attack.presentationMode.ToString(),
                            clips = new[] { attack.clip, attack.phasedGround.start, attack.phasedGround.loop,
                                attack.phasedGround.end, attack.phasedAir.start, attack.phasedAir.loop, attack.phasedAir.end }
                                .Where(clip => clip != null).Select(clip => AssetDatabase.GetAssetPath(clip) + "#" + clip!.name).Distinct().ToArray(),
                            hasEffectiveEvents = hasEvents, canBuildEventMirror = hasMirror,
                            eventMirrorMatches = hasMirror && authoredMirror != null && authoredMirror.Length == mirror.Length
                                && authoredMirror.Zip(mirror, (a, b) => Mathf.Approximately(a.timeNormalized, b.timeNormalized)).All(equal => equal),
                            startupTrimSeconds = attack.ResolveStartupTrimSeconds(), effectiveEventSeconds = times,
                            exportedHitMs = strike.hit_windows.Select(hit => hit.impact_delay_ms).ToArray(),
                            manifestHitMs = old?.hit_windows.Select(hit => hit.impact_delay_ms).ToArray() ?? Array.Empty<int>(),
                            exportedRecoveryMs = strike.recovery_ms, manifestRecoveryMs = old?.recovery_ms ?? -1,
                            manifestStrikeFound = old != null,
                            manifestTimingMatches = old != null && strike.hit_windows.Length == old.hit_windows.Length
                                && strike.hit_windows.Zip(old.hit_windows, (a, b) => a.impact_delay_ms == b.impact_delay_ms
                                    && a.impact_phase == b.impact_phase && a.phase_delay_ms == b.phase_delay_ms).All(equal => equal)
                                && strike.recovery_ms == old.recovery_ms && strike.startup_trim_ms == old.startup_trim_ms,
                        });
                    }
                }
                catch (Exception error) { report.inventoryErrors.Add(AssetDatabase.GetAssetPath(set) + ": " + error); }
            }
        }

        private sealed class Callbacks : ICallbacks
        {
            private readonly Report _report;
            private readonly string _xml;
            public Callbacks(Report report, string xml) { _report = report; _xml = xml; }
            public void RunStarted(ITestAdaptor tests) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result)
            {
                if (!result.HasChildren && result.TestStatus == TestStatus.Failed)
                    _report.failures.Add(result.FullName + ": " + result.Message + "\n" + result.StackTrace);
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                _report.testsCompleted = true;
                _report.testsPassed = result.PassCount;
                _report.testsFailed = result.FailCount;
                TestRunnerApi.SaveResultToFile(result, _xml);
            }
        }
    }

    internal sealed partial class SpellAuthoringWindow
    {
        internal void CaptureVfxInventory(CombatAuthoringVerification.Report report)
        {
            Load();
            report.inventoryErrors.AddRange(_loadErrors);
            EnsureAnimationSetsLoaded();
            foreach (AbilityDefinition ability in _spellAbilities)
            {
                string id = Normalize(ability.ability_id);
                string action = Normalize(ability.action_id);
                string assignment = _spellAnimationMap != null && _spellAnimationMap.TryGetEntry(action, out var mapEntry)
                    ? mapEntry.assignmentKind.ToString() : "MISSING";
                _selectedAbilityCues.Clear();
                _selectedAbilityCues.AddRange(_catalog!.combat_vfx_cues.Where(cue =>
                    (Normalize(cue.owner_kind) == "ABILITY" && Normalize(cue.owner_id) == id)
                    || (Normalize(cue.owner_kind) == "SPELL" && Normalize(cue.owner_id) == action)));
                BuildCatalogBySlot(out var catalog, out var ambiguous, out var uninferrable);
                foreach (CombatAnimationSet? set in _animationSets.Cast<CombatAnimationSet?>().Prepend(null))
                {
                    _generatedCuePreviewByAbilityId.Clear(); // Hand resolution varies with the equipped profile.
                    var mode = SpellAnimationArchetypes.Derive((ulong)Math.Max(0, ability.gameplay.cast_time_ms), ability.gameplay.delivery.kind);
                    bool resolved = SpellCastAnimationResolver.TryResolve(set, action, mode, out var animation);
                    GeneratedCuePreview preview = GetOrBuildGeneratedCuePreview(ability, id, resolved, animation);
                    var row = new CombatAuthoringVerification.VfxRow
                    {
                        ability = id, action = action, profile = set == null ? "GLOBAL" : set.CombatProfileIdOrDefault,
                        animationResolved = resolved, animationAssignment = assignment,
                        archetype = preview.Archetype?.ToString() ?? "UNSUPPORTED",
                        school = preview.School, castHand = preview.CastHandAnchor,
                        generated = preview.Cues.Count, authored = catalog.Count,
                        ambiguous = new List<string>(ambiguous), uninferrable = uninferrable.Select(DescribeCatalog).ToList(),
                        notes = new List<string>(preview.SlotNotes),
                    };
                    foreach (GeneratedCue cue in preview.Cues)
                    {
                        if (!catalog.TryGetValue(cue.SlotKey, out var existing)) { row.generatedOnly.Add(cue.SlotKey); continue; }
                        List<string> differences = DiffFields(cue, existing);
                        if (differences.Count == 0) row.matches++;
                        else row.changed.Add(cue.SlotKey + ": " + string.Join("; ", differences));
                    }
                    var keys = new HashSet<string>(preview.Cues.Select(cue => cue.SlotKey), StringComparer.Ordinal);
                    row.catalogOnly.AddRange(catalog.Keys.Where(key => !keys.Contains(key)));
                    report.vfx.Add(row);
                }
            }
        }
    }
}
