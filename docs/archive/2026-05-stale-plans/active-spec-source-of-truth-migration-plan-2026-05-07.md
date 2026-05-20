# Active Spec Source of Truth Migration Plan - 2026-05-07

## Purpose

This plan removes the duplicated "active spec" source of truth from the loadout/progression system.

The end state is:

- `CharacterProgression` owns only the character's current class/progression identity.
- `CharacterClassLoadoutState` owns the active saved spec for each `(owner, class_id)` pair.
- All gameplay, loadout UI, action bar, stat, and tooltip reads resolve the active spec through `CharacterClassLoadoutState`.
- `CharacterProgression.active_spec_id` is removed from the server schema and generated Unity bindings.
- No runtime path falls back to checking both tables after the migration is complete.

This is separate from the Charge melee gap-close migration. Charge exposed the problem because active loadout authorization could disagree with what the action bar showed, but the duplicated active-spec state is a broader loadout architecture issue.

## Current System

### Server Tables

`server/src/progression.rs` currently defines:

```rust
#[table(accessor = character_progression, public)]
pub struct CharacterProgression {
    #[primary_key]
    pub owner: Identity,
    pub class_id: String,
    pub active_spec_id: String,
}

#[table(accessor = character_class_loadout_state, public)]
pub struct CharacterClassLoadoutState {
    #[primary_key]
    pub key: String,
    #[index(btree)]
    pub owner: Identity,
    pub class_id: String,
    pub active_spec_id: String,
    pub updated_at: Timestamp,
}
```

The `CharacterClassLoadoutState.key` is:

```rust
format!("{}:{}", owner.to_hex(), normalize_identifier(class_id))
```

The default saved spec id is:

```rust
format!("{}:{}:default", owner.to_hex(), normalize_identifier(class_id))
```

### Current Write Paths

`activate_saved_spec` currently writes both tables:

- `CharacterProgression.active_spec_id = selected_spec_id`
- `CharacterClassLoadoutState.active_spec_id = selected_spec_id`

`switch_loadout_class` currently writes:

- `CharacterProgression.class_id = selected_class`
- `CharacterProgression.active_spec_id = active_spec_id_for_class(selected_class)`

`ensure_default_progression_for_identity` currently creates or repairs both:

- `CharacterProgression.class_id`
- `CharacterProgression.active_spec_id`
- `CharacterClassLoadoutState` for the current class

`repair_class_loadout_state_rows` currently syncs `CharacterProgression.active_spec_id` from `CharacterClassLoadoutState.active_spec_id` when they disagree.

### Current Read Paths

Server gameplay currently has a tolerance shim:

```rust
fn active_spec_ids_for_owner(ctx: &ReducerContext, owner: Identity) -> Vec<String> {
    // first CharacterProgression.active_spec_id
    // then CharacterClassLoadoutState.active_spec_id for current class
}
```

That means active loadout authorization can succeed from either table. This is useful during migration, but it is not a production-grade invariant because it allows two different answers.

Other server reads still use `CharacterProgression.active_spec_id` directly. One important example is:

```rust
active_stat_totals_for_owner -> stat_totals_for_spec(progression.active_spec_id)
```

Unity client reads also use `CharacterProgression.ActiveSpecId` directly:

- `ActiveLoadoutResolver.ResolveActiveSelectableAction` filters `SavedSpecSlotAssignment` by `progression.ActiveSpecId`.
- `LoadoutController` selects the visible saved spec from `progression.ActiveSpecId`.
- `HubController.ResolveVisibleSpecId` falls back to `progression.ActiveSpecId`.
- Generated bindings expose `CharacterProgression.ActiveSpecId`.

## Problem

There are two independently stored answers to the same question:

"Which saved spec is active for this character's current class?"

The two answers can drift:

- `CharacterProgression.active_spec_id`
- `CharacterClassLoadoutState.active_spec_id` for `(owner, CharacterProgression.class_id)`

That drift is dangerous because different systems read different tables. The action bar can display one active spec while server gameplay authorization evaluates another. The result is user-visible behavior such as "this action is on my bar, but the server says it is not assigned on the active spec."

The correct model is not "read both forever." The correct model is one authority and one repair/migration story.

## Decision

`CharacterClassLoadoutState` is the source of truth for active saved spec selection.

Reasoning:

- It is keyed by `(owner, class_id)`, which matches the product behavior: each class should remember its own active spec.
- It already has `updated_at`, so active spec changes have an explicit state-change timestamp.
- It lets `CharacterProgression` stay focused on the current class instead of also storing per-class loadout state.
- It prevents a class switch from destroying the previous class's active saved spec choice.

`CharacterProgression` should keep:

- `owner`
- `class_id`

`CharacterProgression` should not keep:

- `active_spec_id`

## Migration Strategy

Use two stages.

Stage 1 is behavior migration with schema compatibility. The `active_spec_id` column remains on `CharacterProgression`, but production code stops reading it as the active spec authority.

Stage 2 is schema cleanup. Remove `CharacterProgression.active_spec_id`, regenerate/update bindings, and delete all compatibility shims.

This two-stage approach avoids mixing a behavior migration with a public table shape change.

## Stage 1 - Behavior Migration

### 1. Add Server Resolver Helpers

Add these helpers in `server/src/progression.rs`.

```rust
fn current_class_id_for_owner(ctx: &ReducerContext, owner: Identity) -> Option<String> {
    ctx.db
        .character_progression()
        .owner()
        .find(owner)
        .map(|progression| canonical_class_id(progression.class_id.as_str()))
}

fn active_class_loadout_state_for_owner(
    ctx: &ReducerContext,
    owner: Identity,
) -> Option<CharacterClassLoadoutState> {
    let class_id = current_class_id_for_owner(ctx, owner)?;
    let key = character_class_loadout_key(owner, class_id.as_str());
    let state = ctx.db.character_class_loadout_state().key().find(key)?;
    if !spec_belongs_to_owner_and_class(ctx, owner, state.active_spec_id.as_str(), class_id.as_str()) {
        return None;
    }
    Some(state)
}

fn active_spec_id_for_owner(ctx: &ReducerContext, owner: Identity) -> Option<String> {
    active_class_loadout_state_for_owner(ctx, owner).map(|state| state.active_spec_id)
}
```

Rules:

- Normal gameplay reads use `active_spec_id_for_owner`.
- Repair/initialization code may use `ensure_class_loadout_state`.
- Normal gameplay reads must not create or repair rows as a side effect.
- `CharacterProgression.active_spec_id` may only be read by migration/repair code during Stage 1.

### 2. Replace Server Reads

Replace direct `CharacterProgression.active_spec_id` reads with `active_spec_id_for_owner`.

Required server changes:

- `active_stat_totals_for_owner`
  - Current behavior: reads `progression.active_spec_id`.
  - New behavior: reads `active_spec_id_for_owner`; returns default totals if absent.
- `active_spec_ids_for_owner`
  - Remove the two-table list behavior.
  - Replace it with a single-spec helper or delete it.
  - `active_selectable_ability_for_authored_action`, `active_selectable_ability_for_ability_id`, and `active_loadout_assignment_debug_summary` must use exactly one active spec id from `CharacterClassLoadoutState`.
- Any future server active-spec reads must go through `active_spec_id_for_owner`.

Do not keep the old multi-spec fallback after Stage 1. If the class loadout state is missing, that is a repair/init bug, not a valid runtime state.

### 3. Change Server Writes

Change `activate_saved_spec`:

- Keep the validation that the saved spec belongs to the owner.
- Keep the validation that `spec.class_id == CharacterProgression.class_id`.
- Write only `CharacterClassLoadoutState.active_spec_id`.
- Do not update `CharacterProgression.active_spec_id`.

Change `switch_loadout_class`:

- Ensure `CharacterClassLoadoutState` exists for the requested class.
- Update `CharacterProgression.class_id`.
- Do not update `CharacterProgression.active_spec_id`.
- Keep updating the `Player.class_id` mirror if that is still required by runtime spawning/presentation.

Change `delete_saved_spec`:

- Reject deletion if `class_loadout_state_uses_spec(ctx, owner, spec.class_id, spec.spec_id)` is true.
- Remove the `progression.active_spec_id == spec.spec_id` condition.

Change `ensure_default_progression_for_identity`:

- Always call `ensure_class_loadout_state` for the resolved class.
- During Stage 1 only, if `CharacterProgression.active_spec_id` is empty, it may be filled for backward compatibility.
- No new production logic may depend on that field.

Change `repair_class_loadout_state_rows`:

- Repair/create `CharacterClassLoadoutState` rows.
- During Stage 1 only, it may still backfill `CharacterProgression.active_spec_id`.
- Add a comment marking that write as temporary and removed in Stage 2.

### 4. Add Client Resolver Helpers

Add a single Unity helper, preferably near `ActiveLoadoutResolver` in `Assets/Arena/Runtime/Combat/GameplayContracts.cs`.

Required behavior:

```csharp
public static bool TryResolveActiveSpec(
    DbConnection? conn,
    SpacetimeDB.Identity? owner,
    out string classId,
    out string activeSpecId)
```

Resolution rules:

1. Read `CharacterProgression` by owner.
2. Use `progression.ClassId` as the current class.
3. Find `CharacterClassLoadoutState` for the same owner and class.
   - Prefer `conn.Db.CharacterClassLoadoutState.Owner.Filter(owner)` and compare normalized `ClassId`.
   - Do not depend on reconstructing the key in C# unless there is already a shared identity-to-hex helper.
4. Return `state.ActiveSpecId`.
5. Return false if progression, class id, state, or active spec id is missing.

Do not read `progression.ActiveSpecId` in this helper.

### 5. Replace Client Reads

Replace direct Unity reads of `CharacterProgression.ActiveSpecId`.

Required client changes:

- `ActiveLoadoutResolver.ResolveActiveSelectableAction`
  - Use `TryResolveActiveSpec`.
  - Filter `SavedSpecSlotAssignment` by the resolved active spec id.
  - Pass the resolved class id into `ResolveSelectableActionFromAssignment`.
- `LoadoutController`
  - When choosing `_selectedSpecId`, use the class loadout state's active spec id.
  - It may still allow UI-selected spec override, but the fallback active spec must come from `CharacterClassLoadoutState`.
- `HubController.ResolveVisibleSpecId`
  - Prefer `LoadoutController.Instance.SelectedSpecId` only if it belongs to the current class.
  - Then prefer `CharacterClassLoadoutState.active_spec_id`.
  - Then fall back to first spec for the class only as a display fallback.
- `ActionTooltipResolver`, if it resolves active class/loadout state, should use the same helper or only read `CharacterProgression.class_id`.

Stage 1 acceptance check:

```sh
rg -n "progression\\.ActiveSpecId|CharacterProgression\\.ActiveSpecId|\\.active_spec_id" Assets/Arena/Runtime server/src
```

Allowed Stage 1 hits:

- Generated binding files.
- Migration/repair comments and code explicitly marked Stage 1 compatibility.
- `CharacterClassLoadoutState.ActiveSpecId`.

Not allowed:

- Gameplay authorization reading `CharacterProgression.active_spec_id`.
- Action bar resolving slots from `CharacterProgression.ActiveSpecId`.
- UI selecting active saved spec from `CharacterProgression.ActiveSpecId`.

### 6. Stage 1 Tests

Add server tests in `server/src/progression.rs`.

Required cases:

1. `active_spec_id_for_owner_uses_class_loadout_state`
   - Create progression with class `WARRIOR`.
   - Create two warrior specs.
   - Set `CharacterProgression.active_spec_id` to spec A.
   - Set `CharacterClassLoadoutState.active_spec_id` to spec B.
   - Assert active action/stat resolution uses spec B.

2. `activate_saved_spec_updates_class_loadout_state`
   - Activate spec B.
   - Assert `CharacterClassLoadoutState.active_spec_id == spec B`.
   - Assert no production read requires `CharacterProgression.active_spec_id`.

3. `switch_loadout_class_restores_per_class_active_spec`
   - Warrior active spec W2.
   - Paladin active spec P2.
   - Switch to Paladin, assert active spec P2.
   - Switch back to Warrior, assert active spec W2.

4. `delete_saved_spec_rejects_class_state_active_spec`
   - Set class loadout state active spec to B.
   - Attempt to delete B.
   - Assert reducer returns `"cannot delete the active spec"`.

Add Unity tests if existing test infrastructure can create table rows cheaply:

1. `ActiveLoadoutResolver` resolves from `CharacterClassLoadoutState.ActiveSpecId` when `CharacterProgression.ActiveSpecId` is stale.
2. `LoadoutController` visible selected spec fallback uses `CharacterClassLoadoutState.ActiveSpecId`.

If Unity table-row setup is too heavy, add a small pure helper test around the new resolver logic rather than testing full UI.

## Stage 2 - Schema Cleanup

Begin Stage 2 only after Stage 1 passes server tests, Unity build, and an editor playtest where action bar inputs resolve from the expected active spec after class/spec switching.

### 1. Remove Server Schema Field

Change `CharacterProgression` to:

```rust
#[table(accessor = character_progression, public)]
pub struct CharacterProgression {
    #[primary_key]
    pub owner: Identity,
    pub class_id: String,
}
```

Then remove all construction/update assignments of `active_spec_id`.

Required edits:

- `ensure_default_progression_for_identity`
- `activate_saved_spec`
- `switch_loadout_class`
- `repair_class_loadout_state_rows`
- Any test fixtures constructing `CharacterProgression`

### 2. Remove Compatibility Code

Delete all Stage 1 compatibility that reads or writes `CharacterProgression.active_spec_id`.

Specifically:

- No `CharacterProgression.active_spec_id` fallback in `ensure_class_loadout_state`.
- No synchronization from `CharacterClassLoadoutState` back into `CharacterProgression`.
- No `active_spec_ids_for_owner` multi-source helper.

The runtime invariant becomes:

```text
CharacterProgression(owner).class_id determines current class.
CharacterClassLoadoutState(owner, class_id).active_spec_id determines active saved spec.
```

### 3. Update Generated Unity Bindings

Regenerate or manually update generated SpacetimeDB files.

Required generated changes:

- `Assets/Arena/Runtime/Generated/SpacetimeDB/Types/CharacterProgression.g.cs`
  - Remove `ActiveSpecId`.
  - Remove constructor parameter.
  - Remove default assignment.
- `Assets/Arena/Runtime/Generated/SpacetimeDB/Tables/CharacterProgression.g.cs`
  - Remove `ActiveSpecId` column.

Do not remove `CharacterClassLoadoutState.ActiveSpecId`; that is now the authoritative field.

### 4. Stage 2 Verification

Run:

```sh
cargo test
dotnet build Assembly-CSharp.csproj --no-restore
```

Run these searches:

```sh
rg -n "active_spec_id" server/src
rg -n "ActiveSpecId" Assets/Arena/Runtime
```

Expected results:

- Server `active_spec_id` hits remain only on `CharacterClassLoadoutState`, `SavedSpec` helpers, resolver helpers, and tests.
- Unity `ActiveSpecId` hits remain only on `CharacterClassLoadoutState` generated files and code that intentionally reads class loadout state.
- No `CharacterProgression.ActiveSpecId` hits.
- No `CharacterProgression.active_spec_id` hits.

## Rollout Notes

This migration changes public table shape in Stage 2. If existing clients may connect to a newer server, do not skip Stage 1. Stage 1 allows all production behavior to move to the new authority before the schema field disappears.

If this project does not need mixed-version client/server compatibility, Stage 1 and Stage 2 can land in one PR, but keep the commit order:

1. Add helpers and move reads.
2. Move writes.
3. Add tests.
4. Remove schema field and generated bindings.
5. Run verification.

Do not remove the schema field before all runtime reads are moved. That produces compile errors at best and silent UI/action-bar disagreement at worst if partial manual generated edits are made.

## Non-Goals

Do not redesign saved spec contents in this migration.

Do not change slot assignment storage.

Do not change stat allocation storage.

Do not change class switching product behavior.

Do not change Charge, melee gap close, or combat animation authoring in this migration.

Do not keep `CharacterProgression.active_spec_id` as a permanent "cache." A cache that participates in gameplay authorization is another source of truth.

## Final Invariants

After migration:

- Every non-dummy player with `CharacterProgression` has exactly one current class.
- For every `(owner, current_class_id)`, there is exactly one `CharacterClassLoadoutState` row.
- The `CharacterClassLoadoutState.active_spec_id` points to a `SavedSpec` owned by the same owner and class.
- All active loadout slot resolution reads assignments from that one active spec id.
- Class switching changes only the current class and then resolves that class's remembered active spec.
- Activating a saved spec changes only the active spec for that saved spec's class.
- Gameplay authorization, UI display, tooltips, and action bar input all resolve through the same active-spec helper.
- `CharacterProgression` no longer stores, mirrors, or caches active spec id.
