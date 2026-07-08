#nullable enable
using System.Collections.Generic;
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
    // (vfx_id + duration) from a seeded per-school VFX palette (+ per-spell signature overrides), and diff
    // the generated cues against the authored combat_vfx_cues rows.
    //
    // This is the generator's *editor face* — the first slice toward the "one place" authoring ideal. It is
    // a READ-ONLY preview: it does not write progression_catalog.shared.json (that needs a tested JSON
    // writer, a later slice). A zero diff proves the generator faithfully reproduces the authored cues
    // (FIRE / FIREBALL is the seeded first exemplar), so we can trust it before it starts materialising rows.
    //
    // The seeded palette below stands in for the per-school VFX-set asset (decision 10) until that asset
    // exists; it is pre-filled from the catalog's real vfx_ids so the FIRE exemplar diffs to zero.
    internal sealed partial class SpellAuthoringWindow
    {
        /// <summary>The look a palette supplies for one slot: <c>vfx_id</c> and (for ONE_SHOT/DURATION slots)
        /// the concrete duration. <see cref="SelfTerminating"/> maps to a <c>PARTICLE_SYSTEM</c> lifecycle
        /// (design doc §3.1 — the only palette influence on lifecycle).</summary>
        private readonly struct PaletteEntry
        {
            public PaletteEntry(string vfxId, bool selfTerminating = false, int durationMs = 0)
            {
                VfxId = vfxId;
                SelfTerminating = selfTerminating;
                DurationMs = durationMs;
            }

            public string VfxId { get; }
            public bool SelfTerminating { get; }
            public int DurationMs { get; }
        }

        // Per-school defaults (school × slot → look). Seeded with FIRE only — the first exemplar. cast_glow is
        // the genuinely school-generic fire look shared across fire spells; a fire school also needs a generic
        // projectile_body/impact for fire projectiles with no signature, but FIREBALL supplies those itself
        // (below), so they are intentionally absent here — a fire projectile without a signature would surface
        // a coverage warning, the correct signal to author a generic fire body prefab.
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>> SchoolPalettes =
            new Dictionary<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>>(System.StringComparer.Ordinal)
            {
                ["FIRE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.CastGlow] = new PaletteEntry("VFX_FIRE_CAST_HAND_01", selfTerminating: false, durationMs: 350),
                },
            };

        // Per-spell signature overrides (ability_id × slot → look). Override wins over the school default for
        // its slot (design doc §3.3). FIREBALL's flying body + explosion are bespoke (signature); its hand
        // glow inherits the FIRE school. This is a full-entry replacement for the slot — a partial per-field
        // merge is a later refinement.
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>> SignatureOverrides =
            new Dictionary<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>>(System.StringComparer.Ordinal)
            {
                ["SPELL_FIREBALL"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_FIREBALL_PROJECTILE_01"),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_FIREBALL_HIT_01", selfTerminating: false, durationMs: 1000),
                },
            };

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
            public string Trigger { get; }
            public string Anchor { get; }
            public string AttachMode { get; }
            public string Role { get; }
            public string Lifecycle { get; }
            public int DurationMs { get; }
            public string VfxId { get; }
            public int? ProjectileSequenceIndex { get; }
        }

        private void DrawGeneratedCuePreview(
            AbilityDefinition selected,
            string abilityId,
            bool hasAnimationEntry,
            WeaponSpellAnimationEntry animationEntry)
        {
            EditorGUILayout.LabelField("Generated Cues (SpellVfxGenerator)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Archetype-derived cues from SpellVfxGenerator + the seeded school VFX palette, diffed against "
                + "the authored combat_vfx_cues rows above. A zero diff means the generator reproduces the "
                + "authored cues exactly (only vfx_id/duration come from the palette). Read-only preview — it "
                + "does not write progression_catalog.shared.json.",
                MessageType.Info);

            string deliveryKind = Normalize(selected.gameplay.delivery.kind);
            SpellDeliveryFacts facts = BuildDeliveryFacts(selected);
            SpellVfxArchetype? archetype = SpellVfxGenerator.DeriveArchetype(facts);
            SpellAnimationArchetype mode = SpellAnimationArchetypes.Derive(
                (ulong)Mathf.Max(0, selected.gameplay.cast_time_ms), deliveryKind);
            string school = ResolveSchool(selected.gameplay.delivery);
            string castHandAnchor = ResolveCastHandAnchor(hasAnimationEntry, animationEntry);

            EditorGUILayout.LabelField(
                "Derivation",
                $"delivery={NoneIfEmpty(deliveryKind)} | archetype={(archetype?.ToString() ?? "<none>")} "
                + $"| mode={mode} | school={NoneIfEmpty(school)} | hand={castHandAnchor}");

            if (archetype == null)
            {
                EditorGUILayout.HelpBox(
                    $"delivery.kind '{NoneIfEmpty(deliveryKind)}' has no VFX archetype, so the generator emits nothing for this spell.",
                    MessageType.Warning);
                return;
            }

            if (string.IsNullOrEmpty(school))
            {
                EditorGUILayout.HelpBox(
                    "No school resolved (vfx_school and damage_type are both unset). The generator can still wire "
                    + "slots, but no palette fills their vfx_ids — set a school or add signature overrides.",
                    MessageType.Warning);
            }

            List<GeneratedCue> generated = GenerateCues(
                archetype.Value, mode, facts, abilityId, school, castHandAnchor, out List<string> slotNotes);

            foreach (string note in slotNotes)
                EditorGUILayout.HelpBox(note, MessageType.Warning);

            DrawCueDiff(abilityId, generated);
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

        private static string ResolveCastHandAnchor(bool hasAnimationEntry, WeaponSpellAnimationEntry animationEntry)
        {
            // E7: the concrete cast hand is inferred from the animation/playback layer; profile-less SPELL_*
            // spells have no animation set, so fall back to the generator's LEFT_HAND default (14/15 hand cues
            // use LEFT today — design doc Appendix B "shared modifiers").
            if (hasAnimationEntry
                && TryInferSpellPresentationHand(animationEntry, out string handAnchor, out _))
            {
                return handAnchor;
            }

            return SpellVfxGenerator.AnchorToCatalog(CueAnchor.Hand);
        }

        private static List<GeneratedCue> GenerateCues(
            SpellVfxArchetype archetype,
            SpellAnimationArchetype mode,
            SpellDeliveryFacts facts,
            string abilityId,
            string school,
            string castHandAnchor,
            out List<string> slotNotes)
        {
            slotNotes = new List<string>();
            var rows = new List<GeneratedCue>();

            foreach (SpellVfxSlot slot in SpellVfxGenerator.RequestedSlots(archetype, mode))
            {
                if (!TryResolvePaletteEntry(school, abilityId, slot, out PaletteEntry entry))
                {
                    slotNotes.Add(
                        $"Slot '{slot}' is requested by the {archetype} archetype but neither the {NoneIfEmpty(school)} "
                        + $"school palette nor a {abilityId} signature override provides a vfx_id — the generator omits it.");
                    continue;
                }

                CueWiring wiring = SpellVfxGenerator.Wire(archetype, slot, mode, entry.SelfTerminating, facts.Deferred);
                string anchor = wiring.Anchor == CueAnchor.Hand
                    ? castHandAnchor
                    : SpellVfxGenerator.AnchorToCatalog(wiring.Anchor);
                int durationMs = wiring.Duration == CueDurationPolicy.PalettePositive ? entry.DurationMs : 0;

                rows.Add(new GeneratedCue(
                    slot: slot,
                    trigger: wiring.Trigger,
                    anchor: anchor,
                    attachMode: wiring.AttachMode,
                    role: wiring.VfxRole,
                    lifecycle: wiring.Lifecycle,
                    durationMs: durationMs,
                    vfxId: entry.VfxId,
                    projectileSequenceIndex: wiring.ProjectileSequenceIndex));
            }

            return rows;
        }

        private static bool TryResolvePaletteEntry(string school, string abilityId, SpellVfxSlot slot, out PaletteEntry entry)
        {
            if (SignatureOverrides.TryGetValue(abilityId, out IReadOnlyDictionary<SpellVfxSlot, PaletteEntry> overrides)
                && overrides.TryGetValue(slot, out entry))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(school)
                && SchoolPalettes.TryGetValue(school, out IReadOnlyDictionary<SpellVfxSlot, PaletteEntry> palette)
                && palette.TryGetValue(slot, out entry))
            {
                return true;
            }

            entry = default;
            return false;
        }

        private void DrawCueDiff(string abilityId, List<GeneratedCue> generated)
        {
            // Assign every authored cue a slot via the design-doc §3.4 legacy inference table, then diff by
            // slot: the generator's whole point is that a slot is the stable identity across the two stores.
            var catalogBySlot = new Dictionary<SpellVfxSlot, CombatVfxCueDefinition>();
            var ambiguous = new List<SpellVfxSlot>();
            var uninferrable = new List<CombatVfxCueDefinition>();
            foreach (CombatVfxCueDefinition cue in _selectedAbilityCues)
            {
                if (!TryInferLegacySlot(cue, out SpellVfxSlot slot))
                {
                    uninferrable.Add(cue);
                    continue;
                }

                if (catalogBySlot.ContainsKey(slot))
                {
                    if (!ambiguous.Contains(slot))
                        ambiguous.Add(slot);
                    continue;
                }

                catalogBySlot.Add(slot, cue);
            }

            var generatedBySlot = generated.ToDictionary(row => row.Slot);
            IEnumerable<SpellVfxSlot> allSlots = generatedBySlot.Keys
                .Union(catalogBySlot.Keys)
                .OrderBy(slot => (int)slot);

            int matches = 0;
            int changed = 0;
            int generatorOnly = 0;
            int catalogOnly = 0;

            foreach (SpellVfxSlot slot in allSlots)
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

            foreach (SpellVfxSlot slot in ambiguous)
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

            // projectile_sequence_index only participates for PROJECTILE_BODY rows (elsewhere the catalog omits it).
            if (gen.ProjectileSequenceIndex.HasValue
                && string.Equals(gen.Role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal))
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
        private static bool TryInferLegacySlot(CombatVfxCueDefinition cue, out SpellVfxSlot slot)
        {
            string role = EffectiveRole(cue.vfx_role);
            if (string.Equals(role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal))
            {
                slot = SpellVfxSlot.ProjectileBody;
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
                && string.Equals(gen.Role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal)
                ? $" | sequence={gen.ProjectileSequenceIndex.Value}"
                : string.Empty;
            return $"{gen.Trigger} | {gen.Role} | {gen.Anchor} | {gen.AttachMode} | {gen.VfxId} | {gen.Lifecycle} "
                + $"| duration={gen.DurationMs}ms{sequence}";
        }

        private static string DescribeCatalog(CombatVfxCueDefinition cue)
        {
            string role = EffectiveRole(cue.vfx_role);
            string sequence = string.Equals(role, SpellVfxGenerator.RoleProjectileBody, System.StringComparison.Ordinal)
                ? $" | sequence={cue.projectile_sequence_index}"
                : string.Empty;
            return $"{Normalize(cue.trigger)} | {role} | {Normalize(cue.anchor)} | {Normalize(cue.attach_mode)} "
                + $"| {Normalize(cue.vfx_id)} | {EffectiveLifecycle(cue.lifecycle)} | duration={cue.duration_ms}ms{sequence}";
        }

        private static string NoneIfEmpty(string value)
            => string.IsNullOrEmpty(value) ? "<none>" : value;
    }
}
