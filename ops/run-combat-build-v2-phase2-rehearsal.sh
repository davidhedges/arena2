#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REHEARSAL_MODULE="$REPO_ROOT/hub-v2-rehearsal"
DATABASE="arena-cbv2-p2-$(date -u +%Y%m%d%H%M%S)-$$"
SERVER="local"
PUBLISHED=0

case "$DATABASE" in
    arena-cbv2-p2-*) ;;
    *)
        echo "Refusing unsafe rehearsal database name: $DATABASE" >&2
        exit 1
        ;;
esac

cleanup() {
    if [[ "$PUBLISHED" == "1" ]]; then
        spacetime delete -s "$SERVER" --yes "$DATABASE" >/dev/null
        echo "Retired disposable Phase 2 database: $DATABASE"
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

echo "Publishing isolated Phase 2 rehearsal database: $DATABASE"
spacetime publish \
    -s "$SERVER" \
    --delete-data=never \
    --yes \
    -p "$REHEARSAL_MODULE" \
    "$DATABASE"
PUBLISHED=1

echo "Running anonymous live save/reload/rejection probe..."
spacetime call -s "$SERVER" --anonymous --yes "$DATABASE" run_phase_2_live_probe

PROBE_OUTPUT="$(spacetime sql -s "$SERVER" "$DATABASE" \
    'SELECT final_revision, three_school_reload_passed, same_parent_reload_passed, dormant_restore_passed, stale_rejection_passed, invalid_rollback_passed, mastery_predicate_passed FROM phase2_probe_result')"
echo "$PROBE_OUTPUT"

TRUE_COUNT="$(printf '%s\n' "$PROBE_OUTPUT" | grep -o 'true' | wc -l | tr -d ' ')"
if [[ "$TRUE_COUNT" != "6" ]]; then
    echo "Phase 2 probe did not report all six live checks as true" >&2
    exit 1
fi

SPECIALIZATION_COUNT="$(row_count "$DATABASE" combat_specialization_definition_v2)"
FEATURE_DEFINITION_COUNT="$(row_count "$DATABASE" combat_feature_definition_v2)"
TRAIT_DEFINITION_COUNT="$(row_count "$DATABASE" combat_trait_definition_v2)"
echo "Catalog counts: specializations=$SPECIALIZATION_COUNT features=$FEATURE_DEFINITION_COUNT traits=$TRAIT_DEFINITION_COUNT"
if [[ "$SPECIALIZATION_COUNT:$FEATURE_DEFINITION_COUNT:$TRAIT_DEFINITION_COUNT" != "18:208:1" ]]; then
    echo "Phase 2 public catalog counts are not 18 / 208 / 1" >&2
    exit 1
fi

ROOT_COUNT="$(row_count "$DATABASE" combat_build_v2)"
SELECTED_COUNT="$(row_count "$DATABASE" selected_specialization_v2)"
DORMANT_COUNT="$(row_count "$DATABASE" dormant_specialization_v2)"
CONFIGURATION_COUNT="$(row_count "$DATABASE" discipline_configuration_v2)"
FEATURE_SELECTION_COUNT="$(row_count "$DATABASE" specialization_feature_selection_v2)"
TRAIT_SELECTION_COUNT="$(row_count "$DATABASE" trait_selection_v2)"
echo "Aggregate counts: roots=$ROOT_COUNT selected=$SELECTED_COUNT dormant=$DORMANT_COUNT configurations=$CONFIGURATION_COUNT features=$FEATURE_SELECTION_COUNT traits=$TRAIT_SELECTION_COUNT"
if [[ "$ROOT_COUNT:$SELECTED_COUNT:$DORMANT_COUNT:$CONFIGURATION_COUNT:$FEATURE_SELECTION_COUNT:$TRAIT_SELECTION_COUNT" != "1:2:0:1:2:1" ]]; then
    echo "Phase 2 final aggregate row counts are unexpected" >&2
    exit 1
fi

CANONICAL_AFTER="$(canonical_v1_counts)"
if [[ "$CANONICAL_BEFORE" != "$CANONICAL_AFTER" ]]; then
    echo "Canonical v1 Hub combat-build row counts changed during rehearsal" >&2
    exit 1
fi

echo "Canonical v1 Hub combat-build row counts were unchanged."
echo "PHASE2_REHEARSAL_PASS database=$DATABASE"
