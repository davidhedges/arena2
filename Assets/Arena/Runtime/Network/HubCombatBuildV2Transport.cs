#nullable enable

using System;
using System.Linq;

namespace Arena.Network
{
    /// <summary>
    /// The only boundary where canonical Hub binding types enter the
    /// transport-neutral Combat Build v2 editor model.
    /// </summary>
    internal static class HubCombatBuildV2Transport
    {
        internal static CombatBuildV2DraftModel FromGenerated(Arena.HubDb.MyCombatBuildV2 row)
            => new(
                row.SchemaVersion,
                row.Revision,
                row.StartingDisciplineId,
                row.SelectedSpecializations.Select(selected =>
                    new CombatBuildV2SelectedSpecializationModel(
                        selected.SlotIndex,
                        selected.SpecializationId)),
                row.DormantSpecializations,
                row.DisciplineConfigurations.Select(configuration =>
                    new CombatBuildV2DisciplineConfigurationModel(
                        configuration.CombatDisciplineId,
                        configuration.MainHandItemDefId,
                        configuration.MainHandColorId,
                        configuration.OffHandItemDefId,
                        configuration.OffHandColorId)),
                row.SelectedFeatures.Select(feature =>
                    new CombatBuildV2FeatureSelectionModel(
                        feature.SpecializationId,
                        feature.AbilityId,
                        feature.PreferredBarOrder)),
                row.SelectedTraits);

        internal static CombatBuildV2ContractModel FromGenerated(
            Arena.HubDb.CombatBuildV2ContractDefinition row)
            => new(
                row.SchemaVersion,
                checked((int)row.MinimumSelectedSpecializations),
                checked((int)row.MaximumSelectedSpecializations),
                checked((int)row.GlobalFeatureCapacity),
                checked((int)row.TraitCapacity),
                row.DirectActionInputIds);

        internal static CombatBuildV2CatalogModel FromGenerated(Arena.HubDb.DbConnection conn)
            => new(
                conn.Db.CombatSpecializationDefinitionV2.Iter().Select(row =>
                    new CombatSpecializationDefinitionV2Model(
                        row.SpecializationId,
                        row.CombatDisciplineId,
                        ParseSpecializationKind(row.SpecializationKind),
                        row.DisplayName,
                        row.SortOrder)),
                conn.Db.CombatFeatureDefinitionV2.Iter().Select(row =>
                    new CombatFeatureDefinitionV2Model(
                        row.AbilityId,
                        row.SpecializationId,
                        row.CombatDisciplineId,
                        ParseLoadoutKind(row.LoadoutKind),
                        row.DisplayName,
                        row.ResourceKind,
                        row.ResourceCost,
                        row.SortOrder)),
                conn.Db.CombatTraitDefinitionV2.Iter().Select(row =>
                    new CombatTraitDefinitionV2Model(
                        row.AbilityId,
                        row.DisplayName,
                        row.ModifierScalar,
                        row.SortOrder)));

        internal static Arena.HubDb.CombatBuildV2DraftInput ToGenerated(
            CombatBuildV2DraftModel draft)
            => new(
                draft.SchemaVersion,
                draft.Revision,
                draft.StartingDisciplineId,
                draft.SelectedSpecializations.Select(row =>
                    new Arena.HubDb.SelectedSpecializationV2Input(
                        row.SlotIndex,
                        row.SpecializationId)).ToList(),
                draft.DormantSpecializations.ToList(),
                draft.DisciplineConfigurations.Select(row =>
                    new Arena.HubDb.DisciplineConfigurationV2Input(
                        row.CombatDisciplineId,
                        row.MainHandItemDefId,
                        row.MainHandColorId,
                        row.OffHandItemDefId,
                        row.OffHandColorId)).ToList(),
                draft.SelectedFeatures.Select(row =>
                    new Arena.HubDb.CombatFeatureSelectionV2Input(
                        row.SpecializationId,
                        row.AbilityId,
                        row.PreferredBarOrder)).ToList(),
                draft.SelectedTraits.ToList());

        private static CombatSpecializationKindV2 ParseSpecializationKind(string value)
            => value switch
            {
                "FORM" => CombatSpecializationKindV2.Form,
                "SCHOOL" => CombatSpecializationKindV2.School,
                _ => throw new InvalidOperationException(
                    $"Unknown Combat Build v2 Specialization kind '{value}'."),
            };

        private static CombatFeatureLoadoutKindV2 ParseLoadoutKind(string value)
            => value switch
            {
                "TECHNIQUE" => CombatFeatureLoadoutKindV2.Technique,
                "SPELL" => CombatFeatureLoadoutKindV2.Spell,
                "PERK" => CombatFeatureLoadoutKindV2.Perk,
                _ => throw new InvalidOperationException(
                    $"Unknown Combat Build v2 Feature kind '{value}'."),
            };
    }
}
