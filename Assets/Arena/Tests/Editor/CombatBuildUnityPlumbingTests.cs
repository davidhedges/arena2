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
        public void HubNetwork_ConsumesAndSavesCanonicalCombatBuildV2()
        {
            string network = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string transport = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubCombatBuildV2Transport.cs");
            string model = File.ReadAllText(
                "Assets/Arena/Runtime/Network/CombatBuildV2Models.cs");

            Assert.That(network, Does.Contain("From.MyCombatBuildV2().ToSql()"));
            Assert.That(network, Does.Contain("conn.Db.MyCombatBuildV2.OnInsert"));
            Assert.That(network, Does.Contain("_conn.Reducers.SaveCombatBuildV2"));
            Assert.That(network, Does.Not.Contain("SaveWeaponLoadout"));
            Assert.That(transport, Does.Contain("CombatBuildV2DraftInput ToGenerated"));
            Assert.That(model, Does.Contain("SelectedSpecializations"));
            Assert.That(model, Does.Contain("DormantSpecializations"));
            Assert.That(model, Does.Contain("DisciplineConfigurations"));
            Assert.That(model, Does.Contain("SelectedFeatures"));
            Assert.That(model, Does.Contain("SelectedTraits"));
            Assert.That(model, Does.Not.Contain("StaffSchoolIds"));
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
            Assert.That(disciplines, Does.Contain("FeatureCapacityText"));
            Assert.That(disciplines, Does.Contain("TraitCapacityText"));
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
        public void HubSummary_UsesThreeOrderedFormOrSchoolSlots()
        {
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            string uxml = File.ReadAllText(
                "Assets/Arena/Resources/UI/Toolkit/Hub.uxml");

            Assert.That(screen, Does.Contain("FindSelectedSpecialization(build, 0)"));
            Assert.That(screen, Does.Contain("FindSelectedSpecialization(build, 1)"));
            Assert.That(screen, Does.Contain("FindSelectedSpecialization(build, 2)"));
            Assert.That(uxml, Does.Contain("name=\"LoadoutSlot0Name\""));
            Assert.That(uxml, Does.Contain("name=\"LoadoutSlot1Name\""));
            Assert.That(uxml, Does.Contain("name=\"LoadoutSlot2Name\""));
            Assert.That(uxml, Does.Not.Contain("PRIMARY DISCIPLINE"));
            Assert.That(uxml, Does.Not.Contain("SECONDARY DISCIPLINE"));
        }
    }
}
