#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REHEARSAL_MODULE="$REPO_ROOT/hub-v2-rehearsal"
CLIENT_PROJECT="$REPO_ROOT/client-v2-rehearsal/CombatBuildV2ClientRehearsal.csproj"
DATABASE="arena-cbv2-p6-$(date -u +%Y%m%d%H%M%S)-$$"
SERVER="local"
PUBLISHED=0

case "$DATABASE" in
    arena-cbv2-p6-*) ;;
    *)
        echo "Refusing unsafe Phase 6 rehearsal database name: $DATABASE" >&2
        exit 1
        ;;
esac

cleanup() {
    if [[ "$PUBLISHED" == "1" ]]; then
        spacetime delete -s "$SERVER" --yes "$DATABASE" >/dev/null
        echo "Retired disposable Phase 6 database: $DATABASE"
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

"$REPO_ROOT/ops/build-combat-build-v2-phase6-bindings.sh"

echo "Running transport-neutral editor/HUD behavior rehearsal..."
dotnet run --project "$CLIENT_PROJECT"

echo "Publishing isolated Phase 6 client rehearsal database: $DATABASE"
spacetime publish \
    -s "$SERVER" \
    --delete-data=never \
    --yes \
    -p "$REHEARSAL_MODULE" \
    "$DATABASE"
PUBLISHED=1

echo "Running generated-binding subscribe/save/reload rehearsal..."
dotnet run --project "$CLIENT_PROJECT" -- --live http://127.0.0.1:3000 "$DATABASE"

SPECIALIZATION_COUNT="$(row_count "$DATABASE" combat_specialization_definition_v2)"
FEATURE_DEFINITION_COUNT="$(row_count "$DATABASE" combat_feature_definition_v2)"
TRAIT_DEFINITION_COUNT="$(row_count "$DATABASE" combat_trait_definition_v2)"
echo "Catalog counts: specializations=$SPECIALIZATION_COUNT features=$FEATURE_DEFINITION_COUNT traits=$TRAIT_DEFINITION_COUNT"
if [[ "$SPECIALIZATION_COUNT:$FEATURE_DEFINITION_COUNT:$TRAIT_DEFINITION_COUNT" != "18:208:1" ]]; then
    echo "Phase 6 public catalog counts are not 18 / 208 / 1" >&2
    exit 1
fi

ROOT_COUNT="$(row_count "$DATABASE" combat_build_v2)"
SELECTED_COUNT="$(row_count "$DATABASE" selected_specialization_v2)"
CONFIGURATION_COUNT="$(row_count "$DATABASE" discipline_configuration_v2)"
FEATURE_SELECTION_COUNT="$(row_count "$DATABASE" specialization_feature_selection_v2)"
TRAIT_SELECTION_COUNT="$(row_count "$DATABASE" trait_selection_v2)"
echo "Aggregate counts: roots=$ROOT_COUNT selected=$SELECTED_COUNT configurations=$CONFIGURATION_COUNT features=$FEATURE_SELECTION_COUNT traits=$TRAIT_SELECTION_COUNT"
if [[ "$ROOT_COUNT:$SELECTED_COUNT:$CONFIGURATION_COUNT:$FEATURE_SELECTION_COUNT:$TRAIT_SELECTION_COUNT" != "1:2:2:3:1" ]]; then
    echo "Phase 6 live client left unexpected aggregate counts" >&2
    exit 1
fi

CANONICAL_AFTER="$(canonical_v1_counts)"
if [[ "$CANONICAL_BEFORE" != "$CANONICAL_AFTER" ]]; then
    echo "Canonical v1 Hub combat-build row counts changed during Phase 6" >&2
    exit 1
fi

echo "Canonical v1 Hub combat-build row counts were unchanged."
echo "PHASE6_REHEARSAL_PASS database=$DATABASE"
