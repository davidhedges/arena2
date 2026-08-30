#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Network;

namespace Arena.UI
{
    internal sealed class CombatBuildV2SpecializationCardModel
    {
        internal CombatBuildV2SpecializationCardModel(
            byte slotIndex,
            CombatSpecializationDefinitionV2Model definition,
            IReadOnlyList<CombatFeatureDefinitionV2Model> selectedFeatures,
            IReadOnlyList<CombatFeatureDefinitionV2Model> pickerOptions)
        {
            SlotIndex = slotIndex;
            Definition = definition;
            SelectedFeatures = selectedFeatures;
            PickerOptions = pickerOptions;
        }

        internal byte SlotIndex { get; }
        internal CombatSpecializationDefinitionV2Model Definition { get; }
        internal IReadOnlyList<CombatFeatureDefinitionV2Model> SelectedFeatures { get; }
        internal IReadOnlyList<CombatFeatureDefinitionV2Model> PickerOptions { get; }
        internal bool IsEmpty => SelectedFeatures.Count == 0;
        internal string KindLabel
            => Definition.SpecializationKind == CombatSpecializationKindV2.School
                ? "SCHOOL"
                : "FORM";
    }

    internal sealed class CombatBuildV2EditorViewModel
    {
        internal CombatBuildV2EditorViewModel(
            CombatBuildV2EditorModel editor,
            CombatBuildV2CatalogModel catalog)
        {
            Editor = editor;
            Catalog = catalog;
        }

        internal CombatBuildV2EditorModel Editor { get; }
        internal CombatBuildV2CatalogModel Catalog { get; }
        internal string FeatureCapacityText => Editor.FeatureCapacityText;
        internal string TraitCapacityText => Editor.TraitCapacityText;
        internal bool SaveEnabled => Editor.CanSubmit;
        internal IReadOnlyList<string> ValidationMessages => Editor.LocalSubmissionIssues();

        internal IReadOnlyList<CombatBuildV2SpecializationCardModel> Cards()
        {
            CombatBuildV2DraftModel draft = Editor.ToDraft();
            var selectedFeatureIds = new HashSet<string>(
                draft.SelectedFeatures.Select(row => row.AbilityId),
                StringComparer.Ordinal);
            return draft.SelectedSpecializations.Select(selected =>
            {
                CombatSpecializationDefinitionV2Model definition =
                    Catalog.FindSpecialization(selected.SpecializationId)
                    ?? throw new InvalidOperationException(
                        $"Missing v2 Specialization '{selected.SpecializationId}'.");
                IReadOnlyList<CombatFeatureDefinitionV2Model> options =
                    Catalog.FeaturesFor(selected.SpecializationId);
                return new CombatBuildV2SpecializationCardModel(
                    selected.SlotIndex,
                    definition,
                    options.Where(row => selectedFeatureIds.Contains(row.AbilityId)).ToArray(),
                    options);
            }).ToArray();
        }

        internal IReadOnlyList<CombatTraitDefinitionV2Model> TraitOptions()
            => Catalog.Traits
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                .ToArray();
    }

    internal sealed class CombatBuildV2BarSlotModel
    {
        internal CombatBuildV2BarSlotModel(
            int displayOrder,
            int globalInputOrder,
            CombatFeatureDefinitionV2Model feature,
            CombatBuildV2InputBindingModel binding)
        {
            DisplayOrder = displayOrder;
            GlobalInputOrder = globalInputOrder;
            Feature = feature;
            InputActionId = binding.InputActionId;
            KeyLabel = binding.DefaultLabel;
        }

        internal int DisplayOrder { get; }
        internal int GlobalInputOrder { get; }
        internal CombatFeatureDefinitionV2Model Feature { get; }
        internal string InputActionId { get; }
        internal string KeyLabel { get; }
    }

    /// <summary>
    /// Read-only dual-bar projection. The Spell bar is stable across weapon
    /// switches; only the current non-Staff parent's Technique projection is
    /// shown. Both are filtered views of one direct-input ordering.
    /// </summary>
    internal sealed class CombatBuildV2HudModel
    {
        private CombatBuildV2HudModel(
            string activeDisciplineId,
            IReadOnlyList<string> switchTargets,
            IReadOnlyList<CombatBuildV2BarSlotModel> spells,
            IReadOnlyList<CombatBuildV2BarSlotModel> techniques,
            IReadOnlyList<string> activePerks)
        {
            ActiveDisciplineId = activeDisciplineId;
            SwitchTargets = switchTargets;
            SpellSlots = spells;
            TechniqueSlots = techniques;
            ActivePerkAbilityIds = activePerks;
        }

        internal string ActiveDisciplineId { get; }
        internal IReadOnlyList<string> SwitchTargets { get; }
        internal IReadOnlyList<CombatBuildV2BarSlotModel> SpellSlots { get; }
        internal IReadOnlyList<CombatBuildV2BarSlotModel> TechniqueSlots { get; }
        internal IReadOnlyList<string> ActivePerkAbilityIds { get; }
        internal bool SpellBarVisible => true;
        internal bool TechniqueBarVisible
            => !string.Equals(ActiveDisciplineId, "STAFF", StringComparison.Ordinal);

        internal static CombatBuildV2HudModel Create(
            CombatBuildV2DraftModel draft,
            CombatBuildV2CatalogModel catalog,
            CombatBuildV2ContractModel contract,
            string activeDisciplineId)
        {
            var selectedSlots = draft.SelectedSpecializations
                .OrderBy(row => row.SlotIndex)
                .ToArray();
            var selectedIds = new HashSet<string>(
                selectedSlots.Select(row => row.SpecializationId),
                StringComparer.Ordinal);
            var slotRank = selectedSlots.ToDictionary(
                row => row.SpecializationId,
                row => (int)row.SlotIndex,
                StringComparer.Ordinal);

            var switchTargets = new List<string>();
            foreach (CombatBuildV2SelectedSpecializationModel selected in selectedSlots)
            {
                string? parent = catalog.FindSpecialization(selected.SpecializationId)
                    ?.CombatDisciplineId;
                if (!string.IsNullOrWhiteSpace(parent)
                    && !switchTargets.Contains(parent!, StringComparer.Ordinal))
                {
                    switchTargets.Add(parent!);
                }
            }
            if (!switchTargets.Contains(activeDisciplineId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Active Discipline '{activeDisciplineId}' is not selected.");
            }

            var active = draft.SelectedFeatures
                .Select((selection, sourceIndex) => new
                {
                    Selection = selection,
                    SourceIndex = sourceIndex,
                    Definition = catalog.FindFeature(selection.AbilityId),
                })
                .Where(row => selectedIds.Contains(row.Selection.SpecializationId))
                .Where(row => row.Definition?.IsActive == true)
                .OrderBy(row => row.Selection.PreferredBarOrder ?? byte.MaxValue)
                .ThenBy(row => slotRank[row.Selection.SpecializationId])
                .ThenBy(row => row.Definition!.LoadoutKind == CombatFeatureLoadoutKindV2.Spell ? 0 : 1)
                .ThenBy(row => row.Definition!.SortOrder)
                .ThenBy(row => row.SourceIndex)
                .ToArray();
            if (active.Length > contract.DirectActionInputIds.Count
                || active.Length > CombatBuildV2InputContract.Bindings.Count)
            {
                throw new InvalidOperationException(
                    "Selected active features exceed the reviewed direct-input contract.");
            }

            var bound = active.Select((row, globalInputOrder) => new
            {
                row.Definition,
                GlobalInputOrder = globalInputOrder,
                Binding = ResolveBinding(contract.DirectActionInputIds[globalInputOrder]),
            }).ToArray();
            CombatBuildV2BarSlotModel[] spells = bound
                .Where(row => row.Definition!.LoadoutKind == CombatFeatureLoadoutKindV2.Spell)
                .Select((row, displayOrder) => new CombatBuildV2BarSlotModel(
                    displayOrder,
                    row.GlobalInputOrder,
                    row.Definition!,
                    row.Binding))
                .ToArray();
            CombatBuildV2BarSlotModel[] techniques = string.Equals(
                    activeDisciplineId,
                    "STAFF",
                    StringComparison.Ordinal)
                ? Array.Empty<CombatBuildV2BarSlotModel>()
                : bound
                    .Where(row => row.Definition!.LoadoutKind
                        == CombatFeatureLoadoutKindV2.Technique)
                    .Where(row => string.Equals(
                        row.Definition!.CombatDisciplineId,
                        activeDisciplineId,
                        StringComparison.Ordinal))
                    .Select((row, displayOrder) => new CombatBuildV2BarSlotModel(
                        displayOrder,
                        row.GlobalInputOrder,
                        row.Definition!,
                        row.Binding))
                    .ToArray();
            string[] perks = draft.SelectedFeatures
                .Where(row => selectedIds.Contains(row.SpecializationId))
                .Select(row => catalog.FindFeature(row.AbilityId))
                .Where(row => row?.LoadoutKind == CombatFeatureLoadoutKindV2.Perk)
                .OrderBy(row => slotRank[row!.SpecializationId])
                .ThenBy(row => row!.SortOrder)
                .Select(row => row!.AbilityId)
                .ToArray();

            return new CombatBuildV2HudModel(
                activeDisciplineId,
                switchTargets,
                spells,
                techniques,
                perks);
        }

        private static CombatBuildV2InputBindingModel ResolveBinding(string inputActionId)
        {
            foreach (CombatBuildV2InputBindingModel binding in CombatBuildV2InputContract.Bindings)
            {
                if (string.Equals(binding.InputActionId, inputActionId, StringComparison.Ordinal))
                    return binding;
            }
            throw new InvalidOperationException(
                $"Unknown direct input identity '{inputActionId}'.");
        }
    }
}
