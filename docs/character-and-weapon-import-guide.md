# Character And Weapon Import Guide

## What Imported Assets Need to Provide

### For character models

The animation system uses Unity's **Humanoid Mecanim** with an `AnimatorOverrideController` — animations are retargeted from the existing `Arena_Character.controller` clips onto whatever humanoid skeleton you import. Bone names don't matter; Unity remaps them through the Avatar definition.

The imported rig must be mappable to Unity's Humanoid avatar. The skeleton needs at minimum all of Unity's required humanoid bones:

| Region | Required bones |
|---|---|
| Core | Hips, Spine, Chest, Upper Chest |
| Head | Neck, Head |
| Arms | Left/Right Shoulder, Upper Arm, Lower Arm, **Hand** |
| Legs | Left/Right Upper Leg, Lower Leg, Foot |

`PlayerAnimator` specifically calls `animator.GetBoneTransform(HumanBodyBones.LeftHand)` and `RightHand` for spell VFX origins — those two are non-negotiable.

**Pose**: T-pose or A-pose. Unity can handle either but prefers T-pose for cleanest retargeting.

**Export format**: FBX. GLB works but FBX gives more control over import settings in Unity.

### For weapon models

Weapons can still be delivered as static meshes, but that is only the mesh asset side of the integration. The runtime placement contract now lives in the avatar prefab plus `CombatAnimationSet` binding data. Requirements:

- **Pivot point** at the grip/handle (where the hand holds it), not at the mesh center
- Correct scale relative to the character

Read [weapon-visual-integration-contract.md](./weapon-visual-integration-contract.md) before wiring a new weapon pack. This guide is the import checklist; the weapon visual integration contract is the source of truth for mount ids, handoff behavior, validation, and per-weapon visual binding rules. Do not assume `main_weapon_hand` plus identity local transforms is always correct.

### For avatars from the Stylized Modular Human studio pack

For the current Stylized Modular Human runtime rebuild, `Assets/Arena/Resources/PlayerArmature 1.prefab` is the legacy semantic-mount source and `Assets/Arena/Resources/PlayerArmature.prefab` is the generated runtime prefab. Transfer legacy mount poses in root/model space onto the new humanoid avatar; do not copy local transforms between unrelated package bones.

Do not treat package socket names or animation prop-node names as semantic runtime mounts. In particular:

- `greatsword_hand` should resolve to the transferred `main_weapon_hand` mount unless a distinct semantic greatsword mount was intentionally authored and validated.
- `greatsword_hand` must not point at a transform named `weapon_r`.
- `main_weapon_hand` must not point at `Sword`.
- `off_weapon_hand` must not point at `Shield`.
- `archer_bow_stowed` and `archer_quiver_stowed` must not point directly at StylizedCharacter `Back_Bow` or `Back_Quiver` sockets.

The spawned weapon prefabs already contain prop-node structure such as `weapon_r/Great_Sword`. Mapping avatar mounts to those same names double-applies transforms and breaks weapon placement.

---

## What Imported Assets Do NOT Need to Provide

- **Weapon mount transforms** (`main_weapon_hand`, `off_weapon_hand`, `main_weapon_stowed`, `off_weapon_stowed`, `greatsword_hand`, `greatsword_stowed`, `archer_bow_hand`, `archer_bow_stowed`, `archer_quiver_stowed`, `dagger_main_stowed`, `dagger_off_stowed`, etc.) — these are authored in Unity on the runtime avatar prefab or generated from avatar assembly/calibration code. Do not wire semantic mounts directly to prop-node names such as `weapon_r`, `Sword`, `Shield`, `Back_Bow`, or `Back_Quiver`; see the weapon integration contract doc above.
- **Weapon animations** — `CombatAnimationSet` clip slots are filled from separate animation packs, not baked into the character mesh.

---

## Import Checklist

1. Set rig type to **Humanoid** in Unity's import settings
2. Click **Configure** and verify all required bones are mapped (fix any that Unity flags red)
3. Rebuild the runtime avatar with the project builder, or transfer semantic mounts from a known-good legacy/source avatar in root/model space
4. Confirm `AvatarWeaponMounts` resolves the current weapon-binding ids: `main_weapon_hand`, `off_weapon_hand`, `main_weapon_stowed`, `off_weapon_stowed`, `greatsword_hand`, `greatsword_stowed`, `archer_bow_hand`, `archer_bow_stowed`, `archer_quiver_stowed`, `dagger_main_stowed`, and `dagger_off_stowed`
5. Confirm `greatsword_hand` resolves to the transferred `main_weapon_hand` when the source avatar has no distinct greatsword mount
6. Run `Arena > Avatars > Validate Runtime Avatar Weapon Mount Contract`
7. Wire up or verify `CombatAnimationSet` assets, then test greatsword, sword/shield, stowed state, and draw/sheath transitions in Play Mode
8. Tune visual offsets only after the validator passes and the semantic mounts are correct

---
