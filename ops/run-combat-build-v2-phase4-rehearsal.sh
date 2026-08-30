#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MATCH_MODULE="$REPO_ROOT/match-v2-rehearsal"
DATABASE="arena-cbv2-p4-$(date -u +%Y%m%d%H%M%S)-$$"
SERVER="local"
PUBLISHED=0

case "$DATABASE" in
    arena-cbv2-p4-*) ;;
    *)
        echo "Refusing unsafe Phase 4 database name: $DATABASE" >&2
        exit 1
        ;;
esac

cleanup() {
    if [[ "$PUBLISHED" == "1" ]]; then
        spacetime delete -s "$SERVER" --yes "$DATABASE" >/dev/null
        echo "Retired disposable Phase 4 database: $DATABASE"
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

echo "Publishing disposable Phase 4 authorization rehearsal: $DATABASE"
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
    run_phase_4_authorization_probe

RESULT_OUTPUT="$(spacetime sql -s "$SERVER" "$DATABASE" \
    'SELECT spell_all_disciplines_passed, wrong_weapon_technique_passed, staff_no_technique_passed, perk_scope_passed, trait_scope_passed, dormant_unselected_fail_closed, persistent_active_membership_passed, mastery_damage_paths_passed FROM phase4_authorization_probe_result')"
echo "$RESULT_OUTPUT"
if [[ "$(grep -o 'true' <<<"$RESULT_OUTPUT" | wc -l | tr -d ' ')" != "8" ]]; then
    echo "Phase 4 authorization probe did not report all eight checks as true" >&2
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
        "$(row_count "$DATABASE" phase4_authorization_probe_result)" \
    | tr '\n' ':'
)"
if [[ "$COUNTS" != "1:3:3:2:1:1:1:1:1:" ]]; then
    echo "Unexpected Phase 4 normalized runtime counts: $COUNTS" >&2
    exit 1
fi

CANONICAL_AFTER="$(canonical_v1_counts)"
if [[ "$CANONICAL_BEFORE" != "$CANONICAL_AFTER" ]]; then
    echo "Canonical v1 Hub combat-build row counts changed during Phase 4" >&2
    exit 1
fi

echo "Canonical v1 Hub combat-build row counts were unchanged."
echo "PHASE4_REHEARSAL_PASS database=$DATABASE"
