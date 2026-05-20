# Ability Implementation Prompt Template

Use this prompt when you need Codex to add combat abilities before the Unity authoring tools cover the workflow.

The canonical contract is `docs/combat-authoring-contract.md`. Prefer a Unity editor workflow once one exists for the action type; this template is the manual/LLM fallback.

## Copy/Paste Prompt

```text
Before making changes, read and follow docs/combat-authoring-contract.md.

Implement this combat ability in the current arena2 architecture.

Ability:
- ability_id: <ABILITY_ID>
- display_name: <DISPLAY_NAME>
- class_id: <SUBCLASS_ID>
- type: <SELECTABLE_MELEE | AUTO_ATTACK_REPLACEMENT | SELECTABLE_SPELL | SELF_APPLY_STATUS_SPELL | MOVEMENT_DELIVERY | FIXED_ACTION_BINDING>

Identity:
- authored_melee_strike_id: <REQUIRED_FOR_SELECTABLE_MELEE>
- replacement_id: <REQUIRED_FOR_AUTO_ATTACK_REPLACEMENT>
- replacement_authored_melee_strike_id: <REQUIRED_FOR_AUTO_ATTACK_REPLACEMENT>
- spell_id: <REQUIRED_FOR_SELECTABLE_SPELL_OR_SELF_APPLY_STATUS_SPELL>
- movement_delivery_kind: <REQUIRED_FOR_MOVEMENT_DELIVERY>
- fixed_action_id: <REQUIRED_FOR_FIXED_ACTION_BINDING>
- bound_ability_id: <REQUIRED_FOR_FIXED_ACTION_BINDING>

Tuning:
- baseline: <OPTIONAL_EXISTING_ABILITY_OR_SPELL_ID>
- overrides: <RESOURCE/COST/DAMAGE/RANGE/COOLDOWN/TARGETING/STATUS DETAILS>

Presentation:
- description: <DESCRIPTION>
- icon_id: <ICON_ID_OR_BASELINE>

Default loadout:
- placement: <NO_DEFAULT_PLACEMENT | FIRST_OPEN_SLOT | SLOT_ID>

Rules:
- If any required identity field is missing, ask before editing.
- If tuning is missing, use the baseline only if one is provided; otherwise ask before editing.
- Do not hand-edit server/src/melee_manifest.shared.json.
- Do not use runtime slot ids like light_combo_1, utility_1, heavy_gapclose, or finisher_2 in player-facing ability data.
- For melee ability rows, action_id must be the authored melee strike id from the combat animation set Id field.
- For auto-attack replacement ability rows, action_id must be the replacement_id; the replacement row points at the authored melee strike id.
- For spell ability rows, use gameplay.kind "SPELL"; action_id is the runtime spell id and spell behavior lives under gameplay.
- For movement delivery ability rows, use gameplay.kind "MOVEMENT"; movement behavior lives under gameplay.delivery.
- For default loadout ability placement, use action_kind "ABILITY" and action_id set to the ability_id.
- For default loadout fixed-action placement, use action_kind "FIXED" and action_id set to the fixed_action_id.
- If the requested behavior needs runtime support that does not exist, stop and report the blocker instead of faking it.

Run cargo test --manifest-path server/Cargo.toml and fix any combat authoring validator failures.
```

## Required Inputs

### Selectable Melee

- `ability_id`
- `display_name`
- `class_id`
- authored melee strike id from the combat animation set `Id` field
- loadout placement, or `NO_DEFAULT_PLACEMENT`
- explicit tuning, or a baseline ability to copy from

The authored melee strike id becomes the ability row's `action_id`. The loadout assignment uses the `ability_id`.

### Auto-Attack Replacement

- `ability_id`
- `display_name`
- `class_id`
- `replacement_id`
- authored melee strike id from the combat animation set `Id` field
- loadout placement, or `NO_DEFAULT_PLACEMENT`
- explicit tuning, or a baseline ability/replacement to copy from

The ability row uses `gameplay.kind: "AUTO_ATTACK_REPLACEMENT"` and `action_id` set to the `replacement_id`. The replacement row uses `authored_melee_strike_id` for the actual swing presentation and hit timing source.

The button arms the next auto attack only. Resource is paid when the auto attack fires and only if the replacement can be used; otherwise the normal auto attack fires and the pending replacement is consumed.

### Selectable Spell Or Self-Apply-Status Spell

- `ability_id`
- `display_name`
- `class_id`
- `spell_id`
- loadout placement, or `NO_DEFAULT_PLACEMENT`
- explicit spell behavior/tuning, or a baseline spell to copy from

The spell id becomes the ability row's `action_id`.

The ability row uses `gameplay.kind: "SPELL"`. Spell cooldown/cast/targeting/resource fields live inside `gameplay`, and behavior-specific fields live inside `gameplay.delivery`. There is no top-level `spells[]` row to edit.

### Movement Delivery

- `ability_id`
- `display_name`
- `class_id`
- movement delivery kind, currently usually `DASH_TO_TARGET`
- loadout placement
- explicit movement tuning, or a baseline movement ability to copy from

The ability row uses `gameplay.kind: "MOVEMENT"`. Movement execution fields live inside `gameplay.delivery`. Charge-like abilities are selectable abilities; place the ability id on the action bar and do not add a fixed-action binding.

### Fixed Action Binding

- `fixed_action_id`
- `class_id`
- bound class ability id
- presentation details if missing
- loadout placement, if it should appear on the action bar

Use fixed-action bindings only for true fixed inputs. Do not model charge-like movement abilities this way; they should be ordinary selectable abilities with `gameplay.kind: "MOVEMENT"`.

## Example: Auto-Attack Replacement

```text
Before making changes, read and follow docs/combat-authoring-contract.md.

Implement this combat ability in the current arena2 architecture.

Ability:
- ability_id: WARRIOR_DREAD_STRIKE
- display_name: Dread Strike
- class_id: WARRIOR
- type: AUTO_ATTACK_REPLACEMENT

Identity:
- replacement_id: WARRIOR_DREAD_STRIKE
- replacement_authored_melee_strike_id: COMBO_ATTACK_2_4_LUNGE

Tuning:
- baseline: WARRIOR_HEW
- overrides: pay Rage only when the next auto attack fires; if the replacement cannot pay, the normal auto attack fires

Presentation:
- description: A committed lunging strike.
- icon_id: copy from WARRIOR_HEW unless a better existing icon is obvious

Default loadout:
- placement: FIRST_OPEN_SLOT

Rules:
- If any required identity field is missing, ask before editing.
- Do not hand-edit server/src/melee_manifest.shared.json.
- Do not use runtime slot ids like finisher_2.
- For the auto-attack replacement ability row, action_id must be WARRIOR_DREAD_STRIKE.
- For the auto_attack_replacements row, authored_melee_strike_id must be COMBO_ATTACK_2_4_LUNGE.
- For default loadout placement, use action_kind "ABILITY" and action_id "WARRIOR_DREAD_STRIKE".

Run cargo test --manifest-path server/Cargo.toml and fix any combat authoring validator failures.
```

## Identity Reminders

- Combat animation set `Id` is the authored melee strike id.
- Combat animation set `Slot Id` is runtime plumbing.
- Melee ability `action_id` uses the authored melee strike id.
- Auto-attack replacement ability `action_id` uses the replacement id; the replacement row uses the authored melee strike id.
- Selectable ability loadout placement uses the ability id.
- Fixed-action loadout placement uses the fixed action id. Charge-like movement loadout placement uses the ability id.
