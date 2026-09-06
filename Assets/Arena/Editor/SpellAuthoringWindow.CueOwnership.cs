#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal sealed partial class SpellAuthoringWindow
    {
        private readonly Dictionary<string, GlobalCueWritePlan> _globalCuePreviewPlans = new(StringComparer.Ordinal);
        internal sealed class GlobalCueWritePlan
        {
            public readonly List<SpellCueRow> Rows = new();
            public readonly List<string> Errors = new();
            public readonly List<string> Drift = new();
        }

        // A global catalog row must have the same generated meaning in GLOBAL and every equipped
        // profile. The editor's selected preview profile is never an input to materialization.
        internal GlobalCueWritePlan BuildGlobalCueWritePlan(string abilityId)
        {
            if (_catalog == null) Load();
            EnsureAnimationSetsLoaded();
            var plan = new GlobalCueWritePlan();
            var abilities = _spellAbilities.Where(a => Normalize(a.ability_id) == Normalize(abilityId)).ToArray();
            if (abilities.Length != 1)
            {
                plan.Errors.Add(abilityId + ": generated cues require exactly one spell-executor ability.");
                return plan;
            }
            SpellPresentationEditorData.LoadCombatVfxDisciplineUsage(out var disciplines, out string warning);
            if (warning.Length > 0) plan.Errors.Add(warning);
            if (disciplines.Count == 0) plan.Errors.Add("No catalog disciplines resolved for VFX context coverage.");
            foreach (var discipline in disciplines)
                if (_animationSets.Count(set => set.CombatProfileIdOrDefault == discipline.DisciplineId) != 1)
                    plan.Errors.Add("Expected exactly one animation set for " + discipline.DisciplineId + ".");
            if (plan.Errors.Count > 0) return plan;

            var ability = abilities[0];
            var contexts = new List<(string Profile, List<GeneratedCue> Cues)>();
            try
            {
                foreach (CombatAnimationSet? set in _animationSets.Cast<CombatAnimationSet?>().Prepend(null))
                {
                    _generatedCuePreviewByAbilityId.Clear();
                    var mode = SpellAnimationArchetypes.Derive((ulong)Math.Max(0, ability.gameplay.cast_time_ms), ability.gameplay.delivery.kind);
                    bool resolved = SpellCastAnimationResolver.TryResolve(set, ability.action_id, mode, out var animation);
                    contexts.Add((set == null ? "GLOBAL" : set.CombatProfileIdOrDefault,
                        GetOrBuildGeneratedCuePreview(ability, abilityId, resolved, animation).Cues));
                }
            }
            finally { _generatedCuePreviewByAbilityId.Clear(); }

            var authored = _catalog!.combat_vfx_cues.Where(c => Normalize(c.owner_kind) == "ABILITY"
                && Normalize(c.owner_id) == Normalize(abilityId)).ToArray();
            foreach (var cue in authored.Where(c => c.authoring_mode == SpellCueCatalogWriter.Generated))
            {
                string label = abilityId + "/" + cue.slot;
                if (!TryNormalizeExplicitSlotKey(cue.slot, out string slot))
                {
                    plan.Errors.Add(label + ": invalid explicit generator slot.");
                    continue;
                }
                if (authored.Count(other => TryResolveCatalogSlotKey(other, out string key) && key == slot) != 1)
                {
                    plan.Errors.Add(label + ": ambiguous ABILITY slot identity.");
                    continue;
                }
                SpellCueRow? first = null;
                int errorsBefore = plan.Errors.Count;
                foreach (var context in contexts)
                {
                    var matches = context.Cues.Where(c => c.SlotKey == slot).ToArray();
                    if (matches.Length != 1)
                    {
                        plan.Errors.Add(label + ": " + context.Profile + " must generate exactly one candidate.");
                        continue;
                    }
                    var row = ToCueRow(matches[0], cue.sort_order);
                    if (first.HasValue)
                        plan.Errors.AddRange(SpellCueCatalogWriter.CompareGeneratedRows(first.Value, row)
                            .Select(diff => label + ": equipment-dependent generation in " + context.Profile + " (" + diff + ")."));
                    else
                    {
                        first = row;
                        plan.Drift.AddRange(DiffFields(matches[0], cue).Select(diff => label + ": " + diff));
                    }
                }
                if (first.HasValue && !CombatVFXTemplateRegistry.CanResolveTemplate(first.Value.VfxId))
                    plan.Errors.Add(label + ": generated VFX ID has no runtime template: " + first.Value.VfxId);
                if (first.HasValue && plan.Errors.Count == errorsBefore) plan.Rows.Add(first.Value);
            }
            return plan;
        }

        private static SpellCueRow ToCueRow(GeneratedCue cue, int sortOrder) => new(
            cue.SlotKey, cue.Trigger, cue.Anchor, cue.VfxId, cue.AttachMode, cue.Role,
            cue.Lifecycle, cue.ProjectileSequenceIndex, cue.DurationMs, sortOrder);

        internal List<string> CheckGeneratedCueOwnership(out int checkedCues)
        {
            if (_catalog == null) Load();
            var errors = SpellCueCatalogWriter.ValidateOwnership(_loadedCatalogJson);
            var generated = _catalog!.combat_vfx_cues.Where(c => c.authoring_mode == SpellCueCatalogWriter.Generated).ToArray();
            checkedCues = generated.Length;
            foreach (string owner in generated.Select(c => Normalize(c.owner_id)).Distinct())
            {
                var plan = BuildGlobalCueWritePlan(owner);
                errors.AddRange(plan.Errors);
                errors.AddRange(plan.Drift);
            }
            return errors;
        }

        internal List<string> ValidateCueWriteRequest(string expectedCatalog, string abilityId, IReadOnlyList<SpellCueRow> requested)
        {
            Load(); // Resolve fresh authoring inputs; a cached preview cannot authorize a write.
            var errors = new List<string>();
            if (_loadedCatalogJson != expectedCatalog)
            {
                errors.Add("The catalog differs from the current authoring source. Reload before writing.");
                return errors;
            }
            var plan = BuildGlobalCueWritePlan(abilityId);
            errors.AddRange(plan.Errors);
            var orders = new HashSet<int>();
            foreach (var row in requested)
            {
                if (!orders.Add(row.SortOrder)) errors.Add("Duplicate requested sort_order: " + row.SortOrder);
                var matches = plan.Rows.Where(candidate => candidate.SortOrder == row.SortOrder).ToArray();
                if (matches.Length != 1)
                    errors.Add($"{abilityId}/{row.Slot}: no validated generated-owned row. Declare ownership before materialization.");
                else
                    errors.AddRange(SpellCueCatalogWriter.CompareGeneratedRows(matches[0], row)
                        .Select(diff => abilityId + "/" + row.Slot + ": preview is stale or differs from current global generation (" + diff + ")."));
            }
            return errors;
        }

        internal void CaptureVfxOwnership(CombatAuthoringVerification.Report report)
        {
            if (_catalog == null) Load();
            foreach (var cue in _catalog!.combat_vfx_cues)
                report.cueOwnership.Add(new CombatAuthoringVerification.CueOwnershipRow
                {
                    ownerKind = cue.owner_kind, ownerId = cue.owner_id, slot = cue.slot, mode = cue.authoring_mode,
                    reason = cue.authoring_reason, vfxId = cue.vfx_id, sortOrder = cue.sort_order,
                });
            foreach (string id in _catalog.combat_vfx_cues.Select(c => Normalize(c.vfx_id)).Distinct().OrderBy(id => id, StringComparer.Ordinal))
            {
                var binding = new CombatAuthoringVerification.VfxBindingRow { vfxId = id };
                if (CombatVFXTemplateRegistry.IsScriptedTemplate(id)) binding.source = "SCRIPTED";
                else
                {
                    var template = CombatVFXTemplateRegistry.ResolveTemplate(id);
                    if (template?.Prefab != null)
                    {
                        binding.source = "REGISTRY";
                        binding.scale = template.Scale;
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(template.Prefab, out binding.prefabGuid, out binding.prefabFileId);
                    }
                    else
                    {
                        binding.source = "UNRESOLVED";
                        report.unresolvedVfxIds.Add(id);
                    }
                }
                report.vfxBindings.Add(binding);
            }
        }
    }
}
