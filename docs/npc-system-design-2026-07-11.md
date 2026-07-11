# NPC System Design

Date: 2026-07-11
Status: implementation in progress; actor-generic targeting, authoritative commitments, shared spell/projectile execution, initial caster/support gameplay, and native NPC spell-phase requests are landed through `73190b8b`, while exemplar profile assets and full visual authoring remain

## Outcome

Arena should support every imported NPC appearance through one authored NPC pipeline, while allowing each gameplay template to use melee attacks, ranged attacks, spells, heals, buffs, debuffs, movement, reactions, death, and loot as appropriate.

The design has four central decisions:

1. **Separate gameplay identity from appearance identity.** A Skeleton Wizard is a gameplay template; `SkeletonWizard_Gn`, `SkeletonWizard_Pe`, and `SkeletonWizard_Rd` are appearance variants of that template. We should not create 146 copies of the same gameplay data merely to expose every prefab.
2. **Reuse the authoritative combat pipeline.** NPC actions should execute the same authored abilities, projectiles, effects, statuses, cooldowns, defenses, VFX cues, and combat events as player actions. NPC AI is another source of action intent, not a second combat implementation.
3. **Use utility-based action selection.** A utility scorer naturally handles “heal the injured ally, otherwise buff an ally, otherwise cast at range, otherwise use melee, otherwise reposition” without a large hard-coded behavior tree for every creature.
4. **Extend the existing combat-presentation contract.** NPC actions use the existing `CombatEvent` source/event vocabulary, `CombatAnimationRequest` lifecycle, and `CombatVfxCueResolver`/`CombatVFXDispatcher` triggers. The server adds only the deterministic presentation variant needed to keep native creature attacks synchronized. Unity visual profiles adapt the shared animation request to native controller states; they do not create a competing cue language.

This is a substantial engineering project. The existing kobold implementation is a useful vertical slice, but it is not a general NPC system yet.

## Implementation progress (2026-07-11 handoff)

The implementation has started and has been committed in coherent slices. The implementation baseline summarized here is `73190b8b` (`Route NPC spell animation phases`). Player combat semantics are an explicit guardrail: actor-generic seams were extended where required, but player damage, healing arithmetic, authorization, animation fallback, input, prediction, rewind, and action-bar behavior must not be redesigned as part of the NPC rollout.

### Landed foundations

- Gameplay and appearance identity are separated through `template_id` and `visual_id`.
- NPC templates, visual sets, action kits, action utility metadata, and brain profiles are authored in `server/src/npc_catalog.shared.json`, validated in Rust, synchronized to runtime catalogs, and represented in generated bindings.
- Combat abilities have validated `PLAYER` / `NPC` / `BOTH` actor scope. Current NPC kits use the explicit `FREE_ACTIONS_ONLY` resource policy rather than fake player loadouts or action bars.
- Combat actor snapshots are actor-generic. Shared healing can target NPCs, combat relations resolve NPC teams, and crowd control interrupts pending NPC actions.
- Kobold melee actions come from authored action kits, use shared named cooldowns, emit shared animation requests/combat events, and retain the established present-time NPC hit policy.
- Fixture target pinning is owner-scoped and the S7-S9 probes use it, so later threat changes do not invalidate their netcode evidence.
- NPC home positions, shared collision-aware movement stepping, leash return, and threat cleanup on leash/despawn/out-of-combat transitions are implemented.

### Landed utility-AI slice

- Target and action reconsideration uses the authored brain interval with deterministic identity/sequence jitter; movement and committed action execution remain fixed-tick.
- Perception uses the shared combat-actor spatial index. Ordinary acquisition queries the aggro disc, while committed and fixture-pinned targets use exact identity lookup.
- Initial threat combines post-resolution damage threat with authored proximity weight. Target stickiness requires a meaningful challenger advantage, and fixture pins remain absolute.
- Utility selection applies authored health, cooldown, tactical-distance, target-status, and indexed nearby ally/enemy count gates with deterministic score/sort-order tie-breaking.
- The selected ability is stored in private NPC runtime state so utility is not rescored every fixed tick.
- Once a melee CAST telegraph is emitted, its pending action/target cannot be silently replaced. Replanning pauses until impact or an explicit interrupt.
- A server-private decision inspector is available when `ARENA_NPC_AI_DEBUG=1`. It records decision sequence, chosen action/target, score and threat summaries, plus deterministic rejection counts for role, selector, health, nearby-count, status, missing-ability, cooldown, and distance gates.
- Perception now exposes relation-filtered enemy and ally candidate views. `CURRENT_ENEMY`, `NEAREST_ENEMY`, `SELF`, and `LOWEST_HEALTH_ALLY` resolve independently of execution with deterministic health/distance/identity tie-breaking, and the winning selector is included in inspector output.
- NPC melee commitments validate current actor-generic pose, audience/relation, range, facing, and the shared scene-query LOS path both when committing and at impact.
- The existing server-actor spell adapter resolves NPC-scoped authored abilities and free-resource contracts without player action bars or a parallel cast path. A Skeleton Wizard frost projectile uses the shared cast, active-cast, cooldown, projectile, effect, combat-event, and VFX-cue lifecycle.
- Ranged actions honor authored approach/hold/retreat distance bands, and active casts pause replanning/movement until the shared cast lifecycle reaches a terminal state.
- A Lich support exemplar uses `LOWEST_HEALTH_ALLY` to apply an authored Bone Ward through the shared targeted buff/status pipeline, including ally relation, LOS, forbidden-status, cast-event, and VFX contracts.
- `NpcVisualProfile` native role maps now include spell cast-start, release, and cancel states. Actor-scoped `ActiveCast` and shared spell release/fizzle events translate into the same `CombatAnimationRequest` phase contract used by players before the NPC adapter resolves native controller states.

### Still incomplete

- `NpcPendingSwing` remains the kobold melee executor. The common validated action executor beneath player/practice/NPC adapters has not yet replaced that special path.
- Utility execution currently supports melee offense, one hostile projectile spell, and one allied targeted buff. Direct healing, debuff, interrupt, summon, mobility, and melee-fallback execution are not implemented.
- Basic ranged approach/hold/retreat bands are implemented. Richer kiting, unreachable-target recovery, navigation, and local avoidance remain.
- Healing/buff support threat, taunts, assist/call-for-help, and richer threat decay remain later work.
- Explicit exemplar `NpcVisualProfile` assets, primary Animator paths, native state authoring, searchable catalog spawning, all 146 appearance mappings, and automated presentation sweeps remain.
- The four-archetype acceptance group is not complete: Kobold Warrior gameplay remains, Skeleton Wizard and Lich support gameplay are authored, Skeleton Archer is absent, and the new exemplars still lack complete Unity visual profiles/native presentation.

### Current verification

- `cargo test --quiet`: 471 passed, 0 failed.
- `spacetime build -p server`: succeeded; the local optional `wasm-opt` binary is absent, so SpacetimeDB emitted an unoptimized module after a successful release build.
- `dotnet build Assembly-CSharp.csproj --no-restore`: succeeded with 0 errors and 11 existing obsolete-API warnings in third-party/current Unity code.
- `dotnet build Assembly-CSharp-Editor.csproj --no-restore`: succeeded with 0 errors and 17 existing obsolete/dead-field warnings.
- Generated C# bindings are current for the landed runtime schema.
- The working tree also contains user-owned changes to `.gitignore` and `Assets/Arena/Resources/NpcVisualCatalog.asset`, plus an unrelated untracked `docs/perf-opportunities-2026-07-11.md`; preserve those unless their owner explicitly brings them into scope.

### Recommended next slice

The next coherent slice should finish presentation for the landed gameplay exemplars without creating a creature-only event language:

1. Author explicit Skeleton Wizard and Lich profile assets with primary Animator paths, cast/release/cancel roles, sockets, and fallback policies; preserve the user-owned `NpcVisualCatalog.asset` change while integrating deliberately.
2. Pin NPC spell phase translation and profile validation with focused Unity edit-mode tests.
3. Add the Skeleton Archer ranged/melee-fallback exemplar through the same shared action executor and ranged-band movement.
4. Add direct allied healing only by extending the shared authored spell/effect contract; do not implement an NPC-only heal path.

## Asset relocation completed

The three imported packages now follow the repository convention documented in `docs/project-structure.md`:

```text
Assets/ThirdParty/AssetStore/Characters/
  KoboldPack/
  StylizedFantasyEnemyNPCBundle/
  StylizedFantasyEnemyNPCBundle2/
  StylizedCharacter/
```

The package folders and their `.meta` files were moved together, preserving Unity GUIDs. Embedded `assetPath` provenance was updated, and the four existing kobold paths in `Assets/Arena/Resources/NpcVisualCatalog.asset` were updated. These large local Asset Store packages remain ignored by Git.

First-party profiles, catalogs, wrapper assets, tests, and gameplay code belong under `Assets/Arena`; vendor source assets do not.

## What was imported

The three packages contain:

- 35 creature families: 3 kobold families and 32 new families.
- 146 prefab appearances: 9 kobolds, 85 in bundle 1, and 52 in bundle 2.
- Native Animator controllers and embedded FBX animation clips.
- No gameplay AI or Arena integration scripts.
- Humanoid rigs for the kobolds (`ModelImporter.animationType = 3`) and Generic rigs for all 32 new families (`animationType = 2`).

The last point matters: the humanoid Mixamo stun/fear override used by the earlier kobold work cannot be assumed to retarget onto the new creatures. Generic rigs generally need their native clips or a creature-specific authored clip.

### Animation inventory

The attack counts below are source clips, not necessarily distinct gameplay abilities. Stance-specific packages include attacks for several equipment configurations, so a particular prefab exposes only the subset used by its controller.

| Family | Prefabs | Attack clips | Spell/cast clips | Notable static gap |
|---|---:|---:|---:|---|
| Kobold Knight | 3 | 27 | 9 | none; source contains multiple weapon stances |
| Kobold Thief | 3 | 27 | 9 | none; source contains multiple weapon stances |
| Kobold Warrior | 3 | 27 | 9 | none; source contains multiple weapon stances |
| Deep Sea Lizard | 10 | 4 | 5 | none obvious |
| Demon Warrior | 5 | 12 | 5 | stance-specific controllers |
| Forest Demon | 4 | 2 | 0 | no native stun |
| Hellguard | 6 | 6 | 0 | unarmed and 1H attacks |
| Imp | 8 | 4 | 5 | none obvious |
| Lich | 6 | 2 | 6 | two spell families plus channels |
| Mushroom | 5 | 2 | 0 | none obvious |
| Rock Golem | 6 | 2 | 0 | none obvious |
| Skeleton Archer | 3 | 2 | 2 | includes `load` and `Spell` clips |
| Skeleton Warrior | 5 | 18 | 0 | extensive weapon/defend stances |
| Skeleton Wizard | 3 | 2 | 3 | cast and channel clips |
| Slime | 4 | 2 | 0 | no run or ready state |
| Slime Man | 4 | 2 | 0 | no native stun |
| Vampire | 3 | 2 | 0 | none obvious |
| Zombie | 7 | 6 | 0 | stance-specific locomotion/hit/death |
| Zombie Hound | 6 | 2 | 0 | none obvious |
| Abomination | 3 | 2 | 0 | no native stun |
| Air Warlord | 4 | 2 | 0 | no native stun |
| Bone Golem | 3 | 4 | 0 | includes jump-end attack |
| Demon Summoner | 3 | 2 | 3 | no native stun or run |
| Dragon Brute | 3 | 2 | 0 | none obvious |
| Grave Digger | 3 | 2 | 0 | none obvious |
| Humanoid Scarab | 4 | 2 | 0 | no native stun |
| Mechabot | 3 | 2 | 0 | walk only; no run |
| Skeleton Reaper | 3 | 3 | 2 | no conventional ready state |
| Spider | 4 | 2 | 0 | no native stun, run, or ready |
| Swamp Hound | 4 | 2 | 0 | none obvious |
| Tomb Shade | 3 | 2 | 0 | no native hit, run, or ready |
| Undead Bear | 3 | 3 | 0 | no native stun or ready |
| Undead Boar | 3 | 2 | 0 | no conventional ready state |
| Undead Eagle | 3 | 1 | 0 | no native stun, run, or ready |
| Undead Rat | 3 | 3 | 0 | no native stun or ready |

Eight new families have obvious spell/ranged presentation material: Deep Sea Lizard, Demon Warrior, Imp, Lich, Skeleton Archer, Skeleton Wizard, Demon Summoner, and Skeleton Reaper. Clip names only establish presentation capability; `SpellA` does not tell us whether the action should damage, heal, buff, summon, or debuff. Arena must author that gameplay meaning explicitly.

Multiple attacks are the norm, not the exception. Most families have at least `attack` and `Attack01`; several have three or four. The weaponized families have much larger stance-specific sets.

## Current system assessment

### What is already good

The existing work established several valuable pieces:

- `server/src/npcs.rs` owns authoritative instance, state, physics, combat runtime, melee telegraph, death, corpse, and despawn behavior.
- NPC melee emits a `CAST` telegraph, waits an authored windup, revalidates range and crowd control, then resolves through shared block/parry and effect code.
- `PlayerSnapshotSet` and `player_snapshot_for` already include NPC state and physics, despite their player-specific names.
- Damage, status effects, world-context resolution, target selection on the client, corpse loot, and several combat event subscriptions already accept NPC identities.
- `NpcVisualCatalog` provides an authored template-to-prefab seam.
- `NpcEntity` provides client interpolation, health bars, targeting, status tint, hit/death calls, and status reaction forwarding.
- Recent subscription work includes NPC-source combat effects, NPC combat events, and NPC projectile presentation events.

These should be generalized and reused.

### What prevents general use

The current implementation is kobold-specific in important places:

- Only four hard-coded Rust templates can be spawned.
- Only four of the nine kobold prefabs are mapped; none of the 137 new-bundle prefabs are mapped.
- The playground UI hard-codes three kobold buttons.
- `NpcAnimationController` hard-codes four kobold template IDs and state names.
- Its default locomotion expects `Walk_Forward` / `Run_Forward`, while the new controllers generally expose `walk` / `run`.
- Its default attack expects `Combat_Unarmed_Attack`, while most new controllers expose `attack` / `Attack01`.
- Its hit candidates do not include the common new state `hit`.
- It always requests `Death`, which does not cover stance-specific Zombie death states.
- It plays one candidate attack per template and cannot select among variants.
- Hard crowd-control reactions exist only on the four kobold catalog entries.
- The prior kobold reaction path relies on a temporary `AnimatorOverrideController` and a Humanoid avatar. That is not a solution for the new Generic rigs.
- The first Animator returned by `GetComponentInChildren` is accepted implicitly. Some imported prefabs contain multiple Animator components, so the primary animated root must be authored, not guessed.

The server behavior is also a single melee loop:

- Hostile NPCs only acquire player targets.
- Targeting is nearest-wins; there is no threat, target stickiness beyond the current runtime row, leash/home, ally perception, or group behavior.
- Non-hostile NPCs never act.
- There is no ranged, casting, healing, buffing, dispelling, interrupting, summoning, fleeing, or tactical repositioning.
- Healing currently updates only `PlayerState`, not `NpcState`.
- Faction relations treat a target NPC's `HOSTILE`/`NEUTRAL`/`FRIENDLY` flag as its relationship to every source. That is insufficient for NPC allies healing or buffing one another.
- `cast_spell_for(...)` is already an internal explicit-caster seam and practice turrets call it with the server-action empty-token convention. Those turrets are player-like actor bundles, however; a real `NpcInstance` still fails the player-only authoritative snapshot and authorization/resource assumptions inside that function. The seam should be retained, but the common executor still needs to be extracted beneath it.
- The direct sweep chase handles simple geometry but is not navigation around obstacles.

## Target architecture

```text
NPC authoring
  template + visual set + action kit + brain profile
                         |
                         v
Player input -> prediction/input adapter ---------+
Practice/test automation -> server actor adapter -+---> shared combat action executor
NPC perception -> utility AI adapter -------------+       validation / cast / projectile
                                                         effect / status / cooldown
                                                                    |
                                                                    v
                                              replicated combat + presentation events
                                                                    |
                                                                    v
                                  player or NPC animation adapter / VFX / audio / sockets
```

There should be one combat executor and multiple explicit intent adapters: validated player input, existing practice/test automation, and server NPC AI. The practice adapter must migrate with the executor extraction rather than remain a stale special case.

## Authoring model

### 1. Gameplay template

Replace the Rust `match` in `npc_template(...)` with a validated shared catalog, following the repository's established authored JSON -> Rust validation/sync -> runtime table pattern.

Suggested source: `server/src/npc_catalog.shared.json`.

Each template defines:

- `template_id`
- `display_name`
- `species_id`
- `visual_set_id`
- base health and collision dimensions
- movement profile
- perception profile
- `brain_profile_id`
- `action_kit_id`
- `loot_profile_id`
- default combat team/faction policy
- XP/reward classification when that system exists

The synchronized public catalog lets the development spawn browser discover valid templates without duplicating constants in C#.

### 2. Appearance

Add `visual_id` to `NpcInstance`. `template_id` determines gameplay; `visual_id` determines the prefab and visual profile.

Examples:

```text
template_id = SKELETON_WIZARD
visual_id   = SKELETON_WIZARD_GN

template_id = DEEP_SEA_LIZARD
visual_id   = DEEP_SEA_LIZARD_RD_2
```

An authored visual set lists the allowed appearances and a default selection policy. Debug spawning can request an exact `visual_id`; normal encounters can choose deterministically from the set.

This gives access to all 146 prefabs without duplicating stats, AI, loot, and ability kits for color variants.

### 3. Unity visual profile

Evolve `NpcVisualCatalogEntry` into a profile reference rather than accumulating more fields on one flat entry. A first-party `NpcVisualProfile` should define:

- vendor prefab reference
- explicit primary Animator transform path
- native animation-role map consumed by the shared combat-animation request lifecycle
- native instant/charged/channel state families using the same cast-archetype phases as `SpellCastAnimationResolver`
- scale and root/pivot offset
- facing correction, if required
- attachment/socket paths for weapons, cast origins, projectiles, hit VFX, nameplate, and ground effects
- shadow/selection bounds where automatic renderer bounds are poor
- locomotion mode: ground, flying, hovering, swimming
- native status reactions
- optional humanoid retarget permission
- audio cue set

Appearances that share the same skeleton/controller can share an animation profile. Color variants should not duplicate mappings.

This profile is the Generic-creature analogue of the player `CombatAnimationSet`, not a new event or VFX system. Player humanoids continue to resolve cast clips through `SpellCastAnimationResolver` and the stitching model in `docs/spell-cast-animation-stitching-2026-07-09.md`. NPC Generic rigs cannot reuse those humanoid clips or playback layers, but they must consume the same action category and instant/charged/channel phase model. `CombatAnimationRequestTranslator` should be generalized so both `PlayerEntity` and `NpcEntity` receive that shared request before their actor-specific animation adapters resolve it.

VFX stays entirely in the existing cue catalog and dispatcher. NPC casts use the same `SPELL_CAST`, `SPELL_RELEASE`, `SPELL_IMPACT`, `SPELL_FIZZLE`, `MELEE_CAST`, and `MELEE_IMPACT` triggers and the same `ABILITY` / `SPELL` / `MELEE_STRIKE` owner definitions. An NPC visual profile supplies anchors/sockets; it does not define replacement VFX triggers.

### 4. Action kit

An action kit is the explicit list of actions the NPC may choose. Each entry references an action authored in the same combat/progression system used by players. NPC-only attacks and spells are still normal authored combat actions; they are not implemented in `npcs.rs` as special effect code.

That reuse requires an explicit ability-catalog migration; it is not free. The current schema is partly actor-neutral—generic spells may already have an empty `combat_profile_id`, `ability_tags` are optional, and practice actors reuse spell definitions—but player melee authorization still depends on combat profiles/action-bar assignments and the current content is overwhelmingly tagged for the action bar.

Add a first-class `actor_scope` such as `PLAYER`, `NPC`, or `BOTH`, then validate by scope:

- player-available actions follow the current combat-profile, spellbook, equipment, and action-bar authorization rules
- NPC-available actions must be granted by an NPC action kit
- profile-less NPC melee actions are legal and resolve directly by ability ID after the executor is generalized
- no NPC-only action needs a fake weapon loadout, action-bar slot, or `ACTION_BAR_ACTION` tag
- resource kind is required only when the action actually spends an actor resource; free NPC actions remain explicit zero-cost actions
- `BOTH` actions satisfy both authorization contracts while sharing one gameplay definition

This changes progression validation and public catalog shape, so it includes generated-binding regeneration and migration tests. Do not create nominal “NPC combat profiles” merely to satisfy player-era validation if the profile has no real NPC gameplay meaning.

AI metadata belongs beside the kit entry:

- role: melee offense, ranged offense, heal, buff, debuff, defense, mobility, interrupt, summon
- allowed target selector
- base utility
- health/resource thresholds
- preferred tactical distance
- ally/enemy count conditions
- required or forbidden target statuses
- whether movement may be planned to enable the action
- allowed presentation variants

Ranges, cast time, cooldown, damage, resource cost, targeting audience, projectile behavior, and status payload remain properties of the shared authored ability. The AI metadata must not duplicate those gameplay values.

### 5. Brain profile

A brain profile controls decision style rather than effects:

- decision interval
- perception and leash radius
- target stickiness
- threat weights
- preferred engagement band
- retreat/kite tolerance
- support urgency
- deterministic variation amount
- idle/patrol policy
- assist/call-for-help policy

Several templates can share a brain profile while using different kits.

## Shared combat actor boundary

The existing code has already started becoming actor-generic, but its names and a few operations still assume players. Formalize that boundary before adding NPC spells.

Introduce shared helpers for:

- `CombatActorSnapshot`: alive, position, facing, grounding, hit shape, world context, actor kind
- reading and mutating actor health for `PlayerState` or `NpcState`
- actor resources
- combat team/relation
- disabling status and cast interruption
- cooldowns and active casts
- derived combat modifiers

Specific required corrections:

1. Rename/refactor `PlayerSnapshot` and `PlayerSnapshotSet` to describe their actual actor-generic role.
2. Generalize `apply_heal` so a valid NPC ally can receive healing and emit the same effect event.
3. Replace player-only action snapshot validation at the shared executor boundary. Player requests keep rewind/input validation in their adapter; AI uses the authoritative current NPC pose.
4. Generalize resources from a player-only assumption if NPC resource gameplay is desired. NPCs can start with free actions, but the architecture should not permanently forbid a mana-limited caster.
5. Ensure crowd control interrupts NPC active casts and pending actions through the same interruption contract.
6. Keep damage, defense, status, projectile, VFX cue, and effect resolution shared.
7. Move the existing practice-turret call path onto the same server-actor adapter as part of the extraction.

`cast_spell_for(...)` already accepts the empty-token convention for server-controlled practice actors; that convention should remain at the practice adapter. Do not make a real NPC impersonate a player-like actor merely to pass its snapshot/action-bar gates. Extract common validated action execution beneath `cast_spell_for(...)`, then call it from the player, practice, and NPC adapters.

## NPC lag-compensation policy

NPC-initiated attacks use authoritative present-time poses. This is an explicit PvE contract, not an accidental omission:

- NPC intent never supplies an attacker-view timestamp.
- NPC targeting/cast commitment validates the current authoritative caster and target poses.
- A telegraphed NPC melee impact revalidates the target's current pose at impact, preserving the player's ability to step out during the windup.
- NPC projectile collision uses the existing present-time sweep/projectile policy.
- NPC targeted spells use present-time server target validation.
- Target line of sight is checked at commitment/cast time and is not rechecked at impact; moving behind cover after launch remains valid counterplay.

Players attacking NPCs continue to use the existing player-input rewind policy. Do not add victim-side rewind to NPC actions unless a later measured design change deliberately replaces this asymmetric, player-favorable rule.

## Combat teams and relations

The current three-way NPC faction flag is adequate for playground targeting but not for group AI. Extend the existing `server/src/relations.rs::combat_relation(...)` path; do not add a second team/relation resolver beside it.

Every combat actor needs a resolved combat team. Relation is computed between source team and target team:

- same team: ally
- opposed team: hostile
- neither: neutral

Player party/match membership and NPC encounter membership feed this resolver. A playground spawn choice such as “hostile to me” assigns the new NPC to an appropriate debug team; it should not become the NPC's universal relationship to every actor.

This enables:

- hostile NPC healers supporting hostile NPC allies
- friendly NPCs assisting a player team
- competing NPC factions
- neutral creatures that retaliate or become hostile through encounter rules
- correct `HOSTILE`, `PARTY_OR_SELF`, and `ASSISTABLE` target-audience checks

The migration should preserve the existing hostile/neutral/friendly spawn UX while changing the underlying representation.

## Combat geometry contract

NPC actions must use the same server geometry rules as player actions:

- Target line of sight calls the existing `server/src/combat/scene_query.rs::has_line_of_sight(...)` path.
- Bodies never block target LOS. Player and NPC bodies may intercept projectile travel only when the delivery design says they do.
- World query raycasts use terrain/seeded-arena layout plus authored query boxes/meshes. They never use the deliberately oversized movement-collision boxes.
- `server/src/gameplay_query_collision.shared.json` currently has no arena query boxes, so seeded arenas rely on their generated layout raycast until additional query geometry is authored.
- Target LOS is checked when an action is committed or a channel explicitly sustains it, never as a generic impact-time recheck.
- Navigation and chase use movement collision, not LOS query geometry. The two representations have different purposes and must stay distinct.

Perception should gather nearby relation-eligible actors without raycasting every candidate. Score using cheap state first, then run the authoritative range/facing/LOS/action gate only for the proposed winning commitment. If LOS fails, the planner may hold, reposition, or replan on its next bounded decision; it must not perform an NPC-by-action-by-target raycast sweep.

## Utility AI design

### Perception

At a bounded interval, build candidate sets from the existing actor spatial index:

- nearby relation-eligible enemies
- nearby relation-eligible allies
- current target
- home position and leash state
- recent attackers and threat
- relevant health, statuses, cast state, and approximate range

Do not scan every actor for every NPC on every server tick. Decisions can run at roughly 5–10 Hz, staggered deterministically across NPCs. Movement and active action resolution can continue at fixed-tick cadence.

### Hard eligibility

An action is removed before scoring if:

- the NPC is dead or disabled
- the action is on cooldown or conflicts with an active cast/action
- no legal target exists
- the target relation/audience is invalid
- required status/health/count conditions fail
- the action cannot be made reachable under the movement policy

### Scoring

Eligible actions receive a score from authored weights and current context. Typical considerations:

- missing-health fraction for healing
- whether a desired buff is absent
- interruptible enemy cast importance
- distance fit
- number of targets affected
- current target stickiness
- self-preservation urgency
- role preference
- recent action repetition penalty
- small deterministic jitter to avoid robotic synchronization

The highest score proposes the next commitment, which then passes the authoritative range/facing/LOS/action gate. Deterministic jitter is seeded by NPC identity and decision sequence for reproducible simulations, stable debugging, and to prevent groups from making identical choices on the same tick; clients never run the planner.

### Commitment and interruption

Once an action emits its authoritative telegraph, the planner is committed. A later higher utility score cannot silently swap it out mid-windup. Only explicit interruption rules—death, disabling crowd control, target invalidation, authored movement cancellation, or another action-specific interrupt—may cancel it, and cancellation emits the existing fizzle/interruption presentation where applicable.

This makes telegraphs trustworthy counterplay rather than provisional animation.

### Movement intent

The planner can choose an action that is not currently in range, then emit a desired engagement band:

- approach to maximum range
- hold current distance
- retreat/kite to minimum range
- move toward an injured ally
- return home when leashed

The movement executor owns collision and navigation. The action is committed only after range, facing, and line-of-sight validation passes.

### Threat and target selection

Nearest-wins should be replaced with a modest threat model:

- damage creates threat
- healing/buffing allies in combat creates distributed threat
- explicit taunts can override or multiply threat
- proximity supplies initial threat
- target stickiness prevents rapid oscillation
- threat decays or clears outside combat/leash

Keep threat rows server-private. Add a development decision inspector before tuning begins: per NPC, record the last decision sequence, considered actions/targets, hard-reject reasons, component scores, winning commitment, and current threat summary. This may be exposed through a development-only table/overlay and must not become production replication.

### Existing probe compatibility

The S7, S8, and S9 netcode fixtures deliberately manipulate nearest-wins targeting and NPC stop-range geometry. Replacing nearest-wins without migrating those probes would create false netcode failures.

Before threat selection lands:

- add explicit harness control that can pin a fixture NPC's target or seed exact threat
- expose the development decision/threat summary needed for SQL verification
- migrate `ops/s7-lap-probe.py`, `ops/s8-lag-comp-probe.py`, and `ops/s9-auto-rewind-probe.py`
- keep the existing attack-stop-range behavior available through authored movement/action range, not through a legacy production AI branch

S4–S6 do not need a blanket NPC-AI migration: their core fixtures use player-like playground targets. Probe dependencies should be updated individually rather than preserving nearest-wins globally.

## Multiple attack animations

Multiple clips should be handled in two distinct ways.

### Presentation variants

`attack`, `Attack01`, and `Attack02` often represent visual variety for the same basic strike. They should be variants of one gameplay action when damage, range, counterplay, and timing are intended to be equivalent.

The server selects the variant before the telegraph begins and includes the action/ability ID plus variant ID in the authoritative action event. The existing combat-event facts determine whether this is a melee cast, spell cast/release, impact, block, parry, or fizzle. The client maps the shared animation request plus variant through the actor's animation profile. Selection should be deterministic and should avoid immediately repeating the last variant where possible.

The client must not choose a random attack locally; different clients could display different telegraphs.

### Distinct gameplay actions

Clips such as `ShieldBash`, a heavy attack, a leap attack, a bow shot, a heal cast, or a channeled spell should be separate authored abilities when they have different range, windup, damage, status effects, cooldown, defenses, or target rules.

### Timing

Each action/presentation variant needs authored timing:

- telegraph/windup to release or impact
- recovery/lock duration
- optional channel start/loop/end

Authoritative resolution is scheduled from server data. Unity animation events may drive local polish but must not decide when damage occurs.

If two visual variants have materially different release frames, either:

- author distinct server timings for the variants, or
- normalize animation playback speed so both reach the release at the action's shared windup.

An editor validator should compare the actual clip/event timing to the shared timing contract.

## Presentation event contract

Retire the special `NPC_MELEE` animation branch by extending the existing actor-generic presentation path. Do not introduce new VFX triggers such as `MELEE_PRIMARY`, `HEAL`, or `BUFF`.

The server continues to emit the existing `CombatEvent` event/source kinds. `CombatVfxCueResolver` and `CombatVFXDispatcher` continue resolving VFX from `(owner_kind, owner_id, trigger)`; actor kind is irrelevant to cue ownership. The one new replicated fact needed for native animation variety is a presentation variant ID selected before the action begins.

Generalize `CombatAnimationRequestTranslator`, which currently accepts a `PlayerEntity` and player combat profile, into the single event-to-animation-request seam. Resolution then branches only at the final actor adapter:

- players resolve through `CombatAnimationSet`, `SpellCastAnimationResolver`, and existing player playback layers
- NPCs resolve the same request/category/phases through `NpcVisualProfile` into native Generic-controller states
- instant, charged, and channel casts use the same derived cast archetype and start/loop/release stitching contract on both adapters
- interruption/fizzle semantics remain shared even when the concrete controller states differ

The replicated action start must identify:

- caster identity
- gameplay action/ability ID
- presentation variant
- target or aim point
- windup/release timing
- action instance ID

The Unity NPC presenter then performs the same lifecycle as player presentation:

- start/ready/cast animation
- release animation phase
- projectile/VFX spawn through existing dispatchers
- impact response
- channel loop/end
- interruption/fizzle
- return to locomotion/ready

NPC spell and projectile subscriptions must cover the same cast/release/impact/terminal rows used by player casters. Any actor-specific subscription semijoin is part of the presentation contract and must be pinned by focused query-shape tests.

Death has highest priority, then hard crowd control, then action, hit reaction, locomotion, and idle. Hit reactions should not constantly cancel important telegraphs; author an interruptibility policy.

## Animation ingestion and validation

Create an Arena editor audit/authoring tool that scans a selected vendor family and produces a reviewable draft profile. It may suggest mappings from names, but generated guesses must be explicitly saved and validated; runtime fallback-by-name is not acceptable.

For every visual profile, validate:

- prefab loads and instantiates
- primary Animator path resolves unambiguously
- controller and avatar are valid
- every authored native animation-role state exists
- required baseline states exist or have an explicit policy
- attack/cast variant count matches the profile
- clip lengths and release timing are valid
- root motion policy is correct
- scale, ground pivot, facing, bounds, and hit/cast sockets are sane
- materials/renderers survive runtime tint and selection effects
- native status reactions exist when referenced
- humanoid retarget clips are used only with valid Humanoid avatars

Missing animation is handled explicitly:

- no run: use authored walk locomotion at adjusted playback speed
- no ready: use authored idle
- no hit: suppress the flinch and use impact VFX/audio
- no native stun on a Generic rig: use a deliberate frozen-pose/material/VFX policy until a family-specific clip is authored
- no death: block the profile from shipping; every combat NPC needs a death presentation

These are profile decisions, not hidden runtime guesses.

## Spawn and authoring UX

Replace the three hard-coded kobold buttons with a searchable development NPC browser populated from the synchronized gameplay catalog and Unity visual catalog.

Filters should include:

- package/family
- gameplay template
- exact appearance
- combat team/relation
- role: melee, ranged, caster, support
- animation validation state

The spawn reducer accepts a validated `template_id`, optional exact `visual_id`, and debug team/relation. It rejects invalid template/appearance combinations.

“Spawn any NPC” means every one of the 146 imported appearances has an explicit appearance entry and belongs to a fully authored gameplay template. There should be no silent generic-template fallback.

## Movement and navigation

The current direct collision sweep is useful for initial melee parity and open spaces. It cannot reliably route around walls, platforms, or complex dungeon geometry.

Use two stages:

1. Preserve the existing sweep mover behind a generic movement intent for the first behavior milestone.
2. Add server-authoritative path planning against the server's movement-collision/layout source before relying on NPCs in complex levels. Do not path against LOS query boxes/meshes, and do not use a Unity-only NavMesh as gameplay authority.

The long-term movement layer should support:

- ground pathing
- leash/home return
- local avoidance and separation
- ranged distance bands and retreat
- flying/hovering profiles
- unreachable-target detection
- stuck recovery
- encounter boundaries

Movement remains independent from the utility planner: the planner asks for a position/range band; navigation determines how to reach it.

## Recommended initial archetypes

Build four vertical slices before authoring all 35 families:

1. **Kobold Warrior:** preserve current melee, block/parry, loot, status, death, and despawn behavior through the new generic action path.
2. **Skeleton Archer:** ranged basic attack, reload/shot presentation, melee fallback when pressured, and distance-band movement.
3. **Skeleton Wizard:** cast-time projectile or direct spell at range, melee fallback, interrupt/fizzle, and native cast/channel presentation.
4. **Demon Summoner or Lich support:** heal or buff the most suitable allied NPC, use offense when support utility is low, and prove correct NPC-to-NPC relations/effects.

A mixed group of those four proves melee, range, casting, support, target choice, movement bands, shared effects, and group relations. Once that foundation is sound, authoring the remaining families is primarily catalog/profile work rather than new runtime logic.

## Implementation sequence

### Phase 0: asset foundation

Completed now:

- relocate all three vendor packages
- update ignore rules and existing kobold asset paths
- inventory families, prefabs, rigs, attacks, spell clips, and obvious animation gaps

Next:

- create `NpcVisualProfile` and native animation-role mappings behind the shared request translator
- create the editor package audit/validator
- separate `visual_id` from `template_id`
- add catalog-driven searchable spawning
- author profiles for the four exemplar families

Acceptance: every appearance for the exemplar families can be spawned explicitly and pass prefab/Animator resolution, idle, locomotion, hit-or-explicit-impact-response, death, and cleanup checks. Kobold melee also remains functional. Archer/wizard/support action and cast acceptance belongs to Phases 2–3, after those actions exist.

### Phase 1: actor and relation foundation

- generalize actor snapshot/state helpers
- add combat teams and source-target relation resolution
- allow heal and helpful statuses on NPC allies
- make action interruption and cleanup actor-generic
- define NPC resource policy
- pin present-time NPC action validation and shared combat-geometry rules

Acceptance: NPC damage, heal, buff, debuff, death, corpse, and cleanup all use the shared effect/status lifecycle with correct team rules.

### Phase 2: shared action executor

- extract common validated ability execution below player input/prediction gates
- add ability `actor_scope`/authorization semantics so NPC actions do not require fake player profiles or action-bar rows
- update progression validators and regenerate public bindings
- route player requests, practice automation, and NPC intents into that executor
- generalize active cast, cooldown, projectile, release, fizzle, and presentation handling
- generalize `CombatAnimationRequestTranslator` while preserving the existing VFX trigger/resolver pipeline
- replace the permanent `NpcPendingSwing` special path after kobold parity is proven

Acceptance: the same authored action produces equivalent gameplay effects whether initiated by a player adapter or NPC adapter, subject to authorization/resource policy.

### Phase 3: utility AI and tactical movement

- perception and spatial candidate queries
- deterministic staggered decision scheduling
- utility scoring and target selectors
- approach/hold/retreat movement bands
- initial threat and leash behavior
- binding action commitment/interruption semantics
- development decision/threat inspector
- explicit fixture-target/threat harness and S7–S9 probe migration
- four exemplar archetypes

Acceptance: the mixed exemplar group makes sensible choices under controlled health, distance, status, line-of-sight, and cooldown scenarios.

### Phase 4: full package authoring

- explicitly author all remaining gameplay families
- map all 146 appearances
- classify attack variants versus distinct abilities
- author missing reaction policies, sockets, VFX/audio cues, timing, stats, kits, brains, and loot
- add automated spawn/presentation sweeps
- add an `NpcHeadlessAcceptanceRunner` modeled on the existing `S7HeadlessAbRunner` batch-mode pattern

Acceptance: every imported appearance is spawnable and its family passes the baseline functional contract without runtime state-name guessing.

### Phase 5: encounter and navigation depth

- authoritative path planning for complex levels
- group assist/call-for-help
- richer threat, taunt, and support threat
- patrols, guards, fleeing, summoning, formations, and encounter scripts as actual game content requires
- AI and replication load profiling

Acceptance: representative encounters remain correct and performant under multiplayer load and complex geometry.

## Verification gates

### Static/editor

- catalog validation rejects duplicate IDs and broken references
- every template references an existing visual set, action kit, brain, and loot profile
- every appearance references a valid prefab and visual profile
- every native animation-role mapping resolves on its authored Animator
- shared action IDs resolve through the existing progression/combat catalogs
- Unity edit-mode tests cover catalog and event translation contracts

### Server simulation

- deterministic action selection for a fixed seed/snapshot
- heal chooses an injured legal ally and never heals a hostile target
- buff does not recast while the required buff is active
- ranged action yields to melee/repositioning at authored distances
- cooldown, cast, interrupt, LOS, world, team, and leash gates
- action commitments survive utility-score changes and cancel only through explicit interruption rules
- NPC melee, targeted spells, and projectiles follow the authored present-time/commit-time geometry policy
- NPC-vs-player and NPC-vs-NPC damage/status/heal paths
- death, loot, corpse lifetime, and despawn cleanup
- decision scheduling and spatial query performance
- migrated S7–S9 fixtures retain their original netcode evidence meaning under explicit target/threat control

### Unity play mode

- automated sequential spawn sweep for all 146 appearances
- batch-mode execution through a dedicated NPC runner modeled on `S7HeadlessAbRunner`
- primary Animator and native animation-role states
- no unexpected root motion or facing flips
- correct ground pivot, nameplate, selection, health bar, and hit bounds
- deterministic attack variant on two clients
- cast/channel/release/projectile/impact/fizzle presentation
- native and fallback status reaction policies
- death remains visible until authoritative despawn

### Merge gate

- `cargo test`
- `spacetime build -p server`
- regenerate Unity bindings after public schema/catalog changes
- `dotnet build Assembly-CSharp.csproj --no-restore`
- relevant Unity edit-mode and play-mode tests
- `git diff --check`
- automated two-client mixed-archetype acceptance run with captured logs/results

Manual play remains valuable for judging feel, but it is exploratory evidence rather than the only merge gate.

## Risks that should shape implementation

- **Generic rigs:** ten new families lack native stun clips, and none of the new rigs can use the kobold Humanoid retarget assumption.
- **Animation timing:** visual variety becomes unfair if the hit timing does not match the selected clip.
- **Presentation drift:** a creature-only cue vocabulary or cast-stitching state machine would compete with the existing combat event, VFX, and spell-animation systems. NPC visuals must remain adapters to those shared contracts.
- **Ability authorization:** profile-less NPC melee and NPC-only abilities require deliberate progression-schema/validator work; fake action bars or weapon profiles would bake player-era concepts into AI.
- **Multiple Animators:** automatic first-child selection can animate the wrong object.
- **Root motion:** authoritative movement must remain on the server; imported root motion cannot move gameplay independently.
- **Navigation:** direct chase will look broken around complex obstacles even if action selection is correct.
- **Geometry drift:** movement collision and LOS query geometry are intentionally different; using either for the other's job creates pathing or visibility defects.
- **Authoring volume:** 146 appearances are manageable only through shared family profiles, generated draft data, and strict validation.
- **Package availability:** the vendor packages are ignored and total several gigabytes. A clean checkout/CI machine needs a documented licensed import procedure or an approved asset distribution strategy.
- **Performance:** support targeting and threat can turn into NPC-by-actor scans unless they use the shared spatial index and staggered decisions.
- **Probe regressions:** S7–S9 currently depend on nearest-wins fixture control and must migrate alongside threat selection.

## Definition of done

The NPC system is complete for these packages when:

- all 146 appearances are discoverable and spawnable through catalog data
- each appearance belongs to an explicitly authored gameplay family
- every family has valid idle/locomotion/action/hit-or-impact/death/status presentation policies
- melee, ranged, caster, and support archetypes use the shared combat pipeline
- NPC VFX and cast phases use the existing combat-event triggers, cue dispatcher, animation request, and cast-archetype lifecycle
- multiple attack variants are authoritative, synchronized, and timing-correct
- NPCs can correctly target enemies and assist allies
- healing, buffs, damage, statuses, defenses, projectiles, VFX, loot, death, and despawn work for NPC actors
- behavior remains sensible with two clients and mixed NPC groups
- present-time NPC hit validation, commit-time LOS, and movement/query geometry responsibilities are explicit and tested
- headless acceptance covers all appearances and the mixed exemplar encounter
- no runtime code contains package-specific template switches, silent state-name fallbacks, or a parallel NPC combat/presentation pipeline
