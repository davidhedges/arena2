use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

use crate::progression::ensure_default_progression_for_identity;

pub const DEFAULT_RACE_ID: &str = "HUMAN";
pub const DEFAULT_SEX_ID: &str = "MALE";
pub const DEFAULT_BODY_ID: &str = "HUMAN_MALE_BODY_01";
pub const DEFAULT_HEAD_ID: &str = "HUMAN_MALE_HEAD_01_A";
pub const DEFAULT_FACE_ID: &str = "";
pub const DEFAULT_HAIR_ID: &str = "";
pub const DEFAULT_EYES_ID: &str = "HUMAN_EYES_BLUE";
pub const DEFAULT_STARTER_OUTFIT_ID: &str = "HUMAN_MALE_PEASANT_STARTER";

#[allow(unused_imports)]
use crate::appearance::character_appearance as _;
#[allow(unused_imports)]
use crate::player::player as _;

#[table(accessor = character_appearance, public)]
pub struct CharacterAppearance {
    #[primary_key]
    pub character_id: String,
    #[index(btree)]
    pub owner: Identity,
    pub race_id: String,
    pub sex_id: String,
    pub body_id: String,
    pub head_id: String,
    pub face_id: String,
    pub hair_id: String,
    pub eyes_id: String,
    pub outfit_id: String,
    pub creation_complete: bool,
    pub appearance_version: u32,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
}

#[reducer]
pub fn create_or_update_character(
    ctx: &ReducerContext,
    race_id: String,
    sex_id: String,
    body_id: String,
    head_id: String,
    face_id: String,
    hair_id: String,
    eyes_id: String,
    outfit_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    ensure_default_progression_for_identity(ctx, owner)?;

    let row = validated_appearance_row(
        ctx, owner, race_id, sex_id, body_id, head_id, face_id, hair_id, eyes_id, outfit_id, true,
    )?;
    upsert_character_appearance(ctx, row);
    Ok(())
}

#[reducer]
pub fn save_character_appearance(
    ctx: &ReducerContext,
    character_id: String,
    race_id: String,
    sex_id: String,
    body_id: String,
    head_id: String,
    face_id: String,
    hair_id: String,
    eyes_id: String,
    outfit_id: String,
) -> Result<(), String> {
    let owner = ctx.sender();
    let expected_character_id = default_character_id(owner);
    if character_id != expected_character_id {
        return Err("cannot save appearance for another character".to_string());
    }

    let existing = ctx
        .db
        .character_appearance()
        .character_id()
        .find(expected_character_id)
        .ok_or_else(|| "character appearance row not found".to_string())?;
    if existing.owner != owner {
        return Err("cannot save appearance for another owner".to_string());
    }

    let mut row = validated_appearance_row(
        ctx,
        owner,
        race_id,
        sex_id,
        body_id,
        head_id,
        face_id,
        hair_id,
        eyes_id,
        outfit_id,
        existing.creation_complete,
    )?;
    row.created_at = existing.created_at;
    upsert_character_appearance(ctx, row);
    Ok(())
}

pub(crate) fn backfill_character_appearance_rows(ctx: &ReducerContext) -> usize {
    let mut repaired = 0;
    for player in ctx.db.player().iter() {
        let character_id = default_character_id(player.identity);
        if let Some(mut row) = ctx
            .db
            .character_appearance()
            .character_id()
            .find(character_id)
        {
            let expected_outfit_id = default_outfit_id().to_string();
            if row.outfit_id != expected_outfit_id {
                row.outfit_id = expected_outfit_id;
                row.appearance_version = row.appearance_version.saturating_add(1).max(1);
                row.updated_at = ctx.timestamp;
                ctx.db.character_appearance().character_id().update(row);
                repaired += 1;
            }
            continue;
        }

        if ensure_default_character_appearance_for_identity(ctx, player.identity).is_ok() {
            repaired += 1;
        }
    }

    repaired
}

pub(crate) fn ensure_default_character_appearance_for_identity(
    ctx: &ReducerContext,
    owner: Identity,
) -> Result<(), String> {
    if ctx
        .db
        .character_appearance()
        .character_id()
        .find(default_character_id(owner))
        .is_some()
    {
        return Ok(());
    }

    ensure_default_progression_for_identity(ctx, owner)?;

    ctx.db.character_appearance().insert(CharacterAppearance {
        character_id: default_character_id(owner),
        owner,
        race_id: DEFAULT_RACE_ID.to_string(),
        sex_id: DEFAULT_SEX_ID.to_string(),
        body_id: DEFAULT_BODY_ID.to_string(),
        head_id: DEFAULT_HEAD_ID.to_string(),
        face_id: DEFAULT_FACE_ID.to_string(),
        hair_id: DEFAULT_HAIR_ID.to_string(),
        eyes_id: DEFAULT_EYES_ID.to_string(),
        outfit_id: default_outfit_id().to_string(),
        creation_complete: true,
        appearance_version: 1,
        created_at: ctx.timestamp,
        updated_at: ctx.timestamp,
    });
    Ok(())
}

pub(crate) fn default_character_id(owner: Identity) -> String {
    format!("default:{}", owner.to_hex())
}

fn validated_appearance_row(
    ctx: &ReducerContext,
    owner: Identity,
    race_id: String,
    sex_id: String,
    body_id: String,
    head_id: String,
    face_id: String,
    hair_id: String,
    eyes_id: String,
    outfit_id: String,
    creation_complete: bool,
) -> Result<CharacterAppearance, String> {
    let race_id = require_allowed("race", race_id.as_str(), &[DEFAULT_RACE_ID])?;
    let sex_id = require_allowed("sex", sex_id.as_str(), &[DEFAULT_SEX_ID])?;
    let body_id = require_allowed("body", body_id.as_str(), &[DEFAULT_BODY_ID])?;
    let head_id = require_allowed("head", head_id.as_str(), &[DEFAULT_HEAD_ID])?;
    let face_id = require_allowed("face", face_id.as_str(), &[DEFAULT_FACE_ID])?;
    let hair_id = require_allowed("hair", hair_id.as_str(), &[DEFAULT_HAIR_ID])?;
    let eyes_id = require_allowed("eyes", eyes_id.as_str(), &[DEFAULT_EYES_ID])?;
    let outfit_id = require_allowed("outfit", outfit_id.as_str(), &[DEFAULT_STARTER_OUTFIT_ID])?;

    Ok(CharacterAppearance {
        character_id: default_character_id(owner),
        owner,
        race_id,
        sex_id,
        body_id,
        head_id,
        face_id,
        hair_id,
        eyes_id,
        outfit_id,
        creation_complete,
        appearance_version: 1,
        created_at: ctx.timestamp,
        updated_at: ctx.timestamp,
    })
}

fn upsert_character_appearance(ctx: &ReducerContext, row: CharacterAppearance) {
    if ctx
        .db
        .character_appearance()
        .character_id()
        .find(row.character_id.clone())
        .is_some()
    {
        ctx.db.character_appearance().character_id().update(row);
    } else {
        ctx.db.character_appearance().insert(row);
    }
}

fn default_outfit_id() -> &'static str {
    DEFAULT_STARTER_OUTFIT_ID
}

fn require_allowed(field: &str, value: &str, allowed: &[&str]) -> Result<String, String> {
    let normalized = normalize_identifier(value);
    if allowed.iter().any(|candidate| *candidate == normalized) {
        return Ok(normalized);
    }

    Err(format!("unsupported {field} id '{}'", value.trim()))
}

fn normalize_identifier(value: &str) -> String {
    value.trim().to_ascii_uppercase()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_outfit_is_classless() {
        assert_eq!(default_outfit_id(), DEFAULT_STARTER_OUTFIT_ID);
    }

    #[test]
    fn appearance_ids_are_normalized_and_restricted_to_v1_scope() {
        assert_eq!(
            require_allowed("race", " human ", &[DEFAULT_RACE_ID]).unwrap(),
            DEFAULT_RACE_ID
        );
        assert!(require_allowed("race", "ORC", &[DEFAULT_RACE_ID]).is_err());
        assert!(require_allowed("sex", "FEMALE", &[DEFAULT_SEX_ID]).is_err());
    }

    #[test]
    fn default_character_id_is_deterministic() {
        let owner =
            Identity::from_hex("0000000000000000000000000000000000000000000000000000000000000001")
                .expect("test identity hex should be valid");

        assert_eq!(
            default_character_id(owner),
            "default:0000000000000000000000000000000000000000000000000000000000000001"
        );
    }
}
