#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena.Network
{
    internal static class HubCombatBuildSaveStatus
    {
        internal static string Rejected(string reducerFailure)
            => $"SAVE REJECTED — {reducerFailure}";
    }

    /// <summary>
    /// Mutable presentation draft for the Hub combat-build editor. This type
    /// only preserves and reshapes the canonical DTO; it does not validate the
    /// build or own any budget/rule constants.
    /// </summary>
    internal sealed class HubCombatBuildEditorModel
    {
        private readonly List<string> _selectedDisciplineIds;
        private readonly List<EditableDisciplineConfiguration> _configurations;

        internal HubCombatBuildEditorModel(HubCombatBuildDraft source)
        {
            Revision = source.Revision;
            StartingDisciplineId = source.StartingDisciplineId;
            _selectedDisciplineIds = source.SelectedDisciplines
                .OrderBy(selected => selected.SlotIndex)
                .Select(selected => selected.CombatDisciplineId)
                .ToList();
            _configurations = source.DisciplineConfigurations
                .Select(configuration => new EditableDisciplineConfiguration(configuration))
                .ToList();
        }

        internal ulong Revision { get; }
        internal string? StartingDisciplineId { get; private set; }
        internal IReadOnlyList<string> SelectedDisciplineIds => _selectedDisciplineIds;
        internal IReadOnlyList<EditableDisciplineConfiguration> Configurations => _configurations;

        internal int ActiveCount => SelectedConfigurations()
            .Sum(configuration => configuration.ActiveAssignments.Count);

        internal int PassiveCount => SelectedConfigurations()
            .Sum(configuration => configuration.PassiveAbilityIds.Count);

        internal int CombinedAbilityCount => ActiveCount + PassiveCount;

        internal bool IsSelected(string combatDisciplineId)
            => _selectedDisciplineIds.Contains(combatDisciplineId, StringComparer.Ordinal);

        internal EditableDisciplineConfiguration? FindConfiguration(string combatDisciplineId)
            => _configurations.FirstOrDefault(configuration => string.Equals(
                configuration.CombatDisciplineId,
                combatDisciplineId,
                StringComparison.Ordinal));

        internal bool AddDiscipline(HubDisciplineSnapshot definition)
        {
            if (IsSelected(definition.Id))
                return false;

            _selectedDisciplineIds.Add(definition.Id);
            if (FindConfiguration(definition.Id) == null)
            {
                _configurations.Add(new EditableDisciplineConfiguration(
                    definition.Id,
                    definition.StarterWeapon));
            }
            return true;
        }

        internal bool RemoveDiscipline(string combatDisciplineId)
        {
            bool removed = _selectedDisciplineIds.Remove(combatDisciplineId);
            if (!removed)
                return false;

            // Keep the configuration dormant so weapons, schools, exact slots,
            // and passives restore when this discipline is selected again.
            if (string.Equals(
                    StartingDisciplineId,
                    combatDisciplineId,
                    StringComparison.Ordinal))
            {
                StartingDisciplineId = null;
            }
            return true;
        }

        internal void SetStartingDiscipline(string? combatDisciplineId)
        {
            StartingDisciplineId = string.IsNullOrWhiteSpace(combatDisciplineId)
                ? null
                : combatDisciplineId;
        }

        internal bool SetStaffSchoolSelected(string spellSchoolId, bool selected)
        {
            EditableDisciplineConfiguration? staff = FindConfiguration("STAFF");
            if (staff == null)
                return false;

            bool contains = staff.StaffSchoolIds.Contains(spellSchoolId, StringComparer.Ordinal);
            if (selected == contains)
                return false;
            if (selected)
                staff.StaffSchoolIds.Add(spellSchoolId);
            else
                staff.StaffSchoolIds.Remove(spellSchoolId);
            return true;
        }

        internal bool AssignActiveAbility(
            string combatDisciplineId,
            string actionSlot,
            string? abilityId)
        {
            EditableDisciplineConfiguration? target = FindConfiguration(combatDisciplineId);
            if (target == null)
                return false;

            target.ActiveAssignments.RemoveAll(assignment => string.Equals(
                assignment.ActionSlot,
                actionSlot,
                StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(abilityId))
                return true;

            RemoveAbilityEverywhere(abilityId);
            target.ActiveAssignments.Add(new HubCombatBuildActionAssignment(actionSlot, abilityId));
            return true;
        }

        internal bool AssignPassiveAbility(
            string combatDisciplineId,
            int passiveIndex,
            string? abilityId)
        {
            EditableDisciplineConfiguration? target = FindConfiguration(combatDisciplineId);
            if (target == null || passiveIndex < 0)
                return false;

            if (passiveIndex < target.PassiveAbilityIds.Count)
                target.PassiveAbilityIds.RemoveAt(passiveIndex);
            if (string.IsNullOrWhiteSpace(abilityId))
                return true;

            RemoveAbilityEverywhere(abilityId);
            int insertionIndex = Math.Min(passiveIndex, target.PassiveAbilityIds.Count);
            target.PassiveAbilityIds.Insert(insertionIndex, abilityId);
            return true;
        }

        internal bool ContainsAbility(string abilityId, string? exceptAbilityId = null)
        {
            foreach (EditableDisciplineConfiguration configuration in _configurations)
            {
                if (configuration.ActiveAssignments.Any(assignment =>
                        string.Equals(assignment.AbilityId, abilityId, StringComparison.Ordinal)
                        && !string.Equals(assignment.AbilityId, exceptAbilityId, StringComparison.Ordinal)))
                {
                    return true;
                }
                if (configuration.PassiveAbilityIds.Any(selected =>
                        string.Equals(selected, abilityId, StringComparison.Ordinal)
                        && !string.Equals(selected, exceptAbilityId, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            return false;
        }

        internal HubCombatBuildDraft ToDraft()
        {
            return new HubCombatBuildDraft(
                Revision,
                StartingDisciplineId,
                _selectedDisciplineIds.Select((disciplineId, index) =>
                    new HubCombatBuildSelectedDiscipline((byte)index, disciplineId)),
                _configurations.Select(configuration => configuration.ToImmutable()));
        }

        private IEnumerable<EditableDisciplineConfiguration> SelectedConfigurations()
        {
            HashSet<string> selected = new(_selectedDisciplineIds, StringComparer.Ordinal);
            return _configurations.Where(configuration =>
                selected.Contains(configuration.CombatDisciplineId));
        }

        private void RemoveAbilityEverywhere(string abilityId)
        {
            foreach (EditableDisciplineConfiguration configuration in _configurations)
            {
                configuration.ActiveAssignments.RemoveAll(assignment => string.Equals(
                    assignment.AbilityId,
                    abilityId,
                    StringComparison.Ordinal));
                configuration.PassiveAbilityIds.RemoveAll(selected => string.Equals(
                    selected,
                    abilityId,
                    StringComparison.Ordinal));
            }
        }
    }

    internal sealed class EditableDisciplineConfiguration
    {
        internal EditableDisciplineConfiguration(HubCombatBuildDisciplineConfiguration source)
        {
            CombatDisciplineId = source.CombatDisciplineId;
            Weapon = source.Weapon;
            StaffSchoolIds = source.StaffSchoolIds.ToList();
            ActiveAssignments = source.ActiveAssignments.ToList();
            PassiveAbilityIds = source.PassiveAbilityIds.ToList();
        }

        internal EditableDisciplineConfiguration(
            string combatDisciplineId,
            HubCombatBuildWeapon starterWeapon)
        {
            CombatDisciplineId = combatDisciplineId;
            Weapon = starterWeapon;
            StaffSchoolIds = new List<string>();
            ActiveAssignments = new List<HubCombatBuildActionAssignment>();
            PassiveAbilityIds = new List<string>();
        }

        internal string CombatDisciplineId { get; }
        internal HubCombatBuildWeapon Weapon { get; }
        internal List<string> StaffSchoolIds { get; }
        internal List<HubCombatBuildActionAssignment> ActiveAssignments { get; }
        internal List<string> PassiveAbilityIds { get; }

        internal HubCombatBuildDisciplineConfiguration ToImmutable()
        {
            return new HubCombatBuildDisciplineConfiguration(
                CombatDisciplineId,
                Weapon,
                StaffSchoolIds,
                ActiveAssignments,
                PassiveAbilityIds);
        }
    }
}
