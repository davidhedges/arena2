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
    /// Covers the surgical catalog writer (Arena.Editor.SpellCueCatalogWriter — the "tested JSON
    /// writer" slice, design doc decision 4). The load-bearing property is that the update path only
    /// touches the inserted author-time <c>slot</c> keys (so FIREBALL's first write was a byte-clean
    /// diff), plus the insertion path that adds generator-only slots / brand-new owners in sort order
    /// (design doc — needed to migrate the 26 spells the map flags as needing new rows).
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
            // Historical formatting fixtures predate ownership metadata. Declare their ABILITY
            // rows generated and strip only that fixture metadata from the result. Ownership
            // rejection itself is exercised against unmodified inputs in VfxOwnershipTests.
            bool historicalFixture = !json.Contains("\"authoring_mode\"");
            if (historicalFixture)
                json = Regex.Replace(json, @"\{[^{}]*\}", match =>
                    Regex.IsMatch(match.Value, "\"owner_kind\"\\s*:\\s*\"ABILITY\"")
                        ? match.Value.Substring(0, match.Value.Length - 1) + ",\"authoring_mode\":\"GENERATED\"}"
                        : match.Value);
            object[] rowArray = rows.ToArray();
            Array typed = Array.CreateInstance(RowType, rowArray.Length);
            for (int i = 0; i < rowArray.Length; i++)
                typed.SetValue(rowArray[i], i);

            string output = (string)WriterType
                .GetMethod("SpliceOwnerCues", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { json, ownerId, typed })!;
            return historicalFixture
                ? Regex.Replace(output, ",\\s*\"authoring_mode\"\\s*:\\s*\"GENERATED\"", "")
                : output;
        }

        // The three FIREBALL rows exactly as authored/materialized today (Projectile archetype, Instant).
        private static List<object> FireballRows() => new()
        {
            Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_FIRE_CAST_HAND_01", "FOLLOW_ANCHOR",
                "ATTACHED", "DURATION", null, 350, 100),
            Row("projectile_body", "SPELL_RELEASE", "LEFT_HAND", "VFX_FIREBALL_PROJECTILE_01", "SPAWN_WORLD",
                "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, 110),
            Row("impact", "SPELL_IMPACT", "IMPACT_POINT", "VFX_FIREBALL_HIT_01", "SPAWN_WORLD",
                "ONE_SHOT", "DURATION", null, 1000, 120),
        };

        // ----- the real catalog: the writer reproduces the committed FIREBALL materialization -----

        [Test]
        public void MaterializeFireball_ReproducesTheCommittedCatalogExactly()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), CatalogRelativePath);
            Assert.That(File.Exists(path), Is.True, $"catalog not found at {path}");
            string original = File.ReadAllText(path);

            // FIREBALL is already materialized in the committed catalog, so re-materializing it is a
            // byte-exact no-op — proving the writer's output matches what's checked in (no drift).
            Assert.That(original, Does.Contain("\"slot\": \"cast_glow\""));
            // Hand-dependent cast/body rows are manual; the impact is generated-owned.
            string output = Splice(original, "SPELL_FIREBALL", FireballRows().Skip(2));
            Assert.That(output, Is.EqualTo(original), "re-materializing FIREBALL must reproduce the committed catalog byte-for-byte");
        }

        // ----- update path: adding slot keys is the only change (the zero-diff-first property) -----

        // FIREBALL's three rows as they looked BEFORE materialization (no slot keys) — the pre-migration
        // shape the update path turns into a slot-only diff.
        private const string PreMigrationOwner =
            "{\n  \"combat_vfx_cues\": [\n" +
            "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_X\",\n      \"trigger\": \"SPELL_CAST\",\n      \"anchor\": \"LEFT_HAND\",\n      \"vfx_id\": \"VFX_CAST\",\n      \"attach_mode\": \"FOLLOW_ANCHOR\",\n      \"vfx_role\": \"ATTACHED\",\n      \"lifecycle\": \"DURATION\",\n      \"duration_ms\": 350,\n      \"sort_order\": 100\n    },\n" +
            "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_X\",\n      \"trigger\": \"SPELL_IMPACT\",\n      \"anchor\": \"IMPACT_POINT\",\n      \"vfx_id\": \"VFX_HIT\",\n      \"attach_mode\": \"SPAWN_WORLD\",\n      \"vfx_role\": \"ONE_SHOT\",\n      \"lifecycle\": \"DURATION\",\n      \"duration_ms\": 1000,\n      \"sort_order\": 120\n    }\n  ]\n}\n";

        private static List<object> SpellXUpdateRows() => new()
        {
            Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_CAST", "FOLLOW_ANCHOR", "ATTACHED", "DURATION", null, 350, 100),
            Row("impact", "SPELL_IMPACT", "IMPACT_POINT", "VFX_HIT", "SPAWN_WORLD", "ONE_SHOT", "DURATION", null, 1000, 120),
        };

        [Test]
        public void UpdatePath_ChangesOnlyTheInsertedSlotKeys()
        {
            string output = Splice(PreMigrationOwner, "SPELL_X", SpellXUpdateRows());
            Assert.That(output, Is.Not.EqualTo(PreMigrationOwner));

            string stripped = Regex.Replace(output, "[ \\t]*\"slot\": \"[^\"]*\",\\n", string.Empty);
            Assert.That(stripped, Is.EqualTo(PreMigrationOwner), "an update must change nothing but the inserted slot keys");
            Assert.That(Regex.Matches(output, "\"slot\":").Count, Is.EqualTo(2));
        }

        [Test]
        public void UpdatePath_OverwritesFields_AndPreservesLegacyOverrideKeys()
        {
            const string catalog =
                "{\n  \"combat_vfx_cues\": [\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"WARRIOR_MELEE\",\n      \"trigger\": \"MELEE_IMPACT\",\n      \"hit_index\": 0,\n      \"anchor\": \"GROUND_UNDER_TARGET\",\n      \"vfx_id\": \"VFX_MELEE_01\",\n      \"attach_mode\": \"SPAWN_WORLD\",\n      \"duration_ms\": 2500,\n      \"sort_order\": 10\n    },\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_TEST\",\n      \"trigger\": \"SPELL_CAST\",\n      \"anchor\": \"LEFT_HAND\",\n      \"vfx_id\": \"VFX_OLD_CAST\",\n      \"attach_mode\": \"FOLLOW_ANCHOR\",\n      \"vfx_role\": \"ATTACHED\",\n      \"lifecycle\": \"DURATION\",\n      \"legacy_note\": \"KEEP_ME\",\n      \"duration_ms\": 350,\n      \"sort_order\": 100\n    }\n  ],\n  \"other\": 42\n}\n";

            var rows = new List<object>
            {
                Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_NEW_CAST", "FOLLOW_ANCHOR",
                    "ATTACHED", "DURATION", null, 350, 100),
            };
            string output = Splice(catalog, "SPELL_TEST", rows);

            Assert.That(output, Does.Contain("\"owner_id\": \"WARRIOR_MELEE\""));
            Assert.That(output, Does.Contain("  \"other\": 42\n}\n"));
            Assert.That(output, Does.Contain("\"vfx_id\": \"VFX_NEW_CAST\""));
            Assert.That(output, Does.Not.Contain("VFX_OLD_CAST"));

            // Known keys re-serialise in canonical order (slot after owner_id); an unmodelled legacy key
            // is preserved but sorts after the known keys.
            string expectedRow =
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_TEST\",\n" +
                "      \"slot\": \"cast_glow\",\n      \"trigger\": \"SPELL_CAST\",\n      \"anchor\": \"LEFT_HAND\",\n" +
                "      \"vfx_id\": \"VFX_NEW_CAST\",\n      \"attach_mode\": \"FOLLOW_ANCHOR\",\n      \"vfx_role\": \"ATTACHED\",\n" +
                "      \"lifecycle\": \"DURATION\",\n      \"duration_ms\": 350,\n      \"sort_order\": 100,\n      \"legacy_note\": \"KEEP_ME\"\n    }";
            Assert.That(output, Does.Contain(expectedRow));
        }

        [Test]
        public void UpdatePath_PreservesEscapedStrings_AsValidJsonEscapes()
        {
            const string catalog =
                "{\n  \"combat_vfx_cues\": [\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_ESCAPES\",\n      \"trigger\": \"SPELL_CAST\",\n      \"anchor\": \"LEFT_HAND\",\n      \"vfx_id\": \"VFX_OLD_CAST\",\n      \"attach_mode\": \"FOLLOW_ANCHOR\",\n      \"vfx_role\": \"ATTACHED\",\n      \"lifecycle\": \"DURATION\",\n      \"legacy_note\": \"Line\\nTab\\tFace \\u263A Slash \\/ Quote \\\" Done\",\n      \"duration_ms\": 350,\n      \"sort_order\": 100\n    }\n  ]\n}\n";

            var rows = new List<object>
            {
                Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_NEW_CAST", "FOLLOW_ANCHOR",
                    "ATTACHED", "DURATION", null, 350, 100),
            };
            string output = Splice(catalog, "SPELL_ESCAPES", rows);

            Assert.That(output, Does.Contain("\"legacy_note\": \"Line\\nTab\\tFace \\u263A Slash / Quote \\\" Done\""));
            Assert.That(output, Does.Contain("\"vfx_id\": \"VFX_NEW_CAST\""));
            Assert.That(output, Does.Not.Contain("Line\nTab\tFace"), "control characters must be re-escaped");
            AssertValidJson(output);
        }

        // ----- insertion path: generator-only slots + brand-new owners -----

        [Test]
        public void InsertPath_AddsNewSlots_InSortOrder_AroundTheExistingRow()
        {
            // Existing owner has only projectile_body (110); the generator also produces cast_glow (100)
            // and impact (120). They must interleave into sort position, not tack onto the end.
            const string catalog =
                "{\n  \"combat_vfx_cues\": [\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_TEST\",\n      \"trigger\": \"SPELL_RELEASE\",\n      \"anchor\": \"LEFT_HAND\",\n      \"vfx_id\": \"VFX_BODY\",\n      \"attach_mode\": \"SPAWN_WORLD\",\n      \"vfx_role\": \"PROJECTILE_BODY\",\n      \"lifecycle\": \"UNTIL_TERMINAL_EVENT\",\n      \"projectile_sequence_index\": 0,\n      \"duration_ms\": 0,\n      \"sort_order\": 110\n    }\n  ]\n}\n";

            var rows = new List<object>
            {
                Row("cast_glow", "SPELL_CAST", "LEFT_HAND", "VFX_CAST", "FOLLOW_ANCHOR", "ATTACHED", "DURATION", null, 350, 100),
                Row("projectile_body", "SPELL_RELEASE", "LEFT_HAND", "VFX_BODY", "SPAWN_WORLD", "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, 110),
                Row("impact", "SPELL_IMPACT", "IMPACT_POINT", "VFX_HIT", "SPAWN_WORLD", "ONE_SHOT", "DURATION", null, 1000, 120),
            };
            string output = Splice(catalog, "SPELL_TEST", rows);

            Assert.That(Regex.Matches(output, "\"owner_id\": \"SPELL_TEST\"").Count, Is.EqualTo(3));
            Assert.That(output.IndexOf("VFX_CAST", StringComparison.Ordinal), Is.LessThan(output.IndexOf("VFX_BODY", StringComparison.Ordinal)));
            Assert.That(output.IndexOf("VFX_BODY", StringComparison.Ordinal), Is.LessThan(output.IndexOf("VFX_HIT", StringComparison.Ordinal)));
            AssertValidJson(output);
        }

        [Test]
        public void InsertPath_AppendsBrandNewOwner_AtArrayEnd()
        {
            const string catalog =
                "{\n  \"combat_vfx_cues\": [\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_OTHER\",\n      \"trigger\": \"SPELL_RELEASE\",\n      \"anchor\": \"CASTER\",\n      \"vfx_id\": \"VFX_A\",\n      \"attach_mode\": \"SPAWN_WORLD\",\n      \"vfx_role\": \"ONE_SHOT\",\n      \"lifecycle\": \"DURATION\",\n      \"duration_ms\": 500,\n      \"sort_order\": 10\n    }\n  ],\n  \"tail\": true\n}\n";

            var rows = new List<object>
            {
                Row("burst", "SPELL_RELEASE", "CASTER", "VFX_NOVA", "SPAWN_WORLD", "ONE_SHOT", "PARTICLE_SYSTEM", null, 0, 200),
            };
            string output = Splice(catalog, "SPELL_BRAND_NEW", rows);

            Assert.That(output, Does.Contain("\"owner_id\": \"SPELL_BRAND_NEW\""));
            Assert.That(output, Does.Contain("\"slot\": \"burst\""));
            Assert.That(output, Does.Contain("  \"tail\": true\n}\n"), "trailing data preserved");
            Assert.That(output.IndexOf("SPELL_BRAND_NEW", StringComparison.Ordinal),
                Is.GreaterThan(output.IndexOf("SPELL_OTHER", StringComparison.Ordinal)), "appended after existing rows");
            AssertValidJson(output);
        }

        [Test]
        public void Splice_KeepsUnmatchedExistingRow_NeverDeletesAuthoredData()
        {
            const string catalog =
                "{\n  \"combat_vfx_cues\": [\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_TEST\",\n      \"trigger\": \"SPELL_RELEASE\",\n      \"anchor\": \"LEFT_HAND\",\n      \"vfx_id\": \"VFX_BODY\",\n      \"attach_mode\": \"SPAWN_WORLD\",\n      \"vfx_role\": \"PROJECTILE_BODY\",\n      \"lifecycle\": \"UNTIL_TERMINAL_EVENT\",\n      \"projectile_sequence_index\": 0,\n      \"duration_ms\": 0,\n      \"sort_order\": 110\n    },\n" +
                "    {\n      \"owner_kind\": \"ABILITY\",\n      \"owner_id\": \"SPELL_TEST\",\n      \"trigger\": \"SPELL_IMPACT\",\n      \"anchor\": \"IMPACT_POINT\",\n      \"vfx_id\": \"VFX_KEEP\",\n      \"attach_mode\": \"SPAWN_WORLD\",\n      \"vfx_role\": \"ONE_SHOT\",\n      \"lifecycle\": \"DURATION\",\n      \"duration_ms\": 900,\n      \"sort_order\": 120\n    }\n  ]\n}\n";

            // Provide only the 110 row; the 120 row has no counterpart and must survive.
            var rows = new List<object>
            {
                Row("projectile_body", "SPELL_RELEASE", "LEFT_HAND", "VFX_BODY2", "SPAWN_WORLD", "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, 110),
            };
            string output = Splice(catalog, "SPELL_TEST", rows);

            Assert.That(output, Does.Contain("\"vfx_id\": \"VFX_KEEP\""), "unmatched authored row preserved");
            Assert.That(output, Does.Contain("\"vfx_id\": \"VFX_BODY2\""), "matched row still updated");
            AssertValidJson(output);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Splice_TargetsAbilityOwnerWithoutRewritingSameIdLegacySpell(bool hasAbilityRow)
        {
            const string legacy = "{\"owner_kind\":\"SPELL\",\"owner_id\":\"SPELL_TEST\",\"trigger\":\"AREA_IMPACT\",\"vfx_id\":\"VFX_KEEP_LEGACY\",\"sort_order\":110}";
            const string ability = "{\"owner_kind\":\"ABILITY\",\"owner_id\":\"SPELL_TEST\",\"trigger\":\"SPELL_RELEASE\",\"vfx_id\":\"VFX_OLD\",\"sort_order\":110}";
            string catalog = "{\"combat_vfx_cues\":[" + legacy + (hasAbilityRow ? "," + ability : "") + "]}";
            var rows = new List<object>
            {
                Row("projectile_body", "SPELL_RELEASE", "LEFT_HAND", "VFX_NEW", "SPAWN_WORLD", "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, 110),
            };

            string output = Splice(catalog, "SPELL_TEST", rows);
            Assert.That(output, Does.Contain(legacy), "legacy namespace must remain byte-identical");
            Assert.That(output, Does.Contain("\"owner_kind\": \"ABILITY\""));
            Assert.That(output, Does.Contain("\"vfx_id\": \"VFX_NEW\""));
            Assert.That(output, Does.Not.Contain("VFX_OLD"));
            AssertValidJson(output);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FileWrite_RequiresTheCatalogUsedByThePreview(bool unchangedSincePreview)
        {
            string preview = File.ReadAllText(CatalogRelativePath);
            string manualEdit = preview + "\n";
            string path = Path.Combine(Path.GetTempPath(), $"arena-cue-write-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, unchangedSincePreview ? preview : manualEdit);
                Array rows = Array.CreateInstance(RowType, 1);
                rows.SetValue(FireballRows()[2], 0);
                MethodInfo write = WriterType.GetMethod("WriteOwnerCues")!;
                object[] arguments = { path, "SPELL_FIREBALL", rows, preview };
                if (unchangedSincePreview)
                {
                    Assert.That((bool)write.Invoke(null, arguments)!, Is.False);
                    Assert.That(File.ReadAllText(path), Is.EqualTo(preview));
                }
                else
                {
                    var error = Assert.Throws<TargetInvocationException>(() => write.Invoke(null, arguments));
                    Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
                    Assert.That(error.InnerException!.Message, Does.Contain("changed since this preview"));
                    Assert.That(File.ReadAllText(path), Is.EqualTo(manualEdit));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        // The authoring window's sort_order-assignment policy for inserted (generator-only) slots. The
        // load-bearing property (the writer joins rows by sort_order) is that an inserted row never
        // collides with an authored row or another inserted row — so it is strictly past the owner's
        // current max and strictly increasing per insert.
        [Test]
        public void NextInsertSortOrder_IsPastTheMax_AndCollisionFree()
        {
            // Real target owners from the migration map: METEOR (max 170), FROZEN_SPLINTERS (189),
            // BLESSED_SHIELD (15), plus the brand-new-owner case (0).
            foreach (int max in new[] { 0, 15, 170, 178, 189, 200 })
            {
                int first = NextInsertSortOrder(max, 0);
                int second = NextInsertSortOrder(max, 1);
                Assert.That(first, Is.GreaterThan(max), $"insert 0 must be past max {max}");
                Assert.That(second, Is.GreaterThan(first), $"insert 1 must be past insert 0 (max {max})");
                Assert.That(first % 10, Is.EqualTo(0), "inserted sort_orders follow the multiple-of-10 convention");
                Assert.That(second - first, Is.EqualTo(10), "inserts are spaced by 10");
            }

            // Concrete: BLESSED_SHIELD (projectile_body @ 15) adds cast_glow + impact → 20, 30.
            Assert.That(NextInsertSortOrder(15, 0), Is.EqualTo(20));
            Assert.That(NextInsertSortOrder(15, 1), Is.EqualTo(30));
            // METEOR (travel_body 168 / impact 170) adds cast_glow → 180 (clears 170).
            Assert.That(NextInsertSortOrder(170, 0), Is.EqualTo(180));
            // FROZEN_SPLINTERS (188/189) adds cast_glow → 190 (clears 189, not a multiple of 10).
            Assert.That(NextInsertSortOrder(189, 0), Is.EqualTo(190));
        }

        [Test]
        public void SpellAuthoringWindow_ParsesExplicitSlotKeysForRoundTrip()
        {
            Assert.That(TryParseSlotKey("self_flash", out string selfFlash), Is.True);
            Assert.That(selfFlash, Is.EqualTo("SelfFlash"));
            Assert.That(TryParseSlotKey("aura_ground", out string auraGround), Is.True);
            Assert.That(auraGround, Is.EqualTo("AuraGround"));
            Assert.That(TryParseSlotKey("character_fx", out string characterFx), Is.True);
            Assert.That(characterFx, Is.EqualTo("CharacterFx"));
            Assert.That(TryParseSlotKey("character_fx/body_rings", out string characterVariant), Is.True);
            Assert.That(characterVariant, Is.EqualTo("CharacterFx"));
            Assert.That(TryParseSlotKey("target_attachment", out string targetAttachment), Is.True);
            Assert.That(targetAttachment, Is.EqualTo("TargetAttachment"));
            Assert.That(TryParseSlotKey("persistent_character_fx", out string persistentCharacterFx), Is.True);
            Assert.That(persistentCharacterFx, Is.EqualTo("PersistentCharacterFx"));
            Assert.That(TryParseSlotKey("max_stack_character_fx", out string maxStackCharacterFx), Is.True);
            Assert.That(maxStackCharacterFx, Is.EqualTo("MaxStackCharacterFx"));
            Assert.That(TryParseSlotKey("not_a_slot", out _), Is.False);
        }

        [Test]
        public void CharacterFxSlotIdentity_IsStableAndRequiresVariantsForMultiplicity()
        {
            Assert.That(TryBuildGeneratedSlotKey("CharacterFx", "", 1, out string single, out _), Is.True);
            Assert.That(single, Is.EqualTo("character_fx"));

            Assert.That(TryBuildGeneratedSlotKey(
                "CharacterFx", "Body Rings", 2, out string first, out _), Is.True);
            Assert.That(first, Is.EqualTo("character_fx/body_rings"));
            Assert.That(TryBuildGeneratedSlotKey(
                "CharacterFx", "Shoulder Flames", 2, out string second, out _), Is.True);
            Assert.That(second, Is.EqualTo("character_fx/shoulder_flames"));
            Assert.That(first, Is.Not.EqualTo(second));
            MethodInfo normalize = EditorAssembly.GetType("Arena.Editor.SpellAuthoringWindow", true)!
                .GetMethod("TryNormalizeExplicitSlotKey", BindingFlags.Static | BindingFlags.NonPublic)!;
            object?[] arguments = { "character_fx/Body   Rings", null };
            Assert.That((bool)normalize.Invoke(null, arguments)!, Is.True);
            Assert.That(arguments[1], Is.EqualTo(first), "explicit and generated identities must agree");

            Assert.That(TryBuildGeneratedSlotKey(
                "CharacterFx", "", 2, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("variantId"));
        }

        private static int NextInsertSortOrder(int maxExistingSortOrder, int insertIndex)
        {
            Type windowType = EditorAssembly.GetType("Arena.Editor.SpellAuthoringWindow", throwOnError: true)!;
            MethodInfo method = windowType.GetMethod(
                "NextInsertSortOrder",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            return (int)method.Invoke(null, new object[] { maxExistingSortOrder, insertIndex })!;
        }

        private static bool TryParseSlotKey(string slotKey, out string parsedSlot)
        {
            Type windowType = EditorAssembly.GetType("Arena.Editor.SpellAuthoringWindow", throwOnError: true)!;
            MethodInfo method = windowType.GetMethod(
                "TryParseSlotKey",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] args = { slotKey, null };
            bool result = (bool)method.Invoke(null, args)!;
            parsedSlot = args[1]?.ToString() ?? string.Empty;
            return result;
        }

        private static bool TryBuildGeneratedSlotKey(
            string slotName,
            string variantId,
            int entryCount,
            out string slotKey,
            out string error)
        {
            Type windowType = EditorAssembly.GetType("Arena.Editor.SpellAuthoringWindow", throwOnError: true)!;
            MethodInfo method = windowType.GetMethod(
                "TryBuildGeneratedSlotKey",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            Type slotType = method.GetParameters()[0].ParameterType;
            object slot = Enum.Parse(slotType, slotName);
            object?[] args = { slot, variantId, entryCount, null, null };
            bool result = (bool)method.Invoke(null, args)!;
            slotKey = args[3]?.ToString() ?? string.Empty;
            error = args[4]?.ToString() ?? string.Empty;
            return result;
        }

        // Lightweight structural sanity (Unity's runtime has no System.Text.Json): delimiters balance
        // outside of strings. The net10 write-check harness additionally parses the output with a real
        // JSON parser.
        private static void AssertValidJson(string s)
        {
            int brace = 0, bracket = 0;
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{': brace++; break;
                    case '}': brace--; break;
                    case '[': bracket++; break;
                    case ']': bracket--; break;
                }
                Assert.That(brace, Is.GreaterThanOrEqualTo(0), "unbalanced }");
                Assert.That(bracket, Is.GreaterThanOrEqualTo(0), "unbalanced ]");
            }

            Assert.That(inString, Is.False, "unterminated string");
            Assert.That(brace, Is.EqualTo(0), "unbalanced braces");
            Assert.That(bracket, Is.EqualTo(0), "unbalanced brackets");
        }
    }
}
