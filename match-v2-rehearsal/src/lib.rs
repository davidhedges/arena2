//! Isolated Combat Build v2 snapshot and match-materialization rehearsal.
//!
//! This crate is deliberately separate from both gameplay server flavors. It
//! proves the handoff contract without changing canonical bootstrap reducers,
//! reservation tables, runtime authorization, or local-direct admission.

use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

#[path = "../../server/src/combat_build_v2.rs"]
#[allow(dead_code)]
mod combat_build_v2_contract;

use combat_build_v2_contract::{
    CombatBuildV2Catalog, CombatBuildV2MaterializationPlan, MaterializedCombatFeatureV2,
    ValidatedCombatBuildV2,
};

const QUEUE_UNRANKED: &str = "UNRANKED";
const QUEUE_OPEN_WORLD: &str = "OPEN_WORLD";
const QUEUE_LOCAL_DIRECT: &str = "LOCAL_DIRECT";

#[table(accessor = match_reservation_v2)]
#[derive(Clone)]
pub struct MatchReservationV2 {
    #[primary_key]
    pub player_identity: Identity,
    pub queue_kind: String,
    pub contract_schema_version: u32,
    pub combat_build_revision: u64,
    pub combat_build_snapshot_json_hex: String,
    pub armor_set_id: String,
    pub reserved_at: Timestamp,
}

#[table(accessor = match_combat_build_v2, public)]
pub struct MatchCombatBuildV2 {
    #[primary_key]
    pub owner: Identity,
    pub queue_kind: String,
    pub contract_schema_version: u32,
    pub revision: u64,
    pub starting_discipline_id: String,
    pub mastery_active: bool,
    pub materialized_at: Timestamp,
}

#[table(accessor = match_selected_specialization_v2, public)]
pub struct MatchSelectedSpecializationV2 {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub slot_index: u8,
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub specialization_kind: String,
}

#[table(accessor = match_discipline_configuration_v2, public)]
pub struct MatchDisciplineConfigurationV2 {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub combat_discipline_id: String,
    pub main_hand_item_def_id: String,
    pub main_hand_color_id: String,
    pub off_hand_item_def_id: String,
    pub off_hand_color_id: String,
}

#[table(accessor = match_technique_selection_v2, public)]
pub struct MatchTechniqueSelectionV2 {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub ability_id: String,
    pub bar_order: u8,
}

#[table(accessor = match_spell_selection_v2, public)]
pub struct MatchSpellSelectionV2 {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub ability_id: String,
    pub bar_order: u8,
}

#[table(accessor = match_perk_selection_v2, public)]
pub struct MatchPerkSelectionV2 {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub specialization_id: String,
    pub combat_discipline_id: String,
    pub ability_id: String,
}

#[table(accessor = match_trait_selection_v2, public)]
pub struct MatchTraitSelectionV2 {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub ability_id: String,
}

#[table(accessor = phase3_match_probe_result, public)]
#[derive(Clone)]
pub struct Phase3MatchProbeResult {
    #[primary_key]
    pub owner: Identity,
    pub queue_kind: String,
    pub contract_schema_version: u32,
    pub combat_build_revision: u64,
    pub snapshot_json_hex: String,
    pub reservation_bytes_equal: bool,
    pub selected_specialization_count: u32,
    pub parent_discipline_count: u32,
    pub technique_count: u32,
    pub spell_count: u32,
    pub perk_count: u32,
    pub trait_count: u32,
    pub mastery_active: bool,
    pub completed_at: Timestamp,
}

#[reducer]
pub fn bootstrap_v2_handoff(
    ctx: &ReducerContext,
    queue_kind: String,
    snapshot_json_hex: String,
    armor_set_id: String,
) -> Result<(), String> {
    let queue_kind = queue_kind.trim().to_ascii_uppercase();
    if !matches!(queue_kind.as_str(), QUEUE_UNRANKED | QUEUE_OPEN_WORLD) {
        return Err(format!(
            "COMBAT_BUILD_V2_QUEUE_INVALID: unsupported rehearsal queue '{queue_kind}'"
        ));
    }
    admit_snapshot(ctx, queue_kind, snapshot_json_hex, armor_set_id)
}

/// Local-direct remains a validator-owned fixture path rather than an
/// alternate authority. This reducer is compiled only into the disposable
/// rehearsal module.
#[reducer]
pub fn admit_local_direct_v2_fixture(
    ctx: &ReducerContext,
    snapshot_json_hex: String,
) -> Result<(), String> {
    admit_snapshot(
        ctx,
        QUEUE_LOCAL_DIRECT.to_string(),
        snapshot_json_hex,
        "LOCAL_DIRECT".to_string(),
    )
}

fn admit_snapshot(
    ctx: &ReducerContext,
    queue_kind: String,
    snapshot_json_hex: String,
    armor_set_id: String,
) -> Result<(), String> {
    if ctx
        .db
        .match_combat_build_v2()
        .owner()
        .find(ctx.sender())
        .is_some()
    {
        return Err("COMBAT_BUILD_V2_ALREADY_MATERIALIZED: admission is one-shot".to_string());
    }
    let armor_set_id = armor_set_id.trim().to_ascii_uppercase();
    if armor_set_id.is_empty() {
        return Err("COMBAT_BUILD_V2_ARMOR_EMPTY: armor set is required".to_string());
    }

    let (snapshot_json, validated, plan) = validated_payload(snapshot_json_hex.as_str())?;
    let canonical_hex = hex_encode(snapshot_json.as_bytes());
    if canonical_hex != snapshot_json_hex {
        return Err(
            "COMBAT_BUILD_V2_SNAPSHOT_NOT_CANONICAL: hex envelope differs from canonical bytes"
                .to_string(),
        );
    }

    ctx.db.match_reservation_v2().insert(MatchReservationV2 {
        player_identity: ctx.sender(),
        queue_kind: queue_kind.clone(),
        contract_schema_version: validated.snapshot.schema_version,
        combat_build_revision: validated.snapshot.revision,
        combat_build_snapshot_json_hex: canonical_hex.clone(),
        armor_set_id,
        reserved_at: ctx.timestamp,
    });
    materialize_plan(ctx, ctx.sender(), queue_kind.clone(), &plan);

    let reservation = ctx
        .db
        .match_reservation_v2()
        .player_identity()
        .find(ctx.sender())
        .expect("reservation inserted in this transaction");
    let reservation_bytes_equal = reservation.contract_schema_version == plan.schema_version
        && reservation.combat_build_revision == plan.revision
        && reservation.combat_build_snapshot_json_hex == canonical_hex;
    if !reservation_bytes_equal {
        return Err(
            "COMBAT_BUILD_V2_RESERVATION_DIVERGED: match reservation differs from payload"
                .to_string(),
        );
    }

    ctx.db
        .phase3_match_probe_result()
        .insert(Phase3MatchProbeResult {
            owner: ctx.sender(),
            queue_kind,
            contract_schema_version: plan.schema_version,
            combat_build_revision: plan.revision,
            snapshot_json_hex: canonical_hex,
            reservation_bytes_equal,
            selected_specialization_count: plan.selected_specializations.len() as u32,
            parent_discipline_count: plan.parent_discipline_ids.len() as u32,
            technique_count: plan.techniques.len() as u32,
            spell_count: plan.spells.len() as u32,
            perk_count: plan.perks.len() as u32,
            trait_count: plan.traits.len() as u32,
            mastery_active: plan.mastery_active,
            completed_at: ctx.timestamp,
        });
    Ok(())
}

fn validated_payload(
    snapshot_json_hex: &str,
) -> Result<
    (
        String,
        ValidatedCombatBuildV2,
        CombatBuildV2MaterializationPlan,
    ),
    String,
> {
    let snapshot_bytes = hex_decode(snapshot_json_hex)?;
    let snapshot_json = String::from_utf8(snapshot_bytes)
        .map_err(|error| format!("COMBAT_BUILD_V2_SNAPSHOT_INVALID_UTF8: {error}"))?;
    let catalog = CombatBuildV2Catalog::from_shared_catalogs()
        .map_err(|error| format!("COMBAT_BUILD_V2_CATALOG_INVALID: {error}"))?;
    let validated = catalog.validate_canonical_snapshot_json(snapshot_json.as_str())?;
    let canonical_json = catalog.serialize_canonical_snapshot(&validated.snapshot)?;
    if canonical_json != snapshot_json {
        return Err(
            "COMBAT_BUILD_V2_SNAPSHOT_NOT_CANONICAL: reserialization changed payload bytes"
                .to_string(),
        );
    }
    let plan = catalog.materialization_plan(&validated)?;
    Ok((snapshot_json, validated, plan))
}

fn materialize_plan(
    ctx: &ReducerContext,
    owner: Identity,
    queue_kind: String,
    plan: &CombatBuildV2MaterializationPlan,
) {
    ctx.db.match_combat_build_v2().insert(MatchCombatBuildV2 {
        owner,
        queue_kind,
        contract_schema_version: plan.schema_version,
        revision: plan.revision,
        starting_discipline_id: plan.starting_discipline_id.clone(),
        mastery_active: plan.mastery_active,
        materialized_at: ctx.timestamp,
    });

    for selected in &plan.selected_specializations {
        ctx.db
            .match_selected_specialization_v2()
            .insert(MatchSelectedSpecializationV2 {
                key: match_key(owner, &[selected.slot_index.to_string().as_str()]),
                owner,
                slot_index: selected.slot_index,
                specialization_id: selected.specialization_id.clone(),
                combat_discipline_id: selected.combat_discipline_id.clone(),
                specialization_kind: match selected.specialization_kind {
                    combat_build_v2_contract::CombatSpecializationKind::Form => "FORM",
                    combat_build_v2_contract::CombatSpecializationKind::School => "SCHOOL",
                }
                .to_string(),
            });
    }
    for configuration in &plan.discipline_configurations {
        ctx.db
            .match_discipline_configuration_v2()
            .insert(MatchDisciplineConfigurationV2 {
                key: match_key(owner, &[configuration.combat_discipline_id.as_str()]),
                owner,
                combat_discipline_id: configuration.combat_discipline_id.clone(),
                main_hand_item_def_id: configuration.main_hand_item_def_id.clone(),
                main_hand_color_id: configuration.main_hand_color_id.clone(),
                off_hand_item_def_id: configuration.off_hand_item_def_id.clone(),
                off_hand_color_id: configuration.off_hand_color_id.clone(),
            });
    }
    for feature in &plan.techniques {
        insert_technique(ctx, owner, feature);
    }
    for feature in &plan.spells {
        insert_spell(ctx, owner, feature);
    }
    for feature in &plan.perks {
        ctx.db
            .match_perk_selection_v2()
            .insert(MatchPerkSelectionV2 {
                key: match_key(owner, &[feature.ability_id.as_str()]),
                owner,
                specialization_id: feature.specialization_id.clone(),
                combat_discipline_id: feature.combat_discipline_id.clone(),
                ability_id: feature.ability_id.clone(),
            });
    }
    for ability_id in &plan.traits {
        ctx.db
            .match_trait_selection_v2()
            .insert(MatchTraitSelectionV2 {
                key: match_key(owner, &[ability_id.as_str()]),
                owner,
                ability_id: ability_id.clone(),
            });
    }
}

fn insert_technique(ctx: &ReducerContext, owner: Identity, feature: &MaterializedCombatFeatureV2) {
    ctx.db
        .match_technique_selection_v2()
        .insert(MatchTechniqueSelectionV2 {
            key: match_key(owner, &[feature.ability_id.as_str()]),
            owner,
            specialization_id: feature.specialization_id.clone(),
            combat_discipline_id: feature.combat_discipline_id.clone(),
            ability_id: feature.ability_id.clone(),
            bar_order: feature
                .bar_order
                .expect("materialized Technique must have bar order"),
        });
}

fn insert_spell(ctx: &ReducerContext, owner: Identity, feature: &MaterializedCombatFeatureV2) {
    ctx.db
        .match_spell_selection_v2()
        .insert(MatchSpellSelectionV2 {
            key: match_key(owner, &[feature.ability_id.as_str()]),
            owner,
            specialization_id: feature.specialization_id.clone(),
            combat_discipline_id: feature.combat_discipline_id.clone(),
            ability_id: feature.ability_id.clone(),
            bar_order: feature
                .bar_order
                .expect("materialized Spell must have bar order"),
        });
}

fn match_key(owner: Identity, parts: &[&str]) -> String {
    let mut key = owner.to_hex().to_string();
    for part in parts {
        key.push(':');
        key.push_str(part);
    }
    key
}

fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut encoded = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        encoded.push(HEX[(byte >> 4) as usize] as char);
        encoded.push(HEX[(byte & 0x0f) as usize] as char);
    }
    encoded
}

fn hex_decode(value: &str) -> Result<Vec<u8>, String> {
    if value.is_empty() || value.len() % 2 != 0 {
        return Err(
            "COMBAT_BUILD_V2_SNAPSHOT_HEX_INVALID: envelope must be nonempty even-length hex"
                .to_string(),
        );
    }
    value
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            let high = hex_nibble(pair[0])?;
            let low = hex_nibble(pair[1])?;
            Ok((high << 4) | low)
        })
        .collect()
}

fn hex_nibble(value: u8) -> Result<u8, String> {
    match value {
        b'0'..=b'9' => Ok(value - b'0'),
        b'a'..=b'f' => Ok(value - b'a' + 10),
        _ => {
            Err("COMBAT_BUILD_V2_SNAPSHOT_HEX_INVALID: envelope must use lowercase hex".to_string())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use combat_build_v2_contract::{
        CombatBuildV2DisciplineConfiguration, CombatBuildV2Draft, CombatBuildV2Snapshot,
        CombatFeatureSelection, SelectedCombatSpecialization, COMBAT_BUILD_V2_SCHEMA_VERSION,
        MASTERY_TRAIT_ID,
    };

    fn three_school_snapshot() -> CombatBuildV2Snapshot {
        let catalog = CombatBuildV2Catalog::from_shared_catalogs().unwrap();
        let draft = CombatBuildV2Draft {
            schema_version: COMBAT_BUILD_V2_SCHEMA_VERSION,
            revision: 12,
            starting_discipline_id: Some("STAFF".to_string()),
            selected_specializations: ["BLIGHT", "MORTALITY", "RUIN"]
                .into_iter()
                .enumerate()
                .map(
                    |(slot_index, specialization_id)| SelectedCombatSpecialization {
                        slot_index: slot_index as u8,
                        specialization_id: specialization_id.to_string(),
                    },
                )
                .collect(),
            dormant_specializations: Vec::new(),
            discipline_configurations: vec![CombatBuildV2DisciplineConfiguration {
                combat_discipline_id: "STAFF".to_string(),
                main_hand_item_def_id: "NEWBIE_STAFF_01".to_string(),
                main_hand_color_id: String::new(),
                off_hand_item_def_id: String::new(),
                off_hand_color_id: String::new(),
            }],
            selected_features: vec![
                feature("BLIGHT", "SPELL_ICICLE", Some(2)),
                feature("BLIGHT", "BLIGHT_TOXIC_WEAPON", None),
                feature("MORTALITY", "SPELL_VAMPIRIC_ORB", Some(1)),
                feature("RUIN", "SPELL_FIREBALL", Some(0)),
            ],
            selected_traits: vec![MASTERY_TRAIT_ID.to_string()],
        };
        catalog.validate_draft(&draft, 12).unwrap().snapshot
    }

    fn feature(
        specialization_id: &str,
        ability_id: &str,
        preferred_bar_order: Option<u8>,
    ) -> CombatFeatureSelection {
        CombatFeatureSelection {
            specialization_id: specialization_id.to_string(),
            ability_id: ability_id.to_string(),
            preferred_bar_order,
        }
    }

    fn encoded_fixture() -> String {
        let catalog = CombatBuildV2Catalog::from_shared_catalogs().unwrap();
        let snapshot_json = catalog
            .serialize_canonical_snapshot(&three_school_snapshot())
            .unwrap();
        hex_encode(snapshot_json.as_bytes())
    }

    #[test]
    fn pvp_open_world_and_local_direct_share_one_selected_only_plan() {
        let (_, _, plan) = validated_payload(encoded_fixture().as_str()).unwrap();
        for queue_kind in [QUEUE_UNRANKED, QUEUE_OPEN_WORLD, QUEUE_LOCAL_DIRECT] {
            assert!(matches!(
                queue_kind,
                QUEUE_UNRANKED | QUEUE_OPEN_WORLD | QUEUE_LOCAL_DIRECT
            ));
            assert_eq!(plan.selected_specializations.len(), 3);
            assert_eq!(plan.parent_discipline_ids, ["STAFF"]);
            assert_eq!(plan.discipline_configurations.len(), 1);
            assert!(plan.techniques.is_empty());
            assert_eq!(plan.spells.len(), 3);
            assert_eq!(plan.perks.len(), 1);
            assert_eq!(plan.traits, [MASTERY_TRAIT_ID]);
            assert!(plan.mastery_active);
        }
    }

    #[test]
    fn old_version_and_noncanonical_bytes_fail_before_materialization() {
        let snapshot = three_school_snapshot();
        let pretty = serde_json::to_string_pretty(&snapshot).unwrap();
        assert!(validated_payload(hex_encode(pretty.as_bytes()).as_str())
            .is_err_and(|error| error.starts_with("COMBAT_BUILD_V2_SNAPSHOT_NOT_CANONICAL")));

        let mut old = snapshot;
        old.schema_version = 1;
        let old_json = serde_json::to_string(&old).unwrap();
        assert!(
            validated_payload(hex_encode(old_json.as_bytes()).as_str()).is_err_and(|error| {
                error.starts_with("COMBAT_BUILD_V2_UNSUPPORTED_SCHEMA_VERSION")
            })
        );
    }

    #[test]
    fn exact_snapshot_bytes_survive_the_transport_envelope() {
        let encoded = encoded_fixture();
        let decoded = hex_decode(encoded.as_str()).unwrap();
        assert_eq!(hex_encode(decoded.as_slice()), encoded);
        assert!(hex_decode("ABC0").is_err());
        assert!(hex_decode("0").is_err());
    }
}
