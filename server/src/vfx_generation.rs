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
    /// Brief one-shot burst at the caster's feet — the common aura visual.
    AuraGround,
    /// Sustained caster-attached aura glow (opt-in) — lives until the aura ends.
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
        // Most auras are a brief ground flourish and nothing else (no cast glow / muzzle /
        // projectile / animation). The sustained `Aura` glow is opt-in — the palette fills it
        // only for schools that provide a persistent aura prefab.
        VfxArchetype::Aura => vec![AuraGround, Aura],
    }
}

/// Anchor category. `Hand` resolves to the concrete cast hand (LEFT/RIGHT) later, via the same
/// inference the editor uses (E7); the rest map straight to catalog anchor identifiers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Anchor {
    Hand,
    Caster,
    CasterOverhead,
    GroundUnderCaster,
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
        // B.8 — the common aura visual: a one-shot flourish at the caster's feet that follows
        // them while it plays (an aura is attached to the caster, not planted in the world).
        // FOLLOW_ANCHOR requires a real transform, so anchor on CASTER (the presentation root),
        // not GROUND_UNDER_CASTER (a computed point). The prefab must be ground-pivoted.
        VfxSlot::AuraGround => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Caster,
            attach_mode: "FOLLOW_ANCHOR",
            vfx_role: "ONE_SHOT",
            lifecycle: one_shot_lifecycle,
            duration: one_shot_duration,
            projectile_sequence_index: None,
        },
        // B.8 — sustained caster aura (opt-in); lives until the aura's ActiveAura row is deleted
        // (decision 11), mirroring UNTIL_CAST_END but keyed on the aura rather than the cast.
        VfxSlot::Aura => CueWiring {
            trigger: "SPELL_RELEASE",
            anchor: Anchor::Caster,
            attach_mode: "FOLLOW_ANCHOR",
            vfx_role: "ATTACHED",
            lifecycle: "UNTIL_AURA_END",
            duration: DurationPolicy::Zero,
            projectile_sequence_index: None,
        },
    }
}

impl Anchor {
    /// The catalog anchor identifier this resolves to for validation. `Hand` is abstract (the
    /// concrete cast hand is inferred later, E7); it maps to a hand anchor so the hand-relative
    /// field rules (Rule 11 / 15) evaluate identically to an already-resolved LEFT/RIGHT_HAND cue.
    fn as_catalog_str(self) -> &'static str {
        match self {
            Anchor::Hand => "LEFT_HAND",
            Anchor::Caster => "CASTER",
            Anchor::CasterOverhead => "CASTER_OVERHEAD",
            Anchor::GroundUnderCaster => "GROUND_UNDER_CASTER",
            Anchor::ImpactPoint => "IMPACT_POINT",
            Anchor::AreaOrigin => "AREA_ORIGIN",
            Anchor::Target => "TARGET",
            Anchor::Origin => "ORIGIN",
        }
    }
}

/// The normalised single-cue fields the Class-A field-relation rules read. Both consumers build
/// one: the generator from a [`CueWiring`], the server catalog validator from a catalog row.
/// `role`/`lifecycle` must already be the *effective* values (`""` normalised to `ONE_SHOT` /
/// `DURATION`) so both callers apply byte-identical predicates.
pub struct CueFields<'a> {
    pub trigger: &'a str,
    pub anchor: &'a str,
    pub attach_mode: &'a str,
    /// Effective `vfx_role` (never `""`).
    pub role: &'a str,
    /// Effective `lifecycle` (never `""`).
    pub lifecycle: &'a str,
    pub duration_is_zero: bool,
    /// The owning spell is charged (`cast_time_ms > 0`) — arms the hand-glow rule (Rule 11).
    pub charged_cast: bool,
}

/// A single-cue Class-A field-relation violation. Rendered to a message by each consumer, so the
/// server keeps its existing catalog-error text and the generator keeps a terse diagnostic.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CueFieldViolation {
    /// Rule 9 — `UNTIL_RELEASE_EVENT` only on `SPELL_CAST`.
    UntilReleaseEventOffCast,
    /// Rule 10a — `PARTICLE_SYSTEM` only for `ONE_SHOT`.
    ParticleSystemBadRole,
    /// Rule 10b — `PARTICLE_SYSTEM` requires `duration_ms == 0`.
    ParticleSystemNonZeroDuration,
    /// Rule 11 — hand-attached cast-time `SPELL_CAST` cue must be `UNTIL_RELEASE_EVENT`.
    CastTimeHandGlowNotUntilRelease,
    /// Rule 12a — `PROJECTILE_BODY` only on `SPELL_RELEASE`.
    ProjectileBodyOffRelease,
    /// Rule 12b — `PROJECTILE_BODY` must not `FOLLOW_ANCHOR`.
    ProjectileBodyFollowAnchor,
    /// Rule 13a — `TRAVEL_BODY` only on `SPELL_RELEASE`.
    TravelBodyOffRelease,
    /// Rule 13b — `TRAVEL_BODY` must not `FOLLOW_ANCHOR`.
    TravelBodyFollowAnchor,
    /// Rule 13c — `TRAVEL_BODY` must use `UNTIL_TERMINAL_EVENT`.
    TravelBodyBadLifecycle,
    /// Rule 13d — `TRAVEL_BODY` must set `duration_ms == 0`.
    TravelBodyNonZeroDuration,
    /// Rule 14 — `ONE_SHOT` + `DURATION` needs a positive `duration_ms`.
    OneShotDurationZero,
    /// Rule 15 — `TARGET` anchor invalid on `SPELL_CAST` / `SPELL_RELEASE`.
    TargetAnchorPreImpact,
}

impl CueFieldViolation {
    /// A terse, context-free description — used by the generator's self-check diagnostics. The
    /// server renders its own richer catalog-error text from the same variants.
    pub fn describe(self) -> &'static str {
        match self {
            CueFieldViolation::UntilReleaseEventOffCast => {
                "UNTIL_RELEASE_EVENT on non-SPELL_CAST trigger"
            }
            CueFieldViolation::ParticleSystemBadRole => "PARTICLE_SYSTEM requires ONE_SHOT role",
            CueFieldViolation::ParticleSystemNonZeroDuration => {
                "PARTICLE_SYSTEM requires duration 0"
            }
            CueFieldViolation::CastTimeHandGlowNotUntilRelease => {
                "cast-time hand SPELL_CAST cue must be UNTIL_RELEASE_EVENT"
            }
            CueFieldViolation::ProjectileBodyOffRelease => "PROJECTILE_BODY outside SPELL_RELEASE",
            CueFieldViolation::ProjectileBodyFollowAnchor => "PROJECTILE_BODY must not FOLLOW_ANCHOR",
            CueFieldViolation::TravelBodyOffRelease => "TRAVEL_BODY outside SPELL_RELEASE",
            CueFieldViolation::TravelBodyFollowAnchor => "TRAVEL_BODY must not FOLLOW_ANCHOR",
            CueFieldViolation::TravelBodyBadLifecycle => "TRAVEL_BODY must use UNTIL_TERMINAL_EVENT",
            CueFieldViolation::TravelBodyNonZeroDuration => "TRAVEL_BODY must set duration 0",
            CueFieldViolation::OneShotDurationZero => {
                "ONE_SHOT DURATION must define positive duration_ms"
            }
            CueFieldViolation::TargetAnchorPreImpact => "TARGET anchor invalid on cast/release trigger",
        }
    }
}

const HAND_ANCHORS: [&str; 2] = ["LEFT_HAND", "RIGHT_HAND"];

/// The single source of truth for the Class-A single-cue field-relation rules (Appendix A). Both
/// the generator's [`validate_wiring`] and the server's catalog-load validator call this, so the
/// rules can never silently diverge between the two paths (design doc decisions 5 / 10). Rules
/// that need catalog context (enum allow-lists, owner resolution, projectile ownership/count,
/// `start_delay_ms`, `hit_index`) stay with the server validator — they are not shareable here.
pub fn check_cue_field_rules(f: &CueFields) -> Vec<CueFieldViolation> {
    use CueFieldViolation::*;
    let mut violations = Vec::new();

    // Rule 9 — UNTIL_RELEASE_EVENT only on SPELL_CAST.
    if f.lifecycle == "UNTIL_RELEASE_EVENT" && f.trigger != "SPELL_CAST" {
        violations.push(UntilReleaseEventOffCast);
    }
    // Rule 10 — PARTICLE_SYSTEM only for ONE_SHOT prefab cues, duration 0.
    if f.lifecycle == "PARTICLE_SYSTEM" {
        if f.role != "ONE_SHOT" {
            violations.push(ParticleSystemBadRole);
        }
        if !f.duration_is_zero {
            violations.push(ParticleSystemNonZeroDuration);
        }
    }
    // Rule 11 — hand-attached cast-time SPELL_CAST cue must use UNTIL_RELEASE_EVENT.
    if f.charged_cast
        && f.trigger == "SPELL_CAST"
        && f.attach_mode == "FOLLOW_ANCHOR"
        && f.role == "ATTACHED"
        && HAND_ANCHORS.contains(&f.anchor)
        && f.lifecycle != "UNTIL_RELEASE_EVENT"
    {
        violations.push(CastTimeHandGlowNotUntilRelease);
    }
    // Rule 12 — PROJECTILE_BODY field legality (owner/index/start_delay are context, server-side).
    if f.role == "PROJECTILE_BODY" {
        if f.trigger != "SPELL_RELEASE" {
            violations.push(ProjectileBodyOffRelease);
        }
        if f.attach_mode == "FOLLOW_ANCHOR" {
            violations.push(ProjectileBodyFollowAnchor);
        }
    }
    // Rule 13 — TRAVEL_BODY.
    if f.role == "TRAVEL_BODY" {
        if f.trigger != "SPELL_RELEASE" {
            violations.push(TravelBodyOffRelease);
        }
        if f.attach_mode == "FOLLOW_ANCHOR" {
            violations.push(TravelBodyFollowAnchor);
        }
        if f.lifecycle != "UNTIL_TERMINAL_EVENT" {
            violations.push(TravelBodyBadLifecycle);
        }
        if !f.duration_is_zero {
            violations.push(TravelBodyNonZeroDuration);
        }
    }
    // Rule 14 — ONE_SHOT + DURATION needs a positive duration.
    if f.role == "ONE_SHOT" && f.lifecycle == "DURATION" && f.duration_is_zero {
        violations.push(OneShotDurationZero);
    }
    // Rule 15 — TARGET anchor only valid post-impact.
    if f.anchor == "TARGET" && matches!(f.trigger, "SPELL_CAST" | "SPELL_RELEASE") {
        violations.push(TargetAnchorPreImpact);
    }

    violations
}

/// Proves every wiring [`wire`] emits is correct-by-construction against the Class-A rules — it
/// delegates to the shared [`check_cue_field_rules`] (the same checker the server validator runs),
/// so "the generator can only emit legal cues" is enforced by the one source of truth.
pub fn validate_wiring(w: &CueWiring, mode: AnimMode) -> Result<(), String> {
    // A generated CueWiring always carries explicit, non-empty role/lifecycle, so its effective
    // values equal the raw ones. Rule 11 arms on the charged mode (== cast_time > 0 server-side).
    let violations = check_cue_field_rules(&CueFields {
        trigger: w.trigger,
        anchor: w.anchor.as_catalog_str(),
        attach_mode: w.attach_mode,
        role: w.vfx_role,
        lifecycle: w.lifecycle,
        duration_is_zero: w.duration == DurationPolicy::Zero,
        charged_cast: mode == AnimMode::Charged,
    });
    if let Some(violation) = violations.first() {
        return Err(violation.describe().to_string());
    }
    // Generator-local completeness invariant: a PROJECTILE_BODY wiring must carry a sequence index.
    // (The server instead derives/defaults the index from the catalog row, so this is not a shared
    // rule.)
    if w.vfx_role == "PROJECTILE_BODY" && w.projectile_sequence_index.is_none() {
        return Err("PROJECTILE_BODY needs a projectile_sequence_index".into());
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
    fn aura_defaults_to_a_brief_ground_burst() {
        // Most auras (e.g. WARDING_AURA) are just a brief effect at the caster's feet — no cast
        // glow, muzzle, projectile, or animation. AuraGround is requested first; Aura is opt-in.
        let slots = requested_slots(VfxArchetype::Aura, AnimMode::Instant);
        assert_eq!(slots.first(), Some(&VfxSlot::AuraGround));

        let w = wire(VfxArchetype::Aura, VfxSlot::AuraGround, AnimMode::Instant, false, false);
        assert_eq!(w.trigger, "SPELL_RELEASE");
        assert_eq!(w.anchor, Anchor::Caster); // follows the caster (FOLLOW_ANCHOR needs a transform)
        assert_eq!(w.attach_mode, "FOLLOW_ANCHOR");
        assert_eq!(w.vfx_role, "ONE_SHOT");
    }

    #[test]
    fn aura_uses_until_aura_end_lifecycle() {
        // Decision 11: an aura's visual lives until the aura's ActiveAura row is deleted.
        let w = wire(VfxArchetype::Aura, VfxSlot::Aura, AnimMode::Instant, false, false);
        assert_eq!(w.trigger, "SPELL_RELEASE");
        assert_eq!(w.anchor, Anchor::Caster);
        assert_eq!(w.attach_mode, "FOLLOW_ANCHOR");
        assert_eq!(w.vfx_role, "ATTACHED");
        assert_eq!(w.lifecycle, "UNTIL_AURA_END");
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

    // ----- the shared Class-A checker, exercised directly (this is the one source of truth the
    // server catalog contract also consumes, so per-rule coverage lives here) -----

    fn legal_one_shot() -> CueFields<'static> {
        // A stock impact burst: ONE_SHOT / DURATION with a positive duration — violates nothing.
        CueFields {
            trigger: "SPELL_IMPACT",
            anchor: "IMPACT_POINT",
            attach_mode: "SPAWN_WORLD",
            role: "ONE_SHOT",
            lifecycle: "DURATION",
            duration_is_zero: false,
            charged_cast: false,
        }
    }

    #[test]
    fn shared_checker_passes_a_legal_cue() {
        assert!(check_cue_field_rules(&legal_one_shot()).is_empty());
    }

    #[test]
    fn rule9_until_release_event_only_on_spell_cast() {
        let mut f = legal_one_shot();
        f.lifecycle = "UNTIL_RELEASE_EVENT";
        f.trigger = "SPELL_RELEASE";
        assert!(check_cue_field_rules(&f).contains(&CueFieldViolation::UntilReleaseEventOffCast));
        f.trigger = "SPELL_CAST"; // legal here
        assert!(!check_cue_field_rules(&f).contains(&CueFieldViolation::UntilReleaseEventOffCast));
    }

    #[test]
    fn rule10_particle_system_requires_one_shot_zero_duration() {
        let mut f = legal_one_shot();
        f.lifecycle = "PARTICLE_SYSTEM";
        f.role = "ATTACHED";
        f.duration_is_zero = false;
        let v = check_cue_field_rules(&f);
        assert!(v.contains(&CueFieldViolation::ParticleSystemBadRole));
        assert!(v.contains(&CueFieldViolation::ParticleSystemNonZeroDuration));
    }

    #[test]
    fn rule11_fires_only_for_charged_hand_glow() {
        // A hand-attached SPELL_CAST ATTACHED glow that is not UNTIL_RELEASE_EVENT...
        let charged = CueFields {
            trigger: "SPELL_CAST",
            anchor: "LEFT_HAND",
            attach_mode: "FOLLOW_ANCHOR",
            role: "ATTACHED",
            lifecycle: "DURATION",
            duration_is_zero: false,
            charged_cast: true,
        };
        // ...is illegal for a charged spell (must be UNTIL_RELEASE_EVENT).
        assert!(check_cue_field_rules(&charged)
            .contains(&CueFieldViolation::CastTimeHandGlowNotUntilRelease));
        // ...but perfectly legal for an instant spell — FIREBALL's DURATION 350 hand glow.
        let instant = CueFields { charged_cast: false, ..charged };
        assert!(!check_cue_field_rules(&instant)
            .contains(&CueFieldViolation::CastTimeHandGlowNotUntilRelease));
    }

    #[test]
    fn rule12_projectile_body_field_legality() {
        let mut f = legal_one_shot();
        f.role = "PROJECTILE_BODY";
        f.trigger = "SPELL_CAST"; // wrong: must be SPELL_RELEASE
        f.attach_mode = "FOLLOW_ANCHOR"; // wrong: never follows
        f.lifecycle = "UNTIL_TERMINAL_EVENT";
        let v = check_cue_field_rules(&f);
        assert!(v.contains(&CueFieldViolation::ProjectileBodyOffRelease));
        assert!(v.contains(&CueFieldViolation::ProjectileBodyFollowAnchor));
    }

    #[test]
    fn rule13_travel_body_field_legality() {
        let mut f = legal_one_shot();
        f.role = "TRAVEL_BODY";
        f.trigger = "SPELL_IMPACT"; // wrong: must be SPELL_RELEASE
        f.attach_mode = "FOLLOW_ANCHOR"; // wrong: never follows
        f.lifecycle = "DURATION"; // wrong: must be UNTIL_TERMINAL_EVENT
        f.duration_is_zero = false; // wrong: must be 0
        let v = check_cue_field_rules(&f);
        assert!(v.contains(&CueFieldViolation::TravelBodyOffRelease));
        assert!(v.contains(&CueFieldViolation::TravelBodyFollowAnchor));
        assert!(v.contains(&CueFieldViolation::TravelBodyBadLifecycle));
        assert!(v.contains(&CueFieldViolation::TravelBodyNonZeroDuration));
    }

    #[test]
    fn rule14_one_shot_duration_needs_positive_duration() {
        let mut f = legal_one_shot();
        f.duration_is_zero = true;
        assert!(check_cue_field_rules(&f).contains(&CueFieldViolation::OneShotDurationZero));
    }

    #[test]
    fn rule15_target_anchor_only_post_impact() {
        let mut f = legal_one_shot();
        f.anchor = "TARGET";
        f.trigger = "SPELL_RELEASE"; // pre-impact: illegal
        assert!(check_cue_field_rules(&f).contains(&CueFieldViolation::TargetAnchorPreImpact));
        f.trigger = "SPELL_IMPACT"; // post-impact: legal
        assert!(!check_cue_field_rules(&f).contains(&CueFieldViolation::TargetAnchorPreImpact));
    }
}
