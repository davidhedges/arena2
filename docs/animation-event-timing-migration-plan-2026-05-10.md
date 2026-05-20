# Animation Event Timing Migration Plan - 2026-05-10

## Purpose

This plan migrates clip-internal presentation timing from `CombatAnimationSet` serialized fields to `AnimationClip` events. Events become the source of truth for in-clip moments — release frame, lower-body unlock, visual interruptible window, hold fade start, strike hit cue, weapon handoff. The `CombatAnimationSet` retains clip references and policy fields (which clip, which layer, which combat-stance behavior) but loses every "when does X happen inside this clip" knob.

This is a presentation-layer migration. Server-authoritative gameplay timing (`cast_time_ms`, strike windows the server uses to schedule damage, projectile release) stays in `progression_catalog.shared.json`. The boundary between what the server reads and what the client reads is unchanged.

The motivation is scale. The roster will grow to thousands of clips. Authoring timing in two places (clip + asset field) creates drift that has already burned us. Magic constants in `PlayerAnimator` (`SpellCastHoldEnterToIdleNormalizedTime`, `SpellCastHoldExitDelaySeconds`, `SpellCastHoldExitCrossFadeDurationSeconds`) describe per-clip behavior with global numbers; events let each clip carry its own timing.

## Context For Reviewers And LLMs

This plan follows the archived cast-time spell animation orchestration plan ([docs/archive/2026-05-stale-plans/cast-time-spell-animation-orchestration-plan-2026-05-08.md](archive/2026-05-stale-plans/cast-time-spell-animation-orchestration-plan-2026-05-08.md)). That plan's orchestration core landed: `SpellCastPresentationController`, `CombatSpellAnimationPhase` (HoldStart/Release/Cancel), `ActiveCast`-driven hold scheduling, `IsCastTimeSpellEvent` gating, local cast-enter prediction, aim/facing propagation, and the cast-hold layer-weight fade-out fix. What did NOT land from that plan is Phase 1 (rename `groundEffectTime`/`airEffectTime` to release timing) and Phase 5 (tests). Phase 1 is now superseded by this plan - the fields aren't renamed, they're deleted entirely.

Animation events in this plan are **timing data**, not Unity `SendMessage` callbacks. The runtime reads events via `AnimationUtility.GetAnimationEvents(clip)` and uses their `time` to schedule its own logic. No Unity-fired `SendMessage` is involved. Callback-style events have known issues (don't fire on interrupted clips, awkward in edit mode, brittle string lookups, hard to test) and we deliberately avoid them.

Authoring events on FBX-embedded clips writes to the third-party `.fbx.meta` file. We extract clips to standalone `.anim` files under `Assets/Arena/Content/Animation/Extracted/` so events live in our tree, survive third-party updates, and produce clean diffs. The `ThirdPartyAnimationExtractor` editor tool handles bulk extraction.

The `CombatAnimationSet` clip references must point at the extracted `.anim` files, not the FBX-embedded sub-assets. Existing references that point at FBX clips need to be repointed when the clip is migrated. This is part of step 3 of the migration.

Hard rules for implementers:

- do not use Unity animation events as `SendMessage` callbacks; treat them as pure timing data
- do not put gameplay-authoritative timing (damage amount, hit timing the server uses, cast acceptance, projectile spawn) in events — server can't read events
- do not blank existing `CombatAnimationSet` timing fields before the runtime reader lands; fields are the fallback path during the intermediate phase
- do not author events on FBX-embedded clips (modifies third-party `.meta`); always author on extracted `.anim` files in `Assets/Arena/Content/Animation/Extracted/`
- do not add new timing fields to `CombatAnimationSet`; new presentation timing belongs as events on the clip
- do not auto-create events; the stamper is explicit-action only
- do not bake default values into per-clip events when a runtime fallback is simpler
- do not let event names diverge from `CombatClipEventTemplates` constants — typos fail silently
- do not migrate strike timing to events without server extraction tooling

## Resolved Design Decisions

These decisions are part of the plan, not implementation-time choices.

### Events as data, not callbacks

The runtime never installs `SendMessage` handlers for animation events. The reader code path is:

```text
AnimationEvent[] events = AnimationUtility.GetAnimationEvents(clip);
float time = events.FirstOrDefault(e => e.functionName == "OnReleaseFrame")?.time ?? fallback;
```

This makes events behave like authored metadata that travels with the clip. No interrupt-clip lifecycle issues, no edit-mode ambiguity, no animator component required for events to be readable, no risk of event-driven gameplay state changes.

### Role inference from `CombatAnimationSet` references

A clip's "role" (SpellRelease, MeleeStrike, SpellCastHoldEnter, etc.) is inferred from how the clip is referenced inside a `CombatAnimationSet`. Naming conventions are not used. The inferer walks every `CombatAnimationSet`, builds a `clip → roles[]` map, and surfaces conflicts (same clip referenced under multiple roles).

For clips not yet referenced by any set, the stamper window provides a manual role override dropdown. Authoring events ahead of wiring is supported but circular: the validator can't audit unreferenced clips, so authored events sit unverified until the clip is wired.

### Default fallback for optional events

Some events are optional and have a sensible runtime default if absent. `OnLowerBodyBlendEnd` is the canonical example: if missing, the runtime computes `OnLowerBodyUnlock + DefaultLowerBodyBlendDurationSeconds` (a constant, default `0.2s`). This avoids forcing per-clip authoring for the common case while leaving room for per-clip overrides where they matter.

The same pattern applies to any optional event: stamper template marks it optional, runtime reader has an explicit fallback, validator does not require it, but the stamper button is still present for clips that need a non-default value.

### Server-authoritative timing stays in the catalog

Cast time (`cast_time_ms`), strike windows the server uses to schedule damage, projectile spawn timing, and any other timing the server needs remain in `progression_catalog.shared.json`. The Rust server cannot read Unity `AnimationClip` events at runtime; making events authoritative would require a build-time extraction tool that does not exist today.

If/when the catalog grows costly to maintain in parallel with clip events, the path forward is a Unity editor build step that walks all referenced clips, reads their events, and writes the catalog. Until then, catalog stays hand-authored and events drive presentation only.

### Stagger does not release locomotion

`Stagger` clips are full-body and stay full-body. No `OnLowerBodyUnlock` or `OnLowerBodyBlendEnd` events. The role's template has only `OnVisualInterruptible` (optional).

### `OnVisualInterruptible` policy: keep for now

`OnVisualInterruptible` controls the ghost-vs-cut decision when a new visual preempts an active one. Currently in the templates for `MeleeStrike`, `Stagger`, `GetUp`, `PhasedMeleeEnd`, and `SpellRelease`. May be dropped later if "always cut" presentation looks acceptable across the roster. Decision deferred until the runtime reader exists and the actual visual cost of always-cut is measurable.

### Clip extraction is on-demand

The `ThirdPartyAnimationExtractor` runs over all third-party FBX/anim assets in `Assets/ThirdParty/AssetStore/Animation/` and copies clips into `Assets/Arena/Content/Animation/Extracted/<pack>/...`. It skips destinations that already exist so re-runs preserve authored events. New third-party packs added later only require a re-run to pick up their clips.

Existing flat `.anim` extracts at `Assets/Arena/Content/Animation/` (e.g., `HumanM@CastingEnter01.anim`) are referenced by `CombatAnimationSet` and stay until references are repointed at their structured `Extracted/` counterparts.

### Phased authoring; no flag day

The migration is staged. The runtime reader layer reads events when present and falls back to existing fields when absent. Each clip can be in either state independently. No big-bang switchover. Cleanup (field deletion) only happens after the bulk migration tool has run and behavior verification is complete.

## Critical Non-Negotiable

The combat presentation system must not regress. Specifically:

- The cast-hold → release fade behavior shipped in the prior plan must continue to work identically. The new event-reader layer reads `OnHoldFadeStart` / `OnHoldFadeEnd` (or falls back to `SpellCastHoldExitDelaySeconds` / `SpellCastHoldExitCrossFadeDurationSeconds` constants and `SpellCastHoldProfile.exitDelaySeconds` / `exitBlendOutSeconds` fields) producing the same visual.
- The visual interrupt / ghost machinery in `PlayerAnimator` must continue to work for clips without `OnVisualInterruptible` events authored.
- The lower-body unlock / blend logic must work whether driven by events or by `lowerBodyUnlockAtSeconds` / `lowerBodyBlendOutSeconds` fields.
- Server-driven timing (cast acceptance, COMBAT_RELEASE, strike damage) is unchanged. This plan touches presentation only.

## Existing Responsibilities To Preserve

### `CombatAnimationSet`

Continues to own:

- spell action id mapping (`spells[].spellId`)
- ground / air clip references
- `playbackLayer` policy
- `requiresCombatStance` / `combatEntryMode` policy
- weapon presentation profile (draw / sheath clips, weapon visuals)
- weapon strike / phased strike clip references
- status reaction loop clips
- defensive stance clips (parry, block)
- hit reaction / stagger / knockdown / stun / death clips
- `defaultSpellCastHold` enter / idleLoop / playbackLayer

Loses (after migration):

- `WeaponSpellAnimationEntry.groundEffectTime`
- `WeaponSpellAnimationEntry.airEffectTime`
- `WeaponSpellAnimationEntry.lowerBodyUnlockAtSeconds`
- `WeaponSpellAnimationEntry.lowerBodyBlendOutSeconds`
- `WeaponSpellAnimationEntry.visualInterruptibleAtSeconds`
- `WeaponMeleeAttackAuthoring.lowerBodyUnlockAtSeconds`
- `WeaponMeleeAttackAuthoring.lowerBodyBlendOutSeconds`
- `WeaponMeleeAttackAuthoring.visualInterruptibleAtSeconds`
- `SpellCastHoldProfile.exitDelaySeconds`
- `SpellCastHoldProfile.exitBlendOutSeconds`

### `PlayerAnimator`

Continues to own animator playback, layer arbitration, preemption, and combat stance. Adds a thin event-reader helper that consumers use to fetch event times. Loses these constants once migration completes:

- `SpellCastHoldEnterToIdleNormalizedTime` (becomes `OnEnterComplete` event on cast-enter clip)
- `SpellCastHoldExitDelaySeconds` (becomes `OnHoldFadeStart` event on release clip)
- `SpellCastHoldExitCrossFadeDurationSeconds` (becomes `OnHoldFadeEnd` event on release clip; or stays as a default constant if no per-clip overrides are needed)

Gains a single default constant: `DefaultLowerBodyBlendDurationSeconds = 0.2f` (used when `OnLowerBodyBlendEnd` is absent).

### `SpellCastPresentationController`

Unchanged. The scheduler reads `ActiveCast.ends_at` and authored release timing — that authored timing source moves from `groundEffectTime * clip.length` to the `OnReleaseFrame` event. The controller doesn't care which.

### `CombatVFXAuthoringValidator`

Gains event-presence checks. During the intermediate phase these are warnings; after migration they become errors. New checks:

- Cast-time spell entries must have `OnReleaseFrame` on their release clips.
- Cast-hold profile enter clips must have `OnEnterComplete`.
- Spell release clips must have `OnHoldFadeStart` if a hold fade is wanted.
- Optional events are not required but unknown event names log warnings (typo detection).

## Desired Model

After full migration, the timing-data flow is:

```text
Authoring time:
  artist → AnimationClip.events (in the .anim file)

Compile / load time:
  no change; events ship with the clip asset

Runtime presentation:
  consumer asks: GetEventTime(clip, "OnReleaseFrame")
    → reads clip.events
    → returns first matching event's time
    → falls back to default if absent

Server timing (unchanged):
  catalog → server logic → COMBAT_RELEASE / damage / projectile spawn
```

During the intermediate phase, the runtime reader has a fallback to the existing `CombatAnimationSet` field:

```text
GetEventTime(clip, "OnReleaseFrame", fallback: entry.groundEffectTime * clip.length)
  → if event present: event.time
  → else: fallback expression
```

So both paths are functional simultaneously. Clips can be migrated one at a time. Re-runs of the bulk migrator are idempotent.

## Authoring Contract

### Event taxonomy

Defined in [`Assets/Arena/Editor/CombatClipEventTemplates.cs`](Assets/Arena/Editor/CombatClipEventTemplates.cs). Per-role required and optional events. Single source of truth shared by stamper tool and validator.

Currently authored:

| Role | Required | Optional |
|---|---|---|
| `SpellCastHoldEnter` | `OnEnterComplete` | (none) |
| `SpellCastHoldIdle` | (none) | (none) |
| `SpellCastHoldExit` | `OnExitComplete` | (none) |
| `SpellRelease` | `OnReleaseFrame`, `OnHoldFadeStart` | `OnLowerBodyUnlock`, `OnLowerBodyBlendEnd`, `OnVisualInterruptible` |
| `MeleeStrike` | `OnStrikeHit` | `OnLowerBodyUnlock`, `OnLowerBodyBlendEnd`, `OnVisualInterruptible` |
| `PhasedMeleeStart` | `OnPhaseLoopReady` | (none) |
| `PhasedMeleeLoop` | (none) | (none) |
| `PhasedMeleeEnd` | `OnStrikeHit` | `OnLowerBodyUnlock`, `OnLowerBodyBlendEnd`, `OnVisualInterruptible` |
| `HitReaction` | (none) | (none) |
| `Stagger` | (none) | `OnVisualInterruptible` |
| `StatusReactionLoop` | (none) | (none) |
| `KnockdownStart` | (none) | `OnGroundedFrame` |
| `KnockdownLoop` | (none) | (none) |
| `GetUp` | (none) | `OnVisualInterruptible` |
| `Stun*` | (none) | (none) |
| `ParryStart` | `OnParryWindowStart`, `OnParryWindowEnd` | (none) |
| `ParryHit` | (none) | (none) |
| `BlockStart` | `OnBlockReady` | (none) |
| `BlockLoop` / `BlockEnd` / `BlockHit` / `BlockHitBreak` | (none) | (none) |
| `Death` | (none) | (none) |
| `DrawWeapon` / `SheathWeapon` | `OnWeaponHandoff` | (none) |

Locomotion clips have no events.

### Authoring location

All events are authored on standalone `.anim` files under `Assets/Arena/Content/Animation/`. New extractions live under `Extracted/<pack>/...`. Existing flat extracts at the root remain until references are repointed.

### Authoring tools

`Arena → Animation → Extract Third-Party Clips` — bulk-extract FBX-embedded and pre-extracted clips from `Assets/ThirdParty/AssetStore/Animation/` to `Assets/Arena/Content/Animation/Extracted/`. Skip-if-exists, idempotent.

`Arena → Animation → Print Combat Clip Roles` — diagnostic. Walks every `CombatAnimationSet`, builds the clip → role map, prints summary and conflicts.

`Arena → Animation → Event Stamper` — sidekick window. Embedded character preview (Unity's `AnimationClipEditor` rendered inline). Role inference + manual override. Stamp buttons per role's template. Custom event name field. Existing events list with remove buttons. Stamps at the embedded preview's playhead time when reflection succeeds; falls back to a manual time slider otherwise.

`Arena → Animation → Print Combat Controller Inventory` — pre-existing. Audits the AnimatorController's states/parameters against an ownership manifest. Updated to recognize `SpellCastHoldAction1..4`.

## Required Runtime Surface (not yet built)

A small static helper:

```csharp
public static class CombatAnimationEvents
{
    public const string OnReleaseFrame        = "OnReleaseFrame";
    public const string OnEnterComplete       = "OnEnterComplete";
    public const string OnHoldFadeStart       = "OnHoldFadeStart";
    public const string OnHoldFadeEnd         = "OnHoldFadeEnd";
    public const string OnLowerBodyUnlock     = "OnLowerBodyUnlock";
    public const string OnLowerBodyBlendEnd   = "OnLowerBodyBlendEnd";
    public const string OnVisualInterruptible = "OnVisualInterruptible";
    public const string OnStrikeHit           = "OnStrikeHit";
    public const string OnPhaseLoopReady      = "OnPhaseLoopReady";
    public const string OnGroundedFrame       = "OnGroundedFrame";
    public const string OnParryWindowStart    = "OnParryWindowStart";
    public const string OnParryWindowEnd      = "OnParryWindowEnd";
    public const string OnBlockReady          = "OnBlockReady";
    public const string OnWeaponHandoff       = "OnWeaponHandoff";

    public static bool TryGetEventTime(AnimationClip clip, string functionName, out float seconds);
    public static float GetEventTimeOrFallback(AnimationClip clip, string functionName, float fallbackSeconds);
}
```

Consumer call sites in `PlayerAnimator`, `SpellCastPresentationController`, and `CombatActionPlaybackController` switch from reading `WeaponSpellAnimationEntry`/`WeaponMeleeAttackAuthoring` timing fields to calling `CombatAnimationEvents.GetEventTimeOrFallback(clip, name, fieldFallback)`. The fallback expression keeps the existing field path live during the intermediate phase.

## Validation Rules

### During intermediate phase

- Missing required event on a referenced clip: warning.
- Unknown event function name on a clip: warning ("did you mean OnReleaseFrame?").
- Optional events: not flagged.

### After migration

- Missing required event: error (blocks build / play mode if validator is wired into the build gate).
- Unknown event function name: still warning, since custom events for non-templated cases may exist.
- Existing field with a non-zero value but no matching event on the clip: warning ("field still authoritative; migrator should run").
- After cleanup phase: fields deleted, this last warning becomes "field doesn't exist" — gone.

## Implementation Phases

### Phase 1 - Tooling and templates (DONE)

Files:

- [`Assets/Arena/Editor/CombatClipRole.cs`](Assets/Arena/Editor/CombatClipRole.cs)
- [`Assets/Arena/Editor/CombatClipEventTemplates.cs`](Assets/Arena/Editor/CombatClipEventTemplates.cs)
- [`Assets/Arena/Editor/CombatClipRoleInferer.cs`](Assets/Arena/Editor/CombatClipRoleInferer.cs)
- [`Assets/Arena/Editor/AnimationEventStamperWindow.cs`](Assets/Arena/Editor/AnimationEventStamperWindow.cs)
- [`Assets/Arena/Editor/ThirdPartyAnimationExtractor.cs`](Assets/Arena/Editor/ThirdPartyAnimationExtractor.cs)

Status: complete. Stamper has embedded preview, role inference + manual override, role-templated stamp buttons, custom event name field, existing events list. Extractor handles both FBX-embedded and pre-extracted `.anim` packs.

### Phase 2 - Runtime reader layer (NEXT)

Files:

- new `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationEvents.cs` (helper + name constants)
- `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs` (consumer updates)
- `Assets/Arena/Runtime/Presentation/SpellCastPresentationController.cs` (release-offset reader updates)
- `Assets/Arena/Runtime/Presentation/CombatActionPlaybackController.cs` (lower-body unlock reader updates)

Work:

- Add `CombatAnimationEvents` helper with name constants and `TryGetEventTime` / `GetEventTimeOrFallback` methods.
- Replace direct field reads with `GetEventTimeOrFallback(clip, name, existingFieldExpression)` at every consumer site.
- Add `DefaultLowerBodyBlendDurationSeconds` constant in `PlayerAnimator` for the `OnLowerBodyBlendEnd` fallback.
- Validator (warn-only) added to `CombatVFXAuthoringValidator`.

Acceptance:

- `ICICLE` cast hold → release continues to play identically with all existing field values, no events authored. (Field path fallback verified.)
- After authoring `OnHoldFadeStart` and `OnReleaseFrame` events on Icicle's release clip with timings that match the field-derived values, behavior is unchanged. (Event path verified.)
- After clearing the field values on the Icicle entry while keeping events authored, behavior is unchanged. (Event-only path verified.)
- Validator surfaces a warning if `OnReleaseFrame` is missing on a `SpellRelease` clip.

### Phase 3 - Bulk migration tool

Files:

- new `Assets/Arena/Editor/AnimationEventMigrator.cs`

Work:

- Menu item `Arena → Animation → Migrate Field Timings to Events`.
- Walks every `CombatAnimationSet`. For every `WeaponSpellAnimationEntry` with non-zero timing fields, stamps corresponding events on the entry's `ground` and `air` clips.
- Same for `WeaponMeleeAttackAuthoring`.
- Same for `SpellCastHoldProfile.exitDelaySeconds` (stamps `OnHoldFadeStart` on each `SpellRelease` clip referenced by spells using that hold profile).
- Skip-if-event-exists: never overwrite hand-authored event values.
- Logs a per-asset summary: events stamped, events skipped (already present), entries with no field value (skipped — runtime uses default).

Acceptance:

- Re-run is idempotent (no duplicate events).
- After running, the warn-only validator passes silently across the existing roster.
- Behavior is unchanged from before the run on a representative sample (Icicle, Fireball, Intimidate, three melee strikes).
- Hand-authored events from Phase 2 spot-check are preserved.

### Phase 4 - Verification

Work:

- Spot-check a handful of clips by deleting the field on the asset and confirming event-only path produces identical behavior.
- Author events on one or two new (not yet field-driven) clips and confirm validator passes.
- Run play-mode tests covering: instant spell release, cast-time spell release, melee strike, phased melee, hit reaction, block, parry. Confirm no visual regressions.

Acceptance:

- Validator silent on the entire referenced roster.
- One spot-checked clip with field cleared and event present plays identically to before.
- Play-mode test suite green.

### Phase 5 - Cleanup

Files:

- `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs` (remove fields)
- `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs` (remove `SpellCastHoldExit*` constants if no per-clip overrides authored)
- `Assets/Arena/Editor/SpellAuthoringWindow.cs` and any other write sites
- `CombatVFXAuthoringValidator.cs` (flip warnings to errors)

Work:

- Remove `groundEffectTime`, `airEffectTime`, `lowerBodyUnlockAtSeconds`, `lowerBodyBlendOutSeconds`, `visualInterruptibleAtSeconds` from `WeaponSpellAnimationEntry`.
- Remove same from `WeaponMeleeAttackAuthoring`.
- Remove `exitDelaySeconds`, `exitBlendOutSeconds` from `SpellCastHoldProfile`.
- Remove fallback expressions in consumer code (events only, no field fallback).
- Decide: keep `SpellCastHoldExitCrossFadeDurationSeconds` as a default if `OnHoldFadeEnd` is consistently authored as optional; remove if always present. Same for related constants.
- Validator flips to error on missing required events.

Acceptance:

- Asset inspectors show only structural fields (clip refs, layer policy, gameplay flags). No timing knobs remain on `CombatAnimationSet`.
- No magic timing constants in `PlayerAnimator` except sensible runtime defaults.
- Validator errors on missing required events; CI / play-mode validation enforces.
- One round of regression testing confirms behavior is unchanged from end of Phase 4.

## Red Flags To Avoid

- Do not blank `CombatAnimationSet` timing fields before Phase 2 runtime reader lands. The fields are the fallback during the intermediate phase.
- Do not use Unity `SendMessage` animation event callbacks for any presentation logic. Event reading is data-only.
- Do not migrate strike timing or cast time to events. Server cannot read events.
- Do not author events on FBX-embedded clips. Always extract to `Assets/Arena/Content/Animation/Extracted/` first.
- Do not let event names diverge from `CombatClipEventTemplates` constants. Typos fail silently at runtime; the validator's typo check is the only safety net.
- Do not bake "smart" auto-creation of events into the stamper. Authoring is explicit-action only.
- Do not migrate `lowerBodyBlendOutSeconds` to a per-clip event when the runtime fallback (`unlock + DefaultLowerBodyBlendDurationSeconds`) covers the common case. Only author `OnLowerBodyBlendEnd` for clips that need a non-default duration.
- Do not delete the timing fields without running Phase 3 migrator first. Unauthored clips will silently fall to defaults.
- Do not skip Phase 4. The bulk migrator can produce wrong values if the field-to-event conversion has unit bugs (normalized vs seconds); a sample verification catches that.

## Open Questions

1. **Should `OnVisualInterruptible` survive long-term?**

   The role's only purpose is to control ghost-vs-cut presentation when interrupting active visuals. If "always cut" looks acceptable across the roster, the event and the underlying ghost machinery can be removed. Decision deferred until Phase 4 verification reveals whether the ghost path is visibly load-bearing.

2. **Per-clip blend duration overrides actually used?**

   `OnLowerBodyBlendEnd` is currently optional with a runtime default. If, after migration, no clip in the roster has the event authored, drop it from the templates (and from `CombatAnimationEvents` constants) and keep only the default constant.

3. **When does build-time event extraction become worth building?**

   The Rust server reads `progression_catalog.shared.json` for hit timing, cast time, etc. Today these are hand-authored in JSON; a future build step could extract them from clip events to keep them co-located with the clip. Probably worth building once the strike roster crosses ~50 entries or maintaining catalog/clip parity becomes painful.

4. **Should there be a per-clip role override asset?**

   Today the role inferer is reference-based and the stamper supports a manual override. If many clips need to be authored ahead of being referenced (or some clips fill different roles in different contexts), a `CombatClipRoleOverride` asset that lets authors map specific clips to specific roles outside `CombatAnimationSet` may be useful. Defer until pain motivates.

5. **Reference repointing — automated or manual?**

   Existing flat extracts at `Assets/Arena/Content/Animation/` are referenced by `CombatAnimationSet` directly; they need to be repointed at their structured `Extracted/` counterparts before the flat duplicates can be deleted. With ~5 flat clips currently, manual repointing in the inspector is fine. A repointing menu item could be built if the count grows.

## Recommended Decision

Proceed with phased migration. The intermediate phase is the safe place to live: events override fields when authored, fields fallback when events are missing, no big-bang cleanup required. Build the Phase 2 runtime reader next; defer the bulk migrator until the reader is verified on a few hand-authored clips.

The hardest part is already done — Phase 1 tooling is complete. Phase 2 is mechanical (replace field reads with helper calls). Phase 3 is mechanical (walk assets, stamp events). Phase 4 is verification. Phase 5 is deletion. Each phase has explicit acceptance criteria and is independently revertible.

This is the cleanest way to scale clip-internal presentation timing to thousands of clips without inventing a new authoring system or rewriting the animator.
