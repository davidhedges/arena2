# S10 — Per-Victim Rewind for Sweeps (+ Projectile Ruling) (Design, 2026-07-05)

Slice S10: the second recorded gap from
`docs/lag-compensation-design-2026-07-04.md` §7 (and
`docs/auto-attack-rewind-design-2026-07-04.md` §7) — **per-victim rewind for
sweeps/projectile impacts**, "the frozen-press-delay pattern applied per
candidate victim at resolve." Per standing practice: **design doc first,
kill-switched, shaped A/B decides the default.** The owner decisions in §6
gate implementation. Everything in §1 was verified in code today.

Sequencing context (recorded when this slice was scoped, S8 §7 / S9 §7): S10
reuses S8 machinery **only** — no new standing signal (that was S9's job). It
extends the same bounded attacker-view rewind to area-of-effect membership,
which today resolves present-time. S11 (deferred defense resolution) still
gates on the `[DEFENSE_LATE]` numbers S9 is collecting. Aerial items stay
deferred, unchanged.

**Status: IMPLEMENTED + server-half LIVE-VERIFIED + default ON (2026-07-05,
owner call); shaped owner A/B deferred as an optional spot-check.** The owner
elected to ship `sweep_rewind_enabled` default ON now on the strength of the
probe PASS below rather than gate on the shaped A/B ("flip the default to ON for
now"); the A/B is recorded as a deferred confirmation in
`docs/netcode-open-items.md`, and the S8 master kill switch / `set_lag_comp_config
true 250 true false` reverts S10 alone if it ever regresses. G1/G2 owner-signed
2026-07-05 (G1 = sweeps only, keep projectile
impacts present-time; G2 = no-target casts report the shared S7 connection
budget); G3–G5 proceeding as recommended. Server + Unity C# compile; bindings
regenerated; the 4-arg `set_lag_comp_config` + `sweep_rewind_enabled` column
verified live. `ops/s10-sweep-rewind-probe.py` **PASS** on a throwaway
measurement-build DB — vehicle `ICE_SPIKES` (a no-target SPELL AREA CASTER_CONE,
7.5 m), learned deterministically via `learn_spell` and cast through
`cast_request` → `resolve_area_impact` → the shared `sweep_rewind_membership`
helper (the spell-area path a headless probe can reach without character
progression; the melee caster-cone path shares the identical helper and is
covered by construction + the shaped A/B). All checks green: config default
OFF; OFF leg logs 5 would-be flips (`enabled=false present=in rewound=out
flip=true`); ON leg puts the rewound verdict in control with 3 used flips
(`enabled=true`, `present=in rewound=out`), `rewound_ms` 252–277,
`source=history`; a zero-report cast produces no rewound evaluation
(degrade-to-present). Fixture notes for the record: an equal-speed chaser pins
to its target, so the flip needs a slowly-shuttling decoy the kobold stays
glued to while the parked attacker (offset < cone range) watches its distance
sweep the boundary — a fast decoy hands nearest-wins aggro to the attacker
(the S7/S8 trap); and the wire spell id is the action_id (`ICE_SPIKES`), not
the ability_id. The **shaped owner A/B with the real client** is the remaining
acceptance gate, per S7/S8/S9 precedent. Two premise refinements this
investigation surfaced, both flagged for the owner:
1. **"Needs no new signal" is true at the wire, not at the client.** For
   *targeted* attacks the press already reports a per-target view delay (S8).
   A **cone/radius sweep has no single target**, so the client currently
   reports `view_server_time_ms = 0` for it (`SpellInputHandler.cs:918-924`)
   — sweeps validate present-time today *because the client sends no delay*,
   not because the server ignores one. Making sweeps rewind needs a small
   **client** change (populate the arg for no-target casts from the shared
   S7 budget); the *wire contract* (the `cast_request` arg) is unchanged, so
   no new signal in the S9 sense. See G2.
2. **The projectile half is a decision, not a default.** The recorded ruling
   (S8 D5, reaffirmed S9 §7) is that projectile *impacts* stay present-time —
   "visible in flight, dodging is counterplay" — and that ruling **stands
   unless the owner reopens it here.** §2.6 recommends **decline** on
   analysis; G1 is the owner's call. If declined, S10 ships **sweeps only.**

**Principle (unchanged from S8):** *the rewind decides whether an attack
connects; the present decides everything about how it resolves.* S10 extends
the S8 rewind to the one *hit-test* with many candidate victims and no
per-target press signal; it adds no new rewind semantics — every never-rewind
rule, barrier, and clamp is inherited from `rewound_pose_for` by construction
(§2.4).

---

## 1. What exists today (verified)

### 1.0 There are two pure-sweep membership paths, not one (found in build)

"Sweeps" is two shipped code paths, both resolving area membership against
present poses, and covering only one would recreate the exact illegibility the
review §2 names ("the same wall blocks your bow but not your charge"):

- **Spell AREA** (`resolve_area_impact`, `casting.rs`) — a `SpellBehavior::Area`
  cast, e.g. `SPELL_ICE_SPIKES` (CASTER_CONE spell). §1.1.
- **Melee caster-cone / caster-radius** (`resolve_pending_melee_hit_volume`,
  `melee.rs:4104`) — a no-target melee whose `PendingMeleeImpact.target ==
  ZERO`, e.g. `WARRIOR_CATACLYSM` (CASTER_CONE melee). Membership is
  `melee_hit_volume_contains_player` (`melee.rs:4217`, present `caster_phys` +
  `CombatAreaShape::Cone/Disc`). The S8 D2 impact re-check
  (`melee.rs:4298`) is gated `targeting_kind == "TARGET"`, so these sweeps
  never rewind today. `PendingMeleeImpact.view_delay_micros` is **already**
  frozen (D2), so the only missing pieces are the membership rewind + a client
  report for no-target melee presses (`MeleeInputHandler.cs:284` sends 0 for
  `!requiresTarget`, the melee twin of the sweep client gap).

S10 covers **both**. A third path — melee **impact-area splash**
(`push_melee_impact_area_effects`, `melee.rs:4584`, cleave-style disc around a
*primary* target's present impact point) — stays present-time: its anchor is an
emitted present position (never-rewind rule 5) and its primary already rewinds
via D2. Recorded, not covered (§7).

### 1.1 Cone/radius sweeps resolve present-time, in one shared loop

The AoE path (`spells/casting.rs`):

- **Press → resolve.** `cast_generic_area` (`casting.rs:4901`) runs inside the
  `cast_request` transaction (`spells/mod.rs:381` → `execute_spell_press` →
  here). It computes the area center + shape, emits the RELEASE event, then
  **branches on windup**:
  - `impact_delay_ms == 0` (`casting.rs:4957`): resolve **immediately, in the
    press transaction**, via `resolve_area_impact` (`casting.rs:4978`).
  - `impact_delay_ms > 0`: insert a `PendingAreaImpact` row
    (`spells/mod.rs:120-139`) and return; the tick resolver
    `resolve_pending_area_impacts` (`casting.rs:5008`) fires it later via
    `resolve_pending_area_impact` → `resolve_area_impact` (`casting.rs:5065`)
    **in a later tick transaction**.
- **The victim loop** (`resolve_area_impact`, `casting.rs:5081`):
  `PlayerSnapshotSet::collect(ctx)` (present poses) → `query_disc_indices`
  around the area center at `area_shape.query_radius()` → per candidate:
  alive / not-caster / same-world-context / `target_audience_allows`, then the
  membership test **`area_shape_contains_player(area_shape, &impact, player)`**
  (`casting.rs:5257`, reads `player.pos_*`). Members go to
  `resolve_blockable_spell_hit` → `resolve_spell_combat_hit_defense`
  (`casting.rs:1240`) → `resolve_defensible_combat_hit` (`casting.rs:1289`).
- **Every position read is present.** Both the candidate pre-filter and the
  shape test read `PlayerSnapshot` present poses. `PendingAreaImpact` has **no**
  `view_delay_micros` field.

### 1.2 The S8 machinery this slice reuses verbatim

- **Press context** `CombatPressViewDelay` + `press_view_delay_micros(ctx,
  caster)` (`position_history.rs:83-92, 387-395`): returns the press's clamped
  view delay **only inside the press transaction** (stamp == `ctx.timestamp`).
  For an **instant sweep**, that is exactly the resolve transaction — the delay
  is readable with **no freeze**. For a **delayed sweep**, resolve is a later
  transaction, so the delay must be frozen onto the pending row (G3, mirrors
  melee).
- **Pose resolution** `rewound_pose_for(ctx, target, view_delay_micros, now,
  present)` (`position_history.rs:430-509`): already per-target, so it *is* the
  per-victim primitive — call it once per candidate. All §2.4 never-rewind
  rules (barrier clamp, active special-movement override, oldest-clamp,
  degrade-to-present) live inside it.
- **The single-target overlay** `overlay_press_rewound_target_pose`
  (`position_history.rs:515-551`) is the exact shape S10 generalizes: swap only
  `pos_*`/`facing_yaw` on a snapshot clone, leave vitality/status present,
  gated on `enabled` + `press_view_delay_micros > 0` + `can_harm`.
- **The melee freeze precedent (D2).** `PendingMeleeImpact.view_delay_micros`
  (`melee.rs:760-764`), frozen at press (`melee.rs:3436:
  press_view_delay_micros(ctx, caster)`), re-checked at impact
  (`melee.rs:4298-4326`: `rewound_pose_for` → recompute reach → `[LAG_COMP]
  impact_recheck` dual-verdict line → use rewound verdict iff switch on). G3
  is this pattern for `PendingAreaImpact`.
- **Config** `combat_lag_comp_config` + `set_lag_comp_config`
  (`position_history.rs:66-166`): master `enabled` (default ON) and
  `auto_swing_enabled` (S9, default ON). S10 adds one flag under the master
  switch (G5).

**Consequence: S10 needs no new rewind code.** It needs (a) the sweep victim
loop to resolve one rewound pose per candidate and test membership against it,
(b) the delayed-sweep path to carry the frozen press delay, and (c) the client
to report a view delay for no-target casts. Everything from candidate to
defense resolution is already built and audited by the `[LAG_COMP]` grammar.

### 1.3 What the client reports today — and the sweep gap

`AttackerViewTime.ViewServerTimeMsFor(target)` (`AttackerViewTime.cs:19`)
needs a target entity: it returns `ServerNowMs −
target.PresentationEffectiveDelayMs`, gated on `HasPreciseSample`, 0 = no
report. `SpellInputHandler` (`SpellInputHandler.cs:918-924`) fills the cast's
`viewServerTimeMs` **only when the pressed `targetId` equals the UI-selected
target**; a no-target area cast sends **0**. So today an AoE press carries no
delay and the server has nothing to rewind by — the sweep's present-time
behavior is *client-enforced*. The shared per-connection delay the sweep needs
already exists: `ServerTimeDelayBudget.BudgetMs(now)` (the S7 adaptive budget,
`ServerTimeDelayBudget.cs`), 66–200 ms, precise-clock gated. G2 wires it.

### 1.4 Projectile impacts resolve present-time, in the flight tick

`tick_combat_projectiles` (`combat/projectiles.rs:81`, per-frame from
`game_loop.rs:960`) advances each projectile and, on a raycast hit against a
present-pose candidate (`advance_projectile_with_collision`,
`projectiles.rs:1176`), resolves against the hit victim's present
`PlayerSnapshot` → `resolve_projectile_defense_with_metadata`
(`projectiles.rs:1315`) → `resolve_defensible_combat_hit`
(`projectiles.rs:1328`, `delivery_kind = Projectile`). The impact is **not** in
the launch transaction; `ActiveCombatProjectile` (`combat.rs:601-652`) has no
`view_delay_micros`. Launch validation (the `cast_request`/`melee_attack`
press) already rewinds via S8. See §2.6 for why the impact should stay
present-time.

---

## 2. Target contract — per-victim sweep rewind

### 2.1 One frozen delay, one rewound pose per victim

At `resolve_area_impact`, when the switch is ON (G5) and a nonzero view delay
is in scope, each candidate victim is tested for area membership against its
**own rewound pose** at `now − view_delay`, resolved by `rewound_pose_for`.
The caster/area origin and facing stay **present** — the attacker's own frame
is server-authoritative, exactly the S8 rule that "attacker pose never comes
from history" (§2.4 rule 4). One delay value applies to every victim in the
sweep (it is a caster-level render delay, G2); each victim rewinds through its
*own* history ring, so this is genuine per-victim rewind.

Threading (both paths converge on `resolve_area_impact`):

- `AreaImpactResolution` gains `view_delay_micros: i64`.
- **Instant path** (`cast_generic_area`, `casting.rs:4978`):
  `view_delay_micros: press_view_delay_micros(ctx, caster)` — readable because
  resolve is the press transaction.
- **Delayed path** (G3): `PendingAreaImpact` gains a `view_delay_micros: i64`
  column, frozen at insert (`casting.rs:4959`) with `press_view_delay_micros`,
  read back at `resolve_pending_area_impact` (`casting.rs:5065`). Mirrors
  `PendingMeleeImpact` exactly. A delayed AoE therefore rewinds each victim by
  the press-frozen delay measured from *impact* time — the same D2 semantics
  as melee: the ~150 ms of view delay is compensated, **not** the authored
  windup, so a victim who left during the telegraph is still gone at
  `impact − view_delay`. The telegraph dodge survives; only the render-delay
  slop is corrected.
- **Melee caster-cone/radius path** (`resolve_pending_melee_hit_volume`,
  `melee.rs:4104`): the same shared helper
  (`position_history::sweep_rewind_membership`) wraps `melee_hit_volume_
  contains_player`, keyed by the already-frozen `PendingMeleeImpact.
  view_delay_micros`. No new schema — only the client no-target report
  (`MeleeInputHandler.cs`) and the membership wrap. Both cone kinds now rewind
  through one code path with one `[LAG_COMP] sweep_hit` grammar.

### 2.2 Membership test uses the rewound pose; everything else stays present

Inside the candidate loop, build an overlaid snapshot (clone with `pos_*` and
`facing_yaw` swapped to the rewound pose — the `overlay_press_rewound_target_
pose` shape) and pass **that** to `area_shape_contains_player`. All other
per-candidate gates — `alive`, `player_id == caster`,
`players_share_world_context`, `target_audience_allows` — and the entire hit
resolution (`resolve_blockable_spell_hit` and below) receive the **present**
snapshot. In particular the impact/event point stays the victim's present
position (`casting.rs:5212: point: Vec3::new(player.pos_x, …)`), and the
defense arc / `DefensibleCombatHit` fields are built from present poses (§2.4
rule 5). The rewind decides *inclusion*; the present decides *resolution*.

### 2.3 Candidate pre-filter must widen by the rewind travel

The present-pose disc pre-filter (`query_disc_indices` at
`area_shape.query_radius()`, `casting.rs:5138`) is the exact edge case the
slice exists for: a victim who was inside the shape at view time but has since
strafed out is the **rewound-in / present-out flip** we want to *land* — yet
if they moved far enough to exit the present disc, they are never a candidate
and never rewound-tested. When the switch is ON and `view_delay_micros > 0`,
expand the pre-filter radius by a **rewind-travel margin** =
`(max_rewind_ms / 1000) × MAX_PLAYER_SPEED` (≈ 0.25 s × ~7 m/s ≈ 1.75 m; use
the authored max speed constant). The margin only *adds* candidates; the
rewound membership test still excludes any that are genuinely out, so
present-time behavior with the switch OFF is byte-for-byte unchanged. Without
this widening the slice silently under-includes fast movers at the boundary.

### 2.4 Never-rewind rules — inherited, not re-implemented

All of S8 §2.4 holds by construction because every rewound pose comes from
`rewound_pose_for`:

1. **Never rewind defenses** — `resolve_defensible_combat_hit` runs on present
   state; the widened success grace (S8 §3) already applies to spell hits
   through the shared funnel.
2. **Never rewind vitality/status** — alive / audience / world-context /
   miss checks read present, before or independent of the pose swap.
3. **Never rewind through discontinuities** — barrier clamp and active
   special-movement override are inside `rewound_pose_for`; a victim mid-dash
   validates present.
4. **Attacker/area frame never comes from history** — origin, facing, and
   area center are present.
5. **Emitted events stay present-time** — impact point, VFX anchor, defense
   arc vector all present.

### 2.5 Failure containment

No report / zero report / `view_delay_micros == 0` / missing history / switch
OFF → present-time membership, byte-for-byte today's game. `rewound_pose_for`
never errors and degrades to present. The pre-filter widening is gated on
switch-ON + nonzero delay, so an OFF leg's candidate set is identical to
today.

### 2.6 Projectile impacts — recommend **keep present-time** (G1)

The recorded ruling (S8 D5, S9 §7) is that projectile *impacts* stay
present-time. Reopening it here, the analysis **reaffirms** it:

- **Rewinding a projectile impact partially negates the visible dodge.** A
  projectile travels visibly; the victim watching it and side-stepping in the
  last ~150 ms is the counterplay. Rewinding the impact hit-test by the frozen
  launch view delay checks the victim at `impact − view_delay` — i.e. it
  *re-hits* a victim who dodged within the reaction window. That is the one
  thing the "dodging is counterplay" ruling exists to protect (parallel to the
  accepted no-impact-LOS-re-check exposure, review §8).
- **It does not even fix the attacker's real problem.** For a straight-line
  projectile the attacker aimed at the render-delayed position; the projectile
  *physically* flew there and passed where the target used to be. Rewinding the
  victim back into that path is dodge-negation, not aim-correction. For a
  homing/`intended_target` projectile the bolt already tracks the live target,
  so there is no aim gap to close. Launch validation (LOS/target-lock)
  **already** rewinds via S8, which is the fair, honest half.
- **Net:** the projectile fairness win is at *launch* (already shipped);
  rewinding the *impact* trades away skillful dodging for no attacker benefit.

**Recommendation: decline — S10 ships sweeps only, the projectile-impact
ruling stands.** If the owner reopens (G1 = reopen), the mechanism is a
straight mirror of G3: add `view_delay_micros` to `ActiveCombatProjectile`,
freeze `press_view_delay_micros` at each launch site (`melee.rs:3945`,
`casting.rs:3225`, `casting.rs:3345`), and rewind the hit victim in
`resolve_projectile_defense_with_metadata` — a `[LAG_COMP] projectile_impact`
dual-verdict line, gated behind a separate `projectile_rewind_enabled` flag so
it A/Bs independently of sweeps. Recorded so the owner can rule with the cost
in front of them; not built unless G1 says so.

---

## 3. Evidence & automated acceptance (no hand-recorded numbers)

**Audit:** the sweep victim loop emits a `[LAG_COMP] sweep_hit` dual-verdict
line per rewound candidate, in the existing grammar
(`melee_gate`/`impact_recheck`/`auto_reach`/`auto_los`,
`melee.rs:3014,4298`), with `present=in|out rewound=in|out flip=bool` and
`signal=press` (sweeps always carry a press-derived delay). To bound log
volume at area scale, log at **info** only when the line carries signal (a
flip, or a used inclusion) and at **debug** when present and rewound agree —
the S9 log-volume rule. Flip rate on `sweep_hit` is the S10 money metric; OFF
legs still log would-be verdicts (S8 property, inherited).

**Analyzer:** extend `ops/analyze-s8-lag-comp.py` in place — add `sweep_hit`
to `CHECKS` and the `GATE_RE` alternation; the existing per-check / per-signal
/ flip-rate / pose-source summary then covers it with no new code path. (If G1
reopens projectiles, add `projectile_impact` the same way.)

**Headless probe leg (server truth) — PASS 2026-07-05:**
`ops/s10-sweep-rewind-probe.py`, self-verifying PASS/FAIL, throwaway
measurement-build DB (`ARENA_NPC_HARMLESS=1 ARENA_NPC_AGGRO_RADIUS=100`),
reusing the S8/S9 arrangement. Vehicle: the attacker probe casts **`ICE_SPIKES`**
(a no-target SPELL AREA CASTER_CONE, 7.5 m, learned via `learn_spell` since the
starter spellbook is random) with `view_server_time_ms = now − 250 ms` while a
slowly-shuttled kobold sweeps through the cone's range boundary. This exercises
the spell-area path (`resolve_area_impact`) and the shared
`sweep_rewind_membership` helper / `[LAG_COMP] sweep_hit` grammar that the melee
caster-cone path (`resolve_pending_melee_hit_volume`) uses identically; the
melee path additionally freezes the delay onto `PendingMeleeImpact` (D2
pattern), covered by construction + the shaped A/B. Checks (all green live):

- **Rewind in control (gate):** with the switch ON, ≥1 `sweep_hit flip=true
  enabled=true` line where the used verdict is the rewound one. Both polarities
  exercise the `if use_rewound { rewound } else { present }` branch — *entry*
  (present-in, rewound-out → victim **excluded**, favor accuracy) reliably
  captured by casting just after the target strafes into the shape; *exit*
  (present-out, rewound-in → victim **included**, the S10 win) is the
  boundary-timing polarity, logged best-effort, gated only on ≥1 used flip
  (S9 precedent).
- **Instant vs delayed path (both freeze routes):** cast one `impact_delay_ms
  == 0` sweep and one `> 0` sweep; require ≥1 `sweep_hit signal=press` from
  each — the delayed one proves the `PendingAreaImpact.view_delay_micros`
  freeze/thaw round-trips (G3).
- **Pre-filter widening:** with the target strafed just outside the present
  shape but inside the rewound shape, the probe still sees a `sweep_hit`
  evaluation for it (proves §2.3; without the widening there would be zero
  lines for that victim).
- **Degradation:** switch OFF → would-be flips logged, none used, membership
  identical to present; zero view report (targeted-cast control) → no
  `sweep_hit` rewound evaluation; config default `sweep_rewind_enabled=false`
  → present-time despite a fresh report.
- **Client-signal unit check:** a tiny assertion that a no-target cast now
  ships a nonzero `view_server_time_ms` (server logs the clamped press-delay
  row) where before it shipped 0.

**Owner client leg (feel + wiring):** shaped +40/+40 ms per
`docs/latency-testing.md`. Arm and cast an area spell repeatedly while
**the player strafes** so victims cross the shape boundary during the render
delay (a stationary target rewinds to the same pose → zero delta → no flips,
so relative motion is mandatory). OFF/ON legs, scored by the analyzer
(`sweep_hit [signal=press]` flip rate: ~0 OFF, nonzero ON) plus the existing
contact-cue ledger.

**Gate (S7/S8/S9 precedent):** `sweep_rewind_enabled` ships **default OFF**;
the shaped owner A/B is the decider. PASS → flip default ON in the acceptance
commit and close S10. FAIL → park OFF, record resolved-dropped with the
numbers.

---

## 4. Kill switch & config

`combat_lag_comp_config` gains `sweep_rewind_enabled: bool` (seeded `false`);
`set_lag_comp_config` gains a matching argument (now
`enabled, max_rewind_ms, auto_swing_enabled, sweep_rewind_enabled`). The S8
master `enabled` still gates everything — S10 activates only when both flags
are on, so the S8 kill switch stays one command and the A/B flips S10 alone.
History writing, standing-row writing, and all audit logging stay on
regardless. The absent-row default was `false` at implementation, **flipped to
`true` 2026-07-05 by owner call** (`lag_comp_sweep_rewind_enabled` →
`unwrap_or(true)`) — shipped on the probe PASS rather than the shaped A/B gate.
(If G1 reopens projectiles, a fifth arg `projectile_rewind_enabled` A/Bs that
half separately.)

---

## 5. Never-rewind & trust (unchanged from S8)

Trust is identical in kind to S8/S9: the sweep delay is client-claimed
(now a connection-budget value for no-target casts), sanity-clamped `≤ now`
and to `max_rewind_ms` (250) at `record_press_view_delay`. The worst cheat
remains "validate against a 250 ms-old world" — the same bounded exposure
every rewind system accepts, now covering area membership. The kill switch and
the `[LAG_COMP] sweep_hit` counters keep it observable.

---

## 6. Owner decisions

*(Letters continue the S8 (D)/S9 (E) sequence; F is skipped — the review uses
F-numbers for feel-audit items.)*

| # | Decision | Recommendation |
|---|----------|----------------|
| G1 | **Projectile-impact scope** | **Decline the reopen — keep projectile impacts present-time; S10 ships sweeps only.** Rewinding a projectile impact partially negates the visible in-flight dodge and does not fix straight-line attacker aim; launch already rewinds (§2.6). Reopen only if you want the full-fat area model to cover projectiles too, at the cost of dodge counterplay. |
| G2 | **Sweep view-delay signal source** | **For no-target (area) casts, the client reports `ServerNowMs − ServerTimeDelayBudget.BudgetMs` (the shared S7 connection budget).** A sweep has no single victim to derive a per-entity delay from; the connection budget is the honest caster-level render delay all victims are seen at. Small client change (`SpellInputHandler`), no wire change. Alternative: leave no-target casts at 0 → sweeps never rewind → the slice is a no-op. |
| G3 | **Delayed-sweep freeze** | **Add `view_delay_micros` to `PendingAreaImpact`, frozen at press, read at tick resolve — same D2 pattern as `PendingMeleeImpact`.** Instant sweeps read `press_view_delay_micros` in-transaction (no freeze). Rewinds by view delay from impact time; the authored windup dodge is untouched. |
| G4 | **Candidate pre-filter widening** | **Expand `query_disc_indices` by the rewind-travel margin (~1.75 m) when the switch is on and a delay is present (§2.3).** Without it the slice under-includes the boundary flips it exists for; the margin only adds candidates the rewound test then filters, so OFF behavior is unchanged. |
| G5 | **Config shape & default** | **New `sweep_rewind_enabled` flag, default OFF, under the S8 master switch; shaped A/B flips it (gate precedent).** One command still kills all of lag comp. |

## 7. Recorded gaps / future extensions (not this slice)

- **Projectile-impact rewind** — mechanism recorded in §2.6; built only if G1
  reopens. The philosophical ruling stands otherwise.
- **Per-victim delay for sweeps** — v1 rewinds all sweep victims by one
  caster-level delay (G2). A per-victim delay (each victim by *its own*
  presentation delay) would need the client to report a vector or the server
  to estimate per-target — not worth it until sweeps prove unfair with the
  shared budget. Same "good enough, bounded" call as S8's single-target delay.
- **Deferred defense resolution** (S11, D4b) — still gated on S9's
  `[DEFENSE_LATE]` telemetry, unaffected by S10.
- Aerial items: still deferred, unchanged.

## 8. Churn notes

- Schema: one new column on `CombatLagCompConfig` (`sweep_rewind_enabled`) and
  one on `PendingAreaImpact` (`view_delay_micros`); `set_lag_comp_config` gains
  an arg. Bindings regen (canonical bin-path mode); no public-row or
  subscription changes. (If G1 reopens: + `ActiveCombatProjectile.view_delay_
  micros`, + `projectile_rewind_enabled`.)
- Client: `SpellInputHandler` **and** `MeleeInputHandler` populate
  `viewServerTimeMs` for no-target casts/presses from the shared budget (via a
  new no-target `AttackerViewTime.ViewServerTimeMsForConnection` accessor); no
  other client changes. Server-side, one shared helper
  (`position_history::sweep_rewind_membership`) serves both the spell-area and
  melee caster-cone/radius loops.
- Ops: probes that call `set_lag_comp_config` (`ops/s8-lag-comp-probe.py`,
  `ops/s9-auto-rewind-probe.py`) add the new arg (`false` is the no-op value).
  Analyzer extended in place.
- Tests: deferred until the contract stabilizes (standing churn ruling).
