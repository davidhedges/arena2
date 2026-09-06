#nullable enable

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CombatBuildEditorUiTests
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void EditorModel_RetainsDormantFeaturesAndReflowsActiveScopes()
        {
            object editor = CreateEditor();
            Assert.That(Call(editor, "SetFeatureSelected", "A1", true), Is.True);
            Assert.That(Call(editor, "SetFeatureSelected", "B1", true), Is.True);
            Assert.That(Call(editor, "SetFeatureSelected", "A2", true), Is.True);
            Assert.That(Call(editor, "RemoveSpecialization", "B"), Is.True);
            Assert.That(Property(editor, "SelectedFeatureCount"), Is.EqualTo(2));
            Assert.That((IEnumerable)Property(editor, "DormantSpecializationIds"), Does.Contain("B"));
            Assert.That(Call(editor, "IsFeatureSelected", "B1"), Is.True);
            object[] active = ((IEnumerable)Property(Call(editor, "ToDraft"), "SelectedFeatures"))
                .Cast<object>().Where(row => (string)Property(row, "SpecializationId") == "A").ToArray();
            Assert.That(active.Select(row => Convert.ToInt32(Property(row, "PreferredBarOrder"))),
                Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(Call(editor, "AddSpecialization", "B"), Is.True);
            Assert.That((IEnumerable)Property(editor, "DormantSpecializationIds"), Does.Not.Contain("B"));
            Assert.That(Property(editor, "SelectedFeatureCount"), Is.EqualTo(3));
            object[] restored = ((IEnumerable)Property(Call(editor, "ToDraft"), "SelectedFeatures"))
                .Cast<object>().ToArray();
            Assert.That(restored.Select(row => (string)Property(row, "AbilityId")),
                Is.EquivalentTo(new[] { "A1", "A2", "B1" }));
            Assert.That(restored.Select(row => Convert.ToInt32(Property(row, "PreferredBarOrder"))),
                Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void EditorLayout_ExposesFormsSchoolsFeaturesTraitsAndPicker()
        {
            string uxml = File.ReadAllText(
                "Assets/Arena/Resources/UI/Toolkit/Disciplines.uxml");
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");

            Assert.That(uxml, Does.Contain("name=\"DisciplineCards\""));
            Assert.That(uxml, Does.Contain("name=\"ActiveAllocation\""));
            Assert.That(uxml, Does.Contain("name=\"TotalAllocation\""));
            Assert.That(uxml, Does.Contain("name=\"DisciplineAllocation\""));
            Assert.That(uxml, Does.Contain("name=\"TraitOptions\""));
            Assert.That(uxml, Does.Contain("name=\"SpecializationEditor\""));
            Assert.That(uxml, Does.Contain("name=\"SaveSummary\""));
            Assert.That(uxml, Does.Contain("name=\"PickerOptions\""));
            Assert.That(uxml, Does.Contain("name=\"SaveBuild\""));

            Assert.That(screen, Does.Contain("BuildSpecializationSummary"));
            Assert.That(screen, Does.Contain("BuildSpecializationCard"));
            Assert.That(screen, Does.Contain("TECHNIQUES · SPELLS · PERKS"));
            Assert.That(screen, Does.Contain("BuildTraitOptions"));
            Assert.That(screen, Does.Contain("BuildSaveSummary"));
            Assert.That(screen, Does.Contain("ADD FORM OR SCHOOL"));
            Assert.That(screen, Does.Contain("FeatureCapacityText"));
            Assert.That(screen, Does.Contain("TraitCapacityText"));
            Assert.That(screen, Does.Contain("_hub.SaveCombatBuild(_model.ToDraft())"));
            Assert.That(uxml + screen, Does.Not.Contain("Requires Rank"));
            Assert.That(uxml + screen, Does.Not.Contain("Requires Level"));
            Assert.That(screen, Does.Not.Contain("BuildStaffSchools"));
            Assert.That(screen, Does.Not.Contain("SaveWeaponLoadout"));
        }

        [Test]
        public void EditorDisablesSubmissionForLocallyInvalidV2Drafts()
        {
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            Assert.That(screen, Does.Contain("_model.CanSubmit"));
            Assert.That(screen, Does.Contain("LocalSubmissionIssues"));
            object editor = CreateEditor();
            Assert.That(Property(editor, "CanSubmit"), Is.False);
            Assert.That(((IEnumerable)Call(editor, "LocalSubmissionIssues")).Cast<string>(),
                Has.Some.Contains("at least one feature"));
            Call(editor, "SetFeatureSelected", "A1", true);
            Assert.That(Property(editor, "CanSubmit"), Is.False, "Every selected form needs a feature.");
            Call(editor, "SetFeatureSelected", "B1", true);
            Assert.That(Property(editor, "CanSubmit"), Is.True);
            Assert.That((IEnumerable)Call(editor, "LocalSubmissionIssues"), Is.Empty);
            Call(editor, "SetFeatureSelected", "B1", false);
            Assert.That(Property(editor, "CanSubmit"), Is.False);
        }

        [Test]
        public void EditorTerminatesCommittedSaveWithSuccessAndRestoresControls()
        {
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");

            Assert.That(screen, Does.Contain("SetStatus(\"Build saved.\")"));
            Assert.That(screen, Does.Not.Contain("Build committed. Waiting for the new revision"));
            Assert.That(
                screen,
                Does.Match(
                    @"if \(committed\)[\s\S]*?_lastServerFailure = string\.Empty;[\s\S]*?Render\(\);[\s\S]*?SetStatus\(""Build saved\.""\);"));
        }

        [Test]
        public void EditorDisplaysServerValidationCodesVerbatimWithoutOwningACodeMap()
        {
            string server = File.ReadAllText("server/src/combat_build_v2.rs");
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            string[] serverCodes = Regex.Matches(server, "COMBAT_BUILD_V2_[A-Z_]+")
                .Cast<Match>()
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.That(serverCodes.Length, Is.GreaterThan(15));
            Assert.That(screen, Does.Contain("_lastServerFailure = reason;"));
            Type status = RuntimeType("Arena.Network.HubCombatBuildSaveStatus");
            MethodInfo rejected = status.GetMethod("Rejected", StaticMembers)!;
            foreach (string serverCode in serverCodes)
            {
                Assert.That(
                    rejected.Invoke(null, new object[] { serverCode }),
                    Is.EqualTo($"SAVE REJECTED — {serverCode}"));
                Assert.That(
                    screen,
                    Does.Not.Contain(serverCode),
                    $"The editor must display {serverCode} from the reducer, not own a parallel code map.");
            }
        }

        [Test]
        public void HubProjectionUsesCanonicalCombatBuildV2Metadata()
        {
            string network = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string hub = File.ReadAllText("hub-server/src/lib.rs");

            Assert.That(network, Does.Contain("From.MyCombatBuildV2().ToSql()"));
            Assert.That(network, Does.Contain("From.CombatBuildV2ContractDefinition().ToSql()"));
            Assert.That(network, Does.Contain("From.CombatSpecializationDefinitionV2().ToSql()"));
            Assert.That(network, Does.Contain("From.CombatFeatureDefinitionV2().ToSql()"));
            Assert.That(network, Does.Contain("From.CombatTraitDefinitionV2().ToSql()"));
            Assert.That(network, Does.Not.Contain("From.MyCombatBuild().ToSql()"));
            Assert.That(hub, Does.Contain("sync_combat_build_v2_catalogs"));
        }

        private static object CreateEditor()
        {
            object form = Enum.Parse(RuntimeType("Arena.Network.CombatSpecializationKindV2"), "Form");
            object technique = Enum.Parse(RuntimeType("Arena.Network.CombatFeatureLoadoutKindV2"), "Technique");
            object Specialization(string id) => New("CombatSpecializationDefinitionV2Model", id, "DAGGERS", form, id, 0U);
            object Feature(string id, string owner) => New("CombatFeatureDefinitionV2Model", id, owner, "DAGGERS", technique, id, "STAMINA", 0f, 0U);
            object catalog = New("CombatBuildV2CatalogModel",
                Rows("CombatSpecializationDefinitionV2Model", Specialization("A"), Specialization("B")),
                Rows("CombatFeatureDefinitionV2Model", Feature("A1", "A"), Feature("A2", "A"), Feature("B1", "B")),
                Rows("CombatTraitDefinitionV2Model"));
            object draft = New("CombatBuildV2DraftModel", 2U, 1UL, "DAGGERS",
                Rows("CombatBuildV2SelectedSpecializationModel",
                    New("CombatBuildV2SelectedSpecializationModel", (byte)0, "A"),
                    New("CombatBuildV2SelectedSpecializationModel", (byte)1, "B")),
                Array.Empty<string>(),
                Rows("CombatBuildV2DisciplineConfigurationModel", New("CombatBuildV2DisciplineConfigurationModel",
                    "DAGGERS", "TRAINING_DAGGER_PAIR", "", "", "")),
                Rows("CombatBuildV2FeatureSelectionModel"), Array.Empty<string>());
            object contract = New("CombatBuildV2ContractModel", 2U, 1, 3, 18, 1, Array.Empty<string>());
            return New("CombatBuildV2EditorModel", draft, catalog, contract);
        }

        private static object New(string name, params object[] args)
            => Activator.CreateInstance(RuntimeType("Arena.Network." + name), InstanceMembers, null, args, null)!;

        private static Array Rows(string name, params object[] rows)
        {
            Array array = Array.CreateInstance(RuntimeType("Arena.Network." + name), rows.Length);
            for (int index = 0; index < rows.Length; index++) array.SetValue(rows[index], index);
            return array;
        }

        private static object Call(object target, string name, params object[] args)
            => target.GetType().GetMethod(name, InstanceMembers)!.Invoke(target, args)!;

        private static object Property(object target, string name)
            => target.GetType().GetProperty(name, InstanceMembers)!.GetValue(target)!;

        private static Type RuntimeType(string typeName)
            => AppDomain.CurrentDomain.Load("Assembly-CSharp")
                .GetType(typeName, throwOnError: true)!;
    }
}
