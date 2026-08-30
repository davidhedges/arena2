#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Network;
using Arena.UI;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0)
            {
                if (args.Length != 3 || args[0] != "--live")
                    throw new InvalidOperationException(
                        "Usage: CombatBuildV2ClientRehearsal [--live SERVER_URI DATABASE]");
                CombatBuildV2LiveClient.Run(args[1], args[2]);
                return 0;
            }

            DormantSameParentReflow();
            MixedDualBarTransitions();
            EighteenSelectedActivesRemainReachable();
            EmptySpecializationsAndExactErrors();
            Console.WriteLine("PHASE6_CLIENT_REHEARSAL_PASS checks=4");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"PHASE6_CLIENT_REHEARSAL_FAIL: {error.Message}");
            return 1;
        }
    }

    private static void DormantSameParentReflow()
    {
        CombatBuildV2CatalogModel catalog = BaseCatalog();
        CombatBuildV2EditorModel editor = new(
            new CombatBuildV2DraftModel(
                2,
                7,
                "DAGGERS",
                new[] { Selected(0, "DAGGERS_BLADEDANCER") },
                new[] { "DAGGERS_EXECUTIONER" },
                new[] { DaggerConfiguration() },
                new[]
                {
                    Feature("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0),
                    Feature("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", 0),
                },
                new[] { "MASTERY" }),
            catalog,
            Contract());

        Require(editor.SelectedFeatureCount == 1, "dormant feature counted toward capacity");
        Require(editor.MasteryActive, "single-parent Mastery should be active");
        Require(editor.AddSpecialization("DAGGERS_EXECUTIONER"), "dormant Form did not restore");
        CombatBuildV2DraftModel restored = editor.ToDraft();
        Require(editor.SelectedFeatureCount == 2, "restored Form feature was not selected");
        Require(editor.DerivedParentDisciplineIds().SequenceEqual(new[] { "DAGGERS" }),
            "same-parent Forms produced duplicate parent configuration targets");
        Require(Order(restored, "DAGGER_QUICK_CUT") == 0, "active order did not win collision");
        Require(Order(restored, "DAGGER_GUT_RIPPER") == 1, "returning order did not reflow");
        Require(editor.CanSubmit, "two nonempty Dagger Forms should submit");
    }

    private static void MixedDualBarTransitions()
    {
        CombatBuildV2CatalogModel catalog = BaseCatalog();
        CombatBuildV2DraftModel draft = new(
            2,
            11,
            "DAGGERS",
            new[]
            {
                Selected(0, "DAGGERS_BLADEDANCER"),
                Selected(1, "DAGGERS_EXECUTIONER"),
                Selected(2, "RUIN"),
            },
            Array.Empty<string>(),
            new[] { DaggerConfiguration(), StaffConfiguration() },
            new[]
            {
                Feature("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0),
                Feature("DAGGERS_EXECUTIONER", "DAGGER_GUT_RIPPER", 1),
                Feature("RUIN", "SPELL_FIREBALL", 0),
                Feature("RUIN", "RUIN_FLAMING_WEAPON", null),
            },
            new[] { "MASTERY" });
        CombatBuildV2EditorModel editor = new(draft, catalog, Contract());
        CombatBuildV2EditorViewModel editorView = new(editor, catalog);
        Require(editor.FeatureCapacityText == "4 / 18 FEATURES", "feature meter drifted");
        Require(editor.TraitCapacityText == "1 / 3 TRAITS", "Trait meter drifted");
        Require(editorView.Cards().All(card => !card.IsEmpty), "nonempty cards rendered empty");
        Require(editor.FeaturePickerOptions("RUIN", CombatFeatureLoadoutKindV2.Spell)
            .All(row => row.LoadoutKind == CombatFeatureLoadoutKindV2.Spell),
            "School picker leaked non-Spells");
        Require(editor.FindDisciplineConfiguration("DAGGERS") != null
            && editor.FindDisciplineConfiguration("STAFF") != null,
            "derived parent weapon editors were not preserved");

        CombatBuildV2HudModel daggers = CombatBuildV2HudModel.Create(
            draft,
            catalog,
            Contract(),
            "DAGGERS");
        CombatBuildV2HudModel staff = CombatBuildV2HudModel.Create(
            draft,
            catalog,
            Contract(),
            "STAFF");
        Require(daggers.SwitchTargets.SequenceEqual(new[] { "DAGGERS", "STAFF" }),
            "switch targets did not deduplicate parents");
        Require(daggers.TechniqueBarVisible && daggers.TechniqueSlots.Count == 2,
            "Dagger merged Technique bar is wrong");
        Require(staff.SpellBarVisible && staff.SpellSlots.Count == 1,
            "global Spell bar disappeared under Staff");
        Require(!staff.TechniqueBarVisible && staff.TechniqueSlots.Count == 0,
            "Staff exposed a Technique bar");
        Require(daggers.SpellSlots[0].InputActionId == staff.SpellSlots[0].InputActionId,
            "Spell input changed across weapon switch");
        Require(daggers.ActivePerkAbilityIds.SequenceEqual(new[] { "RUIN_FLAMING_WEAPON" })
            && staff.ActivePerkAbilityIds.SequenceEqual(new[] { "RUIN_FLAMING_WEAPON" }),
            "Perk did not stay always active while its School remained selected");
    }

    private static void EighteenSelectedActivesRemainReachable()
    {
        var features = Enumerable.Range(0, 18)
            .Select(index => new CombatFeatureDefinitionV2Model(
                $"SPELL_{index:00}",
                "RUIN",
                "STAFF",
                CombatFeatureLoadoutKindV2.Spell,
                $"Spell {index}",
                "MANA",
                1f,
                (uint)index))
            .ToArray();
        CombatBuildV2CatalogModel catalog = new(
            new[] { Specialization("RUIN", "STAFF", CombatSpecializationKindV2.School, 0) },
            features,
            new[] { Mastery() });
        CombatBuildV2DraftModel draft = new(
            2,
            3,
            "STAFF",
            new[] { Selected(0, "RUIN") },
            Array.Empty<string>(),
            new[] { StaffConfiguration() },
            features.Select((row, index) => Feature("RUIN", row.AbilityId, (byte)index)),
            Array.Empty<string>());
        CombatBuildV2EditorModel editor = new(draft, catalog, Contract());
        CombatBuildV2HudModel hud = CombatBuildV2HudModel.Create(draft, catalog, Contract(), "STAFF");
        Require(editor.SelectedFeatureCount == 18 && editor.FeatureCapacityRemaining == 0,
            "18-point feature capacity is wrong");
        Require(hud.SpellSlots.Count == 18, "Spell bar applied an independent cap");
        Require(hud.SpellSlots.Select(row => row.InputActionId)
            .SequenceEqual(Enumerable.Range(0, 18).Select(index => $"COMBAT_ACTION_{index:00}")),
            "selected active input coverage is incomplete");
    }

    private static void EmptySpecializationsAndExactErrors()
    {
        CombatBuildV2CatalogModel catalog = BaseCatalog();
        CombatBuildV2EditorModel editor = new(
            new CombatBuildV2DraftModel(
                2,
                1,
                "DAGGERS",
                new[] { Selected(0, "DAGGERS_BLADEDANCER") },
                Array.Empty<string>(),
                new[] { DaggerConfiguration() },
                new[] { Feature("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0) },
                Array.Empty<string>()),
            catalog,
            Contract());
        Require(editor.AddSpecialization("DAGGERS_TRICKSTER"), "third Dagger Form did not add");
        Require(!editor.CanSubmit, "empty Form was allowed to submit");
        Require(editor.LocalSubmissionIssues().Any(issue => issue.Contains("DAGGERS_TRICKSTER")),
            "empty Form did not present an explicit invalid state");
        const string exact = "COMBAT_BUILD_V2_FEATURE_CAPACITY: selected feature count 19 exceeds 18";
        CombatBuildV2SaveResult rejected = CombatBuildV2SaveResult.Rejected(exact);
        Require(!rejected.Committed && rejected.DisplayText == exact,
            "server validation error was translated instead of displayed exactly");
    }

    private static CombatBuildV2CatalogModel BaseCatalog()
        => new(
            new[]
            {
                Specialization("DAGGERS_BLADEDANCER", "DAGGERS", CombatSpecializationKindV2.Form, 0),
                Specialization("DAGGERS_EXECUTIONER", "DAGGERS", CombatSpecializationKindV2.Form, 1),
                Specialization("DAGGERS_TRICKSTER", "DAGGERS", CombatSpecializationKindV2.Form, 2),
                Specialization("RUIN", "STAFF", CombatSpecializationKindV2.School, 3),
            },
            new[]
            {
                Active("DAGGER_QUICK_CUT", "DAGGERS_BLADEDANCER", "DAGGERS", CombatFeatureLoadoutKindV2.Technique, 0),
                Active("DAGGER_GUT_RIPPER", "DAGGERS_EXECUTIONER", "DAGGERS", CombatFeatureLoadoutKindV2.Technique, 0),
                Active("DAGGER_TRIP", "DAGGERS_TRICKSTER", "DAGGERS", CombatFeatureLoadoutKindV2.Technique, 0),
                Active("SPELL_FIREBALL", "RUIN", "STAFF", CombatFeatureLoadoutKindV2.Spell, 0),
                Active("RUIN_FLAMING_WEAPON", "RUIN", "STAFF", CombatFeatureLoadoutKindV2.Perk, 1),
            },
            new[] { Mastery() });

    private static CombatBuildV2ContractModel Contract()
        => new(
            2,
            1,
            3,
            18,
            3,
            Enumerable.Range(0, 18).Select(index => $"COMBAT_ACTION_{index:00}"));

    private static CombatSpecializationDefinitionV2Model Specialization(
        string id,
        string parent,
        CombatSpecializationKindV2 kind,
        uint order)
        => new(id, parent, kind, id, order);

    private static CombatFeatureDefinitionV2Model Active(
        string id,
        string specialization,
        string parent,
        CombatFeatureLoadoutKindV2 kind,
        uint order)
        => new(id, specialization, parent, kind, id, "NONE", 0f, order);

    private static CombatTraitDefinitionV2Model Mastery()
        => new("MASTERY", "Mastery", 0.1f, 0);

    private static CombatBuildV2SelectedSpecializationModel Selected(byte slot, string id)
        => new(slot, id);

    private static CombatBuildV2FeatureSelectionModel Feature(
        string specialization,
        string ability,
        byte? order)
        => new(specialization, ability, order);

    private static CombatBuildV2DisciplineConfigurationModel DaggerConfiguration()
        => new("DAGGERS", "TRAINING_DAGGER_PAIR", "", "", "");

    private static CombatBuildV2DisciplineConfigurationModel StaffConfiguration()
        => new("STAFF", "NEWBIE_STAFF_01", "", "", "");

    private static byte? Order(CombatBuildV2DraftModel draft, string abilityId)
        => draft.SelectedFeatures.Single(row => row.AbilityId == abilityId).PreferredBarOrder;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
