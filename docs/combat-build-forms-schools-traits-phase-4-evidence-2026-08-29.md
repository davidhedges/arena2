# Combat Build v2 Phase 4 evidence

Date: 2026-08-29
Status: PASS

## Scope

Phase 4 adds normalized Combat Build v2 authorization only to
`match-v2-rehearsal`. It does not change canonical v1 authorization,
production gameplay reducers, match/open-world schemas, the HUD, input, or
animation assets.

The live probe publishes one unique `arena-cbv2-p4-*` database, invokes its
anonymous rehearsal reducer, verifies rows/results, and deletes the database.

## Normalized runtime view

The internal `NormalizedMatchBuildV2` reconstructs immutable authorization
state from materialized rows. Construction fails closed for duplicate
Specializations/features, unknown or mismatched source Specializations,
unselected parents, a Staff Technique, or a stored Mastery predicate that
differs from the selected Trait plus one-distinct-parent rule.

Its predicates are:

- Technique authorization: the feature must be selected, its source
  Specialization and parent must be selected, and its parent must equal the
  currently active/equipped Discipline;
- Spell authorization: the feature must be in the global selected Spell set
  and there must be a currently active selected parent, but the Spell's source
  parent need not be current;
- persistent active membership: a selected Technique or Spell may reconcile
  owned persistent state without granting wrong-weapon invocation;
- Perk activation: the Perk row and its exact source Specialization/parent
  must be selected;
- Trait selection: the Trait is character-wide and has no Specialization
  prerequisite; and
- Mastery activation: `MASTERY` must be selected and the build must contain
  exactly one distinct parent Discipline.

Missing active state, an unselected active parent, absent features, dormant
features, and inconsistent normalized rows deny rather than fall back.

## Mastery damage scope

The Phase 0 10% scalar is exercised for all reviewed normal player-authored
outgoing paths:

- autoattacks;
- Techniques;
- Spells; and
- owned periodic damage.

A one-parent build with selected Mastery maps 100 damage to 110 on every path.
A multi-parent build or a one-parent build without Mastery remains 100.
System, self-inflicted-final, and copied-final scopes remain 100 even when
Mastery is active.

The future canonical insertion point is the non-system outgoing multiplier
chain in `resolve_damage_amount`. Both player and NPC target application call
that function. Existing self-inflicted/redirected/reckoning/assist-cost final
amounts and copied Fulmination final damage branch before the chain, matching
the locked exclusions. Phase 4 records this disposition but does not alter the
canonical resolver.

## Exhaustive call-site inventory

`docs/combat-build-v2-phase-4-runtime-callsite-inventory-2026-08-29.json` is
generated and checked by
`ops/generate-combat-build-v2-phase4-inventory.py`.

It inventories all 42 server runtime calls through the four centralized v1
authorization APIs:

- ten authored-action and three ability-ID active-invocation calls route by
  v2 `loadout_kind` at cutover;
- three persistent-active reconciliation calls route to selected
  Technique-or-Spell membership; and
- 26 existing passive-effect calls route to selected Perks with exact source
  Specializations. They do not become character-wide Traits.

The generator also inventories all 19 direct accesses to the four frozen v1
selection tables and fails if one appears outside `match_contract.rs`
(materialization/cleanup) or `progression.rs` (central authorization/debug).
No leaf gameplay module bypasses the centralized APIs.

## Live rehearsal

`ops/run-combat-build-v2-phase4-rehearsal.sh` published
`arena-cbv2-p4-20260830010816-87342` and ran
`run_phase_4_authorization_probe` anonymously.

The fixture selected Bladedancer, Ruin, and Vanguard across Dagger, Staff, and
Two-Handed Sword parents; remembered Heartseeker as dormant; selected one
Technique for each weapon Form, one Ruin Spell, one Ruin Perk, and Mastery.

All eight live checks were true:

- the selected Spell authorized under all three active parents;
- each Technique authorized only under its own active weapon parent;
- Staff materialized/authorized no Technique;
- the selected Ruin Perk was active while unselected/dormant-source Perks were
  not;
- Mastery was selected as a character-wide Trait but inactive for the
  three-parent build;
- dormant and unselected Techniques, Spells, and Perks failed closed;
- persistent membership included selected actives independent of current
  parent and excluded dormant actives; and
- Mastery modified all four reviewed normal paths only in the one-parent
  selected-Trait fixture and respected all exclusions.

The materialized row counts were one root, three selected Specializations,
three parent configurations, two Techniques, one Spell, one Perk, one Trait,
one active parent, and one result. Canonical v1 Hub combat-build counts were
unchanged and the disposable database was retired.

## Verification

- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`: PASS,
  794 passed, 0 failed.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 25 passed, 0 failed.
- `cargo test --manifest-path match-v2-rehearsal/Cargo.toml --lib --no-fail-fast`:
  PASS, 12 passed, 0 failed.
- `python3 ops/generate-combat-build-v2-phase4-inventory.py --check`: PASS.
- `python3 ops/generate-combat-build-v2-catalog.py --check`: PASS.
- `bash -n ops/run-combat-build-v2-phase4-rehearsal.sh`: PASS.
- `ops/run-combat-build-v2-phase4-rehearsal.sh`: PASS; the disposable database
  was retired.
- `ops/setup-local-multiplayer.sh status`: PASS. The canonical SpacetimeDB,
  match artifact, open-world artifact, and provisioner remain ready/running.
- `git diff --check`: PASS.

No Unity Editor or batch-mode run was used.

## Exit gate

PASS. Anonymous reducer probes prove global selected Spells across every
equipped parent, exact current-weapon Technique gating, no Staff Techniques,
selected-source Perks, character-wide Traits, dormant/unselected fail-closed
behavior, persistent active membership, and the one-parent Mastery damage
scope. The executable inventory covers every current active/passive consumer,
and canonical v1 authorization remains untouched.
