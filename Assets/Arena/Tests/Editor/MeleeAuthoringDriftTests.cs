#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class MeleeAuthoringDriftTests
    {
        private static Type EditorType(string name) => AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor")
            .GetType("Arena.Editor." + name, throwOnError: true)!;
        private static object? Call(string type, string method, params object?[] args) => EditorType(type)
            .GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
        private static string[] Compare(object expected, object? actual) =>
            ((IEnumerable)Call("CombatAuthoringVerification", "CompareMeleeTiming", expected, actual)!).Cast<string>().ToArray();

        private const string StrikeJson = @"{""id"":""ACTION"",""startup_trim_ms"":20,""recovery_ms"":250,
            ""combo_open_ms"":100,""combo_grace_ms"":200,
            ""hit_windows"":[{""impact_delay_ms"":40},{""impact_delay_ms"":80}]}";

        private static object Strike(string json = StrikeJson)
        {
            var doc = Call("CombatAnimationSetEditor", "DeserializeMeleeManifestDocument",
                "{\"profiles\":[{\"combat_profile\":\"PROFILE\",\"strikes\":[" + json + "]}]}")!;
            var profile = ((Array)doc.GetType().GetField("profiles")!.GetValue(doc)!).GetValue(0)!;
            return ((Array)profile.GetType().GetField("strikes")!.GetValue(profile)!).GetValue(0)!;
        }

        private static void Set(object row, string field, object? value) => row.GetType().GetField(field)!.SetValue(row, value);
        private static object SecondHit(object strike) =>
            ((Array)strike.GetType().GetField("hit_windows")!.GetValue(strike)!).GetValue(1)!;

        [Test]
        public void CurrentSelectableMeleeTiming_MatchesNativeExport()
        {
            object[] args = { 0 };
            var errors = (IEnumerable)Call("CombatAuthoringVerification", "CheckSelectableMeleeTiming", args)!;
            Assert.That((int)args[0], Is.GreaterThan(0), "The contract check must not pass vacuously.");
            Assert.That(errors.Cast<string>(), Is.Empty);
        }

        [Test]
        public void SelectableMeleeSelection_UsesParentProfileAndActionIdAndFiltersByExecutor()
        {
            const string progression = @"{""abilities"":[
                {""ability_id"":""WARRIOR_NAMED_ABILITY"",""action_id"":""EXPLICIT_ACTION"",""gameplay"":{""kind"":""MELEE""}},
                {""ability_id"":""SPELL_TECHNIQUE"",""action_id"":""SPELL_ACTION"",""gameplay"":{""kind"":""SPELL""}},
                {""ability_id"":""UNSELECTABLE"",""action_id"":""OLD_ACTION"",""gameplay"":{""kind"":""MELEE""}}
            ]}";
            const string build = @"{""specializations"" : [
                {""combat_discipline_id"":""DAGGERS"",""technique_ability_ids"":[""WARRIOR_NAMED_ABILITY"",""SPELL_TECHNIQUE""]}
            ]}";
            var rows = ((IEnumerable)Call("SpellPresentationEditorData", "ReadSelectableMeleeActions", progression, build)!)
                .Cast<object>().ToArray();
            Assert.That(rows.Length, Is.EqualTo(1));
            Assert.That(rows[0].GetType().GetProperty("AbilityId")!.GetValue(rows[0]), Is.EqualTo("WARRIOR_NAMED_ABILITY"));
            Assert.That(rows[0].GetType().GetProperty("Profile")!.GetValue(rows[0]), Is.EqualTo("DAGGERS"));
            Assert.That(rows[0].GetType().GetProperty("ActionId")!.GetValue(rows[0]), Is.EqualTo("EXPLICIT_ACTION"));
        }

        [TestCase("{\"abilities\":[]}")]
        [TestCase("{\"abilities\":[{\"ability_id\":\"MISSING\",\"gameplay\":{\"kind\":\"MELEE\"}}]}")]
        [TestCase("{\"abilities\":[{\"ability_id\":\"MISSING\",\"action_id\":\"ACTION\"}]}")]
        public void SelectableMeleeSelection_RejectsIncompleteReferences(string progression)
        {
            const string build = @"{""specializations"":[{""combat_discipline_id"":""DAGGERS"",""technique_ability_ids"":[""MISSING""]}]}";
            var error = Assert.Throws<TargetInvocationException>(() =>
                Call("SpellPresentationEditorData", "ReadSelectableMeleeActions", progression, build));
            Assert.That(error!.InnerException, Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void TimingComparison_AcceptsMatchingMultiHitDataAndOptionalObjects()
            => Assert.That(Compare(Strike(), Strike()), Is.Empty);

        [TestCase("startup_trim_ms")]
        [TestCase("recovery_ms")]
        [TestCase("combo_open_ms")]
        [TestCase("combo_grace_ms")]
        public void TimingComparison_RejectsStaleTimingField(string field)
        {
            object actual = Strike();
            Set(actual, field, 999);
            Assert.That(Compare(Strike(), actual), Has.Length.EqualTo(1).And.Some.Contains(field));
        }

        [TestCase("impact_delay_ms")]
        [TestCase("phase_delay_ms")]
        public void TimingComparison_ChecksEveryHit(string field)
        {
            object actual = Strike();
            Set(SecondHit(actual), field, 999);
            Assert.That(Compare(Strike(), actual), Has.Length.EqualTo(1).And.Some.Contains("hit_windows[1]." + field));
        }

        [Test]
        public void TimingComparison_RejectsMissingStrikeOrHit()
        {
            Assert.That(Compare(Strike(), null), Has.Length.EqualTo(1).And.Some.Contains("missing committed"));
            object actual = Strike();
            Array hits = (Array)actual.GetType().GetField("hit_windows")!.GetValue(actual)!;
            Array one = Array.CreateInstance(hits.GetType().GetElementType()!, 1);
            one.SetValue(hits.GetValue(0), 0);
            Set(actual, "hit_windows", one);
            Assert.That(Compare(Strike(), actual), Has.Length.EqualTo(1).And.Some.Contains("hit_windows.length"));
        }

        [Test]
        public void TimingComparison_ChecksPhaseIdentityUsingServerCaseSemantics()
        {
            object expected = Strike(), actual = Strike();
            Set(SecondHit(expected), "impact_phase", "END");
            Set(SecondHit(actual), "impact_phase", "end");
            Assert.That(Compare(expected, actual), Is.Empty);
            Set(SecondHit(actual), "impact_phase", "START");
            Assert.That(Compare(expected, actual), Has.Length.EqualTo(1).And.Some.Contains("impact_phase"));
        }

        [Test]
        public void TimingComparison_ChecksOptionalPhasedGapCloseDurations()
        {
            string json = StrikeJson.Substring(0, StrikeJson.Length - 1)
                + ",\"phased_gap_close_timing\":{\"start_duration_ms\":100,\"loop_duration_ms\":200}}";
            object expected = Strike(json), actual = Strike(json);
            Assert.That(Compare(expected, Strike()), Has.Length.EqualTo(1).And.Some.Contains("presence"));
            object timing = actual.GetType().GetField("phased_gap_close_timing")!.GetValue(actual)!;
            Set(timing, "start_duration_ms", 110);
            Set(timing, "loop_duration_ms", 220);
            Assert.That(Compare(expected, actual), Has.Length.EqualTo(2));
        }
    }
}
