# Combat Animation Authoring Contract

This is the source of truth for combat animation meaning. It exists to keep runtime code, Unity animation assets, server gameplay timing, and future LLM edits from inventing separate interpretations of the same action.

Use this together with `docs/combat-authoring-contract.md`. The broader combat authoring contract owns progression, loadout, and action identity rules. This document owns animation presentation rules.

## Core Rule

Combat animation meaning must live in authored data plus this contract, not in the visual shape of the Animator graph.

The Animator Controller, Playables, and C# runtime are execution mechanisms. They may implement the contract, but they are not allowed to redefine action identity, hit timing, movement authority, or interruption priority.

If the Animator graph appears to disagree with this contract, treat the graph as legacy or wrong until the mismatch is deliberately reconciled.

## Action Taxonomy

Every animated action must fit one of these categories:

- `locomotion`: idle, walk, run, stops, turns, jump, fall, land.
- `stance`: enter combat, exit combat, draw weapon, stow weapon.
- `melee_action`: authored weapon attacks, including auto-attacks and selectable melee.
- `cast_action`: spell, buff, channeled, and ability cast presentation.
- `movement_action`: dash, charge, leap, blink, and other actions whose primary outcome is displacement.
- `defense_action`: block, parry, guard hit, guard break.
- `reaction`: hit reaction, stagger, stun, knockdown, get-up.
- `death`: death presentation.

Do not add a generic "combat animation" bucket when a narrower category applies.

These taxonomy strings are contract vocabulary. They are not currently serialized action type values unless a future migration explicitly adds them to authoring data.

Gameplay-owned displacement is a capability, not a hard taxonomy fork. A melee action, cast action, or movement action may include gameplay-owned displacement when its behavior requires it.

## Source Ownership

### Combat Animation Sets

Files: `Assets/Arena/Resources/CombatAnimationSets/*.asset`

`CombatAnimationSet` assets are the source of truth for Unity-authored combat presentation data:

- combat profile identity
- weapon visuals and weapon visual handoff timing
- locomotion and combat locomotion clips
- combat-mode-specific locomotion overrides
- draw and stow clips
- melee authored strike ids
- melee runtime slot ids
- melee hit-window identity and compatibility mirrors; authored timing lives on `OnStrikeHit` clip events
- melee recovery timing
- melee combo links
- melee presentation mode
- melee phased clips
- melee visual interruption timing
- semantic spell cast-motion family bindings and one-handed cast-hand convention
- dodge, block, parry, charge, hit, stagger, stun, knockdown, get-up, and death clips

Future melee and cast phase metadata should be added to `CombatAnimationSet` unless there is a concrete reason to put it somewhere else.

Do not regenerate, flatten, or "simplify" existing combat animation set assets as part of animation system work. They contain real authored timing and binding work.

### Combat Mode Locomotion Overrides

Combat modes are profile-owned stance/mode state from `CombatModeCatalog` and `ActiveCombatMode`. When a mode changes only how a combat profile moves, author it as a `CombatAnimationSet.locomotionModeOverrides[]` entry keyed by the mode's `mode_id`.

Rules:

- the action that changes the mode must still be a normal action-bar ability with `gameplay.kind: "COMBAT_MODE_TOGGLE"`
- the mode override lives on the existing combat profile animation set, not in a second parallel animation set
- the override may replace only locomotion slots: idle, combat idle, directional walk/run loops, stops, and turns
- leaving a mode must restore the base combat-profile locomotion slots before applying another mode
- if the animation pack has no separate run bank for the mode, explicitly decide whether run slots should stay base-run or reuse the mode's walk clips; do not leave the behavior accidental

Example: Dagger `STEALTHED` uses the `DAGGERS` combat profile and a `STEALTHED` locomotion override on `Assets/Arena/Resources/CombatAnimationSets/Daggers.asset`. It does not create a new Dagger animation set or a hidden input path.

### Animation Clip Object Curves

Imported humanoid clips can also contain non-humanoid object curves for weapon prop nodes. These bindings are path-based and case-sensitive. Unity will only drive them if the runtime Animator hierarchy contains the exact relative path serialized in the clip.

Example failure:

```text
clip path:    root/pelvis/spine_01/.../hand_r/weapon_r
runtime path: Root/Pelvis/spine_01/.../hand_r/weapon_r
```

Those do not match. The humanoid body still retargets, but the weapon prop curves do not bind. The symptom is a weapon staying in its static mount pose while the vendor preview shows the prop being aimed, drawn, stowed, or otherwise animated.

When assigning a new imported clip to a `CombatAnimationSet`, inspect its object-curve paths before assuming a visual discrepancy is bad source animation. For Arena runtime avatars, retarget extracted copies of package object-curve paths to the runtime hierarchy when the clip intentionally animates a held/stowed prop. Do not expose package prop-node names as authored mount ids to make the path match. Mount ids remain semantic; path compatibility belongs inside the extracted clip and avatar compatibility hierarchy.

Current audit, 2026-05-13:

- `TwoHandedSword.asset` references 214 GreatSword clips with weapon object curves; 213 still use package-case `root/pelvis/...` paths, and `Run_Attack_02.anim` has been retargeted to `Root/Pelvis/...` for Impale.
- `SwordAndShield.asset` references 198 SwordAndShield clips with package-case `Sword`, `Shield`, `Sword_Holder`, or `Shield_Holder` object curves.
- `ArcherBow.asset` references 189 Archer clips with package-case bow object curves such as `Weapon_Bow_L`, `Bow_String`, `Weapon_Bow_R`, and `Bow_Holder1`.

Treat these counts as an integration risk list. Retarget clips when their prop curves are supposed to drive an Arena-managed weapon visual; otherwise leave them alone and rely on semantic mounts plus `WeaponAttachmentController`.

### Progression Catalog

File: `server/src/progression_catalog.shared.json`

Progression owns player-facing gameplay data and action exposure. It does not own Unity clip timing, hit windows, lower-body unlock timing, or animation layer behavior.

### Melee Manifest

File: `server/src/melee_manifest.shared.json`

This is an exported bridge from `CombatAnimationSet` assets and their assigned clip events to the server. Do not hand-edit it to fix animation timing. Author `OnStrikeHit` in the Event Stamper; the stamper synchronizes the affected strike automatically. To apply exported timing changes in new local Hub-created matches or open-world instances, run `ops/setup-local-multiplayer.sh setup` from the repository root. Editor auto-publish updates only its direct-local database; see [the local publication workflow](project-structure.md#generated-code).

## Timing Semantics

Keep these concepts separate. They are intentionally not aliases.

### Hit Windows

Hit Windows define when gameplay contact or release occurs inside a melee attack timeline. For migrated attacks, each `OnStrikeHit` event on the assigned single or phased presentation is authoritative. The Event Stamper mirrors those times into `WeaponStrikeCombatAuthoring.hitWindows` for compatibility and replaces only the affected strike in `server/src/melee_manifest.shared.json`.

Attacks without an `OnStrikeHit` event retain their serialized hit-window fallback until they are explicitly migrated. Once an attack has events, do not edit the mirrored array directly.

For direct melee, each hit window schedules a `PendingMeleeImpact` on the server. For projectile weapon attacks, each hit window may schedule a projectile release instead. Multi-hit damage is currently split across hit windows.

Single-clip melee may author `startupTrimSeconds` on its existing `CombatAnimationSet` attack entry. The event stays on the physical contact pose, playback begins at the trim point, and hit-window mirroring/export uses `max(0, event time - startup trim)`. A trim equal to first contact intentionally produces a zero-delay hit. Startup trim is not supported for phased melee. See `docs/melee-startup-trim-design-2026-07-16.md` for the bounded contract and Unity workflow.

Hit Windows do not mean:

- when the animation may be interrupted
- when the lower body may return to locomotion
- when the action leaves recovery
- when a root-motion lunge should move the player

### Recovery

`recoveryMs` is gameplay/server recovery timing for the melee action. It contributes to cadence, follow-up, and pending impact recovery rules.

Recovery is not a visual blend-out instruction. Visual recovery may continue after gameplay recovery, and visual recovery may be interrupted if the action declares it is safe.

`recoveryMs` is not consumed by client animation timing today; it is exported to the melee manifest and consumed by server gameplay.

### Lower-Body Unlock

Lower-body unlock is presentation timing. It defines when locomotion may regain control of the lower body while the same full-body source clip continues through an upper-body mask.

This must be represented separately from Hit Windows and visual interruption. A full-body source clip can drive the whole character early, then continue as upper-body-only recovery later.

Default rule for existing content:

```text
lowerBodyUnlockAtSeconds unset -> lower body stays owned by the action until the action presentation ends.
```

### Visual Interruption

Visual interruption is presentation timing. It defines when the current action visual may be replaced without preserving a ghost or suppressed replay.

Default rule for existing content:

```text
visualInterruptibleAtSeconds unset or invalid -> use the current fallback, usually the timing reference length.
```

Visual interruption does not cancel server gameplay facts that already happened or have already been scheduled.

### Worked Timeline

Example: a 1.0 second greatsword strike with one gameplay hit and a long visual recovery.

```text
0.00s  action starts
       owner: presentation
       full-body combat layer starts the authored full-body clip

0.32s  Hit Window 0
       owner: gameplay/server
       server schedules or resolves melee contact/release from exported hit window timing

0.44s  lowerBodyUnlockAtSeconds
       owner: presentation
       lower body begins returning to locomotion

0.44s-0.56s lowerBodyBlendOutSeconds = 0.12s
       owner: presentation
       full-body action influence fades down while upper-body recovery continues

0.62s  visualInterruptibleAtSeconds
       owner: presentation
       a new eligible combat action may replace this recovery without preserving a ghost

0.32s + recoveryMs
       owner: gameplay/server
       server-side melee recovery/cadence window ends; this is not a client animation blend-out

1.00s  source clip ends
       owner: presentation
       any remaining action presentation should be cleared
```

The important separation is that Hit Windows and `recoveryMs` are gameplay timing, while lower-body unlock, blend-out, and visual interruption are presentation timing.

## Recommended Action Phase Fields

For single-clip melee and cast actions, prefer these optional fields:

```csharp
public float lowerBodyUnlockAtSeconds;
public float lowerBodyBlendOutSeconds;
public float visualInterruptibleAtSeconds;
```

Expected behavior:

```text
0.00s action starts
commit window: full body action layer may own the whole character
hit/release window: server gameplay timing comes from Hit Windows or spell/cast data
lowerBodyUnlockAtSeconds: earliest time lower body may blend back to locomotion if locomotion is demanded
visualInterruptibleAtSeconds: incoming eligible action may replace recovery presentation
```

Lower-body unlock is permission, not motion. If the authored timestamp has elapsed but the character is standing still, the full-body action should keep playing its lower-body settling. If movement is already active when the timestamp arrives, or movement starts later, the runtime may hand the legs back to locomotion and continue the action on an upper-body layer.

For phased or stitched actions, do not pretend a single absolute timestamp is enough when the phase matters. Use phase-aware fields:

```text
lowerBodyUnlockPhase = Start | Loop | End | None
lowerBodyUnlockAtSeconds = phase-relative time
visualInterruptiblePhase = Start | Loop | End | Never
visualInterruptibleAtSeconds = phase-relative time
```

## Layer Ownership

The layer names below describe desired ownership. Existing layer names may differ during migration.

### Base Locomotion

Owns locomotion, jump/fall/land, grounded movement, core stance, and neutral combat stance.

Base locomotion should keep running underneath combat action layers so lower-body unlock can restore movement without restarting locomotion from scratch.

Phased melee is not a Base Layer owner. It is segmented combat action data: start/loop/end clips play on the combat full-body layer until lower-body unlock, then continue on the combat upper-body recovery layer if more segment playback remains.

Current runtime note: phased melee advances its segments through different reusable strike-bank states on the combat full-body layer. This is intentionally an Animator-controller compatibility workaround, not a new authoring concept. It avoids the existing strike states' exit-to-Empty transitions cutting off later segments. Authors should think in start/loop/end segments; runtime maintainers should prefer an Animation Playables segment player if this workaround becomes a source of transition bugs.

### Stance / Upper Body

Owns draw weapon, stow weapon, moving casts, channeling, aim, and other torso/arms/head overlays that should preserve locomotion.

### Left Gesture

Owns left-side cast or gesture overlays that should never use the full-body spell layer. Playback uses a single left-gesture mask including pelvis/spine plus the left shoulder/arm/hand so the source clip can supply posture without taking the legs or right side away from locomotion. This is for authored actions such as pointing or small off-hand gestures.

### Right Gesture

The mirrored right-side cast path. It uses pelvis/spine plus the right shoulder/arm/hand and keeps
the left side on the base weapon pose. One-hand spell composition chooses LeftGesture or
RightGesture from the active set's `oneHandedCastHand`.

### Combat Full Body

Temporarily owns the whole character during committed melee/cast phases when the authored pose needs legs, hips, torso, and arms.

This layer must not stay at full weight through recovery unless the action explicitly declares that the lower body remains locked.

### Combat Upper Body

Owns recovery and continuing action presentation after lower-body unlock. This layer uses an upper-body mask and may continue playing the same full-body source clip at the same clip time.

This should be a general combat action recovery layer, not a melee-only or phased-melee-only layer. Phased melee should be represented as segmented combat action data that uses the same full-body and upper-body action layers as single-clip melee.

### Movement Action

Owns full-body presentation for displacement-first actions such as dash, charge, leap, and blink. Gameplay movement owns actual displacement, pathing, collision, and endpoint.

### Reaction

Owns hit, stagger, stun, knockdown, get-up, and death according to the priority table. Reaction layers may preempt action layers when their priority is higher.

## Priority And Interruption

Use explicit priority rather than scattered special cases.

Highest to lowest:

```text
death
knockdown / incapacitated
stun / hard crowd control
stagger / guard break
non-interruptible movement action, future policy
committed melee or cast
hit reaction
recovery melee or cast
upper-body stance/cast/weapon action
locomotion
```

Rules:

- If a hit reaction and stun are caused by the same resolved hit, play stun only.
- If hit reaction starts and stun arrives shortly after due to event ordering, stun should preempt hit reaction. This is desired contract behavior and must be implemented/verified during reaction migration.
- If already stunned, suppress normal hit reactions unless the new result upgrades the state.
- Death preempts every non-death presentation.
- Knockdown preempts stun and normal reactions.
- A committed action may suppress or ghost lower-priority visuals until its visual interruption threshold.

Define what each interruption means at the call site:

- visual cancel only
- gameplay cancel
- blend into recovery
- replace with next action
- suppress incoming visual
- preserve replay ghost

Do not use the word "interrupt" without one of those concrete meanings.

## Gameplay-Owned Displacement

Some actions include gameplay-owned displacement. Displacement is a capability/policy, not necessarily a separate action category.

Examples:

- dash with no impact
- charge with an impact at the end
- leaping slash with melee Hit Windows
- teleport strike
- cast that moves the caster during release

Displacement contract:

- server/client gameplay movement owns displacement, collision, timing, and endpoint
- animation follows or decorates the movement
- animation root motion is not authoritative
- stitched movement actions should be represented as segments when gameplay has meaningful phases

Do not ask authors to classify "movement-first" versus "attack-first" unless a system genuinely needs that distinction. Prefer explicit capability fields:

```text
displacement = none | gameplayOwned
impactModel = none | meleeHitWindows | projectileRelease | spellEffect
defenseModel = none | melee | spell | custom
targetingModel = none | self | target | direction | location
```

A charge and a leaping slash can share the same underlying shape: gameplay-owned displacement plus optional impact windows plus animation segments. Their differences should come from concrete fields such as targeting, defense behavior, combo behavior, and impact model.

Illustrative segment shape:

```csharp
public string actionType = "melee_action"; // illustrative contract vocabulary
public string movementAuthority = "gameplay";
public string impactModel = "meleeHitWindows";
public CombatActionSegment[] segments =
{
    new("start", bodyMode: FullBody),
    new("loop", bodyMode: FullBody, syncToMovement: true),
    new("impact", bodyMode: FullBody),
    new("recover", lowerBodyUnlockAtSeconds: 0.12f),
};
```

The real authoring shape is deferred. It should be C# serialized data or ScriptableObject data, not necessarily JSON.

## Root Motion Policy

Keep `Animator.applyRootMotion = false` for player combat presentation unless a future controller architecture explicitly changes movement authority.

Root motion may be used as authoring reference. It must not move the authoritative player transform in normal server-authoritative combat.

For normal melee and casts:

- prefer in-place or mostly in-place clips
- avoid strong root travel that gameplay movement does not match
- use lower-body unlock to reduce recovery sliding

For lunges, charges, dodges, and gap closers:

- gameplay movement drives real displacement
- animation should visually match the gameplay path
- do not fix pathing or endpoint problems by enabling clip root motion

## Cast Actions

Casts use the same phase vocabulary as melee, but not every cast is full-body.

Recommended categories:

- standing cast: full-body through release, optional upper-body recovery
- moving cast: upper-body from the start
- channeled cast: upper-body loop while locomotion continues
- rooted cast: full-body until end or explicit lower-body unlock
- interrupted cast: explicit cancel clip or fast blend-out policy

Spell classification is global in `SpellCastAnimationMap`: a normal spell selects a semantic motion,
a genuinely set-independent exception owns one fixed presentation, and a spell that intentionally
plays no cast animation owns an explicit `NoAnimation` assignment. `CombatAnimationSet.spellCastMotionBindings`
maps semantic motion to an animation family for the set's weapon pose. Do not add per-spell rows back
to a combat set, and do not use an absent map entry to mean no animation.

Direct casts are classified as `Direct1H` or `Direct2H` on the spell. Daggers and Staff currently
bind `Direct2H` to `MagicAttackDirect2H02`; other sets intentionally omit that binding and resolve
`Direct2H` through their `Direct1H` family plus `oneHandedCastHand`. Never bind
`MagicAttackDirect2H01`.

`Ground` is the semantic ground-directed gesture and every current combat set binds it to
`MagicAttackGround01`. It remains an animation classification: do not infer it merely because a
spell uses point or ground gameplay targeting.

The resolver still produces a `WeaponSpellAnimationEntry` runtime value. Its ground/air clips are either composed from a family or copied from a fixed global exception. Standing, moving, channeled, rooted, and interrupted presentation policy remains a separate axis.

Current cast lower-body recovery is intentionally narrow:

- `playbackLayer = UpperBody` starts the cast on the masked upper-body layer while locomotion continues.
- `playbackLayer = UpperBodyWhileMoving` preserves the legacy moving-cast behavior: stationary playback uses the full-body spell layer, while moving playback uses the upper-body layer.
- `playbackLayer = LeftGesture` uses masked pelvis/spine/left-arm gesture playback. It does not route through the full-body spell layer.
- `playbackLayer = RightGesture` is the mirrored masked pelvis/spine/right-arm path.
- `requiresCombatStance` and `combatEntryMode` control whether combat stance is requested before playback, after playback starts, or not at all.
- Full-body spell actions start on the `SpellAction` layer.
- `lowerBodyUnlockAtSeconds` is measured in seconds against the selected ground/air spell clip. It is an earliest eligibility time, not a command to fade the lower body immediately. If the player is not moving, the full-body cast keeps its lower-body settling. Unset or invalid values fall back to the selected clip length.
- `lowerBodyBlendOutSeconds` fades the `SpellAction` layer after lower-body unlock. Negative values use the default blend; `0` means immediate release.
- `visualInterruptibleAtSeconds` is measured in seconds against the selected ground/air spell clip. It controls when an active spell presentation may be replaced cleanly by eligible incoming combat presentation; unset or invalid values fall back to the selected clip length.
- At unlock, the runtime continues the same spell bank clip/time on `UpperBodySpellActionN`; authors do not pick a recovery layer or spell slot manually.

## Prediction And Replay

For locally predicted actions, presentation phase timestamps should anchor to the original predicted action start time.

When the matching authoritative event arrives and is classified as a duplicate/replay, it should not re-anchor lower-body unlock or visual interruption clocks. If the authoritative result rejects or materially corrects the predicted action, that correction path may cancel, restart, or replace presentation explicitly.

## Deprecated Terminology

Avoid new UI or code labels that say `Execution`, `Attack Environment`, or `execution environment` for ordinary melee eligibility.

The current serialized field `aerialExecutionMode` means caster movement-state eligibility:

```text
Grounded Only
Grounded Or Airborne
Airborne Only
```

Keep serialized compatibility, but editor labels and docs should move toward `Caster Movement State Requirement`.

## LLM And Developer Guardrails

Before changing combat animation behavior, read:

1. `docs/combat-authoring-contract.md`
2. `docs/combat-animation-authoring-contract.md`
3. `docs/animation-system-audit-2026-07-02.md` (the current animation work of record; the former event-timing migration plan is archived under `docs/archive/2026-05-stale-plans/` and must not be resumed)

Rules:

- Do not infer behavior from Animator graph layout alone.
- Do not add Animator parameters without updating validation.
- Do not add combat Animator transitions as the primary source of action sequencing unless this contract says the controller owns that sequence.
- Do not put full-body combat clips on an unmasked full-weight layer through recovery unless the action declares that lower body remains locked.
- Do not reinterpret Hit Windows as visual interruption timing.
- Do not fix gliding by random transition tuning.
- Do not enable root motion to fix server-authoritative movement mismatches.
- Do not delete or regenerate `CombatAnimationSet` assets to make a refactor easier.
- Do not rename authored strike ids or runtime slot ids without updating progression, manifest export, and validation.

## Target Validation

These checks do not all exist yet. Phase 2 of `docs/archive/2026-05-stale-plans/combat-animation-migration-plan-2026-05-04.md` adds the missing validation. The target validation set is:

- every `CombatAnimationSet` asset has a declared combat profile id
- every combat profile in progression resolves to a combat animation set
- every melee ability action id resolves to an authored strike id
- every exported melee strike has at least one resolved hit window
- every hit window resolves inside the timing reference
- every combat clip with lower-body unlock has `lowerBodyUnlockAtSeconds <= timingReferenceLength`
- every combat clip with visual interruption has `visualInterruptibleAtSeconds <= timingReferenceLength`
- when both are set, `lowerBodyUnlockAtSeconds <= visualInterruptibleAtSeconds`
- every movement action declares gameplay-owned displacement
- every Animator layer is declared in this contract or explicitly marked legacy during migration
- every Animator parameter is referenced by code, validation, or an explicit legacy allowlist
- no stale combat state remains in the Animator Controller without an owner
