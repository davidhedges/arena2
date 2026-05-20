# Combat VFX Cue Authoring Plan

Status: Partially implemented on 2026-05-02.

Phase 1 data plumbing is already landed. `combat_vfx_cues[]` exists in
`server/src/progression_catalog.shared.json`, syncs into the public
`combat_vfx_cue_catalog` table, has first-pass graph validation, and has one
live cue:

```text
WARRIOR_EARTHSHATTER
  MELEE_IMPACT hit 0
  GROUND_UNDER_TARGET
  VFX_FIRE_AREA_BURST_01_ARENA
```

The Unity client also has a first-pass `CombatVFXDispatcher` that listens to
`SpellEvent` inserts and resolves combat cues through
`Resources/CombatVFX/CombatVFXRegistry`.

This plan is now a reconciliation and completion plan. It documents the
intended contract, what is already ratified by code, and the remaining work
needed before combat VFX cues are the general path.

Goal: allow spell-like visual effects to be attached to melee attacks without making melee actions pretend to be spells, duplicating runtime behavior, or hardcoding one-off VFX branches.

## Current Architecture

The current combat authoring split is already close to the right shape:

- `server/src/progression_catalog.shared.json` owns player-facing gameplay data, ability identity, tuning, presentations, and loadout exposure.
- `Assets/Arena/Resources/CombatAnimationSets/*.asset` owns melee strike identity, clips, hit windows, recovery, combo timing, and Unity-authored presentation timing.
- `server/src/melee_manifest.shared.json` is exported from Unity melee authoring by `CombatAnimationSetEditor` / `CombatAnimationSet.BuildMeleeExport()` and should remain a bridge, not a hand-authored gameplay source.
- Spell gameplay is authored on ability rows under `gameplay.kind: "SPELL"` and `gameplay.delivery`; runtime spell rows are derived at startup.
- `SpellVFXDispatcher` currently routes spell event rows into reusable Unity visual effect implementations.

The important constraint: a melee attack that wants a ground slam VFX should still be a melee attack. It should not become a fake spell just to reuse a visual.

## Current Implementation Snapshot

The code on disk currently supports a narrow V1 subset:

- Server catalog:
  - `combat_vfx_cues[]` is a top-level progression catalog array.
  - Valid `owner_kind` values are `ABILITY`, `SPELL`, and `MELEE_STRIKE`.
  - The catalog syncs into public `combat_vfx_cue_catalog`.
  - Validation checks owner resolution, supported trigger values, supported anchor values, supported attach modes, non-empty `vfx_id`, `hit_index` bounds for melee hit windows, and positive `scale`.
  - Melee `SpellEvent` rows carry the accepted `ability_id` when the melee action came from an authored ability; intrinsic auto-attacks leave it empty.
- Client:
  - `CombatVFXDispatcher` subscribes to `SpellEvent` inserts.
  - It builds a small combat VFX fact from authoritative spell event ingestion.
  - It resolves `ABILITY` and `MELEE_STRIKE` cues for melee events and `SPELL` cues for spell events.
  - It maps melee cast, impact, block, and parry events to melee cue triggers.
  - It maps spell cast, impact, and fizzle events to spell cue triggers.
  - It filters melee cues by `hit_index`.
  - It supports `SPAWN_WORLD`.
  - It supports `FOLLOW_ANCHOR` for `CASTER` and `TARGET` anchors only.
  - It resolves `IMPACT_POINT`, `GROUND_UNDER_TARGET`, `GROUND_UNDER_CASTER`, `CASTER`, and `TARGET` positions.
  - It loads prefab templates from `Resources/CombatVFX/CombatVFXRegistry`.
- Compatibility:
  - `SpellVFXDispatcher` still owns spell-kind VFX routing.
  - `SpellVFXDispatcher` also detects melee casts via melee definition lookup and suppresses the unknown-spell placeholder for melee events. That is an existing compatibility seam, not the desired long-term cue path.

Any future plan phase should be written against that reality rather than as a clean-sheet proposal.

## Design Direction

Create a first-class combat VFX cue layer.

VFX cues are presentation data attached to authoritative combat facts:

- melee cast
- melee active animation windows
- melee impact
- melee block or parry
- spell cast
- spell impact
- status application
- special movement start or arrival

The cue layer should be able to reuse visual templates currently implemented under spell VFX, but it should not reuse spell gameplay rows or spell dispatch semantics for melee.

Recommended mental model:

```text
combat action happens
  -> server emits normal gameplay facts
  -> client resolves VFX cues for that fact
  -> client spawns reusable combat VFX templates at authored anchors
```

This keeps gameplay categories clean:

- melee remains `gameplay.kind: "MELEE"`
- spells remain `gameplay.kind: "SPELL"`
- visual templates become shared combat presentation assets

## Cue Classes

V1 should support two presentation classes under the same cue system.

### World Or Impact Cues

These are one-shot or short-lived effects spawned into world space.

Examples:

- ground slam shockwave
- dust burst at impact
- frost nova ring
- fire impact burst
- teleport arrival flash

Typical shape:

```text
MELEE_IMPACT -> spawn VFX_GROUND_SLAM_SHOCKWAVE_01 at GROUND_UNDER_TARGET
```

These cues care most about event position, target position, ground projection, scale, and duration.

### Attached Animation Cues

These are effects that follow a character, hand, weapon, or socket for part of an animation.

Examples:

- greatsword slash trail
- shield glow during pummel
- elemental weapon sweep
- leap takeoff trail
- hand charge glow

Typical shape:

```text
MELEE_CAST -> attach VFX_GREATSWORD_ARC_TRAIL_01 to WEAPON_MAIN_HAND after 80ms for 420ms
```

These cues care most about anchor resolution, start delay, lifetime, attach mode, and weapon/profile compatibility. They are not a different system, but they need richer timing and attachment support than impact bursts.

## Authoring Model

Add VFX cues as optional presentation metadata. There are two useful authoring scopes.

### Ability-Scoped Cues

Use this when a player-facing ability should have a specific visual identity, independent of the underlying strike reused by other abilities.

Example:

```json
{
  "owner_kind": "ABILITY",
  "owner_id": "WARRIOR_GROUND_SLAM",
  "trigger": "MELEE_IMPACT",
  "hit_index": 0,
  "anchor": "GROUND_UNDER_TARGET",
  "vfx_id": "VFX_GROUND_SLAM_SHOCKWAVE_01",
  "scale": 1.25,
  "duration_ms": 700
}
```

This is the best default for ground slams, elemental melee variants, empowered attacks, and class-flavored visuals.

### Strike-Scoped Cues

Use this when the VFX belongs to the authored animation itself and should follow the strike wherever it is used.

Example:

```json
{
  "owner_kind": "MELEE_STRIKE",
  "owner_id": "COMBO_ATTACK_4_4_LUNGING_SLASH",
  "trigger": "MELEE_CAST",
  "anchor": "WEAPON_MAIN_HAND",
  "vfx_id": "VFX_HEAVY_SLASH_TRAIL_01",
  "duration_ms": 450
}
```

This fits weapon trails, footstep bursts, leap takeoff effects, and animation-specific anticipation effects.

Example weapon trail:

```json
{
  "owner_kind": "MELEE_STRIKE",
  "owner_id": "COMBO_ATTACK_4_4_LUNGING_SLASH",
  "trigger": "MELEE_CAST",
  "anchor": "WEAPON_MAIN_HAND",
  "vfx_id": "VFX_GREATSWORD_ARC_TRAIL_01",
  "attach_mode": "FOLLOW_ANCHOR",
  "start_delay_ms": 80,
  "duration_ms": 420,
  "scale": 1.0,
  "sort_order": 10
}
```

## Recommended Schema

Use the existing top-level `combat_vfx_cues[]` array in
`server/src/progression_catalog.shared.json` for combat presentation cues:

```json
"combat_vfx_cues": [
  {
    "owner_kind": "ABILITY",
    "owner_id": "WARRIOR_GROUND_SLAM",
    "trigger": "MELEE_IMPACT",
    "hit_index": 0,
    "anchor": "GROUND_UNDER_TARGET",
    "vfx_id": "VFX_GROUND_SLAM_SHOCKWAVE_01",
    "scale": 1.25,
    "duration_ms": 700,
    "sort_order": 10
  }
]
```

Recommended V1 fields:

- `owner_kind`: `ABILITY`, `SPELL`, or `MELEE_STRIKE`.
- `owner_id`: ability id, spell id, or authored melee strike id depending on `owner_kind`.
- `trigger`: combat fact that causes the cue.
- `hit_index`: optional melee hit window index. Defaults to all hits or first hit depending on trigger policy.
- `anchor`: where the effect should spawn.
- `vfx_id`: stable client-side visual template id.
- `attach_mode`: `SPAWN_WORLD` or `FOLLOW_ANCHOR`. Defaults from the VFX template when omitted.
- `start_delay_ms`: optional delay from the triggering combat fact before spawning or attaching.
- `scale`: optional visual scale multiplier.
- `duration_ms`: optional lifetime hint for non-self-terminating effects.
- `sort_order`: deterministic ordering for multiple cues.

`vfx_id` is a stable catalog identifier resolved by the client VFX registry.
It is not a C# class name. For example, `VFX_FIRE_AREA_BURST_01_ARENA` can resolve to
a prefab template, while code-level helpers such as `ImpactBurstVFX` remain
implementation details.

Cue matching is additive. If a combat fact matches both an ability-scoped cue
and a strike-scoped cue, both cues should fire. `sort_order` controls
deterministic spawn order for cues with the same triggering fact.

V1 trigger values:

- `MELEE_CAST`
- `MELEE_ACTIVE_WINDOW`
- `MELEE_IMPACT`
- `MELEE_BLOCK`
- `MELEE_PARRY`
- `SPELL_CAST`
- `SPELL_IMPACT`
- `SPELL_FIZZLE`
- `SPECIAL_MOVEMENT_START`
- `SPECIAL_MOVEMENT_ARRIVAL`

V1 anchor values:

- `CASTER`
- `TARGET`
- `IMPACT_POINT`
- `GROUND_UNDER_CASTER`
- `GROUND_UNDER_TARGET`
- `WEAPON_MAIN_HAND`
- `WEAPON_OFF_HAND`
- `WEAPON_BLADE_START`
- `WEAPON_BLADE_END`
- `LEFT_HAND`
- `RIGHT_HAND`

V1 attach mode values:

- `SPAWN_WORLD`: instantiate once at the resolved anchor position.
- `FOLLOW_ANCHOR`: currently resolves only `CASTER` and `TARGET` anchors. The
  dispatcher instantiates the prefab at the resolved world position, parents it
  to the resolved presentation root with world position preserved, applies cue
  scale, and schedules `Destroy(instance, durationSeconds)`.

Current `FOLLOW_ANCHOR` lifecycle is the Unity parent-child lifecycle, not a
custom detach policy. If the caster or target root cannot be resolved when the
cue is dispatched, the cue does not spawn. For delayed cues, the dispatcher
captures the resolved transform before the delay; if that transform is gone by
spawn time, the prefab spawns at the originally resolved world position without
being parented. If an attached anchor is destroyed before the cue duration ends,
Unity destroys the child object with the anchor.

## Runtime Contract

Do not make melee runtime cast spells for VFX.

For V1, the server can keep emitting the existing `SpellEvent` rows for melee cast and melee impact because the client already receives those as authoritative combat facts. The important change is that the client should stop treating VFX selection as "spell kind means visual kind" and instead route through a cue resolver.

Recommended client flow:

```text
SpellEvent insert arrives
  -> classify as melee, spell, charge, or other combat fact
  -> build CombatVfxFact
  -> resolve matching combat_vfx_cues
  -> spawn vfx_id at requested anchor
```

Longer term, rename or replace the overloaded `SpellEvent` presentation path with a neutral combat event path. That cleanup is worthwhile, but it is not required for the first VFX cue implementation.

VFX dispatch should be driven by authoritative event ingestion. Do not spawn
cue effects from local prediction or speculative input paths unless they are
explicitly marked as local-only anticipation effects. Replay and late-join
behavior should remain deterministic for authoritative impact cues.

For `MELEE_BLOCK` and `MELEE_PARRY`, `owner_kind` and `owner_id` refer to the
attacker's action, ability, spell, or melee strike that caused the defended
event. Defender-authored reaction VFX would need a separate owner model later.

## Client Presentation Shape

Introduce a neutral dispatcher:

```text
CombatVFXDispatcher
  - receives combat facts
  - resolves authored cues
  - instantiates visual templates
  - owns active VFX lifecycle
```

Then migrate existing spell VFX classes into reusable combat templates:

```text
FireballVFX
IcicleVFX
FrostNovaVFX
MeteorVFX
BeamVFX
NegateVFX
GroundSlamShockwaveVFX
WeaponTrailVFX
ImpactBurstVFX
```

`SpellVFXDispatcher` can either become a thin adapter over `CombatVFXDispatcher` or be retired after spell cues are authored.

`ImpactBurstVFX` already exists and is used by spell VFX implementations. The
shared cue work should register or wrap existing templates where possible
instead of inventing replacement classes first.

## Ground Slam Example

Ability row remains melee:

```json
{
  "ability_id": "WARRIOR_GROUND_SLAM",
  "class_id": "WARRIOR",
  "action_id": "GROUND_SLAM",
  "display_name": "Ground Slam",
  "resource_kind": "RAGE",
  "resource_cost": 35.0,
  "ability_tags": ["LOADOUT_ACTION"],
  "sort_order": 65,
  "gameplay": {
    "kind": "MELEE",
    "base_damage": 42,
    "applies_stagger": true,
    "range": 3.0,
    "melee_impact_area": {
      "radius": 2.75,
      "damage_multiplier": 0.65,
      "hit_index": 0,
      "include_primary_target": false
    },
    "cooldown_ms": 1400,
    "uses_global_cooldown": true,
    "parry_behavior": "UNPARRYABLE",
    "block_behavior": "BLOCKABLE",
    "airborne_targeting_mode": "GROUNDED_TARGET_ONLY"
  }
}
```

VFX cue adds the shockwave:

```json
{
  "owner_kind": "ABILITY",
  "owner_id": "WARRIOR_GROUND_SLAM",
  "trigger": "MELEE_IMPACT",
  "hit_index": 0,
  "anchor": "GROUND_UNDER_TARGET",
  "vfx_id": "VFX_GROUND_SLAM_SHOCKWAVE_01",
  "scale": 1.35,
  "duration_ms": 650,
  "sort_order": 10
}
```

If the slam should be AoE gameplay, author that separately as gameplay data. The VFX cue should present the AoE, not define it.

For targeted melee attacks with AoE damage around the target, keep the ability
as `gameplay.kind: "MELEE"` and add melee gameplay authoring for the area impact.
Do not add fake spell gameplay and do not place radius or damage on
`combat_vfx_cues[]`.

Current V1 gameplay authoring uses `melee_impact_area` on the melee ability.
When the primary melee impact is confirmed, the server applies secondary direct
damage around the impact target position through the normal `EffectPacket`
pipeline. By default the primary target is excluded from the secondary area
damage because it already receives the primary melee hit.

## Validation

Server-side catalog validation should check:

- `owner_kind` is supported.
- `owner_id` resolves to the correct catalog object for its owner kind.
- `trigger` is supported.
- `anchor` is supported.
- `vfx_id` is non-empty and normalized.
- `hit_index` is only used with melee hit triggers.
- `hit_index` requires the cue owner to resolve to a melee strike, either
  directly through `owner_kind: "MELEE_STRIKE"` or through a melee ability, and
  must be within that strike's `hit_windows` length.
- `attach_mode` is supported by the selected `vfx_id`.
- Current runtime `FOLLOW_ANCHOR` cues use `CASTER` or `TARGET`; socket anchors
  need dispatcher support before they can follow.
- blade anchors are only used for combat profiles or weapon visuals that define them.
- `start_delay_ms` is non-negative when present.
- `scale` is positive when present.
- `duration_ms` is non-negative when present.

Client/editor validation should check:

- `vfx_id` resolves to a registered Unity VFX template.
- hand and weapon anchors are valid for the relevant character rig or combat profile.
- attached weapon trail cues have a finite duration unless their template self-terminates safely.
- weapon trail templates fit the authored combat profile or weapon presentation.
- strike-scoped melee cues reference authored strike ids, not runtime slot ids.
- common impact cues are pooled or otherwise verified not to create avoidable GC spikes during repeated combat use.

## Migration Plan

Phase 1: Reconcile and harden the landed data model.

- Keep `combat_vfx_cues[]` as the chosen top-level home.
- Keep `SpellEvent` as the V1 transport for melee and spell presentation facts.
- Update graph validation to check `hit_index` against melee strike hit windows.
- Add accepted melee `ability_id` to authoritative melee presentation facts.
- Decide whether progression remains the home for `MELEE_STRIKE` cues or whether strike-scoped cues migrate into Unity `CombatAnimationSet` export later.
- Document the current live cue as implemented, not hypothetical.

Phase 2: Generalize the client cue resolver.

- Extend `CombatVFXDispatcher` beyond the current event/anchor subset as new trigger and socket needs appear.
- Keep the current neutral `CombatVfxFact` path for existing spell and melee event inserts.
- Resolve ability-scoped and strike-scoped cues additively from authoritative event identity.
- Add `FOLLOW_ANCHOR` lifecycle support for weapon, blade, and hand anchors.
- Keep `SpellVFXDispatcher` behavior as fallback during migration.

Phase 3: Move one melee VFX through the new path.

- First live ability-scoped cue uses the already registered `VFX_FIRE_AREA_BURST_01_ARENA` template for `WARRIOR_EARTHSHATTER`.
- `WARRIOR_EARTHSHATTER` also authors `melee_impact_area` so the confirmed impact applies secondary AoE damage around the target without becoming a spell.
- Register or implement `VFX_GROUND_SLAM_SHOCKWAVE_01` when a distinct ground-slam template is needed.
- Verify cast, hit, block, miss, and replay behavior.

Phase 4: Migrate spell VFX to shared cues where useful.

- Add cue rows for existing spells.
- Convert hardcoded spell-kind switches into template lookups.
- Keep bespoke classes for complex visual behavior, but select them by `vfx_id`.

## Anti-Patterns To Avoid

- Do not create fake spell abilities only to get a VFX.
- Do not make melee abilities call spell reducers for presentation.
- Do not encode VFX identity into melee strike ids.
- Do not put melee VFX selection in `SpellVFXDispatcher`; keep that class as spell fallback until it is retired or turned into an adapter.
- Do not put gameplay semantics such as AoE radius or damage into VFX-only rows.
- Do not hand-edit `melee_manifest.shared.json` to carry player-facing VFX intent.

## Closed Decisions

- Ability-scoped cues live in the top-level `combat_vfx_cues[]` array. This keeps cues uniformly indexable and avoids expanding every ability row with presentation-only subdocuments.
- V1 reuses `SpellEvent` as the transport for melee presentation facts. A neutral presentation event can replace it later, but the current implementation already consumes `SpellEvent`.
- VFX templates are registered through a `CombatVFXRegistry` ScriptableObject loaded from `Resources/CombatVFX/CombatVFXRegistry`.
- Cue matching is additive across scopes. Ability-specific identity and strike-specific animation VFX can both play from the same authoritative fact.
- For block and parry triggers, owner resolution refers to the attacker's source action unless a future defender-reaction owner kind is added.
- V1 carries accepted melee ability identity on `SpellEvent.ability_id`; a neutral combat presentation event can replace the overloaded `SpellEvent` table later.

## Open Decisions

- Should strike-scoped cues remain authored in progression as `owner_kind: "MELEE_STRIKE"`, or should they move into Unity `CombatAnimationSet` and export later?
- Should impact cues fire only on confirmed hits, or should separate triggers exist for attempted impact, hit, block, parry, and miss?
- Should high-frequency cue templates use an explicit pool API, or is prefab-level pooling sufficient through a shared registry wrapper?

## Acceptance

- A melee ability can play a ground slam shockwave without being represented as a spell.
- Spell VFX implementations can be reused by melee through neutral `vfx_id` templates.
- Gameplay data remains in melee/spell gameplay rows, not in VFX cue rows.
- The client resolves visuals through authored cues instead of hardcoded action-id checks.
- Existing melee animation, melee hit timing, gap closers, and spell behavior continue to work unchanged.
- Common impact cues do not allocate or destroy enough per spawn to cause visible frame or GC spikes in repeated combat.
