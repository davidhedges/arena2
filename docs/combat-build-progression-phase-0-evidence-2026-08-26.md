# Combat-build progression cutover — Phase 0 evidence

Date: 2026-08-26

Historical checkpoint: this document records the repository at the Phase 0
exit gate. Phase 1 was subsequently approved and completed; its current
evidence is in
`docs/combat-build-progression-phase-1-evidence-2026-08-26.md`.

Status: **COMPLETE — inventory and target fixtures are complete, the local-data
strategy is approved, the nine pre-existing server failures are repaired with
all 824 tests passing, and the freshly authorized Unity verification run passes
all 87 EditMode tests. No combat-build schema or runtime cutover implementation
has started.**

## 1. Approved boundary

This Phase 0 slice is limited to:

- recording the unchanged checkpoint and baseline results;
- protecting and classifying local Hub loadout data;
- expanding the plan's deletion ledger into a machine-readable consumer
  inventory; and
- freezing target valid/invalid build fixtures before schema work.

It does not change the catalog, Hub schema, match schema, runtime behavior,
generated schema contract, or UI. It does not reset or migrate any database.

The documentation plan and reconciliation were committed before Phase 0 work:

```text
561877f673f2150f7cbedc082d3e75eb27830e95
docs: plan combat build progression cutover
```

`git status --short` was empty at that checkpoint and remained empty after the
canonical local stack refreshed generated bindings. The files added by this
Phase 0 slice are listed in §5.

## 2. Local stack and protected data

The first sandboxed stack probe could not see or create SpacetimeDB's macOS
user-data state. The canonical data-preserving command was rerun outside the
sandbox; no lower-level startup sequence or reset command was used.

Commands:

```sh
ops/setup-local-multiplayer.sh setup
ops/setup-local-multiplayer.sh status
python3 /Users/davidhedges/.codex/skills/arena-spell-pipeline/scripts/hub_loadout_guard.py \
  snapshot --repo . \
  --file Library/ArenaLocalMultiplayer/spell-release-loadouts.before.json
```

Observed state:

- SpacetimeDB ready locally;
- cached match artifact ready;
- cached open-world artifact ready;
- managed provisioner running;
- persistent Hub publish was data-preserving;
- 33 persistent Hub loadout rows captured, with 12 fields per row; and
- the ignored guard artifact has SHA-256
  `22220f67594f6e306f619278553513ddd8bc900807553517579c9b31cd10415a`.

The snapshot contains local player identities and remains under ignored
`Library/`; this evidence records only its count and hash. It must remain
available until the owner chooses and completes the cutover strategy.

Tool versions used for the baseline:

```text
cargo 1.94.0 (85eff7c80 2026-01-15)
.NET SDK 10.0.201
Python 3.11.3
SpacetimeDB CLI/lib 2.1.0
```

## 3. Unchanged baseline results

### Server

Command:

```sh
cd server
cargo test
```

Result: **FAILED — 815 passed, 9 failed, 0 ignored, 824 total.**

Failing tests:

1. `contract_version::tests::shared_files_list_is_complete`
2. `inventory::tests::data_preserving_republish_workflows_publish_item_catalogs`
3. `melee::tests::combo_successor_strikes_reachable_from_melee_roots_have_gameplay_rows`
4. `melee::tests::data_preserving_republish_workflows_publish_melee_definitions`
5. `melee::tests::greatsword_whirlwind_authored_strike_maps_to_finisher_slot`
6. `melee::tests::melee_manifest_authored_ids_match_animation_set_assets`
7. `progression::tests::animation_sets_use_semantic_spell_motion_bindings_without_legacy_spell_rows`
8. `progression::tests::spell_cast_animation_map_matches_requested_semantic_classifications`
9. `world_collision::tests::random_dungeon_uses_baked_lower_floors_without_a_flat_ground_plane`

These failures occurred before any Phase 0 file was added. They cover shared
file hashes, data-preserving republish scripts, melee/animation authoring, and
one dungeon collision assertion; none is a combat-build fixture or inventory
failure. They are nevertheless unresolved baseline failures under the literal
Phase 0 exit gate and are not silently waived.

### Persistent Hub

Command:

```sh
cd hub-server
cargo test
```

Result: **PASS — 18 passed, 0 failed.**

The passing tests explicitly demonstrate current legacy behavior, including
school-slot reconciliation, undifferentiated active/passive selection, a
primary-discipline weapon rule, and the primary/secondary default. They are
inventory evidence, not proof of the target contract.

### Provisioner and benchmark helpers

Commands:

```sh
python3 -m unittest match_provisioner/test_worker.py
python3 ops/test_benchmark_local_match_start.py
```

Results:

- provisioner: **PASS — 22 passed, 0 failed**;
- benchmark helpers: **PASS — 3 passed, 0 failed**.

Both suites currently assert the legacy positional snapshot and are therefore
open ledger consumers, not target-contract coverage.

### Unity client

Command:

```sh
ops/dungeon-compile-gate.sh
```

Result: **PASS — `Assembly-CSharp`, `Assembly-CSharp-Editor`, and
`Arena.EditModeTests` compiled with zero errors.**

The Unity test runner was not executed. Repository policy prohibits Unity
batch mode without explicit current-chat authorization, and no non-batch
automated Unity runner was available in this session. This is recorded as an
unexecuted Phase 0 result, not represented as a test pass.

### Approved baseline-repair follow-up

The owner approved an isolated repair of the nine recorded server failures and
one specific Unity batch-mode EditMode run. The repair did not begin Phase 1 or
change the combat-build schema. It:

- taught the shared-file hash test about the intentionally external
  Unity-owned weapon-appearance catalog;
- accepted the explicit `-s` server selector in the data-preserving republish
  scripts;
- removed two dagger manifest strikes already removed from their progression
  and Unity authoring sources;
- added the hidden Staff Strike II combo-followup gameplay row required by the
  Staff animation set's reachable authored combo;
- refreshed Whirlwind and Random Dungeon assertions against their current
  authored/exported data; and
- replaced adjacency-sensitive spell-map string checks with entry parsing,
  preserving the current semantic and shared-recipe classifications.

Server verification:

```sh
cd server
cargo test
```

Result: **PASS — 824 passed, 0 failed.** Each of the nine formerly failing
tests also passed individually before the full suite.

Additional verification:

```sh
cd hub-server && cargo test
python3 -m unittest match_provisioner/test_worker.py
python3 ops/test_benchmark_local_match_start.py
bash ops/test-setup-local-multiplayer.sh
dotnet build Assembly-CSharp.csproj --nologo
dotnet build Arena.EditModeTests.csproj --nologo
```

Results:

- Hub: **PASS — 18 passed, 0 failed**;
- provisioner: **PASS — 22 passed, 0 failed**;
- benchmark helpers: **PASS — 3 passed, 0 failed**;
- canonical setup-script tests: **PASS**;
- runtime C# assembly: **PASS — 0 errors**; and
- EditMode test assembly: **PASS — 0 errors**.

`cargo fmt --all -- --check` remains red because of pre-existing rustfmt drift
in unrelated portions of the server source, including files not changed by
this repair. The changed Rust hunks match rustfmt's requested form, and
`git diff --check` passes. The unrelated formatter drift was not expanded into
this isolated slice.

Authorized Unity command:

```sh
/Applications/Unity/Hub/Editor/6000.4.0f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit \
  -projectPath /Users/davidhedges/Projects/arena2 \
  -executeMethod Arena.Editor.BuildBlockingEditModeTestGate.ValidateBuildBlockingEditModeTestsBatch
```

Result: **FAILED — 69 passed, 18 failed, 87 total.** Ten
`PredictedMeleeContactCueTests` calls omitted the newer optional
`isAutoAttack` reflection argument; eight `RemotePresentationBufferTests`
calls omitted the newer `consumedCommand` and `bufferedCommands` constructor
arguments. Reflection does not apply C# optional defaults. The test adapters
now pass the runtime defaults explicitly, and both affected assemblies compile
with zero errors.

Freshly authorized verification result: **PASS — 87 passed, 0 failed, 87
total.** Unity logged the build-blocking gate pass and exited batch mode
successfully.

Canonical post-repair local verification:

```sh
ops/setup-local-multiplayer.sh setup
ops/setup-local-multiplayer.sh status
python3 /Users/davidhedges/.codex/skills/arena-spell-pipeline/scripts/hub_loadout_guard.py \
  verify --repo . \
  --file Library/ArenaLocalMultiplayer/spell-release-loadouts.before.json
python3 ops/benchmark-local-match-start.py --samples 1
```

Results:

- data-preserving Hub publish completed;
- optimized disposable-match WASM passed its size guard at
  `3,340,642 / 3,500,000` bytes;
- SpacetimeDB, both cached artifacts, and the managed provisioner were ready;
- all 33 protected pre-existing Hub loadout rows were unchanged;
- the one match sample reached authenticated initial state in `1,724.895 ms`
  from request, including `208.358 ms` from ready to initial state; and
- cleanup completed for the sampled disposable database (`1 / 1`).

## 4. Machine-readable consumer inventory

Artifact:

```text
docs/combat-build-legacy-consumer-inventory-2026-08-26.json
```

Inventory totals:

- 27 consumer groups;
- 25 open production/current-source groups;
- 1 archived documentation group; and
- 1 not-present guard for temporary adapters/dual writers.

Each group records concrete symbols, known paths, required final state, target
phase, current status, and repeatable audit patterns. The trace covers:

- catalog identity and school-as-discipline branches;
- primary/secondary rules, storage, defaults, and DTO fields;
- ambiguous ability selection;
- Hub persistence and weapon saving;
- frozen snapshots, provisioner arguments, and bootstrap reducers;
- match-side save/default paths;
- public combat-profile authority and action-bar replication;
- profile/discipline-only passive gates;
- learned/spellbook alternate authorization;
- generated bindings;
- Hub, Disciplines, Equipment, action-bar, and summary consumers;
- operational benchmark assertions; and
- archived/conflicting documentation plus temporary-adapter guards.

Verification:

```sh
jq empty docs/combat-build-legacy-consumer-inventory-2026-08-26.json
jq -e '
  (.entries | map(.id) | length) ==
    (.entries | map(.id) | unique | length)
  and
  ([.entries[] |
    select((.symbols | length) == 0 or
      ((.locations | length) == 0 and .status != "not_present"))] |
    length == 0)
' docs/combat-build-legacy-consumer-inventory-2026-08-26.json
```

Result: **PASS.**

## 5. Target contract fixtures

Artifacts:

```text
docs/fixtures/combat-build-contract-v1/README.md
docs/fixtures/combat-build-contract-v1/cases.json
```

Fixture totals:

- 29 unique cases;
- 6 valid cases;
- 23 invalid cases; and
- 19 target stable error codes.

The cases include the required `Daggers + Bow + Staff` example with one Staff
bar drawing from `RUIN + ARCANA`, `16 active + 4 passive`, `15 active + 5
passive`, the independent 17-active failure, the 21-combined failure,
active-only/passive-only discipline minima, explicit/fallback starting
discipline, dormant exclusion, Staff school bounds, damage-type rejection,
ability kind/ownership/duplicate/slot failures, weapon mismatch, and unknown
references.

Phase 0 does not add a standalone semantic validator. Doing so would create a
second rules authority. Phase 1's one production pure validator must execute
all cases, and later Hub/snapshot/match boundaries must reuse them.

Verification:

```sh
jq empty docs/fixtures/combat-build-contract-v1/cases.json

jq -e '
  (.cases | map(.id) | length) ==
    (.cases | map(.id) | unique | length)
  and
  ([.cases[] |
    select(.expected.valid == false and
      (.expected.error_code | type) != "string")] |
    length == 0)
' docs/fixtures/combat-build-contract-v1/cases.json

jq -e '
  [.cases[] |
    . as $case |
    ([.build.selected_disciplines[]?.combat_discipline_id]) as $selected |
    ([.build.discipline_configurations[]? |
      select(.combat_discipline_id as $id | $selected | index($id)) |
      .active_assignments[]?] | length) as $active |
    ([.build.discipline_configurations[]? |
      select(.combat_discipline_id as $id | $selected | index($id)) |
      .passive_ability_ids[]?] | length) as $passive |
    select(
      (.expected.active_count? != null and
        .expected.active_count != $active) or
      (.expected.passive_count? != null and
        .expected.passive_count != $passive) or
      (.expected.combined_count? != null and
        .expected.combined_count != ($active + $passive))) |
    {id, declared: .expected,
      calculated: {
        active_count: $active,
        passive_count: $passive,
        combined_count: ($active + $passive)
      }}
  ] | length == 0
' docs/fixtures/combat-build-contract-v1/cases.json

jq -n -e \
  --slurpfile fixtures docs/fixtures/combat-build-contract-v1/cases.json \
  --slurpfile catalog server/src/progression_catalog.shared.json '
  ($catalog[0].abilities | map(.ability_id)) as $known |
  ([$fixtures[0].cases[].build.discipline_configurations[]? |
    (.active_assignments[]?.ability_id),
    (.passive_ability_ids[]?)] | unique) as $used |
  (($used - $known) == ["NOT_A_REAL_ABILITY"])
'

jq -n -e \
  --slurpfile fixtures docs/fixtures/combat-build-contract-v1/cases.json \
  --slurpfile catalog server/src/progression_catalog.shared.json '
  ($catalog[0].slots | map(.slot_id)) as $known |
  ([$fixtures[0].cases[].build.discipline_configurations[]?
    .active_assignments[]?.action_slot] | unique) as $used |
  (($used - $known) == ["slot_99_99"])
'
```

Result: **PASS.** JSON syntax, unique IDs, invalid-case error codes, and all
declared selected-discipline counts are internally consistent. Every referenced
ability and action slot exists in the current authored catalog except the two
deliberately invalid sentinel references. Catalog-backed target ownership,
school metadata, and semantic execution remain Phase 1 exit requirements.

## 6. Cutover choice and exit-gate status

The protected snapshot proves the local Hub is not empty: it contains 33
legacy loadout rows. The plan's recommended strategy remains:

1. keep the protected export;
2. build and test a temporary offline converter against the production Phase 1
   validator;
3. review its deterministic report;
4. explicitly approve one local-only schema reset;
5. import converted builds; and
6. delete the converter after verified use.

The alternative is a separately designed additive, data-preserving migration.
No implicit reset, runtime conversion reducer, compatibility writer, or dual
write is allowed.

Owner ruling on 2026-08-26: **approved the recommended protected-export,
offline-converter, explicit-local-reset, and verified-import strategy.** This
approves the strategy only. The destructive reset itself still requires a
separate explicit approval after the converter report is reviewed.

The owner also authorized a separate isolated repair slice for the nine server
baseline failures and two specific Unity batch-mode EditMode test runs. The
server repair and every non-Unity post-repair gate pass. The first Unity run
exposed 18 stale test adapters; after their repair, the freshly authorized
verification run passed all 87 tests. Phase 0's literal exit gate is satisfied.

No Phase 1 catalog/schema work is authorized or started by this evidence log.
