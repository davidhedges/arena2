use spacetimedb::rand::Rng;
use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

const MAX_DICE_REQUEST_ID_LEN: usize = 64;

#[table(accessor = active_dice_roll, public)]
#[derive(Clone)]
pub struct ActiveDiceRoll {
    #[primary_key]
    pub owner: Identity,
    pub request_id: String,
    pub die_sides: u32,
    pub resolved_value: u32,
    pub created_at: Timestamp,
}

/// Development-only entry point for exercising the dice overlay before
/// outcome-producing gameplay reducers exist. It must not be reused as an
/// authorization path for rewards or events.
#[reducer]
pub fn request_dice_roll_preview(
    ctx: &ReducerContext,
    request_id: String,
    die_sides: u32,
) -> Result<(), String> {
    validate_die_sides(die_sides)?;
    validate_request_id(&request_id)?;

    let owner = ctx.sender();
    if let Some(active) = ctx.db.active_dice_roll().owner().find(owner) {
        if active.request_id == request_id && active.die_sides == die_sides {
            return Ok(());
        }

        return Err("Dismiss the active dice roll before requesting another".to_string());
    }

    let resolved_value = roll_die(ctx, die_sides);
    ctx.db.active_dice_roll().insert(ActiveDiceRoll {
        owner,
        request_id,
        die_sides,
        resolved_value,
        created_at: ctx.timestamp,
    });
    Ok(())
}

#[reducer]
pub fn dismiss_dice_roll(ctx: &ReducerContext) -> Result<(), String> {
    ctx.db.active_dice_roll().owner().delete(ctx.sender());
    Ok(())
}

fn validate_die_sides(die_sides: u32) -> Result<(), String> {
    if matches!(die_sides, 4 | 6 | 8 | 10 | 12 | 20) {
        Ok(())
    } else {
        Err("Dice sides must be one of 4, 6, 8, 10, 12, or 20".to_string())
    }
}

fn validate_request_id(request_id: &str) -> Result<(), String> {
    if request_id.is_empty() {
        return Err("Dice request identifier cannot be empty".to_string());
    }
    if request_id.len() > MAX_DICE_REQUEST_ID_LEN {
        return Err(format!(
            "Dice request identifier cannot exceed {MAX_DICE_REQUEST_ID_LEN} bytes"
        ));
    }
    if !request_id
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err(
            "Dice request identifier may contain only ASCII letters, numbers, '-' and '_'"
                .to_string(),
        );
    }

    Ok(())
}

/// Samples a uniform face using SpacetimeDB's reducer-scoped RNG. Future
/// outcome reducers should call this helper in the same transaction that
/// commits the resulting gameplay consequence.
pub(crate) fn roll_die(ctx: &ReducerContext, die_sides: u32) -> u32 {
    ctx.rng().gen_range(1..=die_sides)
}
