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
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void EditorModel_RestoresDormantConfigurationAndPreservesExactAssignments()
        {
            object daggerWeapon = Construct(
                "Arena.Network.HubCombatBuildWeapon",
                "NEWBIE_DAGGERS_01",
                "DEFAULT",
                "NEWBIE_DAGGERS_02",
                "DEFAULT");
            object staffWeapon = Construct(
                "Arena.Network.HubCombatBuildWeapon",
                "NEWBIE_STAFF_01",
                "DEFAULT",
                string.Empty,
                string.Empty);
            object daggerAssignment = Construct(
                "Arena.Network.HubCombatBuildActionAssignment",
                "slot_0_0",
                "DAGGER_STALK");
            object staffAssignment = Construct(
                "Arena.Network.HubCombatBuildActionAssignment",
                "slot_0_1",
                "SPELL_FIREBALL");
            object daggerConfiguration = Configuration(
                "DAGGERS",
                daggerWeapon,
                Array.Empty<string>(),
                RuntimeArray("Arena.Network.HubCombatBuildActionAssignment", daggerAssignment),
                new[] { "DAGGER_LIGHTNING_REFLEXES" });
            object dormantStaffConfiguration = Configuration(
                "STAFF",
                staffWeapon,
                new[] { "RUIN" },
                RuntimeArray("Arena.Network.HubCombatBuildActionAssignment", staffAssignment),
                new[] { "SPELL_STAFF_PASSIVE" });
            object selectedDagger = Construct(
                "Arena.Network.HubCombatBuildSelectedDiscipline",
                (byte)0,
                "DAGGERS");
            object source = Construct(
                "Arena.Network.HubCombatBuildDraft",
                (ulong)7,
                "DAGGERS",
                RuntimeArray("Arena.Network.HubCombatBuildSelectedDiscipline", selectedDagger),
                RuntimeArray(
                    "Arena.Network.HubCombatBuildDisciplineConfiguration",
                    daggerConfiguration,
                    dormantStaffConfiguration));
            object model = Construct("Arena.Network.HubCombatBuildEditorModel", source);
            object staffDefinition = Construct(
                "Arena.Network.HubDisciplineSnapshot",
                "STAFF",
                "Staff",
                (uint)50,
                staffWeapon);

            Assert.That(Invoke<bool>(model, "AddDiscipline", staffDefinition), Is.True);
            Assert.That(Get<int>(model, "ActiveCount"), Is.EqualTo(2));
            Assert.That(Get<int>(model, "PassiveCount"), Is.EqualTo(2));
            object restoredStaff = Invoke<object>(model, "FindConfiguration", "STAFF");
            Assert.That(StringValues(restoredStaff, "StaffSchoolIds"), Is.EqualTo(new[] { "RUIN" }));
            AssertAssignment(restoredStaff, "slot_0_1", "SPELL_FIREBALL");

            Assert.That(Invoke<bool>(model, "RemoveDiscipline", "STAFF"), Is.True);
            Assert.That(Get<int>(model, "ActiveCount"), Is.EqualTo(1));
            Assert.That(Get<int>(model, "PassiveCount"), Is.EqualTo(1));
            Assert.That(Invoke<bool>(model, "AddDiscipline", staffDefinition), Is.True);
            restoredStaff = Invoke<object>(model, "FindConfiguration", "STAFF");
            Assert.That(StringValues(restoredStaff, "StaffSchoolIds"), Is.EqualTo(new[] { "RUIN" }));
            AssertAssignment(restoredStaff, "slot_0_1", "SPELL_FIREBALL");

            Assert.That(
                Invoke<bool>(model, "AssignActiveAbility", "DAGGERS", "slot_0_0", "DAGGER_BLINK"),
                Is.True);
            Assert.That(
                Invoke<bool>(model, "AssignPassiveAbility", "DAGGERS", 3, "DAGGER_NEW_PASSIVE"),
                Is.True);
            object daggers = Invoke<object>(model, "FindConfiguration", "DAGGERS");
            AssertAssignment(daggers, "slot_0_0", "DAGGER_BLINK");
            Assert.That(
                StringValues(daggers, "PassiveAbilityIds"),
                Is.EqualTo(new[] { "DAGGER_LIGHTNING_REFLEXES", "DAGGER_NEW_PASSIVE" }));

            object savedDraft = Invoke<object>(model, "ToDraft");
            Assert.That(Get<ulong>(savedDraft, "Revision"), Is.EqualTo(7));
            Assert.That(SelectedDisciplineIds(savedDraft), Is.EqualTo(new[] { "DAGGERS", "STAFF" }));
            Assert.That(
                Values(savedDraft, "DisciplineConfigurations").Count(),
                Is.EqualTo(2),
                "Atomic saves retain selected and dormant configurations in one draft.");
        }

        [Test]
        public void EditorLayout_ExposesCanonicalCardsSchoolsBarsAndPicker()
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
            Assert.That(uxml, Does.Not.Contain("PRIMARY DISCIPLINE"));
            Assert.That(uxml, Does.Not.Contain("SECONDARY DISCIPLINES"));
            Assert.That(uxml, Does.Not.Contain("StatList"));
            Assert.That(uxml, Does.Not.Contain("PointsRemaining"));

            Assert.That(screen, Does.Contain("BuildStaffSchools"));
            Assert.That(screen, Does.Contain("ACTIVE ABILITY ACTION BAR"));
            Assert.That(screen, Does.Contain("PASSIVE ABILITY ACTION BAR"));
            Assert.That(screen, Does.Contain("contract.ActionSlotIds"));
            Assert.That(screen, Does.Contain("BuildAddDisciplineCard"));
            Assert.That(screen, Does.Contain("_hub.SaveCombatBuild(_model.ToDraft())"));
            Assert.That(screen, Does.Not.Contain("SaveWeaponLoadout"));
        }

        [Test]
        public void EditorDisplaysServerValidationCodesVerbatimWithoutOwningACodeMap()
        {
            string server = File.ReadAllText("server/src/combat_build.rs");
            string screen = File.ReadAllText(
                "Assets/Arena/Runtime/UI/Toolkit/DisciplinesScreen.cs");
            string[] serverCodes = Regex.Matches(server, "COMBAT_BUILD_[A-Z_]+")
                .Cast<Match>()
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.That(serverCodes.Length, Is.GreaterThan(20));
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
        public void HubProjectionUsesCanonicalCombatBuildMetadata()
        {
            string network = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string hub = File.ReadAllText("hub-server/src/lib.rs");

            Assert.That(network, Does.Contain("From.HubCombatBuildContractDefinition().ToSql()"));
            Assert.That(network, Does.Contain("From.HubCombatBuildDisciplineDefinition().ToSql()"));
            Assert.That(network, Does.Contain("From.HubSpellSchoolDefinition().ToSql()"));
            Assert.That(network, Does.Contain("From.HubCombatBuildAbilityDefinition().ToSql()"));
            Assert.That(network, Does.Not.Contain("From.HubCombatDisciplineDefinition().ToSql()"));
            Assert.That(network, Does.Not.Contain("From.HubAbilityDefinition().ToSql()"));
            Assert.That(hub, Does.Contain("sync_canonical_combat_build_catalogs"));
            Assert.That(hub, Does.Contain("combat-build-editor-projection-v1"));
        }

        private static object Configuration(
            string disciplineId,
            object weapon,
            string[] schools,
            Array assignments,
            string[] passives)
            => Construct(
                "Arena.Network.HubCombatBuildDisciplineConfiguration",
                disciplineId,
                weapon,
                schools,
                assignments,
                passives);

        private static void AssertAssignment(object configuration, string slot, string abilityId)
        {
            object assignment = Values(configuration, "ActiveAssignments").Single();
            Assert.That(Get<string>(assignment, "ActionSlot"), Is.EqualTo(slot));
            Assert.That(Get<string>(assignment, "AbilityId"), Is.EqualTo(abilityId));
        }

        private static string[] SelectedDisciplineIds(object draft)
            => Values(draft, "SelectedDisciplines")
                .Select(value => Get<string>(value, "CombatDisciplineId"))
                .ToArray();

        private static string[] StringValues(object target, string propertyName)
            => Values(target, propertyName).Cast<string>().ToArray();

        private static object[] Values(object target, string propertyName)
            => ((IEnumerable)Get<object>(target, propertyName)).Cast<object>().ToArray();

        private static T Get<T>(object target, string propertyName)
            => (T)target.GetType().GetProperty(propertyName, InstanceMembers)!.GetValue(target)!;

        private static T Invoke<T>(object target, string methodName, params object?[] arguments)
            => (T)target.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(target, arguments)!;

        private static object Construct(string typeName, params object?[] arguments)
        {
            Type type = RuntimeType(typeName);
            return Activator.CreateInstance(
                type,
                InstanceMembers,
                binder: null,
                args: arguments,
                culture: null)!;
        }

        private static Array RuntimeArray(string typeName, params object[] values)
        {
            Array result = Array.CreateInstance(RuntimeType(typeName), values.Length);
            for (int index = 0; index < values.Length; index++)
                result.SetValue(values[index], index);
            return result;
        }

        private static Type RuntimeType(string typeName)
            => AppDomain.CurrentDomain.Load("Assembly-CSharp")
                .GetType(typeName, throwOnError: true)!;
    }
}
