# Combat Authoring Contract

Status: **Current for Combat Build v2; timing and authority reviewed 2026-09-06.**

This is the entry point for adding or reviewing player combat actions. The
canonical build hierarchy is:

```text
Combat Discipline
  -> Specialization (weapon Form or Spellcasting School)
    -> Combat Feature (Technique, Spell, or Perk)

Character
  -> Trait (independent of every Discipline and Specialization)
```

Prefer Unity editor authoring tools when they exist. Use
`docs/ability-implementation-prompt-template-2026-04-22.md` only as the manual
fallback. Animation-layer ownership remains in
`docs/combat-animation-authoring-contract.md`.

## Hit Validation Timing

These are the current contracts for action input, authoritative simulation,
and remote presentation. The July audits describe the migration and accepted
tradeoffs; their pre-migration defect descriptions are not the current behavior.

| Path | Input and prediction | Server authority | Remote presentation and permitted disagreement |
| --- | --- | --- | --- |
| Melee | Action-bar dispatch runs advisory range/facing/LOS checks against the local pose and rendered target, sends a prediction token and target-view timestamp, and predicts costs and animation. | `melee.rs` uses the current authoritative attacker pose, bounds target rewind at press, then schedules the authored impact. Impact rechecks target position with the frozen rewind delay and uses current life, world, relation, LOS policy, and defenses. | Cast/contact facts drive remote animation and cues. A locally valid turn-and-strike can be rejected before the movement turn reaches authority. Moving out during windup can cause a silent whiff. Neither acceptance nor a predicted contact cue promises damage. |
| Fireball | `SpellInputHandler` predicts instant release and a projectile body, carrying a prediction token and movement input tick with the cast pose. | Bounded action-snapshot validation can use the predicted launch pose; an excessive discrepancy falls back to authority. Press gates may rewind the target. Projectile movement, swept collision, and defense resolution use current simulation state. | `CombatVFXDispatcher` correlates the accepted action to its exact prediction token, adopts the body, and follows projectile presentation events. Homing and visual smoothing can differ briefly from the server trajectory; damage comes from authoritative impact resolution. |
| Movement | `LocalMovementPredictionDriver` authors fixed 33 ms commands ahead of authority and predicts immediately. | The server consumes queued commands in tick order; a missing command retains movement axes/yaw without repeating jump. Acknowledgements include whether a real command was consumed. | The local predictor rebuilds from authority and replays pending inputs. Remote actors use delayed interpolation and bounded extrapolation. A rendered target is deliberately behind the current server pose. |
| Match handoff | The Hub creates a ticket and freezes the selected build; the assignment must identify a supported map, same credential cluster, and valid database. | Hub ticket expiry and match admission use server time. The reserved human's connection starts countdown. Initial subscription and map-contract validation gate match readiness; scoped world rows and scene loading follow. | Readiness means accepted initial state, not a fully rendered scene. Loading can consume countdown. Later Hub edits do not alter a match's frozen build. |

Gameplay cooldown prediction, gates, and HUD countdowns use `ArenaServerClock`;
prediction rollback records the converted timestamp. Hub deadlines belong to
the Hub/match clock domain, not the PC's wall clock or a previous gameplay
connection's estimate. Client handoff waits use monotonic elapsed-time limits;
the server rejects expired admission. Subscription callbacks from a replaced
connection must never advance or clear the current scope.

Exact token/action identity owns reconciliation. Two casts of the same spell
are distinct even when their prediction windows overlap. A transaction's
cached acceptance row can establish identity before its row callback runs;
spell name alone cannot. Predicted melee contact cues are cosmetic and may be
false positives; HP, damage numbers, and block/parry results remain authoritative.

The intentional tradeoffs are documented in
[the lag-compensation design](lag-compensation-design-2026-07-04.md),
[the sweep/projectile decision](sweep-projectile-rewind-design-2026-07-05.md),
[the feel audit](multiplayer-feel-audit-2026-07-02.md), and
[the match-start plan](match-start-latency-optimization-plan-2026-08-11.md).
Change them only with an explicit gameplay decision supported by measurements.

### Maintenance evidence — 2026-09-06

The cooldown clock conversion was already implemented in `cd83bcc2`. The
separate Hub assignment preflight still used PC UTC: an unchanged assignment
with 30 server seconds remaining passed at zero skew and failed at +120 s.
Client waits now use elapsed time; server admission still enforces expiry.

Managed regressions reproduced three same-spell VFX miscorrelations (unknown,
already-consumed, and expired action identity) and reused subscription
generations for all three delayed scope callbacks. Exact cached acceptance
now resolves VFX without depending on callback order, and scope generations
remain distinct across reconnects. `NetcodeBoundaryTests` covers these cases,
rejected acceptance, server clock offsets, deadline validity, and the cluster
guard. Its 16 cases and the 25 existing clock/rollback cases pass (41 total)
through a standalone .NET NUnit runner; runtime,
Editor, and EditMode test assemblies compile without launching Unity.
The baseline server/Hub suites passed 824/25 tests; the disposable-match
admission and frozen-build suites passed 9/11 tests.

Measurements preserve the distinction between current protocol probes and
saved gameplay captures:

| Measurement | Evidence | Limit |
| --- | --- | --- |
| Turn then melee | The saved Editor session beginning `2026-09-06T04:32:56Z` contains four accepted server melee requests, zero server rejections, two local range rejections, and three local GCD rejections. | No controlled turn sequence is identified; this does **not** establish a turn-related rejection rate. |
| Predicted contact cues | CSV session `2026-09-06T04:51:17Z`: three cues fired, three matched, zero recorded false positives. | Small sample; not evidence that false positives cannot occur. |
| Movement correction | Same CSV session: 588 acknowledgement ticks, five fallback acknowledgements (0.85%), zero snaps/resyncs or absorbed correction distance; three jumps predicted and confirmed. | Nineteen sampled seconds, with zero reported reconciliation error. No shaped-latency retuning conclusion. |
| Loading during countdown | Saved startup trace: transport connected at 1567.0 ms, scene requested at 2850.3 ms, scene loaded at 6866.4 ms. Connection-to-load is 5299.4 ms against a three-second server countdown. | One Editor capture; scene-loaded is not a measurement of the first controllable/rendered frame. A loaded handshake would change the accepted readiness rule. |
| Fresh local match startup | Three serial `benchmark-local-match-start.py --samples 3` samples reached initial state in 1046.3, 887.9, and 902.3 ms (median 902.3 ms). | Protocol-only; does not execute the changed Unity code or load a scene. |

The serial benchmark exposed a separate cleanup fault: replacing the dedicated
probe identity's ticket removed the prior frozen Hub snapshot, causing two
allocations to enter `ORPHANED` with `frozen Hub combat-build snapshot is missing`.
The benchmark therefore failed its automatic-cleanup gate despite completing
all three timing samples. Both exact probe allocations were checked against
their database identities, bootstrap match IDs, and provisioner ownership,
then removed through the existing guarded deletion method. All three ledger
entries reached `CLEANED`; all 69 existing Hub profiles and their durable v2
children, audit, and catalogs were verified unchanged. The cleanup fault is
recorded here, not changed by this client maintenance work.

No new Unity play session was run for this maintenance. A controlled
turn-and-strike capture and visual verification of repeated Fireballs remain
unverified; the available measurements do not justify changing melee geometry,
cosmetic contact prediction, movement lead, or countdown/readiness behavior.

## Source ownership

### Gameplay and presentation

`server/src/progression_catalog.shared.json` owns stable ability identity,
actor scope, resource cost, gameplay behavior, combat tuning, action
presentation, VFX cues, and intrinsic autoattacks. It does not own durable
player choices or bar placement.

For upfront resource costs, `server/src/ability_cost.rs` owns the resolution
policy shared by runtime spell definitions and Hub feature metadata. Spell
executors prefer a positive `gameplay.resource_cost`, then fall back to the
top-level `resource_cost`; other executors use the top-level cost. This follows
the gameplay executor even when the feature is classified as a Technique.
Channel and Emanation per-second costs remain delivery-specific and are not
projected as Hub upfront costs. Runtime spending and modifiers are separate.

`Assets/Arena/Resources/CombatAnimationSets/*.asset` owns Unity combat
presentation, authored melee strike IDs, hit windows, recovery, combos,
phased clips, cast-motion bindings, and weapon presentation.
`server/src/melee_manifest.shared.json` is exported from those animation sets;
do not hand-edit it to repair identity drift.

### VFX cue ownership

`combat_vfx_cues` in progression is the runtime cue authority. Each row now
explicitly declares its authoring source with `authoring_mode`:

| Mode | Owner and rule |
| --- | --- |
| `GENERATED` | Gameplay/animation facts plus school palettes and per-spell slot looks own the generated fields. The catalog row is checked materialized output. Every global row must reproduce across GLOBAL and every equipped animation profile. |
| `MANUAL` | The catalog owns the effective fields directly. `authoring_reason` explains the exception. Generation comparisons are informational and cannot overwrite it. |
| `LEGACY` | A non-ABILITY compatibility cue remains authored in the catalog, with an explicit reason. It is excluded from generator slot matching and preserved by writes. |

The current 179 cues comprise **50 generated, 128 manual, and one legacy** row.
The 166 explicit `slot` keys identify rows; they do not confer ownership.
Generated rows require an explicit valid slot, unambiguous ABILITY ownership,
and a resolvable runtime VFX template. Extra runtime conditions outside the
generator's field contract require manual ownership.

`SpellVfxGenerator` derives triggers, roles, attachment, and lifetime from
gameplay and animation facts. Rust `vfx_generation.rs` validates runtime field
relationships; it does not generate a competing catalog. `SchoolVfxSet` owns
school-default slot looks, and `SpellVfxOverrideCatalog` owns explicit per-ability
look exceptions. VFX palette school remains separate from a selected build School.

Cast hand is inferred from resolved animation origin, mirroring, then gesture
or clip inference, retaining the existing left-hand default when presentation
is missing. The editor's selected profile affects preview only. A candidate that
varies by equipment cannot overwrite a global generated row; affected existing
cues retain their authored anchors under manual ownership.

`CombatVFXRegistry` owns live prefab bindings and scales. Palette/override assets
no longer serialize those fields: their inspector displays resolved values
read-only. Existing scripted templates retain their own implementation and are
shown as scripted effects rather than missing prefab bindings.

The editor's regeneration button targets declared GENERATED rows only. The file
writer reloads inputs, validates every animation context, checks the requested
rows against fresh generation, and rejects stale catalog previews. Manual and
legacy rows, ordering, and unrelated fields are preserved. This check is not a
general file locking mechanism. New generated-only slots remain proposals:
stage explicit ownership in the catalog before materializing them. Manual spell
drafts include ownership and reason fields.

`authoring_mode` and `authoring_reason` are editor metadata. The existing
`server/build.rs` compiled progression export strips exactly these two cue
fields, preserving every other parsed value and array order. Source hashes still
cover the complete authored JSON. No SpacetimeDB table or generated C# wire field
is added for ownership metadata.

CharacterFx variants share normalized slot identity on generated and explicit
keys: `Body Rings` becomes `character_fx/body_rings`. Multiple entries require
distinct variant IDs.

The remaining SPELL-owned Ice Spikes cue is required compatibility data. Its
ABILITY counterpart suppresses it when ability identity is present; an
empty-ability fact still needs the fallback. Keep it until all relevant event
and prediction paths are proven to carry the ability ID.

### Build taxonomy

`server/src/combat_build_v2_catalog.shared.json` is the compact runtime build
catalog. It contains:

- Forms and Schools with one parent Discipline each;
- exact Technique, Spell, and Perk ownership;
- the Trait catalog;
- intrinsic and removed-player-ability ledgers;
- the default legal build; and
- capacities and direct action input IDs.

The file is generated by `ops/generate-combat-build-v2-catalog.py` from the
reviewed classification ledger in
`docs/combat-build-v2-phase-0-contract-2026-08-29.json` plus the progression
catalog. Change the reviewed ledger and regenerate; do not patch the compact
catalog by hand.

The ledger's `feature_classification` array is maintained with the implemented
roster. Its order within each Specialization and feature kind determines the
generated ability-array order; progression `sort_order` remains separate
metadata. The generator requires every selectable player ability to be
classified exactly once and rejects missing, duplicate, or non-player entries.
The ledger retains its historical Phase 0 filename and fixture inventory;
updating current classification must preserve those fixtures and existing
saved-build compatibility. Earlier cutover annotations are historical context,
not an instruction to reset current builds.

For every selectable player ability, progression's `combat_discipline_id` is
a checked copy of its v2 Specialization's parent. Both the generator and the
shared Rust validator reject disagreement. Spell school describes the spell's
magic and remains independent of that parent: a Dagger Form may own a
Mortality spell. NPC and intrinsic rows are outside this selectable-feature
parity rule.

The pure validator and materializer live in
`server/src/combat_build_v2.rs`. Hub saves, Hub-to-match snapshots, PvP, and
open-world matches all use that contract. There is no v1 build adapter or
alternate match-side loadout writer.

### Equipment catalogs

`server/src/armor_catalog.rs` owns armor set IDs, names, pieces, tiers, and
derived base stats. Gameplay inventory and the Hub project the same resolved
catalog. Do not maintain a second Hub roster or armor stat formula.

`Assets/Arena/Resources/SharedData/weapon_appearance_catalog.shared.json` is
the single authored weapon catalog, included directly by Rust. Its shared
Rust read schema is `server/src/weapon_catalog.rs`; inventory, Hub projection,
and v2 build validation use that schema. Unity-only prefab and placement
fields remain in the JSON. Consumer-specific legality checks remain at their
existing boundaries; the read schema is not another writer.

### Shared files and generated bindings

Bundled map/world JSON under `Assets/Arena/Resources/SharedData` is a derived
copy of the corresponding `server/src/map_data` or `server/src/world_data`
export. `Maps/` contract keys use the `map_data/` prefix; `Worlds/` and
`WorldInteractions/` use `world_data/`. The heightfield root mirror follows
the same byte comparison. Weapon appearance is the single-copy exception
above. Runtime-only catalogs need no Unity JSON mirror.

`ops/verify-spacetimedb-contracts.py --offline` rejects divergent mirrors,
missing sources or bundles, and ambiguous contract keys. It ignores carriage
returns, matching Rust and Unity hashing. Without `--offline`, it also
compares bundled hashes against a live module's `contract_version` table.

Rust SpacetimeDB table/reducer definitions own the wire schema. Generated C#
bindings are derived output, regenerated through the publication scripts
below. A generated row type is not a place to add independent gameplay rules.

### NPC authoring destinations

The NPC catalog is `Assets/Arena/Content/NPC/NpcVisualCatalog.asset`.
Profiles live at
`Assets/Arena/Resources/NpcVisualProfiles/<VISUAL_ID>.asset`.
`ops/npc_profile_paths.rb` resolves those paths for the family generator and
preserves existing `.meta` contents and GUIDs. Check existing references
without writing assets with:

```bash
ruby ops/generate-npc-family-profiles.rb --check-paths
```

Remaining animation/VFX ownership conflicts and proposed migrations are
recorded in [the cleanup handoff](source-of-truth-cleanup-2026-09-05.md).
In the normal Unity Editor, `Arena/Animation/Verify Combat Authoring and Export
Inventory` runs the targeted authoring regressions and writes a read-only
melee/VFX comparison under `Logs/CombatAuthoringVerification`. It does not
regenerate authored content or replace visual verification.

The verification also requires selectable melee timing to match the server
manifest. It derives Technique membership and weapon ownership from the v2
Specialization catalog, then reads each ability's explicit `action_id` and
gameplay executor from progression. It does not infer ownership from an ID
prefix or treat every Technique as melee. It checks all hit windows, recovery,
startup trim, combo windows, and phased gap-close durations. Missing authored or
committed actions fail the check. Eventless attacks use their existing exported
fallback; unselected legacy rows remain visible in the broader inventory.
This check covers direct selectable melee roots, not separately synthesized
autoattack aliases or a complete traversal of combo successors.

## Canonical concepts

The five runtime Disciplines are:

- `DAGGERS`
- `TWO_HANDED_SWORD`
- `SWORD_AND_SHIELD`
- `ARCHER_BOW`
- `STAFF`

A build selects one to three Specializations. A non-Staff Specialization is a
weapon Form. A Staff Specialization is one of the six Schools: `BLIGHT`,
`MORTALITY`, `RUIN`, `DIVINITY`, `ARCANA`, or `PRIMAL`. Each Form or School
uses one top-level slot. Multiple Forms may share one weapon Discipline, and
multiple Schools all share Staff.

Combat Features have three player-facing kinds:

- `TECHNIQUE`: active, owned by a non-Staff Form, and authorized only while
  that Form's parent weapon Discipline is equipped;
- `SPELL`: active, owned by a Form or School, and authorized while any selected
  Discipline is equipped; and
- `PERK`: passive, owned by a Form or School, and active whenever that source
  Specialization is selected, independent of the equipped weapon.

Traits are character-wide passives. They have no Discipline, Form, School, or
weapon prerequisite. `MASTERY` is the initial Trait and grants its authored
outgoing-damage bonus only when the selected build derives exactly one distinct
parent Discipline.

## Build invariants

- Every selected Form or School contributes at least one selected Combat
  Feature. Empty Specializations and empty builds are invalid.
- Selected Techniques, Spells, and Perks share one global capacity of 18.
  There is no per-Specialization, Technique-bar, Spell-bar, or active-feature
  cap inside that total.
- Traits use a separate capacity of 3.
- The same Specialization cannot occupy two slots.
- Repeated parent Disciplines are legal. They derive one weapon configuration,
  one switch target, and one merged Technique bar for that parent.
- Removed Specializations may retain dormant selections/configuration for the
  editor, but dormant rows do not authorize gameplay and do not conflict with
  active selections.
- Staff owns no selectable or intrinsic Technique. Its ordinary intrinsic
  autoattack remains authored in `auto_attacks[]`.

## Bars and runtime authorization

All selected Spells appear on one global Spell bar, which remains visible
while switching weapons. All selected Techniques for the currently equipped
non-Staff Discipline appear on one merged Technique bar. Selected Perks and
Traits have no bar entry.

Bar position is presentation metadata, not authorization. Runtime execution
checks the selected-only v2 rows materialized from the frozen Hub snapshot:

- Technique: selected feature + selected source Form + matching equipped
  parent Discipline;
- Spell: selected feature + selected source Form/School, with no equipped
  Discipline requirement;
- Perk: selected feature + selected source Form/School; and
- Trait: selected Trait row.

Do not reconstruct availability from inventory, equipment alone, animation
profiles, legacy `selection_kind` groupings, presentation rows, learned-spell
state, or client bar contents.

## Authoring workflows

### Technique

1. Author or select the melee/projectile action in the owning weapon
   Discipline's established gameplay and animation path.
2. Add the player ability row to `progression_catalog.shared.json`.
3. Classify it exactly once as `TECHNIQUE` under a non-Staff Form in the
   reviewed v2 ledger.
4. Keep gameplay kind honest. A Technique may use an existing spell executor
   when that is its real mechanic; this does not make it a Spell.
5. Regenerate the v2 catalog and run the synchronization tests below.

### Spell

1. Add the player ability with its cast, targeting, delivery, cooldown, and
   resource behavior in `progression_catalog.shared.json`.
2. Classify it exactly once as `SPELL` under a Form or School. A weapon Form
   may own a Spell.
3. Add a semantic motion, fixed exception, or explicit no-animation entry in
   `SpellCastAnimationMap` for an active cast.
4. Preserve the global spell animation mapping. Equipped-Discipline overrides
   are allowed; Form- or School-level animation sets are not.
5. Add the required ability presentation, VFX cues, and icon.

### Perk

Use `PERK` for a selectable passive that belongs to a Form or School. Its
effect must consult the selected Perk/source-Specialization predicate and must
not require that source weapon to be equipped unless the individual mechanic
explicitly says so.

### Trait

Add a reviewed Trait definition and implement its effect through the shared
selected-Trait predicate. Traits do not satisfy a selected Specialization's
nonempty requirement and do not consume the global Combat Feature capacity.

### Autoattack and replacement

`auto_attacks[]` rows are intrinsic, keyed by Discipline plus an optional
mode, and never consume a build slot or feature capacity. An
`AUTO_ATTACK_REPLACEMENT` can be a selected Combat Feature; pressing it arms
the next intrinsic swing rather than creating a second autoattack authority.

## Validation and publication

Run the repeatable catalog gate from the repository root:

```bash
ops/check-source-of-truth.sh
```

It checks v2 generation and ownership, shared JSON mirrors, NPC paths/GUIDs,
and the full server and Hub Rust unit suites. It requires Python 3, Ruby with
Minitest, and Rust dependencies; it does not start Unity, publish modules, or
require a live database. The existing PvP validation workflow runs the same
command when the relevant sources or authoring assets change. This is not
Unity compilation, visual verification, or a binding regeneration check.

For build/handoff changes, also run the relevant match tests:

```bash
cargo test --manifest-path match-server/Cargo.toml combat_build_v2::tests
cargo test --manifest-path match-server/Cargo.toml \
  match_contract::tests::frozen_combat_build_is_typed_bounded_canonical_and_revalidated \
  -- --exact
```

After changing server code or baked shared data (including progression, melee
timing, or collision), use the canonical local synchronization command:

```bash
ops/setup-local-multiplayer.sh setup
ops/setup-local-multiplayer.sh status
python3 ops/benchmark-local-match-start.py --samples 1
python3 ops/test-combat-build-v2-compositions.py
```

It republishes the Hub data-preservingly, rebuilds disposable PvP/open-world
artifacts, records provenance, regenerates Hub/PvP Unity bindings, and restarts
the managed provisioner. Regenerate the open-world/harness binding namespace
with the documented command in `docs/project-structure.md` when its public
schema changes. Never hand-edit generated bindings to preserve a removed row
or reducer.

Start a new Hub-created match or open-world instance to use the rebuilt
artifacts. Editor shared-data auto-publish updates only its direct-local
database; its success does not refresh the Hub or these cached artifacts. See
[the local publication workflow](project-structure.md#generated-code) for the
targets and scope of each command.
