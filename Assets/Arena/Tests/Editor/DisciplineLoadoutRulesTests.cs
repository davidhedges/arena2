#nullable enable

using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DisciplineLoadoutRulesTests
    {
        private static readonly Type Rules = AppDomain.CurrentDomain
            .Load("Assembly-CSharp")
            .GetType("Arena.UI.DisciplineLoadoutRules", throwOnError: true)!;
        private static readonly Type AbilityTags = AppDomain.CurrentDomain
            .Load("Assembly-CSharp")
            .GetType("Arena.Combat.AbilityTagCodec", throwOnError: true)!;

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
            MethodInfo method = AbilityTags.GetMethod(
                "HasTag",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null);
            Assert.That(
                (bool)method.Invoke(null, new object?[] { encodedTags, expectedTag })!,
                Is.EqualTo(expected));
        }

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(7, true)]
        [TestCase(8, true)]
        [TestCase(35, true)]
        public void PrimaryDiscipline_RequiresAtLeastOneAuthoredAbility(
            int availableAbilityCount,
            bool expected)
        {
            Assert.That(
                Invoke<bool>("CanBePrimary", availableAbilityCount),
                Is.EqualTo(expected));
        }

        [Test]
        public void ValidLoadout_RequiresEightPrimaryAndOneAbilityPerSecondary()
        {
            Assert.That(Invoke<bool>("IsValid", 8, new[] { 1, 1 }), Is.True);
            Assert.That(Invoke<bool>("IsValid", 7, new[] { 1, 1 }), Is.False);
            Assert.That(Invoke<bool>("IsValid", 8, new[] { 1, 0 }), Is.False);
            Assert.That(Invoke<bool>("IsValid", 8, new[] { 1, 1, 1 }), Is.False);
        }

        [Test]
        public void AbilityPointBudget_ClampsAtZero()
        {
            Assert.That(Invoke<int>("RemainingPoints", new[] { 6, 5, 5, 4, 5 }), Is.Zero);
            Assert.That(Invoke<int>("RemainingPoints", new[] { 6, 5, 0, 0, 0 }), Is.EqualTo(14));
            Assert.That(Invoke<int>("RemainingPoints", new[] { 30 }), Is.Zero);
        }

        [Test]
        public void DisciplinesScreen_HasLiveHubNavigationAndNoCornerOrnaments()
        {
            string hub = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            string uxml = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/Disciplines.uxml");
            string uss = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/Disciplines.uss");

            Assert.That(hub, Does.Contain("_root.Q<Button>(\"NavDisciplines\")"));
            Assert.That(hub, Does.Contain("_disciplinesScreen.Open()"));
            Assert.That(uxml, Does.Contain("name=\"PrimaryAbilityGrid\""));
            Assert.That(uxml, Does.Contain("vertical-scroller-visibility=\"Hidden\""));
            Assert.That(uxml, Does.Contain("name=\"SecondaryAbilityGroups\""));
            Assert.That(uss, Does.Not.Contain(".corner"));
            Assert.That(uss, Does.Not.Contain("+.secondary-group"));

            string screen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            Assert.That(screen, Does.Contain("verticalScrollerVisibility = ScrollerVisibility.Hidden"));
        }

        [Test]
        public void DisciplineLoadout_PersistsAbilitiesAndBindsAllHubRows()
        {
            string screen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            string subscriptions = File.ReadAllText("Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs");
            string hub = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            string hubNetwork = File.ReadAllText("Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string hubUxml = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/Hub.uxml");

            Assert.That(screen, Does.Contain("BuildSelectedAbilityIds()"));
            Assert.That(screen, Does.Contain("hub.SaveDisciplineLoadout"));
            Assert.That(screen, Does.Not.Contain("NetworkManager.Instance?.Conn"));
            Assert.That(subscriptions, Does.Contain("From.CharacterDisciplineAbilitySelection()"));
            Assert.That(hubNetwork, Does.Contain("From.MyHubLoadout().ToSql()"));
            Assert.That(hubNetwork, Does.Contain("From.HubCombatDisciplineDefinition().ToSql()"));
            Assert.That(hubNetwork, Does.Contain("From.HubAbilityDefinition().ToSql()"));
            Assert.That(hubNetwork, Does.Contain("context.Db.MyHubLoadout.Iter()"));
            Assert.That(hubNetwork, Does.Not.Contain("ApplyCommittedDisciplineLoadout("));
            Assert.That(hub, Does.Contain("HubLoadoutSnapshot? loadout"));
            Assert.That(hub, Does.Contain("_hubController?.RefreshShowcaseLoadout()"));
            Assert.That(hubUxml, Does.Contain("name=\"LoadoutPrimaryName\""));
            Assert.That(hubUxml, Does.Contain("name=\"LoadoutSecondary1Name\""));
            Assert.That(hubUxml, Does.Contain("name=\"LoadoutSecondary2Name\""));
            Assert.That(hubUxml, Does.Contain("name=\"LoadoutPrimaryArt\""));
            Assert.That(hubUxml, Does.Contain("name=\"LoadoutSecondary1Art\""));
            Assert.That(hubUxml, Does.Contain("name=\"LoadoutSecondary2Art\""));
            Assert.That(hubUxml, Does.Not.Contain("ACTIVE LOADOUT"));
            Assert.That(hubUxml, Does.Not.Contain("RANKED RECORD"));
            Assert.That(hub, Does.Contain("DisciplinesScreen.ResolveDisciplineIcon(normalizedId)"));
        }

        [Test]
        public void DisciplineLoadout_PassivesAreExplicitSelectableQuotaChoices()
        {
            string screen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");

            Assert.That(
                screen,
                Does.Contain("bool selected = SelectedSet(discipline.Id).Contains(ability.Id);"));
            Assert.That(
                screen,
                Does.Not.Contain("ability.IsPassive || SelectedSet(discipline.Id).Contains(ability.Id)"));
            Assert.That(
                screen,
                Does.Contain("button.clicked += () => ToggleAbility(disciplineId, abilityId);"));
            Assert.That(
                screen,
                Does.Contain(".Where(ability => selected.Contains(ability.Id))"));
        }

        private static T Invoke<T>(string methodName, params object[] arguments)
        {
            object? result = Rules.GetMethod(methodName)?.Invoke(null, arguments);
            Assert.That(result, Is.Not.Null, $"{methodName} should return a value.");
            return (T)result!;
        }
    }
}
