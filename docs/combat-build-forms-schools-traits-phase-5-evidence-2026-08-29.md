# Combat Build v2 Phase 5 evidence

Date: 2026-08-29
Status: PASS

## Scope

Phase 5 adds a transport-neutral weapon-switch and cast-interruption policy to
`match-v2-rehearsal`, plus an executable source/presentation audit. It does not
change canonical v1 gameplay reducers, match/open-world schemas, Unity
animation assets, Animator controllers, or player-visible HUD/input behavior.

The live probe publishes one unique `arena-cbv2-p5-*` database, invokes its
anonymous rehearsal reducer, verifies rows/results, and deletes the database.

## Distinct-parent switching

`SwitchRuntimeV2` derives switch targets in selected Specialization slot order
and deduplicates them by parent Discipline. Its Phase 5 fixture selects
Bladedancer, Executioner, and Ruin, producing exactly two targets:

1. `DAGGERS`
2. `STAFF`

The two Dagger Forms share one merged Technique bar containing Quick Cut and
Gut Ripper. Selecting the already-current Dagger parent is a no-op and neither
duplicates switch state nor resets timing.

An actual parent switch applies the locked order: interrupt/fizzle any active
cast, change the equipped parent, clear auto-attack state and advance its
timing epoch, clear combo/potential/weapon transients, expose the new parent's
Technique bar, and leave the global Spell bar unchanged. Under Staff, the
Technique bar is empty while ordinary auto-attack remains available.

## Cast interruption

The executable policy contains all fourteen accepted action families from the
plan: movement, jump, Discipline switch, Technique, Spell, dodge, block,
parry, interact, fixed combat action, auto-attack start, stagger, knockback,
and death. Each active-cast probe produces an authoritative fizzle, an
immediate client `CombatSpellAnimationPhase.Cancel`, and a postcondition with
no active hold or temporary action-owned prop. Rejected input remains outside
the accepted-action policy.

The canonical integration primitives already exist and are source-anchored in
the generated audit: `fizzle_active_cast_for_interrupt`,
`clear_auto_attack_for_owner`, the local movement cancel request, the client
Cancel-phase dispatcher, and action-owned temporary-prop release. Phase 7 will
atomically connect the canonical accepted-action entry points to this policy.

## Presentation compatibility

`docs/combat-build-v2-phase-5-presentation-inventory-2026-08-29.json` is
generated and checked by
`ops/generate-combat-build-v2-phase5-presentation-inventory.py`.

The audit deliberately separates discovery from compatibility scope:

- presentation discovery remains keyed by the actual spell execution path,
  currently `gameplay.kind=SPELL` and the global spell map;
- all 104 semantic Spells require resolution and hold/release/cancel coverage
  under Dagger, Two-Handed Sword, Sword and Shield, Bow, and Staff animation
  profiles; and
- all 23 Techniques that use the spell executor validate only under their one
  parent weapon profile, as selected by semantic `loadout_kind`.

The resolver remains global map/shared recipe first with an optional override
from the currently equipped `CombatAnimationSet`. No Form/School animation
set or override, Animator controller, layer, state, recipe duplication, or
topology was added.

Blessed Shield remains a Sword-and-Shield-gated Technique that happens to use
the spell executor. It is not converted into a weapon-independent Spell. Its
temporary shield is action-owned, and the existing Cancel path releases it
before the new equipped-Discipline presentation takes over.

The four Staff melee player abilities remain on the removal ledger. Staff has
zero selectable/intrinsic Techniques; only private `STAFF_STRIKE_2`
clip/action data may survive when required by ordinary Staff auto-attack, and
that private data grants no feature authorization.

## Live rehearsal

`ops/run-combat-build-v2-phase5-rehearsal.sh` published
`arena-cbv2-p5-20260830012046-89554` and ran
`run_phase_5_switch_interrupt_probe` anonymously.

All eleven live checks were true:

- distinct parent targets;
- repeated-parent deduplication;
- switch reset ordering and state cleanup;
- switch-driven authoritative cast cancellation;
- the complete accepted-action interrupt matrix;
- immediate client Cancel-phase outcome;
- selected Spell authorization under every selected parent;
- no Staff Technique bar or authorization;
- retained Staff ordinary auto-attack;
- stable global Spell bar across switching; and
- Blessed Shield temporary-prop disposition.

The materialized row counts were one root, three selected Specializations, two
parent configurations, two Techniques, one Spell, zero Perks, zero Traits,
one active parent, two switch targets, and one result. Canonical v1 Hub
combat-build counts were unchanged and the disposable database was retired.

## Verification

- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`: PASS,
  794 passed, 0 failed.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 25 passed, 0 failed.
- `cargo test --manifest-path match-v2-rehearsal/Cargo.toml --lib --no-fail-fast`:
  PASS, 15 passed, 0 failed.
- `python3 ops/generate-combat-build-v2-phase5-presentation-inventory.py --check`:
  PASS.
- `python3 ops/generate-combat-build-v2-phase4-inventory.py --check`: PASS.
- `python3 ops/generate-combat-build-v2-catalog.py --check`: PASS.
- `bash -n ops/run-combat-build-v2-phase5-rehearsal.sh`: PASS.
- `ops/run-combat-build-v2-phase5-rehearsal.sh`: PASS; the disposable database
  was retired.
- `ops/setup-local-multiplayer.sh status`: PASS. The canonical SpacetimeDB,
  match artifact, open-world artifact, and provisioner are ready/running.
- `git diff --check`: PASS.

No Unity Editor or batch-mode run was used.

## Exit gate

PASS. The interrupt matrix leaves no active cast hold or temporary prop,
repeated same-parent Forms produce no duplicate switch state, Staff has no
Technique authorization while retaining ordinary auto-attack, semantic Spell
presentation is compatible under all equipped Discipline profiles, and the
existing animation architecture required no expansion.
