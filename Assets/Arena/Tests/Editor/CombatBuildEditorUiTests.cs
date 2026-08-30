#nullable enable

using System;
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

        [Test]
        public void EditorModel_RetainsDormantFeaturesAndReflowsActiveScopes()
        {
            string editor = File.ReadAllText(
                "Assets/Arena/Runtime/Network/CombatBuildV2EditorModel.cs");

            Assert.That(editor, Does.Contain("_dormantSpecializationIds"));
            Assert.That(editor, Does.Contain("_dormantSpecializationIds.Add(specializationId)"));
            Assert.That(editor, Does.Contain("_dormantSpecializationIds.Remove(specializationId)"));
            Assert.That(editor, Does.Contain("ReflowAllActiveScopes"));
            Assert.That(editor, Does.Contain("ActiveSelectionsInScope"));
            Assert.That(editor, Does.Contain("SelectedFeatureCount"));
            Assert.That(editor, Does.Contain("EmptySpecialization"));
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
            Assert.That(uxml, Does.Contain("name=\"PickerOptions\""));
            Assert.That(uxml, Does.Contain("name=\"SaveBuild\""));

            Assert.That(screen, Does.Contain("BuildSpecializationCard"));
            Assert.That(screen, Does.Contain("TECHNIQUES · SPELLS · PERKS"));
            Assert.That(screen, Does.Contain("CHARACTER TRAITS"));
            Assert.That(screen, Does.Contain("ADD FORM OR SCHOOL"));
            Assert.That(screen, Does.Contain("FeatureCapacityText"));
            Assert.That(screen, Does.Contain("TraitCapacityText"));
            Assert.That(screen, Does.Contain("_hub.SaveCombatBuild(_model.ToDraft())"));
            Assert.That(screen, Does.Not.Contain("BuildStaffSchools"));
            Assert.That(screen, Does.Not.Contain("SaveWeaponLoadout"));
        }

        [Test]
        public void EditorDisablesSubmissionForLocallyInvalidV2Drafts()
        {
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            string editor = File.ReadAllText(
                "Assets/Arena/Runtime/Network/CombatBuildV2EditorModel.cs");

            Assert.That(screen, Does.Contain("_model.CanSubmit"));
            Assert.That(screen, Does.Contain("LocalSubmissionIssues"));
            Assert.That(editor, Does.Contain("Select at least one Feature for"));
            Assert.That(editor, Does.Contain("CanSubmit => LocalSubmissionIssues().Count == 0"));
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

        private static Type RuntimeType(string typeName)
            => AppDomain.CurrentDomain.Load("Assembly-CSharp")
                .GetType(typeName, throwOnError: true)!;
    }
}
