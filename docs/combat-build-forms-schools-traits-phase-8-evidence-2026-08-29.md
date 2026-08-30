# Combat Build v2 Phase 8 Evidence

Date: 2026-08-29

Result: **PASS**

Phase 8 removed the legacy combat-build authority, obsolete Staff melee
features, rehearsal-only paths, and stale generated contracts. The canonical
Hub, disposable PvP match, open-world match, Unity client, editor projections,
and local acceptance probes now use only Combat Build v2.

## Boundary

This slice implements Phase 8 of
`docs/combat-build-forms-schools-traits-plan-2026-08-29.md`:

- remove the v1 selected-Discipline/Staff-school-child schema;
- remove v1 mixed action-bar and passive-selection paths;
- remove Staff Techniques while retaining the ordinary Staff autoattack;
- delete adapters, fallbacks, obsolete rehearsals, and stale bindings;
- update the current authoring contract; and
- prove the final schema, runtime authorization, local artifacts, and live
  compositions.

No animation-system redesign was introduced. Existing spell action-to-motion
resolution and equipped-Discipline presentation overrides remain intact.

## Reset and data disposition

SpacetimeDB 2.1 cannot data-preservingly publish removal of the legacy tables.
The owner explicitly approved clearing `arena-hub-local`.

Before the reset, the complete local v2 profile/build state and cutover audit
were saved to the ignored local file
`Library/ArenaLocalMultiplayer/combat-build-v2-phase8-preserved-state.json`:

- SHA-256:
  `563268d7f1a41e1a2a6e7254f4dbce6fca3d50b8f03bbe7b6d3a65d54f5af5f6`;
- 14 Hub profiles;
- 14 armor selections;
- 14 v2 roots;
- 21 selected Specializations;
- 0 dormant Specializations;
- 16 Discipline configurations;
- 38 selected Combat Features; and
- 4 selected Traits.

Those 14 disposable local profiles and builds were intentionally not restored.
Only the durable Phase 7 cutover audit was restored, using a temporary
owner-only reducer that was removed before the canonical final publish. Its
live row remains exact:

- snapshot SHA-256:
  `9c9b6864142859c5b305c96e8270f72341508dc85a9c6cc63bc88a57ceaa3af5`;
- v1 roots/children before cutover: 8/44;
- preserved Hub player/armor rows at cutover: 8/8;
- v2 roots after cutover: 8; and
- original execution timestamp:
  `2026-08-30T02:44:49.370585+00:00`.

The final canonical publish used the data-preserving setup path. The four Hub
profiles/builds present after acceptance are new local provisioner/probe
identities created after the approved reset; none of the 14 saved historical
profiles were restored. Live probe tickets are `CLOSED` and their Hub
snapshots remain only for the normal terminal observation window. Every
disposable match database created by the probes was deleted.

The spell-pipeline skill's legacy loadout guard still queries the removed
`hub_player_loadout` table and therefore cannot inspect this schema. The final
state was instead verified directly through the canonical v2 aggregate and
child tables before and after the data-preserving publish.

## Deletion ledger

### Hub and match authority

- Deleted Hub v1 roots, Discipline children, weapon configurations, Staff
  school selections, mixed action assignments, passive selections, draft DTOs,
  view projection, save reducer, reset reducer, and display catalogs.
- Deleted the imported v1 validator/materializer module
  `server/src/combat_build.rs`.
- Deleted PvP/open-world v1 frozen rows and materialization logic from
  `server/src/match_contract.rs`.
- Deleted the v1 module from `match-server/src/lib.rs` and v1 public row
  declarations from `server/src/lib.rs`.
- Deleted the old `combat_build_contract` block from
  `server/src/progression_catalog.shared.json`. The v2 validator now checks
  exact canonical Discipline and School sets without reading that obsolete
  projection.

### Staff Techniques

Removed `STAFF_STRIKE`, `STAFF_STRIKE_2`, `STAFF_SWEEP`, and `STAFF_THRUST`
from progression gameplay and public ability presentation. They remain only in
the v2 removal ledger and negative tests. `STAFF_STRIKE_2` may retain private
clip/action presentation data but grants no player authorization.

Staff remains a derived Discipline with its authored ordinary autoattack in
`auto_attacks[]`. It has zero selectable or intrinsic Techniques.

### Generated and rehearsal surfaces

- Regenerated canonical Hub, PvP, and open-world/harness C# bindings from the
  final schemas; deleted every generated v1 type/table and paired `.meta`.
- Deleted the Hub v2 rehearsal, match v2 rehearsal, standalone rehearsal
  client, rehearsal Unity bindings/client, Phase 6 rehearsal EditMode test,
  and Phase 2–6 rehearsal runners/build script.
- Deleted dated Phase 0/4/5 generators and the runnable v1 Hub probe.
- Ported `ops/combat_build_probe_support.py` and its tests to v2
  Specialization/Technique/Spell/Perk materialization.
- Replaced removed Staff Technique fixtures in LOS/lag/rewind probes with the
  retained Paladin Sacred Thrust Technique.
- Updated spell-authoring editor readers to consume the v2 Specialization
  catalog rather than the deleted progression v1 build block.

Historical dated evidence and ledgers remain documentation, not executable or
runtime authority.

## Ownership audits

Negative source searches found no retained v1 tables, DTOs, reducers,
materializers, generated bindings, or progression `combat_build_contract` in
the Hub, match, server, Unity, or ops runtime surfaces. The only generic
`CombatBuild` property hits are typed `CombatBuildV2DraftModel` client access.

Removed Staff IDs appear only in:

- the reviewed v2 removal ledger;
- validator deletion-ledger assertions;
- live negative composition assertions; and
- historical dated documentation.

Generated Hub, PvP, and open-world binding directories contain no orphan
`.meta` files and no generated `.cs` files without a paired `.meta`.

The final compact catalog contains 18 Specializations, 80 Techniques, 104
Spells, 24 Perks, one Trait (`MASTERY`), and four removed Staff ability ledger
entries. It enforces three Specialization slots, global feature capacity 18,
and Trait capacity 3.

## Verification

- `python3 ops/generate-combat-build-v2-catalog.py --check`: PASS.
- `python3 -m py_compile ops/*.py`: PASS.
- `python3 -m unittest discover -s ops -p 'test_*.py'`: PASS, 9 tests.
- provisioner/provenance/benchmark unit suites: PASS, 35 tests.
- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`:
  PASS, 785 tests.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 20 tests.
- projectile-harness Combat Build v2 tests: PASS, 6 tests.
- PvP Combat Build v2 tests: PASS, 6 tests.
- frozen PvP snapshot contract test: PASS, 1 test.
- `ops/dungeon-compile-gate.sh`: PASS; `Assembly-CSharp`,
  `Assembly-CSharp-Editor`, and `Arena.EditModeTests` each compile with zero
  errors using refreshed temporary file lists.
- canonical generated-binding `.meta` synchronization: PASS.
- targeted `rustfmt --check` for `server/src/combat_build_v2.rs`: PASS. The
  whole-manifest check also reports pre-existing formatting drift in
  `server/src/combat/projectiles.rs` and `server/src/spells/catalog.rs`; those
  unrelated files were not changed.
- JSON parsing and `git diff --check`: PASS.
- Unity batch mode: not used.

A direct invocation of the complete `match-server` unit suite is not a valid
standalone gate in the current test harness: imported shared-server tests use
`CARGO_MANIFEST_DIR`, resolve fixtures under `match-server/src`, and produce 13
file-not-found/catalog-fixture failures. The canonical full server suite and
the focused PvP v2/frozen-contract suites above pass. This pre-existing harness
cwd assumption is unrelated to Combat Build v2 runtime or schema behavior.

## Live proof

`ops/test-combat-build-v2-compositions.py` passed against the final local
stack:

- three Schools (`RUIN`, `ARCANA`, `BLIGHT`) derived only `STAFF`, materialized
  18 Spells, zero Techniques, and active Mastery;
- three Dagger Forms (`BLADEDANCER`, `EXECUTIONER`, `SHADOW`) derived one
  `DAGGERS` Discipline, one merged weapon configuration/bar, two Techniques,
  one Spell, and active Mastery;
- a nineteenth feature was rejected at the global capacity; and
- changing the Hub build after handoff did not mutate the frozen match build.

`ops/test-combat-build-runtime.py` passed:

- a School Spell cast while Daggers were equipped;
- a Form Spell cast while Staff was equipped;
- a Dagger Technique was rejected while Staff was equipped;
- an unselected feature was rejected;
- moving during a cast interrupted it;
- the Technique/Spell/Perk projections stayed stable through
  Daggers -> Staff -> Daggers;
- Mastery remained inactive for the two-parent-Discipline build; and
- zero Staff Techniques materialized.

The one-sample anonymous benchmark proved an exact schema-v2 Hub-to-match
handoff and disposable cleanup:

- request -> ready: 1080.108 ms;
- request -> initial state: 1290.815 ms;
- ready -> match transport: 38.944 ms; and
- ready -> initial state: 210.706 ms.

## Final artifacts

`ops/setup-local-multiplayer.sh status` reports SpacetimeDB, PvP artifact,
open-world artifact, and the managed provisioner ready.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| optimized PvP match WASM | 3,408,123 | `25b1adb4c9dd2aad7b648636c505c28d908f1177ca9ce35f574ab153f00b2712` |
| PvP provenance manifest | — | `b0e2aeadad23d83f52fba5814166e17b7d07d3c90e008867a0aaa9a9c7701348` |
| optimized open-world WASM | 126,009,905 | `7d122b874ff04de02837aa778f75870fcbb39c02077f1e86fcfdfd365dd2eb13` |
| open-world provenance manifest | — | `715b2a3dff6ee7580eae893f0f4ef5c7d93dfb31bf8e077dbff1dbe12bb53710` |

The provenance verifier passed for all 108 PvP source inputs and all 109
open-world source inputs. The optimized PvP artifact remains below its
3,500,000-byte guard.

## Exit gate

PASS. Combat Build v2 is the only retained build authority. There is no v1
schema, fallback writer, Staff Technique authorization path, or stale generated
contract. Static ownership, canonical validation, normal C# compilation,
data-preserving final publication, live compositions, runtime authorization,
snapshot isolation, artifact provenance, and cleanup all pass.
