#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    /// <summary>
    /// Golden-file + robustness coverage for the surgical catalog writer
    /// (Arena.Editor.SpellCueCatalogWriter — the "tested JSON writer" slice, design doc decision 4).
    /// The load-bearing test is zero-diff-first: materialising FIREBALL back into the real catalog
    /// changes only the inserted author-time <c>slot</c> keys — every other byte (melee rows, other
    /// spells, gameplay data, key order, indentation, line endings) is identical. That is the safety
    /// property that lets the generator start writing the catalog.
    /// <para>
    /// The writer lives in the editor assembly (Assembly-CSharp-Editor), which this editor test
    /// assembly cannot reference statically, so it is exercised via reflection — same pattern as
    /// SpellVfxGeneratorTests (which reflects into Assembly-CSharp).
    /// </para>
    /// </summary>
    public sealed class SpellCueCatalogWriterTests
    {
        private const string CatalogRelativePath = "server/src/progression_catalog.shared.json";

        private static readonly Assembly EditorAssembly = LoadEditorAssembly();
        private static readonly Type WriterType = EditorAssembly.GetType("Arena.Editor.SpellCueCatalogWriter", throwOnError: true)!;
        private static readonly Type RowType = EditorAssembly.GetType("Arena.Editor.SpellCueRow", throwOnError: true)!;

        private static Assembly LoadEditorAssembly()
        {
            const string name = "Assembly-CSharp-Editor";
            Assembly? found = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
            return found ?? AppDomain.CurrentDomain.Load(name);
        }

        private static object Row(
            string slot, string trigger, string anchor, string vfxId, string attachMode,
            string vfxRole, string lifecycle, int? projectileSequenceIndex, int durationMs, int sortOrder)
            => Activator.CreateInstance(
                RowType,
                new object?[]
                {
                    slot, trigger, anchor, vfxId, attachMode, vfxRole, lifecycle,
                    projectileSequenceIndex, durationMs, sortOrder,
                })!;

        private static string Splice(string json, string ownerId, IEnumerable<object> rows)
        {
            object[] rowArray = rows.ToArray();
            Array typed = Array.CreateInstance(RowType, rowArray.Length);
            for (int i = 0; i < rowArray.Length; i++)
                typed.SetValue(rowArray[i], i);

            return (string)WriterType
                .GetMethod("SpliceOwnerCues", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { json, ownerId, typed })!;
        }

        // The three FIREBALL rows exactly as authored today (Projectile archetype, Instant mode). The
        // wiring values (trigger/anchor/attach/role/lifecycle/sequence) are what SpellVfxGenerator.Wire
        // emits — proven by SpellVfxGeneratorTests and the window's zero-diff preview; the look
        // (vfx_id/duration) is the FIRE school + FIREBALL signature. Because generated == authored, a
        // faithful writer changes only the slot keys.
        private static List<object> FireballRows() => new()
        {
            Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_FIRE_CAST_HAND_01", "FOLLOW_ANCHOR",
                "ATTACHED", "DURATION", null, 350, 100),
            Row("projectile_body", "SPELL_RELEASE", "LEFT_HAND", "VFX_FIREBALL_PROJECTILE_01", "SPAWN_WORLD",
                "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, 110),
            Row("impact", "SPELL_IMPACT", "IMPACT_POINT", "VFX_FIREBALL_HIT_01", "SPAWN_WORLD",
                "ONE_SHOT", "DURATION", null, 1000, 120),
        };

        // ----- the load-bearing zero-diff-first golden test -----

        [Test]
        public void MaterializeFireball_ChangesOnlyTheInsertedSlotKeys()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), CatalogRelativePath);
            Assert.That(File.Exists(path), Is.True, $"catalog not found at {path}");
            string original = File.ReadAllText(path);

            string output = Splice(original, "SPELL_FIREBALL", FireballRows());

            Assert.That(output, Is.Not.EqualTo(original), "write should have inserted the slot keys");

            // Strip exactly the inserted `slot` lines; the remainder must be byte-identical to the input.
            string stripped = Regex.Replace(output, "[ \\t]*\"slot\": \"[^\"]*\",\\n", string.Empty);
            Assert.That(stripped, Is.EqualTo(original),
                "materialising FIREBALL must change nothing but the inserted slot keys");

            // Exactly the three FIREBALL slots were added — nothing stray elsewhere.
            Assert.That(Regex.Matches(output, "\"slot\":").Count, Is.EqualTo(3));
            Assert.That(output, Does.Contain("\"slot\": \"cast_glow\""));
            Assert.That(output, Does.Contain("\"slot\": \"projectile_body\""));
            Assert.That(output, Does.Contain("\"slot\": \"impact\""));
        }

        [Test]
        public void MaterializeFireball_IsIdempotentOnItsOwnJoinContract()
        {
            // Re-running the write over already-written rows keeps the same sort_order join and must
            // reproduce the identical file (the merge overwrites the same fields to the same values).
            string path = Path.Combine(Directory.GetCurrentDirectory(), CatalogRelativePath);
            string original = File.ReadAllText(path);
            string once = Splice(original, "SPELL_FIREBALL", FireballRows());
            string twice = Splice(once, "SPELL_FIREBALL", FireballRows());
            Assert.That(twice, Is.EqualTo(once));
        }

        // ----- writer robustness on synthetic fixtures -----

        // A minimal catalog: an unrelated melee owner, two rows for the target owner, and trailing
        // non-cue data. Everything but the target rows must survive verbatim.
        private const string SyntheticCatalog =
            "{\n" +
            "  \"combat_vfx_cues\": [\n" +
            "    {\n" +
            "      \"owner_kind\": \"ABILITY\",\n" +
            "      \"owner_id\": \"WARRIOR_MELEE\",\n" +
            "      \"trigger\": \"MELEE_IMPACT\",\n" +
            "      \"hit_index\": 0,\n" +
            "      \"anchor\": \"GROUND_UNDER_TARGET\",\n" +
            "      \"vfx_id\": \"VFX_MELEE_01\",\n" +
            "      \"attach_mode\": \"SPAWN_WORLD\",\n" +
            "      \"duration_ms\": 2500,\n" +
            "      \"sort_order\": 10\n" +
            "    },\n" +
            "    {\n" +
            "      \"owner_kind\": \"ABILITY\",\n" +
            "      \"owner_id\": \"SPELL_TEST\",\n" +
            "      \"trigger\": \"SPELL_CAST\",\n" +
            "      \"anchor\": \"LEFT_HAND\",\n" +
            "      \"vfx_id\": \"VFX_OLD_CAST\",\n" +
            "      \"attach_mode\": \"FOLLOW_ANCHOR\",\n" +
            "      \"vfx_role\": \"ATTACHED\",\n" +
            "      \"lifecycle\": \"DURATION\",\n" +
            "      \"legacy_note\": \"KEEP_ME\",\n" +
            "      \"duration_ms\": 350,\n" +
            "      \"sort_order\": 100\n" +
            "    }\n" +
            "  ],\n" +
            "  \"other\": 42\n" +
            "}\n";

        [Test]
        public void Splice_OverwritesTargetFields_AndPreservesEverythingElse()
        {
            // Change the cast vfx_id; keep the row's sort_order + the unmodelled legacy_note override.
            var rows = new List<object>
            {
                Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_NEW_CAST", "FOLLOW_ANCHOR",
                    "ATTACHED", "DURATION", null, 350, 100),
            };

            string output = Splice(SyntheticCatalog, "SPELL_TEST", rows);

            // The melee row + trailing data are byte-identical.
            Assert.That(output, Does.Contain("\"owner_id\": \"WARRIOR_MELEE\""));
            Assert.That(output, Does.Contain("  \"other\": 42\n}\n"));
            // Target field overwritten, slot inserted after owner_id, legacy override + sort_order kept.
            Assert.That(output, Does.Contain("\"vfx_id\": \"VFX_NEW_CAST\""));
            Assert.That(output, Does.Not.Contain("VFX_OLD_CAST"));
            Assert.That(output, Does.Contain("\"legacy_note\": \"KEEP_ME\""));
            Assert.That(output, Does.Contain("\"sort_order\": 100"));

            // Known keys re-serialise in canonical order (slot after owner_id); an unmodelled legacy
            // key is preserved but sorts after the known keys (it has no canonical position).
            string expectedRow =
                "    {\n" +
                "      \"owner_kind\": \"ABILITY\",\n" +
                "      \"owner_id\": \"SPELL_TEST\",\n" +
                "      \"slot\": \"cast_glow\",\n" +
                "      \"trigger\": \"SPELL_CAST\",\n" +
                "      \"anchor\": \"LEFT_HAND\",\n" +
                "      \"vfx_id\": \"VFX_NEW_CAST\",\n" +
                "      \"attach_mode\": \"FOLLOW_ANCHOR\",\n" +
                "      \"vfx_role\": \"ATTACHED\",\n" +
                "      \"lifecycle\": \"DURATION\",\n" +
                "      \"duration_ms\": 350,\n" +
                "      \"sort_order\": 100,\n" +
                "      \"legacy_note\": \"KEEP_ME\"\n" +
                "    }";
            Assert.That(output, Does.Contain(expectedRow));
        }

        [Test]
        public void Splice_Throws_WhenGeneratedRowHasNoMatchingAuthoredRow()
        {
            var rows = new List<object>
            {
                // sort_order 999 does not exist in the fixture.
                Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_NEW_CAST", "FOLLOW_ANCHOR",
                    "ATTACHED", "DURATION", null, 350, 999),
            };

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
                () => Splice(SyntheticCatalog, "SPELL_TEST", rows))!;
            Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Splice_Throws_WhenAuthoredRowHasNoGeneratedCounterpart()
        {
            // Fixture with two target rows; supply only one generated row.
            const string twoRow =
                "{\n" +
                "  \"combat_vfx_cues\": [\n" +
                "    {\n" +
                "      \"owner_kind\": \"ABILITY\",\n" +
                "      \"owner_id\": \"SPELL_TEST\",\n" +
                "      \"trigger\": \"SPELL_CAST\",\n" +
                "      \"anchor\": \"LEFT_HAND\",\n" +
                "      \"vfx_id\": \"VFX_A\",\n" +
                "      \"attach_mode\": \"FOLLOW_ANCHOR\",\n" +
                "      \"vfx_role\": \"ATTACHED\",\n" +
                "      \"lifecycle\": \"DURATION\",\n" +
                "      \"duration_ms\": 350,\n" +
                "      \"sort_order\": 100\n" +
                "    },\n" +
                "    {\n" +
                "      \"owner_kind\": \"ABILITY\",\n" +
                "      \"owner_id\": \"SPELL_TEST\",\n" +
                "      \"trigger\": \"SPELL_IMPACT\",\n" +
                "      \"anchor\": \"IMPACT_POINT\",\n" +
                "      \"vfx_id\": \"VFX_B\",\n" +
                "      \"attach_mode\": \"SPAWN_WORLD\",\n" +
                "      \"vfx_role\": \"ONE_SHOT\",\n" +
                "      \"lifecycle\": \"DURATION\",\n" +
                "      \"duration_ms\": 1000,\n" +
                "      \"sort_order\": 110\n" +
                "    }\n" +
                "  ]\n" +
                "}\n";

            var rows = new List<object>
            {
                Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_A", "FOLLOW_ANCHOR",
                    "ATTACHED", "DURATION", null, 350, 100),
            };

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
                () => Splice(twoRow, "SPELL_TEST", rows))!;
            Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
        }
    }
}
