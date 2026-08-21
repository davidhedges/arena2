# Spellcasting Preemptive Refactor Plan - 2026-05-07

> Historical plan. The cast-animation ownership described here was superseded by the completed
> 2026-08-22 semantic motion cutover. Use `docs/spell-cast-animation-stitching-2026-07-09.md` for
> the live spell animation contract; `CombatAnimationSet.spells[]` no longer exists.

## Purpose

This plan defines the production target for improving spell authoring before adding a larger set of projectile, area, beam, instant-cast, laser, channel, and publisher-sourced VFX spells.

The current spellcasting system is partly a port from an older game. Some newer work is sound: player-facing spell actions now live in `abilities[]`, spell gameplay is derived from `gameplay.kind: "SPELL"`, animation entries live in `CombatAnimationSet.spells[]`, and authoritative presentation facts flow through `CombatEvent`. The weak parts are the older spell-specific runtime and VFX branches around that newer foundation.

This plan deliberately refactors those weak parts before expanding content. The goal is not to make Fireball work with another prefab. The goal is a durable spell authoring contract that can support many visual packs without adding bespoke code paths.

## Non-Negotiable End State

- Player-facing spell gameplay remains authored on `abilities[]` rows in `server/src/progression_catalog.shared.json`.
- Spell animation remains authored per combat profile in `Assets/Arena/Resources/CombatAnimationSets/*.asset`.
- Spell VFX selection is authored through neutral combat VFX cues, not hardcoded spell ids.
- The server remains authoritative for gameplay timing, cast acceptance, release, projectile travel, impact, block, parry, fizzle, and status effects.
- The client may present local anticipation only through an explicit prediction path. Authoritative VFX must be driven by authoritative combat facts.
- There must be one spell presentation path, not separate legacy, alternate, or publisher-specific routes.
- Adding a normal projectile spell must not require Rust enum edits or C# spell-id switches.

## Coverage Of The Requested Authoring Problem

The first production target is projectile spell authoring. A projectile spell must be authorable with all of these pieces:

| Requirement | Authoring Home | Runtime Owner |
| --- | --- | --- |
| Spell gameplay, targeting, damage, cooldown, cast time, mobility, projectile speed/radius/range | `abilities[].gameplay` in `server/src/progression_catalog.shared.json` | Server spell casting and projectile simulation |
| Cast animation | `CombatAnimationSet.spells[]`, keyed by runtime spell/action id | `PlayerAnimator` through `CombatAnimationRequest` |
| One or more cast animation VFX | `combat_vfx_cues[]` rows with `trigger: "SPELL_CAST"` | `CombatVFXDispatcher` |
| Cast VFX source such as left hand, right hand, caster root, ground under caster, weapon socket | cue `anchor` field | shared anchor resolver |
| Projectile body VFX | `combat_vfx_cues[]` row with `trigger: "SPELL_RELEASE"` and `vfx_role: "PROJECTILE_BODY"` | unified projectile VFX presentation |
| Hit/block/parry/fizzle VFX | `combat_vfx_cues[]` terminal trigger rows | `CombatVFXDispatcher` |
| VFX prefab or scripted visual from any publisher | `CombatVFXRegistry` entry keyed by stable `vfx_id` | VFX template runtime |

The important decision is that these are not separate systems. They are one spell action instance with one authoritative event stream. Gameplay, animation, and VFX each have a distinct authoring home, but they are joined by stable action identity and combat event phases.

Concrete projectile authoring shape:

```text
Ability row
  action_id = FIREBALL
  gameplay.kind = SPELL
  gameplay.delivery.kind = PROJECTILE

CombatAnimationSet spell entry
  spellId = FIREBALL
  ground/air clips
  playback policy
  release timing

combat_vfx_cues
  ABILITY:WARRIOR_FIREBALL + SPELL_CAST + LEFT_HAND + VFX_FIRE_CAST_HAND_01
  ABILITY:WARRIOR_FIREBALL + SPELL_CAST + GROUND_UNDER_CASTER + VFX_FIRE_CAST_AURA_01
  ABILITY:WARRIOR_FIREBALL + SPELL_RELEASE + PROJECTILE_BODY + VFX_FIREBALL_PROJECTILE_01
  ABILITY:WARRIOR_FIREBALL + SPELL_IMPACT + IMPACT_POINT + VFX_FIREBALL_HIT_01
```

This directly supports publisher VFX variety because imported prefabs are normalized into registry templates. Spell code never needs to know which publisher a prefab came from.

## Current System Context

### Good Foundations To Keep

`server/src/progression_catalog.shared.json` is already the right home for player-facing spell gameplay. Runtime spell definitions derive from ability rows where:

```json
{
  "gameplay": {
    "kind": "SPELL",
    "delivery": { "kind": "PROJECTILE" }
  }
}
```

`Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs` already contains `WeaponSpellAnimationEntry[] spells`. These entries correctly own profile-specific spell clips, playback layer policy, combat stance policy, lower-body unlock timing, and visual interruption timing.

`Assets/Arena/Runtime/Presentation/CombatAnimationRequestTranslator.cs` and `PlayerAnimator.RequestCombatAnimation(...)` already route authoritative `COMBAT_CAST` events into the shared combat animation request path.

`server/src/progression.rs` already has a partially implemented `combat_vfx_cues[]` catalog and `combat_vfx_cue_catalog` public table. `Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs` already consumes those rows for a narrow V1 subset.

These pieces should be strengthened, not replaced.

### Weak Parts To Refactor

`Assets/Arena/Runtime/Presentation/SpellVFXDispatcher.cs` hardcodes spell ids such as `FIREBALL`, `ICICLE`, `FROST_NOVA`, `INSTANT_BEAM`, `ELECTROCUTE`, and `NEGATE` into C# VFX constructors. This is the wrong extension point for third-party VFX packs. It is also the main source of future bespoke visual drift.

`Assets/Arena/Runtime/Presentation/CombatProjectileVFXDispatcher.cs` handles weapon projectile visuals through `CombatEvent.ProjectileId`, while spell projectiles currently use spell-kind based VFX through `SpellVFXDispatcher`. Gameplay source can differ, but projectile visual playback should be one presentation pipeline.

`server/src/spells/casting.rs` currently inserts spell projectiles with an empty `ActiveCombatProjectile.projectile_id`. That prevents data-authored spell projectile visuals from resolving through the registry.

`WeaponSpellAnimationEntry.groundEffectTime` and `airEffectTime` exist but are not consumed by runtime playback. That means spell release timing is currently not a reliable authored concept.

Cast-time spells use `ActiveCast` as gameplay state, but `begin_active_cast(...)` does not emit a clean authoritative cast-start combat event. Presentation should not infer cast animation or cast VFX from table presence side effects.

The `BespokeRuntimeSpell` budget is useful as a guardrail, but it also shows where the ported system still leans on special runtime identities. New normal spells must use generic delivery code.

## Core Design

Spell casting should become a generic action instance pipeline with explicit presentation phases.

```text
CastRequest
  -> validate ability, target, resource, cooldown, mobility
  -> create one stable action_instance_id
  -> emit SPELL_CAST when the cast is accepted
  -> optional ActiveCast while cast time or channel is active
  -> emit SPELL_RELEASE when gameplay leaves the caster
  -> optional COMBAT_UPDATE rows while projectile, beam, area, or channel is active
  -> emit SPELL_IMPACT, SPELL_BLOCK, SPELL_PARRY, or SPELL_FIZZLE
```

The action instance id must remain stable from cast acceptance through release, projectile travel, and terminal event. Animation, cast VFX, projectile body VFX, beam body VFX, impact VFX, fizzle VFX, and replay suppression all bind to that same identity.

Current code note: `CombatEvent.action_instance_id` is already an instance id for many spell events because `next_spell_id(...)` returns a timestamp/caster/counter id. `CombatEvent.action_kind` carries the spell kind. The confusing part is local naming: many functions call the instance id `spell_id`. The refactor should preserve the instance-id meaning, rename locals to `spell_instance_id`, and audit any client/server consumer that treats `ActionInstanceId` as an action kind.

### Identity Glossary

These names must stay distinct in code, data, docs, and editor UI:

- `action_instance_id`, `cast_id`, and `spell_instance_id` identify one accepted runtime spell occurrence.
- `projectile_instance_id` identifies one projectile spawned by that spell occurrence. For single-projectile spells it may equal the action instance id during migration, but the production model should treat it as a child id derived from the parent action instance id, such as `{action_instance_id}:p0`.
- `action_kind`, `kind`, and `spell_kind` identify the runtime spell behavior, such as `FIREBALL`.
- `ability_id` identifies the player-facing authored ability row that requested the spell, such as `WARRIOR_FIREBALL`.
- `vfx_id` and `projectile_id` identify presentation templates, not gameplay behavior. For spell projectiles, `ActiveCombatProjectile.projectile_id` should be copied from the selected `PROJECTILE_BODY` cue's `vfx_id`.

Ambiguous names such as local `spell_id` are not acceptable in code that handles runtime occurrences. If a value is an instance id, name it `spell_instance_id` or `action_instance_id`. If it is a spell kind, name it `spell_kind` or `action_kind`.

The real current gap is cast lifecycle continuity. Cast-time and bespoke paths can create one id for cast start and another id for release/runtime effects. Phase 1 must move instance id allocation to accepted cast start and thread that same id through `ActiveCast`, release, projectile rows, updates, and terminal events.

Cue trigger vocabulary is presentation vocabulary. Server `CombatEvent.event_type` can continue using `COMBAT_CAST`, `COMBAT_IMPACT`, etc. The cue resolver maps source plus event type into cue triggers such as `SPELL_CAST` or `SPELL_IMPACT`. Add a real `COMBAT_RELEASE` event type for spell release; map `source_kind: "SPELL" + COMBAT_RELEASE` to `SPELL_RELEASE`.

## Authoring Model

### Source-Of-Truth Boundary

Do not collapse gameplay, animation, and VFX into one giant spell object. That would make authoring look convenient at first, but it would blur ownership and recreate drift.

Use this boundary instead:

- `abilities[].gameplay` answers: what does the spell do?
- `CombatAnimationSet.spells[]` answers: how does this combat profile animate the cast?
- `combat_vfx_cues[]` answers: what visual templates play on each authoritative phase?
- `CombatVFXRegistry` answers: what Unity prefab or scripted template does this stable `vfx_id` instantiate?

The baking work is the validation and runtime join between those sources. It is not a license to duplicate fields across all of them.

### Finished Developer Experience

When this refactor is complete, adding a normal projectile spell should feel like authoring data plus registering visual templates, not changing spell runtime code.

Expected workflow:

1. Add or update the player-facing ability in `server/src/progression_catalog.shared.json`.
   - Set `ability_id`, `class_id`, and `action_id`.
   - Configure gameplay under `gameplay`: cast time, cooldown, resource cost, targeting, damage, projectile speed, range, radius, block/parry behavior, and projectile sequence pattern.
   - Do not add prefab names, hand sockets, hit VFX, cast glows, or publisher-specific data here.
2. Add or update the spell animation entry in `CombatAnimationSet.spells[]` for each exposed combat profile.
   - Key the entry by the runtime `action_id`/spell kind.
   - Select ground and air clips.
   - Configure playback policy, combat stance policy, lower-body unlock, interrupt windows, and release timing.
3. Register VFX templates in `CombatVFXRegistry`.
   - Each publisher prefab or scripted visual gets a stable `vfx_id`.
   - Prefabs are normalized and stripped of gameplay/demo behavior before registration.
4. Add `combat_vfx_cues[]` rows.
   - `SPELL_CAST` rows define hand glows, ground auras, charge effects, and other cast-time visuals.
   - `SPELL_RELEASE` + `PROJECTILE_BODY` rows define projectile visuals by `projectile_sequence_index`.
   - `SPELL_IMPACT`, `SPELL_BLOCK`, `SPELL_PARRY`, and `SPELL_FIZZLE` rows define terminal visuals.
   - Anchors live on the cue rows, not in `PlayerAnimator` or spell runtime switches.
5. Run validation.
   - The resolved presentation manifest should show which cues were selected, which came from ability overrides, which came from spell fallbacks, and whether every projectile sequence has exactly one body template.

The common authoring question should become: "which source of truth owns this field?"

- Gameplay numbers and targeting belong in `progression_catalog.shared.json`.
- Animation clips and release-frame metadata belong in `CombatAnimationSet`.
- VFX choices, anchors, phase triggers, and projectile visual sequence mapping belong in `combat_vfx_cues[]`.
- Prefab/scripted visual implementation belongs in `CombatVFXRegistry` templates.
- Runtime code should only change when introducing a new delivery mechanic, lifecycle type, anchor kind, or template capability.

### Gameplay

Gameplay remains under `abilities[].gameplay`.

Example projectile gameplay:

```json
{
  "ability_id": "WARRIOR_FIREBALL",
  "class_id": "WARRIOR",
  "action_id": "FIREBALL",
  "gameplay": {
    "kind": "SPELL",
    "cooldown_ms": 450,
    "uses_global_cooldown": true,
    "cast_time_ms": 0,
    "cast_mobility": "MOBILE",
    "targeting": "TARGET",
    "requires_target": true,
    "resource_cost": 0.0,
    "arms_auto_attack_on_cast": true,
    "delivery": {
      "kind": "PROJECTILE",
      "speed": 18.0,
      "max_distance": 30.0,
      "damage": 30,
      "spawn_forward": 1.0,
      "spawn_height": 1.2,
      "turn_rate": 3.0,
      "update_interval_seconds": 0.05,
      "radius": 0.8,
      "block_behavior": "BLOCKABLE",
      "parry_behavior": "PARRYABLE",
      "homing_window_seconds": 0.15,
      "impact_effects": []
    }
  }
}
```

Gameplay data must not contain prefab references, hand socket choices, cast aura choices, or hit VFX choices.

### Animation

Cast animation remains in `CombatAnimationSet.spells[]`, keyed by runtime spell/action id.

The existing `groundEffectTime` and `airEffectTime` fields should be repurposed or replaced by explicit release timing:

```csharp
public float groundReleaseTime;
public float airReleaseTime;
```

If serialized compatibility is worth preserving, the existing fields can be renamed in the inspector first while keeping their serialized names. The important contract is:

- cast animation starts on `SPELL_CAST`
- `SPELL_RELEASE` is the authoritative gameplay release fact and is emitted by the server from gameplay timing
- release timing in `CombatAnimationSet` is presentation/prediction metadata, not server authority
- server cast-time spells use `gameplay.cast_time_ms`; the server does not read Unity ScriptableObjects
- animation clips should be authored so the visible release frame matches `gameplay.cast_time_ms`
- editor validation should compare exposed spell `cast_time_ms` against the combat profile's authored release timing and fail when the mismatch is greater than 50ms unless an explicit tolerance override is introduced later
- local prediction may use authored release timing for anticipation, but authoritative release remains server truth

### VFX

VFX selection moves to `combat_vfx_cues[]`.

Example:

```json
{
  "owner_kind": "ABILITY",
  "owner_id": "WARRIOR_FIREBALL",
  "trigger": "SPELL_CAST",
  "anchor": "LEFT_HAND",
  "attach_mode": "FOLLOW_ANCHOR",
  "vfx_id": "VFX_FIRE_CAST_HAND_01",
  "duration_ms": 450,
  "sort_order": 10
}
```

```json
{
  "owner_kind": "ABILITY",
  "owner_id": "WARRIOR_FIREBALL",
  "trigger": "SPELL_CAST",
  "anchor": "GROUND_UNDER_CASTER",
  "attach_mode": "SPAWN_WORLD",
  "vfx_id": "VFX_FIRE_CAST_GROUND_AURA_01",
  "duration_ms": 550,
  "sort_order": 20
}
```

```json
{
  "owner_kind": "ABILITY",
  "owner_id": "WARRIOR_FIREBALL",
  "trigger": "SPELL_RELEASE",
  "anchor": "LEFT_HAND",
  "attach_mode": "SPAWN_WORLD",
  "vfx_role": "PROJECTILE_BODY",
  "vfx_id": "VFX_FIREBALL_PROJECTILE_01",
  "sort_order": 30
}
```

```json
{
  "owner_kind": "ABILITY",
  "owner_id": "WARRIOR_FIREBALL",
  "trigger": "SPELL_IMPACT",
  "anchor": "IMPACT_POINT",
  "attach_mode": "SPAWN_WORLD",
  "vfx_id": "VFX_FIREBALL_HIT_01",
  "duration_ms": 1200,
  "sort_order": 40
}
```

Multiple cast VFX are modeled as multiple cue rows. There is no special field for "the hand glow" or "the ground aura"; those are ordinary cue rows with different anchors and timing.

`sort_order` is a deterministic spawn/render ordering hint for cues that share the same owner and trigger. Lower values spawn first; higher values spawn later.

## Required Schema Extensions

Extend `CombatVfxCueDefinition` and `CombatVfxCueCatalog` with:

```text
vfx_role: ONE_SHOT | ATTACHED | PROJECTILE_BODY
lifecycle: DURATION | UNTIL_TERMINAL_EVENT
projectile_sequence_index: optional uint
```

Recommended defaults:

- `vfx_role` defaults to `ONE_SHOT` for `SPAWN_WORLD`
- `vfx_role` defaults to `ATTACHED` for `FOLLOW_ANCHOR`
- `lifecycle` defaults to `DURATION` when `duration_ms > 0`
- projectile body cues require `PROJECTILE_BODY`
- projectile body cues default to `UNTIL_TERMINAL_EVENT`

For `vfx_role: "PROJECTILE_BODY"`:

- `anchor` is the spawn-origin anchor at release time, not the follow target
- `attach_mode` must be omitted or `SPAWN_WORLD`
- `FOLLOW_ANCHOR` is invalid because the projectile follows projectile runtime state, not the caster hand
- `projectile_sequence_index` selects which projectile emission this body cue represents; omitted means sequence `0`
- after spawn, the projectile template binds to the public projectile runtime row by `projectile_instance_id`
- V1 projectile delivery intentionally supports one emitted gameplay projectile, sequence `0`, and must have exactly one selected body cue for that sequence
- multi-projectile authoring is deferred, but the schema and runtime identity model are already sequence-aware; do not remove `projectile_sequence_index`, do not reuse `sort_order` as projectile identity, and do not let duplicated body cues imply multiple projectiles
- when multi-projectile delivery is implemented, every authored gameplay projectile sequence must have exactly one selected body cue and its own `projectile_instance_id`

Do not add `BEAM_BODY`, `UNTIL_RELEASE`, or `UNTIL_CAST_END` in the projectile migration unless the same phase also migrates beams/channels. Add those values when beam or channel templates are implemented and exercised by content.

Extend supported triggers:

```text
SPELL_CAST
SPELL_RELEASE
SPELL_IMPACT
SPELL_BLOCK
SPELL_PARRY
SPELL_FIZZLE
```

Existing melee triggers remain unchanged.

`COMBAT_UPDATE` can still be consumed by projectile helpers for correction and terminal behavior, but it does not need to become a cue trigger in the projectile migration. Add `SPELL_UPDATE` as a cue trigger later only when area, beam, or channel content needs repeated authored cue evaluation.

`CombatEvent.action_kind` remains the runtime spell/action id such as `FIREBALL`. `CombatEvent.ability_id` should carry the accepted player-facing ability id when the spell came from an ability, such as `WARRIOR_FIREBALL`. Cue matching should use `ability_id` for `owner_kind: "ABILITY"` and `action_kind` for `owner_kind: "SPELL"`.

Cue resolution is additive across distinct visual slots. Ability-scoped cues override spell-scoped fallback cues only for the same resolved slot:

- for `PROJECTILE_BODY`, the slot key is trigger, role, hit index, and `projectile_sequence_index`; anchor is spawn-origin data and must not become projectile identity
- for attached and one-shot cues, the slot key also includes anchor and attach mode, so overriding a left-hand cast cue does not remove a right-hand spell fallback or ground cue
- spell-scoped cues are the fallback for shared visuals when no matching ability-scoped cue exists
- after override resolution there must be exactly one selected projectile body cue for each gameplay projectile sequence on a player-facing projectile spell

### Resolved Presentation Manifest

Do not make hot casting code scan raw cue rows. Build a resolved presentation manifest during catalog validation/load.

For each exposed spell ability, the manifest should contain:

- the accepted `ability_id`
- the runtime `action_kind`
- ordered cast cues
- ordered release cues
- selected projectile body template per `projectile_sequence_index`
- ordered terminal cues by terminal trigger
- validation diagnostics explaining whether each selected cue came from an ability override or spell fallback

Server casting can then ask the manifest for the selected projectile visual template when inserting `ActiveCombatProjectile`. Client presentation can use the same resolution rules through generated catalog data. This keeps cue selection deterministic and makes authoring failures visible before combat begins.

## Runtime Architecture

### Server

Refactor `server/src/spells/casting.rs` so cast acceptance creates one stable spell action instance id.

For instant spells:

```text
validate
pay cost
stamp GCD/cooldown
emit SPELL_CAST
emit SPELL_RELEASE
execute delivery
```

For cast-time spells:

```text
validate
pay cost
stamp GCD
emit SPELL_CAST
insert ActiveCast with action_instance_id
finish_active_cast
  -> emit SPELL_RELEASE
  -> execute delivery
  -> stamp cooldown if policy requires completion
```

For projectile spells:

- `ActiveCombatProjectile.projectile_id` must be populated from the selected `PROJECTILE_BODY` cue's `vfx_id`.
- `ActiveCombatProjectile.projectile_instance_id` must identify the individual projectile body.
- V1 single-projectile spells derive `projectile_instance_id` as `{action_instance_id}:p0`; this is a sequence-indexed child id, not permission to collapse projectile identity back into action identity.
- multi-projectile support is deferred. When implemented, it must derive one id per emitted projectile sequence, such as `{action_instance_id}:p1`, and must insert/update/terminate each projectile body independently.
- projectile update and terminal events keep the same parent `action_instance_id` and include the relevant `projectile_instance_id` when they refer to a specific projectile body.
- impact effects still queue through the existing `EffectPacket` pipeline
- ongoing projectile presentation must bind to authoritative projectile runtime state, not only transient update events

Current code note: `ActiveCombatProjectile` is server-private today. Its current fields are presentation-safe candidates: ids, source/action kind, ability id, caster/target, origin, position, direction, speed, range, radius, timing, damage, defense behavior, hit index, and created time. Phase 2 should make `active_combat_projectile` public after a field audit instead of introducing a duplicate projection table. Do not implement projectile body movement from `COMBAT_UPDATE` events alone. Updates can correct or terminate presentation, but late join and reconnect require reconstructable ongoing projectile state.

The selected projectile body visual id comes from cue authoring, not gameplay delivery. The server should resolve it from the precomputed presentation manifest when inserting `ActiveCombatProjectile`:

```text
ability id from accepted spell action
  + projectile_sequence_index
  -> selected PROJECTILE_BODY cue from the resolved presentation manifest
  -> copy cue.vfx_id into ActiveCombatProjectile.projectile_id
```

This keeps VFX selection in `combat_vfx_cues[]` while still giving projectile presentation a stable id on the authoritative runtime row.

The server should not know prefab paths or Unity asset names. It only publishes stable ids and authoritative event timing.

### Client

`CombatVFXDispatcher` becomes the visual owner for all combat VFX cues.

It should:

- consume `CombatEvent` rows
- build a neutral combat VFX fact
- resolve ability-scoped and spell-scoped cues
- resolve anchors through a dedicated `CombatVFXAnchorResolver` sibling component/service using `PlayerEntity`, humanoid Animator bones, and weapon attachments
- instantiate registered templates
- maintain lifecycle for attached, projectile, and one-shot cues

Keep `CombatVFXDispatcher` as orchestration, not as a new monolith. Split implementation into explicit collaborators:

- `CombatVFXCueResolver`: maps combat facts to resolved cue rows and emits diagnostics for ability override versus spell fallback decisions.
- `CombatVFXAnchorResolver`: resolves authored anchors to transforms or world poses.
- `CombatVFXTemplateRegistry`: resolves `vfx_id` to prefab or scripted template.
- `CombatVFXLifecycleRegistry`: tracks live one-shot, attached, and projectile body instances by action and projectile identity.
- `CombatProjectileVisualController`: follows public projectile runtime state for one projectile body.

LLM and human maintainers should be able to answer "where is cue selection?", "where is anchor resolution?", "where is projectile following?", and "where is prefab spawning?" by opening one named class per responsibility.

`CombatProjectileVFXDispatcher` should be folded into this path or reduced to an internal helper used by `CombatVFXDispatcher` for `vfx_role: PROJECTILE_BODY`.

`SpellVFXDispatcher` should be retired after migration. Until then it may remain only as a compatibility fallback for spells without authored cue rows. New spell work must not add branches to it.

Cutover rule: after a spell has authored cue parity and validation coverage, remove its `SpellVFXDispatcher` branch in the same change. Do not leave a permanent runtime fallback. Rollback is a source-control revert or forward fix, not a second production path.

## VFX Template Registry

`Assets/Arena/Runtime/Presentation/VFX/CombatVFXRegistry.cs` should evolve from prefab-only lookup to template lookup.

Template kinds:

- `PrefabOneShotTemplate`
- `PrefabAttachedTemplate`
- `ProjectileTemplate`
- `ProceduralTemplate`

Add `BeamTemplate` with the beam/channel migration, not during the projectile-only schema pass.

The registry id remains the stable authoring key:

```text
VFX_FIREBALL_PROJECTILE_01
VFX_FIRE_CAST_HAND_01
VFX_FIREBALL_HIT_01
VFX_LIGHTNING_BEAM_01
```

Third-party publisher prefabs should usually enter as prefab templates. Complex custom visuals can use a scripted template, but selection still happens by `vfx_id`, not by spell id.

Scripted/procedural templates must share one input contract, not custom constructors per spell:

```csharp
public readonly struct CombatVfxTemplateContext
{
    public string CueKey;
    public string ActionInstanceId;
    public string ActionKind;
    public string AbilityId;
    public string Trigger;
    public Transform? CasterAnchor;
    public Transform? TargetAnchor;
    public Vector3 Origin;
    public Vector3 Direction;
    public Vector3 Point;
    public float Speed;
    public float MaxDistance;
    public float ScalarValue;
    public uint SequenceIndex;
    public uint SequenceCount;
}
```

Templates can ignore fields they do not need. They must not require spell-specific constructor signatures.

Pooling is deferred for the first migration, but templates must be written so pooling can be added later. Do not rely on one-time `Awake` scene side effects, global mutable setup, or self-destroy-only lifecycles that prevent reset and reuse.

Publisher VFX intake rules:

- imported prefabs must be wrapped or registered as visual-only templates
- gameplay colliders, rigidbodies, damage scripts, demo shooter scripts, and publisher sample controllers must not be active in runtime spell prefabs
- prefab local forward/up conventions should be normalized in the template, not patched per spell
- lifetime should be controlled by the cue/template lifecycle, not by arbitrary demo scripts
- scale and offsets should be cue/template data, not hidden scene edits

## Anchor Contract

Supported anchors should be resolved uniformly:

```text
CASTER
TARGET
IMPACT_POINT
GROUND_UNDER_CASTER
GROUND_UNDER_TARGET
LEFT_HAND
RIGHT_HAND
WEAPON_MAIN_HAND
WEAPON_OFF_HAND
WEAPON_BLADE_START
WEAPON_BLADE_END
```

Remove hardcoded spell socket methods from `PlayerAnimator`, such as spell-id based launch socket selection. The cue row owns the anchor. `PlayerAnimator` should not grow a new general `ResolveAnchor(...)` API. Anchor resolution belongs in `CombatVFXAnchorResolver`, which can read presentation roots, humanoid bones, and weapon mount components without taking over animation orchestration.

For hand anchors, resolve humanoid bones from the caster's Animator.

For weapon anchors, resolve through the existing weapon attachment/mount model.

For ground anchors, project against the current combat/world terrain helper when available; otherwise use the event point plus a small ground offset.

## Projectile Spell Migration

Projectile spells are the first implementation target.

Required behavior:

1. Pressing a projectile spell ability sends the normal `CastRequest`.
2. Server validates the spell through the existing ability/loadout/spell path.
3. Server emits `SPELL_CAST` with the stable action instance id.
4. Client starts the authored cast animation through the existing `CombatAnimationRequest` path.
5. Client spawns any `SPELL_CAST` cues, such as hand glow and ground aura.
6. Server emits `SPELL_RELEASE` at the actual release time.
7. Server inserts one or more `ActiveCombatProjectile` rows, each with a `projectile_instance_id`, `projectile_sequence_index`, and selected visual `projectile_id`.
8. Client spawns the matching `PROJECTILE_BODY` cue for each projectile sequence and binds it to authoritative projectile runtime state by `projectile_instance_id`.
9. Server emits terminal event: `SPELL_IMPACT`, `SPELL_BLOCK`, `SPELL_PARRY`, or `SPELL_FIZZLE`, including `projectile_instance_id` when the terminal event belongs to one projectile body.
10. Client terminates the matching projectile body and spawns matching terminal cues.

This replaces the current split where `SpellVFXDispatcher` owns spell projectiles and `CombatProjectileVFXDispatcher` owns weapon projectiles.

Projectile migration V1 is not complete until all of these are true:

- projectile body VFX selection is no longer derived from `row.ActionKind`
- spell projectiles populate a visual/template id instead of leaving `ActiveCombatProjectile.projectile_id` empty
- weapon projectile and spell projectile visuals use the same projectile presentation helper
- ongoing projectile visuals can be reconstructed after reconnect or late join from a public projectile runtime row
- impact VFX is selected by terminal cue rows, not by projectile class code
- cast VFX can include multiple simultaneous attached and world cues
- left-hand and right-hand launch anchors are cue data, not hardcoded spell-id switches

Deferred multi-projectile completion criteria:

- gameplay delivery can author the number/timing/pattern of emitted projectile sequences
- server release code inserts one `ActiveCombatProjectile` row per sequence
- every row has a unique `projectile_instance_id`, a stable parent `action_instance_id`, and the correct `projectile_sequence_index`
- terminal events include the matching `projectile_instance_id`
- late join/reconnect can reconstruct every still-active projectile body independently
- validation requires exactly one selected `PROJECTILE_BODY` cue for every emitted sequence

Projectile cardinality rule: projectile delivery is sequence-indexed even while V1 emits only sequence `0`. If a single projectile visual needs core, trail, sparks, and light, wrap those as children of the projectile template. If a spell needs shotgun bolts, split missiles, chained spawns, or delayed salvos, represent those as explicit gameplay projectile sequences and correlated cue rows; do not sneak multi-projectile behavior into `sort_order` or duplicated body cues without sequence identity.

## Area, Beam, Instant, Laser, Channel Support

The same phase model extends to other delivery shapes.

Area:

- `SPELL_CAST`: anticipation
- `SPELL_RELEASE`: area visual starts at point or target ground
- `SPELL_IMPACT`: damage/status application pulse
- optional `SPELL_UPDATE`: persistent area ticking or warning telegraph

Instant beam or laser:

- `SPELL_CAST`: charge-up or hand glow
- `SPELL_RELEASE`: beam body starts and hit resolves
- `SPELL_IMPACT`: endpoint burst or target reaction VFX
- charged-release spells can keep `ActiveCast` for charge state, but presentation facts still use `SPELL_CAST` and `SPELL_RELEASE`

Channel:

- `SPELL_CAST`: channel starts
- `SPELL_UPDATE`: channel tick or beam refresh
- `SPELL_FIZZLE` or terminal impact: channel stops
- lifecycle can be `UNTIL_CAST_END`

Self buff:

- `SPELL_CAST`: caster animation and aura
- `SPELL_RELEASE`: status application moment
- no projectile body required

Gameplay-side retirement of `BespokeRuntimeSpell` is not part of the projectile VFX authoring refactor. Some bespoke branches still encode real gameplay such as charge cycles, channel ticks, and special area behavior. This plan stops new normal projectile spells from adding bespoke branches and moves visual selection out of bespoke VFX dispatch. Gameplay generalization for existing bespoke area/beam/channel spells should be a follow-up plan.

## Validation Requirements

Server validation should reject:

- loadout-exposed spell abilities without a matching spell animation entry for their class combat profile
- V1 player-facing projectile delivery spells without exactly one selected `PROJECTILE_BODY` release cue for gameplay projectile sequence `0`
- projectile body cues on non-projectile deliveries
- projectile body cues with `FOLLOW_ANCHOR`
- projectile body cues with `start_delay_ms > 0`; body visuals are bound to active projectile runtime rows, not delayed cue spawning
- duplicate selected `PROJECTILE_BODY` cues for the same owner/trigger/projectile sequence after ability override and spell fallback resolution
- projectile body cue sequence indexes other than `0` until multi-projectile gameplay delivery is implemented
- unknown `vfx_id`
- unsupported cue trigger
- unsupported anchor
- `TARGET` anchors on `SPELL_CAST` or `SPELL_RELEASE`; target anchors require a confirmed terminal target
- hand or weapon anchors with `FOLLOW_ANCHOR` when the runtime cannot resolve that anchor for the combat profile
- `duration_ms == 0` for `ONE_SHOT` cues that do not self-terminate
- direct authoring of spell VFX through `SpellVFXDispatcher`

Do not add a production `silent: true` escape hatch. Test-only spells that intentionally have no visuals should either stay out of player-facing loadout validation or use a registered no-op test template such as `TEST_NO_VISUAL_PROJECTILE`.

Unity/editor validation should reject:

- `CombatVFXRegistry` entries whose prefab contains gameplay colliders or rigidbodies
- missing prefab/template references
- duplicate normalized `vfx_id` entries
- prefab registry entries that shadow scripted template ids; a `vfx_id` must resolve through exactly one template path
- hand and weapon anchors that cannot resolve against the runtime avatar, weapon mounts, or blade-marker contract used by exposed combat profiles
- non-projectile prefab templates using `UNTIL_TERMINAL_EVENT`; use scripted templates for terminal-driven lifecycle, or finite `DURATION` for prefabs
- projectile templates without deterministic termination behavior
- attached cues without finite lifecycle or explicit supported lifecycle

Tests should assert:

- adding a simple projectile spell does not require Rust enum changes
- adding a simple projectile spell does not require C# spell-id switch changes
- `SpellVFXDispatcher` has no new spell cases after the migration starts
- authored `SPELL_RELEASE` cues resolve for projectile spells
- terminal projectile events terminate only the body with the matching `projectile_instance_id`
- `CombatVFXAnchorResolver` returns expected hand and weapon anchors across all exposed combat profiles
- existing melee VFX cues still work

Deferred multi-projectile tests should assert:

- multi-projectile spells resolve one projectile body template per gameplay projectile sequence
- sequence indexes without gameplay emitters fail validation
- terminal projectile events terminate only the body with the matching `projectile_instance_id`

## Migration Plan

### Phase 1 - Stabilize Spell Action Instances

- Preserve `CombatEvent.action_instance_id` as the unique instance id.
- Rename misleading local variables such as `spell_id` to `spell_instance_id` where they store `next_spell_id(...)`.
- Rename `next_spell_id(...)` to `next_spell_instance_id(...)`.
- Rename runtime fields that store instance ids but are called `spell_id`, including `ChannelCastRuntime.spell_id`, to `spell_instance_id`.
- Keep `ActiveCast.cast_id` as the instance id and `ActiveCast.kind` as the spell kind; audit call sites to preserve that meaning.
- Add a stable action instance id creation point for accepted spells.
- Store that id on `ActiveCast`.
- Emit `SPELL_CAST` for accepted cast starts.
- Emit `SPELL_RELEASE` when gameplay delivery executes.
- Add `COMBAT_RELEASE` as a real event type and map it to cue trigger `SPELL_RELEASE`.
- Audit every client `switch`/comparison on `CombatEvent.EventType` and add safe handling for `COMBAT_RELEASE`.
- Keep `COMBAT_CAST` as the event type for cast starts; it is not a legacy compatibility path.

### Phase 2 - Expand Combat VFX Cue Schema

- Add `SPELL_RELEASE`, `SPELL_BLOCK`, and `SPELL_PARRY` trigger support.
- Add `vfx_role`.
- Add `projectile_sequence_index` to projectile body cue authoring.
- Add projectile-level correlation to runtime facts: public projectile rows must expose `projectile_instance_id` and `projectile_sequence_index`, and projectile terminal events must include `projectile_instance_id` when they target a specific projectile.
- Add only the lifecycle values needed by projectile migration: `DURATION` and `UNTIL_TERMINAL_EVENT`.
- Build a resolved presentation manifest during catalog validation/load; server casting must consume the resolved manifest rather than scan raw cue rows.
- Make `active_combat_projectile` public after the field audit described above.
- Regenerate SpacetimeDB bindings after schema changes.
- Update graph validation.

### Phase 3 - Generalize Client VFX Runtime

- Add `CombatVFXAnchorResolver` and use it from `CombatVFXDispatcher`.
- Add `CombatVFXCueResolver`, `CombatVFXLifecycleRegistry`, and `CombatProjectileVisualController` so `CombatVFXDispatcher` remains orchestration instead of owning every detail directly.
- Keep procedural templates registered behind a single `CombatVFXTemplateRegistry`; runtime cue spawning must warn once for unresolved template ids instead of silently dropping authored cues.
- Add projectile body lifecycle support.
- Move `WeaponProjectileVFX` behind the cue/template path.

### Phase 4 - Migrate Projectile Spells

- Add animation entries if currently missing for exposed combat profiles.
- Migrate Fireball and Icicle first because they exercise the projectile body path.
- Also migrate one cast-time spell, preferably Meteor, before considering Phase 4 complete so `ActiveCast -> COMBAT_RELEASE -> SPELL_RELEASE` is exercised by real content.
- Author cast, release/projectile body, and impact cues for migrated spells.
- Register the relevant VFX templates in `CombatVFXRegistry`.
- Remove migrated spells' hardcoded branches from `SpellVFXDispatcher`.

### Phase 5 - Retire Legacy SpellVFXDispatcher

- Migrate Frost Nova, Meteor, Instant Beam, Electrocute, Negate, and self buffs to cue/template selection.
- Delete or disable `SpellVFXDispatcher`.
- Keep procedural VFX classes only as templates selected by `vfx_id`.

## Late Join And Replay

Transient cues are not replayed for late joiners. A player who connects after a cast aura or impact burst has already happened should not see that short-lived cue.

Ongoing cues must be reconstructable from authoritative runtime state:

- projectile bodies reconstruct from the public projectile runtime row
- future channel or beam bodies reconstruct from the relevant public channel/beam runtime row
- terminal one-shots are spawned only from terminal events observed live

This is the reason projectile body lifecycle must not depend only on `COMBAT_UPDATE` events.

## Audio Scope

Audio is out of scope for this refactor. The same phase model should eventually drive a parallel `combat_audio_cues[]` path. Do not graft SFX fields onto `combat_vfx_cues[]`; visual and audio templates have different lifecycle and mixing requirements.

## Anti-Patterns

- Do not add new spell ids to `SpellVFXDispatcher`.
- Do not add publisher-specific code paths.
- Do not make projectile gameplay depend on visual prefab components.
- Do not put VFX ids inside gameplay delivery unless the field is explicitly a visual template id.
- Do not use animation events as gameplay release authority.
- Do not infer release timing from clip length when authored release timing exists.
- Do not create fake melee, movement, or spell actions just to reuse a visual.
- Do not let `ActiveCast` table callbacks become the primary presentation path.

## Acceptance Criteria

- A new projectile spell can specify:
  - one cast animation
  - one or more cast-time VFX cues
  - cast VFX anchors such as left hand, right hand, caster root, or ground under caster
  - one projectile body VFX
  - one or more terminal hit/block/parry/fizzle VFX cues
- The spell uses the normal ability loadout, cast request, server validation, combat event, animation request, and combat VFX cue paths.
- No new spell-specific C# dispatch branch is required.
- No new Rust bespoke runtime spell identity is required for a normal projectile spell.
- Existing melee animation, melee VFX cues, movement delivery abilities, status application spells, and combat animation replay behavior remain intact.
- Validation fails loudly when authoring is incomplete.

## Implementation Status - 2026-05-08

This plan is implemented far enough for code review of the architecture and migrated V1 spell path.

Completed:

- stable spell action instance ids are threaded through generic, direct-delivery, and migrated bespoke paths
- `COMBAT_RELEASE` / `SPELL_RELEASE` exists and is used for release-phase cue selection
- `active_combat_projectile` is public presentation state with projectile instance and sequence identity
- spell projectile body VFX resolves from selected `PROJECTILE_BODY` cue data
- Fireball, Icicle, Meteor, Frost Nova, Negate, Instant Beam, and Electrocute are routed through cue/template presentation instead of `SpellVFXDispatcher`
- `SpellVFXDispatcher` and `CombatProjectileVFXDispatcher` are removed
- client VFX dispatch is split into resolver, anchor resolver, lifecycle registry, projectile visual controller, and template registry layers
- editor validation covers VFX registry entries, missing cue templates, scripted-template collisions, selectable spell animation entries, cast release timing, and anchor contracts
- `Arena/Spell Authoring/Open Spell Authoring` provides a first-pass Unity audit/snippet window for existing spell abilities, animation coverage, VFX cue coverage, known VFX template selection, safe missing `CombatAnimationSet` entry creation, and projectile spell JSON snippets
- multi-projectile gameplay is deferred, but projectile sequence identity remains in schema, validation, docs, and runtime child ids

Automated verification run:

- `dotnet build Assembly-CSharp.csproj --no-restore` passed
- `dotnet build Assembly-CSharp-Editor.csproj --no-restore` passed
- `cargo check --manifest-path server/Cargo.toml` passed
- targeted Rust tests for combat authoring graph, combat VFX cue resolution, projectile body selection, migrated dispatcher deletion, and runtime spell-id allowlisting passed
- full `cargo test --manifest-path server/Cargo.toml` passed: 196 passed, 0 failed
- `git diff --check` passed

Remaining before final production signoff:

- run the `Arena/Combat VFX/Validate Authoring` editor menu command and address any asset-level findings it reports
- manually playtest migrated spell visuals in Unity, at minimum Fireball, Icicle, Meteor, Frost Nova, Negate, Instant Beam, and Electrocute
- Unity edit-mode tests are deferred by owner decision for this V1 closeout
- a tested catalog writer remains a post-V1 authoring improvement; until then, the Unity spell authoring window intentionally generates reviewed JSON snippets instead of mutating `progression_catalog.shared.json`
