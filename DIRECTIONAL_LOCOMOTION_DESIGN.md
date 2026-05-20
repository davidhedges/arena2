# Phase 2: 2D Directional Locomotion — Final Design

## What stays

- 2D Freeform Directional blend tree, one per stance (normal, combat)
- `PredictedVelocity` for local player, position delta for remote
- Facing basis from `PredictedFacingYaw` for local player, `transform.forward` for remote
- Dead zone at magnitude < 0.05
- `Speed` removed; `MotionSpeed` is continuous [0,1]
- Smoothing (k=10) on normalized inputs
- `DirectionalClipSet` struct

---

## What changes

**Naming:** Drop the mixed angle/direction convention. Use compass directions — 8 points, no ambiguity.

**Clip count:** 8 directions × 2 tiers (walk/run) × 2 stances = 32 clips. Not 40.
`B_L_90` and `B_R_90` from the pack are dropped — they're at the same compass angle as `F_L_90`/`F_R_90` and would create duplicate positions in the blend tree.

**Normalization:** `maxRunSpeed` is not a magic number. The server defines `MOVE_SPEED = 7.0` in `server/src/movement.rs`. Mirror it as a named constant — one place to update when the server changes.

**Blend tree positions:** Mathematically exact. No hand-wavy adjustments.

---

## Naming scheme

### Compass directions (8 points)

| Name | Meaning | Pack clip suffix |
|------|---------|-----------------|
| `N` | Forward | `_F_0` |
| `NE` | Forward-right 45° | `_F_R_45` |
| `E` | Right | `_F_R_90` |
| `SE` | Back-right 45° | `_B_R_45` |
| `S` | Back | `_B_180` |
| `SW` | Back-left 45° | `_B_L_45` |
| `W` | Left | `_F_L_90` |
| `NW` | Forward-left 45° | `_F_L_45` |

`B_L_90` and `B_R_90` from the pack are **not used** — they map to the same angles as W and E.

### `DirectionalClipSet` struct fields

```csharp
[Serializable]
public struct DirectionalClipSet
{
    public AnimationClip? n, ne, e, se, s, sw, w, nw;
}
```

### Slot names in the controller

```
slot_walk_N     slot_walk_NE    slot_walk_E    slot_walk_SE
slot_walk_S     slot_walk_SW    slot_walk_W    slot_walk_NW

slot_run_N      slot_run_NE     slot_run_E     slot_run_SE
slot_run_S      slot_run_SW     slot_run_W     slot_run_NW

slot_walk_combat_N  ...  slot_walk_combat_NW
slot_run_combat_N   ...  slot_run_combat_NW
```

### ApplyAnimationSet mapping (one set — repeat for walk, walkCombat, run, runCombat)

```csharp
static void MapDirectionalSet(Dictionary<string, AnimationClip> clipMap,
    string prefix, DirectionalClipSet set)
{
    if (set.n  != null) clipMap[$"{prefix}_N"]  = set.n;
    if (set.ne != null) clipMap[$"{prefix}_NE"] = set.ne;
    if (set.e  != null) clipMap[$"{prefix}_E"]  = set.e;
    if (set.se != null) clipMap[$"{prefix}_SE"] = set.se;
    if (set.s  != null) clipMap[$"{prefix}_S"]  = set.s;
    if (set.sw != null) clipMap[$"{prefix}_SW"] = set.sw;
    if (set.w  != null) clipMap[$"{prefix}_W"]  = set.w;
    if (set.nw != null) clipMap[$"{prefix}_NW"] = set.nw;
}

// Usage:
MapDirectionalSet(clipMap, "slot_walk",         set.walk);
MapDirectionalSet(clipMap, "slot_run",          set.run);
MapDirectionalSet(clipMap, "slot_walk_combat",  set.walkCombat);
MapDirectionalSet(clipMap, "slot_run_combat",   set.runCombat);
```

---

## Final blend tree coordinate table

**Freeform Directional, one tree per stance. Parameters: VelocityX (X axis), VelocityZ (Y axis).**

Walk radius = 0.5 · (sin/cos of angle)  
Run radius  = 1.0 · (sin/cos of angle)  
sin(45°) = cos(45°) = √2/2 ≈ 0.7071 → at walk radius: 0.354

| Slot | VelocityX | VelocityZ |
|------|-----------|-----------|
| `slot_loco_idle` | 0 | 0 |
| `slot_walk_N` | 0 | 0.5 |
| `slot_walk_NE` | 0.354 | 0.354 |
| `slot_walk_E` | 0.5 | 0 |
| `slot_walk_SE` | 0.354 | −0.354 |
| `slot_walk_S` | 0 | −0.5 |
| `slot_walk_SW` | −0.354 | −0.354 |
| `slot_walk_W` | −0.5 | 0 |
| `slot_walk_NW` | −0.354 | 0.354 |
| `slot_run_N` | 0 | 1 |
| `slot_run_NE` | 0.707 | 0.707 |
| `slot_run_E` | 1 | 0 |
| `slot_run_SE` | 0.707 | −0.707 |
| `slot_run_S` | 0 | −1 |
| `slot_run_SW` | −0.707 | −0.707 |
| `slot_run_W` | −1 | 0 |
| `slot_run_NW` | −0.707 | 0.707 |

**17 positions. No duplicates. No ambiguity.**

Combat blend tree is identical using `slot_loco_idle_combat` + `slot_walk_combat_*` + `slot_run_combat_*`.

---

## Normalization: source of truth for run speed

The server defines `pub const MOVE_SPEED: f32 = 7.0` in `server/src/movement.rs`.

Mirror it in C# as a single named constant:

```csharp
// Must match server/src/movement.rs MOVE_SPEED.
// Update here when the server constant changes.
private const float BaseRunSpeed = 7f;
```

`MoveSpeedMultiplier` from the server is relative (1.0 = normal). It does NOT change `BaseRunSpeed` — it changes actual physics velocity. The blend tree already handles this correctly: a 0.5× slow reduces actual velocity to 3.5 m/s, which normalizes to 0.5 → walk clips play. No extra wiring needed.

---

## Final C# implementation

```csharp
// New hash fields — add to existing list, remove SpeedHash
private static readonly int VelocityXHash   = Animator.StringToHash("VelocityX");
private static readonly int VelocityZHash   = Animator.StringToHash("VelocityZ");
// Remove: SpeedHash

// New state fields
private float _smoothVelX;
private float _smoothVelZ;
private LocalPlayerStateProvider? _stateProvider; // cached — not GetComponent every frame

// Must match server/src/movement.rs MOVE_SPEED. Update here when server changes.
private const float BaseRunSpeed = 7f;

// In Initialize() — add:
_stateProvider = GetComponent<LocalPlayerStateProvider>();

// Replace the velocity + animator block in Update() with:
private void UpdateLocomotion()
{
    Vector3 worldVel = GetWorldVelocity();

    // Build facing basis from predicted yaw for local; transform for remote.
    Vector3 fwd, right;
    if (_isLocalPlayer && _stateProvider != null && _stateProvider.HasPredictedState)
    {
        float yaw = _stateProvider.PredictedFacingYaw;
        fwd   = new Vector3(Mathf.Sin(yaw), 0f,  Mathf.Cos(yaw));
        right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
    }
    else
    {
        fwd   = transform.forward;
        right = transform.right;
    }

    float targetVelX = Mathf.Clamp(Vector3.Dot(worldVel, right)   / BaseRunSpeed, -1f, 1f);
    float targetVelZ = Mathf.Clamp(Vector3.Dot(worldVel, fwd)     / BaseRunSpeed, -1f, 1f);

    // Dead zone: suppress idle "swimming" from near-zero drift
    if (new Vector2(targetVelX, targetVelZ).magnitude < 0.05f)
        targetVelX = targetVelZ = 0f;

    // k=10 → ~100ms to settle. Snappy without jitter.
    float t = 1f - Mathf.Exp(-10f * Time.deltaTime);
    _smoothVelX = Mathf.Lerp(_smoothVelX, targetVelX, t);
    _smoothVelZ = Mathf.Lerp(_smoothVelZ, targetVelZ, t);

    float smoothMag = new Vector2(_smoothVelX, _smoothVelZ).magnitude;

    _animator!.SetFloat(VelocityXHash,  _smoothVelX);
    _animator!.SetFloat(VelocityZHash,  _smoothVelZ);
    _animator!.SetFloat(MotionSpeedHash, smoothMag); // continuous — drives playback rate
}

private Vector3 GetWorldVelocity()
{
    // Always advance _prevPosition so remote fallback stays current
    Vector3 pos = (_motionSource ?? transform).position;
    Vector3 posDelta = (pos - _prevPosition) / Mathf.Max(Time.deltaTime, 0.001f);
    _prevPosition = pos;
    posDelta.y = 0f;

    // Local player: predicted velocity avoids reconciliation spikes
    if (_isLocalPlayer && _stateProvider != null && _stateProvider.HasPredictedState)
    {
        var vel = _stateProvider.PredictedVelocity;
        vel.y = 0f;
        return vel;
    }

    return posDelta;
}
```

---

## Animator changes

**Parameters:**
- Delete `Speed`
- Add `VelocityX` (Float, default 0)
- Add `VelocityZ` (Float, default 0)
- `MotionSpeed` stays, no change needed in controller

**`IdleWalkRunBlend`:** Rebuild as 2D Freeform Directional, 17 positions from table above.

**`IdleCombat`:** Rebuild as 2D Freeform Directional, same 17 positions with combat slots.

**UpperBody layer:** No changes.

---

## Slot clips to create

`Assets/Arena/Content/Animation/Slots/Directional/` — 32 clips total:

```
slot_walk_N     slot_walk_NE    slot_walk_E     slot_walk_SE
slot_walk_S     slot_walk_SW    slot_walk_W     slot_walk_NW
slot_run_N      slot_run_NE     slot_run_E      slot_run_SE
slot_run_S      slot_run_SW     slot_run_W      slot_run_NW

slot_walk_combat_N   ... slot_walk_combat_NW  (8 clips)
slot_run_combat_N    ... slot_run_combat_NW   (8 clips)
```

---

## Pack clips to assign in SwordAndShield asset

| Field | Pack clip |
|-------|-----------|
| walk.n | Walk_Loop_F_0 |
| walk.ne | Walk_Loop_F_R_45 |
| walk.e | Walk_Loop_F_R_90 |
| walk.se | Walk_Loop_B_R_45 |
| walk.s | Walk_Loop_B_180 |
| walk.sw | Walk_Loop_B_L_45 |
| walk.w | Walk_Loop_F_L_90 |
| walk.nw | Walk_Loop_F_L_45 |
| run.n | Run_Loop_F_0 |
| ... (same pattern) | |
| walkCombat.n | Walk_Combat_Loop_F_0 |
| ... (same pattern) | |
| runCombat.n | Run_Combat_Loop_F_0 |
| ... (same pattern) | |

---

## Implementation checklist

**C# — PlayerAnimator.cs**
- [ ] Remove `SpeedHash` field and all usages
- [ ] Add `VelocityXHash`, `VelocityZHash` static fields
- [ ] Add `_smoothVelX`, `_smoothVelZ`, `_stateProvider` fields
- [ ] Cache `_stateProvider` in `Initialize()`
- [ ] Add `BaseRunSpeed = 7f` constant with comment pointing to server file
- [ ] Replace velocity block in `Update()` with `UpdateLocomotion()` call
- [ ] Add `UpdateLocomotion()` and `GetWorldVelocity()` methods
- [ ] Change `MotionSpeedHash` feed to `smoothMag`

**C# — CombatAnimationSet.cs**
- [ ] Add `[Serializable] DirectionalClipSet` struct (8 compass fields)
- [ ] Remove `locomotionWalk`, `locomotionRun` fields
- [ ] Add `walk`, `walkCombat`, `run`, `runCombat` fields of type `DirectionalClipSet`
- [ ] Add `MapDirectionalSet` helper in `PlayerAnimator.ApplyAnimationSet()`

**Unity Editor — Arena_Character.controller**
- [ ] Delete `Speed` parameter
- [ ] Add `VelocityX` Float parameter
- [ ] Add `VelocityZ` Float parameter
- [ ] Rebuild `IdleWalkRunBlend` — 2D Freeform Directional, 17 positions
- [ ] Rebuild `IdleCombat` — 2D Freeform Directional, 17 positions (combat slots)

**Unity Editor — slot clips**
- [ ] Create `Assets/Arena/Content/Animation/Slots/Directional/` folder
- [ ] Create 32 one-frame placeholder clips

**SwordAndShield asset**
- [ ] Assign all 32 clips per table above

---

## Deferred

| Item | Reason |
|------|--------|
| B_L_90 / B_R_90 clips | Redundant compass angles — same as W/E |
| Start/Stop per direction | Requires velocity-threshold state machine and one-shot playback |
| Walk_Block directional | Needs melee block mechanic |
| Walk_to_Walk_Combat (while moving) | Low priority transition polish |
| Dash animation | Blocked on dash mechanic |
