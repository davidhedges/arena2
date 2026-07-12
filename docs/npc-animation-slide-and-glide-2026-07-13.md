# NPC animation slide & glide — findings (2026-07-13)

Two distinct presentation defects investigated for NPC models. The first is fixed and
verified live; the second is implemented and server-verified, with final live visual
confirmation still pending.

## 1. Hit-reaction slide (FIXED)

**Symptom.** When certain NPCs took damage, the visible model slid ~1–2 m backward
during the hit animation, then snapped back.

**Root cause.** Three vendor FBXs bake a *non-returning* translation into the `hit`
take's root **bone** (an ordinary bone curve, not exportable root motion):

| Model | `hit` root-bone net displacement |
|---|---|
| SlimeMan.fbx | 0 → −1.15 m (Z) |
| Abomination.fbx | 0 → −1.15 m (Z, identical reused curve) |
| ForestDemon.fbx | −0.01 → −1.74 m (Z) |

All three affected FBXs import as **Generic with no motion root node**, and
`NpcAnimationController` force-disables `applyRootMotion`
(`Assets/Arena/Runtime/Presentation/NpcAnimationController.cs`), so Unity applies the
baked curve literally to the skeleton. Gameplay pins only the prefab root each frame
(`NpcEntity.TickPresentation`, `Assets/Arena/Runtime/Entity/NpcEntity.cs:197`), so the
skeleton walks out from under the replicated root. The server applies no knockback;
the root GameObject never moves (verified live: `rootGoMaxDelta = 0` in all captures).

**Fix.** `Assets/Arena/Editor/NpcModelImportPolicy.cs` flattens the horizontal
root-bone position curves of exactly those three (model, clip) pairs at import.

- The policy overrides `GetVersion()`. **Bump it on every behavior change** — without
  it the asset database serves stale import artifacts, which masked a correct fix
  once during this investigation (both early "no improvement" reports were stale
  artifacts / testing a different NPC, not a wrong fix).
- Defect criterion: **non-returning** displacement (first ≠ last key). Clips whose
  root excursion returns to start are intentional animation and must not be listed —
  notably the Imp, whose root motion is its hover.

**Verification.**
- FBX-level: parsed the FBX binaries directly (python, struct+zlib) to dump per-take
  translation curves for every character FBX in all three catalog sources
  (`StylizedFantasyEnemyNPCBundle`, `…Bundle2`, `KoboldPack`).
- Artifact-level: editor probe read the *imported* clips (`AnimationUtility` on FBX
  sub-assets) post-reimport — all Root curves flat; sampled Animator playback of each
  prefab's `hit` state showed zero root-bone and mesh-bounds drift.
- Live: play-mode probe on a hostile `ABOMINATION_GN` — 3× `hit`, 3× `Death`
  captures, `rootGoMaxDelta = 0` and `rootBoneRelToRootGoMax = 0` in all of them.

**Scope check across all catalog models.** Kobolds and Zombie are Humanoid rigs →
root motion is extracted and discarded, immune. Every other model is Generic; only
the three above have a defective `hit` take.

**Watch-items** (non-returning displacement in other clips; one-line additions to the
policy's `InPlaceOverrides` table if ever reported):

- Imp `SpellB`: root ends ~0.75 m displaced → possible end-of-cast snap.
- DeepSeaLizard `Spell_A`: pelvis ends ~0.6 m displaced.
- SkeletonWarrior `Ready_Unarmed`: stray key pops the skeleton 2 m sideways for one
  frame (vendor glitch).
- `Death` takes displace the pelvis on most Generic models — intentional fall-over
  motion (authored on the pelvis, reads as falling). Deliberately not flattened;
  flattening would break death poses.

## 2. Gliding while moving + attacking (FIXED, live verification pending)

**Symptom.** Occasional gliding when an NPC (observed on Abomination) moves while its
attack animation plays.

**Root cause — shared server-side timing hole whose visible impact varies by model and
action.** The server already holds NPCs in place (face-only) during melee windup
(`server/src/npcs.rs:1362-1370`) and spell casts (npcs.rs:1373-1387). But NPC melee
is authored with **zero recovery**: `recovery_until = impact_at`
(`server/src/melee.rs:908-909`), and the pending-impact row is consumed at the top of
the tick the hit resolves — so chase resumes at full speed (Abomination: 4.5 m/s) the
same tick the hit lands, while the client's attack clip still plays its follow-through.
Before this fix, instant spells had no post-cast hold: the decision timer reset
immediately after execution.

The client cannot compensate: `NpcAnimationController` is a single-layer full-body
animator, so an attack state means the legs are not stepping. Per-rig upper-body
masks across ~37 heterogeneous generic rigs (quadrupeds, slimes, floaters) is the
wrong altitude.

**Implemented fix.** Extend the existing face-only hold through
an authored post-impact recovery window, server-side. The recovery must cover the
remainder of the client animation, not merely use one arbitrary duration:

- New catalog field: `attack_recovery_ms` per template with per-ability override,
  stored in `server/src/npc_catalog.shared.json` and its Rust structs. For each action,
  author and validate
  `recovery_ms >= max(0, longest_attack_clip_ms + presentation_margin_ms - windup_ms)`
  across every visual variant available to that template. A ~500 ms fallback may be
  reasonable for migrated rows, but it is not by itself the correctness contract.
  Add explicit Serde defaults for migrated rows and validation bounds; both catalog
  structs use `deny_unknown_fields`.
- Compute `hold_movement_until_micros = swing_start + windup_ms + recovery_ms` in the
  caller-owned `NpcCombatRuntime`. Pass the runtime mutably to
  `begin_npc_melee_swing`, or return the deadline and apply it before the caller's
  final `upsert_npc_combat_runtime`; updating the table only inside
  `begin_npc_melee_swing` would be overwritten by that final upsert. Apply the same
  rule after successful spell execution: instant spells hold from `now`, while
  cast-time spells hold through `active_cast.ends_at + recovery_ms` so their release
  follow-through is covered too.
- Check it beside the existing windup/cast holds (npcs.rs:1389-1406).

The migrated default is 500 ms. Abomination is explicitly authored from its imported
clip lengths: `NPC_ABOMINATION_HEAVY_CLAW` uses 850 ms recovery after its 900 ms
windup, and `NPC_ABOMINATION_CLAW` uses 900 ms recovery after its 700 ms windup.

One rule at one server choke point and no client runtime changes. Recovery authoring
still varies by template/action because shipped attack clips have different lengths;
the runtime behavior remains the genre-standard "mobs plant while swinging" feel.
Gameplay consequence: NPCs remain planted through each authored follow-through,
improving melee readability and kiting counterplay.

**Replication note.** The client sees melee attack start + windup via the public
`combat_event` row (`source_kind = "NPC_MELEE"`, scalar
`MELEE_RELEASE_DELAY_SECONDS`), but nothing replicates recovery or an "attacking"
flag, and `NpcPhysics` has no velocity — the client infers motion purely from
position deltas. A server-side hold therefore needs no new public/client replication
contract. The private `NpcCombatRuntime.hold_movement_until_micros` column is appended
with a const `0i64` migration default, so it remains a server module schema change but
is eligible for non-destructive automatic migration and requires no public Unity
binding.

**Verification.** `cargo test --quiet` passes all 481 server tests, including focused
catalog fallback/override, recovery validation, and monotonic hold-deadline coverage.
`spacetime build` also succeeds. The module was republished non-destructively to the
local `arena` database (`delete-data=never`); automatic migration appended the private
deadline with its `0i64` default, and the catalog re-sync restored 53 templates / 146
visuals. A live Abomination chase/attack capture remains before calling the visual
result verified in play mode.
