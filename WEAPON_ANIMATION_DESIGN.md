# Weapon Animation System Design

> Historical design discussion. References to the old `CastAnimationMap` type are superseded by
> `docs/spell-cast-animation-stitching-2026-07-09.md`; that compatibility stub has been removed.

---

## What in the Revised Design Is Solid

- Canonical `AnimatorController` owned by the team — correct
- `AnimatorOverrideController` for clip variation — correct, defended below
- Fixed hash constants, no string lookups, no `_has*` guards — correct
- `CombatAnimationSet` as clip map, not controller reference — correct
- `SetWeaponState(set, weaponReady)` decoupled from class — correct
- Input gating stays on `LocalCombatState.ActiveCast` / GCD — already correct in this codebase, and animation events must never replace it

---

## What Is Still Hand-Wavy in the Revision

- Layer interaction was not fully specified (additive vs override, what beats what)
- Timing model was vague (`castDefaultEffectTime = 0.5f` with no explanation of what it's for)
- Recovery/input gate via animation events was suggested — that would be wrong for this project
- `CastAnimationType` enum is fine today but the extension path was underspecified

---

## AnimatorOverrideController: Why This Tool, Not the Alternatives

**One controller with all clips directly assigned:**
You end up with states like `idle_sword`, `idle_staff`, `idle_daggers`, `idle_unarmed`. N combat profiles × M animation states = NM states to maintain, NM transition edges to update when you add a state. Not viable past two combat profiles.

**Sub-state machines per combat profile family:**
Better organization, but the topology still diverges per combat profile. You need entry nodes routing to the correct sub-state machine, which means conditional transitions or cross-machine jumps in script. You lose the guarantee that all classes have identical interrupt behavior because the transition graphs are structurally different. This is the same problem as controller-swap but one level up.

**Playables API:**
Gives you full programmatic control — runtime graph construction, dynamic layer count, mixing from arbitrary clip sources. The cost: you implement all transition logic, blending, exit timing, and interrupt priority that the Animator gives you for free. Playables is the right choice when you need procedural animation, dynamic layer counts, or mixing from sources the Animator can't express. None of those apply here. The canonical controller's state machine IS the product; Playables would just recreate it in code.

**`AnimatorOverrideController`:**
Wraps a base controller and replaces named clips. The state machine — layers, masks, transitions, blend trees, exit times — is completely unchanged. Crucially, swapping clips via `ApplyOverrides` does NOT reset animator state, unlike replacing `runtimeAnimatorController`. The character stays in `State_CastDefault`; the clip playing in that state just changes. This is exactly the property this system needs: identical behavior, variable movement and action aesthetics.

---

## Layer Structure and Transition Policy (Concrete)

```
Layer 0 — Locomotion       Override   Full body      Weight 1.0
Layer 1 — UpperBody        Override   Upper body*    Weight 1.0
Layer 2 — HitReaction      Override   Full body      Weight 1.0
Layer 3 — DeathOverride    Override   Full body      Weight 1.0
```

*Upper body avatar mask: spine and above, including both arms. Legs and root remain on Layer 0.

**None of these are additive.** Additive layers accumulate bone rotations on top of whatever is below — appropriate for breathing, procedural lean, subtle secondary motion. Discrete state machine behaviors (hit reactions, death) need Override so they fully own the bones they drive.

### Priority

Higher layer index wins on any overlapping bones. Layer 3 (full body) overrides everything. Layer 2 (full body) overrides Layer 1 and Layer 0. Layer 1 (upper body only) overrides Layer 0 on the upper body; legs are still driven by Layer 0.

### What can interrupt what

| Event | What fires | Effect |
|---|---|---|
| `TriggerCastDefault` etc. | Layer 1 transition | Upper body enters cast state. Legs keep running. |
| `TriggerHitLight` | Layer 2 transition | Upper body flinch plays over Layer 1. Cast continues on Layer 1. |
| `TriggerStagger` | Layer 2 transition | Full body stagger. Server decision drives whether cast is also interrupted. |
| `TriggerCastInterrupt` | Layer 1 transition | Immediate return to `State_UpperBodyEmpty`. Zero transition duration. |
| `IsDead = true` | Layer 3 transition | Full body death at weight 1. Overrides all other layers visually. |

### Specific interaction cases

**Hit reaction during cast:**
- Light hit: Layer 2 fires, plays on top of Layer 1 cast clip. Cast state on Layer 1 continues. The cast is not interrupted. The server has not cleared `ActiveCast`. Visual: brief flinch, cast resumes. Correct.
- Stagger: Layer 2 fires full body stagger. Simultaneously, if the server decided the hit interrupts the cast (clears `ActiveCast` row), the client's `LocalCombatState.OnActiveCastDelete` fires → call `entity.EndCast()` → `TriggerCastInterrupt` on Layer 1, `IsCasting = false`. The visual (Layer 2 stagger) and the game logic (cast ended, Layer 1 emptied) are driven by separate signals but happen in the same frame. Layer 2 does not determine whether the cast was interrupted. The server does.

**Death during cast:**
- Server sets player dead → `PlayerState.Alive = false`
- Client calls `SetDead(true)` and `EndCast()` in the same `SetState` call
- `IsDead = true` → Layer 3 transitions to death state (full body, weight 1, wins over everything)
- `TriggerCastInterrupt` → Layer 1 transitions to empty
- Both are in flight simultaneously. Layer 3 visually dominates immediately. Layer 1 cleans up in the background. No visible conflict.

**Transition durations:**
- Layer 1 → cast: 0.1s blend in, exit via `ExitTime` at normalized 0.9 (for non-looping casts)
- Layer 1 cast → interrupt: 0.0s (instant). A cast interrupt must be visually immediate.
- Layer 2 → hit reaction: 0.05s blend in, exit via `ExitTime`
- Layer 3 → death: 0.15s blend in. No exit transition — death is terminal until `SetDead(false)` is called on respawn.

---

## Timing: What It's Actually For in This Game

`SpellInputHandler.CanAttemptCast()` already gates on `LocalCombatState.ActiveCast.endMs` and GCD. These come from SpacetimeDB rows. The server determines when a new cast can start. **Animation events must not be used to gate input in this project.**

The places where animation timing matters are cosmetic only:
- When to spawn the VFX (fireball leaves the hand)
- When to play the impact SFX
- Footsteps

That means `effectTime: float` (normalized 0-1 within the clip) is sufficient for this project's current needs. It answers: "at what point in the animation does the visual event happen?" It does not answer: "when does the server resolve damage?" The server already answers that.

**What `castDefaultEffectTime` should be named and where it lives:**

It lives on `CombatAnimationSet`, not on a spell definition, because it's relative to the clip duration, not the server cast duration. Different weapon packs may have the same spell at a different animation frame. Keep it per-set, per-action-slot:

```csharp
[Header("VFX Timing (normalized 0-1 within clip)")]
public float castDefaultEffectTime = 0.5f;
public float castUpEffectTime      = 0.45f;
public float meleeSwingEffectTime  = 0.4f;
```

**What would require richer timing:**
If you add abilities where server cast duration is variable (charged attacks where hold time affects resolution time), and you want the VFX to track the charge level, you need a different model — the VFX is driven by game state, not a normalized animation time. But that's a charged attack feature, not an animation system feature. Cross that bridge when you add charged attacks.

---

## `CastAnimationType`: The Next Step

The enum is correct for now. The extension path: replace `CastAnimationMap.Get(spellKind) → CastAnimationType` with `CastAnimationMap.Get(spellKind) → AbilityAnimationDef`.

```csharp
[CreateAssetMenu(menuName = "Arena/Ability Animation Def")]
public class AbilityAnimationDef : ScriptableObject
{
    public int triggerHash;         // canonical trigger hash — set in the Inspector using a wrapper
    public Handedness handedness;   // Left, Right, Both — drives which bone socket VFX uses
    public float effectTime;        // normalized time for VFX (overrides CombatAnimationSet default)
    public bool lockLocomotion;     // cosmetic: should locomotion blend down during cast?
}
```

`CastAnimationMap` becomes:
```csharp
private static readonly Dictionary<string, AbilityAnimationDef> Map = new() { ... };
public static AbilityAnimationDef? Get(string spellKind) => Map.GetValueOrDefault(spellKind);
```

New spells get their own asset. No enum extension, no recompile for designers. Multiple spells can share the same trigger (same animation posture, different game effects) without coupling them in code.

This is the correct next step. Do not do it now — the enum is fine until you have more than ~5 distinct animation postures.

---

## Where the Architecture Breaks: Explicit Boundaries

**Dual wield asymmetry:**
Two simultaneous hand animations that differ require two clip slots: `slot_cast_right` and `slot_cast_left`, with corresponding states and transitions in the canonical controller. `AnimatorOverrideController` handles per-weapon clip assignment once the slots exist. The controller needs to grow before dual wield works. Not a fundamental limitation, just setup work.

**Spear/polearm locomotion:**
Different idle stance, different walk cycle — these are just different clips in the locomotion slots. OverrideController handles this with no structural changes. Not a limitation.

**Crouched rogue movement:**
If "crouched" means different clips but same blend structure — fine, OverrideController handles it.
If "crouched" requires different locomotion blend tree thresholds or 8-directional vs 1D blending — OverrideController cannot change blend tree structure. You'd need a `Crouched` bool in the canonical controller that branches inside the locomotion blend tree: `Crouched = false` uses the standard 1D speed blend; `Crouched = true` uses a separate blend tree with crouch-specific thresholds. OverrideController still swaps the clips inside each sub-tree. This means: plan the canonical controller's locomotion blend tree to have a crouched branch before you need it, not after.

**Charged heavy attacks:**
Multi-phase state: hold → charge loop → execute on release. Requires 3 new canonical controller states (`ChargeStart`, `ChargeLoop`, `ChargeRelease`) and input-driven transitions between them. OverrideController assigns clips to those states per combat profile. The pattern is correct — new interaction model = new canonical controller states. Adding these states later is low-risk as long as you own the controller.

**Abilities with bespoke animation structure (combo chains, conditional exits):**
A 3-hit combo needs 3 states and combo-window transitions. OverrideController swaps the per-hit clips. Manageable.
A channel ability with an interruptible loop needs a `ChargingLoop` state with a looping clip, interrupted by `TriggerCastInterrupt` or release. One new state, clip per combat profile. Fine.
A teleport with a bespoke mid-air pose: one new state. Fine.

**The honest boundary:** OverrideController handles everything where animation categories (states) are stable and only visual aesthetics (clips) vary per combat profile. It cannot handle cases where the interaction logic (state machine topology) differs per combat profile. In practice that means: if you are adding a genuinely new player action (charged attack, dodge roll, combo), the canonical controller grows. If you are adding a new combat profile that does existing actions differently, only clip assets are needed.

---

## Recovery and the Animation Event Question

The previous revision suggested `OnCastRecoveryStart` animation events gating input via an `_inRecovery` flag checked by `SpellInputHandler`. This was wrong for this project.

`SpellInputHandler.CanAttemptCast()` already gates on:
- `LocalCombatState.GcdStartMs + GcdDurationMs` (GCD, server-driven)
- `LocalCombatState.ActiveCast.endMs` (cast in progress, from `ActiveCast` SpacetimeDB row)

The server clears `ActiveCast` when the cast resolves. That's the recovery gate. Nothing in the animator should duplicate or replace it.

Animation events are appropriate for:
- Cosmetic VFX spawn (fire particle at normalized time 0.5)
- SFX triggers (cast sound, footsteps)
- IK hint activation (when the hand closes around a weapon)

Animation events are not appropriate for:
- Determining whether input is accepted
- Setting flags that game logic reads
- Anything that would have different behavior if the animator is culled offscreen

If the animator is culled, animation events don't fire. Input gates that rely on them would silently break for off-screen characters. The server-driven `ActiveCast` row fires regardless.

---

## Actual MVP Implementation for This Game

In order, nothing skipped:

**1. Build `Arena_Character.controller`**

Four layers as specified. Placeholder clips in each state (the default Unity empty clip is fine for now). Assign the upper body avatar mask to Layer 1. Wire the canonical parameter set as constants in `PlayerAnimator`. This replaces the existing `StarterAssetsThirdPerson.controller` as the character controller.

**2. Create `CombatAnimationSet.cs`**

Clip fields only. VFX timing floats. No controller reference, no string parameter mappings.

**3. Rewrite `PlayerAnimator`**

Remove all `_has*` bool guards. Remove `CacheAnimatorParameters` string matching. Replace with `AnimatorOverrideController` initialized from the canonical controller. Add `ApplyAnimationSet(CombatAnimationSet)` that batch-overrides clips. Hash constants are static readonly, computed once, never looked up by string at runtime.

**4. Port existing spell-to-trigger mapping to `CastAnimationMap`**

Static dictionary, `string → CastAnimationType` (enum). Same logic as today, just explicit rather than a switch in `PlayerAnimator`. No behavioral change.

**5. Add `player_class` to server `Player` table**

Hardcode `"WARRIOR"` in `client_connected` until class selection exists. Regenerate bindings.

**6. Create one `CombatAnimationSet` asset for Sword and Shield**

Assign clips from the pack into each slot. Fill in locomotion clips. Assign one cast clip to `castDefault` (the mage cast can be placeholder until warrior abilities are designed). Set VFX timing floats.

**7. Wire `EntityRegistry`**

Add `[SerializeField] WeaponAnimationRegistry _animRegistry` to `EntityRegistry`. On `Player` row insert/update for any entity, resolve `CombatProfileIds` from class, get the set, call `entity.SetCombatAnimationSet(set)`. `SetCombatAnimationSet` calls `_animator.ApplyAnimationSet(set)`.

**What this does not include yet:**

- `AbilityAnimationDef` assets (add when you have >5 distinct postures)
- Charged attack states in the canonical controller (add when you design the mechanic)
- Crouched locomotion branch (add when you design rogue class)
- Disarm/polymorph weapon state stack (add when you design those effects — the `SetWeaponState(set, ready)` API already supports it)

---

## Practical Implementation Pass

### 1. Ways This Will Go Wrong During Implementation

**`AnimatorOverrideController` initialized at the wrong time.**
If you call `new AnimatorOverrideController(animator.runtimeAnimatorController)` before the Animator component has evaluated its first frame, `runtimeAnimatorController` may be null or the default. Initialize in `Start()` or after the first `Update()`, not in `Awake()`. For remote players instantiated at runtime, initialize inside `ApplyAnimationSet` on first call with a null guard — not in a lifecycle method.

**`ApplyOverrides` uses the base controller's clip references as keys, not names.**
This is the most common `AnimatorOverrideController` mistake. The key in each `KeyValuePair` is the actual `AnimationClip` object from the base controller (the slot clip), not a string. If you pass the wrong clip object as a key, `ApplyOverrides` silently does nothing. The slot clips must be read from `_overrideController.GetOverrides(list)` or retrieved by iterating `_overrideController.animationClips`. Using string names only works via the indexer: `_overrideController["slot_loco_idle"] = newClip` — valid but slower in a loop. For batch apply, read the slots first, then replace.

**Avatar mask on Layer 1 not assigned.**
If you forget to assign the upper body avatar mask to the UpperBody layer, Layer 1 runs full body and fights Layer 0 on the legs. The character will have twitching legs during casts. Silent, visually confusing. Check: Layer 1 → Avatar Mask field in the Animator window.

**Exit Time transitions on Layer 1 with looping clips.**
If `slot_cast_default` is accidentally set to loop in the clip import settings, the state will never exit via `ExitTime`. The cast animation plays forever. Always set action clips (casts, hits, death) to non-looping in the clip inspector. Only locomotion clips should loop.

**`TriggerCastInterrupt` stacking.**
If `EndCast()` is called twice (e.g., from both `SetState` and a separate code path), the trigger queues a second interrupt that fires on the next cast, causing a one-frame skip. Use `ResetTrigger(TriggerCastInterrupt)` before `SetTrigger` in `EndCast()`. Or make `EndCast()` idempotent with a `_isCasting` bool guard.

**`AnimatorOverrideController` swap during active state resets the state.**
Calling `_animator.runtimeAnimatorController = overrideController` at any point after init resets the entire state machine to layer defaults. Only assign `runtimeAnimatorController` once, at init. All subsequent clip changes go through `ApplyOverrides` on the already-assigned `_overrideController`.

**Layer 2 HitReaction state never exits.**
If you set up Layer 2 but forget to add an `ExitTime` transition back to the empty state, every hit locks the character in the hit animation permanently. Check each action state in layers 2 and 3 has a transition out.

**Debugging silent failures.**
When a trigger fires but nothing plays: check the Animator window's live state view during Play mode — it shows which state each layer is in and pending triggers. Triggers that fire but find no valid transition are silently consumed. If the live view shows the trigger set but no transition, your transition condition is wrong (wrong layer, wrong state, wrong condition type).

---

### 2. MVP Simplification

Skip Layer 2 (HitReaction) entirely for now. Hit reactions are cosmetic and you have no melee-range combat yet. Add it when you have a use case.

**Minimum layers: 2**
```
Layer 0 — Locomotion    Override   Full body   Weight 1.0
Layer 1 — UpperBody     Override   Upper body  Weight 1.0
```

Add Layer 3 (Death) only if you want a distinct death pose instead of just disabling the renderer. Given that your current `SetState` already hides renderers on death, a death layer is optional for now.

**Minimum states:**

Layer 0:
- `BlendTree` (idle/walk/run on Speed)
- `Jump`
- `FreeFall`

Layer 1:
- `Empty` (default, no clip)
- `CastDefault`
- `CastUp`

That covers every current spell in `SpellInputHandler` via `CastAnimationType.Default` and `CastAnimationType.Up`. `ShieldBlock` and `MeleeSwing` states can be added when those interactions exist.

**Minimum parameters:**
```
Speed         Float
Grounded      Bool
Jump          Bool
FreeFall      Bool
IsDead        Bool
IsCasting     Bool
TriggerCastDefault  Trigger
TriggerCastUp       Trigger
TriggerCastInterrupt Trigger
```

`MotionSpeed` and `WeaponReady` can be added later. `MotionSpeed` is a quality-of-life blend tree refinement. `WeaponReady` is needed for disarm, which you don't have yet.

**Minimum `CombatAnimationSet` fields for MVP:**
```csharp
public AnimationClip locomotionIdle;
public AnimationClip locomotionWalk;
public AnimationClip locomotionRun;
public AnimationClip castDefault;
public AnimationClip castUp;
public float castDefaultEffectTime = 0.5f;
public float castUpEffectTime      = 0.45f;
```

Everything else (jump, freeFall, land, hitLight, death, meleeSwing) can default to null and be skipped in `TryOverride`. The slot clips in the base controller serve as fallbacks.

---

### 3. What Not to Build Yet

**`AbilityAnimationDef` ScriptableObject assets.** `CastAnimationType` enum covers all current spells. Add `AbilityAnimationDef` when you have abilities that need per-spell handedness, per-spell locomotion lockout, or per-spell timing that differs from the weapon default.

**Layer 2 HitReaction.** No melee combat yet. Add when you have a hit that warrants a visible flinch distinct from the hurt sound.

**`WeaponReady` bool and disarm/polymorph state stack.** Neither mechanic exists. The `SetWeaponState(set, ready)` API is already correct; just don't implement the stack.

**`MotionSpeed` parameter.** It's a blend tree refinement for motion-matched animation quality. The current `Speed` float is sufficient.

**`player_class`-based weapon resolution in `WeaponAnimationRegistry`.** Until you have more than one class, hardcode the set assignment in `EntityRegistry` directly. The registry asset is the right long-term home; it's premature until you have two classes.

**Death layer.** Your existing `SetState` already hides renderers on death. A death animation layer is a visual polish item, not a system requirement.

---

### 4. Canonical AnimatorController Checklist

Build this exactly. Nothing more.

**Parameters:**
- `Speed` — Float
- `Grounded` — Bool
- `Jump` — Bool
- `FreeFall` — Bool
- `IsDead` — Bool
- `IsCasting` — Bool
- `TriggerCastDefault` — Trigger
- `TriggerCastUp` — Trigger
- `TriggerCastInterrupt` — Trigger

**Layer 0 — Locomotion** (full body, Override, weight 1.0, no avatar mask)

States:
- `Grounded` — BlendTree, 1D on `Speed`, thresholds 0 / 2 / 5.5, clips: `slot_loco_idle` / `slot_loco_walk` / `slot_loco_run`. All three clips loop. **This is the default state.**
- `Jump` — clip: `slot_jump`, no loop
- `FreeFall` — clip: `slot_freefall`, loop

Transitions:
- `Grounded → Jump`: condition `Jump == true`, no exit time, duration 0.1
- `Jump → FreeFall`: condition `Grounded == false`, exit time 0.5, duration 0.1
- `FreeFall → Grounded`: condition `Grounded == true`, no exit time, duration 0.15
- `Jump → Grounded`: condition `Grounded == true`, no exit time, duration 0.1

**Layer 1 — UpperBody** (Override, weight 1.0, upper body avatar mask)

States:
- `Empty` — no clip, no loop. **Default state.**
- `CastDefault` — clip: `slot_cast_default`, no loop
- `CastUp` — clip: `slot_cast_up`, no loop

Transitions (all from Any State except where noted):
- `Any → CastDefault`: condition `TriggerCastDefault`, can interrupt self: false, duration 0.1
- `Any → CastUp`: condition `TriggerCastUp`, can interrupt self: false, duration 0.1
- `CastDefault → Empty`: exit time 0.85, duration 0.1
- `CastUp → Empty`: exit time 0.85, duration 0.1
- `Any → Empty`: condition `TriggerCastInterrupt`, duration 0.0 (instant)

The `TriggerCastInterrupt → Empty` transition must have higher priority than the cast trigger transitions. In Unity this means it appears first in the Any State transition list.

**Slot clips in the base controller** (placeholder clips — any clip works, they get overridden):
Assign the StarterAssets clips to each slot so the controller works before any `CombatAnimationSet` is applied:
- `slot_loco_idle` → StarterAssets idle clip
- `slot_loco_walk` → StarterAssets walk clip
- `slot_loco_run` → StarterAssets run clip
- `slot_jump` → StarterAssets jump clip
- `slot_freefall` → StarterAssets freefall clip
- `slot_cast_default` → any single pose clip (create a 1-frame clip if needed)
- `slot_cast_up` → same placeholder

**What is explicitly not in this controller:**
- No IK passes
- No hit reaction layer
- No death layer
- No MotionSpeed parameter
- No WeaponReady parameter
- No sub-state machines
- No blend tree for 8-directional movement
