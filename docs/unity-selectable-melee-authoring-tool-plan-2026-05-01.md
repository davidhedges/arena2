# Unity Selectable Melee Authoring Tool Plan

Goal: build a lightweight Unity editor workflow for adding selectable melee abilities without asking authors or LLMs to remember `ability_id` vs authored strike id vs runtime slot id.

V1 should generate a copyable catalog snippet, not mutate `server/src/progression_catalog.shared.json`. That solves the real id-confusion problem without adding a catalog mutation library, Rust subprocess, or duplicated full-catalog DTO model.

This plan covers only selectable melee. Existing spell behavior and fixed-action authoring can be added later. Bespoke spell behavior remains an engineering task, not a form-fill task.

## Current Contract

Source files:

- `Assets/Arena/Resources/CombatAnimationSets/*.asset` owns authored melee strike ids, runtime slot ids, clips, hit windows, and combo links.
- `server/src/melee_manifest.shared.json` is exported from combat animation sets. The tool must not hand-edit it.
- `server/src/progression_catalog.shared.json` owns player-facing ability rows, melee gameplay tuning, action presentations, and default loadout assignments.
- `docs/combat-authoring-contract.md` remains the canonical authoring contract.

Manual trial finding before Dread Strike became an auto-attack replacement:

- `WARRIOR_DREAD_STRIKE` needed very little bespoke tuning. The useful inputs were ability id, display name, description, authored strike id, baseline ability, and loadout slot. Most gameplay fields were copied from `WARRIOR_HEW`.

## Phase 1: Read-Only Authoring View

Goal: expose the authoring graph before writing anything.

Actions:

- Add `SelectableMeleeAuthoringWindow` under `Arena/Combat Authoring/Selectable Melee`.
- Select class, then derive the combat profile and load the live `CombatAnimationSet` asset.
- Require saved/exported animation-set state before snippet generation, or clearly warn that the manifest may be stale.
- List authored melee strikes from the animation set:
  - show `Id` as the selectable ability `action_id`
  - show `Slot Id` as read-only runtime plumbing
  - show hit window count, recovery, and combo source
- List existing melee abilities for the class as tuning baselines.
- List loadout slots with current occupants.

Stop when a designer can select Warrior, see `COMBO_ATTACK_2_4_LUNGE`, and clearly see that `finisher_2` is not the ability action id.

## Phase 2: Snippet Generator V1

Goal: generate the three catalog snippets needed for a selectable melee ability without mutating the catalog file.

Form inputs:

- `ability_id`
- display name
- description
- class
- authored strike id selected from the animation set dropdown
- baseline melee ability
- optional tuning overrides, shown collapsed by default
- explicit loadout slot, or no loadout placement

Do not include "first open slot" in V1. The author should choose a slot explicitly until real usage shows this is tedious.

Generated output:

- one `abilities[]` row with `gameplay.kind: "MELEE"` and `action_id` set to the authored strike id
- one `action_presentations[]` row with `presentation_kind: "ABILITY"`
- optionally one default loadout assignment with `action_kind: "ABILITY"` and `action_id` set to the `ability_id`

The output should appear in a read-only text area with a copy-to-clipboard button and brief paste instructions. The author pastes the rows into `server/src/progression_catalog.shared.json` and runs validation in their terminal.

Stop when the tool can reproduce the manual `WARRIOR_DREAD_STRIKE` catalog rows as copyable JSON snippets.

## Phase 3: Guardrails

Goal: prevent the mistakes the tool exists to prevent.

Actions:

- Runtime `Slot Id` values must be visible but impossible to select as ability `action_id`.
- Warn before generating a snippet for an existing `ability_id`.
- Warn when the authored strike id is already exposed by another selectable ability for the same class.
- Warn if the selected strike exists in the animation set but does not appear in the exported melee manifest.
- Add an editor-side contract test that checks the window/snippet model does not offer runtime slot ids as ability action ids.

Stop when bad input such as `finisher_2` cannot be produced by the UI.

## Phase 4: Validation Guidance

Goal: make validation obvious without making Unity own shell/toolchain behavior in V1.

Actions:

- Show the exact validation command in the window:

```bash
cargo test --manifest-path server/Cargo.toml combat_authoring_graph_validates_first_pass_contract
```

- Show the full validation command:

```bash
cargo test --manifest-path server/Cargo.toml
```

- Add a copy command button.
- Defer in-editor command execution until there is a clear need and a cross-platform shell strategy.

Stop when the user can copy the command and validate the pasted snippet without opening another doc.

## Deferred Work

These are not V1:

- Directly mutating `progression_catalog.shared.json`.
- A Rust authoring subprocess.
- A full C# DTO model for the progression catalog.
- In-editor `cargo test` execution.
- "First open slot" automatic placement.
- Per-hit damage preview.
- Spell or fixed-action authoring.

Only add direct catalog mutation after the snippet workflow proves too slow. If that happens, prefer a small, tested writer with deterministic output and preservation tests over full-catalog reimplementation in Unity.

## Non-Goals

- Do not author new melee strikes in this window. Use `CombatAnimationSetEditor` for strike timing and presentation.
- Do not edit `server/src/melee_manifest.shared.json` directly.
- Do not implement spell behavior authoring.
- Do not implement bespoke runtime behavior.
- Do not replace server validation with editor-only validation.

## Final Acceptance

- A selectable melee ability snippet can be generated from Unity using only authored strike ids from the selected combat profile.
- Runtime slot ids are visible but impossible to submit as ability `action_id`.
- The snippet includes ability, presentation, and optional loadout rows.
- The user can paste the snippet into `server/src/progression_catalog.shared.json` and run `cargo test --manifest-path server/Cargo.toml`.
- `docs/combat-authoring-contract.md` still describes the workflow accurately.
