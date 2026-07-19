//! Server-side Class-A field-relation checks for authored combat VFX cue rows.
//!
//! The editor owns cue generation now. This module remains as the Rust validator's shared
//! rule source for single-cue field relationships that can be checked without catalog context.

/// The normalised single-cue fields the Class-A field-relation rules read.
/// `role`/`lifecycle` must already be the effective values (`""` normalised to
/// `ONE_SHOT` / `DURATION`).
pub struct CueFields<'a> {
    pub trigger: &'a str,
    pub anchor: &'a str,
    pub attach_mode: &'a str,
    /// Effective `vfx_role` (never `""`).
    pub role: &'a str,
    /// Effective `lifecycle` (never `""`).
    pub lifecycle: &'a str,
    pub duration_is_zero: bool,
    /// The owning spell is charged (`cast_time_ms > 0`) — arms the hand-glow rule.
    pub charged_cast: bool,
}

/// A single-cue Class-A field-relation violation. The server renders richer catalog-error
/// text from these variants in `progression.rs`.
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
    ProjectileTrailOffRelease,
    ProjectileTrailFollowAnchor,
    ProjectileTrailBadLifecycle,
    ProjectileTrailNonZeroDuration,
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
    /// Rule 16 — world-spawned per-target terminal cues use `IMPACT_POINT`;
    /// `TARGET` is reserved for transform-following effects.
    WorldImpactTargetAnchor,
}

const HAND_ANCHORS: [&str; 2] = ["LEFT_HAND", "RIGHT_HAND"];
const POINT_IMPACT_TRIGGERS: [&str; 6] = [
    "MELEE_IMPACT",
    "MELEE_BLOCK",
    "MELEE_PARRY",
    "SPELL_IMPACT",
    "SPELL_BLOCK",
    "SPELL_PARRY",
];

/// The single source of truth for Class-A single-cue field-relation rules. Rules that
/// need catalog context (enum allow-lists, owner resolution, projectile ownership/count,
/// `start_delay_ms`, `hit_index`) stay with the server catalog validator.
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
    // Rule 12c-f — an optional trail is a second visual on the same authoritative projectile.
    if f.role == "PROJECTILE_TRAIL" {
        if f.trigger != "SPELL_RELEASE" {
            violations.push(ProjectileTrailOffRelease);
        }
        if f.attach_mode == "FOLLOW_ANCHOR" {
            violations.push(ProjectileTrailFollowAnchor);
        }
        if f.lifecycle != "UNTIL_TERMINAL_EVENT" {
            violations.push(ProjectileTrailBadLifecycle);
        }
        if !f.duration_is_zero {
            violations.push(ProjectileTrailNonZeroDuration);
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
    // Rule 16 — a detached world-space impact belongs at the event's resolved hit point.
    // TARGET remains valid for FOLLOW_ANCHOR effects that intentionally track an entity.
    if f.anchor == "TARGET"
        && matches!(f.attach_mode, "" | "SPAWN_WORLD")
        && POINT_IMPACT_TRIGGERS.contains(&f.trigger)
    {
        violations.push(WorldImpactTargetAnchor);
    }

    violations
}

#[cfg(test)]
mod tests {
    use super::*;

    fn legal_one_shot() -> CueFields<'static> {
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
        f.trigger = "SPELL_CAST";
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
        let charged = CueFields {
            trigger: "SPELL_CAST",
            anchor: "LEFT_HAND",
            attach_mode: "FOLLOW_ANCHOR",
            role: "ATTACHED",
            lifecycle: "DURATION",
            duration_is_zero: false,
            charged_cast: true,
        };
        assert!(check_cue_field_rules(&charged)
            .contains(&CueFieldViolation::CastTimeHandGlowNotUntilRelease));

        let instant = CueFields {
            charged_cast: false,
            ..charged
        };
        assert!(!check_cue_field_rules(&instant)
            .contains(&CueFieldViolation::CastTimeHandGlowNotUntilRelease));
    }

    #[test]
    fn rule12_projectile_body_field_legality() {
        let mut f = legal_one_shot();
        f.role = "PROJECTILE_BODY";
        f.trigger = "SPELL_CAST";
        f.attach_mode = "FOLLOW_ANCHOR";
        f.lifecycle = "UNTIL_TERMINAL_EVENT";
        let v = check_cue_field_rules(&f);
        assert!(v.contains(&CueFieldViolation::ProjectileBodyOffRelease));
        assert!(v.contains(&CueFieldViolation::ProjectileBodyFollowAnchor));
    }

    #[test]
    fn rule12_projectile_trail_field_legality() {
        let mut f = legal_one_shot();
        f.role = "PROJECTILE_TRAIL";
        f.trigger = "SPELL_CAST";
        f.attach_mode = "FOLLOW_ANCHOR";
        f.lifecycle = "DURATION";
        f.duration_is_zero = false;
        let v = check_cue_field_rules(&f);
        assert!(v.contains(&CueFieldViolation::ProjectileTrailOffRelease));
        assert!(v.contains(&CueFieldViolation::ProjectileTrailFollowAnchor));
        assert!(v.contains(&CueFieldViolation::ProjectileTrailBadLifecycle));
        assert!(v.contains(&CueFieldViolation::ProjectileTrailNonZeroDuration));
    }

    #[test]
    fn rule13_travel_body_field_legality() {
        let mut f = legal_one_shot();
        f.role = "TRAVEL_BODY";
        f.trigger = "SPELL_IMPACT";
        f.attach_mode = "FOLLOW_ANCHOR";
        f.lifecycle = "DURATION";
        f.duration_is_zero = false;
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
        f.trigger = "SPELL_RELEASE";
        assert!(check_cue_field_rules(&f).contains(&CueFieldViolation::TargetAnchorPreImpact));
        f.trigger = "SPELL_IMPACT";
        assert!(!check_cue_field_rules(&f).contains(&CueFieldViolation::TargetAnchorPreImpact));
    }

    #[test]
    fn rule16_world_impacts_default_to_impact_point() {
        let mut f = legal_one_shot();
        f.anchor = "TARGET";
        assert!(check_cue_field_rules(&f).contains(&CueFieldViolation::WorldImpactTargetAnchor));

        f.anchor = "IMPACT_POINT";
        assert!(!check_cue_field_rules(&f).contains(&CueFieldViolation::WorldImpactTargetAnchor));

        f.anchor = "TARGET";
        f.attach_mode = "FOLLOW_ANCHOR";
        assert!(!check_cue_field_rules(&f).contains(&CueFieldViolation::WorldImpactTargetAnchor));
    }
}
