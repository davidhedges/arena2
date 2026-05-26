use std::time::Duration;

use spacetimedb::{Identity, ReducerContext, Table, Timestamp};

#[cfg(feature = "spellcasting_terminal_harness")]
use crate::progression::default_global_cooldown_ms;

use super::{GlobalCooldown, SpellCooldown, SpellId};

#[allow(unused_imports)]
use crate::spells::global_cooldown as _;
#[allow(unused_imports)]
use crate::spells::spell_cooldown as _;

struct CooldownKey {
    caster: Identity,
    kind: String,
}

impl CooldownKey {
    fn new(caster: Identity, kind: &str) -> Self {
        Self {
            caster,
            kind: kind.to_string(),
        }
    }

    fn as_key_string(&self) -> String {
        format!("{}:{}", self.caster.to_hex(), self.kind)
    }
}

pub(crate) fn is_on_named_cooldown(
    ctx: &ReducerContext,
    caster: Identity,
    kind: &str,
    now: Timestamp,
) -> bool {
    let key = CooldownKey::new(caster, kind).as_key_string();
    let Some(cooldown) = ctx.db.spell_cooldown().key().find(key) else {
        return false;
    };
    now < cooldown.last_cast_at + Duration::from_millis(cooldown.duration_ms.max(1))
}

pub(super) fn is_on_cooldown(
    ctx: &ReducerContext,
    caster: Identity,
    kind: &SpellId,
    now: Timestamp,
) -> bool {
    is_on_named_cooldown(ctx, caster, kind.as_str(), now)
}

pub(super) fn stamp_cooldown(
    ctx: &ReducerContext,
    caster: Identity,
    kind: &SpellId,
    now: Timestamp,
) {
    let Some(definition) = super::catalog::spell_definition(kind) else {
        return;
    };
    stamp_named_cooldown_for_duration(ctx, caster, kind.as_str(), definition.cooldown, now);
}

pub(crate) fn stamp_named_cooldown_for_duration(
    ctx: &ReducerContext,
    caster: Identity,
    kind: &str,
    duration: Duration,
    now: Timestamp,
) {
    let key = CooldownKey::new(caster, kind).as_key_string();
    if let Some(mut cooldown) = ctx.db.spell_cooldown().key().find(key.clone()) {
        cooldown.last_cast_at = now;
        cooldown.duration_ms = duration.as_millis().max(1) as u64;
        cooldown.kind = kind.to_string();
        ctx.db.spell_cooldown().key().update(cooldown);
        return;
    }

    ctx.db.spell_cooldown().insert(SpellCooldown {
        key,
        caster,
        kind: kind.to_string(),
        last_cast_at: now,
        duration_ms: duration.as_millis().max(1) as u64,
    });
}

pub(crate) fn is_on_global_cooldown(
    ctx: &ReducerContext,
    caster: Identity,
    now: Timestamp,
) -> bool {
    let Some(gcd) = ctx.db.global_cooldown().caster().find(caster) else {
        return false;
    };
    now < gcd.started_at + Duration::from_millis(gcd.duration_ms.max(1))
}

#[cfg(feature = "spellcasting_terminal_harness")]
pub(crate) fn stamp_global_cooldown(ctx: &ReducerContext, caster: Identity, now: Timestamp) {
    // Cast-time spells stamp GCD with the same `now` passed to begin_active_cast.
    // Premature self-cancel refunds rely on that timestamp pairing as the match key.
    stamp_global_cooldown_for_duration(ctx, caster, default_global_cooldown_duration(), now);
}

pub(crate) fn stamp_global_cooldown_for_duration(
    ctx: &ReducerContext,
    caster: Identity,
    duration: Duration,
    now: Timestamp,
) {
    let duration_ms = duration.as_millis().max(1) as u64;
    if let Some(mut gcd) = ctx.db.global_cooldown().caster().find(caster) {
        gcd.started_at = now;
        gcd.duration_ms = duration_ms;
        ctx.db.global_cooldown().caster().update(gcd);
        return;
    }

    ctx.db.global_cooldown().insert(GlobalCooldown {
        caster,
        started_at: now,
        duration_ms,
    });
}

pub(crate) fn clear_global_cooldown_if_matches(
    ctx: &ReducerContext,
    caster: Identity,
    started_at: Timestamp,
) {
    let Some(gcd) = ctx.db.global_cooldown().caster().find(caster) else {
        return;
    };
    if global_cooldown_started_at_matches(Some(&gcd), started_at) {
        ctx.db.global_cooldown().caster().delete(caster);
    }
}

#[cfg(feature = "spellcasting_terminal_harness")]
fn default_global_cooldown_duration() -> Duration {
    Duration::from_millis(default_global_cooldown_ms())
}

fn global_cooldown_started_at_matches(gcd: Option<&GlobalCooldown>, started_at: Timestamp) -> bool {
    gcd.is_some_and(|gcd| gcd.started_at == started_at)
}

#[cfg(test)]
mod tests {
    use super::{global_cooldown_started_at_matches, GlobalCooldown};
    use spacetimedb::{Identity, Timestamp};
    use std::time::Duration;

    #[test]
    fn global_cooldown_started_at_match_guard_handles_match_mismatch_and_absent_row() {
        let started_at = Timestamp::UNIX_EPOCH + Duration::from_millis(500);
        let row = GlobalCooldown {
            caster: Identity::ZERO,
            started_at,
            duration_ms: 1500,
        };

        assert!(global_cooldown_started_at_matches(Some(&row), started_at));
        assert!(!global_cooldown_started_at_matches(
            Some(&row),
            started_at + Duration::from_millis(1)
        ));
        assert!(!global_cooldown_started_at_matches(None, started_at));
    }
}
