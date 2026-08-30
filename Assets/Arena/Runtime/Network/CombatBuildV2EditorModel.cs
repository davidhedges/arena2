#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena.Network
{
    internal sealed class CombatBuildV2EditorModel
    {
        private readonly CombatBuildV2CatalogModel _catalog;
        private readonly CombatBuildV2ContractModel _contract;
        private readonly List<string> _selectedSpecializationIds;
        private readonly HashSet<string> _dormantSpecializationIds;
        private readonly Dictionary<string, CombatBuildV2DisciplineConfigurationModel> _configurations;
        private readonly List<CombatBuildV2FeatureSelectionModel> _features;
        private readonly List<string> _traits;

        internal CombatBuildV2EditorModel(
            CombatBuildV2DraftModel source,
            CombatBuildV2CatalogModel catalog,
            CombatBuildV2ContractModel contract)
        {
            _catalog = catalog;
            _contract = contract;
            SchemaVersion = source.SchemaVersion;
            Revision = source.Revision;
            StartingDisciplineId = source.StartingDisciplineId;
            _selectedSpecializationIds = source.SelectedSpecializations
                .OrderBy(row => row.SlotIndex)
                .Select(row => row.SpecializationId)
                .ToList();
            _dormantSpecializationIds = new HashSet<string>(
                source.DormantSpecializations,
                StringComparer.Ordinal);
            _configurations = source.DisciplineConfigurations.ToDictionary(
                row => row.CombatDisciplineId,
                StringComparer.Ordinal);
            _features = source.SelectedFeatures.Select(row => row.Clone()).ToList();
            _traits = source.SelectedTraits.ToList();
        }

        internal uint SchemaVersion { get; }
        internal ulong Revision { get; }
        internal string? StartingDisciplineId { get; private set; }
        internal IReadOnlyList<string> SelectedSpecializationIds => _selectedSpecializationIds;
        internal IReadOnlyCollection<string> DormantSpecializationIds => _dormantSpecializationIds;
        internal IReadOnlyList<string> SelectedTraitIds => _traits;

        internal int SelectedFeatureCount => _features.Count(row => IsSourceSelected(row.SpecializationId));
        internal int SelectedActiveCount => _features.Count(row =>
            IsSourceSelected(row.SpecializationId)
            && _catalog.FindFeature(row.AbilityId)?.IsActive == true);
        internal int SelectedPerkCount => _features.Count(row =>
            IsSourceSelected(row.SpecializationId)
            && _catalog.FindFeature(row.AbilityId)?.LoadoutKind == CombatFeatureLoadoutKindV2.Perk);
        internal int TraitCount => _traits.Count;
        internal int FeatureCapacityRemaining
            => Math.Max(0, _contract.GlobalFeatureCapacity - SelectedFeatureCount);
        internal int TraitCapacityRemaining
            => Math.Max(0, _contract.TraitCapacity - TraitCount);
        internal string FeatureCapacityText
            => $"{SelectedFeatureCount} / {_contract.GlobalFeatureCapacity} FEATURES";
        internal string TraitCapacityText
            => $"{TraitCount} / {_contract.TraitCapacity} TRAITS";

        internal bool MasteryActive
            => _traits.Contains("MASTERY", StringComparer.Ordinal)
               && DerivedParentDisciplineIds().Count == 1;

        internal bool AddSpecialization(string specializationId)
        {
            if (_selectedSpecializationIds.Count >= _contract.MaximumSelectedSpecializations
                || _selectedSpecializationIds.Contains(specializationId, StringComparer.Ordinal)
                || _catalog.FindSpecialization(specializationId) == null)
            {
                return false;
            }

            _selectedSpecializationIds.Add(specializationId);
            _dormantSpecializationIds.Remove(specializationId);
            ReflowAllActiveScopes(specializationId);
            if (string.IsNullOrWhiteSpace(StartingDisciplineId))
            {
                StartingDisciplineId = _catalog.FindSpecialization(specializationId)!
                    .CombatDisciplineId;
            }
            return true;
        }

        internal bool RemoveSpecialization(string specializationId)
        {
            if (!_selectedSpecializationIds.Remove(specializationId))
                return false;

            _dormantSpecializationIds.Add(specializationId);
            string? removedParent = _catalog.FindSpecialization(specializationId)?.CombatDisciplineId;
            if (!string.IsNullOrWhiteSpace(removedParent)
                && string.Equals(StartingDisciplineId, removedParent, StringComparison.Ordinal)
                && !DerivedParentDisciplineIds().Contains(removedParent!, StringComparer.Ordinal))
            {
                StartingDisciplineId = DerivedParentDisciplineIds().FirstOrDefault();
            }
            ReflowAllActiveScopes(returningSpecializationId: null);
            return true;
        }

        internal void SetStartingDiscipline(string? combatDisciplineId)
        {
            StartingDisciplineId = string.IsNullOrWhiteSpace(combatDisciplineId)
                ? null
                : combatDisciplineId;
        }

        internal bool SetDisciplineConfiguration(
            CombatBuildV2DisciplineConfigurationModel configuration)
        {
            if (!AllRetainedParentDisciplineIds().Contains(
                    configuration.CombatDisciplineId,
                    StringComparer.Ordinal))
            {
                return false;
            }
            _configurations[configuration.CombatDisciplineId] = configuration;
            return true;
        }

        internal CombatBuildV2DisciplineConfigurationModel? FindDisciplineConfiguration(
            string combatDisciplineId)
            => _configurations.TryGetValue(combatDisciplineId, out var row) ? row : null;

        internal IReadOnlyList<CombatSpecializationDefinitionV2Model> SpecializationPickerOptions()
            => _catalog.Specializations
                .Where(row => !_selectedSpecializationIds.Contains(
                    row.SpecializationId,
                    StringComparer.Ordinal))
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.SpecializationId, StringComparer.Ordinal)
                .ToArray();

        internal IReadOnlyList<CombatFeatureDefinitionV2Model> FeaturePickerOptions(
            string specializationId,
            CombatFeatureLoadoutKindV2? kind = null)
            => _catalog.FeaturesFor(specializationId, kind);

        internal bool IsFeatureSelected(string abilityId)
            => _features.Any(row => string.Equals(row.AbilityId, abilityId, StringComparison.Ordinal));

        internal bool SetFeatureSelected(string abilityId, bool selected)
        {
            CombatFeatureDefinitionV2Model? definition = _catalog.FindFeature(abilityId);
            if (definition == null
                || (!IsSourceSelected(definition.SpecializationId)
                    && !_dormantSpecializationIds.Contains(definition.SpecializationId)))
            {
                return false;
            }

            CombatBuildV2FeatureSelectionModel? existing = _features.FirstOrDefault(row =>
                string.Equals(row.AbilityId, abilityId, StringComparison.Ordinal));
            if (selected == (existing != null))
                return false;

            if (!selected)
            {
                _features.Remove(existing!);
                ReflowAllActiveScopes(returningSpecializationId: null);
                return true;
            }

            if (IsSourceSelected(definition.SpecializationId)
                && SelectedFeatureCount >= _contract.GlobalFeatureCapacity)
            {
                return false;
            }

            byte? preferredOrder = definition.IsActive
                ? NextPreferredOrder(definition)
                : null;
            _features.Add(new CombatBuildV2FeatureSelectionModel(
                definition.SpecializationId,
                definition.AbilityId,
                preferredOrder));
            ReflowAllActiveScopes(returningSpecializationId: null);
            return true;
        }

        internal bool SetTraitSelected(string abilityId, bool selected)
        {
            if (_catalog.FindTrait(abilityId) == null)
                return false;
            bool contains = _traits.Contains(abilityId, StringComparer.Ordinal);
            if (contains == selected)
                return false;
            if (selected)
            {
                if (_traits.Count >= _contract.TraitCapacity)
                    return false;
                _traits.Add(abilityId);
            }
            else
            {
                _traits.Remove(abilityId);
            }
            return true;
        }

        internal bool MoveActiveFeature(string abilityId, int destinationIndex)
        {
            CombatBuildV2FeatureSelectionModel? target = _features.FirstOrDefault(row =>
                string.Equals(row.AbilityId, abilityId, StringComparison.Ordinal));
            CombatFeatureDefinitionV2Model? definition = target == null
                ? null
                : _catalog.FindFeature(target.AbilityId);
            if (target == null || definition?.IsActive != true || !IsSourceSelected(target.SpecializationId))
                return false;

            List<CombatBuildV2FeatureSelectionModel> scope = ActiveSelectionsInScope(definition)
                .OrderBy(row => row.PreferredBarOrder ?? byte.MaxValue)
                .ThenBy(row => _features.IndexOf(row))
                .ToList();
            scope.Remove(target);
            scope.Insert(Math.Max(0, Math.Min(destinationIndex, scope.Count)), target);
            for (int index = 0; index < scope.Count; index++)
                scope[index].PreferredBarOrder = checked((byte)index);
            return true;
        }

        internal IReadOnlyList<string> DerivedParentDisciplineIds()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return _selectedSpecializationIds
                .Select(id => _catalog.FindSpecialization(id)?.CombatDisciplineId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id!))
                .Cast<string>()
                .ToArray();
        }

        internal IReadOnlyList<string> SelectedPerkAbilityIds()
            => _features
                .Where(row => IsSourceSelected(row.SpecializationId))
                .Where(row => _catalog.FindFeature(row.AbilityId)?.LoadoutKind
                    == CombatFeatureLoadoutKindV2.Perk)
                .Select(row => row.AbilityId)
                .ToArray();

        internal IReadOnlyList<string> LocalSubmissionIssues()
        {
            var issues = new List<string>();
            if (_selectedSpecializationIds.Count < _contract.MinimumSelectedSpecializations
                || _selectedSpecializationIds.Count > _contract.MaximumSelectedSpecializations)
            {
                issues.Add("Select one to three Forms or Schools.");
            }
            foreach (string specializationId in _selectedSpecializationIds)
            {
                if (!_features.Any(row => string.Equals(
                        row.SpecializationId,
                        specializationId,
                        StringComparison.Ordinal)))
                {
                    issues.Add($"{specializationId} must select at least one feature.");
                }
            }
            if (SelectedFeatureCount > _contract.GlobalFeatureCapacity)
                issues.Add("The global feature capacity is exceeded.");
            if (_traits.Count > _contract.TraitCapacity)
                issues.Add("The Trait capacity is exceeded.");
            foreach (string parentId in DerivedParentDisciplineIds())
            {
                if (!_configurations.ContainsKey(parentId))
                    issues.Add($"{parentId} needs a weapon configuration.");
            }
            if (!string.IsNullOrWhiteSpace(StartingDisciplineId)
                && !DerivedParentDisciplineIds().Contains(
                    StartingDisciplineId!,
                    StringComparer.Ordinal))
            {
                issues.Add("The starting Discipline must be selected.");
            }
            return issues;
        }

        internal bool CanSubmit => LocalSubmissionIssues().Count == 0;

        internal CombatBuildV2DraftModel ToDraft()
        {
            ReflowAllActiveScopes(returningSpecializationId: null);
            var retainedParents = new HashSet<string>(
                AllRetainedParentDisciplineIds(),
                StringComparer.Ordinal);
            return new CombatBuildV2DraftModel(
                SchemaVersion,
                Revision,
                StartingDisciplineId,
                _selectedSpecializationIds.Select((id, index) =>
                    new CombatBuildV2SelectedSpecializationModel(checked((byte)index), id)),
                _dormantSpecializationIds.OrderBy(id => id, StringComparer.Ordinal),
                _configurations.Values
                    .Where(row => retainedParents.Contains(row.CombatDisciplineId))
                    .OrderBy(row => row.CombatDisciplineId, StringComparer.Ordinal),
                _features.OrderBy(row => row.SpecializationId, StringComparer.Ordinal)
                    .ThenBy(row => row.AbilityId, StringComparer.Ordinal),
                _traits.OrderBy(id => _catalog.FindTrait(id)?.SortOrder ?? uint.MaxValue)
                    .ThenBy(id => id, StringComparer.Ordinal));
        }

        private bool IsSourceSelected(string specializationId)
            => _selectedSpecializationIds.Contains(specializationId, StringComparer.Ordinal);

        private IReadOnlyList<string> AllRetainedParentDisciplineIds()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return _selectedSpecializationIds
                .Concat(_dormantSpecializationIds.OrderBy(id => id, StringComparer.Ordinal))
                .Select(id => _catalog.FindSpecialization(id)?.CombatDisciplineId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id!))
                .Cast<string>()
                .ToArray();
        }

        private byte NextPreferredOrder(CombatFeatureDefinitionV2Model definition)
        {
            int next = ActiveSelectionsInScope(definition)
                .Select(row => (int)(row.PreferredBarOrder ?? 0))
                .DefaultIfEmpty(-1)
                .Max() + 1;
            return checked((byte)next);
        }

        private IEnumerable<CombatBuildV2FeatureSelectionModel> ActiveSelectionsInScope(
            CombatFeatureDefinitionV2Model definition)
        {
            foreach (CombatBuildV2FeatureSelectionModel row in _features)
            {
                if (!IsSourceSelected(row.SpecializationId))
                    continue;
                CombatFeatureDefinitionV2Model? candidate = _catalog.FindFeature(row.AbilityId);
                if (candidate == null || candidate.LoadoutKind != definition.LoadoutKind)
                    continue;
                if (definition.LoadoutKind == CombatFeatureLoadoutKindV2.Spell
                    || (definition.LoadoutKind == CombatFeatureLoadoutKindV2.Technique
                        && string.Equals(
                            candidate.CombatDisciplineId,
                            definition.CombatDisciplineId,
                            StringComparison.Ordinal)))
                {
                    yield return row;
                }
            }
        }

        private void ReflowAllActiveScopes(string? returningSpecializationId)
        {
            var scopeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CombatBuildV2FeatureSelectionModel row in _features)
            {
                if (!IsSourceSelected(row.SpecializationId))
                    continue;
                CombatFeatureDefinitionV2Model? definition = _catalog.FindFeature(row.AbilityId);
                if (definition?.IsActive != true)
                    continue;
                scopeIds.Add(definition.LoadoutKind == CombatFeatureLoadoutKindV2.Spell
                    ? "SPELL"
                    : $"TECHNIQUE:{definition.CombatDisciplineId}");
            }

            foreach (string scopeId in scopeIds)
            {
                List<CombatBuildV2FeatureSelectionModel> rows = _features
                    .Where(row => IsSourceSelected(row.SpecializationId))
                    .Where(row => ScopeId(row.AbilityId) == scopeId)
                    .OrderBy(row => row.PreferredBarOrder ?? byte.MaxValue)
                    .ThenBy(row => string.Equals(
                        row.SpecializationId,
                        returningSpecializationId,
                        StringComparison.Ordinal) ? 1 : 0)
                    .ThenBy(row => _selectedSpecializationIds.IndexOf(row.SpecializationId))
                    .ThenBy(row => _catalog.FindFeature(row.AbilityId)?.SortOrder ?? uint.MaxValue)
                    .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                    .ToList();
                for (int index = 0; index < rows.Count; index++)
                    rows[index].PreferredBarOrder = checked((byte)index);
            }
        }

        private string? ScopeId(string abilityId)
        {
            CombatFeatureDefinitionV2Model? definition = _catalog.FindFeature(abilityId);
            if (definition?.LoadoutKind == CombatFeatureLoadoutKindV2.Spell)
                return "SPELL";
            if (definition?.LoadoutKind == CombatFeatureLoadoutKindV2.Technique)
                return $"TECHNIQUE:{definition.CombatDisciplineId}";
            return null;
        }
    }
}
