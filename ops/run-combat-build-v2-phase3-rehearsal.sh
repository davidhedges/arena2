#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HUB_MODULE="$REPO_ROOT/hub-v2-rehearsal"
MATCH_MODULE="$REPO_ROOT/match-v2-rehearsal"
RUN_ID="$(date -u +%Y%m%d%H%M%S)-$$"
HUB_DATABASE="arena-cbv2-p3-hub-$RUN_ID"
PVP_DATABASE="arena-cbv2-p3-pvp-$RUN_ID"
OPEN_WORLD_DATABASE="arena-cbv2-p3-world-$RUN_ID"
LOCAL_DIRECT_DATABASE="arena-cbv2-p3-direct-$RUN_ID"
SERVER="local"
PUBLISHED_DATABASES=()

for database in \
    "$HUB_DATABASE" \
    "$PVP_DATABASE" \
    "$OPEN_WORLD_DATABASE" \
    "$LOCAL_DIRECT_DATABASE"
do
    case "$database" in
        arena-cbv2-p3-*) ;;
        *)
            echo "Refusing unsafe Phase 3 database name: $database" >&2
            exit 1
            ;;
    esac
done

cleanup() {
    local database
    for database in "${PUBLISHED_DATABASES[@]}"
    do
        spacetime delete -s "$SERVER" --yes "$database" >/dev/null
        echo "Retired disposable Phase 3 database: $database"
    done
}
trap cleanup EXIT

row_count() {
    local database="$1"
    local table_name="$2"
    spacetime sql -s "$SERVER" "$database" \
        "SELECT COUNT(*) AS row_count FROM $table_name" 2>/dev/null \
        | awk '/^[[:space:]]*[0-9]+[[:space:]]*$/ { gsub(/[[:space:]]/, ""); print; exit }'
}

single_hex_value() {
    local database="$1"
    local table_name="$2"
    local column_name="$3"
    local sql_output
    local parsed
    sql_output="$(spacetime sql -s "$SERVER" "$database" \
        "SELECT $column_name FROM $table_name" 2>/dev/null)"
    parsed="$(awk '
        {
            value = $0
            gsub(/[[:space:]"|]/, "", value)
            if (value ~ /^[0-9a-f]+$/) {
                print value
                exit
            }
        }
    ' <<<"$sql_output")"
    if [[ -z "$parsed" ]]; then
        echo "Could not parse $table_name.$column_name from SQL output:" >&2
        echo "$sql_output" >&2
        return 1
    fi
    echo "$parsed"
}

canonical_v1_counts() {
    local table_name
    for table_name in \
        combat_build \
        combat_build_discipline \
        discipline_configuration \
        staff_school_selection \
        discipline_action_bar_assignment \
        discipline_passive_selection
    do
        row_count arena-hub-local "$table_name"
    done
}

publish_rehearsal() {
    local database="$1"
    local module="$2"
    spacetime publish \
        -s "$SERVER" \
        --delete-data=never \
        --yes \
        -p "$module" \
        "$database"
    PUBLISHED_DATABASES+=("$database")
}

assert_match_projection() {
    local database="$1"
    local expected_queue="$2"
    local actual_hex
    local result_output

    result_output="$(spacetime sql -s "$SERVER" "$database" \
        'SELECT queue_kind, contract_schema_version, combat_build_revision, reservation_bytes_equal, selected_specialization_count, parent_discipline_count, technique_count, spell_count, perk_count, trait_count, mastery_active FROM phase3_match_probe_result')"
    echo "$result_output"
    if ! grep -q "$expected_queue" <<<"$result_output"; then
        echo "Phase 3 match result did not report queue $expected_queue" >&2
        exit 1
    fi
    if [[ "$(grep -o 'true' <<<"$result_output" | wc -l | tr -d ' ')" != "2" ]]; then
        echo "Phase 3 reservation/Mastery checks were not both true" >&2
        exit 1
    fi

    actual_hex="$(single_hex_value "$database" phase3_match_probe_result snapshot_json_hex)"
    if [[ -z "$actual_hex" || "$actual_hex" != "$SNAPSHOT_JSON_HEX" ]]; then
        echo "Phase 3 match did not preserve exact canonical snapshot bytes" >&2
        exit 1
    fi

    local counts
    counts="$(
        printf '%s\n' \
            "$(row_count "$database" match_combat_build_v2)" \
            "$(row_count "$database" match_selected_specialization_v2)" \
            "$(row_count "$database" match_discipline_configuration_v2)" \
            "$(row_count "$database" match_technique_selection_v2)" \
            "$(row_count "$database" match_spell_selection_v2)" \
            "$(row_count "$database" match_perk_selection_v2)" \
            "$(row_count "$database" match_trait_selection_v2)" \
        | tr '\n' ':'
    )"
    if [[ "$counts" != "1:3:1:0:3:1:1:" ]]; then
        echo "Unexpected Phase 3 match aggregate counts: $counts" >&2
        exit 1
    fi
}

CANONICAL_BEFORE="$(canonical_v1_counts)"

echo "Publishing isolated Phase 3 Hub rehearsal: $HUB_DATABASE"
publish_rehearsal "$HUB_DATABASE" "$HUB_MODULE"
spacetime call \
    -s "$SERVER" \
    --anonymous \
    --yes \
    "$HUB_DATABASE" \
    prepare_phase_3_three_school_handoff

HUB_RESULT="$(spacetime sql -s "$SERVER" "$HUB_DATABASE" \
    'SELECT contract_schema_version, combat_build_revision, selected_specialization_count, parent_discipline_count, technique_count, spell_count, perk_count, trait_count, mastery_active FROM phase3_handoff_result')"
echo "$HUB_RESULT"
if [[ "$(grep -o 'true' <<<"$HUB_RESULT" | wc -l | tr -d ' ')" != "1" ]]; then
    echo "Phase 3 Hub handoff did not report active Mastery" >&2
    exit 1
fi

SNAPSHOT_JSON_HEX="$(single_hex_value "$HUB_DATABASE" phase3_handoff_result snapshot_json_hex)"
if [[ -z "$SNAPSHOT_JSON_HEX" ]]; then
    echo "Could not read the Phase 3 canonical snapshot envelope" >&2
    exit 1
fi
if [[ "$(single_hex_value "$HUB_DATABASE" match_player_combat_build_snapshot_v2 combat_build_snapshot_json_hex)" != "$SNAPSHOT_JSON_HEX" ]]; then
    echo "Phase 3 Hub ticket bytes differ from the aggregate handoff bytes" >&2
    exit 1
fi

echo "Publishing disposable PvP materialization rehearsal: $PVP_DATABASE"
publish_rehearsal "$PVP_DATABASE" "$MATCH_MODULE"
spacetime call \
    -s "$SERVER" \
    --anonymous \
    --yes \
    "$PVP_DATABASE" \
    bootstrap_v_2_handoff \
    UNRANKED \
    "$SNAPSHOT_JSON_HEX" \
    IRON
assert_match_projection "$PVP_DATABASE" UNRANKED

echo "Publishing disposable open-world materialization rehearsal: $OPEN_WORLD_DATABASE"
publish_rehearsal "$OPEN_WORLD_DATABASE" "$MATCH_MODULE"
spacetime call \
    -s "$SERVER" \
    --anonymous \
    --yes \
    "$OPEN_WORLD_DATABASE" \
    bootstrap_v_2_handoff \
    OPEN_WORLD \
    "$SNAPSHOT_JSON_HEX" \
    IRON
assert_match_projection "$OPEN_WORLD_DATABASE" OPEN_WORLD

echo "Publishing disposable local-direct admission rehearsal: $LOCAL_DIRECT_DATABASE"
publish_rehearsal "$LOCAL_DIRECT_DATABASE" "$MATCH_MODULE"
OLD_SNAPSHOT_JSON_HEX="$(python3 -c 'import json,sys; value=json.loads(bytes.fromhex(sys.argv[1])); value["schema_version"]=1; print(json.dumps(value,separators=(",",":")).encode().hex())' "$SNAPSHOT_JSON_HEX")"
if spacetime call \
    -s "$SERVER" \
    --anonymous \
    --yes \
    "$LOCAL_DIRECT_DATABASE" \
    admit_local_direct_v_2_fixture \
    "$OLD_SNAPSHOT_JSON_HEX"
then
    echo "Old-version local-direct fixture was unexpectedly accepted" >&2
    exit 1
fi
if [[ "$(row_count "$LOCAL_DIRECT_DATABASE" match_combat_build_v2)" != "0" ]]; then
    echo "Rejected old-version fixture left match state behind" >&2
    exit 1
fi
spacetime call \
    -s "$SERVER" \
    --anonymous \
    --yes \
    "$LOCAL_DIRECT_DATABASE" \
    admit_local_direct_v_2_fixture \
    "$SNAPSHOT_JSON_HEX"
assert_match_projection "$LOCAL_DIRECT_DATABASE" LOCAL_DIRECT

CANONICAL_AFTER="$(canonical_v1_counts)"
if [[ "$CANONICAL_BEFORE" != "$CANONICAL_AFTER" ]]; then
    echo "Canonical v1 Hub combat-build row counts changed during Phase 3" >&2
    exit 1
fi

echo "Canonical v1 Hub combat-build row counts were unchanged."
echo "PHASE3_REHEARSAL_PASS hub=$HUB_DATABASE pvp=$PVP_DATABASE world=$OPEN_WORLD_DATABASE direct=$LOCAL_DIRECT_DATABASE"
