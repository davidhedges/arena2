# Combat Authoring Contract

Status: **Current after the combat-build cutover (2026-08-27).**

This is the entry point for adding or reviewing combat actions. Player combat
availability is owned only by the canonical Hub combat build described in
`docs/combat-build-progression-cutover-plan-2026-08-26.md`.

Prefer Unity editor authoring tools when they exist. Use
`docs/ability-implementation-prompt-template-2026-04-22.md` only as the manual
fallback. Animation-layer ownership remains in
`docs/combat-animation-authoring-contract.md`.

## Source ownership

### Progression catalog

`server/src/progression_catalog.shared.json` owns:

- the projected combat-build contract and its rules;
- the five combat disciplines and six Staff schools;
- selectable active and passive abilities;
- combat modes, resources, combat rules, and stat scaling;
- melee, spell, movement-delivery, and auto-attack gameplay tuning;
- action presentation and VFX cue ownership; and
- legal action-bar slot definitions.

The catalog does not own durable player choices. Hub build rows own selected
disciplines, per-discipline weapon configurations, Staff schools, exact active
slot assignments, and ordered passive selections. A disposable match receives
one validated, versioned snapshot of that build.

### Combat-build identity

The only selectable combat disciplines are:

- `DAGGERS`
- `TWO_HANDED_SWORD`
- `SWORD_AND_SHIELD`
- `ARCHER_BOW`
- `STAFF`

Every selectable ability has exactly one `combat_discipline_id`. A Staff
ability also has exactly one `spell_school_id`: `BLIGHT`, `MORTALITY`, `RUIN`,
`DIVINITY`, `ARCANA`, or `PRIMAL`. A Staff school configures the single Staff
discipline; it is never a discipline slot.

Fire, Cold, Lightning, Holy, Shadow, Air, and Necromancy are damage or
presentation values, not Staff schools and not build identities.

`selection_kind` is the build-facing classification:

- `ACTIVE`: assignable to one exact active slot in its owning discipline;
- `PASSIVE`: selectable in that discipline's ordered passive list; and
- `INTRINSIC`: runtime behavior outside the selectable ability budget.

Do not reconstruct selection from tags, inventory contents, equipment,
animation profiles, or presentation rows.

### Combat animation sets and melee manifest

`Assets/Arena/Resources/CombatAnimationSets/*.asset` owns Unity presentation,
authored melee strike IDs, runtime melee slot IDs, hit windows, recovery,
combos, phased clips, cast-motion bindings, and weapon presentation.

An animation profile is private presentation machinery derived from the active
canonical combat discipline. It is not a player-selected or persisted build
identity.

`server/src/melee_manifest.shared.json` is exported from those animation sets.
Do not hand-edit it to repair identity drift.

## Ability authoring shape

A selectable `abilities[]` row supplies at least:

- `ability_id`: stable player-facing ability identity;
- `actor_scope`: `PLAYER`, `NPC`, or `BOTH`;
- `combat_discipline_id`: the canonical owning discipline;
- `spell_school_id`: required only for selectable Staff abilities;
- `selection_kind`: `ACTIVE` or `PASSIVE`;
- `action_id`: the gameplay action or authored strike identity;
- resource cost and resource kind; and
- one typed `gameplay` block.

The supported gameplay kinds include `MELEE`, `SPELL`, `MOVEMENT`,
`AUTO_ATTACK_REPLACEMENT`, and `COMBAT_MODE_TOGGLE`. Public ability and
auto-attack projections derive their discipline identity from these canonical
fields.

Exact active placement is not catalog-authored. The Hub editor stores
`combat_discipline_id + action_slot + ability_id`, and the match materializes
that frozen assignment without synthesizing or copying it to another bar.

## Checklists

### Selectable melee

1. Author or select the melee strike in the owning discipline's animation set.
2. Re-export `server/src/melee_manifest.shared.json`.
3. Add an `abilities[]` row with the canonical discipline,
   `selection_kind: "ACTIVE"`, and `gameplay.kind: "MELEE"`.
4. Point `action_id` at the authored strike ID, never a runtime slot ID.
5. Put damage, range, cooldown, defense, and targeting tuning in `gameplay`.
6. Add the `ABILITY` presentation and required VFX cues.

### Selectable spell

1. Add an `abilities[]` row with `gameplay.kind: "SPELL"`.
2. Set its canonical discipline. If it belongs to Staff, set one consolidated
   `spell_school_id`.
3. Set `selection_kind` to `ACTIVE` or `PASSIVE` as appropriate.
4. Put cast, targeting, delivery, cooldown, and resource behavior in
   `gameplay`.
5. Add a semantic motion, fixed exception, or explicit no-animation entry in
   `SpellCastAnimationMap` for an active cast.
6. Add its `ABILITY` presentation and cue rows.

### Movement delivery

Use `gameplay.kind: "MOVEMENT"` and author execution in `gameplay.delivery`.
Arrival effects also belong in the delivery block. Do not create a duplicate
spell row or fixed-action wrapper.

### Passive

Use `selection_kind: "PASSIVE"` and the exact owning discipline. Runtime
effects must call the one canonical selected-passive predicate; current weapon
equipment does not gate a selected passive.

### Auto-attack and replacement

`auto_attacks[]` rows are intrinsic and keyed by `combat_discipline_id` plus
an optional mode. They do not consume selectable slots.

An `AUTO_ATTACK_REPLACEMENT` ability is selectable. Its `action_id` references
an `auto_attack_replacements[]` row, whose discipline must match. Pressing the
ability arms the next intrinsic swing; it does not execute a separate strike.

### Combat mode

`combat_modes[]` rows are keyed by canonical discipline. Mode state is derived
runtime state under the active frozen discipline, not an independent build
choice.

## Validation

Run, in proportion to the changed surfaces:

```bash
cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast
cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast
ops/dungeon-compile-gate.sh
```

The production combat-build validator checks catalog membership, exact active
slots, selected-passive ownership, Staff school membership, combined and active
budgets, per-discipline minimums, weapon pair legality, and dormant-reference
validity. Hub saves and match bootstrap use that same rules projection.

Regenerate Hub and match bindings from the final schemas after any public-row
change. Never hand-edit generated bindings to preserve a removed field.
