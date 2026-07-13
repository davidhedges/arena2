#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                + "the authored combat_vfx_cues rows above. A zero diff means the generator reproduces the "
                + "authored cues exactly (only vfx_id/duration come from the palette). Read-only preview — it "
                + "does not write progression_catalog.shared.json.",
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
                    "No SpellVfxOverrideCatalog asset found. School-derived slots can still preview, but bespoke spell slots and cast-hand exceptions are unavailable.",
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
            DrawWriteToCatalogButton(
                abilityId, preview.Cues, catalogBySlot,
                inferenceClean: ambiguousSlots.Count == 0 && uninferrableCues.Count == 0);
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
                _cachedSpellOverrides, abilityId, hasResolvedAnimation, resolvedAnimation);

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
            SpellVfxOverrideCatalog? spellOverrides,
            string abilityId,
            bool hasResolvedAnimation,
            WeaponSpellAnimationEntry resolvedAnimation)
        {
            // An explicit per-spell override wins — the authored E7 hand for spells the animation resolves
            // wrongly (or can't resolve), e.g. BLADE_BARRIER's RIGHT launch.
            if (spellOverrides != null && spellOverrides.TryGet(abilityId, out SpellVfxAbilityOverride spellOverride))
            {
                if (spellOverride.castHand == SpellVfxCastHandOverride.Left)
                    return SpellVfxGenerator.AnchorLeftHand;
                if (spellOverride.castHand == SpellVfxCastHandOverride.Right)
                    return SpellVfxGenerator.AnchorRightHand;
            }

            // E7: otherwise the concrete cast hand is inferred from the resolved animation/playback layer
            // (explicit entry or SpellCastAnimationMap composition). Profile-less SPELL_* spells have no
            // animation set, so fall back to the generator's LEFT_HAND default (14/15 hand cues use LEFT
            // today — design doc Appendix B "shared modifiers").
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
                    if (!IsOptionalSlot(slot))
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

            return rows;
        }

        private static bool IsOptionalSlot(SpellVfxSlot slot)
            => slot == SpellVfxSlot.Muzzle
                || slot == SpellVfxSlot.ProjectileTrail
                || slot == SpellVfxSlot.CharacterFx
                || slot == SpellVfxSlot.SelfFlash;

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

            string variant = WireIdentifier.Normalize(variantId);
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

        // Materialise the generated cues into progression_catalog.shared.json via the tested surgical
        // writer (SpellCueCatalogWriter). Two row dispositions:
        //   • a generated slot that matches an authored cue 1:1 UPDATES that row in place, keeping its
        //     sort_order (so an unchanged spell like FIREBALL diffs to only the inserted slot keys);
        //   • a generator-only slot (the "generator adds a slot" migration bucket) is INSERTED with a
        //     fresh sort_order from NextInsertSortOrder — past the owner's current max, so it collides
        //     with neither an authored row nor another inserted row.
        // The write is gated to the cases where it is provably non-destructive and unsurprising: slot
        // inference must be unambiguous (inferenceClean), no matched slot may differ (`changed` — a
        // wiring diff to adjudicate by hand, never auto-applied), and the generator must be a superset
        // of the authored slots (no `catalogOnly` — an authored slot the generator does not emit would
        // survive alongside an inserted one, e.g. the SelfNova Burst/Impact slot-name nuance, doubling
        // the effect). Those three still block; a pure superset (updates + inserts) is writable.
        private void DrawWriteToCatalogButton(
            string abilityId,
            List<GeneratedCue> generated,
            Dictionary<string, CombatVfxCueDefinition> catalogBySlot,
            bool inferenceClean)
        {
            int maxExistingSortOrder = 0;
            foreach (CombatVfxCueDefinition cue in catalogBySlot.Values)
                maxExistingSortOrder = Mathf.Max(maxExistingSortOrder, cue.sort_order);

            var rows = new List<SpellCueRow>(generated.Count);
            int changed = 0;
            int inserted = 0;
            // Slot-enum order so a multi-insert (e.g. BLESSED_SHIELD: cast_glow + impact) assigns the
            // inserted sort_orders deterministically.
            foreach (GeneratedCue gen in generated
                         .OrderBy(g => (int)g.Slot)
                         .ThenBy(g => g.SlotKey, System.StringComparer.Ordinal))
            {
                int sortOrder;
                if (catalogBySlot.TryGetValue(gen.SlotKey, out CombatVfxCueDefinition authored))
                {
                    if (DiffFields(gen, authored).Count > 0)
                        changed++;
                    sortOrder = authored.sort_order; // update in place
                }
                else
                {
                    sortOrder = NextInsertSortOrder(maxExistingSortOrder, inserted);
                    inserted++;
                }

                rows.Add(new SpellCueRow(
                    slot: gen.SlotKey,
                    trigger: gen.Trigger,
                    anchor: gen.Anchor,
                    vfxId: gen.VfxId,
                    attachMode: gen.AttachMode,
                    vfxRole: gen.Role,
                    lifecycle: gen.Lifecycle,
                    projectileSequenceIndex: gen.ProjectileSequenceIndex,
                    durationMs: gen.DurationMs,
                    sortOrder: sortOrder));
            }

            int catalogOnly = 0;
            var generatedSlots = new HashSet<string>(
                generated.Select(g => g.SlotKey),
                System.StringComparer.Ordinal);
            foreach (string slot in catalogBySlot.Keys)
                if (!generatedSlots.Contains(slot))
                    catalogOnly++;

            bool writable = inferenceClean
                && generated.Count > 0
                && changed == 0
                && catalogOnly == 0;

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(!writable || rows.Count == 0))
            {
                if (GUILayout.Button($"Write {abilityId} Cues to Catalog", GUILayout.Width(300f)))
                    ConfirmAndWriteOwnerCues(abilityId, rows, inserted);
            }

            if (!writable)
            {
                EditorGUILayout.HelpBox(
                    "Writing is enabled when every generated slot either matches an authored cue 1:1 "
                    + "(updated in place) or is a new slot the generator adds (inserted in sort order). "
                    + "It is blocked while any matched row is changed (a wiring diff to adjudicate), any "
                    + "authored slot is catalog-only (the generator emits nothing for it — resolve or "
                    + "remove it by hand), or slot inference is ambiguous.",
                    MessageType.None);
            }
            else
            {
                string disposition = inserted > 0
                    ? $"updates {rows.Count - inserted} authored cue(s) in place and inserts {inserted} new slot(s)"
                    : "updates the authored cues in place";
                EditorGUILayout.HelpBox(
                    $"Writes the generated cues into progression_catalog.shared.json — {disposition}, "
                    + "assigning inserted rows a fresh sort_order and leaving every other byte identical. "
                    + "Republish the module (spacetime publish -p server) to apply — the catalog JSON is "
                    + "include_str!'d.",
                    MessageType.None);
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
                    + "Author-time slot keys are inserted and inserted rows get a fresh sort_order; every "
                    + "other byte is preserved. Republish the module afterwards to apply.",
                    "Write",
                    "Cancel"))
            {
                return;
            }

            string absolutePath = SpellPresentationEditorData.AbsoluteProgressionCatalogPath;
            try
            {
                bool changed = SpellCueCatalogWriter.WriteOwnerCues(absolutePath, abilityId, rows);
                if (changed)
                {
                    EditorUtility.DisplayDialog(
                        "Cues written",
                        $"Wrote {rows.Count} generated cue(s) for {abilityId} into {SpellPresentationEditorData.ProgressionCatalogPath}.\n\n"
                        + "Republish the module (spacetime publish -p server) to apply.",
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

            // projectile_sequence_index participates for visuals bound to an authoritative projectile row.
            if (gen.ProjectileSequenceIndex.HasValue
                && (string.Equals(gen.Role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal)
                    || string.Equals(gen.Role, SpellVfxGenerator.RoleProjectileTrail, System.StringComparison.Ordinal)))
            {
                AddDiff(diffs, "projectile_sequence_index",
                    gen.ProjectileSequenceIndex.Value.ToString(), cat.projectile_sequence_index.ToString());
            }

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

        private static bool TryNormalizeExplicitSlotKey(string value, out string slotKey)
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (normalized.StartsWith("character_fx/", System.StringComparison.Ordinal))
            {
                string variant = WireIdentifier.Normalize(normalized.Substring("character_fx/".Length));
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
