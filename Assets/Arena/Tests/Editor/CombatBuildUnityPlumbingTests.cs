#nullable enable

using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CombatBuildUnityPlumbingTests
    {
        [TestCase("ACTION_BAR_ACTION", "ACTION_BAR_ACTION", true)]
        [TestCase("PASSIVE,ACTION_BAR_ACTION", "ACTION_BAR_ACTION", true)]
        [TestCase("PASSIVE, ACTION_BAR_ACTION", "ACTION_BAR_ACTION", true)]
        [TestCase("PASSIVE", "ACTION_BAR_ACTION", false)]
        [TestCase("PASSIVE|ACTION_BAR_ACTION", "ACTION_BAR_ACTION", false)]
        public void AbilityTags_UseCanonicalCommaDelimitedEncoding(
            string encodedTags,
            string expectedTag,
            bool expected)
        {
            Type abilityTags = AppDomain.CurrentDomain
                .Load("Assembly-CSharp")
                .GetType("Arena.Combat.AbilityTagCodec", throwOnError: true)!;
            MethodInfo method = abilityTags.GetMethod(
                "HasTag",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            Assert.That(
                (bool)method.Invoke(null, new object?[] { encodedTags, expectedTag })!,
                Is.EqualTo(expected));
        }

        [Test]
        public void HubNetwork_ConsumesAndSavesCanonicalCombatBuild()
        {
            string network = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string draft = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubCombatBuildDraft.cs");

            Assert.That(network, Does.Contain("From.MyCombatBuild().ToSql()"));
            Assert.That(network, Does.Contain("conn.Db.MyCombatBuild.OnInsert"));
            Assert.That(network, Does.Contain("_conn.Reducers.SaveCombatBuild"));
            Assert.That(network, Does.Not.Contain("SaveDisciplineLoadout"));
            Assert.That(network, Does.Not.Contain("SaveWeaponLoadout"));
            Assert.That(draft, Does.Contain("CombatBuildDraftInput ToReducerInput()"));
            Assert.That(draft, Does.Contain("SelectedDisciplines"));
            Assert.That(draft, Does.Contain("DisciplineConfigurations"));
            Assert.That(draft, Does.Contain("StaffSchoolIds"));
            Assert.That(draft, Does.Contain("ActiveAssignments"));
            Assert.That(draft, Does.Contain("PassiveAbilityIds"));
            Assert.That(draft, Does.Not.Contain("MaxActive"));
            Assert.That(draft, Does.Not.Contain("AbilityBudget"));
        }

        [Test]
        public void CanonicalEditors_CannotWriteRetiredShapes()
        {
            string disciplines = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            string actionBar = File.ReadAllText(
                "Assets/Arena/Runtime/UI/CharacterActionBarPanel.cs");
            string dragDrop = File.ReadAllText(
                "Assets/Arena/Runtime/UI/ActionBarDragDrop.cs");
            string equipment = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/EquipmentScreen.cs");

            Assert.That(disciplines, Does.Contain("_hub.SaveCombatBuild(_model.ToDraft())"));
            Assert.That(disciplines, Does.Contain("contract.ActionSlotIds"));
            Assert.That(disciplines, Does.Not.Contain("SaveDisciplineLoadout"));
            Assert.That(disciplines, Does.Not.Contain("PRIMARY"));
            Assert.That(disciplines, Does.Not.Contain("SECONDARY"));
            Assert.That(actionBar, Does.Contain("enabled = false"));
            Assert.That(actionBar, Does.Not.Contain("AssignCharacterActionBar"));
            Assert.That(dragDrop, Does.Not.Contain("AssignCharacterActionBar"));
            Assert.That(dragDrop, Does.Not.Contain("ClearCharacterActionBar"));
            Assert.That(equipment, Does.Contain("hub.SaveCombatBuild(updated)"));
            Assert.That(equipment, Does.Not.Contain("SaveWeaponLoadout"));
            Assert.That(equipment, Does.Not.Contain("} PRIMARY"));
        }

        [Test]
        public void HubSummary_UsesThreeOrderedCombatDisciplineSlots()
        {
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            string uxml = File.ReadAllText(
                "Assets/Arena/Resources/UI/Toolkit/Hub.uxml");

            Assert.That(screen, Does.Contain("FindSelectedDiscipline(build, 0)"));
            Assert.That(screen, Does.Contain("FindSelectedDiscipline(build, 1)"));
            Assert.That(screen, Does.Contain("FindSelectedDiscipline(build, 2)"));
            Assert.That(uxml, Does.Contain("name=\"LoadoutSlot0Name\""));
            Assert.That(uxml, Does.Contain("name=\"LoadoutSlot1Name\""));
            Assert.That(uxml, Does.Contain("name=\"LoadoutSlot2Name\""));
            Assert.That(uxml, Does.Not.Contain("PRIMARY DISCIPLINE"));
            Assert.That(uxml, Does.Not.Contain("SECONDARY DISCIPLINE"));
        }
    }
}
