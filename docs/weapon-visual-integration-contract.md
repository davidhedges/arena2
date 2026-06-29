# Weapon Visual Integration Contract

## Why This Exists

We churned on the stylized male avatar swap because we blurred two different contracts:

- `AvatarWeaponMounts` semantic mount ids are where runtime weapon prefabs attach.
- Authored prop-node names such as `Sword`, `Shield`, `weapon_r`, `Sword_Holder`, and `Shield_Holder` are part of animation/weapon prefab structure.

Those are not interchangeable. The greatsword broke when `greatsword_hand` was mapped to an avatar `weapon_r` prop node while the spawned greatsword prefab already contained its own `weapon_r/Great_Sword` hierarchy. That double-applied the authored transform and made the sword stand vertically/off-body.

Arena mount ids are semantic API names. The current primary weapon-binding ids used by `CombatAnimationSet` visual bindings are:

- `main_weapon_hand`
- `off_weapon_hand`
- `main_weapon_stowed`
- `off_weapon_stowed`
- `greatsword_hand`
- `greatsword_stowed`
- `archer_bow_hand`
- `archer_bow_stowed`
- `archer_quiver_stowed`
- `dagger_main_stowed`
- `dagger_off_stowed`

Legacy ids such as `main_hand`, `off_hand`, `main_sheath`, `off_sheath`, `main_stowed`, `off_stowed`, and `archer_quiver_back` remain compatibility aliases only. Do not author new assets with them.

`AvatarWeaponMounts` also exposes lower-level utility ids such as `main_back`, `off_back`, `main_hip`, and `off_hip` for avatar assembly and generic body-location resolution. The checked-in `CombatAnimationSet` visual bindings currently use the weapon-binding ids listed above, not these utility ids. Do not treat utility ids as the default ids for new weapon visual bindings unless the weapon contract for that specific item calls for them.

## Runtime Contract

Weapon visuals in runtime are the combination of three things:

1. The runtime avatar prefab and its semantic mount transforms.
2. The semantic mount ids exposed by `AvatarWeaponMounts`.
3. The spawned weapon prefab hierarchy and `CombatAnimationSet` visual binding.

Relevant runtime code:

- `Assets/Arena/Resources/PlayerArmature.prefab`
- `Assets/Arena/Runtime/Presentation/Equipment/AvatarWeaponMounts.cs`
- `Assets/Arena/Runtime/Presentation/Equipment/WeaponAttachmentController.cs`
- `Assets/Arena/Editor/CombatAnimationSetEditor.cs`

`WeaponAttachmentController` does not invent correct placement. It only attaches a prefab to the requested mount id and applies authored local transform overrides from the animation set.

Combat state has two independent channels:

- `PlayerEntity.IsInCombat` — animator stance, owned by `PlayerAnimator`.
- `WeaponAttachmentController.IsInCombatVisual` — which mount each spawned visual is parented to.

They can be desynced during a draw/sheath transition: `PlayerAnimator.SetInCombat(...)` registers a pending handoff that drives the visual channel from `normalizedTime` against `drawWeaponHandoffTime` / `sheathWeaponHandoffTime`. To snap both channels in a single frame and clear any in-flight handoff, use `EnterCombatImmediate` / `ExitCombatImmediate` on `PlayerEntity`. Calling `WeaponAttachmentController.SetInCombat(...)` directly while a handoff is pending will get overwritten on the next polling frame.

## The Rule

Semantic mount ids must point at semantic mount transforms, not at weapon prefab prop-node names.

For the current Stylized Modular Human runtime rebuild, `main_weapon_hand`, `off_weapon_hand`, `main_weapon_stowed`, and `off_weapon_stowed` are transferred from the legacy runtime avatar in root/model space. `greatsword_hand` is a deliberately-authored semantic mount named `Arena_Greatsword_hand`; it is parented under an internal `weapon_r` compatibility socket only so GreatSwordAnimationPack object curves can drive the held prop. `greatsword_stowed` is a deliberately-authored semantic mount calibrated from the GreatSwordAnimationPack's `sword_holder` bone. Archer stowed mounts are calibrated semantic Arena mounts, not direct uses of package `Back_Bow` / `Back_Quiver` sockets. Dagger stowed mounts are calibrated semantic Arena mounts recovered from DaggersAnimationPack `Dagger_Holder_R1` / `Dagger_Holder_L1` under the pack pelvis.

Do not map:

- `main_weapon_hand` to `Sword`
- `off_weapon_hand` to `Shield`
- `greatsword_hand` to `weapon_r`
- `archer_bow_stowed` to `Back_Bow`
- `archer_quiver_stowed` to `Back_Quiver`

Those names are allowed inside weapon prefabs or as compatibility children, but they are not the runtime attachment targets.

StylizedCharacter package sockets may provide visible rig structure, but they are not Arena weapon mount authority. In particular, never use `Back_Bow`, `Back_Quiver`, `Back_L`, `Back_M`, `Back_R`, `Back_2HL`, `WeaponR`, `WeaponL`, `Sword`, `Shield`, or `weapon_r` as direct targets for canonical Arena weapon ids.

## Greatsword Example

`TwoHandedSword.asset` uses two distinct mount ids:

- `greatsword_hand` for the drawn pose. This resolves to `Arena_Greatsword_hand`, an Arena-named semantic child under the internal `weapon_r` compatibility socket. The binding's drawn local position and rotation are zero because the mount and animated parent carry the pose.
- `greatsword_stowed` for the stowed pose. This is a real, distinct mount transform parented to `spine_03` with the local pose recovered from the `sword_holder` bone in `Assets/GreatSwordAnimationPack/Prefabs/9CG_Great_Sword.prefab`. Because the mount itself carries the authored pose, the asset's `stowedLocalPosition` / `stowedLocalRotation` are zero — do not double-apply.

The spawned greatsword visual prefab contains:

- `greatsword`
- `weapon_r`
- `Great_Sword`

That means the correct integration is:

1. Transfer the legacy hand semantic mount to the new avatar as `main_weapon_hand` in root/model space.
2. Add an internal `weapon_r` child under the right hand only when a GreatSword clip needs package object-curve compatibility.
3. Add `Arena_Greatsword_hand` under that internal socket and register the semantic mount id `greatsword_hand` to `Arena_Greatsword_hand`, not to `weapon_r`.
4. Add `greatsword_stowed` as a child of `spine_03` using the calibration recovered from the pack's `sword_holder` bone.
5. Retarget extracted GreatSword clip object curves from package-case `root/pelvis/...` to runtime `Root/Pelvis/...` when the clip should animate the weapon prop.
6. Only tune the `CombatAnimationSet` visual offset if there is still a small remaining mesh-pivot correction *after* the mount itself and clip binding paths are correct.

The wrong integration was:

1. Create an avatar child named `weapon_r`.
2. Register `greatsword_hand` directly to that `weapon_r`.
3. Spawn `GreatSwordPackAuthored.prefab`, which already has its own `weapon_r`.
4. Try to fix the resulting double transform with rotation numbers.

That is the failure mode to prevent.

The allowed compatibility pattern is different: `weapon_r` may exist as a hidden/internal parent for imported clip bindings, but the authored Arena mount id must point to an Arena-named semantic child such as `Arena_Greatsword_hand`.

## Animated Prop Curve Binding Paths

Weapon mount ids and animation object-curve paths solve different problems.

- `CombatAnimationSet.weaponPresentation.visuals[].drawnMountId` / `stowedMountId` must use semantic Arena ids.
- Imported animation clips may contain object curves for package prop nodes such as `weapon_r`, `sword_holder`, `Sword`, `Shield`, `Weapon_Bow_L`, or `Bow_Holder1`.
- Those object curves are path-based and case-sensitive. If the clip serializes `root/pelvis/...` but the runtime avatar hierarchy is `Root/Pelvis/...`, Unity will not drive the prop node even though the humanoid body animation still plays.

When a weapon appears correct in the vendor preview but wrong in Arena, check the clip's serialized `path:` entries before adjusting mount offsets or contacting the asset developer. For extracted clips used by Arena, retarget the extracted copy's prop-object paths to the runtime hierarchy and keep package names out of authored mount ids.

## Recovering Stowed Mount Calibration From Animation Packs

A weapon pack's sheath/draw animations are authored against a specific holder bone in the *pack's* skeleton — `sword_holder` in the greatsword pack, `Back_Bow` / `Back_Quiver` in StylizedCharacter archer presets, similar bones in future packs. The authored local pose of that bone is the source of truth for where the prop sits while stowed.

To make the runtime avatar match, recover the bone's transform values from the pack's prefab and bake them into a new `ArenaWeaponMountCalibrationEntry`:

1. Open the pack's authored prefab (e.g. `Assets/GreatSwordAnimationPack/Prefabs/9CG_Great_Sword.prefab`).
2. Find the holder bone by name. Note its parent (almost always `spine_03`), `m_LocalPosition`, and `m_LocalRotation`.
3. Add a constant for the new mount id in `AvatarWeaponMounts`.
4. Add an `ArenaWeaponMountCalibrationEntry` in `ArenaWeaponMountCalibration` with an Arena-prefixed marker name (e.g. `Arena_Greatsword_stowed`), parented to the bone you identified, using the exact local position and rotation from the pack.
5. Register the mount in `CharacterAvatarAssembler.EnsureArenaWeaponMounts` so the runtime avatar exposes it.
6. In the `CombatAnimationSet` asset, point `stowedMountId` at the new id and zero the binding's `stowedLocalPosition` / `stowedLocalRotation`. The mount carries the pose; the binding should not double-apply it.

Use Arena-prefixed marker names so the runtime hierarchy stays readable and pack-authored bone names never become Arena mount targets.

## Symptom Catalog

Common visual failure modes and their actual causes:

- **"Weapon doesn't appear to move when I toggle drawn / stowed."** The `CombatAnimationSet` asset's `drawnMountId` and `stowedMountId` point at the same mount. Authoring oversight, not a runtime bug. Fix the asset, or add a real stowed mount and point at it.
- **"Sheath animation plays but the prop ends up back in hand."** `PlayerAnimator.SetInCombat(false)` registered a pending handoff that holds the visual on the drawn mount until `normalizedTime ≥ sheathWeaponHandoffTime`. If the animator state transitions away before that point, or if test code calls `WeaponAttachmentController.SetInCombat(false)` while the handoff is still active, the visual gets re-asserted to drawn on the next polling frame. For deterministic snap paths, use `PlayerEntity.ExitCombatImmediate()` — it clears the pending handoff and snaps both channels.
- **"Two weapons visible at the same time, only one of them moves with toggling."** The runtime avatar contains a package-authored skinned weapon mesh (e.g. `Bow_NewbieArcher`, `Bow_NArcher_*`, `Quiver_*`) that is not owned by `WeaponAttachmentController`, in addition to the Arena-managed visual spawned from the `CombatAnimationSet` binding. Either suppress the package mesh during avatar assembly, or remove the redundant Arena binding so only one bow exists.
- **"Stowed mount is in roughly the right area but rotation is off by 90 degrees."** The pack's holder bone has a non-identity local rotation that you didn't copy. Re-read the bone's `m_LocalRotation` from the prefab and update the calibration entry; do not try to correct it by tuning the asset's per-binding rotation.

## New Weapon Pack Checklist

Use this sequence every time a new weapon animation pack is imported.

1. Inspect the existing working runtime avatar first. For the current Stylized Modular Human rebuild, `Assets/Arena/Resources/PlayerArmature 1.prefab` is the legacy source and `Assets/Arena/Resources/PlayerArmature.prefab` is the generated runtime output.
2. Record the semantic mount ids the source/runtime avatar actually resolves. Do not invent missing ids just because an animation set references them.
3. Transfer semantic mounts in root/model space, not by copying local transforms between different hand bones.
4. Inspect the spawned weapon visual prefab hierarchy. If it already contains `Sword`, `Shield`, `weapon_r`, or similar prop nodes, do not attach the avatar semantic mount to a node with the same name.
5. Inspect imported clip object-curve `path:` entries for package prop nodes. If the clip must animate an Arena-managed prop, retarget the extracted clip path to the runtime avatar hierarchy before tuning offsets.
6. Rebuild the runtime avatar and run the avatar mount validator.
7. Verify greatsword, sword/shield, stowed state, and draw/sheath transitions before tuning any offsets.
8. Only after the semantic mount contract and object-curve bindings are correct, tune per-weapon visual offsets.

## Mount Selection Guidance

Use generic semantic mounts when:

- The pack expects a standard held weapon.
- The pack does not animate a dedicated prop node.
- Small local transform overrides are sufficient.

Use a dedicated semantic mount id when:

- The weapon class needs a unique holder or back mount.
- Reusing an existing mount would require large corrective offsets.
- The mount is an intentionally-authored semantic target, not just a prop-node name copied from a prefab hierarchy.

Per-weapon-set local overrides are allowed, but they are the last mile, not the foundation.

## Manual Editor Step To Remember

For avatar swaps, the required manual/editor flow is:

- run `Arena > Avatars > Rebuild Runtime Player With Stylized Male`,
- run `Arena > Avatars > Validate Runtime Avatar Weapon Mount Contract`,
- test all currently supported animation sets in Play Mode.

If validation fails, fix the mount contract before touching visual offsets.

## Validation And Regression Protection

`StylizedPlayerAvatarBuilder` validates the default runtime avatar prefab against the regression that caused the greatsword churn:

- `greatsword_hand` must not point at `weapon_r` unless the legacy source truly defined a distinct greatsword semantic mount.
- `main_weapon_hand` must not point at `Sword`.
- `off_weapon_hand` must not point at `Shield`.
- `archer_bow_stowed` and `archer_quiver_stowed` must not point directly at StylizedCharacter `Back_Bow` or `Back_Quiver` sockets.
- semantic hand mounts must not be descendants of those prop-node names.

Treat validation failures as integration blockers.

When adding a new pack, also add or update an editor regression test so the animation set keeps pointing at the intended mount id.

## Short Version

Weapon integration is not just:

- mesh prefab
- mount id
- offset tuning

It is:

- legacy semantic mount source
- runtime avatar semantic mount
- spawned weapon prefab hierarchy
- semantic mount id
- animation set binding
- then offset tuning if still needed
