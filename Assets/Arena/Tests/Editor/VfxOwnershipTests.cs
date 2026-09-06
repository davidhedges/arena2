#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class VfxOwnershipTests
    {
        private static Type Writer => Assembly.Load("Assembly-CSharp-Editor").GetType("Arena.Editor.SpellCueCatalogWriter", true)!;
        private static IList Errors(string json) => (IList)Writer.GetMethod("ValidateOwnership")!.Invoke(null, new object[] { json })!;
        private static string Cue(string mode, string reason = "", string kind = "ABILITY", string slot = "impact")
            => "{\"owner_kind\":\"" + kind + "\",\"owner_id\":\"SPELL_TEST\",\"sort_order\":10,\"slot\":\"" + slot
                + "\",\"authoring_mode\":\"" + mode + "\",\"authoring_reason\":\"" + reason + "\"}";
        private static string Catalog(params string[] cues) => "{\"combat_vfx_cues\":[" + string.Join(",", cues) + "]}";

        private static Array Rows(string slot = "impact", string vfxId = "VFX_FIREBALL_HIT_01", int sort = 120)
        {
            var type = Writer.Assembly.GetType("Arena.Editor.SpellCueRow", true)!;
            var result = Array.CreateInstance(type, 1);
            result.SetValue(Activator.CreateInstance(type, new object?[]
                { slot, "SPELL_IMPACT", "IMPACT_POINT", vfxId, "SPAWN_WORLD", "ONE_SHOT", "DURATION", null, 1000, sort }), 0);
            return result;
        }

        [TestCase("MANUAL")]
        [TestCase("LEGACY")]
        [TestCase("")]
        public void Splice_RejectsUnownedOrManualTargets(string mode)
        {
            var error = Assert.Throws<TargetInvocationException>(() => Writer.GetMethod("SpliceOwnerCues")!
                .Invoke(null, new object[] { Catalog(Cue(mode, "Preserved")), "SPELL_TEST", Rows(sort: 10) }));
            Assert.That(error!.InnerException!.Message, Does.Contain("not GENERATED"));
        }

        [Test]
        public void Splice_RejectsAnExistingRowWithAnotherSlot()
        {
            var error = Assert.Throws<TargetInvocationException>(() => Writer.GetMethod("SpliceOwnerCues")!
                .Invoke(null, new object[] { Catalog(Cue("GENERATED")), "SPELL_TEST", Rows(slot: "cast_glow", sort: 10) }));
            Assert.That(error!.InnerException!.Message, Does.Contain("different slot"));
        }

        [TestCase("impact", "VFX_WRONG", 120)]
        [TestCase("cast_glow", "VFX_FIRE_CAST_HAND_01", 100)]
        [TestCase("impact", "VFX_FIREBALL_HIT_01", 999)]
        public void FileWriter_RejectsStaleCandidatesManualRowsAndImplicitInsertions(string slot, string vfxId, int order)
        {
            string original = File.ReadAllText("server/src/progression_catalog.shared.json");
            string path = Path.Combine(Path.GetTempPath(), "arena-vfx-ownership-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, original);
                var error = Assert.Throws<TargetInvocationException>(() => Writer.GetMethod("WriteOwnerCues")!
                    .Invoke(null, new object[] { path, "SPELL_FIREBALL", Rows(slot, vfxId, order), original }));
                Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(File.ReadAllText(path), Is.EqualTo(original));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void CurrentCatalog_EveryCueHasExplicitOwnership()
            => Assert.That(Errors(File.ReadAllText("server/src/progression_catalog.shared.json")), Is.Empty);

        [TestCase("GENERATED", "", "ABILITY", "impact")]
        [TestCase("MANUAL", "Preserve authored effect.", "ABILITY", "")]
        [TestCase("LEGACY", "Required when ability identity is absent.", "SPELL", "impact")]
        public void Ownership_AcceptsDeclaredSources(string mode, string reason, string kind, string slot)
            => Assert.That(Errors(Catalog(Cue(mode, reason, kind, slot))), Is.Empty);

        [TestCase("", "", "ABILITY", "impact")]
        [TestCase("AUTO", "", "ABILITY", "impact")]
        [TestCase("MANUAL", "", "ABILITY", "impact")]
        [TestCase("GENERATED", "", "SPELL", "impact")]
        [TestCase("GENERATED", "", "ABILITY", "")]
        [TestCase("GENERATED", "Old exception", "ABILITY", "impact")]
        [TestCase("LEGACY", "Compatibility", "ABILITY", "impact")]
        public void Ownership_RejectsIncompleteOrContradictoryDeclarations(string mode, string reason, string kind, string slot)
            => Assert.That(Errors(Catalog(Cue(mode, reason, kind, slot))), Is.Not.Empty);

        [Test]
        public void Ownership_SeparatesLegacyOwnerAndRejectsDuplicateRows()
        {
            Assert.That(Errors(Catalog(Cue("GENERATED"), Cue("LEGACY", "Compatibility", "SPELL"))), Is.Empty);
            Assert.That(Errors(Catalog(Cue("GENERATED"), Cue("GENERATED"))), Is.Not.Empty);
        }

        [TestCase("hit_index")]
        [TestCase("start_delay_ms")]
        [TestCase("scale")]
        [TestCase("unmodeled_condition")]
        public void GeneratedOwnership_RejectsFieldsOutsideItsContract(string field)
            => Assert.That(Errors(Catalog(Cue("GENERATED").Replace("}", ",\"" + field + "\":0}"))), Is.Not.Empty);

        [TestCase(0, "other_slot")]
        [TestCase(1, "SPELL_RELEASE")]
        [TestCase(2, "RIGHT_HAND")]
        [TestCase(3, "VFX_OTHER")]
        [TestCase(4, "FOLLOW_ANCHOR")]
        [TestCase(5, "ATTACHED")]
        [TestCase(6, "PARTICLE_SYSTEM")]
        [TestCase(7, 0)]
        [TestCase(8, 900)]
        [TestCase(9, 11)]
        public void GeneratedComparison_DetectsEveryOwnedField(int index, object changed)
        {
            var type = Writer.Assembly.GetType("Arena.Editor.SpellCueRow", true)!;
            object?[] values = { "impact", "SPELL_IMPACT", "IMPACT_POINT", "VFX_HIT", "SPAWN_WORLD", "ONE_SHOT", "DURATION", null, 1000, 10 };
            var baseline = Activator.CreateInstance(type, values)!;
            Assert.That((IList)Writer.GetMethod("CompareGeneratedRows")!.Invoke(null, new[] { baseline, baseline })!, Is.Empty);
            values[index] = changed;
            var actual = Activator.CreateInstance(type, values)!;
            Assert.That((IList)Writer.GetMethod("CompareGeneratedRows")!.Invoke(null, new[] { baseline, actual })!, Has.Count.EqualTo(1));
        }

        private static ScriptableObject Window()
        {
            var window = ScriptableObject.CreateInstance(Writer.Assembly.GetType("Arena.Editor.SpellAuthoringWindow", true)!);
            window.GetType().GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            return window;
        }

        private static object[] Cues(ScriptableObject window)
        {
            var catalog = window.GetType().GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            return ((IEnumerable)catalog.GetType().GetField("combat_vfx_cues")!.GetValue(catalog)!).Cast<object>().ToArray();
        }
        private static string Value(object cue, string field) => (string)cue.GetType().GetField(field)!.GetValue(cue)!;
        private static IList Check(ScriptableObject window, out int count)
        {
            object[] args = { 0 };
            var errors = (IList)window.GetType().GetMethod("CheckGeneratedCueOwnership", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
            count = (int)args[0];
            return errors;
        }

        [Test]
        public void CurrentGeneratedCues_MatchEveryNativeContext()
        {
            var window = Window();
            try
            {
                Assert.That(Check(window, out int count), Is.Empty);
                Assert.That(count, Is.GreaterThan(0), "Generation drift checks must not pass vacuously.");
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void NativeOwnershipCheck_DetectsGeneratedDriftAndAllowsManualDifferences()
        {
            var window = Window();
            try
            {
                var cues = Cues(window);
                var manual = cues.First(c => Value(c, "authoring_mode") == "MANUAL");
                manual.GetType().GetField("duration_ms")!.SetValue(manual, 12345);
                Assert.That(Check(window, out _), Is.Empty);
                var generated = cues.First(c => Value(c, "authoring_mode") == "GENERATED");
                generated.GetType().GetField("duration_ms")!.SetValue(generated, 12345);
                Assert.That(Check(window, out _).Cast<string>().Any(e => e.Contains("duration_ms")), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void GlobalPlan_RejectsEquipmentDependentCastHand()
        {
            var window = Window();
            try
            {
                var cue = Cues(window).Single(c => Value(c, "owner_id") == "SPELL_FIREBALL" && Value(c, "slot") == "cast_glow");
                cue.GetType().GetField("authoring_mode")!.SetValue(cue, "GENERATED");
                Assert.That(Check(window, out _).Cast<string>().Any(e => e.Contains("equipment-dependent")), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GlobalPlan_RejectsAmbiguousOrMissingCandidates(bool ambiguous)
        {
            var window = Window();
            try
            {
                var cue = Cues(window).Single(c => Value(c, "owner_id") == "SPELL_FIREBALL"
                    && Value(c, "slot") == (ambiguous ? "cast_glow" : "impact"));
                cue.GetType().GetField("slot")!.SetValue(cue, ambiguous ? "impact" : "status_attachment");
                Assert.That(Check(window, out _).Cast<string>().Any(e => e.Contains(ambiguous ? "ambiguous" : "exactly one candidate")), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void GlobalPlan_RequiresEveryCatalogDisciplineContext()
        {
            var window = Window();
            try
            {
                window.GetType().GetMethod("EnsureAnimationSetsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                var field = window.GetType().GetField("_animationSets", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var sets = (Array)field.GetValue(window)!;
                var missing = Array.CreateInstance(sets.GetType().GetElementType()!, sets.Length - 1);
                Array.Copy(sets, missing, missing.Length);
                field.SetValue(window, missing);
                Assert.That(Check(window, out _).Cast<string>().Any(e => e.Contains("exactly one animation set")), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
