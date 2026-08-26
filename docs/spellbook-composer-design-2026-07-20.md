# Spellbook Composer — Pages & Glyphs Design

> **ARCHIVED IN PLACE — SUPERSEDED 2026-08-26. DO NOT IMPLEMENT.**
>
> The attachment and glyph-modifier research is retained as historical design
> input, but this document's spell-availability chain conflicts with
> `docs/combat-build-progression-cutover-plan-2026-08-26.md`. All authored
> abilities are owned/unlocked, and the current combat build—not spellbook
> contents, known-spell rows, or spellbook capacity—is the sole player-facing
> match authorization source. Any future spellbook-composer proposal must be
> redesigned as a modifier/collection feature that cannot independently grant
> an action-bar assignment or cast.

**Status:** Archived and superseded. Its phases and unresolved forks are closed without implementation.
**Rev 2026-07-20b:** revised after static review. Cutover of the spell-list source is now an atomic server+client phase (ItemSpell has seven client consumers, including input dispatch); `SpellbookPageView` now feeds client cast gating and prediction, not just tooltips; glyph values are snapshotted at cast commit and carried on every long-lived runtime row (ActiveCast, projectiles, channels, persistent areas, auras/emanations) with an exhaustive per-behavior coverage table; an explicit attachment-subtree ownership/deletion invariant added; publish strategy corrected (schema changes go through `republish-local-clear.sh`, and fork 5 now couples schema strategy with migration); atomic `move_attachment` reducer added for socket-to-socket drags.
**Scope:** Item-level design (pages, glyphs, attachment), server cast-path modifier plumbing, resolved-stat display/gating contract, and the composer UI screen. Out of scope: economy tuning (drop rates, vendor pricing), new glyph kinds beyond the first four, and any change to spell *behavior* authoring (`progression_catalog.shared.json` stays untouched).

---

## 0. What the owner asked (restated to lock understanding)

> A fully fledged spellbook composer. Add and remove 'pages' from a spellbook; an attached page adds that spell to the spellbook — a page is basically a spell. A spellbook can have 10 spells max. 'Glyphs' can be added to a page to empower that spell: Glyph of Power → potency (not merely damage — also healing, defense, resistance, debuff duration, etc.), Glyph of Haste → cast speed, Glyph of Memory → mana cost, Glyph of Force → add/increase knockback. Many more possibilities.

| Requested | Verdict | Corrected / grounded direction |
|---|---|---|
| Spellbook that holds up to 10 spells | **Already exists** | `SPELLBOOK` item kind + equip slot + `ItemSpell` slot table + `STARTER_SPELLBOOK_SPELL_COUNT = 10` are live today. Pages replace the current *random-seed + slot-rewrite* fill mechanism; the availability chain (spellbook → known → action bar → cast) stays untouched. |
| Page "is basically a spell" | **Reframe slightly** | A page is an **item that grants access to one spell** while attached. Spell definitions stay global and unforked; glyphs never create spell variants, they contribute numeric deltas resolved per caster at cast commit. |
| Glyphs empower spells "in different ways" | **Constrain deliberately** | Glyphs are **data, not code**: each glyph is `(modifier_kind, value)` on a small closed set of numeric axes. New glyph = one const row + zero new code, exactly like item affixes today. Bespoke per-glyph logic is the failure mode to avoid. |
| "Many possibilities" | **Yes, cheaply** | The axis model makes Glyph of Reach (+range), Glyph of Breadth (+radius), Glyph of Recall (−cooldown) etc. pure data adds once the four launch axes prove the plumbing. |

---

## 1. Ground truth — what already exists (verified in code)

**Spellbook is a real item with a real equip slot.**
`ITEM_KIND_SPELLBOOK` (`server/src/inventory.rs:81`), `EQUIP_SLOT_SPELLBOOK` (`inventory.rs:105`), `EquipmentLoadout.spellbook_item_id` (`inventory.rs:299`), starter `APPRENTICE_SPELLBOOK` with capacity 10 (`inventory.rs:125-126`, `spellbook_spell_count_for_definition` at `inventory.rs:3145`).

**A spellbook's spells are rows, and the whole gameplay chain hangs off them.**
`ItemSpell { item_instance_id, slot_index, spell_id }` (`inventory.rs:229-238`) → `equipped_spellbook_spell_ids_for_owner` (`inventory.rs:3203`) → `player_knows_spell` (`server/src/spells/mod.rs:352`, ORs `PlayerKnownSpell` with the equipped book) → action-bar validation `validate_character_action_bar_ref` (`server/src/progression.rs:3902`) and spell-slot capacity (`equipment_spell_slot_capacity_for_owner`, `inventory.rs:2430`) → cast authorization `spell_cast_is_authorized_by_action_bar_or_spellbook` (`spells/mod.rs:518`).

**`ItemSpell` is a load-bearing public table on the CLIENT, not just the legacy panel's data source.** Seven non-generated client files read it: `GameplayContracts.cs` (the active-spellbook-slot resolver at `:1074`, called by `ActionBarInputDispatcher.cs:44` — spellbook *key dispatch* resolves through it), `SpellInputHandler`-adjacent known-spell checks, `HUDController.cs`, `CharacterActionBarPanel.cs`, `SpellCatalogPanel.cs`, `SpellbookPanel.cs`, `InventoryScreen.cs`, plus `GameplaySubscriptionPlanner.cs` (it is a subscribed table). **Consequence: the server-side source flip and the client consumer migration are one atomic cutover (§9 phase 3); flipping the server alone would make spellbook keys resolve empty.**

**The client locally gates and predicts casts from global `SpellDefinition` numbers.** `HasResourceForSpell` (`SpellInputHandler.cs:590-617`) computes cost from `SpellDefinition.PrimaryResourceCost` and issues a local advisory denial *before any reducer call*; `RecordPredictedSpellStart` / `PredictCastTimeSpellHold` (`SpellInputHandler.cs:551-558`, `:625`) predict from the same global rows, and the server commits cast-time-spell cost **at release, not press**. Glyphed numbers must therefore reach the input path (§4.4), or a Glyph of Memory player with 45 mana and a base-50/glyphed-40 spell is wrongly blocked client-side.

**Item plumbing is complete.** `ItemDefinition`/`ItemInstance`/`ItemAffixDefinition`/`ItemAffixInstance`, grid containers with footprints, move/merge/consume/equip reducers, loot rolls, `sync_item_definitions` publish, item-icon pipeline. Item defs are authored as Rust const specs (`STARTER_ITEM_DEFINITIONS`, `inventory.rs:459-980`).

**Long-lived cast state carries no modifier context today.** `ActiveCast` (`spells/mod.rs:166-185`) stores aim/timing/charge fields only; `ActivePersistentArea` (`mod.rs:150-164`) stores timing only, and its pulses re-read global definition damage (`casting.rs:6484`); auras/emanations apply global payloads at activation-time-later (`combat.rs:1329`); projectile impact statuses bypass `scale_apply_status_payload_for_caster` (`combat/projectiles.rs:1523`). This forces the snapshot model in §4.2.

**Publish contract.** `ops/republish-catalog.sh` is *explicitly* for non-schema catalog edits (its header says so; `republish-catalog.sh:4-11`) and directs schema/binding work to `ops/republish-local-clear.sh`, which defaults to `--delete-data=always` (`republish-local-clear.sh:7`) but supports `never`/`on-conflict`. SpacetimeDB auto-migration accepts **new tables** on a data-preserving publish but not **altered row types** on existing tables. This couples schema strategy to migration strategy — see §2.4 and fork 5.

**What does NOT exist:** item-inside-item attachment (items live only in grid containers or equip slots), and any per-player-per-spell parameter modification (affixes only feed `EquipmentModifierTotals` → derived stats; spell numbers are global).

---

## 2. Data model

### 2.1 New item kinds

- `ITEM_KIND_SPELL_PAGE` — one `ItemDefinition` **per castable spell**, generated at publish time from the spell catalog (loop over `spell_definitions()` inside `sync_item_definitions`; a spell added to the catalog gets its page def for free — no hand-authoring). Pages are 1×1, stack 1, tradeable/lootable like any item.
- `ITEM_KIND_GLYPH` — hand-authored const defs. Launch set:

| Glyph | `modifier_kind` | value semantics |
|---|---|---|
| Glyph of Power | `SPELL_POTENCY_PCT` | +N% to all magnitude outputs of the spell (§4.1) |
| Glyph of Haste | `SPELL_CAST_TIME_PCT` | −N% cast time |
| Glyph of Memory | `SPELL_RESOURCE_COST_PCT` | −N% primary resource cost (applies to the per-second basis for channel-cost spells) |
| Glyph of Force | `SPELL_KNOCKBACK_METERS` | +N meters knockback distance (§10 fork 2 for knockback-less spells) |

The page→spell binding (`spell_id`) and glyph payload (`modifier_kind`, `value`) live either as new `ItemDefinition` columns **or** as new side tables (`SpellPageBinding { item_def_id, spell_id }`, `GlyphBinding { item_def_id, modifier_kind, value }`) depending on the fork-5 schema-strategy ruling (§2.4). Columns are simpler; side tables are the only auto-migratable option.

### 2.2 Attachment — one generic mechanism for both nestings

```rust
#[table(public)]
ItemAttachment {
    #[primary_key] key: String,                 // "{parent_instance_id}:{socket_index}"
    #[index] parent_item_instance_id: String,
    socket_index: u32,
    #[unique] child_item_instance_id: String,   // an item attaches to at most one parent
    #[index] owner_key: String,                 // denormalized for client subscription
}
```

Same table serves page→book and glyph→page (validation matrix decides legality, §3). `owner_key` is denormalized because subscription SQL can't chase `attachment → instance → owner` (two-semijoin limit; filterable `*_key` string column per project constraint).

An attached item is a **third placement state** alongside "in a grid container" and "equipped": the `ItemInstance` keeps its owner but has no `InventorySlot` row. Detach requires free bag space (`first_free_position`), mirroring unequip.

**Nesting composes:** a detached page keeps its glyphs. Books are editable while unequipped — attachment is item-level; only the *equipped* book grants casts.

### 2.3 The attachment-subtree invariant (ownership & deletion cascade)

Because attached children have no `InventorySlot` row, **no existing code path will ever see them**: grid movement updates only the moved instance's owner (`inventory.rs:1723`), and container cleanup deletes only instances represented by slot rows (`inventory.rs:4882`). Without an explicit rule, looting a book leaves its pages owned by the corpse, and expiring a corpse chest leaks orphan page/glyph instances and dangling attachment rows.

**Invariant:** an `ItemInstance` and its entire `ItemAttachment` descendant subtree agree, at all times, on `current_owner`, `current_owner_key`, and existence.

**Mechanism:** one shared helper, `for_each_item_subtree(instance_id, f)` (depth-first over `ItemAttachment.parent_item_instance_id`), through which every mutation routes:
- **ownership transfer** (grid move across owners, `quick_loot`, corpse/chest transfer, any future trade): cascade `current_owner`/`current_owner_key` on every descendant instance and `owner_key` on every descendant attachment row;
- **deletion** (container expiry/cleanup, consume/destroy, any reset path): delete descendant instances and attachment rows with the parent.

Phase 1 includes an audit item: enumerate every site that writes `current_owner` or deletes an `ItemInstance` and route each through the helper. The acceptance probe loots a glyphed-page-bearing book off a corpse and asserts subtree ownership, then lets a corpse container expire and asserts zero orphan instances/attachments.

### 2.4 Schema strategy (coupled to fork 5)

Two internally consistent packages — mixing them does not work:

- **Package R (reset; recommended):** page/glyph payloads as new `ItemDefinition` columns; glyph snapshots as new columns on `ActiveCast`, projectile rows, channel state, `ActivePersistentArea`, aura/emanation rows. Simple code, but altered row types → publish via `ops/republish-local-clear.sh` with its default `--delete-data=always` (the repo's established schema workflow). Existing dev-DB state is wiped; the starter flow re-materializes books as pre-attached pages (§3).
- **Package A (additive; only if a DB must survive):** all new data in new tables — `SpellPageBinding`/`GlyphBinding` side tables (§2.1) and a `GlyphSnapshot { key: "{kind}:{runtime_row_key}", potency_multiplier, resource_cost_multiplier, knockback_bonus_meters }` side table keyed per runtime row, plus a one-shot `migrate_itemspell_to_pages` reducer that mints page instances from existing `ItemSpell` rows and attaches them. Data-preserving publish (`ARENA_DELETE_DATA=never`), but adds cross-row cleanup discipline (snapshot rows must die with their parents) for the lifetime of the feature.

There is no current production DB, which is why R is recommended — but this is the owner's call (fork 5).

### 2.5 What happens to `ItemSpell`

The book's spell list becomes **derived**: attachments → page instance → page binding → `spell_id`. At cutover (§9 phase 3) `equipped_spellbook_spell_ids_for_owner` and `equipped_spellbook_contains_spell` change source, `ensure_spellbook_spells_for_item` / `random_spellbook_spell_ids` / `assign_equipped_spellbook_spell` retire, and **all seven client consumers migrate in the same change**. `ItemSpell` is dropped in the final phase once nothing reads it. No denormalized copy is kept — one source of truth.

---

## 3. Reducers & validation

`attach_item(child_instance_id, parent_instance_id, socket_index)`
- caller owns both instances; child currently in a grid container (not equipped, not attached elsewhere — `#[unique]` enforces the latter).
- Matrix: `SPELLBOOK` parent accepts `SPELL_PAGE`, `socket_index < spellbook_spell_count_for_definition` (the max-10); `SPELL_PAGE` parent accepts `GLYPH`, `socket_index < page glyph sockets` (§10 fork 1).
- Book may not hold two pages of the same `spell_id` (second attach rejected).
- Page may not hold two glyphs of the same `modifier_kind` (v1; §10 fork 1).
- Removes the child's `InventorySlot` row; bumps container revision.

`detach_item(child_instance_id)`
- Requires free bag space; restores an `InventorySlot` at `first_free_position`.
- Detaching a page from the **equipped** book: clear any `CharacterActionBarAssignment` whose spell is no longer known afterward (assignment-time validation already exists; this adds the detach-time sweep). A pending/queued cast of that spell re-fails authorization at `resolve_pending_casts` — verify during phase 1 that pending resolution re-checks `player_knows_spell`; add the re-check if it does not (§7).

`move_attachment(child_instance_id, new_parent_instance_id, new_socket_index)`
- Atomic re-socket for already-attached children — **no bag space required** (attach_item alone would force detach-to-bag first, making reordering fail with a full bag). Covers: reordering pages within a book, moving a page between books, moving a glyph between pages.
- Same validation matrix as attach. If the target socket is occupied, the two children **swap** (legality re-checked both ways; for same-kind children this is trivially symmetric — the duplicate-spell / duplicate-modifier-kind rules are evaluated post-swap).

Grants: starter flow replaces the random `ItemSpell` seed with the **same random selection materialized as page items pre-attached** to the apprentice book, plus a couple of loose pages/glyphs in the bag so the composer is discoverable. Loot: pages and glyphs enter `create_corpse_loot_for_npc` roll tables (weights = follow-up, not v1-blocking).

---

## 4. Glyph modifiers on the cast path

### 4.1 The resolved struct

```rust
pub(crate) struct SpellGlyphModifiers {
    pub potency_multiplier: f32,        // default 1.0
    pub cast_time_multiplier: f32,      // default 1.0
    pub resource_cost_multiplier: f32,  // default 1.0
    pub knockback_bonus_meters: f32,    // default 0.0
}
```

`spell_glyph_modifiers_for_cast(ctx, caster, spell_kind) -> SpellGlyphModifiers`: equipped book (`EquipmentLoadout.spellbook_item_id`) → its page attachments → the page whose binding matches `spell_kind` → its glyph attachments → fold `(modifier_kind, value)`; percentages of one kind sum, then apply as `base × (1 + sum)`. A handful of index lookups, once per cast — no caching table; casts are low-frequency.

**Potency** scales every *magnitude* output of the spell: damage, `heal_amount` (direct + channel), status `modifier_scalar`, `tick_damage`/`tick_heal`, `absorb_amount/cap`. That covers the owner's "not merely damage — also healing, defense, resistance, debuff" list, because defensive buffs and resistances *are* status payloads with `modifier_scalar`. Durations are deliberately excluded (§10 fork 3).

### 4.2 Snapshot-at-commit — where resolved values live

Spells outlive the press: cast-time spells execute later from `ActiveCast`, projectiles impact later, channels/areas/auras tick for their whole duration. **Rule: modifiers are resolved exactly once, in `execute_cast_intent` (`casting.rs:655`), and the resolved values are stamped onto whichever long-lived runtime row the behavior spawns. No deferred site ever re-resolves live.** Editing the book mid-flight affects only future casts — consistent with how projectile damage is already frozen at spawn (`casting.rs:3641`). Under package R the stamp is new columns on each runtime row; under package A it is a keyed `GlyphSnapshot` row (§2.4).

Carriers and their consumed axes:

| Runtime row | Stamped at | Carries | Consumed by |
|---|---|---|---|
| `ActiveCast` (`spells/mod.rs:166`) | cast start | potency, **resource_cost** (cost commits at *release*, not press), knockback | release-time execution of every cast-time spell |
| Projectile row | spawn (`casting.rs:3427/3653`) | potency (damage already frozen; statuses are not), knockback | impact damage (`projectiles.rs:1530/1612`), impact statuses (`projectiles.rs:1523` — currently bypasses the caster-scaling hook; route through a snapshot-aware payload scale), impact knockback |
| Channel state | channel start (`casting.rs:4754`) | potency, resource_cost (per-second drain) | `tick_channel` heal/damage incl. the **bespoke Electrocute damage path** |
| `ActivePersistentArea` (`mod.rs:150`) | area spawn | potency | per-pulse damage — today re-reads global `definition.damage` every pulse (`casting.rs:6484`); change to apply the frozen multiplier |
| Aura / emanation runtime rows | activation | potency | deferred payload application (`combat.rs:1329`) |

### 4.3 Per-behavior coverage (exhaustive over `SpellBehavior`, `manifest.rs:136-152`)

| Behavior | Magnitudes affected | Injection site(s) |
|---|---|---|
| DirectTarget | damage, heal | `apply_direct_target_spell` `casting.rs:6734` (damage), `:6706` (heal) — from ActiveCast snapshot when cast-time |
| Projectile | impact damage, impact status payloads, knockback | spawn freeze `casting.rs:3641/:3770` + snapshot columns; impact sites per §4.2 |
| Area | damage, status payloads | `resolve_area_impact` `casting.rs:5943` + per-target packet |
| PersistentArea | pulse damage | pulse site `casting.rs:6484` via row snapshot |
| InstantBeam | damage | beam packet build (locate in phase 2 audit; executes at release → ActiveCast snapshot) |
| Channel | tick damage, tick heal, per-second cost | `tick_channel`/`apply_generic_channel_heal` `casting.rs:4795/:4861` **+ bespoke Electrocute damage path** — via channel-state snapshot |
| ApplyStatus | payload magnitudes (`modifier_scalar`, ticks, absorb) | extend `scale_apply_status_payload_for_caster` `casting.rs:6964` to take the snapshot |
| RemoveStatus | none | — |
| ConsumeStatus | consume-payoff amounts if any | phase 2 audit item |
| Aura / Emanation | payload magnitudes | activation stamp + deferred application `combat.rs:1329` |
| SelfResource | none in v1 (resource generation is not "potency") | — flagged, not silently skipped |
| WorldObstacle | none | — |

Cast time (`casting.rs:1003-1009`, the single derivation) and resource cost (`resolved_initial_primary_resource_cost_for_action` `casting.rs:139` / `resolved_primary_resource_cost_for_amount` `casting.rs:177`, covering pre-check and commit) are press/release-scoped and consume the intent-time resolution directly.

**Phase 2 opens with a closing audit:** grep every read of `definition.damage`, `heal_amount`, `tick_damage`, `tick_heal`, `modifier_scalar`, `absorb_*`, and `ImpactEffect::Knockback` outside this table; any hit extends the table before any injection code lands. The table above is believed complete but is *proven* complete by that audit, not by this doc.

### 4.4 Display AND gating contract — server-resolved, client-dumb

New public table `SpellbookPageView { key(page instance), owner_key, book_item_instance_id, slot_index, spell_id, cast_time_ms, resource_cost, potency_multiplier, knockback_meters }`, rewritten by the same resolver on attach/detach/move/equip. `resource_cost` keeps the same semantics as `SpellDefinition.PrimaryResourceCost` (the per-second basis for channel-cost spells) so existing client math is unchanged apart from the source row.

This table is **not just tooltips**. The client consumes it everywhere it currently reads global numbers for a book-granted spell, with fallback to `SpellDefinition` when no view row exists (spells granted via `PlayerKnownSpell` carry no glyphs, so base values are correct for them):
- **cast gating:** `HasResourceForSpell` (`SpellInputHandler.cs:590`) — otherwise Glyph of Memory casts are wrongly denied locally;
- **prediction:** `RecordPredictedSpellStart` (resource reservation basis) and `PredictCastTimeSpellHold` (cast-bar duration) (`SpellInputHandler.cs:551-558/:625`) — otherwise Glyph of Haste desyncs the predicted cast bar from the server cast;
- **composer, tooltips, action-bar metadata** (`ActionTooltipResolver`, bar cost display).

The client never mirrors the modifier math — it swaps which row it reads a number from. The server cast path never reads this table (it calls the resolver live); the view is a projection.

---

## 5. Composer UI

Follows `docs/ui-toolkit-workflow.md` end to end (prototype → lint → one-way UXML/USS translate → thin controller → `ops/ui-preview.py`). Art direction v2 (forged/leather/Cinzel) — a spellbook is the single most on-theme screen this art language will ever get.

- **Prototype:** `docs/ui-prototypes/spellbook/index.html` + `spellbook.css`. Layout: left = the open book, 10 page slots showing page art + spell icon (`ActionIconResolver` for the spell art, `ItemIconResolver` for page/glyph items) + glyph sockets per page; right = owned loose pages/glyphs (filtered bag view). Tooltip shows base vs. glyphed numbers from `SpellbookPageView` (green deltas).
- **Drag contract:** bag → socket calls `attach_item`; socket → bag calls `detach_item` (needs bag space; surface the failure); **socket → socket calls `move_attachment`** (atomic, swaps on occupied target, needs no bag space) — reordering must never require free bag room.
- **Controller:** `SpellbookComposerScreen.cs` modeled on `InventoryScreen.cs` (drag-ghost, pointer gating via `IsPointerOverUi`, tooltips) — `ArenaPanel.CreateDocument`, `IEscapeCloseable` priority 95 (the slot the legacy panel holds), no pause, sorting via `RuntimeUiLayer.NextSortingOrder()`.
- **Entry:** clicking a spellbook item / the equipped book slot in the Inventory (exactly where `SpellbookPanel.Open` is invoked today: `InventoryScreen.cs:738`, `:760`). Optional letter keybind = fork 4. Never F1–F12 (repo standard).
- **Supersedes** the legacy uGUI `SpellbookPanel` (retired with `ItemSpell` in the final phase).
- Icons: pages share one parchment base badged by the spell's ability icon (composite at draw time — two `Image` elements, no new art per spell); glyphs get one authored item icon each via the standard sheet pipeline.

## 6. Behavior changes vs. today (explicit, per no-unflagged-changes rule)

1. Spellbooks are no longer randomly seeded on first equip; content comes from attached page items. Starter books arrive pre-attached with the same random selection materialized as pages.
2. `assign_equipped_spellbook_spell` (drag-a-spell-onto-a-`SPELLBOOK_`-slot in `CharacterActionBarPanel.cs:662`) retires — slot rewriting is replaced by page attach/detach in the composer.
3. Legacy `SpellbookPanel` (read-only viewer) is replaced by the composer screen.
4. Detaching a page can now silently clear action-bar slots that referenced its spell.
5. Spell numbers become per-player-variable: two casters of the same spell may differ in damage/cast time/cost/knockback. Combat log/FCT and any probe assertions that assume catalog-constant values must read live values (probes: assert deltas, not constants).
6. Under package R (fork 5), **each schema-bearing phase wipes the local dev DB** (`republish-local-clear.sh`, delete-data=always): characters, inventories, and world state reset; starter grant re-seeds on next connect.
7. Transitional (phases 1–2 only): pages can be attached but do **not** grant spells until the phase-3 cutover; glyphs on a page *do* already modify a spell that the legacy `ItemSpell` rows grant. Intentional, dev-local, probe-tested.
8. `ItemSpell` (public table) is dropped at the end — client binding regen; any external tooling reading it breaks then.

## 7. Edge cases & risks

- **Pending casts vs. detach:** verify `resolve_pending_casts` re-authorizes; if it resolves against a stale grant, add the re-check (§3).
- **In-flight snapshots:** projectiles/channels/areas/auras keep cast-commit glyph values for their lifetime (§4.2) — deliberate; mid-flight book edits never retro-apply.
- **Subtree invariant regressions:** the §2.3 helper is only as good as its call-site coverage; the phase-1 audit + orphan-sweep probe is the guard. Any *future* owner-mutation path must route through it — noted as a standing contract in the doc header of the helper.
- **Capacity interplay:** action-bar spell-slot capacity today = `spell_slots` affix total + book spell count (`inventory.rs:2430`) — derives cleanly from page count; explicit test that an empty book yields zero slots.
- **Duplicate-page attach ordering** during starter grant must be deterministic (seeded roll already is).
- **Subscription growth:** `ItemAttachment` + `SpellbookPageView` join the gameplay subscription (`GameplaySubscriptionPlanner.cs`), filtered on `owner_key`; `ItemSpell` leaves at the end — net table count unchanged.
- **UITK constraint:** many small glyph-socket plates → each baked at exact size in `PLATE_BAKES` (no runtime 9-slicing); popover glyph pickers must respect no-z-index (top-level overlay layer).

## 8. What this deliberately does NOT do

- No per-glyph bespoke code paths; a glyph that can't be expressed as an axis value is a new *axis* (one enum arm + injection rows in the §4.3 table), not a special case.
- No spell-definition forking or per-combo variant spells; `progression_catalog.shared.json` untouched.
- No changes to spell behavior selection, targeting, LOS rules, or the animation/VFX pipelines (glyphs are numbers-only in v1 — a later "glyph tint" presentation pass is possible but out of scope).

## 9. Build phases

1. **Server foundation** — `ItemAttachment` + attach/detach/**move** reducers + matrix validation + **subtree-invariant helper and owner-mutation audit (§2.3)**; `SPELL_PAGE`/`GLYPH` kinds + payload storage per fork-5 package; page defs auto-derived at publish; four launch glyph defs; starter materialization. Publish via `republish-local-clear.sh` (regens bindings + dotnet-verifies). **`ItemSpell` untouched; spell-list source NOT flipped.** *Acceptance: headless probe attaches/moves/detaches pages+glyphs, loots a composed book across owners, expires a corpse, asserts subtree ownership and zero orphans.*
2. **Cast-path modifiers** — `SpellGlyphModifiers` resolver, snapshot stamping on all §4.2 carriers, every §4.3 injection site, opening with the closing-audit grep. Glyphs go live against legacy-granted spells (§6.7). *Acceptance: probe casts with/without each glyph against live data and asserts observed deltas — including a persistent-area pulse, a projectile impact status, a channel tick, and release-time (not press-time) cost commit under Glyph of Memory.*
3. **Cutover (atomic server+client)** — flip `equipped_spellbook_spell_ids_for_owner`/`equipped_spellbook_contains_spell` to pages; add `SpellbookPageView`; retire seeding + `assign_equipped_spellbook_spell`; migrate **all seven client consumers** (GameplayContracts slot resolver → input dispatch, HUD, action-bar editor, spell catalog, inventory, subscription planner) and the **input gating/prediction reads (§4.4)**; binding regen. Lands as one unit — the server flip alone breaks spellbook key dispatch. *Acceptance: full press-to-impact probe leg through action-bar dispatch on a page-granted, glyphed spell; client-side local-denial check passes at glyphed (not base) cost.*
4. **Composer UI** — prototype → translate → controller; supersede `SpellbookPanel`; page/glyph icons. *Acceptance: `ops/ui-preview.py` states + scripted attach/move/detach round-trip in play mode.*
5. **Economy + polish** — loot-table entries, action-bar tooltip shows glyphed cost, drop `ItemSpell`, retire dead reducers/panel.

## 10. Forks (owner rulings requested)

1. **Glyph sockets per page** — *Option A (recommended):* 3 sockets, max one glyph per `modifier_kind` (three distinct empowerments, no same-kind stacking to balance). *Option B:* 1 socket (maximally simple, weakest composer fantasy). *Option C:* sockets scale with page/book rarity (best long-term itemization; more UI + def plumbing now). A→C is a data change later, so A does not paint us into a corner.
2. **Glyph of Force on spells with no authored knockback** — *Option A (recommended v1):* amplify-only; the glyph is inert on knockback-less spells (composer greys it out with a reason). *Option B:* grants `ImpactEffect::Knockback` where none exists — mechanically one `push` and presentation is already generic (knockback design 2026-07-17), but it's a balance decision touching every spell at once.
3. **Potency scope** — *Option A (recommended):* magnitudes only (damage/heal/ticks/absorb/modifier_scalar); durations become a future *Glyph of Extension* (`SPELL_STATUS_DURATION_PCT` axis is ~free to add). *Option B:* potency also multiplies status durations, per the original ask — one glyph does more, but conflates two balance levers.
4. **Composer entry point** — *Option A (recommended):* from the spellbook item in the Inventory only (v1). *Option B:* also a dedicated letter key (suggest `B`; never F-keys). Trivial either way; pure preference.
5. **Schema strategy + migration (coupled — see §2.4)** — *Option R (recommended):* columns on existing tables + the repo-standard destructive local republish per schema phase; existing dev characters reset and re-seed with materialized starter books. Simplest code; nothing of value is lost today. *Option A:* additive-only side tables + `migrate_itemspell_to_pages` reducer + data-preserving publish (`ARENA_DELETE_DATA=never`); preserves the current DB at the cost of permanent side-table/cleanup complexity. Choose R unless there is a DB whose state matters.
