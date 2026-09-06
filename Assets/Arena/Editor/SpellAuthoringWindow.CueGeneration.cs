#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Arena.Combat;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    // Wires the pure SpellVfxGenerator core (Assets/Arena/Runtime/Presentation/VFX/SpellVfxGenerator.cs,
    // design doc Appendix B / decision 10) into the authoring window: read the selected spell's gameplay
    // into SpellDeliveryFacts, derive the archetype + animation mode, fill each requested slot's look
    // (vfx_id + duration) from SchoolVfxSet assets (+ per-spell signature overrides), and diff
    // the generated cues against the authored combat_vfx_cues rows.
    internal sealed partial class SpellAuthoringWindow
    {
        /// <summary>The look a palette supplies for one slot: <c>vfx_id</c> and (for ONE_SHOT/DURATION slots)
        /// the concrete duration. <see cref="SelfTerminating"/> maps to a <c>PARTICLE_SYSTEM</c> lifecycle
        /// (design doc §3.1 — the only palette influence on lifecycle).</summary>
        private readonly struct PaletteEntry
        {
            public PaletteEntry(
                string vfxId,
                bool selfTerminating = false,
                int durationMs = 0,
                string variantId = "")
            {
                VfxId = vfxId;
                SelfTerminating = selfTerminating;
                DurationMs = durationMs;
                VariantId = variantId;
            }

            public string VfxId { get; }
            public bool SelfTerminating { get; }
            public int DurationMs { get; }
            public string VariantId { get; }
        }

        private enum CueMatchState
        {
            Match,
            Changed,
            GeneratorOnly,
            CatalogOnly,
        }

        /// <summary>A concrete cue row the generator materialises from a <see cref="CueWiring"/> + a
        /// <see cref="PaletteEntry"/> — the same shape as a catalog <c>combat_vfx_cues</c> row, plus the
        /// author-time <see cref="SpellVfxSlot"/> key it was generated for.</summary>
        private readonly struct GeneratedCue
        {
            public GeneratedCue(
                SpellVfxSlot slot,
                string slotKey,
                string trigger,
                string anchor,
                string attachMode,
                string role,
                string lifecycle,
                int durationMs,
                string vfxId,
                int? projectileSequenceIndex)
            {
                Slot = slot;
                SlotKey = slotKey;
                Trigger = trigger;
                Anchor = anchor;
                AttachMode = attachMode;
                Role = role;
                Lifecycle = lifecycle;
                DurationMs = durationMs;
                VfxId = vfxId;
                ProjectileSequenceIndex = projectileSequenceIndex;
            }

            public SpellVfxSlot Slot { get; }
            public string SlotKey { get; }
            public string Trigger { get; }
            public string Anchor { get; }
            public string AttachMode { get; }
            public string Role { get; }
            public string Lifecycle { get; }
            public int DurationMs { get; }
            public string VfxId { get; }
            public int? ProjectileSequenceIndex { get; }
        }

        private sealed class GeneratedCuePreview
        {
            public GeneratedCuePreview(
                string deliveryKind,
                SpellVfxArchetype? archetype,
                SpellAnimationArchetype mode,
                string school,
                string castHandAnchor,
                bool missingOverrideCatalog,
                List<GeneratedCue> cues,
                List<string> slotNotes)
            {
                DeliveryKind = deliveryKind;
                Archetype = archetype;
                Mode = mode;
                School = school;
                CastHandAnchor = castHandAnchor;
                MissingOverrideCatalog = missingOverrideCatalog;
                Cues = cues;
                SlotNotes = slotNotes;
            }

            public string DeliveryKind { get; }
            public SpellVfxArchetype? Archetype { get; }
            public SpellAnimationArchetype Mode { get; }
            public string School { get; }
            public string CastHandAnchor { get; }
            public bool MissingOverrideCatalog { get; }
            public List<GeneratedCue> Cues { get; }
            public List<string> SlotNotes { get; }
        }

        private readonly Dictionary<string, GeneratedCuePreview> _generatedCuePreviewByAbilityId =
            new(System.StringComparer.Ordinal);
        private Dictionary<string, Dictionary<SpellVfxSlot, List<PaletteEntry>>> _cachedSchoolPalettes =
            new(System.StringComparer.Ordinal);
        private SpellVfxOverrideCatalog? _cachedSpellOverrides;
        private bool _vfxAuthoringAssetsLoaded;

        private void DrawGeneratedCuePreview(
            AbilityDefinition selected,
            string abilityId,
            bool hasResolvedAnimation,
            WeaponSpellAnimationEntry resolvedAnimation)
        {
            EditorGUILayout.LabelField("Generated Cues (SpellVfxGenerator)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Archetype-derived cues from SpellVfxGenerator + SchoolVfxSet assets, diffed against "
                + "the runtime combat_vfx_cues rows above. This comparison does not save changes. "
                + "Regeneration updates only explicitly GENERATED ABILITY rows, using all animation "
                + "contexts. MANUAL and LEGACY rows retain their authored fields. Generated-only slots "
                + "are proposals: declare ownership in the catalog before materializing them.",
                MessageType.Info);

            GeneratedCuePreview preview = GetOrBuildGeneratedCuePreview(
                selected, abilityId, hasResolvedAnimation, resolvedAnimation);

            EditorGUILayout.LabelField(
                "Derivation",
                $"delivery={NoneIfEmpty(preview.DeliveryKind)} | archetype={(preview.Archetype?.ToString() ?? "<none>")} "
                + $"| mode={preview.Mode} | school={NoneIfEmpty(preview.School)} | hand={preview.CastHandAnchor}");

            if (preview.Archetype == null)
            {
                EditorGUILayout.HelpBox(
                    $"delivery.kind '{NoneIfEmpty(preview.DeliveryKind)}' has no VFX archetype, so the generator emits nothing for this spell.",
                    MessageType.Warning);
                return;
            }

            if (preview.MissingOverrideCatalog)
            {
                EditorGUILayout.HelpBox(
                    "No SpellVfxOverrideCatalog asset found. School-derived slots can still preview, but bespoke spell slot looks are unavailable.",
                    MessageType.Error);
            }

            if (string.IsNullOrEmpty(preview.School))
            {
                EditorGUILayout.HelpBox(
                    "No school resolved (vfx_school and damage_type are both unset). The generator can still wire "
                    + "slots, but no palette fills their vfx_ids — set a school or add signature overrides.",
                    MessageType.Warning);
            }

            foreach (string note in preview.SlotNotes)
                EditorGUILayout.HelpBox(note, MessageType.Warning);

            BuildCatalogBySlot(
                out Dictionary<string, CombatVfxCueDefinition> catalogBySlot,
                out List<string> ambiguousSlots,
                out List<CombatVfxCueDefinition> uninferrableCues);
            DrawCueDiff(preview.Cues, catalogBySlot, ambiguousSlots, uninferrableCues);
            DrawWriteToCatalogButton(abilityId);
        }

        private GeneratedCuePreview GetOrBuildGeneratedCuePreview(
            AbilityDefinition selected,
            string abilityId,
            bool hasResolvedAnimation,
            WeaponSpellAnimationEntry resolvedAnimation)
        {
            if (_generatedCuePreviewByAbilityId.TryGetValue(abilityId, out GeneratedCuePreview cached))
                return cached;

            EnsureVfxAuthoringAssetsLoaded();
            string deliveryKind = Normalize(selected.gameplay.delivery.kind);
            SpellDeliveryFacts facts = BuildDeliveryFacts(selected);
            SpellVfxArchetype? archetype = SpellVfxGenerator.DeriveArchetype(facts);
            SpellAnimationArchetype mode = SpellAnimationArchetypes.Derive(
                (ulong)Mathf.Max(0, selected.gameplay.cast_time_ms), deliveryKind);
            string school = ResolveSchool(selected.gameplay.delivery);
            string castHandAnchor = ResolveCastHandAnchor(
                hasResolvedAnimation, resolvedAnimation);

            List<GeneratedCue> cues;
            List<string> notes;
            if (archetype.HasValue)
            {
                cues = GenerateCues(
                    archetype.Value,
                    mode,
                    facts,
                    _cachedSchoolPalettes,
                    _cachedSpellOverrides,
                    abilityId,
                    school,
                    castHandAnchor,
                    out notes);
            }
            else
            {
                cues = new List<GeneratedCue>();
                notes = new List<string>();
            }

            var preview = new GeneratedCuePreview(
                deliveryKind,
                archetype,
                mode,
                school,
                castHandAnchor,
                _cachedSpellOverrides == null,
                cues,
                notes);
            _generatedCuePreviewByAbilityId.Add(abilityId, preview);
            return preview;
        }

        private void EnsureVfxAuthoringAssetsLoaded()
        {
            if (_vfxAuthoringAssetsLoaded)
                return;

            _cachedSchoolPalettes = LoadSchoolPalettesFromAssets();
            _cachedSpellOverrides = SpellPresentationEditorData.FindFirstAsset<SpellVfxOverrideCatalog>();
            _vfxAuthoringAssetsLoaded = true;
        }

        private void InvalidateGeneratedCueCache()
        {
            _generatedCuePreviewByAbilityId.Clear();
            _globalCuePreviewPlans.Clear();
            _cachedSchoolPalettes.Clear();
            _cachedSpellOverrides = null;
            _vfxAuthoringAssetsLoaded = false;
        }

        private SpellDeliveryFacts BuildDeliveryFacts(AbilityDefinition selected)
        {
            DeliveryDefinition delivery = selected.gameplay.delivery;
            string kind = Normalize(delivery.kind);
            string targeting = Normalize(selected.gameplay.targeting);

            // Presence is guarded by a positive/non-empty proxy field so it is robust whether or not
            // JsonUtility instantiates an absent nested object (design doc B.9 read boundary).
            bool hasSkyOrigin = delivery.sky_origin != null && delivery.sky_origin.height > 0f;
            bool firesProjectiles = string.Equals(kind, "CHANNEL", System.StringComparison.Ordinal)
                && delivery.projectile != null && delivery.projectile.speed > 0f;
            bool deferredByCone = delivery.shape != null
                && string.Equals(Normalize(delivery.shape.kind), "CASTER_CONE", System.StringComparison.Ordinal);
            bool deferred = delivery.impact_delay_ms > 0 || deferredByCone;

            return new SpellDeliveryFacts(
                kind: kind,
                targeting: targeting,
                hasSkyOrigin: hasSkyOrigin,
                firesProjectiles: firesProjectiles,
                deferred: deferred);
        }

        // SCHOOL = vfx_school ?? damage_type ?? profile_default (design doc §2.3). vfx_school is a true
        // override; damage_type is the free default. profile_default is not modelled here yet (no spell in
        // scope needs it) — an unresolved school surfaces a warning rather than guessing.
        private static string ResolveSchool(DeliveryDefinition delivery)
        {
            string vfxSchool = Normalize(delivery.vfx_school);
            if (!string.IsNullOrEmpty(vfxSchool))
                return vfxSchool;
            return Normalize(delivery.damage_type);
        }

        private static string ResolveCastHandAnchor(
            bool hasResolvedAnimation,
            WeaponSpellAnimationEntry resolvedAnimation)
        {
            // The resolved animation owns origin and mirroring, with gesture/clip
            // inference for legacy recipes. A profile-less spell retains LEFT_HAND.
            if (hasResolvedAnimation
                && TryInferSpellPresentationHand(resolvedAnimation, out string handAnchor, out _))
            {
                return handAnchor;
            }

            return SpellVfxGenerator.AnchorToCatalog(CueAnchor.Hand);
        }

        private static List<GeneratedCue> GenerateCues(
            SpellVfxArchetype archetype,
            SpellAnimationArchetype mode,
            SpellDeliveryFacts facts,
            Dictionary<string, Dictionary<SpellVfxSlot, List<PaletteEntry>>> schoolPalettes,
            SpellVfxOverrideCatalog? spellOverrides,
            string abilityId,
            string school,
            string castHandAnchor,
            out List<string> slotNotes)
        {
            slotNotes = new List<string>();
            var rows = new List<GeneratedCue>();

            var requestedSlots = new List<SpellVfxSlot>(SpellVfxGenerator.RequestedSlots(archetype, mode));

            foreach (SpellVfxSlot slot in requestedSlots)
            {
                IReadOnlyList<PaletteEntry> entries = ResolvePaletteEntries(
                    schoolPalettes, spellOverrides, school, abilityId, slot);
                if (entries.Count == 0)
                {
                    if (!IsOptionalSlot(archetype, slot))
                    {
                        slotNotes.Add(
                            $"Slot '{slot}' is requested by the {archetype} archetype but neither the {NoneIfEmpty(school)} "
                            + $"school palette nor a {abilityId} signature override provides a vfx_id — the generator omits it.");
                    }
                    continue;
                }

                bool repeatable = slot == SpellVfxSlot.CharacterFx;
                if (!repeatable && entries.Count > 1)
                {
                    slotNotes.Add(
                        $"Slot '{slot}' resolves {entries.Count} palette entries, but only CharacterFx is repeatable. "
                        + "The generator uses the first entry; remove the duplicate authoring.");
                }

                int entryCount = repeatable ? entries.Count : 1;
                var emittedSlotKeys = new HashSet<string>(System.StringComparer.Ordinal);
                for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
                {
                    PaletteEntry entry = entries[entryIndex];
                    if (!TryBuildGeneratedSlotKey(slot, entry.VariantId, entryCount, out string slotKey, out string keyError))
                    {
                        slotNotes.Add($"Slot '{slot}' vfx_id '{entry.VfxId}' cannot be generated: {keyError}");
                        continue;
                    }

                    if (!emittedSlotKeys.Add(slotKey))
                    {
                        slotNotes.Add(
                            $"Slot '{slot}' uses duplicate variant identity '{slotKey}'. CharacterFx variantId values must be unique per spell.");
                        continue;
                    }

                    CueWiring wiring = SpellVfxGenerator.Wire(archetype, slot, mode, entry.SelfTerminating, facts.Deferred);
                    string anchor = wiring.Anchor == CueAnchor.Hand
                        ? castHandAnchor
                        : SpellVfxGenerator.AnchorToCatalog(wiring.Anchor);
                    int durationMs = 0;
                    if (wiring.Duration == CueDurationPolicy.PalettePositive)
                    {
                        if (entry.DurationMs <= 0)
                        {
                            slotNotes.Add(
                                $"Slot '{slotKey}' resolves vfx_id '{entry.VfxId}' but requires a positive duration_ms because it is not self-terminating; set the slot duration above 0.");
                            continue;
                        }

                        durationMs = entry.DurationMs;
                    }

                    rows.Add(new GeneratedCue(
                        slot: slot,
                        slotKey: slotKey,
                        trigger: wiring.Trigger,
                        anchor: anchor,
                        attachMode: wiring.AttachMode,
                        role: wiring.VfxRole,
                        lifecycle: wiring.Lifecycle,
                        durationMs: durationMs,
                        vfxId: entry.VfxId,
                        projectileSequenceIndex: wiring.ProjectileSequenceIndex));
                }
            }

            if (archetype == SpellVfxArchetype.Emanation
                && !rows.Exists(row => row.Slot == SpellVfxSlot.PersistentField
                    || row.Slot == SpellVfxSlot.PersistentCharacterFx))
            {
                slotNotes.Add(
                    "The Emanation archetype requires either PersistentField or PersistentCharacterFx, "
                    + "but neither the school palette nor the per-spell override provides one.");
            }

            return rows;
        }

        private static bool IsOptionalSlot(SpellVfxArchetype archetype, SpellVfxSlot slot)
            => slot == SpellVfxSlot.Muzzle
                || slot == SpellVfxSlot.ProjectileTrail
                || slot == SpellVfxSlot.CharacterFx
                || slot == SpellVfxSlot.SelfFlash
                || slot == SpellVfxSlot.TargetAttachment
                || slot == SpellVfxSlot.StatusAttachment
                || (archetype == SpellVfxArchetype.Emanation
                    && (slot == SpellVfxSlot.PersistentField
                        || slot == SpellVfxSlot.PersistentCharacterFx))
                || slot == SpellVfxSlot.MaxStackCharacterFx;

        private static bool TryBuildGeneratedSlotKey(
            SpellVfxSlot slot,
            string variantId,
            int entryCount,
            out string slotKey,
            out string error)
        {
            if (slot != SpellVfxSlot.CharacterFx)
            {
                slotKey = SlotKey(slot);
                error = string.Empty;
                return true;
            }

            // Variant display names and their authored identifiers share one slot key.
            string variant = NormalizeSlotVariant(variantId);
            if (variant.Length == 0)
            {
                if (entryCount == 1)
                {
                    slotKey = SlotKey(slot);
                    error = string.Empty;
                    return true;
                }

                slotKey = string.Empty;
                error = "multiple CharacterFx entries require distinct variantId values so their slot identities remain stable";
                return false;
            }

            slotKey = $"{SlotKey(slot)}/{variant.ToLowerInvariant()}";
            error = string.Empty;
            return true;
        }

        private static Dictionary<string, Dictionary<SpellVfxSlot, List<PaletteEntry>>> LoadSchoolPalettesFromAssets()
        {
            var map = new Dictionary<string, Dictionary<SpellVfxSlot, List<PaletteEntry>>>(System.StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:SchoolVfxSet"))
            {
                var set = AssetDatabase.LoadAssetAtPath<SchoolVfxSet>(AssetDatabase.GUIDToAssetPath(guid));
                if (set == null || set.SchoolIdOrEmpty.Length == 0) continue;

                if (!map.TryGetValue(set.SchoolIdOrEmpty, out Dictionary<SpellVfxSlot, List<PaletteEntry>> slotMap))
                    map[set.SchoolIdOrEmpty] = slotMap = new Dictionary<SpellVfxSlot, List<PaletteEntry>>();
                foreach (SchoolVfxSlotEntry e in set.Slots)
                {
                    if (!string.IsNullOrWhiteSpace(e.vfxId))
                    {
                        if (!slotMap.TryGetValue(e.slot, out List<PaletteEntry> entries))
                            slotMap[e.slot] = entries = new List<PaletteEntry>();
                        entries.Add(new PaletteEntry(e.vfxId, e.selfTerminating, e.durationMs, e.variantId));
                    }
                }
            }

            return map;
        }

        private static IReadOnlyList<PaletteEntry> ResolvePaletteEntries(
            Dictionary<string, Dictionary<SpellVfxSlot, List<PaletteEntry>>> schoolPalettes,
            SpellVfxOverrideCatalog? spellOverrides,
            string school,
            string abilityId,
            SpellVfxSlot slot)
        {
            if (spellOverrides != null
                && spellOverrides.TryGet(abilityId, out SpellVfxAbilityOverride spellOverride))
            {
                var overrides = new List<PaletteEntry>();
                foreach (SchoolVfxSlotEntry overrideEntry in spellOverride.Slots)
                {
                    if (overrideEntry.slot != slot || string.IsNullOrWhiteSpace(overrideEntry.vfxId))
                        continue;
                    overrides.Add(new PaletteEntry(
                        overrideEntry.vfxId,
                        overrideEntry.selfTerminating,
                        overrideEntry.durationMs,
                        overrideEntry.variantId));
                }

                if (overrides.Count > 0)
                    return overrides;
            }

            if (!string.IsNullOrEmpty(school))
            {
                if (schoolPalettes.TryGetValue(school, out Dictionary<SpellVfxSlot, List<PaletteEntry>> palette)
                    && palette.TryGetValue(slot, out List<PaletteEntry> entries))
                {
                    return entries;
                }
            }

            return System.Array.Empty<PaletteEntry>();
        }

        // Assign every authored cue a slot via the design-doc §3.4 legacy inference table. The slot is
        // the stable identity across the two stores (the diff and the writer both key on it): matching
        // is exact by slot, ambiguous/uninferrable rows are surfaced rather than silently dropped.
        private void BuildCatalogBySlot(
            out Dictionary<string, CombatVfxCueDefinition> catalogBySlot,
            out List<string> ambiguous,
            out List<CombatVfxCueDefinition> uninferrable)
        {
            catalogBySlot = new Dictionary<string, CombatVfxCueDefinition>(System.StringComparer.Ordinal);
            ambiguous = new List<string>();
            uninferrable = new List<CombatVfxCueDefinition>();
            foreach (CombatVfxCueDefinition cue in _selectedAbilityCues)
            {
                if (cue.authoring_mode == SpellCueCatalogWriter.Legacy) continue;
                if (!TryResolveCatalogSlotKey(cue, out string slotKey))
                {
                    uninferrable.Add(cue);
                    continue;
                }

                if (catalogBySlot.ContainsKey(slotKey))
                {
                    if (!ambiguous.Contains(slotKey))
                        ambiguous.Add(slotKey);
                    continue;
                }

                catalogBySlot.Add(slotKey, cue);
            }
        }

        private void DrawCueDiff(
            List<GeneratedCue> generated,
            Dictionary<string, CombatVfxCueDefinition> catalogBySlot,
            List<string> ambiguous,
            List<CombatVfxCueDefinition> uninferrable)
        {
            var generatedBySlot = generated.ToDictionary(row => row.SlotKey, System.StringComparer.Ordinal);
            IEnumerable<string> allSlots = generatedBySlot.Keys
                .Union(catalogBySlot.Keys)
                .OrderBy(slot => slot, System.StringComparer.Ordinal);

            int matches = 0;
            int changed = 0;
            int generatorOnly = 0;
            int catalogOnly = 0;

            foreach (string slot in allSlots)
            {
                bool hasGenerated = generatedBySlot.TryGetValue(slot, out GeneratedCue gen);
                bool hasCatalog = catalogBySlot.TryGetValue(slot, out CombatVfxCueDefinition cat);

                if (hasGenerated && hasCatalog)
                {
                    List<string> diffs = DiffFields(gen, cat);
                    if (diffs.Count == 0)
                    {
                        matches++;
                        EditorGUILayout.LabelField($"[{slot}] MATCH", DescribeGenerated(gen));
                    }
                    else
                    {
                        changed++;
                        EditorGUILayout.LabelField($"[{slot}] CHANGED", DescribeGenerated(gen));
                        EditorGUILayout.HelpBox(
                            $"Generated vs authored differs:\n - {string.Join("\n - ", diffs)}",
                            MessageType.Warning);
                    }
                }
                else if (hasGenerated)
                {
                    generatorOnly++;
                    EditorGUILayout.LabelField($"[{slot}] GENERATOR ADDS", DescribeGenerated(gen));
                    EditorGUILayout.HelpBox(
                        "The generator would emit this cue, but no authored cue infers to this slot.",
                        MessageType.Warning);
                }
                else
                {
                    catalogOnly++;
                    EditorGUILayout.LabelField($"[{slot}] CATALOG ONLY", DescribeCatalog(cat));
                    EditorGUILayout.HelpBox(
                        "An authored cue infers to this slot, but the generator emits nothing for it (its slot "
                        + "is not requested, or the palette opts out).",
                        MessageType.Warning);
                }
            }

            foreach (string slot in ambiguous)
            {
                EditorGUILayout.HelpBox(
                    $"Two or more authored cues infer to slot '{slot}'. §3.4 refuses to auto-key ambiguous rows — "
                    + "author an explicit slot key to resolve.",
                    MessageType.Error);
            }

            foreach (CombatVfxCueDefinition cue in uninferrable)
            {
                EditorGUILayout.HelpBox(
                    $"Authored cue could not be assigned a slot by the §3.4 inference table: {DescribeCatalog(cue)}",
                    MessageType.Error);
            }

            string summary = $"{generated.Count} generated, {catalogBySlot.Count} authored (inferred) — "
                + $"{matches} match, {changed} changed, {generatorOnly} generator-only, {catalogOnly} catalog-only.";
            bool clean = changed == 0 && generatorOnly == 0 && catalogOnly == 0
                && ambiguous.Count == 0 && uninferrable.Count == 0;
            EditorGUILayout.HelpBox(summary, clean ? MessageType.Info : MessageType.Warning);
        }

        // The selected profile is preview-only. Writes use an independently resolved global plan.
        private void DrawWriteToCatalogButton(string abilityId)
        {
            if (!_globalCuePreviewPlans.TryGetValue(abilityId, out var plan))
            {
                plan = BuildGlobalCueWritePlan(abilityId);
                _globalCuePreviewPlans.Add(abilityId, plan);
            }
            foreach (string error in plan.Errors)
                EditorGUILayout.HelpBox(error, MessageType.Error);
            foreach (string drift in plan.Drift)
                EditorGUILayout.HelpBox(drift, MessageType.Warning);
            EditorGUILayout.HelpBox(
                $"{plan.Rows.Count} generated-owned cue(s) can be materialized across all animation contexts. "
                + "Manual and legacy cues are preserved. New slots require an explicit ownership declaration.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(plan.Errors.Count > 0 || plan.Rows.Count == 0))
            {
                if (GUILayout.Button($"Regenerate {abilityId} Owned Cues", GUILayout.Width(320f)))
                    ConfirmAndWriteOwnerCues(abilityId, plan.Rows, 0);
            }
        }

        // Fresh sort_order for the i-th inserted (generator-only) slot: the next multiple of 10 strictly
        // above the owner's current max authored sort_order, then +10 per subsequent inserted slot. This
        // is collision-free by construction — every authored row is ≤ the max, so an inserted row (past
        // it) matches neither an authored row nor another inserted row. Inserted slots therefore append
        // after the owner's existing rows; that placement is cosmetic, because sort_order is only a
        // within-trigger render tiebreaker and an added slot (cast_glow @ SPELL_CAST, impact @
        // SPELL_IMPACT) never shares a trigger frame with the projectile_body it joins. Multiples of 10
        // match the FIREBALL authoring convention (100/110/120) and leave gaps for a later hand-insert.
        internal static int NextInsertSortOrder(int maxExistingSortOrder, int insertIndex)
        {
            int baseSort = maxExistingSortOrder < 0 ? 0 : maxExistingSortOrder;
            return (((baseSort / 10) + 1) * 10) + (insertIndex * 10);
        }

        private void ConfirmAndWriteOwnerCues(string abilityId, List<SpellCueRow> rows, int insertedCount)
        {
            int updatedCount = rows.Count - insertedCount;
            string detail = insertedCount > 0
                ? $"Update {updatedCount} authored cue(s) in place and insert {insertedCount} generator-only slot(s)"
                : $"Update {rows.Count} authored cue(s) in place";
            if (!EditorUtility.DisplayDialog(
                    "Write generated cues",
                    $"{detail} for {abilityId} in {SpellPresentationEditorData.ProgressionCatalogPath}?\n\n"
                    + "The displayed generated fields will be materialized after checking every animation context. "
                    + "Manual and legacy cue fields are preserved. "
                    + LocalSpacetimeDbSharedDataPublisher.HubMatchRefreshGuidance,
                    "Write",
                    "Cancel"))
            {
                return;
            }

            string absolutePath = SpellPresentationEditorData.AbsoluteProgressionCatalogPath;
            try
            {
                bool changed = SpellCueCatalogWriter.WriteOwnerCues(
                    absolutePath, abilityId, rows, _loadedCatalogJson);
                if (changed)
                {
                    EditorUtility.DisplayDialog(
                        "Cues written",
                        $"Wrote {rows.Count} generated cue(s) for {abilityId} into {SpellPresentationEditorData.ProgressionCatalogPath}.\n\n"
                        + LocalSpacetimeDbSharedDataPublisher.HubMatchRefreshGuidance,
                        "OK");
                    Load();
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "No change",
                        $"The catalog already matches the generated cues for {abilityId}.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Write failed", ex.Message, "OK");
            }
        }

        // Runtime SpellVfxSlot -> the author-time snake_case slot key written to the JSON (design doc
        // §3.1/§3.4). The inverse of TryInferLegacySlot's (trigger, role, anchor-class) inference.
        private static string SlotKey(SpellVfxSlot slot)
            => slot switch
            {
                SpellVfxSlot.CastGlow => "cast_glow",
                SpellVfxSlot.Muzzle => "muzzle",
                SpellVfxSlot.ProjectileBody => "projectile_body",
                SpellVfxSlot.ProjectileTrail => "projectile_trail",
                SpellVfxSlot.TravelBody => "travel_body",
                SpellVfxSlot.Impact => "impact",
                SpellVfxSlot.Burst => "burst",
                SpellVfxSlot.Beam => "beam",
                SpellVfxSlot.SelfFlash => "self_flash",
                SpellVfxSlot.AuraGround => "aura_ground",
                SpellVfxSlot.CharacterFx => "character_fx",
                SpellVfxSlot.PersistentField => "persistent_field",
                SpellVfxSlot.TargetAttachment => "target_attachment",
                SpellVfxSlot.StatusAttachment => "status_attachment",
                SpellVfxSlot.PersistentCharacterFx => "persistent_character_fx",
                SpellVfxSlot.MaxStackCharacterFx => "max_stack_character_fx",
                _ => slot.ToString().ToLowerInvariant(),
            };

        private static List<string> DiffFields(GeneratedCue gen, CombatVfxCueDefinition cat)
        {
            var diffs = new List<string>();
            AddDiff(diffs, "trigger", gen.Trigger, Normalize(cat.trigger));
            AddDiff(diffs, "anchor", gen.Anchor, Normalize(cat.anchor));
            AddDiff(diffs, "attach_mode", gen.AttachMode, Normalize(cat.attach_mode));
            AddDiff(diffs, "vfx_role", gen.Role, EffectiveRole(cat.vfx_role));
            AddDiff(diffs, "lifecycle", gen.Lifecycle, EffectiveLifecycle(cat.lifecycle));
            AddDiff(diffs, "vfx_id", gen.VfxId, Normalize(cat.vfx_id));
            AddDiff(diffs, "duration_ms", gen.DurationMs.ToString(), cat.duration_ms.ToString());

            // Absence and sequence zero are different conditions, even outside projectile-body cues.
            AddDiff(diffs, "projectile_sequence_index", gen.ProjectileSequenceIndex?.ToString() ?? "<none>",
                cat.projectile_sequence_index < 0 ? "<none>" : cat.projectile_sequence_index.ToString());

            return diffs;
        }

        private static void AddDiff(List<string> diffs, string field, string generatedValue, string authoredValue)
        {
            if (!string.Equals(generatedValue, authoredValue, System.StringComparison.Ordinal))
                diffs.Add($"{field}: generated={NoneIfEmpty(generatedValue)}, authored={NoneIfEmpty(authoredValue)}");
        }

        // Design doc §3.4: assign an un-migrated cue a slot from (trigger, role, anchor-class). Total and
        // collision-free over the current catalog; anything it can't key is surfaced, never silently dropped.
        private static bool TryResolveCatalogSlotKey(CombatVfxCueDefinition cue, out string slotKey)
        {
            if (!string.IsNullOrWhiteSpace(cue.slot))
                return TryNormalizeExplicitSlotKey(cue.slot, out slotKey);

            if (TryInferLegacySlot(cue, out SpellVfxSlot inferred))
            {
                slotKey = SlotKey(inferred);
                return true;
            }

            slotKey = string.Empty;
            return false;
        }

        internal static bool TryNormalizeExplicitSlotKey(string value, out string slotKey)
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (normalized.StartsWith("character_fx/", System.StringComparison.Ordinal))
            {
                string variant = NormalizeSlotVariant(normalized.Substring("character_fx/".Length));
                if (variant.Length > 0)
                {
                    slotKey = $"character_fx/{variant.ToLowerInvariant()}";
                    return true;
                }
            }

            if (TryParseSlotKey(normalized, out SpellVfxSlot slot))
            {
                slotKey = SlotKey(slot);
                return true;
            }

            slotKey = string.Empty;
            return false;
        }

        private static string NormalizeSlotVariant(string value)
            => Regex.Replace(WireIdentifier.Normalize(value), @"\s+", "_");

        private static bool TryParseSlotKey(string slotKey, out SpellVfxSlot slot)
        {
            string normalized = slotKey.Trim().ToLowerInvariant();
            if (normalized.StartsWith("character_fx/", System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.CharacterFx;
                return normalized.Length > "character_fx/".Length;
            }

            switch (normalized)
            {
                case "cast_glow": slot = SpellVfxSlot.CastGlow; return true;
                case "muzzle": slot = SpellVfxSlot.Muzzle; return true;
                case "projectile_body": slot = SpellVfxSlot.ProjectileBody; return true;
                case "projectile_trail": slot = SpellVfxSlot.ProjectileTrail; return true;
                case "travel_body": slot = SpellVfxSlot.TravelBody; return true;
                case "impact": slot = SpellVfxSlot.Impact; return true;
                case "burst": slot = SpellVfxSlot.Burst; return true;
                case "beam": slot = SpellVfxSlot.Beam; return true;
                case "self_flash": slot = SpellVfxSlot.SelfFlash; return true;
                case "aura_ground": slot = SpellVfxSlot.AuraGround; return true;
                case "character_fx": slot = SpellVfxSlot.CharacterFx; return true;
                case "persistent_field": slot = SpellVfxSlot.PersistentField; return true;
                case "target_attachment": slot = SpellVfxSlot.TargetAttachment; return true;
                case "status_attachment": slot = SpellVfxSlot.StatusAttachment; return true;
                case "persistent_character_fx": slot = SpellVfxSlot.PersistentCharacterFx; return true;
                case "max_stack_character_fx": slot = SpellVfxSlot.MaxStackCharacterFx; return true;
                default:
                    slot = default;
                    return false;
            }
        }

        private static bool TryInferLegacySlot(CombatVfxCueDefinition cue, out SpellVfxSlot slot)
        {
            string role = EffectiveRole(cue.vfx_role);
            if (string.Equals(role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.ProjectileBody;
                return true;
            }
            if (string.Equals(role, SpellVfxGenerator.RoleProjectileTrail, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.ProjectileTrail;
                return true;
            }
            if (string.Equals(role, SpellVfxGenerator.RoleTravelBody, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.TravelBody;
                return true;
            }

            string trigger = Normalize(cue.trigger);
            AnchorClass anchorClass = ClassifyAnchor(Normalize(cue.anchor));
            bool attached = string.Equals(role, SpellVfxGenerator.RoleAttached, System.StringComparison.Ordinal);
            bool oneShot = string.Equals(role, SpellVfxGenerator.RoleOneShot, System.StringComparison.Ordinal);

            if (attached
                && string.Equals(Normalize(cue.anchor), SpellVfxGenerator.AnchorTarget, System.StringComparison.Ordinal)
                && string.Equals(trigger, SpellVfxGenerator.TriggerStatusActive, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.StatusAttachment;
                return true;
            }

            if (attached
                && string.Equals(Normalize(cue.anchor), SpellVfxGenerator.AnchorTargetBack, System.StringComparison.Ordinal)
                && string.Equals(trigger, SpellVfxGenerator.TriggerSpellImpact, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.TargetAttachment;
                return true;
            }

            if (attached && anchorClass == AnchorClass.Caster
                && string.Equals(trigger, SpellVfxGenerator.TriggerEmanationMaxStacks, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.MaxStackCharacterFx;
                return true;
            }
            if (attached && anchorClass == AnchorClass.Caster
                && string.Equals(trigger, SpellVfxGenerator.TriggerEmanationActive, System.StringComparison.Ordinal)
                && string.Equals(Normalize(cue.attach_mode), SpellVfxGenerator.AttachFollowAnchor, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.PersistentCharacterFx;
                return true;
            }

            if (attached && anchorClass == AnchorClass.Hand
                && string.Equals(trigger, SpellVfxGenerator.TriggerSpellCast, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.CastGlow;
                return true;
            }
            if (attached && anchorClass == AnchorClass.Hand
                && string.Equals(trigger, SpellVfxGenerator.TriggerSpellRelease, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.Beam;
                return true;
            }
            if (oneShot && anchorClass == AnchorClass.Hand
                && string.Equals(trigger, SpellVfxGenerator.TriggerSpellRelease, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.Muzzle;
                return true;
            }
            if (oneShot && anchorClass == AnchorClass.Caster
                && string.Equals(trigger, SpellVfxGenerator.TriggerSpellRelease, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.Burst;
                return true;
            }
            if (oneShot && anchorClass == AnchorClass.Impact && IsImpactTrigger(trigger))
            {
                slot = SpellVfxSlot.Impact;
                return true;
            }

            slot = default;
            return false;
        }

        private static bool IsImpactTrigger(string trigger)
            => string.Equals(trigger, SpellVfxGenerator.TriggerSpellRelease, System.StringComparison.Ordinal)
                || string.Equals(trigger, SpellVfxGenerator.TriggerSpellImpact, System.StringComparison.Ordinal)
                || string.Equals(trigger, SpellVfxGenerator.TriggerAreaImpact, System.StringComparison.Ordinal);

        private enum AnchorClass
        {
            Hand,
            Caster,
            Impact,
            Other,
        }

        private static AnchorClass ClassifyAnchor(string anchor)
        {
            switch (anchor)
            {
                case SpellVfxGenerator.AnchorLeftHand:
                case SpellVfxGenerator.AnchorRightHand:
                    return AnchorClass.Hand;
                case SpellVfxGenerator.AnchorCaster:
                case SpellVfxGenerator.AnchorCasterOverhead:
                    return AnchorClass.Caster;
                case SpellVfxGenerator.AnchorImpactPoint:
                case SpellVfxGenerator.AnchorAreaOrigin:
                case SpellVfxGenerator.AnchorTarget:
                case SpellVfxGenerator.AnchorTargetBack:
                case SpellVfxGenerator.AnchorGroundUnderCaster:
                case SpellVfxGenerator.AnchorOrigin:
                case "GROUND_UNDER_TARGET":
                    return AnchorClass.Impact;
                default:
                    return AnchorClass.Other;
            }
        }

        private static string DescribeGenerated(GeneratedCue gen)
        {
            string sequence = gen.ProjectileSequenceIndex.HasValue
                && (string.Equals(gen.Role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal)
                    || string.Equals(gen.Role, SpellVfxGenerator.RoleProjectileTrail, System.StringComparison.Ordinal))
                ? $" | sequence={gen.ProjectileSequenceIndex.Value}"
                : string.Empty;
            return $"{gen.Trigger} | {gen.Role} | {gen.Anchor} | {gen.AttachMode} | {gen.VfxId} | {gen.Lifecycle} "
                + $"| duration={gen.DurationMs}ms{sequence}";
        }

        private static string DescribeCatalog(CombatVfxCueDefinition cue)
        {
            string role = EffectiveRole(cue.vfx_role);
            string sequence = string.Equals(role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal)
                || string.Equals(role, SpellVfxGenerator.RoleProjectileTrail, System.StringComparison.Ordinal)
                ? $" | sequence={cue.projectile_sequence_index}"
                : string.Empty;
            return $"{Normalize(cue.trigger)} | {role} | {Normalize(cue.anchor)} | {Normalize(cue.attach_mode)} "
                + $"| {Normalize(cue.vfx_id)} | {EffectiveLifecycle(cue.lifecycle)} | duration={cue.duration_ms}ms{sequence}";
        }

        private static string NoneIfEmpty(string value)
            => string.IsNullOrEmpty(value) ? "<none>" : value;
    }
}
