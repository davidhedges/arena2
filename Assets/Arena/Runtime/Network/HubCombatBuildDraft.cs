#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.HubDb;

namespace Arena.Network
{
    /// <summary>
    /// The Unity-side representation of the canonical Hub combat-build
    /// contract. It is deliberately structural: the Hub remains the only
    /// authority for build validation and budget rules.
    /// </summary>
    internal sealed class HubCombatBuildDraft
    {
        internal HubCombatBuildDraft(
            ulong revision,
            string? startingDisciplineId,
            IEnumerable<HubCombatBuildSelectedDiscipline> selectedDisciplines,
            IEnumerable<HubCombatBuildDisciplineConfiguration> disciplineConfigurations)
        {
            Revision = revision;
            StartingDisciplineId = startingDisciplineId;
            SelectedDisciplines = selectedDisciplines.ToArray();
            DisciplineConfigurations = disciplineConfigurations.ToArray();
        }

        internal ulong Revision { get; }
        internal string? StartingDisciplineId { get; }
        internal IReadOnlyList<HubCombatBuildSelectedDiscipline> SelectedDisciplines { get; }
        internal IReadOnlyList<HubCombatBuildDisciplineConfiguration> DisciplineConfigurations { get; }

        internal static HubCombatBuildDraft FromRow(MyCombatBuild row)
        {
            return new HubCombatBuildDraft(
                row.Revision,
                row.StartingDisciplineId,
                row.SelectedDisciplines.Select(selected =>
                    new HubCombatBuildSelectedDiscipline(
                        selected.SlotIndex,
                        selected.CombatDisciplineId)),
                row.DisciplineConfigurations.Select(configuration =>
                    new HubCombatBuildDisciplineConfiguration(
                        configuration.CombatDisciplineId,
                        new HubCombatBuildWeapon(
                            configuration.Weapon.MainHandItemDefId,
                            configuration.Weapon.MainHandColorId,
                            configuration.Weapon.OffHandItemDefId,
                            configuration.Weapon.OffHandColorId),
                        configuration.StaffSchoolIds,
                        configuration.ActiveAssignments.Select(assignment =>
                            new HubCombatBuildActionAssignment(
                                assignment.ActionSlot,
                                assignment.AbilityId)),
                        configuration.PassiveAbilityIds)));
        }

        internal CombatBuildDraftInput ToReducerInput()
        {
            return new CombatBuildDraftInput(
                Revision,
                StartingDisciplineId,
                SelectedDisciplines.Select(selected =>
                    new CombatBuildSelectedDisciplineInput(
                        selected.SlotIndex,
                        selected.CombatDisciplineId)).ToList(),
                DisciplineConfigurations.Select(configuration =>
                    new CombatBuildDisciplineConfigurationInput(
                        configuration.CombatDisciplineId,
                        new CombatBuildWeaponInput(
                            configuration.Weapon.MainHandItemDefId,
                            configuration.Weapon.MainHandColorId,
                            configuration.Weapon.OffHandItemDefId,
                            configuration.Weapon.OffHandColorId),
                        configuration.StaffSchoolIds.ToList(),
                        configuration.ActiveAssignments.Select(assignment =>
                            new CombatBuildActionAssignmentInput(
                                assignment.ActionSlot,
                                assignment.AbilityId)).ToList(),
                        configuration.PassiveAbilityIds.ToList())).ToList());
        }

        internal HubCombatBuildDisciplineConfiguration? FindConfiguration(string? combatDisciplineId)
        {
            if (string.IsNullOrWhiteSpace(combatDisciplineId))
                return null;

            return DisciplineConfigurations.FirstOrDefault(configuration =>
                string.Equals(
                    configuration.CombatDisciplineId,
                    combatDisciplineId,
                    StringComparison.Ordinal));
        }

        internal HubCombatBuildDraft WithWeapon(
            string combatDisciplineId,
            HubCombatBuildWeapon weapon)
        {
            return new HubCombatBuildDraft(
                Revision,
                StartingDisciplineId,
                SelectedDisciplines,
                DisciplineConfigurations.Select(configuration =>
                    string.Equals(
                        configuration.CombatDisciplineId,
                        combatDisciplineId,
                        StringComparison.Ordinal)
                        ? configuration.WithWeapon(weapon)
                        : configuration));
        }
    }

    internal readonly struct HubCombatBuildSelectedDiscipline
    {
        internal HubCombatBuildSelectedDiscipline(byte slotIndex, string combatDisciplineId)
        {
            SlotIndex = slotIndex;
            CombatDisciplineId = combatDisciplineId;
        }

        internal byte SlotIndex { get; }
        internal string CombatDisciplineId { get; }
    }

    internal sealed class HubCombatBuildDisciplineConfiguration
    {
        internal HubCombatBuildDisciplineConfiguration(
            string combatDisciplineId,
            HubCombatBuildWeapon weapon,
            IEnumerable<string> staffSchoolIds,
            IEnumerable<HubCombatBuildActionAssignment> activeAssignments,
            IEnumerable<string> passiveAbilityIds)
        {
            CombatDisciplineId = combatDisciplineId;
            Weapon = weapon;
            StaffSchoolIds = staffSchoolIds.ToArray();
            ActiveAssignments = activeAssignments.ToArray();
            PassiveAbilityIds = passiveAbilityIds.ToArray();
        }

        internal string CombatDisciplineId { get; }
        internal HubCombatBuildWeapon Weapon { get; }
        internal IReadOnlyList<string> StaffSchoolIds { get; }
        internal IReadOnlyList<HubCombatBuildActionAssignment> ActiveAssignments { get; }
        internal IReadOnlyList<string> PassiveAbilityIds { get; }

        internal HubCombatBuildDisciplineConfiguration WithWeapon(HubCombatBuildWeapon weapon)
        {
            return new HubCombatBuildDisciplineConfiguration(
                CombatDisciplineId,
                weapon,
                StaffSchoolIds,
                ActiveAssignments,
                PassiveAbilityIds);
        }
    }

    internal readonly struct HubCombatBuildWeapon
    {
        internal HubCombatBuildWeapon(
            string mainHandItemDefId,
            string mainHandColorId,
            string offHandItemDefId,
            string offHandColorId)
        {
            MainHandItemDefId = mainHandItemDefId;
            MainHandColorId = mainHandColorId;
            OffHandItemDefId = offHandItemDefId;
            OffHandColorId = offHandColorId;
        }

        internal string MainHandItemDefId { get; }
        internal string MainHandColorId { get; }
        internal string OffHandItemDefId { get; }
        internal string OffHandColorId { get; }
    }

    internal readonly struct HubCombatBuildActionAssignment
    {
        internal HubCombatBuildActionAssignment(string actionSlot, string abilityId)
        {
            ActionSlot = actionSlot;
            AbilityId = abilityId;
        }

        internal string ActionSlot { get; }
        internal string AbilityId { get; }
    }
}
