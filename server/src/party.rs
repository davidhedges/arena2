use std::time::Duration;

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::arena::players_share_world_context;
use crate::player_state::player_state as _;

#[allow(unused_imports)]
use crate::party::party as _;
#[allow(unused_imports)]
use crate::party::party_invite as _;
#[allow(unused_imports)]
use crate::party::party_member as _;

pub(crate) const MAX_PARTY_MEMBERS: usize = 5;
const PARTY_INVITE_TTL: Duration = Duration::from_secs(60);

#[table(accessor = party, public)]
pub struct Party {
    #[primary_key]
    #[auto_inc]
    pub party_id: u64,
    pub leader: Identity,
    pub created_at: Timestamp,
}

#[table(accessor = party_member, public)]
pub struct PartyMember {
    #[primary_key]
    pub member: Identity,
    #[index(btree)]
    pub party_id: u64,
    pub joined_at: Timestamp,
}

#[table(accessor = party_invite, public)]
pub struct PartyInvite {
    #[primary_key]
    #[auto_inc]
    pub invite_id: u64,
    #[index(btree)]
    pub party_id: u64,
    pub inviter: Identity,
    #[index(btree)]
    pub invitee: Identity,
    pub created_at: Timestamp,
    pub expires_at: Timestamp,
}

#[reducer]
pub fn invite_to_party(ctx: &ReducerContext, target_id: String) -> Result<(), String> {
    expire_party_invites(ctx, ctx.timestamp);

    let inviter = ctx.sender();
    let target = Identity::from_hex(target_id.as_str())
        .map_err(|_| "Invalid invite target identity".to_string())?;
    if target == inviter {
        return Err("Cannot invite yourself to a party".to_string());
    }
    ensure_live_player(ctx, target, "invite target")?;
    ensure_live_player(ctx, inviter, "inviter")?;
    if !players_share_world_context(ctx, inviter, target) {
        return Err("Invite target must be in the same world".to_string());
    }
    if ctx.db.party_member().member().find(target).is_some() {
        return Err("Invite target is already in a party".to_string());
    }

    let party_id = match ctx.db.party_member().member().find(inviter) {
        Some(member) => {
            ensure_party_leader(ctx, inviter, member.party_id)?;
            member.party_id
        }
        None => create_party_for_leader(ctx, inviter),
    };

    if party_size(ctx, party_id) >= MAX_PARTY_MEMBERS {
        return Err("Party is full".to_string());
    }

    delete_invites_for_invitee_in_party(ctx, party_id, target);
    ctx.db.party_invite().insert(PartyInvite {
        invite_id: 0,
        party_id,
        inviter,
        invitee: target,
        created_at: ctx.timestamp,
        expires_at: ctx.timestamp + PARTY_INVITE_TTL,
    });
    Ok(())
}

#[reducer]
pub fn accept_party_invite(ctx: &ReducerContext, invite_id: u64) -> Result<(), String> {
    expire_party_invites(ctx, ctx.timestamp);

    let invitee = ctx.sender();
    let Some(invite) = ctx.db.party_invite().invite_id().find(invite_id) else {
        return Err("Party invite not found".to_string());
    };
    if invite.invitee != invitee {
        return Err("Party invite belongs to another player".to_string());
    }
    if invite.expires_at <= ctx.timestamp {
        ctx.db.party_invite().invite_id().delete(invite_id);
        return Err("Party invite expired".to_string());
    }
    if ctx.db.party_member().member().find(invitee).is_some() {
        delete_invites_for_invitee(ctx, invitee);
        return Err("You are already in a party".to_string());
    }
    if ctx.db.party().party_id().find(invite.party_id).is_none() {
        ctx.db.party_invite().invite_id().delete(invite_id);
        return Err("Party no longer exists".to_string());
    }
    if party_size(ctx, invite.party_id) >= MAX_PARTY_MEMBERS {
        return Err("Party is full".to_string());
    }

    ctx.db.party_member().insert(PartyMember {
        member: invitee,
        party_id: invite.party_id,
        joined_at: ctx.timestamp,
    });
    delete_invites_for_invitee(ctx, invitee);
    Ok(())
}

#[reducer]
pub fn decline_party_invite(ctx: &ReducerContext, invite_id: u64) -> Result<(), String> {
    let Some(invite) = ctx.db.party_invite().invite_id().find(invite_id) else {
        return Ok(());
    };
    if invite.invitee != ctx.sender() {
        return Err("Party invite belongs to another player".to_string());
    }
    ctx.db.party_invite().invite_id().delete(invite_id);
    Ok(())
}

#[reducer]
pub fn leave_party(ctx: &ReducerContext) -> Result<(), String> {
    remove_party_member(ctx, ctx.sender())
}

#[reducer]
pub fn kick_party_member(ctx: &ReducerContext, member_id: String) -> Result<(), String> {
    let leader = ctx.sender();
    let target = Identity::from_hex(member_id.as_str())
        .map_err(|_| "Invalid party member identity".to_string())?;
    if target == leader {
        return Err("Use leave_party to leave your own party".to_string());
    }
    let party_id = party_id_for_member(ctx, leader).ok_or("You are not in a party")?;
    ensure_party_leader(ctx, leader, party_id)?;
    let target_party_id = party_id_for_member(ctx, target).ok_or("Target is not in your party")?;
    if target_party_id != party_id {
        return Err("Target is not in your party".to_string());
    }
    remove_party_member(ctx, target)
}

#[reducer]
pub fn promote_party_leader(ctx: &ReducerContext, member_id: String) -> Result<(), String> {
    let leader = ctx.sender();
    let target = Identity::from_hex(member_id.as_str())
        .map_err(|_| "Invalid party member identity".to_string())?;
    let party_id = party_id_for_member(ctx, leader).ok_or("You are not in a party")?;
    ensure_party_leader(ctx, leader, party_id)?;
    let target_party_id = party_id_for_member(ctx, target).ok_or("Target is not in your party")?;
    if target_party_id != party_id {
        return Err("Target is not in your party".to_string());
    }
    let Some(mut party) = ctx.db.party().party_id().find(party_id) else {
        return Err("Party no longer exists".to_string());
    };
    party.leader = target;
    ctx.db.party().party_id().update(party);
    Ok(())
}

pub(crate) fn expire_party_invites(ctx: &ReducerContext, now: Timestamp) {
    let expired: Vec<_> = ctx
        .db
        .party_invite()
        .iter()
        .filter(|invite| invite.expires_at <= now)
        .map(|invite| invite.invite_id)
        .collect();
    for invite_id in expired {
        ctx.db.party_invite().invite_id().delete(invite_id);
    }
}

pub(crate) fn remove_player_from_party_state(ctx: &ReducerContext, identity: Identity) {
    delete_invites_for_invitee(ctx, identity);
    let invite_ids: Vec<_> = ctx
        .db
        .party_invite()
        .iter()
        .filter(|invite| invite.inviter == identity)
        .map(|invite| invite.invite_id)
        .collect();
    for invite_id in invite_ids {
        ctx.db.party_invite().invite_id().delete(invite_id);
    }
    let _ = remove_party_member(ctx, identity);
}

pub(crate) fn party_id_for_member(ctx: &ReducerContext, member: Identity) -> Option<u64> {
    ctx.db
        .party_member()
        .member()
        .find(member)
        .map(|row| row.party_id)
}

pub(crate) fn same_party(ctx: &ReducerContext, a: Identity, b: Identity) -> bool {
    if a == b {
        return true;
    }
    let Some(a_party) = party_id_for_member(ctx, a) else {
        return false;
    };
    party_id_for_member(ctx, b) == Some(a_party)
}

pub(crate) struct PlaygroundPartyMemberAssignment {
    pub(crate) party_id: u64,
    pub(crate) created_party_for_owner: bool,
}

pub(crate) fn ensure_playground_party_member(
    ctx: &ReducerContext,
    owner: Identity,
    member: Identity,
) -> Result<PlaygroundPartyMemberAssignment, String> {
    let existing_party_id = party_id_for_member(ctx, owner);
    let party_id = existing_party_id.unwrap_or_else(|| create_party_for_leader(ctx, owner));
    if ctx.db.party_member().member().find(member).is_some() {
        return Ok(PlaygroundPartyMemberAssignment {
            party_id,
            created_party_for_owner: existing_party_id.is_none(),
        });
    }
    if party_size(ctx, party_id) >= MAX_PARTY_MEMBERS {
        return Err("Party is full; cannot add playground party member".to_string());
    }
    ctx.db.party_member().insert(PartyMember {
        member,
        party_id,
        joined_at: ctx.timestamp,
    });
    Ok(PlaygroundPartyMemberAssignment {
        party_id,
        created_party_for_owner: existing_party_id.is_none(),
    })
}

pub(crate) fn remove_playground_party_member(ctx: &ReducerContext, member: Identity) {
    if ctx.db.party_member().member().find(member).is_some() {
        ctx.db.party_member().member().delete(member);
    }
}

pub(crate) fn remove_playground_party_member_and_created_party(
    ctx: &ReducerContext,
    owner: Identity,
    member: Identity,
    party_id: u64,
    created_party_for_owner: bool,
) {
    remove_playground_party_member(ctx, member);
    if !created_party_for_owner {
        return;
    }

    let remaining: Vec<_> = ctx.db.party_member().party_id().filter(party_id).collect();
    if remaining.len() == 1 && remaining[0].member == owner {
        let _ = remove_party_member(ctx, owner);
    }
}

fn create_party_for_leader(ctx: &ReducerContext, leader: Identity) -> u64 {
    let party = ctx.db.party().insert(Party {
        party_id: 0,
        leader,
        created_at: ctx.timestamp,
    });
    ctx.db.party_member().insert(PartyMember {
        member: leader,
        party_id: party.party_id,
        joined_at: ctx.timestamp,
    });
    party.party_id
}

fn ensure_party_leader(
    ctx: &ReducerContext,
    identity: Identity,
    party_id: u64,
) -> Result<(), String> {
    let Some(party) = ctx.db.party().party_id().find(party_id) else {
        return Err("Party no longer exists".to_string());
    };
    if party.leader != identity {
        return Err("Only the party leader can do that".to_string());
    }
    Ok(())
}

fn ensure_live_player(ctx: &ReducerContext, identity: Identity, label: &str) -> Result<(), String> {
    let Some(state) = ctx.db.player_state().player_id().find(identity) else {
        return Err(format!("{label} is not available"));
    };
    if !state.alive {
        return Err(format!("{label} is not alive"));
    }
    Ok(())
}

fn party_size(ctx: &ReducerContext, party_id: u64) -> usize {
    ctx.db.party_member().party_id().filter(party_id).count()
}

fn delete_invites_for_invitee(ctx: &ReducerContext, invitee: Identity) {
    let invite_ids: Vec<_> = ctx
        .db
        .party_invite()
        .invitee()
        .filter(invitee)
        .map(|invite| invite.invite_id)
        .collect();
    for invite_id in invite_ids {
        ctx.db.party_invite().invite_id().delete(invite_id);
    }
}

fn delete_invites_for_invitee_in_party(ctx: &ReducerContext, party_id: u64, invitee: Identity) {
    let invite_ids: Vec<_> = ctx
        .db
        .party_invite()
        .invitee()
        .filter(invitee)
        .filter(|invite| invite.party_id == party_id)
        .map(|invite| invite.invite_id)
        .collect();
    for invite_id in invite_ids {
        ctx.db.party_invite().invite_id().delete(invite_id);
    }
}

fn remove_party_member(ctx: &ReducerContext, member: Identity) -> Result<(), String> {
    let Some(row) = ctx.db.party_member().member().find(member) else {
        return Ok(());
    };
    let party_id = row.party_id;
    ctx.db.party_member().member().delete(member);

    let mut remaining: Vec<_> = ctx.db.party_member().party_id().filter(party_id).collect();
    remaining.sort_by_key(|member| member.joined_at);
    if remaining.is_empty() {
        ctx.db.party().party_id().delete(party_id);
        let invite_ids: Vec<_> = ctx
            .db
            .party_invite()
            .party_id()
            .filter(party_id)
            .map(|invite| invite.invite_id)
            .collect();
        for invite_id in invite_ids {
            ctx.db.party_invite().invite_id().delete(invite_id);
        }
        return Ok(());
    }

    if let Some(mut party) = ctx.db.party().party_id().find(party_id) {
        if party.leader == member {
            party.leader = remaining[0].member;
            ctx.db.party().party_id().update(party);
        }
    }
    Ok(())
}
