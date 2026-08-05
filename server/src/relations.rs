use serde::{Deserialize, Serialize};
use spacetimedb::{Identity, ReducerContext};

use crate::arena::{
    players_share_world_context, ArenaInstance, INSTANCE_KIND_ARENA, MATCH_PHASE_COUNTDOWN,
    MATCH_PHASE_IN_PROGRESS,
};
use crate::npcs::{npc_relation_profile, NpcFaction, NpcRelationProfile};
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
pub(crate) const TARGET_AUDIENCE_ANY: &str = "ANY";

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
    Any,
}

impl TargetAudience {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::SelfOnly => TARGET_AUDIENCE_SELF_ONLY,
            Self::Hostile => TARGET_AUDIENCE_HOSTILE,
            Self::PartyOrSelf => TARGET_AUDIENCE_PARTY_OR_SELF,
            Self::Assistable => TARGET_AUDIENCE_ASSISTABLE,
            Self::Any => TARGET_AUDIENCE_ANY,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Option<Self> {
        match value.trim().to_ascii_uppercase().as_str() {
            TARGET_AUDIENCE_SELF_ONLY => Some(Self::SelfOnly),
            TARGET_AUDIENCE_HOSTILE => Some(Self::Hostile),
            TARGET_AUDIENCE_PARTY_OR_SELF => Some(Self::PartyOrSelf),
            TARGET_AUDIENCE_ASSISTABLE => Some(Self::Assistable),
            TARGET_AUDIENCE_ANY => Some(Self::Any),
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
            Self::Any => true,
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

    let source_npc = npc_relation_profile(ctx, source);
    let target_npc = npc_relation_profile(ctx, target);
    match (source_npc.as_ref(), target_npc.as_ref()) {
        (Some(source_profile), Some(target_profile)) => {
            return npc_pair_relation(source_profile, target_profile);
        }
        (Some(profile), None) if target_is_player(ctx, target) => {
            return npc_player_relation(ctx, profile, target);
        }
        (None, Some(profile)) if target_is_player(ctx, source) => {
            return npc_player_relation(ctx, profile, source);
        }
        _ => {}
    }

    // Playground-only override for local targeting and party-frame testing. This
    // is not a general faction, NPC, or bot relationship system.
    if let Some(kind) = playground_target_kind_for_relation(ctx, source, target) {
        return match kind {
            PlaygroundTargetKind::Hostile => CombatRelation::Hostile,
            PlaygroundTargetKind::Neutral => CombatRelation::Neutral,
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

fn npc_pair_relation(source: &NpcRelationProfile, target: &NpcRelationProfile) -> CombatRelation {
    if source.combat_team_id == target.combat_team_id {
        return CombatRelation::PartyAlly;
    }
    if source.faction == NpcFaction::Neutral || target.faction == NpcFaction::Neutral {
        CombatRelation::Neutral
    } else {
        CombatRelation::Hostile
    }
}

fn npc_player_relation(
    ctx: &ReducerContext,
    npc: &NpcRelationProfile,
    player: Identity,
) -> CombatRelation {
    match npc.faction {
        NpcFaction::Hostile => CombatRelation::Hostile,
        NpcFaction::Neutral => CombatRelation::Neutral,
        NpcFaction::Friendly => {
            if player == npc.spawned_by || same_party(ctx, player, npc.spawned_by) {
                CombatRelation::PartyAlly
            } else if match_context_makes_hostile(ctx, npc.spawned_by, player) {
                CombatRelation::Hostile
            } else {
                CombatRelation::Neutral
            }
        }
    }
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
        crate::spells::SpellBehavior::SelfTeleport => TargetAudience::SelfOnly,
        crate::spells::SpellBehavior::Recall => TargetAudience::SelfOnly,
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

fn target_is_player(ctx: &ReducerContext, target: Identity) -> bool {
    ctx.db.player_state().player_id().find(target).is_some()
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
    arena.instance_kind == INSTANCE_KIND_ARENA
        && (arena.phase == MATCH_PHASE_COUNTDOWN || arena.phase == MATCH_PHASE_IN_PROGRESS)
}

#[cfg(test)]
mod tests {
    use super::{npc_pair_relation, CombatRelation, TargetAudience};
    use crate::npcs::{NpcFaction, NpcRelationProfile};
    use spacetimedb::Identity;

    fn npc_profile(faction: NpcFaction, team: &str) -> NpcRelationProfile {
        NpcRelationProfile {
            faction,
            spawned_by: Identity::ZERO,
            combat_team_id: team.to_string(),
        }
    }

    #[test]
    fn audience_allows_expected_relationships() {
        assert!(TargetAudience::SelfOnly.allows(CombatRelation::Self_));
        assert!(!TargetAudience::SelfOnly.allows(CombatRelation::PartyAlly));
        assert!(TargetAudience::Hostile.allows(CombatRelation::Hostile));
        assert!(!TargetAudience::Hostile.allows(CombatRelation::Neutral));
        assert!(TargetAudience::PartyOrSelf.allows(CombatRelation::PartyAlly));
        assert!(TargetAudience::PartyOrSelf.allows(CombatRelation::Self_));
        assert!(!TargetAudience::PartyOrSelf.allows(CombatRelation::Neutral));
        assert!(TargetAudience::Assistable.allows(CombatRelation::Self_));
        assert!(TargetAudience::Assistable.allows(CombatRelation::PartyAlly));
        assert!(TargetAudience::Assistable.allows(CombatRelation::Neutral));
        assert!(!TargetAudience::Assistable.allows(CombatRelation::Hostile));
        assert!(TargetAudience::Any.allows(CombatRelation::Self_));
        assert!(TargetAudience::Any.allows(CombatRelation::PartyAlly));
        assert!(TargetAudience::Any.allows(CombatRelation::Neutral));
        assert!(TargetAudience::Any.allows(CombatRelation::Hostile));
    }

    #[test]
    fn npc_team_relations_are_symmetric_and_neutral_safe() {
        let hostile_a = npc_profile(NpcFaction::Hostile, "ENCOUNTER_A");
        let hostile_a_ally = npc_profile(NpcFaction::Hostile, "ENCOUNTER_A");
        let hostile_b = npc_profile(NpcFaction::Hostile, "ENCOUNTER_B");
        let neutral = npc_profile(NpcFaction::Neutral, "NEUTRAL_A");

        assert_eq!(
            npc_pair_relation(&hostile_a, &hostile_a_ally),
            CombatRelation::PartyAlly
        );
        assert_eq!(
            npc_pair_relation(&hostile_a, &hostile_b),
            CombatRelation::Hostile
        );
        assert_eq!(
            npc_pair_relation(&hostile_b, &hostile_a),
            CombatRelation::Hostile
        );
        assert_eq!(
            npc_pair_relation(&hostile_a, &neutral),
            CombatRelation::Neutral
        );
        assert_eq!(
            npc_pair_relation(&neutral, &hostile_a),
            CombatRelation::Neutral
        );
    }
}
