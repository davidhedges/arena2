use serde::{Deserialize, Serialize};
use spacetimedb::{Identity, ReducerContext};

use crate::arena::{
    players_share_world_context, ArenaInstance, MATCH_PHASE_COUNTDOWN, MATCH_PHASE_IN_PROGRESS,
};
use crate::party::same_party;
use crate::playground_targets::{playground_target_kind_for_relation, PlaygroundTargetKind};

#[allow(unused_imports)]
use crate::arena::arena_instance as _;
#[allow(unused_imports)]
use crate::arena::player_world as _;
#[allow(unused_imports)]
use crate::player_state::player_state as _;

pub(crate) const TARGET_AUDIENCE_SELF_ONLY: &str = "SELF_ONLY";
pub(crate) const TARGET_AUDIENCE_HOSTILE: &str = "HOSTILE";
pub(crate) const TARGET_AUDIENCE_PARTY_OR_SELF: &str = "PARTY_OR_SELF";
pub(crate) const TARGET_AUDIENCE_ASSISTABLE: &str = "ASSISTABLE";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CombatRelation {
    Self_,
    PartyAlly,
    Neutral,
    Hostile,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub(crate) enum TargetAudience {
    #[serde(rename = "SELF_ONLY")]
    SelfOnly,
    Hostile,
    #[serde(rename = "PARTY_OR_SELF")]
    PartyOrSelf,
    Assistable,
}

impl TargetAudience {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::SelfOnly => TARGET_AUDIENCE_SELF_ONLY,
            Self::Hostile => TARGET_AUDIENCE_HOSTILE,
            Self::PartyOrSelf => TARGET_AUDIENCE_PARTY_OR_SELF,
            Self::Assistable => TARGET_AUDIENCE_ASSISTABLE,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            TARGET_AUDIENCE_SELF_ONLY => Some(Self::SelfOnly),
            TARGET_AUDIENCE_HOSTILE => Some(Self::Hostile),
            TARGET_AUDIENCE_PARTY_OR_SELF => Some(Self::PartyOrSelf),
            TARGET_AUDIENCE_ASSISTABLE => Some(Self::Assistable),
            _ => None,
        }
    }

    pub(crate) fn allows(self, relation: CombatRelation) -> bool {
        match self {
            Self::SelfOnly => relation == CombatRelation::Self_,
            Self::Hostile => relation == CombatRelation::Hostile,
            Self::PartyOrSelf => {
                matches!(relation, CombatRelation::Self_ | CombatRelation::PartyAlly)
            }
            Self::Assistable => {
                matches!(
                    relation,
                    CombatRelation::Self_ | CombatRelation::PartyAlly | CombatRelation::Neutral
                )
            }
        }
    }
}

pub(crate) fn combat_relation(
    ctx: &ReducerContext,
    source: Identity,
    target: Identity,
) -> CombatRelation {
    if source == target {
        return CombatRelation::Self_;
    }

    // Playground-only override for local targeting and party-frame testing. This
    // is not a general faction, NPC, or bot relationship system.
    if let Some(kind) = playground_target_kind_for_relation(ctx, source, target) {
        return match kind {
            PlaygroundTargetKind::Hostile | PlaygroundTargetKind::MobHostile => {
                CombatRelation::Hostile
            }
            PlaygroundTargetKind::Neutral | PlaygroundTargetKind::MobNeutral => {
                CombatRelation::Neutral
            }
            PlaygroundTargetKind::MobFriendly => CombatRelation::PartyAlly,
            PlaygroundTargetKind::PartyMember => {
                if source != Identity::ZERO && same_party(ctx, source, target) {
                    CombatRelation::PartyAlly
                } else {
                    CombatRelation::Neutral
                }
            }
        };
    }

    if target_is_dummy(ctx, target) {
        return CombatRelation::Hostile;
    }

    if source != Identity::ZERO
        && players_share_world_context(ctx, source, target)
        && match_context_makes_hostile(ctx, source, target)
    {
        return CombatRelation::Hostile;
    }

    if source != Identity::ZERO && same_party(ctx, source, target) {
        return CombatRelation::PartyAlly;
    }

    CombatRelation::Neutral
}

pub(crate) fn target_audience_allows(
    ctx: &ReducerContext,
    source: Identity,
    target: Identity,
    audience: TargetAudience,
) -> bool {
    source == Identity::ZERO || audience.allows(combat_relation(ctx, source, target))
}

pub(crate) fn can_harm(ctx: &ReducerContext, source: Identity, target: Identity) -> bool {
    target_audience_allows(ctx, source, target, TargetAudience::Hostile)
}

pub(crate) fn can_apply_status_polarity(
    ctx: &ReducerContext,
    source: Identity,
    target: Identity,
    polarity: crate::combat::StatusPolarity,
    audience: TargetAudience,
) -> bool {
    match polarity {
        crate::combat::StatusPolarity::Debuff => can_harm(ctx, source, target),
        crate::combat::StatusPolarity::Buff => {
            target_audience_allows(ctx, source, target, audience)
        }
    }
}

pub(crate) fn default_spell_target_audience(
    behavior: crate::spells::SpellBehavior,
    targeting: crate::spells::SpellTargeting,
    damage: i32,
    polarity: Option<crate::combat::StatusPolarity>,
) -> TargetAudience {
    if damage > 0 {
        return TargetAudience::Hostile;
    }
    match behavior {
        crate::spells::SpellBehavior::ApplyStatus => match polarity {
            Some(crate::combat::StatusPolarity::Buff)
                if targeting == crate::spells::SpellTargeting::Self_ =>
            {
                TargetAudience::SelfOnly
            }
            Some(crate::combat::StatusPolarity::Buff) => TargetAudience::PartyOrSelf,
            Some(crate::combat::StatusPolarity::Debuff) => TargetAudience::Hostile,
            None => TargetAudience::Hostile,
        },
        crate::spells::SpellBehavior::RemoveStatus => TargetAudience::SelfOnly,
        crate::spells::SpellBehavior::SelfResource => TargetAudience::SelfOnly,
        _ => TargetAudience::Hostile,
    }
}

fn target_is_dummy(ctx: &ReducerContext, target: Identity) -> bool {
    ctx.db
        .player_state()
        .player_id()
        .find(target)
        .is_some_and(|state| state.is_dummy)
}

fn match_context_makes_hostile(ctx: &ReducerContext, source: Identity, target: Identity) -> bool {
    let Some(source_world) = ctx.db.player_world().identity().find(source) else {
        return false;
    };
    let Some(target_world) = ctx.db.player_world().identity().find(target) else {
        return false;
    };
    let Some(source_instance) = source_world.instance_id else {
        return false;
    };
    if target_world.instance_id != Some(source_instance) {
        return false;
    }
    let Some(arena) = ctx.db.arena_instance().id().find(source_instance) else {
        return false;
    };
    arena_is_hostile_match_context(&arena)
}

fn arena_is_hostile_match_context(arena: &ArenaInstance) -> bool {
    !arena.is_practice
        && (arena.phase == MATCH_PHASE_COUNTDOWN || arena.phase == MATCH_PHASE_IN_PROGRESS)
}

#[cfg(test)]
mod tests {
    use super::{CombatRelation, TargetAudience};

    #[test]
    fn audience_allows_expected_relationships() {
        assert!(TargetAudience::SelfOnly.allows(CombatRelation::Self_));
        assert!(!TargetAudience::SelfOnly.allows(CombatRelation::PartyAlly));
        assert!(TargetAudience::Hostile.allows(CombatRelation::Hostile));
        assert!(!TargetAudience::Hostile.allows(CombatRelation::Neutral));
        assert!(TargetAudience::PartyOrSelf.allows(CombatRelation::PartyAlly));
        assert!(!TargetAudience::PartyOrSelf.allows(CombatRelation::Neutral));
        assert!(TargetAudience::Assistable.allows(CombatRelation::Neutral));
        assert!(!TargetAudience::Assistable.allows(CombatRelation::Hostile));
    }
}
