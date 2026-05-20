# Animation Implementation Plan

## Progress

| Phase | Status |
|-------|--------|
| Phase 1 — Draw/Sheathe Transitions | ✅ Complete |
| Phase 2 — 2D Directional Locomotion | ✅ Complete |
| Phase 3 — Hit Reactions | ✅ Complete |
| Phase 4 — Death Animation | ✅ Complete |

---

## Phase 1 — Draw/Sheathe Transitions

**Goal:** Play transition animations when toggling combat stance instead of crossfading instantly.

### C# Changes

**`CombatAnimationSet.cs`** — add two clip fields under the Locomotion header:
```
public AnimationClip? drawWeapon;
public AnimationClip? sheathWeapon;
```
And two new slot names in the override map inside `PlayerAnimator.ApplyAnimationSet()`:
```
if (set.drawWeapon   != null) clipMap["slot_draw_weapon"]   = set.drawWeapon;
if (set.sheathWeapon != null) clipMap["slot_sheath_weapon"] = set.sheathWeapon;
```

### Unity Editor Steps

1. **Create 2 slot clips** in `Assets/Arena/Content/Animation/Slots/`:
   - `slot_draw_weapon` (1-frame placeholder)
   - `slot_sheath_weapon` (1-frame placeholder)

2. **Add 2 transition states** to `Arena_Character.controller` Base Layer:
   - `DrawWeapon` — clip: `slot_draw_weapon`, loop = false
   - `SheathWeapon` — clip: `slot_sheath_weapon`, loop = false

3. **Wire transitions** (replace the direct `IdleWalkRunBlend ↔ IdleCombat` connection):
   - `IdleWalkRunBlend → DrawWeapon`: condition `InCombat = true`, transition duration 0.1
   - `DrawWeapon → IdleCombat`: Exit Time = 0.95, duration 0.05
   - `IdleCombat → SheathWeapon`: condition `InCombat = false`, transition duration 0.1
   - `SheathWeapon → IdleWalkRunBlend`: Exit Time = 0.95, duration 0.05

4. **Assign clips** in the `SwordAndShield` CombatAnimationSet asset:
   - `drawWeapon` → `Idle_to_Idle_Combat.anim`
   - `sheathWeapon` → `Idle_Combat_to_Idle.anim`

### Verification
- Press G: character plays draw animation then settles into combat idle
- Press G again: character plays sheathe animation then returns to normal idle
- Running while toggling: transitions still fire (they're Any State driven, not from Idle only)

---

## Phase 2 — 2D Directional Locomotion

**Goal:** Character faces forward and strafes/backpedals with directional clips instead of always playing the forward-run clip.

### C# Changes

**`PlayerAnimator.cs`**

Add parameter hashes:
```csharp
private static readonly int VelocityXHash = Animator.StringToHash("VelocityX");
private static readonly int VelocityZHash = Animator.StringToHash("VelocityZ");
```

Replace `horizontalSpeed` block in `Update()` with local-space velocity:
```csharp
Vector3 worldVel = (pos - _prevPosition) / Mathf.Max(Time.deltaTime, 0.001f);
Transform basis = _motionSource ?? transform;
float rawX = Vector3.Dot(worldVel, basis.right);
float rawZ = Vector3.Dot(worldVel, basis.forward);

const float maxSpeed = 6f;  // tune to match motor run speed
float velX = Mathf.Clamp(rawX / maxSpeed, -1f, 1f);
float velZ = Mathf.Clamp(rawZ / maxSpeed, -1f, 1f);
float speed = new Vector3(rawX, 0f, rawZ).magnitude;

const float smoothing = 10f;
_smoothSpeed = Mathf.Lerp(_smoothSpeed, speed, 1f - Mathf.Exp(-smoothing * Time.deltaTime));

_animator.SetFloat(SpeedHash, _smoothSpeed);
_animator.SetFloat(MotionSpeedHash, _smoothSpeed > 0.1f ? 1f : 0f);
_animator.SetFloat(VelocityXHash, velX);
_animator.SetFloat(VelocityZHash, velZ);
```

**`CombatAnimationSet.cs`** — replace `locomotionWalk` / `locomotionRun` with directional sets:
```csharp
[Header("Locomotion — Normal (10 walk + 10 run directions)")]
public AnimationClip? walkF;
public AnimationClip? walkFL45;
public AnimationClip? walkFR45;
public AnimationClip? walkL90;
public AnimationClip? walkR90;
public AnimationClip? walkBL135;
public AnimationClip? walkBR135;
public AnimationClip? walkBL90;
public AnimationClip? walkBR90;
public AnimationClip? walkB180;

public AnimationClip? runF;
public AnimationClip? runFL45;
public AnimationClip? runFR45;
public AnimationClip? runL90;
public AnimationClip? runR90;
public AnimationClip? runBL135;
public AnimationClip? runBR135;
public AnimationClip? runBL90;
public AnimationClip? runBR90;
public AnimationClip? runB180;

[Header("Locomotion — Combat (10 walk + 10 run directions)")]
public AnimationClip? walkCombatF;
// ... same 10 directions
public AnimationClip? runCombatF;
// ... same 10 directions
```

Update `ApplyAnimationSet()` clip map with 40 new slot entries (e.g. `slot_walk_F`, `slot_run_combat_BL135`, etc.).

### Unity Editor Steps

1. **Add parameters** to `Arena_Character.controller`: `VelocityX` (Float), `VelocityZ` (Float)

2. **Replace IdleWalkRunBlend** with a 2D Freeform Directional blend tree:
   - Parameters: VelocityX, VelocityZ
   - 11 positions: (0,0) = slot_loco_idle, then 10 walk clips at unit-circle positions scaled ~0.5, then 10 run clips at unit-circle positions scaled 1.0
   - Clip positions (X, Z): F=(0,1), FL45=(-0.7,0.7), FR45=(0.7,0.7), L90=(-1,0), R90=(1,0), BL135=(-0.7,-0.7), BR135=(0.7,-0.7), BL90=(-1,0) [at 0.5 radius for walk], B180=(0,-1)

3. **Repeat for IdleCombat** blend tree with combat directional clips.

4. **Create 40 slot clips** in `Assets/Arena/Content/Animation/Slots/Directional/`:
   - `slot_walk_F`, `slot_walk_FL45`, ... (10 walk)
   - `slot_run_F`, `slot_run_FL45`, ... (10 run)
   - `slot_walk_combat_F`, ... (10 walk combat)
   - `slot_run_combat_F`, ... (10 run combat)

5. **Assign all 40 clips** in the SwordAndShield asset from the animation pack.

### Verification
- Moving forward: forward clip plays
- Strafing left/right: lateral clips play, character faces original direction
- Backpedaling: backward clip plays
- Same in combat mode

---

## Phase 3 — Hit Reactions

**Goal:** Play directional flinch animations when the local player takes damage.

### C# Changes

**`PlayerAnimator.cs`** — add parameter hashes and public method:
```csharp
private static readonly int TriggerHitFHash = Animator.StringToHash("TriggerHitF");
private static readonly int TriggerHitBHash = Animator.StringToHash("TriggerHitB");
private static readonly int TriggerHitLHash = Animator.StringToHash("TriggerHitL");
private static readonly int TriggerHitRHash = Animator.StringToHash("TriggerHitR");

public void TriggerHit(Vector3 hitDirection, Vector3 characterForward)
{
    if (_animator == null || _overrideController == null) return;

    float angle = Vector3.SignedAngle(characterForward, hitDirection, Vector3.up);
    int triggerHash = angle switch
    {
        < -135f or > 135f => TriggerHitBHash,
        < -45f            => TriggerHitLHash,
        < 45f             => TriggerHitFHash,
        _                 => TriggerHitRHash,
    };
    _animator.SetTrigger(triggerHash);
}
```

**`PlayerEntity.cs`** — expose:
```csharp
public void TriggerHit(Vector3 hitDirection) =>
    _animator?.TriggerHit(hitDirection, GameObject.transform.forward);
```

**`EntityRegistry.cs`** (or wherever spell damage events are handled) — call `entity.TriggerHit(sourceDirection)` when local player receives damage.

**`CombatAnimationSet.cs`** — add hit clip fields:
```csharp
[Header("Hit Reactions")]
public AnimationClip? hitF;
public AnimationClip? hitB;
public AnimationClip? hitL;
public AnimationClip? hitR;
public AnimationClip? hitCombatF;
public AnimationClip? hitCombatB;
public AnimationClip? hitCombatL;
public AnimationClip? hitCombatR;
```

### Unity Editor Steps

1. **Create Avatar Mask** `Assets/Arena/Content/Animation/HitReactionMask.mask` — enable full body (or upper + spine only to allow locomotion underneath).

2. **Add Layer 2** `HitReaction` to `Arena_Character.controller`:
   - Avatar Mask: HitReactionMask, Weight: 1.0, Blending: Override
   - States: `Empty` (default), `HitF`, `HitB`, `HitL`, `HitR`
   - Each hit state uses a slot clip, loop = false, Exit Time = 0.9, transition to Empty 0.1s
   - Parameters: TriggerHitF, TriggerHitB, TriggerHitL, TriggerHitR (Trigger)
   - Any State → HitF/B/L/R on respective trigger

3. **Create 4 slot clips** in `Assets/Arena/Content/Animation/Slots/`: `slot_hit_F`, `slot_hit_B`, `slot_hit_L`, `slot_hit_R`

4. **Assign clips** in SwordAndShield asset.

### Verification
- Take damage: flinch animation plays from hit direction
- Running while taking a hit: legs continue, upper body flinches (if mask is upper-only)

---

## Phase 4 — Death Animation

**Goal:** Play a full-body death animation when `IsAlive = false`.

### C# Changes

No new C# needed — `PlayerAnimator.SetDead(bool)` and `IsDeadHash` already exist.

Verify `EntityRegistry` calls `entity.SetState(playerState)` which calls `SetDead(!state.Alive)` — this is already wired.

**`CombatAnimationSet.cs`** — add death clip field:
```csharp
[Header("Death")]
public AnimationClip? death;
```

### Unity Editor Steps

1. **Add death state** to `Arena_Character.controller` Base Layer:
   - State: `Death`, clip: `slot_death`, loop = false, speed = 1.0
   - Transition: Any State → Death: condition `IsDead = true`, duration 0.2, Can Interrupt Self = false
   - No transition out (stay on last frame)

2. **Create slot clip** `Assets/Arena/Content/Animation/Slots/slot_death`

3. **Assign clip** in SwordAndShield asset: `Hit_Combat_Death.anim` (or `Hit_Death.anim` for non-combat)

### Verification
- Player dies: death animation plays and holds on last frame
- Player respawns: `SetDead(false)` fires, character snaps back to locomotion (add `IsDead = false` transition to `IdleWalkRunBlend` with instant duration if needed)

---

## Deferred (Blocked on Other Systems)

| Feature | Blocker |
|---------|---------|
| Dodge / Roll | Needs dodge input + movement mechanic |
| Melee attacks | Needs melee combat system |
| Blocking | Needs melee combat system |
| Turn-in-place (90°/180° clips) | Nice-to-have, no blocker — low priority |
