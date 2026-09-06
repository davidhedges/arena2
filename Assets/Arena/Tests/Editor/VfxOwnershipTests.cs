#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;

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
    }
}
