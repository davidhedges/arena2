# Archer CombatAnimationSet Seeding Plan

Date: 2026-05-03

## Goal

Seed a new `CombatAnimationSet` for the imported Archer Animation Pack so a new bow/archer combat profile can use authored movement, hit reactions, defensive clips, charge clips, and combo attack animations.

The first deliverable should be a valid animation/combat-profile asset under `Assets/Arena/Resources/CombatAnimationSets` with stable authored attack ids. Class progression, projectile gameplay, and loadout wiring should only be added after the animation set and manifest export are valid.

## Current Authoring Contract

Relevant code and docs:

- `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs`
- `Assets/Arena/Editor/CombatAnimationSetEditor.cs`
- `Assets/Arena/Runtime/Presentation/Animation/CombatProfileIds.cs`
- `Assets/Arena/Runtime/Entity/EntityRegistry.cs`
- `Assets/Arena/Runtime/Combat/GameplayContracts.cs`
- `server/src/progression.rs`
- `docs/combat-authoring-contract.md`
- `docs/weapon-visual-integration-contract.md`

Important findings:

- `CombatAnimationSet` already has `[CreateAssetMenu(menuName = "Arena/Animation Set")]`, so a new asset can be created from Unity without code changes.
- `CombatAnimationSetCatalog.Resolve()` resolves assets by authored `combatProfileId`, not by file name. The Archer asset only needs the correct `combatProfileId`.
- The server progression tests scan `Assets/Arena/Resources/CombatAnimationSets/*.asset` and assert that every `combat_profiles[]` row has a matching authored asset. Do not add the new combat profile row until the asset exists.
- `CombatAnimationSet.BuildMeleeExport()` still exports attacks through `server/src/melee_manifest.shared.json`. For Archer V1, treat "melee attack" rows as the current generic authored attack lane, even though the weapon is ranged.
- The custom inspector exports and imports the shared melee manifest, protects authored assets with backups, and validates animated weapon prop paths.
- Strict animated prop validation currently knows only sword/shield/greatsword prop paths. Archer-specific animated props will not be validated until the editor requirements are extended.

## Imported Archer Pack

Animation root:

`Assets/ThirdParty/AssetStore/Animation/ArcherAnimationPack/Animations/Humanoid`

Top-level folders:

- `01_Idle`
- `02_Attack`
- `03_Walk`
- `04_Run`
- `05_Jump`
- `06_Dodge`
- `07_Roll`
- `08_Hit`
- `09_Turn`

Prefab/model root:

- `Assets/ThirdParty/AssetStore/Animation/ArcherAnimationPack/PreFabs/9CG_Archer.prefab`
- `Assets/ThirdParty/AssetStore/Animation/ArcherAnimationPack/Model/9CG_Archer.fbx`

Animated Archer prop paths observed in attack clips and the prefab:

- `root/pelvis/spine_01/spine_02/spine_03/spine_04/spine_05/clavicle_l/upperarm_l/lowerarm_l/hand_l/Weapon_Bow_L`
- `root/pelvis/spine_01/spine_02/spine_03/spine_04/spine_05/clavicle_l/upperarm_l/lowerarm_l/hand_l/Weapon_Bow_L/Bow_String`
- `root/pelvis/spine_01/spine_02/spine_03/spine_04/spine_05/clavicle_r/upperarm_r/lowerarm_r/hand_r/Weapon_Bow_R`
- `root/pelvis/spine_01/spine_02/spine_03/spine_04/spine_05/Arrow_Holder_01`
- `root/pelvis/spine_01/spine_02/spine_03/spine_04/spine_05/Arrow_Holder_01/Arrow_Holder_02`
- `root/pelvis/spine_01/spine_02/spine_03/spine_04/spine_05/Bow_Holder1`

## Proposed Identity

Use a combat-profile-first identity:

- Combat profile id: `ARCHER_BOW`
- Animation set id: `ARCHER_BOW`
- Asset path: `Assets/Arena/Resources/CombatAnimationSets/ArcherBow.asset`
- Auto attack authored strike id: `AUTO_ATTACK_1`
- Auto attack visual source action id: `ARCHER_QUICK_SHOT`
- Resource type for selectable Archer abilities: `MANA`

This leaves the future class name independent. A class can be named `ARCHER`, `MARKSMAN`, `RANGER`, or something else while still using the `ARCHER_BOW` combat profile.

## Implementation Decisions

The implementation resolved the initial ambiguities this way:

1. The new class id is `RANGER`, with default combat profile `ARCHER_BOW`.
2. Archer abilities currently use the existing authored attack lane. True ranged projectile gameplay, timed arrow spawn/release, and projectile VFX remain follow-up work.
3. Runtime weapon presentation uses local authored combat-set prefabs: `Assets/Arena/Resources/CombatAnimationSets/BowPackAuthored.prefab` and `Assets/Arena/Resources/CombatAnimationSets/QuiverPackAuthored.prefab`. These preserve the imported StylizedCharacter bow/quiver meshes and textures through local authored material copies, but the combat profile does not reference source package prefabs directly.
4. V1 uses existing semantic mounts rather than adding Archer-specific mount ids.
5. The default outfit uses the Human Male `NRanger` green armor prefabs.
6. V1 Archer ability rows remain `PARRYABLE` / `BLOCKABLE` only because they ride the current melee-action lane. Revisit those defense semantics when true ranged projectile gameplay is authored.

## Clip Mapping Plan

### Direction Mapping

The Archer pack has 10 movement directions in some folders, while `CombatAnimationSet.DirectionalClipSet` has 8 compass slots. Use this mapping:

| DirectionalClipSet field | Archer suffix |
| --- | --- |
| `n` | `F_0` |
| `ne` | `F_R_45` |
| `e` | `F_R_90` |
| `se` | `B_R_45` |
| `s` | `B_180` |
| `sw` | `B_L_45` |
| `w` | `F_L_90` |
| `nw` | `F_L_45` |

Do not use the extra `B_L_90` and `B_R_90` clips in V1 unless visual testing shows the diagonals need them.

### Idle And Combat Stance

- `locomotionIdle`: `01_Idle/01_Idle/Idle.anim`
- `locomotionIdleCombat`: `01_Idle/02_Idle_Combat/Idle_Combat.anim`
- `enterCombatIdle`: `01_Idle/01_Idle/Idle_to_Idle_Combat.anim`
- `exitCombatIdle`: `01_Idle/02_Idle_Combat/Idle_Combat_to_Idle.anim`
- `enterCombatWalk`: `03_Walk/.../Walk_to_Walk_Combat.anim` if present, otherwise leave empty for V1.
- `exitCombatWalk`: `03_Walk/.../Walk_Combat_to_Walk.anim` if present, otherwise leave empty for V1.
- `enterCombatRun`: `04_Run/.../Run_to_Run_Combat.anim` if present, otherwise leave empty for V1.
- `exitCombatRun`: `04_Run/.../Run_Combat_to_Run.anim` if present, otherwise leave empty for V1.

### Walk And Run

Use loop clips for movement sets and stop clips for stop sets:

- `walk`: `03_Walk/01_Walk/**/Walk_*_Loop.anim`
- `walkCombat`: `03_Walk/02_Walk_Combat/**/Walk_Combat_*_Loop.anim`
- `run`: `04_Run/01_Run/**/Run_*_Loop.anim`
- `runCombat`: `04_Run/02_Run_Combat/**/Run_Combat_*_Loop.anim`
- `walkStop`: `03_Walk/01_Walk/**/Walk_*_Stop.anim`
- `walkStopCombat`: `03_Walk/02_Walk_Combat/**/Walk_Combat_*_Stop.anim`
- `runStop`: `04_Run/01_Run/**/Run_*_Stop.anim`
- `runStopCombat`: `04_Run/02_Run_Combat/**/Run_Combat_*_Stop.anim`

Prefer regular run loops over `Fast` loops for V1 unless playtesting shows the character should read as a sprint-focused class.

### Turns

Use `09_Turn` regular turn clips for non-combat fields and combat turn clips for combat fields:

- `turn90L`, `turn90R`, `turn180L`, `turn180R`
- `turn90CombatL`, `turn90CombatR`, `turn180CombatL`, `turn180CombatR`

If the pack only has one 180 turn per stance, assign it to both 180 fields.

### Airborne

- `jumpStart`: cardinal `05_Jump` regular jump start clips.
- `jumpStartCombat`: cardinal `05_Jump` combat jump start clips.
- `freeFall`: `Jump_Loop_0.anim`
- `freeFallCombat`: `Jump_Combat_Loop_0.anim`
- `jumpLand`: cardinal regular jump end clips.
- `jumpLandCombat`: cardinal combat jump end clips.

Double-jump clips should not be wired in V1 unless there is already a fixed action or shared action profile expecting them.

### Dodge

Use the dedicated dodge folders:

- `dodge`: `06_Dodge/01_Dodge/Dodge_*.anim`
- `dodgeCombat`: `06_Dodge/02_Dodge_Combat/Dodge_Combat_*.anim`
- `airDodge`: `06_Dodge/03_Dodge_Air/Dodge_Air_*.anim`
- `airDodgeCombat`: `06_Dodge/04_Dodge_Air_Combat/Dodge_Air_Combat_*.anim`

The `07_Roll` folder can remain unused until a roll action is explicitly exposed.

### Defensive And Charge

Defensive source clips:

- Parry/counter source candidates: `02_Attack/16_Parry_Counter_Attack/*.anim`
- Block source candidates: `08_Hit/14_Block/*.anim`

Charge source clips:

- `chargeStart`: `02_Attack/07_Skill_Attack/Skill_04_Charge_LV1_Start.anim`
- `chargeLoop`: `02_Attack/07_Skill_Attack/Skill_04_Charge_LV1_Loop.anim`
- `chargeEnd`: `02_Attack/07_Skill_Attack/Skill_04_Charge_LV1_End.anim`

Only wire LV2/LV3 charge clips as authored attacks if the gameplay has separate charged-shot stages. Otherwise keep the global charge fields at LV1. Leave air-charge fields empty unless a true airborne charged-shot action is authored.

### Hit, Stagger, Knockdown, Stun, Death

Use combat variants where available:

- `hitF/B/L/R`: `08_Hit` combat directional hit clips.
- `airHitF/B/L/R`: combat air directional hit clips.
- `staggerF/B/L/R`: large combat directional hit clips. These must be populated because export validation depends on stagger durations.
- `knockdownStart`: combat knockdown start.
- `knockdownLoop`: combat knockdown loop.
- `getUp`: combat get-up.
- `death`: combat death clip if present; otherwise regular death clip.

If there are no stun-specific Archer clips, leave stun fields empty. Do not reuse knockdown clips as stun placeholders unless a concrete runtime requirement is identified.

## Attack Seeding Plan

Seed a compact V1 set first. Avoid importing every attack row until the base profile is playable and the timings can be inspected in the Combat Animation Set editor.

### V1 Authored Attacks

| Authored id | Runtime slot id | Clip | Notes |
| --- | --- | --- | --- |
| `ARCHER_QUICK_SHOT` | `quick_shot_slot` | `02_Attack/01_Combo_Attack_01/Combo_Attack_01_01.anim` | Regular quick-shot row and auto-attack visual source |
| `ARCHER_FOLLOW_THROUGH` | `follow_through_slot` | `02_Attack/01_Combo_Attack_01/Combo_Attack_01_02.anim` | Combo from `ARCHER_QUICK_SHOT` |
| `ARCHER_LOW_DRAW` | `low_draw_slot` | `02_Attack/01_Combo_Attack_01/Combo_Attack_01_03.anim` | Combo from `ARCHER_FOLLOW_THROUGH` |
| `ARCHER_FINISHING_SHOT` | `finishing_shot_slot` | `02_Attack/01_Combo_Attack_01/Combo_Attack_01_04.anim` | Combo finisher |
| `ARCHER_POWER_SHOT` | `power_shot_slot` | `02_Attack/07_Skill_Attack/Skill_01.anim` | Selectable attack candidate |
| `ARCHER_RAIN_SHOT` | `rain_shot_slot` | `02_Attack/07_Skill_Attack/Skill_02.anim` | Selectable attack candidate |
| `ARCHER_EVASIVE_SHOT` | `evasive_shot_slot` | `02_Attack/07_Skill_Attack/Skill_03.anim` | Selectable attack candidate |
| `ARCHER_AIR_SHOT` | `air_shot_slot` | `02_Attack/08_Combo_Attack_Air/Combo_Attack_Air_01.anim` | Airborne candidate |

Default timing for V1 before tuning:

- Use single-clip presentation except for charged shots.
- Set `recoveryMs` from clip length minus hit/release timing, rounded conservatively.
- Use one narrow hit/release window per shot until gameplay direction is confirmed.
- Use `comboFrom` only for the four `Combo_Attack_01_*` rows.
- Use the existing combo open/grace defaults from nearby authored sets unless the inspector shows obvious mismatch.

### Optional V2 Authored Attacks

Add these after V1 validates:

- `ARCHER_BURST_SHOT_1` through `ARCHER_BURST_SHOT_4` from `02_Combo_Attack_02`.
- `ARCHER_WIDE_SHOT_1` through `ARCHER_WIDE_SHOT_4` from `03_Combo_Attack_03`.
- `ARCHER_AIR_COMBO_1` through `ARCHER_AIR_COMBO_4` from `08_Combo_Attack_Air`.
- `ARCHER_RUN_SHOT_1` and `ARCHER_RUN_SHOT_2` from `15_Run_Attack`.
- `ARCHER_DODGE_SHOT_F/B/L/R` from `18_Dodge_Attack`.
- `ARCHER_CHARGED_SHOT_LV1`, `ARCHER_CHARGED_SHOT_LV2`, `ARCHER_CHARGED_SHOT_LV3` as phased attacks using the `Skill_04_Charge_*_Start/Loop/End` triplets.

## Weapon Presentation Plan

Follow `docs/weapon-visual-integration-contract.md` before tuning offsets.

1. Use local authored bow/quiver prefabs in `Assets/Arena/Resources/CombatAnimationSets`, matching the existing sword/shield/greatsword weapon presentation pattern.
2. Verify the runtime avatar has semantic mounts for the selected bow and quiver placement.
3. Bind bow visual(s) in `CombatAnimationSet.weaponPresentation`.
4. Add quiver visual binding if it should always be visible.
5. Only tune offsets after validating semantic mount placement.

Recommended V1 mount strategy:

- Bow in hand: use `archer_bow_hand`.
- Bow stowed: use `archer_bow_stowed`.
- Quiver: use `archer_quiver_stowed`.
- Do not use StylizedCharacter `Back_Bow` or `Back_Quiver` sockets as Arena mount targets.

Authored V1 weapon assets:

- `Assets/Arena/Resources/CombatAnimationSets/BowPackAuthored.prefab`
- `Assets/Arena/Resources/CombatAnimationSets/BowPackAuthored.mat`
- `Assets/Arena/Resources/CombatAnimationSets/QuiverPackAuthored.prefab`
- `Assets/Arena/Resources/CombatAnimationSets/QuiverPackAuthored.mat`

`ArcherBow.asset` must reference those local authored prefabs, not `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Weapon/Bow/Bow_NArcher_Gn.prefab` or `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Weapon/Quiver/Quiver_NArcher_Gn.prefab`.

## Appearance Plan

Continue using the existing Human Male avatar model, matching the current Warrior and Paladin starter setup. The server appearance defaults currently use `race_id = HUMAN`, `sex_id = MALE`, `body_id = HUMAN_MALE_BODY_01`, and `head_id = HUMAN_MALE_HEAD_01_A`; the Unity catalogs also only map Warrior and Paladin starter outfits for `HUMAN` / `MALE`.

Use the Human Male ranger armor prefabs from the StylizedCharacter package for the new class outfit. The package names these assets with `NRanger`, not `Archer`. The relevant Human Male green starter candidates are:

- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Chest/Hu_M_Chest_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Gloves/Hu_M_Gloves_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Boots/Hu_M_Boots_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Belt/Hu_M_Belt_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Shoulder/Hu_M_Shoulders_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Cape/Hu_M_Cape_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Helmet/Hu_M_Helm_NRanger_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/ChestSkin/ChestSkin_NRanger_U_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/PantsSkin/Pants_NRanger_U_Gn.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/GlovesSkin/GlovesSkin_NRanger_Gn.prefab`

Broader race/gender/color variants exist under:

- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Chest/*_Chest_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/ChestSkin/ChestSkin_NRanger_U_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/PantsSkin/Pants_NRanger_U_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Gloves/*_Gloves_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/GlovesSkin/GlovesSkin_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Boots/*_Boots_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Belt/*_Belt_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Shoulder/*_Shoulders_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Cape/*_Cape_NRanger_*.prefab`
- `Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Prefabs/Item/Equipment/Helmet/*_Helm_NRanger_*.prefab`

Default color recommendation: use only the Human Male green `Hu_M_*_NRanger_Gn.prefab` and shared `*_NRanger_U_Gn.prefab` variants for the Archer/Ranger starter outfit. Blue/red and non-Human-Male variants should remain later cosmetics or future race/gender support, not part of the initial class seed.

## Authoring-System Improvements

The request is straightforward at the data-contract level, but not fully straightforward at the Unity authoring level because the asset has many clip slots and attack rows.

Recommended improvements:

1. Extend `CombatAnimationSetEditor` animated prop requirements for Archer prop paths after the mount decision is made.
2. Improve the existing inspector flow if manual slot filling exposes avoidable friction, but keep the Archer asset itself as normal `CombatAnimationSet` data.

Manual creation is acceptable for this profile because `CombatAnimationSet` already has a Unity create menu and the catalog resolves by authored `combatProfileId`.

## Execution Steps

1. Create `Assets/Arena/Resources/CombatAnimationSets/ArcherBow.asset` via `Create > Arena > Animation Set`.
2. Set `animationSetId = ARCHER_BOW` and `combatProfileId = ARCHER_BOW`.
3. Populate locomotion, turns, airborne, dodge, hit, stagger, knockdown, death, and charge fields using the clip mapping above.
4. Choose bow/quiver runtime visual prefabs and fill `weaponPresentation`.
5. Add the V1 authored attacks, set `autoAttackAuthoredStrikeId` to `AUTO_ATTACK_1`, and set `autoAttackVisualSourceActionId` to `ARCHER_QUICK_SHOT`.
6. Preview attacks in the `CombatAnimationSetEditor` and adjust hit/release windows and visual interrupt timestamps.
7. Export `server/src/melee_manifest.shared.json` from the asset inspector.
8. Run `cargo test progression::tests::`.
9. Only after the asset and manifest are valid, add progression rows for the new combat profile, class, auto attack, mana-backed abilities, presentations, default loadout, and default class outfit using the `NRanger` StylizedCharacter prefabs.
10. Run broader server and Unity tests after progression wiring.

## Validation Checklist

- `CombatAnimationSetCatalog.Resolve("ARCHER_BOW")` resolves the new asset.
- The asset has both identity fields normalized to `ARCHER_BOW`.
- All required stagger clips are assigned.
- The V1 attacks export with stable authored ids and runtime slot ids.
- `AUTO_ATTACK_1` is the authored auto attack id, and `ARCHER_QUICK_SHOT` is its visual source.
- The shared manifest includes the `ARCHER_BOW` profile after export.
- No `combat_profiles[]` row points to `ARCHER_BOW` before the asset exists.
- Strict avatar validation either passes or reports only known Archer mount work that is explicitly tracked.
- `cargo test progression::tests::` passes.
