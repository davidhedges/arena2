# Combat-build progression cutover — Phase 5 evidence

Date: 2026-08-26

Status: **COMPLETE — Unity consumes and saves the canonical combat build,
gameplay HUD/input reads exact frozen per-discipline bars, legacy Unity writers
are inert, and the Phase 5 exit gate passes. Phases 6 and 7 were not started.**

## 1. Approved boundary

This slice implemented only Phase 5 of
`docs/combat-build-progression-cutover-plan-2026-08-26.md`:

- consume the generated Hub and match combat-build bindings;
- introduce one client draft that mirrors the canonical whole-build contract;
- remove Unity primary/secondary semantics and local budget validation;
- make gameplay HUD/input read the current discipline's exact frozen bar;
- retain equipment editing only through an atomic whole-build save; and
- disable old editors that cannot safely write the canonical contract.

This slice did not build or restyle the replacement Disciplines UI. It did not
remove generated compatibility schema, data-preserving server tombstones,
collection-only spell rows, or the unreachable legacy Toolkit assets assigned
to Phases 6 and 7.

## 2. One Unity Hub model and writer

`HubNetworkManager` now subscribes to generated `MyCombatBuild`, caches one
`HubCombatBuildDraft`, and saves only through generated `SaveCombatBuild`.
`HubCombatBuildDraft` mirrors:

- revision and optional starting discipline;
- ordered selected disciplines;
- per-discipline weapon/color configuration;
- Staff school IDs;
- exact active slot assignments; and
- selected passive IDs.

The draft contains no budget constants and performs no competing validation.
`ToReducerInput()` sends the whole revision-checked draft to the Hub, where the
existing server contract remains authoritative.

The old `SaveDisciplineLoadout` and `SaveWeaponLoadout` client methods and
completion events were removed. The remaining legacy `MyHubLoadout` read is
restricted to the separately scoped armor snapshot; it is not a combat-build
DTO or writer.

## 3. Ordered Hub presentation and atomic equipment editing

The Hub summary renders three equal ordered slots. It no longer labels or
interprets any selection as primary or secondary. The showcase uses the
configured starting discipline, falling back deterministically to the first
selected discipline.

Equipment editing updates the starting/first selected discipline's preserved
configuration with `HubCombatBuildDraft.WithWeapon(...)`, then calls the same
atomic `SaveCombatBuild` contract. School choices, exact active assignments,
passives, dormant configurations, order, and revision remain in the submitted
draft. There is no global weapon-pair save.

The generated Hub weapon catalog still exposes a legacy
`PrimaryDisciplineId` metadata field. The Equipment screen isolates that field
behind `WeaponCatalogDisciplineId` solely to list compatible weapon definitions
for a canonical discipline. It does not define build identity or grant combat
authorization. Renaming/removing that generated catalog field remains
`CBL-008`/`CBL-024` Phase 7 work.

## 4. Exact frozen gameplay read path

Gameplay subscriptions now include the six owner-filtered frozen tables:

1. `MatchCombatBuild`;
2. `MatchCombatBuildDiscipline`;
3. `MatchDisciplineConfiguration`;
4. `MatchStaffSchoolSelection`;
5. `MatchDisciplineActionBarAssignment`; and
6. `MatchDisciplinePassiveSelection`.

The local/open-world plan no longer subscribes to the legacy character action
bar, discipline selection, ability selection, or discipline weapon projection
as an action-bar truth. The disposable PvP initial plan also does not subscribe
to `PlayerKnownSpell`.

`ActiveActionBarResolver` resolves a selectable action only when an exact
`MatchDisciplineActionBarAssignment` matches:

- the local owner;
- the current `ActiveCombatDiscipline`;
- an ordered discipline in the frozen build; and
- the requested exact action-bar slot.

It then uses `AbilityCatalog` only to map that exact ability ID to its runtime
action and presentation. It has no combat-profile, global replicated bar,
selected-ability, learned-spell, item-spell, or spellbook fallback.

The three discipline switch inputs resolve their exact ordered canonical IDs
from `MatchCombatBuildDiscipline` and use F1/F2/F3. The 27 bar cells retain the
shared three-row keymap; the server-owned 20-total/16-active contract controls
how many may be populated.

## 5. Retired Unity editors

`CharacterActionBarPanel` is now a disabled compatibility component with no
bootstrap, catalog browser, drag/drop writer, or reducer call.
`ActionBarDropApplier` was removed while the generic HUD drag components were
retained.

`DisciplinesScreen` is a disabled shell. Opening it warns, immediately closes,
and returns control to the Hub. It does not load or reconstruct the old model
and cannot call a legacy writer. `DisciplineLoadoutRules` and its tests were
deleted, removing the 25-point client budget, primary/secondary minima, and
session-only stat-allocation code path.

The old `Disciplines.uxml`/`.uss` files and the archived prototype remain as
unreachable provenance for the future UI decision. They are explicitly open
under `CBL-016`, `CBL-017`, and `CBL-025`; Phase 5 does not misclassify their
physical presence as a live parallel writer.

## 6. Legacy-consumer audit

The non-generated runtime audit found no live occurrences of:

- `SaveDisciplineLoadout` or `SaveWeaponLoadout`;
- `CharacterActionBarAssignment` or
  `CharacterDisciplineAbilitySelection` consumption;
- `SpellbookResolver`, `SpellSlotResolver`,
  `ResolveEquippedSpellbookAction`, or `ResolveKnownSpellAction`; or
- owner/profile/global action-bar fallback helpers.

Matches in Phase 5 tests are negative assertions. The one non-generated
`PrimaryDisciplineId` access is the generated Hub weapon-catalog metadata seam
classified above. `PlayerKnownSpell` and `ItemSpell` remain used by collection
and inventory panels and by general local inventory subscriptions only; they
do not enter HUD/input action resolution and do not authorize provisioned PvP
combat.

The ledger remains deliberately open for physical Phase 7 deletion. Phase 5
therefore establishes no live competing Unity combat-build reader or writer,
but it does not claim the entire migration's final no-legacy proof early.

## 7. Verification evidence

Pre-change baselines recorded for this slice:

```text
cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast
PASS — 836 passed, 0 failed

cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast
PASS — 27 passed, 0 failed
```

Final non-Unity gates:

```text
cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast
PASS — 836 passed, 0 failed

cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast
PASS — 27 passed, 0 failed

python3 -m unittest match_provisioner.test_worker \
  ops.test_benchmark_local_match_start
PASS — 28 passed, 0 failed

bash ops/test-setup-local-multiplayer.sh
PASS

ops/dungeon-compile-gate.sh
PASS — Assembly-CSharp, Assembly-CSharp-Editor, and Arena.EditModeTests
compiled with 0 errors

git diff --check
PASS
```

The user explicitly authorized the Unity batch run in the current chat. The
first repository-wide EditMode run executed 642 cases:

```text
604 passed, 38 failed
```

Two failures were new Phase 5 query-count expectations. Replacing three legacy
local query rows with six canonical frozen rows yields 35 general-local queries
and 46 disposable-PvP initial queries. Those expectations were corrected. The
remaining 36 failures are in unchanged animation, VFX, Dungeon Lab, map/scene,
movement, and world-interaction assertions. They are not suppressed or called
passing.

The exact Phase 5-added/modified EditMode filter then passed:

```text
19 passed, 0 failed
Unity test runner exit code: 0
```

This filter includes all eight cases in
`CombatBuildUnityPlumbingTests`, all nine changed UI/input contract cases, and
both changed subscription-plan cases.

The final post-fix repository-wide run recorded:

```text
606 passed, 36 failed, 0 skipped
Phase 5 failures: 0
```

The same 36 unrelated failures remained; the two corrected subscription-plan
cases passed in both the focused and final full runs.

## 8. Live handoff and persistence

The `arena-spell-pipeline` release-safety workflow required the canonical
data-preserving stack:

```text
ops/setup-local-multiplayer.sh setup
PASS — Hub republished with delete-data=never, bindings regenerated, both
artifacts rebuilt, managed provisioner started

ops/setup-local-multiplayer.sh status
PASS — SpacetimeDB, PvP artifact, open-world artifact, provisioner ready

python3 <arena-spell-pipeline>/scripts/hub_loadout_guard.py verify ...
PASS — all 60 pre-existing Hub loadout rows unchanged; 0 new rows

python3 ops/benchmark-local-match-start.py --samples 1
PASS — canonical initial state received; cleanup 1/1 CLEANED

python3 ops/test-combat-build-runtime.py
PASS — Daggers -> Bow -> Staff -> Daggers exact switch sequence; Arcana +
Ruin Staff schools; exact frozen assignments and weapons; passive preserved
across swaps; dormant/unassigned/wrong-bar/wrong-school denials; cleanup
CLEANED
```

The binding regeneration created no additional tracked generated-file diff.

## 9. Exit decision

Phase 5 exit gate: **PASS**.

Phase 6 was not started. The replacement UI and Phase 7 destructive schema and
generated-surface removal require their own explicit approvals.
