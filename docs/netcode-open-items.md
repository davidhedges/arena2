# Netcode Slices — Open / Pending / Deferred (updated 2026-07-05)

For current timing, authority, and readiness rules, start with the
[combat contract](combat-authoring-contract.md#hit-validation-timing).
The slice decisions below retain their July evidence and dates.

**This is the "what's left" board for the netcode migration.** Companion to
`docs/netcode-design-review-2026-07-03.md` (the plan) and the per-slice design
docs. Slices **S1–S9 are delivered and owner-accepted** (see the design
review's slice table). Everything still open is below.

---

## ✅ SHIPPED default ON (owner call) — shaped A/B DEFERRED as optional spot-check

### S10 — per-victim rewind for cone/radius sweeps
- **State:** implemented + committed (`909c00ef`, default OFF) then **flipped to
  default ON 2026-07-05 by owner call** ("flip the default to ON for now") on the
  strength of the historical server-half **live probe PASS** from
  `ops/s10-sweep-rewind-probe.py` (now ported to the canonical frozen-build
  setup; OFF logged would-be flips and ON put the rewound verdict in control,
  source=history, degrade-to-present). Kill switch: `set_lag_comp_config true 250
  true false` (4th arg = S10; `false, …` = the S8 master kill).
- **Design:** `docs/sweep-projectile-rewind-design-2026-07-05.md` (rulings
  G1 = sweeps only / projectiles stay present-time, G2 = connection-budget
  signal — both owner-signed). Covers **both** sweep paths (spell `AREA` +
  melee caster-cone/radius) via one shared `sweep_rewind_membership` helper.
- **DEFERRED (optional, not a gate): the shaped +40/+40 owner A/B spot-check.**
  Run it if/when there's time to confirm feel + real-client wiring under
  shaping; it can only *reassure*, not block (already live ON). If it ever shows
  a regression, kill-switch it and reopen.
  - Shaping recipe: `docs/latency-testing.md` Profile L (`dnctl` pipes 1/2 at
    40 ms + the port-3000 pf rules).
  - Toggle for the A/B: `spacetime call arena set_lag_comp_config true 250 true
    <false|true>` (args: `enabled max_rewind_ms auto_swing_enabled
    sweep_rewind_enabled`). Verify: `spacetime sql arena "SELECT
    sweep_rewind_enabled FROM combat_lag_comp_config"`.
  - Play pattern: cast an area sweep (Cataclysm/Whirlwind melee, or Ice Spikes/
    Frost Nova/Consecrate spell) at a moving enemy while strafing so it crosses
    the sweep boundary. Back-pedal to hold a chaser near the sweep's OUTER edge
    to force flips.
  - Score: `python3 ops/analyze-s8-lag-comp.py --database arena`, read the
    `== sweep_hit [signal=press]` section. Expect ON `enabled=true` lines with
    `source=history` (real client reports reaching the server) and no feel
    regression. (Flips are a bonus — S8's accepted owner leg had zero flips at
    the owner's play pattern and still passed on wiring + no-regression.)

---

## 🔜 NEXT SLICE — decided by data S9 is already collecting

### S11 — deferred defense resolution (D4b)
- The late-parry case: a parry pressed in honest reaction to a windup seen
  ~150 ms late can arrive *after* the hit resolved and lose whole. The only true
  fix is holding defensible hits open ~150 ms so a late press can still defend.
  **Declined in S8 (D4)** as an accepted exposure (it taxes every hit's damage
  timing).
- **GATE:** decide on the **`[DEFENSE_LATE]`** telemetry S9 ships (logging-only
  rider at `resolve_defensible_combat_hit`, all builds). Run
  `ops/analyze-s8-lag-comp.py` over **real unshaped combat** logs; its
  `[DEFENSE_LATE]` section gives late-press count / rate-per-combat-minute /
  lateness distribution. Decision becomes "the late-press loss happens N/hour at
  p50 X ms" instead of intuition.
- **If taken:** design the hold around the defender's standing delay (clamped),
  not a flat 150 ms — most connections would pay only ~70–100 ms.

---

## 💤 DEFERRED (owner direction / future / post-launch)

- **Projectile-impact rewind** — S10 **G1 DECLINED**: projectile impacts stay
  present-time (visible-in-flight dodge is counterplay; launch already rewinds).
  Reopen only if targeted-action fairness proves insufficient. Mechanism (freeze
  view delay on `ActiveCombatProjectile` at launch) recorded in the S10 design
  §2.6, behind its own flag if ever built.
- **Aerial gating ruling (§5)** — `GROUNDED_ONLY` is authored on every strike;
  the owner ruled it a disputed default. **Decision needed per archetype**
  (gap-closers plausibly `GROUNDED_OR_AIRBORNE`; dash math is server-owned).
  Still deferred by owner direction 2026-07-04. Gates part of S2's eventual test
  matrix (S2's cut-on-reject presentation already handles the `AerialMismatch`
  reject regardless).
- **Per-victim delay for sweeps** — S10 v1 rewinds all sweep victims by one
  caster-level delay (the shared S7 connection budget). A per-victim delay (each
  victim by its own presentation delay) is a future refinement if sweeps prove
  unfair with the shared budget. (S10 design §7.)
- **Server-side RTT / view-delay estimation** — cross-check the client-claimed
  view delays (the S9 standing row gives a natural comparison point). Anti-cheat
  hardening; post-launch (review §8).
- **Interest management** — subscriptions filter by world/instance scope only,
  so a modified client reads every in-scope position through walls. Accepted
  pre-launch; needs server-side distance/relevance filtering for competitive
  integrity later (note SpacetimeDB two-table semijoin limits). (Review §8.)

---

## ✅ Accepted exposures kept deliberately (no action — review §8)
TCP transport (loss = stalls, resync backstop is right); no impact-time LOS
re-check (dodging behind cover after launch is legit counterplay, now the
authored contract); instant cast fizzle on stagger (fine now that telegraphs +
S2 rejection presentation exist).

---

## Housekeeping
- **Local SpacetimeDB disk growth** bit hard on 2026-07-05 (31 GB backlog made
  the machine unusable mid-run). Before a long probe/A/B session: stop the
  server, run `ops/cleanup-local-spacetimedb-data.sh`, restart, republish. See
  the memory note `project_local_spacetimedb_disk_growth`.
