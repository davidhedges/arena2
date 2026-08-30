#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MATCH_MODULE="$REPO_ROOT/match-v2-rehearsal"
DATABASE="arena-cbv2-p5-$(date -u +%Y%m%d%H%M%S)-$$"
SERVER="local"
PUBLISHED=0

case "$DATABASE" in
    arena-cbv2-p5-*) ;;
    *)
        echo "Refusing unsafe Phase 5 database name: $DATABASE" >&2
        exit 1
        ;;
esac

cleanup() {
    if [[ "$PUBLISHED" == "1" ]]; then
        spacetime delete -s "$SERVER" --yes "$DATABASE" >/dev/null
        echo "Retired disposable Phase 5 database: $DATABASE"
    fi
}
trap cleanup EXIT

row_count() {
    local database="$1"
    local table_name="$2"
    spacetime sql -s "$SERVER" "$database" \
        "SELECT COUNT(*) AS row_count FROM $table_name" 2>/dev/null \
        | awk '/^[[:space:]]*[0-9]+[[:space:]]*$/ { gsub(/[[:space:]]/, ""); print; exit }'
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

CANONICAL_BEFORE="$(canonical_v1_counts)"

echo "Publishing disposable Phase 5 switching rehearsal: $DATABASE"
spacetime publish \
    -s "$SERVER" \
    --delete-data=never \
    --yes \
    -p "$MATCH_MODULE" \
    "$DATABASE"
PUBLISHED=1

spacetime call \
    -s "$SERVER" \
    --anonymous \
    --yes \
    "$DATABASE" \
    run_phase_5_switch_interrupt_probe

RESULT_OUTPUT="$(spacetime sql -s "$SERVER" "$DATABASE" \
    'SELECT distinct_parent_targets_passed, repeated_parent_deduplicated, switch_reset_passed, switch_cancel_passed, interrupt_matrix_passed, immediate_cancel_phase_passed, spell_all_disciplines_passed, staff_no_technique_passed, staff_auto_attack_passed, spell_bar_stable, blessed_shield_disposition_passed FROM phase5_interrupt_probe_result')"
echo "$RESULT_OUTPUT"
if [[ "$(grep -o 'true' <<<"$RESULT_OUTPUT" | wc -l | tr -d ' ')" != "11" ]]; then
    echo "Phase 5 switching probe did not report all eleven checks as true" >&2
    exit 1
fi

TARGET_OUTPUT="$(spacetime sql -s "$SERVER" "$DATABASE" \
    'SELECT switch_order, combat_discipline_id FROM phase5_switch_target_v2')"
echo "$TARGET_OUTPUT"
if [[ "$(grep -o 'DAGGERS\|STAFF' <<<"$TARGET_OUTPUT" | wc -l | tr -d ' ')" != "2" ]]; then
    echo "Phase 5 switch targets were not exactly DAGGERS and STAFF" >&2
    exit 1
fi

COUNTS="$(
    printf '%s\n' \
        "$(row_count "$DATABASE" match_combat_build_v2)" \
        "$(row_count "$DATABASE" match_selected_specialization_v2)" \
        "$(row_count "$DATABASE" match_discipline_configuration_v2)" \
        "$(row_count "$DATABASE" match_technique_selection_v2)" \
        "$(row_count "$DATABASE" match_spell_selection_v2)" \
        "$(row_count "$DATABASE" match_perk_selection_v2)" \
        "$(row_count "$DATABASE" match_trait_selection_v2)" \
        "$(row_count "$DATABASE" active_combat_build_discipline_v2)" \
        "$(row_count "$DATABASE" phase5_switch_target_v2)" \
        "$(row_count "$DATABASE" phase5_interrupt_probe_result)" \
    | tr '\n' ':'
)"
if [[ "$COUNTS" != "1:3:2:2:1:0:0:1:2:1:" ]]; then
    echo "Unexpected Phase 5 normalized runtime counts: $COUNTS" >&2
    exit 1
fi

CANONICAL_AFTER="$(canonical_v1_counts)"
if [[ "$CANONICAL_BEFORE" != "$CANONICAL_AFTER" ]]; then
    echo "Canonical v1 Hub combat-build row counts changed during Phase 5" >&2
    exit 1
fi

echo "Canonical v1 Hub combat-build row counts were unchanged."
echo "PHASE5_REHEARSAL_PASS database=$DATABASE"
