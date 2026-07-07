//! Pure core of the spell VFX cue generator (design doc: Appendix B).
//!
//! Derives the presentation archetype from authored gameplay and emits the per-slot cue
//! *wiring* (trigger / anchor / attach_mode / vfx_role / lifecycle / duration). A school
//! palette then fills each slot with a `vfx_id`; per-spell legacy cues override slots.
//!
//! The wiring is correct-by-construction against the Class-A authoring rules (Appendix A):
//! [`validate_wiring`] encodes those single-cue field-relation rules so the generator and the
//! contract stay in lockstep (design doc decision 5). Integration — reading the catalog,
//! applying palettes, per-slot legacy overrides, and materialising cues into
//! `progression_catalog.shared.json` — layers on top of this core and is intentionally not here.
#![allow(dead_code)]

/// Animation presentation mode, derived from `cast_time_ms` + `behavior`. Mirrors the client
/// `SpellAnimationArchetypes.Derive`; drives lifecycle selection for the cast_glow / beam slots.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AnimMode {
    Instant,
    Charged,
    Channel,
}

pub fn derive_anim_mode(cast_time_ms: u64, behavior: &str) -> AnimMode {
    if behavior.eq_ignore_ascii_case("CHANNEL") {
        AnimMode::Channel
    } else if cast_time_ms > 0 {
        AnimMode::Charged
    } else {
        AnimMode::Instant
    }
}

/// The VFX "shape" of a spell's cue set. Derived from `delivery.kind` + `targeting` + a couple
/// of delivery sub-fields. Every `delivery.kind` maps to one of these (design doc B.9).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VfxArchetype {
    Projectile,
    SkyDrop,
    GroundAoe,
    SelfNova,
    Beam,
    TargetHit,
    SelfFx,
    Aura,
}

/// The gameplay facts the VFX archetype derivation reads.
pub struct DeliveryFacts<'a> {
    /// Normalised `delivery.kind` (PROJECTILE / AREA / CHANNEL / INSTANT_BEAM / DIRECT_TARGET / APPLY_STATUS / ...).
    pub kind: &'a str,
    /// Normalised `targeting` (SELF / TARGET / POINT).
    pub targeting: &'a str,
    /// `delivery.sky_origin` present/true — a falling body (METEOR).
    pub has_sky_origin: bool,
    /// A channel that launches projectile bodies (MAGIC_MISSILE / FROZEN_SPLINTERS) rather than a sustained beam.
    pub fires_projectiles: bool,
    /// The area resolves on a delay (`impact_delay_ms` / deferred area) → AREA_IMPACT rather than at SPELL_RELEASE.
    pub deferred: bool,
}

/// Returns `None` for kinds we do not generate VFX for (AURA — deferred; anything unknown).
pub fn derive_vfx_archetype(facts: &DeliveryFacts) -> Option<VfxArchetype> {
    match facts.kind {
        "PROJECTILE" => Some(VfxArchetype::Projectile),
        "CHANNEL" => Some(if facts.fires_projectiles {
            VfxArchetype::Projectile
        } else {
            VfxArchetype::Beam
        }),
        "INSTANT_BEAM" => Some(VfxArchetype::Beam),
        "DIRECT_TARGET" => Some(VfxArchetype::TargetHit),
        "AREA" => Some(if facts.has_sky_origin {
            VfxArchetype::SkyDrop
        } else if facts.targeting == "SELF" {
            VfxArchetype::SelfNova
        } else {
            VfxArchetype::GroundAoe
        }),
        "APPLY_STATUS" | "REMOVE_STATUS" | "CONSUME_STATUS" | "SELF_RESOURCE" => {
            Some(if facts.targeting == "TARGET" {
                VfxArchetype::TargetHit
            } else {
                VfxArchetype::SelfFx
            })
        }
        "AURA" => Some(VfxArchetype::Aura),
        _ => None,
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VfxSlot {
    CastGlow,
    Muzzle,
    ProjectileBody,
    TravelBody,
    Impact,
    Burst,
    Beam,
    SelfFlash,
    Aura,
}

/// The slots an archetype requests by default. `Muzzle` is palette-gated (design doc decision 2:
/// optional per-school, off by default) so it is never requested here — the palette layer adds it
/// only when a school provides a `muzzle` entry.
pub fn requested_slots(archetype: VfxArchetype, mode: AnimMode) -> Vec<VfxSlot> {
    use VfxSlot::*;
    match archetype {
        VfxArchetype::Projectile => vec![CastGlow, ProjectileBody, Impact],
        VfxArchetype::SkyDrop => {
            // A charged sky-drop can show a hand cast-glow while charging; instant has none today.
            if mode == AnimMode::Instant {
                vec![TravelBody, Impact]
            } else {
                vec![CastGlow, TravelBody, Impact]
            }
        }
        VfxArchetype::GroundAoe => vec![Impact],
        VfxArchetype::SelfNova => vec![Burst],
        VfxArchetype::Beam => vec![CastGlow, Beam],
        VfxArchetype::TargetHit => vec![Impact],
        VfxArchetype::SelfFx => vec![SelfFlash],
        VfxArchetype::Aura => vec![Aura],
    }
}

/// Anchor category. `Hand` resolves to the concrete cast hand (LEFT/RIGHT) later, via the same
/// inference the editor uses (E7); the rest map straight to catalog anchor identifiers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Anchor {
    Hand,
    Caster,
    CasterOverhead,
    ImpactPoint,
    AreaOrigin,
    Target,
    Origin,
}

/// How the concrete `duration_ms` is chosen. `Zero` → 0; `PalettePositive` → the palette's
/// positive duration (validators require `> 0` for ONE_SHOT+DURATION).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DurationPolicy {
    Zero,
    PalettePositive,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CueWiring {
    pub trigger: &'static str,
    pub anchor: Anchor,
    pub attach_mode: &'static str,
    pub vfx_role: &'static str,
    pub lifecycle: &'static str,
    pub duration: DurationPolicy,
    pub projectile_sequence_index: Option<u32>,
}

/// The Appendix-B wiring for one `(archetype, slot)` given the animation mode, whether the
/// palette entry is a self-terminating particle system, and whether the delivery is deferred.
pub fn wire(
    archetype: VfxArchetype,
    slot: VfxSlot,
    mode: AnimMode,
    self_terminating: bool,
    deferred: bool,
) -> CueWiring {
    let one_shot_lifecycle = if self_terminating {
        "PARTICLE_SYSTEM"
    } else {
        "DURATION"
    };
    let one_shot_duration = if self_terminating {
        DurationPolicy::Zero
    } else {
        DurationPolicy::PalettePositive
    };

    match slot {
        // B.1
        VfxSlot::CastGlow => CueWiring {
            trigger: "SPELL_CAST",
            anchor: Anchor::Hand,
            attach_mode: "FOLLOW_ANCHOR",
            vfx_role: "ATTACHED",
            lifecycle: match mode {
                AnimMode::Instant => "DURATION",
                AnimMode::Charged => "UNTIL_RELEASE_EVENT",
                AnimMode::Channel => "UNTIL_CAST_END",
            },
            duration: match mode {
                AnimMode::Instant => DurationPolicy::PalettePositive,
                _ => DurationPolicy::Zero,
            },
            projectile_sequence_index: None,
        },
        // B.2
        VfxSlot::Muzzle => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Hand,
            attach_mode: "SPAWN_WORLD",
            vfx_role: "ONE_SHOT",
            lifecycle: one_shot_lifecycle,
            duration: one_shot_duration,
            projectile_sequence_index: None,
        },
        // B.3
        VfxSlot::ProjectileBody => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Hand,
            attach_mode: "SPAWN_WORLD",
            vfx_role: "PROJECTILE_BODY",
            lifecycle: "UNTIL_TERMINAL_EVENT",
            duration: DurationPolicy::Zero,
            projectile_sequence_index: Some(0),
        },
        // B.4
        VfxSlot::TravelBody => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Origin,
            attach_mode: "SPAWN_WORLD",
            vfx_role: "TRAVEL_BODY",
            lifecycle: "UNTIL_TERMINAL_EVENT",
            duration: DurationPolicy::Zero,
            projectile_sequence_index: None,
        },
        // B.5
        VfxSlot::Impact => {
            let (trigger, anchor) = match archetype {
                VfxArchetype::TargetHit => ("SPELL_IMPACT", Anchor::Target),
                VfxArchetype::GroundAoe if deferred => ("AREA_IMPACT", Anchor::AreaOrigin),
                VfxArchetype::GroundAoe => ("SPELL_RELEASE", Anchor::ImpactPoint),
                // Projectile / SkyDrop resolve their impact on the projectile/body landing.
                _ => ("SPELL_IMPACT", Anchor::ImpactPoint),
            };
            CueWiring {
                trigger,
                anchor,
                attach_mode: "SPAWN_WORLD",
                vfx_role: "ONE_SHOT",
                lifecycle: one_shot_lifecycle,
                duration: one_shot_duration,
                projectile_sequence_index: None,
            }
        }
        // B.6
        VfxSlot::Burst => CueWiring {
            trigger: if deferred { "AREA_IMPACT" } else { "SPELL_RELEASE" },
            anchor: if deferred {
                Anchor::AreaOrigin
            } else {
                Anchor::Caster
            },
            attach_mode: "SPAWN_WORLD",
            vfx_role: "ONE_SHOT",
            lifecycle: one_shot_lifecycle,
            duration: one_shot_duration,
            projectile_sequence_index: None,
        },
        // B.7
        VfxSlot::Beam => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Hand,
            attach_mode: "FOLLOW_ANCHOR",
            vfx_role: "ATTACHED",
            lifecycle: match mode {
                AnimMode::Channel => "UNTIL_CAST_END",
                _ => "DURATION",
            },
            duration: match mode {
                AnimMode::Channel => DurationPolicy::Zero,
                _ => DurationPolicy::PalettePositive,
            },
            projectile_sequence_index: None,
        },
        // B.8
        VfxSlot::SelfFlash => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Caster,
            attach_mode: "SPAWN_WORLD",
            vfx_role: "ONE_SHOT",
            lifecycle: one_shot_lifecycle,
            duration: one_shot_duration,
            projectile_sequence_index: None,
        },
        // B.8 — sustained caster aura; lives until the buff's StatusEffect row is deleted
        // (decision 11), mirroring UNTIL_CAST_END but keyed on the status rather than the cast.
        VfxSlot::Aura => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Caster,
            attach_mode: "FOLLOW_ANCHOR",
            vfx_role: "ATTACHED",
            lifecycle: "UNTIL_STATUS_END",
            duration: DurationPolicy::Zero,
            projectile_sequence_index: None,
        },
    }
}

fn anchor_is_target(anchor: Anchor) -> bool {
    matches!(anchor, Anchor::Target)
}

/// Encodes the single-cue Class-A authoring rules (Appendix A) that the generator must satisfy.
/// Every wiring [`wire`] emits must pass this — it is how "correct by construction" is proven,
/// and the intended shared source of truth with the server/editor validators (decision 5).
pub fn validate_wiring(w: &CueWiring, mode: AnimMode) -> Result<(), String> {
    let zero_duration = w.duration == DurationPolicy::Zero;

    // Rule 9 — UNTIL_RELEASE_EVENT only on SPELL_CAST.
    if w.lifecycle == "UNTIL_RELEASE_EVENT" && w.trigger != "SPELL_CAST" {
        return Err(format!("UNTIL_RELEASE_EVENT on non-SPELL_CAST trigger '{}'", w.trigger));
    }
    // Rule 10 — PARTICLE_SYSTEM only for ONE_SHOT, duration 0.
    if w.lifecycle == "PARTICLE_SYSTEM" && (w.vfx_role != "ONE_SHOT" || !zero_duration) {
        return Err(format!(
            "PARTICLE_SYSTEM requires ONE_SHOT + duration 0 (role '{}', zero_duration {})",
            w.vfx_role, zero_duration
        ));
    }
    // Rule 11 — hand-attached cast-time SPELL_CAST cue must use UNTIL_RELEASE_EVENT.
    if w.trigger == "SPELL_CAST"
        && w.attach_mode == "FOLLOW_ANCHOR"
        && w.vfx_role == "ATTACHED"
        && matches!(w.anchor, Anchor::Hand)
        && mode == AnimMode::Charged
        && w.lifecycle != "UNTIL_RELEASE_EVENT"
    {
        return Err(format!(
            "cast-time hand SPELL_CAST cue must be UNTIL_RELEASE_EVENT, got '{}'",
            w.lifecycle
        ));
    }
    // Rule 12 — PROJECTILE_BODY.
    if w.vfx_role == "PROJECTILE_BODY" {
        if w.trigger != "SPELL_RELEASE" {
            return Err("PROJECTILE_BODY outside SPELL_RELEASE".into());
        }
        if w.attach_mode == "FOLLOW_ANCHOR" {
            return Err("PROJECTILE_BODY must not FOLLOW_ANCHOR".into());
        }
        if w.projectile_sequence_index.is_none() {
            return Err("PROJECTILE_BODY needs a projectile_sequence_index".into());
        }
    }
    // Rule 13 — TRAVEL_BODY.
    if w.vfx_role == "TRAVEL_BODY" {
        if w.trigger != "SPELL_RELEASE" {
            return Err("TRAVEL_BODY outside SPELL_RELEASE".into());
        }
        if w.attach_mode == "FOLLOW_ANCHOR" {
            return Err("TRAVEL_BODY must not FOLLOW_ANCHOR".into());
        }
        if w.lifecycle != "UNTIL_TERMINAL_EVENT" {
            return Err("TRAVEL_BODY must use UNTIL_TERMINAL_EVENT".into());
        }
        if !zero_duration {
            return Err("TRAVEL_BODY must set duration 0".into());
        }
    }
    // Rule 14 — ONE_SHOT + DURATION needs a positive duration.
    if w.vfx_role == "ONE_SHOT" && w.lifecycle == "DURATION" && zero_duration {
        return Err("ONE_SHOT DURATION must define positive duration_ms".into());
    }
    // Rule 15 — TARGET anchor only valid post-impact.
    if anchor_is_target(w.anchor) && (w.trigger == "SPELL_CAST" || w.trigger == "SPELL_RELEASE") {
        return Err(format!("TARGET anchor invalid on '{}'", w.trigger));
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    // ----- animation mode derivation -----

    #[test]
    fn anim_mode_matches_current_spells() {
        assert_eq!(derive_anim_mode(0, "CHANNEL"), AnimMode::Channel); // ELECTROCUTE / MAGIC_MISSILE
        assert_eq!(derive_anim_mode(0, ""), AnimMode::Instant); // FIREBALL / most spells
        assert_eq!(derive_anim_mode(750, ""), AnimMode::Charged); // METEOR
        assert_eq!(derive_anim_mode(1200, "INSTANT_BEAM"), AnimMode::Charged); // SPELL_INSTANT_BEAM
        assert_eq!(derive_anim_mode(2000, ""), AnimMode::Charged); // GLACIAL_SPIKE
    }

    // ----- archetype derivation, grounded in the real spell list -----

    fn facts<'a>(kind: &'a str, targeting: &'a str) -> DeliveryFacts<'a> {
        DeliveryFacts {
            kind,
            targeting,
            has_sky_origin: false,
            fires_projectiles: false,
            deferred: false,
        }
    }

    #[test]
    fn archetype_derivation_covers_the_catalog() {
        assert_eq!(derive_vfx_archetype(&facts("PROJECTILE", "TARGET")), Some(VfxArchetype::Projectile)); // FIREBALL
        assert_eq!(derive_vfx_archetype(&facts("DIRECT_TARGET", "TARGET")), Some(VfxArchetype::TargetHit)); // GLACIAL_SPIKE
        assert_eq!(derive_vfx_archetype(&facts("INSTANT_BEAM", "TARGET")), Some(VfxArchetype::Beam)); // SPELL_INSTANT_BEAM
        assert_eq!(derive_vfx_archetype(&facts("AREA", "POINT")), Some(VfxArchetype::GroundAoe)); // LIGHTNING
        assert_eq!(derive_vfx_archetype(&facts("AREA", "SELF")), Some(VfxArchetype::SelfNova)); // FROST_NOVA
        assert_eq!(derive_vfx_archetype(&facts("APPLY_STATUS", "SELF")), Some(VfxArchetype::SelfFx)); // BLINDING_LIGHT
        assert_eq!(derive_vfx_archetype(&facts("APPLY_STATUS", "TARGET")), Some(VfxArchetype::TargetHit)); // PALADIN_SACRED_FLAME

        // METEOR: AREA + sky origin -> SkyDrop, not GroundAoe.
        let mut meteor = facts("AREA", "POINT");
        meteor.has_sky_origin = true;
        assert_eq!(derive_vfx_archetype(&meteor), Some(VfxArchetype::SkyDrop));

        // CHANNEL splits: ELECTROCUTE (no projectiles) is a beam; MAGIC_MISSILE (projectiles) is projectile.
        assert_eq!(derive_vfx_archetype(&facts("CHANNEL", "TARGET")), Some(VfxArchetype::Beam));
        let mut mm = facts("CHANNEL", "TARGET");
        mm.fires_projectiles = true;
        assert_eq!(derive_vfx_archetype(&mm), Some(VfxArchetype::Projectile));

        // AURA is supported (decision 11): all spell types generate.
        assert_eq!(derive_vfx_archetype(&facts("AURA", "SELF")), Some(VfxArchetype::Aura)); // PALADIN_FERVOR
    }

    #[test]
    fn aura_uses_until_status_end_lifecycle() {
        // Decision 11: an aura's visual lives until the buff's StatusEffect row is deleted.
        let w = wire(VfxArchetype::Aura, VfxSlot::Aura, AnimMode::Instant, false, false);
        assert_eq!(w.trigger, "SPELL_RELEASE");
        assert_eq!(w.anchor, Anchor::Caster);
        assert_eq!(w.attach_mode, "FOLLOW_ANCHOR");
        assert_eq!(w.vfx_role, "ATTACHED");
        assert_eq!(w.lifecycle, "UNTIL_STATUS_END");
        assert_eq!(w.duration, DurationPolicy::Zero);
    }

    // ----- cast_glow lifecycle is exactly the current FIREBALL/ICICLE/MAGIC_MISSILE trio -----

    #[test]
    fn cast_glow_lifecycle_is_mode_driven() {
        let instant = wire(VfxArchetype::Projectile, VfxSlot::CastGlow, AnimMode::Instant, false, false);
        assert_eq!(instant.lifecycle, "DURATION"); // FIREBALL 350
        assert_eq!(instant.duration, DurationPolicy::PalettePositive);

        let charged = wire(VfxArchetype::Projectile, VfxSlot::CastGlow, AnimMode::Charged, false, false);
        assert_eq!(charged.lifecycle, "UNTIL_RELEASE_EVENT"); // ICICLE (Rule 11 forces this)
        assert_eq!(charged.duration, DurationPolicy::Zero);

        let channel = wire(VfxArchetype::Projectile, VfxSlot::CastGlow, AnimMode::Channel, false, false);
        assert_eq!(channel.lifecycle, "UNTIL_CAST_END"); // MAGIC_MISSILE
        assert_eq!(channel.duration, DurationPolicy::Zero);
    }

    #[test]
    fn projectile_body_and_travel_body_wiring() {
        let body = wire(VfxArchetype::Projectile, VfxSlot::ProjectileBody, AnimMode::Instant, false, false);
        assert_eq!(body.trigger, "SPELL_RELEASE");
        assert_eq!(body.vfx_role, "PROJECTILE_BODY");
        assert_eq!(body.lifecycle, "UNTIL_TERMINAL_EVENT");
        assert_eq!(body.projectile_sequence_index, Some(0));
        assert_eq!(body.duration, DurationPolicy::Zero);

        let travel = wire(VfxArchetype::SkyDrop, VfxSlot::TravelBody, AnimMode::Charged, false, false);
        assert_eq!(travel.vfx_role, "TRAVEL_BODY");
        assert_eq!(travel.lifecycle, "UNTIL_TERMINAL_EVENT");
        assert_eq!(travel.anchor, Anchor::Origin);
        assert_eq!(travel.duration, DurationPolicy::Zero);
    }

    #[test]
    fn ground_aoe_impact_switches_on_deferred() {
        let instant = wire(VfxArchetype::GroundAoe, VfxSlot::Impact, AnimMode::Instant, false, false);
        assert_eq!(instant.trigger, "SPELL_RELEASE"); // LIGHTNING
        assert_eq!(instant.anchor, Anchor::ImpactPoint);

        let deferred = wire(VfxArchetype::GroundAoe, VfxSlot::Impact, AnimMode::Instant, true, true);
        assert_eq!(deferred.trigger, "AREA_IMPACT"); // ICE_SPIKES-style delayed area
        assert_eq!(deferred.anchor, Anchor::AreaOrigin);
    }

    #[test]
    fn target_hit_impact_uses_spell_impact_target_anchor() {
        // Rule 15: TARGET anchor is only legal because the trigger is SPELL_IMPACT (post-impact).
        let hit = wire(VfxArchetype::TargetHit, VfxSlot::Impact, AnimMode::Charged, true, false);
        assert_eq!(hit.trigger, "SPELL_IMPACT"); // GLACIAL_SPIKE
        assert_eq!(hit.anchor, Anchor::Target);
    }

    #[test]
    fn beam_lifecycle_channel_is_until_cast_end() {
        // Decision 8: channel beam ends on ActiveCast-delete.
        let channel = wire(VfxArchetype::Beam, VfxSlot::Beam, AnimMode::Channel, false, false);
        assert_eq!(channel.lifecycle, "UNTIL_CAST_END");
        let charged = wire(VfxArchetype::Beam, VfxSlot::Beam, AnimMode::Charged, false, false);
        assert_eq!(charged.lifecycle, "DURATION"); // INSTANT_BEAM 500
    }

    // ----- the whole generator is correct-by-construction against Class-A rules -----

    #[test]
    fn every_generated_wiring_passes_class_a_rules() {
        let archetypes = [
            VfxArchetype::Projectile,
            VfxArchetype::SkyDrop,
            VfxArchetype::GroundAoe,
            VfxArchetype::SelfNova,
            VfxArchetype::Beam,
            VfxArchetype::TargetHit,
            VfxArchetype::SelfFx,
            VfxArchetype::Aura,
        ];
        let modes = [AnimMode::Instant, AnimMode::Charged, AnimMode::Channel];

        for archetype in archetypes {
            for mode in modes {
                for &self_terminating in &[false, true] {
                    for &deferred in &[false, true] {
                        for slot in requested_slots(archetype, mode) {
                            let w = wire(archetype, slot, mode, self_terminating, deferred);
                            validate_wiring(&w, mode).unwrap_or_else(|e| {
                                panic!(
                                    "generated wiring for {:?}/{:?} mode {:?} (self_term {}, deferred {}) violates a Class-A rule: {}",
                                    archetype, slot, mode, self_terminating, deferred, e
                                )
                            });
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn validator_rejects_a_deliberately_illegal_wiring() {
        // Sanity: the checker is not vacuous. PROJECTILE_BODY on SPELL_CAST must fail (Rule 12).
        let bad = CueWiring {
            trigger: "SPELL_CAST",
            anchor: Anchor::Hand,
            attach_mode: "SPAWN_WORLD",
            vfx_role: "PROJECTILE_BODY",
            lifecycle: "UNTIL_TERMINAL_EVENT",
            duration: DurationPolicy::Zero,
            projectile_sequence_index: Some(0),
        };
        assert!(validate_wiring(&bad, AnimMode::Instant).is_err());
    }
}
