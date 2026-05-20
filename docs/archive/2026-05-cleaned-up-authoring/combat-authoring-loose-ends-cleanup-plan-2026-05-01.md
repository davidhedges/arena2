# Combat Authoring Loose Ends Cleanup Plan

This document is historical. Use `docs/combat-authoring-contract.md` for the current combat authoring contract.

Goal: remove stale combat-authoring artifacts, leave one obvious source of truth for humans and LLMs, close the deferred client dispatch ownership boundary, and defer Unity editor tooling until the manual path has been used once or twice.

## Desired End State

An LLM or developer should read these files, in this order:

1. `docs/combat-authoring-contract.md`
2. `docs/ability-implementation-prompt-template-2026-04-22.md`, only when using the manual/LLM fallback
3. the relevant source files named by the contract

They should not need to read old plans, audits, risk registers, or one-off example prompts to author a combat action.

## Phase 1: Canonical Docs Only

Keep:

- `docs/combat-authoring-contract.md`
- `docs/ability-implementation-prompt-template-2026-04-22.md`

Update if needed:

- Add a short note to `docs/combat-authoring-contract.md` that auto-attack per-hit resource gain is intrinsic auto-attack behavior; selectable melee resource changes should come from authored costs or explicit behavior, not generic hit gain.
- Add a short note that Unity editor authoring is the intended future workflow, while the prompt template is only the fallback.

Do not add more example prompt files. If an example is useful, it belongs inside the prompt template.

## Phase 2: Archive Or Delete Stale Planning Artifacts

Move or remove docs that describe already-completed plans or older mental models.

Candidates to archive under `docs/archive/2026-05-cleaned-up-authoring/`:

- `docs/combat-authoring-system-upgrade-plan-2026-04-29.md`
- `docs/client-server-combat-ownership-audit-2026-04-28.md`
- `docs/combat-animation-remaining-risk-register-2026-04-24.md`

Before archiving, extract any still-valid rule into `docs/combat-authoring-contract.md`. If nothing needs extraction, archive as-is with a one-line header:

```md
This document is historical. Use docs/combat-authoring-contract.md for the current combat authoring contract.
```

Delete one-off prompt scratch files instead of archiving them. They are worse than no doc because they look actionable but may encode a single experiment.

## Phase 3: Manual Workflow Trial

Use the cleaned prompt template for one real selectable melee ability.

The trial should answer:

- Did the prompt supply enough information without follow-up?
- Did the validator catch id/loadout mistakes?
- Did the LLM touch only source-of-truth files?
- Which fields were annoying enough to justify Unity editor UI?

After the trial, update only the canonical contract or prompt template. Do not create a new retrospective doc unless it captures a durable rule.

## Phase 4: Extract Action-Bar Dispatch Ownership

Goal: extract action-bar dispatch out of `SpellInputHandler` now that graph-backed dispatch metadata exists, so the file name stops teaching the wrong model.

Actions:

- Introduce `ActionBarInputDispatcher` or equivalent to own `ActionBarKeymap.SelectableBindings` iteration, slot resolution through `ActiveLoadoutResolver`, and routing to `FixedActionDispatcher`, melee executors, or spell executors from the resolved action.
- Leave `SpellInputHandler` owning only spell-specific behavior: aim mode, charge-on-release, and point-targeted aim confirm/cancel.
- Update `Assets/Arena/Tests/Editor/UiInputContractTests.cs` so `ActionBarDispatch_UsesResolvedAbilityKind` locks the new ownership boundary against the new dispatcher instead of `SpellInputHandler.cs`.

Stop when `SpellInputHandler.cs` no longer references `ActionBarKeymap.SelectableBindings`, existing keybind behavior is unchanged, `cargo test --manifest-path server/Cargo.toml` passes, and the editor tests pass.

Anti-goal: do not rename `SpellInputHandler` to `ActionBarInputDispatcher` in place. The extraction is the fix; a rename alone preserves the wrong ownership.

## Phase 5: Unity Editor First Slice

Build a Unity editor tool only after Phase 3 confirms the manual workflow shape.

First slice: selectable melee only.

The tool should:

- select subclass/combat profile
- list authored strike ids from the combat animation set `Id` fields
- show runtime `Slot Id` as read-only internal data, never as the ability action id
- create/update the progression ability row
- create/update the `ABILITY` presentation row
- place the ability in an explicit slot or first open slot
- refuse to write `server/src/melee_manifest.shared.json` by hand
- run or clearly point to `cargo test --manifest-path server/Cargo.toml`

Do not include bespoke spell behavior in the first editor slice. Existing spell behavior can be form-assisted later; new bespoke spell behavior remains an engineering task.

## Phase 6: Validation And Stop Criteria

After cleanup:

- `rg "Use docs/combat-authoring-contract.md|historical|superseded" docs` should make it obvious which docs are current and which are archived.
- `rg "ActionBarKeymap.SelectableBindings" Assets/Arena/Runtime/Input/SpellInputHandler.cs` should return nothing.
- `cargo test --manifest-path server/Cargo.toml` should pass.
- No generated combat action manifest should be checked in unless a real runtime/editor/audit consumer reads it.
- No loose one-off prompt file should remain outside the canonical prompt template.

Stop when the current authoring path is boring: one contract, one fallback prompt, one validator command, and no old plans in the main docs path.
