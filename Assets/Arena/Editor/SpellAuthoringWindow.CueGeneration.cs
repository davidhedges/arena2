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

        // Per-school defaults (school × slot → look), derived from the catalog by the NAMING convention
        // (VFX_<SCHOOL>_* belongs to that school) rather than raw frequency — frequency would codify drift
        // (e.g. two ARCANE spells wear VFX_ICE_* today; that is the drift to correct, not an arcane generic).
        // Only genuinely school-generic looks live here (a hand cast-glow, a stock impact); bespoke bodies /
        // explosions are per-spell signatures below. A requested slot a school does not provide surfaces a
        // coverage warning and is omitted — never a block (design §4.2).
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>> SchoolPalettes =
            new Dictionary<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>>(System.StringComparer.Ordinal)
            {
                ["FIRE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.CastGlow] = new PaletteEntry("VFX_FIRE_CAST_HAND_01", selfTerminating: false, durationMs: 350),
                },
                ["COLD"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.CastGlow] = new PaletteEntry("VFX_ICE_CAST_HAND_01", selfTerminating: false, durationMs: 350),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_ICE_HIT_01", selfTerminating: false, durationMs: 1000),
                },
                ["LIGHTNING"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_LIGHTNING_01", selfTerminating: false, durationMs: 1200),
                },
                // ARCANE corrects the ice-drift (owner-supplied prefabs): ORBITING_BLADES + MAGIC_MISSILE
                // wore VFX_ICE_* for their hand glow / impact; these are the real arcane generics.
                ["ARCANE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.CastGlow] = new PaletteEntry("VFX_ARCANE_CAST_HAND_01", selfTerminating: false, durationMs: 350),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_ARCANE_HIT_01", selfTerminating: false, durationMs: 700),
                },
                // HOLY applies to exactly the two holy-damage projectiles (BLESSED_SHIELD, BLADE_BARRIER —
                // the only spells with damage_type HOLY); they share a generic holy hit. No holy hand
                // cast-glow prefab is authored yet (there is Human_SpellAura_{Arcane,Fire,Ice} but no Holy),
                // so cast_glow is intentionally absent — the generator omits it with a coverage note rather
                // than emit a dangling vfx_id. Add a CastGlow entry here once a holy hand-glow is registered.
                ["HOLY"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_HOLY_HIT_01", selfTerminating: false, durationMs: 1000),
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
                // ICICLE inherits the COLD cast_glow + impact; only its flying body is bespoke.
                ["SPELL_ICICLE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_ICICLE_PROJECTILE_01"),
                },
                ["SPELL_GLACIAL_SPIKE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_GLACIAL_SPIKE_TARGET_01", selfTerminating: true),
                },
                ["SPELL_FROST_NOVA"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Burst] = new PaletteEntry("VFX_FROST_NOVA_01", selfTerminating: true),
                },
                // No damage_type/vfx_school (school resolves to none) → their bursts are pure signatures.
                ["WARRIOR_INTIMIDATE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Burst] = new PaletteEntry("VFX_VOID_AREA_01", selfTerminating: true),
                },
                ["WARRIOR_SHOCKWAVE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Burst] = new PaletteEntry("VFX_AIR_BURST_01_ARENA", selfTerminating: true),
                },
                // Arcane projectiles: only the flying body is bespoke; cast_glow + impact come from the
                // ARCANE school (the retint). MAGIC_MISSILE's impact is unauthored today (generator-adds).
                ["SPELL_ORBITING_BLADES"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_ORBITING_BLADES_PROJECTILE_01"),
                },
                ["SPELL_MAGIC_MISSILE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_MAGIC_MISSILE_PROJECTILE_01"),
                },
                // Deferred point-areas (impact_delay > 0): the impact resolves at detonation, so the
                // generator wires it to AREA_IMPACT@AREA_ORIGIN — the burst is each spell's signature.
                ["SPELL_ERUPTION"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_ERUPTION_01", selfTerminating: true),
                },
                ["SPELL_FROST_NEEDLE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_FROST_NEEDLE_01", selfTerminating: true),
                },
                ["PALADIN_CONSECRATE"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_LIGHT_AREA_01_ARENA", selfTerminating: true),
                },
                // No-projectile immediate impact on the target → TargetHit anchors on TARGET (owner-verified in-game).
                ["PALADIN_SACRED_FLAME"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_SACRED_FLAME_HIT_01", selfTerminating: true),
                },
                // Holy-damage projectiles: bespoke flying body (signature); the impact comes from the HOLY
                // school (generic holy hit). cast_glow has no holy prefab yet, so it is omitted. BLESSED_SHIELD
                // launches LEFT (matches the generator default); BLADE_BARRIER launches RIGHT (via the
                // CastHandOverrides below) — both bodies now match, and each inserts the HOLY impact.
                ["PALADIN_BLESSED_SHIELD"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_BLESSED_SHIELD_PROJECTILE_01"),
                },
                ["PALADIN_BLADE_BARRIER"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_BLADE_BARRIER_PROJECTILE_01"),
                },
                // Charged fire sky-drop: bespoke meteor head (travel body, lifecycle forced UNTIL_TERMINAL) +
                // a PARTICLE_SYSTEM impact; the FIRE school adds a charging hand cast-glow (inserted).
                ["SPELL_METEOR"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.TravelBody] = new PaletteEntry("VFX_METEOR_HEAD_01"),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_METEOR_01", selfTerminating: true),
                },
                // Channel ice projectile: bespoke splinter body + a per-spell impact DURATION 700 (vs the
                // COLD generic 1000 that ICICLE uses) reusing the COLD hit look; the COLD school adds a
                // channel hand cast-glow (inserted, UNTIL_CAST_END).
                ["SPELL_FROZEN_SPLINTERS"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_FROZEN_SPLINTER_PROJECTILE_01"),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_ICE_HIT_01", selfTerminating: false, durationMs: 700),
                },
                // Charged arcane beam: bespoke beam body (DURATION 500); the ARCANE school adds a charging
                // hand cast-glow (inserted).
                ["SPELL_INSTANT_BEAM"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.Beam] = new PaletteEntry("VFX_INSTANT_BEAM_01", selfTerminating: false, durationMs: 500),
                },
                // SHADOW projectiles: bespoke body + hit (both signatures). No SHADOW cast-glow prefab yet,
                // so cast_glow is omitted → these migrate as a pure slot-stamp (no new effect, no republish).
                ["SPELL_BOOMERANG_ORB"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_BOOMERANG_ORB_PROJECTILE_01"),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_BOOMERANG_ORB_HIT_01", selfTerminating: false, durationMs: 700),
                },
                ["SPELL_WITHERING_ORB"] = new Dictionary<SpellVfxSlot, PaletteEntry>
                {
                    [SpellVfxSlot.ProjectileBody] = new PaletteEntry("VFX_WITHERING_ORB_PROJECTILE_01"),
                    [SpellVfxSlot.Impact] = new PaletteEntry("VFX_WITHERING_ORB_HIT_01", selfTerminating: false, durationMs: 700),
                },
            };

        // Per-spell cast-hand override (design doc Appendix B "shared modifiers" — the resolved E7 cast
        // hand). The generator normally infers the cast hand from the animation entry and falls back to
        // LEFT_HAND when it can't; this asserts a spell's hand explicitly and WINS over inference, for
        // spells whose authored hand disagrees with what the animation resolves. Applies to every
        // Hand-anchored slot the spell requests (cast_glow, projectile_body, muzzle, beam), so a
        // right-handed spell stays internally consistent. BLADE_BARRIER launches from the RIGHT hand,
        // but its SwordAndShield animation resolves LEFT (as BLESSED_SHIELD does) — without this its
        // projectile_body anchor would diff and block the write.
        private static readonly IReadOnlyDictionary<string, string> CastHandOverrides =
            new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["PALADIN_BLADE_BARRIER"] = SpellVfxGenerator.AnchorRightHand,
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
            string castHandAnchor = ResolveCastHandAnchor(abilityId, hasAnimationEntry, animationEntry);

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

            BuildCatalogBySlot(
                out Dictionary<SpellVfxSlot, CombatVfxCueDefinition> catalogBySlot,
                out List<SpellVfxSlot> ambiguousSlots,
                out List<CombatVfxCueDefinition> uninferrableCues);
            DrawCueDiff(generated, catalogBySlot, ambiguousSlots, uninferrableCues);
            DrawWriteToCatalogButton(
                abilityId, generated, catalogBySlot,
                inferenceClean: ambiguousSlots.Count == 0 && uninferrableCues.Count == 0);
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
            string abilityId, bool hasAnimationEntry, WeaponSpellAnimationEntry animationEntry)
        {
            // An explicit per-spell override wins — the authored E7 hand for spells the animation resolves
            // wrongly (or can't resolve), e.g. BLADE_BARRIER's RIGHT launch.
            if (CastHandOverrides.TryGetValue(abilityId, out string overrideHand))
                return overrideHand;

            // E7: otherwise the concrete cast hand is inferred from the animation/playback layer; profile-less
            // SPELL_* spells have no animation set, so fall back to the generator's LEFT_HAND default (14/15
            // hand cues use LEFT today — design doc Appendix B "shared modifiers").
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

            // Decision 10: prefer the externalized per-school VFX-set assets over the seed dictionary.
            // Reloaded per generation so authoring edits to the assets are picked up immediately.
            _assetSchoolPalettes = LoadSchoolPalettesFromAssets();

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

        // Decision 10: per-school palettes loaded from SchoolVfxSet assets (school → slot → look).
        // A school present here wins over the seed SchoolPalettes below, so schools are edited as
        // assets; a school absent here falls back to the seed — so nothing changes until an asset exists.
        private static Dictionary<string, Dictionary<SpellVfxSlot, PaletteEntry>> _assetSchoolPalettes = new();

        private static Dictionary<string, Dictionary<SpellVfxSlot, PaletteEntry>> LoadSchoolPalettesFromAssets()
        {
            var map = new Dictionary<string, Dictionary<SpellVfxSlot, PaletteEntry>>(System.StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:SchoolVfxSet"))
            {
                var set = AssetDatabase.LoadAssetAtPath<SchoolVfxSet>(AssetDatabase.GUIDToAssetPath(guid));
                if (set == null || set.SchoolIdOrEmpty.Length == 0) continue;

                if (!map.TryGetValue(set.SchoolIdOrEmpty, out Dictionary<SpellVfxSlot, PaletteEntry> slotMap))
                    map[set.SchoolIdOrEmpty] = slotMap = new Dictionary<SpellVfxSlot, PaletteEntry>();
                foreach (SchoolVfxSlotEntry e in set.Slots)
                    if (!string.IsNullOrWhiteSpace(e.vfxId))
                        slotMap[e.slot] = new PaletteEntry(e.vfxId, e.selfTerminating, e.durationMs);
            }

            return map;
        }

        // One-time migration (decision 10): materialize the seed SchoolPalettes into editable
        // SchoolVfxSet assets. Byte-faithful (same vfx_id/duration/self_terminating), so the generator
        // produces the identical output afterward — it just reads the assets instead of the dictionary.
        [MenuItem("Arena/Combat VFX/Externalize School Palettes to Assets")]
        private static void ExternalizeSchoolPalettes()
        {
            const string parent = "Assets/Arena/Resources";
            const string folder = "SchoolVfxSets";
            string dir = $"{parent}/{folder}";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder(parent, folder);

            int created = 0, updated = 0;
            foreach (KeyValuePair<string, IReadOnlyDictionary<SpellVfxSlot, PaletteEntry>> kvp in SchoolPalettes)
            {
                string path = $"{dir}/{kvp.Key}.asset";
                var set = AssetDatabase.LoadAssetAtPath<SchoolVfxSet>(path);
                bool isNew = set == null;
                if (isNew)
                {
                    set = ScriptableObject.CreateInstance<SchoolVfxSet>();
                    AssetDatabase.CreateAsset(set, path);
                    created++;
                }
                else
                {
                    updated++;
                }

                set!.schoolId = kvp.Key;
                var slots = new List<SchoolVfxSlotEntry>();
                foreach (KeyValuePair<SpellVfxSlot, PaletteEntry> s in kvp.Value)
                    slots.Add(new SchoolVfxSlotEntry
                    {
                        slot = s.Key,
                        vfxId = s.Value.VfxId,
                        selfTerminating = s.Value.SelfTerminating,
                        durationMs = s.Value.DurationMs,
                        scale = 1f,
                    });
                set.EditorSetSlots(slots);
                EditorUtility.SetDirty(set);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            string msg = $"{created} created, {updated} updated in {dir}.\nThe generator now reads these assets (seed dictionary is the fallback).";
            Debug.Log($"[SchoolVfxSet] Externalized {SchoolPalettes.Count} school palettes — {msg}");
            EditorUtility.DisplayDialog("Externalize School Palettes", msg, "OK");
        }

        private static bool TryResolvePaletteEntry(string school, string abilityId, SpellVfxSlot slot, out PaletteEntry entry)
        {
            if (SignatureOverrides.TryGetValue(abilityId, out IReadOnlyDictionary<SpellVfxSlot, PaletteEntry> overrides)
                && overrides.TryGetValue(slot, out entry))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(school))
            {
                // Asset set wins when the school has one; else the seed dictionary.
                if (_assetSchoolPalettes.TryGetValue(school, out Dictionary<SpellVfxSlot, PaletteEntry> assetPalette)
                    && assetPalette.TryGetValue(slot, out entry))
                {
                    return true;
                }

                if (SchoolPalettes.TryGetValue(school, out IReadOnlyDictionary<SpellVfxSlot, PaletteEntry> palette)
                    && palette.TryGetValue(slot, out entry))
                {
                    return true;
                }
            }

            entry = default;
            return false;
        }

        // Assign every authored cue a slot via the design-doc §3.4 legacy inference table. The slot is
        // the stable identity across the two stores (the diff and the writer both key on it): matching
        // is exact by slot, ambiguous/uninferrable rows are surfaced rather than silently dropped.
        private void BuildCatalogBySlot(
            out Dictionary<SpellVfxSlot, CombatVfxCueDefinition> catalogBySlot,
            out List<SpellVfxSlot> ambiguous,
            out List<CombatVfxCueDefinition> uninferrable)
        {
            catalogBySlot = new Dictionary<SpellVfxSlot, CombatVfxCueDefinition>();
            ambiguous = new List<SpellVfxSlot>();
            uninferrable = new List<CombatVfxCueDefinition>();
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
        }

        private void DrawCueDiff(
            List<GeneratedCue> generated,
            Dictionary<SpellVfxSlot, CombatVfxCueDefinition> catalogBySlot,
            List<SpellVfxSlot> ambiguous,
            List<CombatVfxCueDefinition> uninferrable)
        {
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
            Dictionary<SpellVfxSlot, CombatVfxCueDefinition> catalogBySlot,
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
            foreach (GeneratedCue gen in generated.OrderBy(g => (int)g.Slot))
            {
                int sortOrder;
                if (catalogBySlot.TryGetValue(gen.Slot, out CombatVfxCueDefinition authored))
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
                    slot: SlotKey(gen.Slot),
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
            var generatedSlots = new HashSet<SpellVfxSlot>(generated.Select(g => g.Slot));
            foreach (SpellVfxSlot slot in catalogBySlot.Keys)
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
                    $"{detail} for {abilityId} in {ProgressionCatalogPath}?\n\n"
                    + "Author-time slot keys are inserted and inserted rows get a fresh sort_order; every "
                    + "other byte is preserved. Republish the module afterwards to apply.",
                    "Write",
                    "Cancel"))
            {
                return;
            }

            string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), ProgressionCatalogPath);
            try
            {
                bool changed = SpellCueCatalogWriter.WriteOwnerCues(absolutePath, abilityId, rows);
                if (changed)
                {
                    EditorUtility.DisplayDialog(
                        "Cues written",
                        $"Wrote {rows.Count} generated cue(s) for {abilityId} into {ProgressionCatalogPath}.\n\n"
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
                SpellVfxSlot.TravelBody => "travel_body",
                SpellVfxSlot.Impact => "impact",
                SpellVfxSlot.Burst => "burst",
                SpellVfxSlot.Beam => "beam",
                SpellVfxSlot.SelfFlash => "self_flash",
                SpellVfxSlot.AuraGround => "aura_ground",
                SpellVfxSlot.Aura => "aura",
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
