use std::time::Duration;

use spacetimedb::{table, Identity, ReducerContext, SpacetimeType, Table, Timestamp};

#[allow(unused_imports)]
use crate::action_prediction::predicted_action_result as _;
use crate::combat::timestamp_to_micros;

const PREDICTED_ACTION_ID_MAX_LEN: usize = 64;
const PREDICTED_ACTION_RESULT_TTL_MS: u64 = 10_000;

#[derive(Clone, Copy, Debug, PartialEq, Eq, SpacetimeType)]
pub enum PredictedActionFamily {
    SpellCast,
    Melee,
    Movement,
    Defense,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, SpacetimeType)]
pub enum ActionResultKind {
    Accepted,
    Rejected,
    Canceled,
    CancelTooLate,
    StaleToken,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ActionPredictionToken {
    pub predicted_action_id: String,
    pub client_action_seq: u64,
}

impl ActionPredictionToken {
    pub fn new(predicted_action_id: String, client_action_seq: u64) -> Option<Self> {
        if !valid_predicted_action_token(predicted_action_id.as_str(), client_action_seq) {
            return None;
        }

        Some(Self {
            predicted_action_id,
            client_action_seq,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum OptionalActionPredictionToken {
    Legacy,
    Invalid,
    Predicted(ActionPredictionToken),
}

#[table(accessor = predicted_action_result, public)]
#[derive(Clone)]
pub struct PredictedActionResult {
    #[primary_key]
    #[auto_inc]
    pub event_id: u64,
    #[index(btree)]
    pub owner: Identity,
    pub family: PredictedActionFamily,
    pub action_instance_id: String,
    pub predicted_action_id: String,
    pub client_action_seq: u64,
    pub result: ActionResultKind,
    pub created_at: Timestamp,
    #[index(btree)]
    pub created_at_micros: i64,
    #[index(btree)]
    pub expires_at_micros: i64,
}

pub(crate) fn valid_predicted_action_token(
    predicted_action_id: &str,
    client_action_seq: u64,
) -> bool {
    client_action_seq != 0
        && !predicted_action_id.is_empty()
        && predicted_action_id.len() <= PREDICTED_ACTION_ID_MAX_LEN
        && predicted_action_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
}

pub(crate) fn optional_action_prediction_token(
    predicted_action_id: String,
    client_action_seq: u64,
) -> OptionalActionPredictionToken {
    let input_was_present = !predicted_action_id.is_empty() || client_action_seq != 0;
    match ActionPredictionToken::new(predicted_action_id, client_action_seq) {
        Some(token) => OptionalActionPredictionToken::Predicted(token),
        None if input_was_present => OptionalActionPredictionToken::Invalid,
        None => OptionalActionPredictionToken::Legacy,
    }
}

pub(crate) fn has_predicted_action_result(
    ctx: &ReducerContext,
    owner: Identity,
    family: PredictedActionFamily,
    token: &ActionPredictionToken,
) -> bool {
    ctx.db.predicted_action_result().iter().any(|row| {
        row.owner == owner
            && row.family == family
            && row.predicted_action_id == token.predicted_action_id
            && row.client_action_seq == token.client_action_seq
    })
}

pub(crate) fn record_predicted_action_result(
    ctx: &ReducerContext,
    owner: Identity,
    family: PredictedActionFamily,
    token: &ActionPredictionToken,
    action_instance_id: &str,
    result: ActionResultKind,
    now: Timestamp,
) {
    if has_predicted_action_result(ctx, owner, family, token) {
        return;
    }

    insert_predicted_action_result(ctx, owner, family, token, action_instance_id, result, now);
}

pub(crate) fn insert_predicted_action_result(
    ctx: &ReducerContext,
    owner: Identity,
    family: PredictedActionFamily,
    token: &ActionPredictionToken,
    action_instance_id: &str,
    result: ActionResultKind,
    now: Timestamp,
) {
    ctx.db
        .predicted_action_result()
        .insert(PredictedActionResult {
            event_id: 0,
            owner,
            family,
            action_instance_id: action_instance_id.to_string(),
            predicted_action_id: token.predicted_action_id.clone(),
            client_action_seq: token.client_action_seq,
            result,
            created_at: now,
            created_at_micros: timestamp_to_micros(now),
            expires_at_micros: timestamp_to_micros(
                now + Duration::from_millis(PREDICTED_ACTION_RESULT_TTL_MS),
            ),
        });
}

pub(crate) fn prune_predicted_action_results(ctx: &ReducerContext, now: Timestamp) {
    let now_micros = timestamp_to_micros(now);
    let stale: Vec<u64> = ctx
        .db
        .predicted_action_result()
        .expires_at_micros()
        .filter(..=now_micros)
        .map(|row| row.event_id)
        .collect();
    for event_id in stale {
        ctx.db.predicted_action_result().event_id().delete(event_id);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn action_prediction_token_accepts_expected_wire_ids() {
        let token = ActionPredictionToken::new("abc-DEF_0123456789".to_string(), 1);

        assert!(token.is_some());
    }

    #[test]
    fn action_prediction_token_rejects_empty_or_zero_sequence() {
        assert!(ActionPredictionToken::new(String::new(), 1).is_none());
        assert!(ActionPredictionToken::new("abc".to_string(), 0).is_none());
    }

    #[test]
    fn action_prediction_token_rejects_invalid_or_oversized_ids() {
        let oversized = "a".repeat(PREDICTED_ACTION_ID_MAX_LEN + 1);

        assert!(ActionPredictionToken::new("abc:1".to_string(), 1).is_none());
        assert!(ActionPredictionToken::new("abc 1".to_string(), 1).is_none());
        assert!(ActionPredictionToken::new(oversized, 1).is_none());
    }

    #[test]
    fn optional_action_prediction_token_preserves_legacy_unpredicted_calls() {
        assert_eq!(
            optional_action_prediction_token(String::new(), 0),
            OptionalActionPredictionToken::Legacy
        );
    }

    #[test]
    fn optional_action_prediction_token_accepts_valid_predicted_calls() {
        let result = optional_action_prediction_token("dodge-123".to_string(), 42);

        match result {
            OptionalActionPredictionToken::Predicted(token) => {
                assert_eq!(token.predicted_action_id, "dodge-123");
                assert_eq!(token.client_action_seq, 42);
            }
            other => panic!("expected predicted token, got {other:?}"),
        }
    }

    #[test]
    fn optional_action_prediction_token_rejects_partial_or_malformed_inputs() {
        assert_eq!(
            optional_action_prediction_token(String::new(), 42),
            OptionalActionPredictionToken::Invalid
        );
        assert_eq!(
            optional_action_prediction_token("dodge-123".to_string(), 0),
            OptionalActionPredictionToken::Invalid
        );
        assert_eq!(
            optional_action_prediction_token("dodge:123".to_string(), 42),
            OptionalActionPredictionToken::Invalid
        );
    }
}
