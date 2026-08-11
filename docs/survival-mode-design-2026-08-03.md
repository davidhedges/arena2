# Survival Mode Design

Date: 2026-08-03
Status: design approved, unimplemented. Owner rulings recorded in §11.
Revised 2026-08-03 after a design review; all ten findings verified against the
tree and resolved — see §1.1. Slice 0 (instance kind) was added as a result.

Implementation note (2026-08-12): the authored scene created by this design is
now the mode-neutral `Arena_Map_01` map. Survival still owns its rules and may
select that map, but it no longer owns the Unity scene identity.

## Outcome

A solo, round-based survival mode. NPCs spawn into a flat arena and aggro the
player instantly. Kills pay gold scaled to the NPC's threat rating. Between
rounds the player spends gold in a shop on stat modifiers and real equipment.
Difficulty escalates by pulling harder templates and, past the roster ceiling,
by scaling stats. Every fifth round is a boss. Death ends the run.

The mode is deliberately built as a **new instance kind that composes existing
systems**, not as a parallel combat, NPC, inventory, or progression stack.

## 1. What already exists (verified, not assumed)

Measured against the tree at `a55f7f69`.

| Need | Existing seam | State |
|---|---|---|
| Private per-player world | `PracticeInstance` + `ArenaInstance` + `PlayerWorld` | reuse **shape**, but survival needs its own instance kind — §1.1 F1 |
| Client scene load | `world_kind == INSTANCE` → `Arena_Map_01` map | shared map identity; Survival rules remain separate |
| NPC roster | 92 templates / 329 appearances, `max_hp` `move_speed` `aggro_radius` `attack_windup_ms` | reuse as-is |
| Ability damage | `gameplay.base_damage` (MELEE), `gameplay.delivery.damage` (SPELL) | reuse as-is |
| Permanent aggro | `NpcTargetOverride` — absolute, un-droppable pin | reuse, needs internal entry point **and ownership fix** — §1.1 F2 |
| Kill attribution | death carries the killing source identity, `combat.rs:4407` | reuse as-is |
| NPC stat scaling | `NpcState.max_hp` is per-instance; `temporary_modifiers.damage_multiplier_for(source, …)` is actor-generic (`combat.rs:4431`) | reuse as-is |
| Item generation | `roll_item_affixes`, `LootRollContext { hidden_loot_quality }` | reuse for shop stock; **`drop_chance` cannot be zeroed** — §1.1 F8 |
| Equip / unequip | `equip_item`, `unequip_item` | reuse **only** if the run bag is `CONTAINER_KIND_PLAYER_BAG` — §1.1 F4 |
| **Weapon swap → action bar** | `equip_item` → `sync_progression_for_equipment_change` → re-syncs combat mode **and re-derives action-bar assignments** | **reuse as-is, no new plumbing** |
| Player death/respawn | `respawn_at`, `resolve_respawn_pose` per instance | reuse as-is |
| Round scheduling | `ScheduleAt` scheduled-reducer pattern in `game_loop.rs` | reuse as-is |
| Stat upgrades | `equipment_modifier_totals_for_owner` | **cannot** carry non-item upgrades — §1.1 F6 |

### What does not exist

1. **No currency.** Nothing in the server matches gold/coin/currency. New.
2. **No XP or character levels.** `progression.rs` is a catalog, not an
   advancement ledger. Upgrades cannot hang off it.
3. **Invisibility does not affect aggro.** `MODIFIER_STEALTH` /
   `stealth_aggro_reduction` is authored in `inventory.rs` and **read by
   nothing** — `inventory.rs` is the only file that mentions it.
   `COMBAT_MODE_STEALTHED` is a dagger profile toggle, unrelated.
4. **Leash fights permanent aggro.** Both brain profiles carry
   `leash_radius` 24–28 with return-home.
5. **No pathfinding.** Chase is collision-aware stepping only; navigation,
   unreachable-target recovery and local avoidance are explicitly unimplemented
   in `docs/npc-system-design-2026-07-11.md`. **Mitigated by owner ruling: the
   arena is flat and open** (§11.1).
6. **Summon and mobility execution are unimplemented.** `DEMON_SUMMONER`
   cannot summon. §5.3 designs the seam; §10 defers the execution.
7. **Only two brain profiles exist.** A boss currently thinks like a kobold.

## 1.1 Design review, 2026-08-03 — findings and resolutions

A review of the first draft falsified several "free reuse" claims. All ten
findings were verified against the tree and **all ten were correct**. The
sections below are written to their resolutions; this table is the index.

| # | Finding | Verified at | Resolution |
|---|---|---|---|
| F1 | `is_practice = false` makes survival read as **PvP arena** — client sets `IsArenaMode` for any non-practice instance, the lobby lists it, and player death runs match-conclusion/winner logic | `MatchStateCache.cs:42`, `LobbyController.cs:209`, `combat.rs:5698` | explicit `instance_kind` column, §2.1 |
| F2 | Survival NPCs spawned with `spawned_by = player` let that player **clear the aggro pin or despawn the wave** through public reducers | `npcs.rs:1126`, `npcs.rs:1147` | system ownership + reducer guards, §8.1 |
| F3 | Flat-ground collision is granted **only** to training instances; the seeded arena has ruin walls, platforms, ramps and pillars | `game_loop.rs:1535`, `npcs.rs:3080`, `arena_layout.shared.json:4` | survival layout + policy extension, §2.2 |
| F4 | `equip_item`/`unequip_item` accept **only** `PLAYER_BAG`; `move_item` lets items cross between any accessible containers | `inventory.rs:1990`, `inventory.rs:1674` | run bag *is* a `PLAYER_BAG`; parking + provenance, §9 |
| F5 | Disconnect calls `clear_inventory_for_owner`, which deletes **every** container and item for the owner | `player.rs:145` → `actor_lifecycle.rs:286` → `inventory.rs:2510` | ordering + aggregate snapshot, §9.2 |
| F6 | `SurvivalRun` was private, so the HUD could not read round/timer/gold; and `equipment_modifier_totals_for_owner` aggregates **only affixes on equipped items**, so bought stat upgrades cannot reach it | `inventory.rs:2340` | public run view + upgrade ledger, §6 |
| F7 | Schema could not distinguish a boss death from an add death, had no `gold_earned`, and no summoner quota state | design draft §2 | fields added, §2 |
| F8 | `drop_chance` is clamped to `.clamp(0.02, 0.35)` — **zero is unreachable** — and the corpse container is created unconditionally before any loot roll | `inventory.rs:2816`, `combat.rs:4407` | corpse-less death branch, §6.1 |
| F9 | The rating script used template-level windup while the runtime uses **action-entry overrides** (264 of 299 entries override windup) | `npcs.rs:3156` | script fixed, §4 |
| F10 | "Seed + round" cannot reproduce sequential draws across ticks; budget exhaustion was undefined | design draft §5.1 | `spawn_sequence` + exhaustion rule, §5.1 |

### Second review round, 2026-08-03

A review of the revision found nine more blockers. All nine verified; two
carried factual errors of their own, noted below.

| # | Finding | Verified at | Resolution |
|---|---|---|---|
| F11 | The flat-layout audit named **two** sites; there are **nine files** consuming `is_training_instance`, plus `flat_ground_only` threaded through `world_collision.rs` | `arena.rs:580`, `npcs.rs:1072`, `scene_query.rs:969`, `casting.rs:3032`, `melee.rs:2263`, `game_loop.rs:1535`, `npcs.rs:3080`, `world_obstacles.rs:69`, `combat.rs` | one canonical predicate, §2.2 |
| F12 | Parking a second `PLAYER_BAG` is impossible — `player_bag_container_id` derives **one fixed id per owner**, and the client returns the first owned bag | `inventory.rs:4946`, `InventoryScreen.cs:1325` | reuse the singleton, §9.1 |
| F13 | Public `spawn_npc` still lets a player inject NPCs — including `FRIENDLY` allies — into their own survival instance, bypassing `SurvivalNpc`, the cap and the ceiling | `npcs.rs:1017` | reject in-survival callers, §8.1 |
| F14 | Disconnect semantics contradictory: §6.3 promised offers survive reconnect while §9.2 said inventory is session-scoped; disconnect also **deletes the arena** at zero players and cleans only player-owned NPCs | `arena.rs:370` | disconnect abandons the run, §3.1 |
| F15 | Adding `instance_kind` alters a **public** row type, which the repo's own publish contract says auto-migration rejects | `spellbook-composer-design-2026-07-20.md:38` | explicit cutover, §2.1 |
| F16 | Shop had no offer count, weighting, modifier values, stack caps, prices or quality curve — slice 3 would require inventing economy rules | design draft §6.3 | concrete economy, §6.4 |
| F17 | Spell cadence still diverged: script used a fixed `cast_time + 250`, runtime applies `npc_action_recovery_ms` after the cast | `npcs.rs:1671` | script fixed, §4 |
| F18 | No lifetime rule for offers and pre-rolled item aggregates; and deleting `SurvivalRun` at teardown leaves the results screen with nothing to read | design draft §2 | rollover + `SurvivalResult`, §3.2 |
| F19 | Slice 1 routes every 5th round into `BOSS`, but boss spawning ships in slice 5 — slice 1 would hang at round 5 | design draft §12 | explicit slice-1 rule, §12 |

### Third review round, 2026-08-03

| # | Finding | Verified at | Resolution |
|---|---|---|---|
| F20 | The stash snapshotted **only bag contents**. Equipping deletes the item's `InventorySlot` and stores its id in `EquipmentLoadout`, so equipped items have no bag slot and were missed entirely — leaving real gear equipped during the run, displaceable into the run bag, with slot order unrestorable and dangling loadout references on teardown | `inventory.rs:2020`, `4691` | full equipment snapshot + ordered teardown, §9.1/§9.2 |

**F20 is worse than a restore bug — it breaks an owner ruling.** With real gear
still equipped, the player enters a run wearing their own equipment on top of
the starter kit, so §11.9's *fixed starter kit* never holds and no two runs are
comparable. It is a functional failure, not only a data-integrity one.

**Two corrections to the review itself.** F17 stated spell entries fall back to
a *500ms* template default; the authored value is **900ms** (`attack_recovery_ms`
is 900 across every template, and 0 of 25 spell entries override it). And F11
understated the problem — it listed six sites; there are nine files plus the
`world_collision.rs` parameter chain.

### A correction to the first draft's risk framing

The draft called the equipment stash "the highest-risk piece… it can damage
persistent character state." **That was wrong, and F5 is why.** Player
inventory is *not* persistent today: `client_disconnected` tears down the actor
bundle, which calls `clear_inventory_for_owner`, and `client_connected` re-grants
starter equipment. Inventory is session-scoped.

This makes the stash *less* dangerous than claimed — there is no durable
inventory to corrupt — but it changes the design: **restore must complete
before actor teardown**, and stashing bare item IDs is useless because the
items themselves are deleted. §9.2 is rewritten accordingly.

## 2. Architecture: a sibling of the training instance

`practice.rs` is the proven pattern for "private instance + server-owned actors
+ spawn overrides + respawn pose + teardown". Survival copies its shape.

```
PlayerWorld.world_kind = INSTANCE
  └─ ArenaInstance (seed, instance_kind = SURVIVAL, phase = IN_PROGRESS)
       └─ SurvivalRun          ← run/round state machine, gold, budget
            ├─ SurvivalNpc     ← per-NPC run membership, origin, role, payout
            ├─ InventoryContainer (kind PLAYER_BAG, instance-scoped)   [F4]
            └─ SurvivalStash   ← the character's parked item aggregates [F5]
```

`survival.rs` is a new module. It must not fork combat, NPC AI, inventory or
progression; every gameplay effect routes through the existing shared paths.

### 2.1 Survival must be its own instance kind [F1]

`is_practice: bool` cannot express a third kind, and every existing consumer
reads "not practice" as "PvP arena". Add an explicit column:

```rust
// ArenaInstance gains:
pub instance_kind: String,   // ARENA | PRACTICE | TRAINING | SURVIVAL
```

`is_practice` stays as a derived/compatibility field so existing readers do not
break in the same change. **`instance_kind` is the canonical discriminator** and
every classification site must be migrated to it. A side-table check in the
`is_training_instance` style is *not* sufficient here, because the client needs
the classification too and `ArenaInstance` is already public while
`PracticeInstance` is not.

**Publication is a cutover, not an additive change [F15].** `ArenaInstance` is
public, so adding a column alters a live row type. Per the repo's publish
contract, SpacetimeDB auto-migration accepts *new tables* on a data-preserving
publish but **not altered row types on existing tables**. Keeping `is_practice`
does not make the row compatible — the row type still changed.

Slice 0 therefore picks one, explicitly:

- **Preferred — reset cutover.** `ops/republish-local-clear.sh` with its default
  `--delete-data=always`, plus `spacetime generate` for the bindings. Arena
  instances are runtime rows, not authored content; nothing durable is lost.
  Player inventory is session-scoped anyway (§1.1), so the blast radius is a
  local DB reset.
- **Fallback — additive side table.** If a data-preserving publish is ever
  required, add a public `SurvivalInstanceMarker { arena_id }` new table instead
  and classify by join. Costs a join at every site and leaves the
  PRACTICE/TRAINING ambiguity unresolved; take it only under that constraint.

**The PRACTICE/TRAINING mapping, resolved.** These are not parallel values today
— **TRAINING is a strict subset of PRACTICE**. `ensure_training_instance` calls
`create_arena_instance_with_options(…, is_practice = true)` *and* inserts a
`PracticeInstance` row. The migration is therefore total and unambiguous:

| Existing state | `instance_kind` |
|---|---|
| `is_practice = true` **and** a `practice_instance` row exists | `TRAINING` |
| `is_practice = true`, no `practice_instance` row | `PRACTICE` |
| `is_practice = false` | `ARENA` |
| newly created by survival | `SURVIVAL` |

Mandatory consumer audit — none of these may be skipped:

| Site | Current behavior | Required |
|---|---|---|
| `combat.rs:5698` `is_live_arena_match` | true for any non-practice, non-training `IN_PROGRESS` instance → **runs winner/match-conclusion logic on survival death** | exclude `SURVIVAL` |
| `MatchStateCache.cs:42` `IsArenaMode` | `_hasInstanceData && !_isPractice` | add `IsSurvivalMode`; `IsArenaMode` must exclude it |
| `LobbyController.cs:209` | lists every non-practice, non-`ENDED` instance | exclude `SURVIVAL` — runs are private |
| `arena.rs` `start_match` | rejects practice only | reject `SURVIVAL` |
| match overlay / countdown UI | keyed off `IsArenaMode` | must not drive survival |

Slice 1 is not complete until a grep for `is_practice` in classification
contexts returns only compatibility shims.

### 2.2 The flat arena is authored, not inherited [F3]

The premise that makes this mode viable without pathfinding is a flat, open
arena. **The seeded arena is not that**: `arena_layout.shared.json` authors
ruin walls, two raised platforms, ramps and pillars, and flat-ground collision
is granted only to training instances at two separate sites.

Required in slice 1:

1. **A survival arena layout** — its own layout JSON with no walls, platforms,
   ramps or pillars, or an explicit empty-obstacle layout for survival seeds.
   Do not reuse `arena_layout.shared.json`.
2. **One canonical flat-layout predicate** — see below.
3. **A client scene** that does not instantiate the arena props, or an
   `ArenaMatch` variant gated on `instance_kind == SURVIVAL`.

Until all three land, the no-pathfinding mitigation in §11.1 does not hold and
NPCs will wedge on geometry.

**The predicate is duplicated nine times, not twice [F11].** The revision
claimed two sites. That was wrong, and it is the most dangerous kind of wrong:
each missed site produces a *different* symptom — invisible ruins, wrong spawn
heights, LOS through geometry that is not there, gap-closes into nothing —
rather than one obvious failure.

Every site below re-derives the same question with its own near-duplicate
helper:

| Site | Local helper | Symptom if missed |
|---|---|---|
| `game_loop.rs:1535` | `uses_flat_training_collision` | players collide with invisible ruins |
| `npcs.rs:3080` | `npc_movement_world` | NPCs path around geometry players walk through |
| `arena.rs:580` | `use_flat_training_layout` | players spawn at wrong heights |
| `npcs.rs:1072` | `flat_ground_only` | NPCs spawn embedded or floating |
| `scene_query.rs:969` | `flat_ground_only_for_identity` | LOS blocked by absent walls |
| `casting.rs:3032` | `uses_flat_training_collision` | special movement stops mid-air |
| `melee.rs:2263` | `gap_close_uses_flat_training_collision` | gap-close lands short |
| `world_obstacles.rs:69` | inline `matches!` | obstacle set disagrees |
| `combat.rs` | respawn path via `is_training_instance` | respawn height wrong |

Beyond these, `flat_ground_only` is threaded as a **parameter** through
`world_collision.rs` in a dozen signatures, so the value must be correct at
every entry point above — the collision layer itself has no way to recover it.

Slice 1 introduces `instance_uses_flat_layout(ctx, instance_id) -> bool` as the
single definition, returns `TRAINING || SURVIVAL` from it, and **replaces all
nine local helpers with calls to it**. Adding a second condition to two of nine
duplicated predicates is how this mode ships with subtly wrong geometry.
Verification: a grep for `is_training_instance` outside `practice.rs` and the
new predicate must return zero hits in collision/spawn/query contexts.

### Tables

`SurvivalRun` is **public** so the HUD can observe round, timer, gold and kills
[F6]. It carries no secrets — the upcoming spawn table lives in private
director state, not here.

```rust
#[table(accessor = survival_run, public)]
pub struct SurvivalRun {
    #[primary_key] pub arena_id: u64,
    #[index(btree)] pub owner: Identity,
    pub round: u32,
    pub phase: String,              // INTERMISSION | ACTIVE | BOSS | ENDED
    pub round_started_at: Timestamp,
    pub round_ends_at: Timestamp,   // ignored while phase == BOSS
    pub gold: u64,                  // current balance
    pub gold_earned: u64,           // lifetime this run, for SurvivalScore [F7]
    pub kills: u32,
    pub director_alive: u32,        // DIRECTOR-origin only; see §5.2
    pub total_alive: u32,           // all origins; safety ceiling
    pub budget_remaining: f32,
    pub spawn_sequence: u32,        // monotonic draw index; see §5.1 [F10]
    pub next_spawn_at: Timestamp,
    pub boss_identity: Option<Identity>,  // set in BOSS phase only [F7]
    pub seed: u64,
}

#[table(accessor = survival_npc)]
pub struct SurvivalNpc {
    #[primary_key] pub identity: Identity,
    #[index(btree)] pub arena_id: u64,
    pub origin: String,             // DIRECTOR | SUMMON
    pub role: String,               // ADD | BOSS  [F7]
    pub summoner: Option<Identity>, // quota owner for SUMMON origin [F7]
    pub summon_depth: u32,          // 0 for DIRECTOR
    pub rating: f32,                // frozen at spawn, includes round scaling
    pub gold_value: u32,            // 0 for SUMMON; see §5.3
    pub round: u32,
}

#[table(accessor = survival_summon_quota)]
pub struct SurvivalSummonQuota {    // per-summoner, per-round lifetime cap [F7]
    #[primary_key] pub summoner: Identity,
    #[index(btree)] pub arena_id: u64,
    pub round: u32,
    pub summoned_this_round: u32,
}

#[table(accessor = survival_upgrade)]
pub struct SurvivalUpgrade {        // run-scoped stat buys; merged in §6.2 [F6]
    #[primary_key] pub key: String, // arena_id + modifier_id
    #[index(btree)] pub arena_id: u64,
    pub modifier_id: String,        // MODIFIER_* vocabulary
    pub stacks: u32,
    pub total_value: f32,
}

#[table(accessor = survival_shop_offer, public)]
pub struct SurvivalShopOffer {      // deterministic stock for one intermission
    #[primary_key] pub offer_id: String,   // arena_id + round + slot
    #[index(btree)] pub arena_id: u64,
    pub round: u32,
    pub kind: String,               // MODIFIER | ITEM
    pub modifier_id: String,        // MODIFIER kind
    pub item_instance_id: String,   // ITEM kind — pre-rolled, unowned
    pub price: u64,
    pub purchased: bool,
}

#[table(accessor = survival_stash)]
pub struct SurvivalStash { /* parked item aggregates — see §9.2 [F5] */ }

#[table(accessor = survival_score, public)]
pub struct SurvivalScore {          // the ONLY row that outlives a run
    #[primary_key] pub owner: Identity,
    pub best_round: u32,
    pub best_kills: u32,
    pub best_gold_earned: u64,
    pub runs_played: u32,
}
```

## 3. Run and round lifecycle

The run is a server-authoritative state machine ticked from the existing game
loop. No client timer is trusted for anything.

```
start_survival_run
  └→ INTERMISSION (round 0, shop open, player invulnerable)
        └─ ready_for_next_round ─→ ACTIVE  (round n, 60s timer)
                                     │
                    timer expires ───┤─── all spawns dead + budget spent
                                     ↓
                            despawn ALL survivors
                                     ↓
                              INTERMISSION (round n+1)
                                     │
                        every 5th round → BOSS (no timer)
                                     │
                          boss dies ──┘
                    player dies (any phase) → ENDED
```

Rules:

- **60-second rounds.** At expiry the round ends regardless of what is alive.
- **Survivors are despawned at round end** (owner ruling §11.2) — every origin,
  including summons. Each round is a clean slate.
- **Boss rounds have no timer.** They end when the boss dies; remaining adds are
  then despawned. A timer would let a boss survive by attrition. The transition
  fires on `death_identity == SurvivalRun.boss_identity` — **not** on "an NPC
  with `role = BOSS` died" and never on an add death [F7]. `boss_identity` is
  set when the boss is spawned and cleared on phase exit.
- **The intermission has no timer.** The player presses Ready. Shop is open,
  the player is invulnerable, and no NPCs exist to freeze.
- **Death ends the run** in any phase → `ENDED`, teardown, results screen.
- Gold is kill-only. Kiting for 60s is self-punishing and needs no anti-farm
  rule.

### 3.1 Disconnect abandons the run [F14]

The revision was self-contradictory: §6.3 promised shop stock identical across a
reconnect while §9.2 established that inventory is cleared on disconnect. Both
could not hold. **Disconnect ends the run.** Resolved in favour of the simpler
rule, and it is the only one consistent with what the server already does:

- `client_disconnected` → `despawn_actor_bundle` → `clear_inventory_for_owner`
  destroys the run's items regardless of intent.
- `remove_identity_from_current_instance` decrements `player_count` and, at
  zero, **deletes the `ArenaInstance` row outright** (`arena.rs:370`). The run's
  host is gone before any survival code would run.
- Pure roguelite already means nothing but score persists.

Preserving a run would mean re-granting items, re-pinning NPCs and resurrecting
a deleted arena — a "resume" feature, not a disconnect fix. Out of scope.

**Teardown ordering.** Every step is required, in this order, driven from the
survival teardown hook placed *ahead of* actor teardown:

1. Commit `SurvivalScore` from the run's `round` / `kills` / `gold_earned`.
2. Write `SurvivalResult` (§3.2) if the client still needs it.
3. Despawn all `SurvivalNpc` rows **by the synthetic owner**, not the player.
   `despawn_all_npcs_for_owner(player)` will not find them — they are not the
   player's. This is the step most likely to be missed, and it leaks NPCs into a
   deleted arena.
4. Clear run equipment, delete `SurvivalRunItem` items and child rows, then
   restore the stash — the six ordered steps in §9.2. Order is load-bearing.
5. Delete `SurvivalShopOffer` rows and their unowned pre-rolled item aggregates.
6. Delete `SurvivalSummonQuota`, `SurvivalUpgrade`, `SurvivalNpc`, `SurvivalRun`.
7. Only then let the normal instance path delete the arena.

The same ordered teardown serves death, leave-instance and disconnect. It is one
function with one caller per path, not three implementations.

### 3.2 Row lifetimes [F18]

The claim that `SurvivalScore` is the only row outliving a run was false as
written — offers and their pre-rolled items had no deletion rule at all.

- **Offers roll over.** Entering an intermission deletes the previous round's
  `SurvivalShopOffer` rows and any **unpurchased** pre-rolled item aggregates.
  Purchased items are now run items and are owned by `SurvivalRunItem`.
- **Results outlive the run row.** Teardown deletes `SurvivalRun`, so a results
  screen reading it would find nothing. A small `SurvivalResult` row (owner,
  round reached, kills, gold earned, ended_at) is written at teardown and
  deleted when the player next starts a run or leaves. It is the one deliberate
  exception to "only score persists", and it exists because the alternative —
  keeping the whole run alive after `ENDED` — keeps the director state machine
  reachable after teardown.

## 4. The NPC rating system (derived, not authored)

Authoring a difficulty number onto 92 templates is content debt that rots on
every catalog edit. The rating is **computed from data the catalog already
carries**:

```
dps    = base_damage / cycle                    (MELEE)
       = delivery.damage / cycle                (SPELL)
cycle  = max(cooldown_ms, windup + recovery, 400ms)   melee
       = max(cooldown_ms, cast_time + 250, 400ms)     spell
rating = 100 · (hp/hp_med)^0.5
             · (dps/dps_med)^0.65
             · (1 + 0.35·(speed − speed_med)/speed_med)
             · (1.15 if effective range > 5m else 1.0)
```

**Cadence must mirror the runtime [F9, F17].** Both branches took two passes to
get right:

- *Melee* — `windup`/`recovery` come from the **action-kit entry** when nonzero,
  falling back to the template, exactly as `npc_action_windup_ms` /
  `npc_action_recovery_ms` do. 264 of 299 entries override windup.
- *Spell* — the first fix left a hardcoded `cast_time + 250ms`. The runtime
  instead stamps the movement hold at cast end **+ `npc_action_recovery_ms`**
  (`npcs.rs:1671`) — the same entry-then-template fallback. The authored
  fallback is **900ms** (every template authors `attack_recovery_ms: 900`, and
  0 of 25 spell entries override it), so the constant was off by 650ms.

*Measured note: neither fix moved a single rating, because `cooldown_ms`
dominates the cadence term for all 299 melee entries and all 25 spell entries in
the current catalog. The claim "correct by construction" was premature when
first made — it applied to melee only — and holds now that both branches mirror
the runtime.*

**Known modelling gap.** The runtime planner selects by utility, distance,
health and cooldown gates, so an NPC does not always use its best action. The
rating is a deliberate **upper bound on sustained threat** — an ordering over
the roster, not a damage prediction. Do not tune encounter difficulty by
treating it as predicted DPS.

**How Rust consumes it.** The Python script is the source of truth for the
*formula*; the server must not re-implement it. Slice 2 has the script emit a
checked-in `survival_ratings.shared.json` that the server includes, plus a
drift check that fails when the artifact disagrees with a regeneration. Two
independent implementations of this formula would diverge silently.

`hp_med = 155`, `dps_med = 6.38`, `speed_med = 5.3` over the current roster.
Exponents below 1 damp the extremes so a single 650hp outlier does not swamp
the curve. Ranged gets a flat premium because it is harder to disengage from.

Regenerate with `ops/survival-npc-ratings.py`. **Never hand-copy these numbers
into code** — the script is the source of truth and the catalog moves.

### Current tiers

Near-even quintiles (18/18/17/20/19), every template rated, no degenerate
entries. Regenerate with `ops/survival-npc-ratings.py`:

| Tier | Rating | n | Examples |
|---|---|---|---|
| I | 54–81 | 18 | `SLIME` (54), `TOMB_SHADE` (54), `SPIDER` (64) |
| II | 82–91 | 18 | `DEMON_MINION_ONE_HANDED` (82), `BANSHEE` (83) |
| III | 92–107 | 17 | `DEMON_SUMMONER` (92), `GRAVEDIGGER` (93) |
| IV | 111–129 | 20 | `SLIME_MAN` (111), `UNDEAD_RAT` (111), `VAMPIRE` (111) |
| V | 133–316 | 19 | `HELLGUARD_ARMORED` (133), `LICH_BOSS` (174), `DEMON_BOSS` (176), `DRAGON` (274), `ELDER_DRAGON` (316) |

### The finding that shapes boss design

**The named bosses are not boss-shaped.** `DEMON_BOSS` rates 176 and
`LICH_BOSS` 174 — only ~1.75× median trash — and the entire roster spans just
5.9×. Dropped in unmodified, a boss round would feel like fighting two kobolds.
Boss rounds therefore require an **explicit multiplier**, not a template swap
(§7). Corollary: `DEMON_SUMMONER` rates *below* median at 92 and cannot summon
yet — it is not boss material. The dragons (234–316) are the real heavies.

## 5. The round director

### 5.1 Budget and band

```
budget(r) = 150 · r^1.15                      rating points to spend
band(r)   = [ 54 + 4·(r−1),  82 + 9·(r−1) ]   clamped to [54, 316]
```

Round 1 buys ~150 points from tier I. Round 10 buys ~2120 points from tiers
III–V. The band slides so early trash stops appearing rather than diluting
later rounds.

Spawns **trickle** from authored edge spawn points across the round rather than
arriving as one alpha strike, pacing to `next_spawn_at`.

**Determinism [F10].** "Seed + round" is not enough: the director draws
sequentially across many ticks, so replay needs a stored draw index. Each draw
uses `hash(seed, round, spawn_sequence)` and increments
`SurvivalRun.spawn_sequence`. The sequence resets per round. Without this a
replay diverges the moment tick boundaries shift.

**Budget accounting [F10].** The **scaled** rating is deducted — the same value
frozen into `SurvivalNpc.rating` — so §5.4's multipliers make late rounds spawn
correspondingly fewer bodies rather than compounding budget and stats.

**Exhaustion [F10].** A round stops spawning when **no eligible candidate is
affordable**, i.e. `budget_remaining < min(scaled_rating)` over the current
band — not when the budget reaches zero, which it generally never does. The
residue is discarded, not carried. Combined with the cap, a round therefore
ends on whichever comes first: the 60s timer, or exhaustion with every spawned
NPC dead.

### 5.2 The concurrency cap

`DIRECTOR_CAP = 14` concurrent director-origin NPCs. The director stops
spawning while `director_alive >= DIRECTOR_CAP` and resumes as the player
kills. This is a **tick-budget protection**, not a difficulty dial — NPC
decision load at 30Hz is already on the perf board
(`docs/perf-opportunities-2026-07-11.md`).

### 5.3 Summons bypass the cap — and the ceiling that keeps that safe

Owner ruling §11.3: some NPCs will summon regardless of the cap. Origin is
therefore a first-class field:

| Origin | Counts to `director_alive` | Spends budget | Pays gold |
|---|---|---|---|
| `DIRECTOR` | yes | yes | yes |
| `SUMMON` | **no** | no | **no** |

Three guards make an uncapped source survivable. An unbounded spawn source on
an authoritative 30Hz server is a stability hazard, not just a balance one:

1. **`TOTAL_ALIVE_CEILING = 40`** — a hard ceiling every origin respects,
   including summons. Distinct from the gameplay cap; it exists only so the
   tick cannot be driven off a cliff. A summon that would breach it fails
   silently.
2. **`MAX_SUMMON_DEPTH = 1`** — a summoned NPC cannot itself summon. Without
   this, one summoner-that-summons-summoners is unbounded growth.
3. **Per-summoner lifetime quota** — each summoner may produce a bounded number
   of adds per round.

**Summoned NPCs pay zero gold.** Otherwise a summoner is an infinite gold farm
and the shop economy collapses. They still count as kills.

### 5.4 Scaling past the roster ceiling

From round 12, applied at spawn:

```
hp_mult  = 1 + 0.06·(r − 11)
dmg_mult = 1 + 0.04·(r − 11)
```

HP is a direct `NpcState` write. Damage rides the existing actor-generic
temporary damage-multiplier seam — no NPC-only damage path. The frozen
`SurvivalNpc.rating` includes the multiplier, so scaled NPCs automatically pay
more gold without a separate payout rule.

## 6. Gold and the shop

```
gold_value = ceil(rating · 0.35)
```

19g at the tier I floor, ≈35g at the median, 47–111g across tier V, topping out
at 111g for `ELDER_DRAGON`. Paid to the killing identity on death, resolved
from the existing death source. `ops/survival-npc-ratings.py --all` prints the
per-template payout alongside the rating.

The shop sells both (owner ruling §11.4):

- **Stat modifiers** — stacking buys from the existing `MODIFIER_*` vocabulary
  (`PHYSICAL_DAMAGE`, `MOVE_SPEED`, `FORTITUDE`, `CRIT_CHANCE`, resistances…).
- **Equipment** — real item instances rolled through `roll_item_affixes` with
  `LootRollContext.hidden_loot_quality` scaled by round, so late-run stock is
  better. **Including weapons** (owner ruling §11.5).

Unspent gold carries across rounds. There is no obligation to spend.

### 6.1 Survival needs a corpse-less death path [F8]

The first draft claimed `drop_chance = 0` suppresses loot. **It cannot.** Two
independent reasons, both verified:

1. `npc_loot_roll_context` clamps to `.clamp(0.02, 0.35)` — zero is
   unreachable, so every corpse keeps a 2% floor.
2. `create_corpse_loot_for_npc` is called **unconditionally** on NPC death
   (`combat.rs:4407`) and creates the container *before* any roll, immediately
   followed by `schedule_npc_corpse_despawn`. Even a genuine zero roll leaves an
   empty container and a despawn timer per kill.

So survival branches **before** corpse creation: on death of an NPC holding a
`SurvivalNpc` row, skip `create_corpse_loot_for_npc` entirely and use a
corpse-less despawn. This is one branch at the death site, not a change to
loot-roll math.

### 6.2 Stat purchases need their own ledger [F6]

`equipment_modifier_totals_for_owner` walks `equipment_item_ids(&equipment)` —
it aggregates **only affixes on currently equipped items**. A bought
`+10% PHYSICAL_DAMAGE` belongs to no item and is therefore invisible to it.

Resolution: `SurvivalUpgrade` is the run's upgrade ledger, and its totals are
merged into `EquipmentModifierTotals` at the point of use. Merging *inside*
`equipment_modifier_totals_for_owner` keeps every downstream consumer
(derived stats, resistances, move speed) working with no further edits; the
function early-outs to existing behavior when the owner has no active run, so
non-survival players are untouched.

### 6.3 Purchase contract

Offers are **server-authored per intermission** into `SurvivalShopOffer`, with
`offer_id = hash(arena_id, round, slot)` so stock is deterministic and
replayable within the run. (It is not "identical across a reconnect" — a
disconnect ends the run, §3.1.) The client never proposes a price.

`purchase_survival_offer(offer_id)` is a single atomic reducer that must:
verify the offer belongs to the caller's active run and round; verify
`!purchased`; verify `gold >= price`; debit gold; apply the effect (grant the
pre-rolled item into the run bag, or increment the `SurvivalUpgrade` stack);
mark `purchased = true`. Any failure leaves all of it unapplied.

Item offers are pre-rolled at intermission start and owned by nobody until
purchased.

### 6.4 The economy, concretely [F16]

Slice 3 must not invent these. All values are v1 starting points chosen against
the §6 payout curve — tune from playtest, but tune *these*, and record changes
here rather than in code comments.

**Stock: 6 offers per intermission** — 4 modifier, 2 item. Drawn with
`hash(seed, round, slot)`. A modifier already at its stack cap is not offered.

**Modifier offers.** One buy grants the listed value; price rises with the
stack already owned so the fifth buy costs more than the first.

| Modifier | Value / stack | Cap | Base price |
|---|---|---|---|
| `PHYSICAL_DAMAGE` | +8% | 8 | 110g |
| `FORTITUDE` | +12 max HP | 10 | 90g |
| `MOVE_SPEED` | +4% | 5 | 130g |
| `CRIT_CHANCE` | +3% | 6 | 120g |
| `HEALTH_REGEN` | +1.5/s | 5 | 100g |
| single-school resistance | +6% | 5 | 70g |

```
modifier_price(base, stacks_owned) = round(base · (1 + 0.35 · stacks_owned))
```

**Item offers.** Priced from the roll, not hand-set:

```
item_price(round, affix_count) = round(140 · (1 + 0.18·(round−1)) · (1 + 0.5·affix_count))
```

**Quality curve.** The round feeds `LootRollContext.hidden_loot_quality`, which
already drives affix count and value:

```
hidden_loot_quality(round) = min(1.0, 0.10 + 0.055 · (round − 1))
```

Round 1 offers near-plain items; quality saturates around round 17, after which
§5.4's stat scaling is what keeps rounds meaningful.

**Sanity check against payout.** A round-5 player has earned roughly
5 · ~700 rating · 0.35 ≈ 1200g if they cleared every wave, against a first
`FORTITUDE` buy at 90g and a mid item at ~350g. Early rounds should feel
generous; the stack-price ramp is what removes that slack by round 15. **This
ratio is the first thing to check in playtest** — it is the least evidenced
number in the design.

## 7. Boss rounds

Every 5th round. The boss is a template from the top of the band plus an
explicit multiplier, because §4 showed selection alone is not enough:

| Round | HP multiplier | Selection |
|---|---|---|
| 5 | ×3 | tier V lower (`FIRE_REVENANT`, `ROCK_GOLEM`) |
| 10 | ×4 | `LICH_BOSS`, `DEMON_BOSS`, `OGRE_KING` |
| 15 | ×4 | `DRAKE`, `SKELETAL_DRAGON` |
| 20+ | ×5 | `DRAGON`, `ELDER_DRAGON` + round scaling |

Boss rounds also run a reduced add stream (30% budget) so the fight is not a
pure duel. No timer; ends on boss death; adds despawn after.

Until boss brains exist (§10) a boss is a stat check with a bigger health bar.
That is an accepted v1 limitation, not an oversight.

## 8. Aggro, leash and invisibility

### 8.1 The player must not own the wave [F2]

`spawn_npc` records `spawned_by = ctx.sender()`, and the public debug reducers
authorize on exactly that field. If survival NPCs were spawned as the player's
own, **the player could dismantle their own wave**: `set_npc_target_override`
with `None` clears the permanent pin (`npcs.rs:1147`), and `despawn_npc` /
`despawn_all_npcs` delete NPCs outright (`npcs.rs:1126`) — desynchronising
`director_alive` / `total_alive` and voiding the gold economy.

Two layers, both required:

1. **System ownership.** Survival NPCs are spawned by an internal path with
   `spawned_by` set to a per-run synthetic identity, not the player. The
   existing `npc_identity(owner, sequence)` derivation works unchanged with a
   synthetic owner. Ownership checks then reject the player by construction.
2. **Explicit guards.** `set_npc_target_override`, `despawn_npc` and
   `despawn_all_npcs` reject any NPC holding a `SurvivalNpc` row, regardless of
   ownership. Defence in depth: layer 1 alone breaks the moment someone
   "helpfully" reassigns ownership.
3. **`spawn_npc` rejects callers inside a survival instance [F13].** Guarding
   only the *existing* NPCs left the larger hole open: `spawn_npc` is public,
   spawns into the caller's current instance, and accepts a `faction` argument —
   so a player could spawn `FRIENDLY` allies to fight for them, or flood the
   arena, entirely outside `SurvivalNpc`, the director cap and
   `TOTAL_ALIVE_CEILING`. All in-instance spawning during a run routes through
   survival authorization; the public reducer returns an error.

Counter state is maintained only by the survival despawn path, so no external
deletion can skew it.

**Instant permanent aggro.** On spawn the NPC is pinned to the run owner
through the existing absolute target-override mechanism. Factor the body of
`set_npc_target_override` into an internal `pin_npc_target(ctx, npc, target)`
helper and call that. Do not have the server call its own reducer.

**Leash suppression.** The leash/return-home path early-outs when the NPC has a
`SurvivalNpc` row. One check at the leash site. Deliberately *not* done by
adding survival brain profiles to `npc_catalog.shared.json`: brain id lives on
the template, so per-mode brains would mean duplicating templates, and the
catalog carries pinned 92/329 count assertions.

**Invisibility** (owner ruling §11.6) — freeze, then resume:

- While the player holds the invisibility status, pinned survival NPCs drop the
  pin, halt offensive action, and walk toward the last known position.
- The instant the status ends they re-pin and resume full aggro.
- They **never** return home and **never** heal. Invisibility is a pause button,
  not an escape and not a damage eraser.

This requires a genuine perception gate — nothing reads `stealth_aggro_reduction`
today. Whether the mode's invisibility reuses that modifier or a dedicated
status is an implementation detail for the slice.

## 9. Run equipment (not "loadout")

Owner ruling §11.7: within a run the player simply has *equipment*. It is not
the character's loadout and should not be described as one.

`EquipmentLoadout` is a single row keyed by `owner` with no world dimension,
and derived stats, combat profile and action bar all read from it. So the run's
equipment **occupies that row for the duration of the run** and the character's
real equipment is parked. This is what makes the weapon-swap chain work with
zero new plumbing:

```
buy weapon → equip_item → combat_profile_for_weapon_pair
           → sync_progression_for_equipment_change
           → active combat mode re-synced + action bar re-derived
```

### 9.1 There is one bag, and the run borrows it [F4, F12]

Two dead ends, both verified, before the design that works:

- A `SURVIVAL_BAG` container kind silently breaks equipping — `equip_item`
  rejects any source container that is not `CONTAINER_KIND_PLAYER_BAG`,
  `unequip_item` targets the same kind, and the client's inventory screen only
  locates that kind.
- A *second* `PLAYER_BAG` is impossible. `player_bag_container_id(owner)` is
  `format!("player:{}:bag:0", …)` — **one derived id per owner, permanently**.
  `require_player_bag_container` looks up exactly that id, and the client
  returns the first owned `PLAYER_BAG` it finds. A second bag is either
  unreachable or ambiguous.

**So the run does not get its own container. It borrows the singleton.**

**The snapshot covers equipment, not just the bag [F20].** Equipping *deletes*
the item's `InventorySlot` row and records the id in `EquipmentLoadout`
(`inventory.rs:2020`), so an equipped item has **no bag slot at all** — it is
reachable only through one of the 13 loadout fields. Snapshotting bag contents
alone would leave every equipped piece in place, and the character would fight
the run in their own gear with the starter kit merely added to the bag. There
are two stores to capture, and both must be emptied:

- **Run start** — snapshot (a) the whole `EquipmentLoadout` row, (b) the
  aggregate of every item it references, and (c) the bag's contents *with their
  grid placements*. Delete all of those items, clear the loadout to empty, then
  grant the starter kit and equip it. The container row itself is never touched.
- **During** — one bag, one loadout, exactly as outside survival. `equip_item`,
  `unequip_item`, `move_item` and the client resolver all work unmodified,
  because nothing about the topology changed.
- **Run end** — the ordered restore in §9.2.

Clearing the loadout at run start also closes the displacement hole: with no
real gear equipped, `displaced_equipment_item_ids_for_equip` can never push a
character-owned item into the run bag when a starter or shop weapon is equipped.

```rust
#[table(accessor = survival_stash)]
pub struct SurvivalStash {
    #[primary_key] pub owner: Identity,
    pub equipment_json: String,   // the 13 slot ids + original revision
    pub items_json: String,       // ItemInstance + affix + ItemSpell aggregates
    pub placements_json: String,  // bag grid: item id → (x, y, w, h)
    pub captured_at: Timestamp,
}
```

This is strictly less machinery than parking, and it removes the leakage class
entirely: with one bag there is no second endpoint for `move_item` to reach, so
no cross-container guard is needed. It also needs **no resolver changes on
server or client**, which the two-bag design would have required in both.

**The run-item ledger [F12].** "Recorded run items" was a placeholder; teardown
needs a concrete provenance list, because deleting "everything the owner holds"
would destroy the restored snapshot:

```rust
#[table(accessor = survival_run_item)]
pub struct SurvivalRunItem {
    #[primary_key] pub item_instance_id: String,
    #[index(btree)] pub arena_id: u64,
    pub source: String,   // STARTER | SHOP
}
```

Every item the run creates — starter kit *and* shop purchases — gets a row.
Teardown deletes exactly this set and their child rows, then restores the
snapshot. Any item without a row is the character's property and is never
touched.

### 9.2 The snapshot must survive teardown ordering [F5]

The draft said restore "must also fire from the disconnect path." Verified, that
is not sufficient — it is backwards. `client_disconnected` (`player.rs:145`)
calls `despawn_actor_bundle`, which calls `clear_inventory_for_owner`
(`actor_lifecycle.rs:286`), and that deletes **every container and every item
instance owned by the identity**. A stash holding bare item IDs would restore
into dangling references.

Note that on the disconnect path specifically the run is abandoned (§3.1) and
the session's inventory is discarded wholesale, so the snapshot matters most on
the *in-session* paths — run end, death, leave-instance. It must still be
correct on disconnect so teardown does not fail partway and strand rows.

Two facts from §1.1 shape the fix. Inventory is **session-scoped** — it is
cleared on disconnect and starter equipment is re-granted on connect — so there
is no durable inventory to protect across sessions. What must be protected is
consistency *within* a session and across the run boundary.

Rules:

- **Restore runs before actor teardown.** Survival teardown is ordered ahead of
  `clear_inventory_for_owner` on every path that reaches it. Restoring after is
  a no-op on deleted rows.
- **Snapshot aggregates, not IDs.** The stash captures whole item aggregates —
  `ItemInstance`, its `ItemAffixInstance` rows, its `ItemSpell` rows, and slot
  placements — so restore can rebuild them even if the originals were deleted.
- **Restore is idempotent** and re-entrant from run end, death, leave-instance
  and a watchdog.
- **Teardown deletes child rows too.** Dropping `ItemInstance` alone orphans
  affix and item-spell rows.

**The restore order is load-bearing [F20].** Run items can be *equipped* when a
run ends — that is the normal case, since the player fights in what they bought.
Deleting those `ItemInstance` rows while `EquipmentLoadout` still references
them leaves dangling ids that `item_definition_for_instance` cannot resolve,
which silently corrupts derived stats and equipment presentation. Steps 4–5 of
the §3.1 teardown expand to:

1. **Clear run equipment first.** Drop every `EquipmentLoadout` reference to a
   `SurvivalRunItem` — clear the loadout wholesale, since by construction every
   equipped item is a run item.
2. **Then delete run items** and their affix/`ItemSpell`/slot child rows.
3. **Restore the snapshotted item aggregates** — `ItemInstance` first, then
   affix and `ItemSpell` children.
4. **Restore bag placements** exactly, so the grid layout the player left with
   is the grid layout they return to.
5. **Restore the `EquipmentLoadout` row wholesale**, with `revision` bumped
   monotonically above its current value — never reset to the snapshot's
   revision, or clients treating revision as a change token will ignore the
   restore.
6. **Sync presentation and progression** —
   `sync_equipment_presentation_for_owner` plus
   `sync_progression_for_equipment_change`, or the character keeps rendering run
   gear and the action bar stays on the run's weapon.

Never delete a run item before step 1, and never restore the loadout before
step 3, or it points at items that do not exist yet.

**Teardown invariant to assert:** zero `SurvivalRunItem` rows remain, and no
`EquipmentLoadout` field or `InventorySlot` row references an id that no longer
resolves.

This remains the most intricate part of the design, but it is a
**consistency** risk, not the persistent-data-loss risk the draft claimed.

## 10. Explicitly out of scope for v1

- Co-op / party survival. Spawn ownership, gold split and shop sync all
  multiply scope; solo first.
- Boss brains, mobility execution, and summon *execution*. §5.3 designs the
  origin seam; the abilities that use it come later (owner ruling §11.8).
- Persistent meta-progression. Only `SurvivalScore` outlives a run, plus the
  transient `SurvivalResult` row the results screen reads (§3.2).
- **Resuming a run after disconnect** (§3.1). Disconnect abandons the run.
- Leaderboards beyond the local high-water row.
- Corpse loot during survival.
- Open-world or dungeon survival arenas — blocked on navigation.

## 11. Owner rulings, 2026-08-03

1. **The arena is flat and open.** This is what makes the mode viable without
   pathfinding.
2. **Survivors are despawned at round end.** Rejected the alternative of
   carrying frozen survivors into the next round.
3. **A concurrency cap is fine, but some NPCs will spawn enemies regardless of
   it.** Drove the origin/ceiling design in §5.3.
4. Shop sells **both** stat modifiers and real equipment.
5. **Weapons are purchasable and swapping mid-run intentionally changes the
   action bar.** Not a hazard to guard against — the intended behavior.
6. Invisibility = **freeze, then resume**.
7. **Drop the term "loadout"** for run equipment.
8. Unique NPC abilities are fleshed out later.
9. Fixed starter kit; pure roguelite (nothing persists but score); death ends
   the run; roster + stat multipliers for scaling.

## 12. Implementation slices

Each slice is independently playable or verifiable.

0. **Instance kind** — add `instance_kind`, migrate every classification site in
   the §2.1 audit table, apply the §2.1 PRACTICE/TRAINING mapping, and execute
   the reset-publish cutover with regenerated bindings [F15]. Prove survival is
   not treated as a PvP match. Small, but slice 1 is unsafe without it [F1].
1. **Run lifecycle + arena** — `survival.rs`, `SurvivalRun`, the phase machine,
   the authored flat layout, the canonical `instance_uses_flat_layout` predicate
   replacing all nine helpers [F11], the client scene [F3], system-owned NPC
   spawning with all three guards including `spawn_npc` [F2, F13], the director
   with budget/band/cap/sequence [F10], despawn-on-round-end, and the ordered
   teardown [F14].
   No gold, no shop. Playable: escalating waves that aggro instantly.

   **Slice-1 boss rule [F19]:** boss rounds do not exist yet. Every round —
   including multiples of 5 — is a normal timed round, and the `BOSS` phase is
   unreachable until slice 5. Without this, slice 1 hangs at round 5 waiting for
   a boss that nothing spawns.
2. **Rating + gold** — the generated `survival_ratings.shared.json` artifact and
   its drift check, payout on death, the corpse-less death branch [F8], run HUD
   reading the public `SurvivalRun` [F6].
3. **Run equipment + shop** — the borrowed singleton bag [F4, F12], the **full
   equipment + bag snapshot** and the six-step ordered restore [F5, F20], the
   `SurvivalRunItem` ledger, `SurvivalUpgrade` merged into modifier totals [F6],
   the deterministic offer/purchase contract §6.3 and economy §6.4. UI Toolkit
   screen with a web prototype as spec first, per
   `docs/ui-toolkit-workflow.md`.

   Gate: a run started while wearing full character gear must leave the player
   in the starter kit only, and end with gear, affixes, item spells, bag grid
   positions and action bar all identical to pre-run.
4. **Invisibility** — perception gate, leash suppression, freeze/resume.
5. **Boss rounds** — multipliers, selection table, timerless phase keyed on
   `boss_identity` [F7].
6. **Summon origin** — uncapped spawns, total-alive ceiling, depth limit,
   per-summoner quota, zero-gold adds. Seam only until summon execution exists.

## 13. What I would push back on

- **Boss rounds will disappoint until boss brains exist.** A ×4 health bar on a
  kobold brain is a slog, not a fight. If boss rounds are load-bearing for the
  mode's appeal, boss brains should be promoted out of §10 rather than shipped
  as a stat check.
- **60s + despawn makes late rounds spiky.** With budget growing at `r^1.15`
  and a cap of 14, late rounds spend most of the timer waiting on the cap, so
  the budget stops being the difficulty dial and the cap becomes it. If that
  reads badly in playtest, raise the cap with scaling rather than inflating the
  budget.
- **The equipment stash is the most intricate piece and I would gate slice 3
  behind an explicit disconnect/reconnect test** — restore ordering against
  `clear_inventory_for_owner` is not something to verify by reading. Note the
  corrected framing in §1.1: inventory is session-scoped, so this is a
  consistency risk, not persistent data loss.
- **Slice 0 is not optional and is easy to under-scope.** Survival inherits PvP
  classification from `!is_practice` in at least five places, including the
  server's match-conclusion path. Shipping slice 1 without it means player death
  in survival runs winner logic.
- **`equipment_modifier_totals_for_owner` is on a hot path.** It already records
  `record_equipment_modifier_scan()` for tick metrics, so merging the run
  upgrade ledger into it needs a look at scan cost before slice 3 lands rather
  than after.
