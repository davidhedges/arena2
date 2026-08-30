#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena.Network
{
    internal enum CombatSpecializationKindV2
    {
        Form,
        School,
    }

    internal enum CombatFeatureLoadoutKindV2
    {
        Technique,
        Spell,
        Perk,
    }

    internal readonly struct CombatBuildV2SelectedSpecializationModel
    {
        internal CombatBuildV2SelectedSpecializationModel(byte slotIndex, string specializationId)
        {
            SlotIndex = slotIndex;
            SpecializationId = specializationId;
        }

        internal byte SlotIndex { get; }
        internal string SpecializationId { get; }
    }

    internal sealed class CombatBuildV2DisciplineConfigurationModel
    {
        internal CombatBuildV2DisciplineConfigurationModel(
            string combatDisciplineId,
            string mainHandItemDefId,
            string mainHandColorId,
            string offHandItemDefId,
            string offHandColorId)
        {
            CombatDisciplineId = combatDisciplineId;
            MainHandItemDefId = mainHandItemDefId;
            MainHandColorId = mainHandColorId;
            OffHandItemDefId = offHandItemDefId;
            OffHandColorId = offHandColorId;
        }

        internal string CombatDisciplineId { get; }
        internal string MainHandItemDefId { get; }
        internal string MainHandColorId { get; }
        internal string OffHandItemDefId { get; }
        internal string OffHandColorId { get; }
    }

    internal sealed class CombatBuildV2FeatureSelectionModel
    {
        internal CombatBuildV2FeatureSelectionModel(
            string specializationId,
            string abilityId,
            byte? preferredBarOrder)
        {
            SpecializationId = specializationId;
            AbilityId = abilityId;
            PreferredBarOrder = preferredBarOrder;
        }

        internal string SpecializationId { get; }
        internal string AbilityId { get; }
        internal byte? PreferredBarOrder { get; set; }

        internal CombatBuildV2FeatureSelectionModel Clone()
            => new(SpecializationId, AbilityId, PreferredBarOrder);
    }

    /// <summary>
    /// Transport-neutral mirror of the v2 whole-build aggregate. Generated
    /// SpacetimeDB rows are converted at the rehearsal boundary and never leak
    /// into editor or HUD models.
    /// </summary>
    internal sealed class CombatBuildV2DraftModel
    {
        internal CombatBuildV2DraftModel(
            uint schemaVersion,
            ulong revision,
            string? startingDisciplineId,
            IEnumerable<CombatBuildV2SelectedSpecializationModel> selectedSpecializations,
            IEnumerable<string> dormantSpecializations,
            IEnumerable<CombatBuildV2DisciplineConfigurationModel> disciplineConfigurations,
            IEnumerable<CombatBuildV2FeatureSelectionModel> selectedFeatures,
            IEnumerable<string> selectedTraits)
        {
            SchemaVersion = schemaVersion;
            Revision = revision;
            StartingDisciplineId = startingDisciplineId;
            SelectedSpecializations = selectedSpecializations
                .OrderBy(row => row.SlotIndex)
                .ToArray();
            DormantSpecializations = dormantSpecializations.ToArray();
            DisciplineConfigurations = disciplineConfigurations.ToArray();
            SelectedFeatures = selectedFeatures.Select(row => row.Clone()).ToArray();
            SelectedTraits = selectedTraits.ToArray();
        }

        internal uint SchemaVersion { get; }
        internal ulong Revision { get; }
        internal string? StartingDisciplineId { get; }
        internal IReadOnlyList<CombatBuildV2SelectedSpecializationModel> SelectedSpecializations { get; }
        internal IReadOnlyList<string> DormantSpecializations { get; }
        internal IReadOnlyList<CombatBuildV2DisciplineConfigurationModel> DisciplineConfigurations { get; }
        internal IReadOnlyList<CombatBuildV2FeatureSelectionModel> SelectedFeatures { get; }
        internal IReadOnlyList<string> SelectedTraits { get; }

        internal CombatBuildV2DisciplineConfigurationModel? FindDisciplineConfiguration(
            string? combatDisciplineId)
        {
            if (string.IsNullOrWhiteSpace(combatDisciplineId))
                return null;

            return DisciplineConfigurations.FirstOrDefault(row => string.Equals(
                row.CombatDisciplineId,
                combatDisciplineId,
                StringComparison.Ordinal));
        }

        internal CombatBuildV2DraftModel WithDisciplineConfiguration(
            CombatBuildV2DisciplineConfigurationModel replacement)
        {
            bool replaced = DisciplineConfigurations.Any(row => string.Equals(
                row.CombatDisciplineId,
                replacement.CombatDisciplineId,
                StringComparison.Ordinal));
            IEnumerable<CombatBuildV2DisciplineConfigurationModel> configurations =
                DisciplineConfigurations.Select(row => string.Equals(
                    row.CombatDisciplineId,
                    replacement.CombatDisciplineId,
                    StringComparison.Ordinal)
                    ? replacement
                    : row);
            if (!replaced)
                configurations = configurations.Append(replacement);

            return new CombatBuildV2DraftModel(
                SchemaVersion,
                Revision,
                StartingDisciplineId,
                SelectedSpecializations,
                DormantSpecializations,
                configurations,
                SelectedFeatures,
                SelectedTraits);
        }
    }

    internal sealed class CombatBuildV2ContractModel
    {
        internal CombatBuildV2ContractModel(
            uint schemaVersion,
            int minimumSelectedSpecializations,
            int maximumSelectedSpecializations,
            int globalFeatureCapacity,
            int traitCapacity,
            IEnumerable<string> directActionInputIds)
        {
            SchemaVersion = schemaVersion;
            MinimumSelectedSpecializations = minimumSelectedSpecializations;
            MaximumSelectedSpecializations = maximumSelectedSpecializations;
            GlobalFeatureCapacity = globalFeatureCapacity;
            TraitCapacity = traitCapacity;
            DirectActionInputIds = directActionInputIds.ToArray();
        }

        internal uint SchemaVersion { get; }
        internal int MinimumSelectedSpecializations { get; }
        internal int MaximumSelectedSpecializations { get; }
        internal int GlobalFeatureCapacity { get; }
        internal int TraitCapacity { get; }
        internal IReadOnlyList<string> DirectActionInputIds { get; }
    }

    internal sealed class CombatSpecializationDefinitionV2Model
    {
        internal CombatSpecializationDefinitionV2Model(
            string specializationId,
            string combatDisciplineId,
            CombatSpecializationKindV2 specializationKind,
            string displayName,
            uint sortOrder)
        {
            SpecializationId = specializationId;
            CombatDisciplineId = combatDisciplineId;
            SpecializationKind = specializationKind;
            DisplayName = displayName;
            SortOrder = sortOrder;
        }

        internal string SpecializationId { get; }
        internal string CombatDisciplineId { get; }
        internal CombatSpecializationKindV2 SpecializationKind { get; }
        internal string DisplayName { get; }
        internal uint SortOrder { get; }
    }

    internal sealed class CombatFeatureDefinitionV2Model
    {
        internal CombatFeatureDefinitionV2Model(
            string abilityId,
            string specializationId,
            string combatDisciplineId,
            CombatFeatureLoadoutKindV2 loadoutKind,
            string displayName,
            string resourceKind,
            float resourceCost,
            uint sortOrder)
        {
            AbilityId = abilityId;
            SpecializationId = specializationId;
            CombatDisciplineId = combatDisciplineId;
            LoadoutKind = loadoutKind;
            DisplayName = displayName;
            ResourceKind = resourceKind;
            ResourceCost = resourceCost;
            SortOrder = sortOrder;
        }

        internal string AbilityId { get; }
        internal string SpecializationId { get; }
        internal string CombatDisciplineId { get; }
        internal CombatFeatureLoadoutKindV2 LoadoutKind { get; }
        internal string DisplayName { get; }
        internal string ResourceKind { get; }
        internal float ResourceCost { get; }
        internal uint SortOrder { get; }
        internal bool IsActive
            => LoadoutKind == CombatFeatureLoadoutKindV2.Technique
               || LoadoutKind == CombatFeatureLoadoutKindV2.Spell;
    }

    internal sealed class CombatTraitDefinitionV2Model
    {
        internal CombatTraitDefinitionV2Model(
            string abilityId,
            string displayName,
            float modifierScalar,
            uint sortOrder)
        {
            AbilityId = abilityId;
            DisplayName = displayName;
            ModifierScalar = modifierScalar;
            SortOrder = sortOrder;
        }

        internal string AbilityId { get; }
        internal string DisplayName { get; }
        internal float ModifierScalar { get; }
        internal uint SortOrder { get; }
    }

    internal sealed class CombatBuildV2CatalogModel
    {
        private readonly Dictionary<string, CombatSpecializationDefinitionV2Model> _specializations;
        private readonly Dictionary<string, CombatFeatureDefinitionV2Model> _features;
        private readonly Dictionary<string, CombatTraitDefinitionV2Model> _traits;

        internal CombatBuildV2CatalogModel(
            IEnumerable<CombatSpecializationDefinitionV2Model> specializations,
            IEnumerable<CombatFeatureDefinitionV2Model> features,
            IEnumerable<CombatTraitDefinitionV2Model> traits)
        {
            _specializations = specializations.ToDictionary(
                row => row.SpecializationId,
                StringComparer.Ordinal);
            _features = features.ToDictionary(row => row.AbilityId, StringComparer.Ordinal);
            _traits = traits.ToDictionary(row => row.AbilityId, StringComparer.Ordinal);
        }

        internal IReadOnlyCollection<CombatSpecializationDefinitionV2Model> Specializations
            => _specializations.Values;
        internal IReadOnlyCollection<CombatFeatureDefinitionV2Model> Features => _features.Values;
        internal IReadOnlyCollection<CombatTraitDefinitionV2Model> Traits => _traits.Values;

        internal CombatSpecializationDefinitionV2Model? FindSpecialization(string specializationId)
            => _specializations.TryGetValue(specializationId, out var row) ? row : null;

        internal CombatFeatureDefinitionV2Model? FindFeature(string abilityId)
            => _features.TryGetValue(abilityId, out var row) ? row : null;

        internal CombatTraitDefinitionV2Model? FindTrait(string abilityId)
            => _traits.TryGetValue(abilityId, out var row) ? row : null;

        internal IReadOnlyList<CombatFeatureDefinitionV2Model> FeaturesFor(
            string specializationId,
            CombatFeatureLoadoutKindV2? kind = null)
            => _features.Values
                .Where(row => string.Equals(
                    row.SpecializationId,
                    specializationId,
                    StringComparison.Ordinal))
                .Where(row => !kind.HasValue || row.LoadoutKind == kind.Value)
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                .ToArray();
    }

    internal readonly struct CombatBuildV2InputBindingModel
    {
        internal CombatBuildV2InputBindingModel(
            string inputActionId,
            string defaultKeyCode,
            string defaultLabel)
        {
            InputActionId = inputActionId;
            DefaultKeyCode = defaultKeyCode;
            DefaultLabel = defaultLabel;
        }

        internal string InputActionId { get; }
        internal string DefaultKeyCode { get; }
        internal string DefaultLabel { get; }
    }

    /// <summary>The owner-approved Phase 0 direct-access contract.</summary>
    internal static class CombatBuildV2InputContract
    {
        internal static readonly IReadOnlyList<CombatBuildV2InputBindingModel> Bindings =
            new[]
            {
                Binding(0, "Alpha1", "1"), Binding(1, "Alpha2", "2"),
                Binding(2, "Alpha3", "3"), Binding(3, "Alpha4", "4"),
                Binding(4, "Alpha5", "5"), Binding(5, "Alpha6", "6"),
                Binding(6, "Alpha7", "7"), Binding(7, "Alpha8", "8"),
                Binding(8, "Alpha9", "9"), Binding(9, "Alpha0", "0"),
                Binding(10, "E", "E"), Binding(11, "R", "R"),
                Binding(12, "T", "T"), Binding(13, "F", "F"),
                Binding(14, "G", "G"), Binding(15, "Z", "Z"),
                Binding(16, "X", "X"), Binding(17, "C", "C"),
            };

        private static CombatBuildV2InputBindingModel Binding(
            int index,
            string keyCode,
            string label)
            => new($"COMBAT_ACTION_{index:00}", keyCode, label);
    }

    internal readonly struct CombatBuildV2SaveResult
    {
        private CombatBuildV2SaveResult(bool committed, string serverError)
        {
            Committed = committed;
            ServerError = serverError;
        }

        internal bool Committed { get; }
        internal string ServerError { get; }
        internal string DisplayText => ServerError;

        internal static CombatBuildV2SaveResult Accepted() => new(true, string.Empty);
        internal static CombatBuildV2SaveResult Rejected(string serverError)
            => new(false, serverError ?? string.Empty);
    }

    internal static class HubCombatBuildSaveStatus
    {
        internal static string Rejected(string reducerFailure)
            => $"SAVE REJECTED — {reducerFailure}";
    }
}
