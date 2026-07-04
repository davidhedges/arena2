# S9 — Standing View-Delay Signal + Auto-Attack Tick Rewind (Design, 2026-07-04)

Slice S9: the first recorded gap from
`docs/lag-compensation-design-2026-07-04.md` §7 — **auto-attack tick rewind**,
which requires the **standing view-delay signal** as its substrate. Also
carries a zero-behavior-change telemetry rider (**[DEFENSE_LATE]**) that
produces the parry-loss numbers D4b (deferred defense resolution) was
explicitly parked on. Per standing practice: **design doc first,
kill-switched, shaped A/B decides the default.** The owner decisions in §6
gate implementation. Everything in §1 was verified in code today.

Sequencing context (recorded when this slice was scoped): S9 first because
the standing signal is the shared substrate — S10 (per-victim sweep rewind)
reuses S8 machinery only, and S11 (deferred defense resolution) is gated on
the telemetry this slice starts collecting and is designed better with a
per-defender delay signal than with a flat 150 ms hold. Aerial items stay
deferred, unchanged.

**Status: ACCEPTED (2026-07-05) — §6 rulings E1–E5 accepted; headless probe
leg PASS (§4) and shaped owner A/B PASS. Default flipped to
`auto_swing_enabled=true` (owner call "leave it on"): with S9 ON, kiting at
the reach edge, the rewound pose controlled 78.5% of auto-swing reach
verdicts (holding swings that present-time would fire, to match the
attacker's view) and whiffed 63.6% of edge impacts that present-time would
land — the favor-accuracy behavior, no feel regression. `set_lag_comp_config
true 250 false` disables S9 alone; the S8 master kill switch is unchanged.
Slice closed.**

Implementation note (log volume): a due-held auto swing re-evaluates every
tick (~30 Hz), so the `auto_reach`/`auto_los` dual-verdict lines log at
info only when they carry audit signal — a flip, or an evaluation that
proceeds/fires — and at debug when both verdicts agree on a hold. Flip
evidence and OFF-leg would-be verdicts are fully preserved; unanimous holds
don't spam ~30 info lines/s per held attacker. The analyzer's flip rate is
therefore "flips per consequential evaluation", not per tick.

**Principle (unchanged from S8):** *the rewind decides whether an attack
connects; the present decides everything about how it resolves.* S9 extends
the S8 rewind to the one attack path with no press to carry a view time; it
adds no new rewind semantics — every never-rewind rule, barrier, and clamp is
inherited by construction (§2.4).

---

## 1. What exists today (verified)

### 1.1 The auto-attack tick is the last present-time player attack path

`tick_auto_attacks` (`auto_attack.rs:320`, runs inside the `game_tick`
reducer transaction, `game_loop.rs:926`) holds a due swing on two positional
gates, both **present-time**:

- **Reach hold** (`auto_attack.rs:447-461`): present `caster_phys` vs present
  `target_snapshot`, `range + hit_radius`; out of reach → `mark_pending_due`
  and retry next tick.
- **LOS hold** (`auto_attack.rs:463-480`, S4): present snapshots for both
  endpoints; blocked → same silent hold/retry.

A due swing that passes both dispatches through
`attempt_auto_attack_replacement` / `perform_intrinsic_auto_attack_for`
(`auto_attack.rs:491-515`) — i.e. **into the ordinary melee targeted chain**,
where S8's rewind machinery already lives. The chain stays present-time for
these swings only because of the S8 press-context rule (§1.2). D5 recorded
this exclusion as deliberate: *there is no press to carry a view time.*

NPC attacks are out of scope by construction: the cadence swing in `npcs.rs`
is the only NPC attack path and `auto_attack.rs` has zero NPC references
(S3/S6 finding, re-confirmed). NPCs have zero latency and never rewind.

### 1.2 The S8 machinery this slice reuses verbatim

- **Press context row** `CombatPressViewDelay`
  (`combat/position_history.rs:76-84`): the press's clamped view delay,
  stamped with the press transaction's timestamp.
  `press_view_delay_micros` (`position_history.rs:247-255`) returns it **only
  when the stamp equals `ctx.timestamp`** — that exact-match rule is why auto
  swings are present-time today, and it is also the extension point: a
  context stamped *by the tick, in the tick's transaction* is
  indistinguishable from a pressed one to every downstream consumer.
- **Chain read** (`melee.rs:2969-2977`): the whole extracted positional gate
  (facing, range, min-range, LOS endpoints) resolves one rewound target pose
  when a press context exists.
- **Impact freeze** (`melee.rs:3413` → `melee.rs:4263-4277`): the press
  delay is frozen onto the pending impact row (`view_delay_micros`,
  `melee.rs:760-763`) and the D2 impact re-check rewinds by it.
- **Pose resolution** `rewound_pose_for` (`position_history.rs:290-369`):
  history ring, rewind barriers, active-special-movement override,
  oldest-clamp, degrade-to-present. All §2.4 never-rewind rules live here.
- **Config** `combat_lag_comp_config` + `set_lag_comp_config`
  (`position_history.rs:63-108`), default ON since the S8 acceptance.

**Consequence: S9 needs no new rewind code.** It needs (a) a standing delay
value for the tick to stamp, and (b) the tick's own two hold gates to read
the same rewound pose. Everything between dispatch and impact is already
built and already audited by the `[LAG_COMP]` dual-verdict lines.

### 1.3 The ping surface is the natural report carrier

- Server: `ping_clock` (`ping.rs:9-10`) is a deliberate no-op —
  `(_ctx, _client_send_ms: u64)`. The "writes no rows" rationale in its
  comment is **replication fan-out**, which a private-table write does not
  cause (private rows never reach subscribers; only the caller sees its own
  transaction update either way).
- Client: `NetworkManager.SendClockPingIfDue` calls it every
  `ClockPingIntervalSeconds = 2` (`NetworkManager.cs:113,246`) — exactly the
  "slow-cadence client report, ~2 s, piggybacked on `ping_clock`" the S8 §7
  note sketched.
- The client already knows what to report: `AttackerViewTime`
  (`AttackerViewTime.cs`) computes
  `ServerNowMs − target.PresentationEffectiveDelayMs` per entity, gated on
  `ArenaServerClock.HasPreciseSample`, 0 = no report. The S7 acceptance
  proved `LastEffectiveDelayMs` tracks real delivery
  (`RemotePresentationBuffer.cs:156`).

As with the press report, the **uplink half needs no reporting**: the server
computes `delay = arrival_time − claimed_view_time`, and the ping's transit
is automatically inside the difference.

### 1.4 The defense resolve choke point (telemetry rider)

`resolve_defensible_combat_hit` (`defense.rs:486-531`) is the single funnel
for every defensible hit — melee impacts (`melee.rs:4337`), NPC pending
swings (`npcs.rs:1121`), projectile impacts (`projectiles.rs:1328`), and the
two spell sites (`casting.rs:1289,6264`). A payload-valid, parryable-or-
blockable hit that returns `DefenseResolution::None` is exactly "a
defensible hit resolved undefended" — including the late-press case where no
`defense_state` row existed yet (`defense.rs:494-496`). `start_parry`
(`defense.rs:189`) and `start_block` (`defense.rs:306`) are the press sites
that can measure their own lateness against it. No timing state exists for
this today; D4b was declined partly for lack of exactly these numbers.

---

## 2. Target contract — standing signal + tick rewind

### 2.1 Standing view-delay report (client → server)

`ping_clock` gains one argument: `view_server_time_ms: u64` (0 = no report —
byte-for-byte today's behavior, and the value every non-reporting caller
passes, including existing ops probes).

**Client report policy (E1):** report only while an auto-attack target is
armed, using that target's buffer —
`AttackerViewTime.ViewServerTimeMsFor(armedAutoTarget)` — else 0. Purpose-
true and minimal: server-initiated swings are the only S9 consumer, and they
exist only while a target is armed. The signal is per-attacker but
effectively per-armed-target, which is the same honesty level as the S8
press report. (S11 may want an always-on aggregate for defenders; that is a
client-side policy widening later — the wire contract does not change.)

**Server-side row** — new private table:

```
combat_standing_view_delay
  identity: Identity            ← primary key
  updated_at_micros: i64
  view_delay_micros: i64        ← clamped [0, max_rewind_ms] at write
  reported_view_ms: u64
  clamped_to_max: bool
```

Written by `ping_clock` when the report is nonzero (mirrors
`record_press_view_delay`, `position_history.rs:213-242`: sanity-clamp claim
≤ now, clamp delay to `max_rewind_ms`); deleted on zero-report and alongside
`clear_position_history` on despawn/disconnect. `ping_clock`'s doc comment
updates honestly: it now writes one small private row per reporting player
per 2 s — no fan-out (private), and negligible against 30 Hz physics commits
for local commitlog growth (the standing cleanup recipe covers it).

**Staleness (E2):** a read older than **6 s** (three missed pings) is
ignored — the client stopped reporting, degraded, or disconnected. Stale or
absent → present-time, never a reject; identical degradation ladder to S8
§2.5.

**Trust analysis (unchanged in kind from S8 §2.1):** the report is
client-claimed and clamped, so the worst cheat remains "validate against a
250 ms-old world" — the same bounded trust already accepted, now on a 2 s
cadence instead of per-press. Same audit counters, same kill switch, and the
standing row is one more thing `ops/analyze-s8-lag-comp.py` can distribute.

### 2.2 Auto-attack tick rewind — one stamp, one pose, one timeline

When the switch is ON (E4) and a fresh standing row exists for the owner, a
**due** swing (`auto_attack.rs:434` onward) does the following before its
hold gates:

1. **Stamp the press context** for the owner in the tick's transaction:
   `CombatPressViewDelay` with `stamped_at_micros = ctx.timestamp` and the
   standing `view_delay_micros`, plus `signal = "standing"` (new column on
   the private row, `"press"` for reducer-stamped contexts — audit lines and
   the analyzer split on it).
2. **Resolve one rewound target pose** via `rewound_pose_for` and feed
   **both hold gates** from it: reach (`auto_attack.rs:447`) and the LOS
   target endpoint (`auto_attack.rs:468`; caster endpoint stays present —
   the attacker's own pose is server-authoritative here, there is no claim
   to honor, matching §2.4 rule 4 in spirit). Each hold gate logs an
   S8-grammar dual-verdict line (`[LAG_COMP] auto_reach …` /
   `[LAG_COMP] auto_los …`, same fields as the existing `melee_gate` line
   at `melee.rs:3014`, plus `signal=standing`) — flip rate on these is the
   S9 money metric.
3. **Dispatch as today.** Because the context row matches `ctx.timestamp`,
   the melee targeted chain (`melee.rs:2969`) rewinds exactly as a reported
   press does, and the pending impact row freezes the standing delay
   (`melee.rs:3413`) so the D2 impact re-check uses it — **the swing that
   fired because of the rewound pose also connects or whiffs on that same
   frozen timeline.** No mixed-timeline swing can exist (the S8 §2.3
   coherence rule, satisfied by construction).

A held swing re-evaluates each tick with a fresh transaction stamp and the
then-current standing delay, exactly like today's retry loop.

What stays present-time, explicitly:

| Path | Ruling |
|---|---|
| Cadence/scheduling (`next_swing_at`, pending_due, pause/mode/epoch logic) | Untouched — rewind changes *where the target is checked*, never *when swings happen* (S6 contract intact). |
| Vitality/status/world-context clears (`auto_attack.rs:336-369`) | Present — §2.4 rule 2, inherited. |
| Replacement-auto *arming* (`arm_auto_attack_replacement`, `auto_attack.rs:78-176`) | Untouched — it validates ability/profile/catalog state only, nothing positional to rewind; the replacement *swing* rewinds via the same tick stamp as intrinsic swings. |
| NPC swings | Never rewind (S3; NPCs have zero latency). |
| Projectile-mode autos (bow), if the armed mode fires a projectile | Launch validation rewinds via the stamp like any chain dispatch; projectile *impact* stays present-time (S8 D5, untouched — S10 territory). |

### 2.3 Failure containment

No report, zero report, stale row, missing history, switch OFF — every
degraded path is byte-for-byte the shipped S8 behavior (present-time holds).
The stamp is transaction-scoped, so a crash between stamp and dispatch
leaves nothing dangling (`press_view_delay_micros` ignores stale stamps by
the exact-match rule).

---

## 3. Telemetry rider — [DEFENSE_LATE] (no behavior change)

Two pieces, both logging-only, active regardless of the switch:

1. **Stamp:** new private row `combat_last_undefended_hit`
   `(identity ← defender, resolved_at_micros, delivery_kind)`, upserted
   inside `resolve_defensible_combat_hit` whenever a payload-valid,
   parryable-or-blockable hit returns `DefenseResolution::None`. Deleted
   with the entity's other combat rows.
2. **Press check:** `start_parry` / `start_block`, after their existing
   validation, read the row; if `now − resolved_at_micros ≤ 400 ms` (E5),
   log `[DEFENSE_LATE] defender=… kind=parry|block late_by_ms=…
   delivery=…`. 400 ms = the 250 ms rewind cap plus reaction jitter — wide
   enough to see the distribution's tail, not so wide it counts unrelated
   presses.

This is the D4b re-evaluation dataset: the S11 decision becomes "the
late-press loss happens N times per hour of combat with a p50 lateness of X
ms" instead of intuition. The analyzer (§4) summarizes it from any session's
logs. Measurement note: `ARENA_NPC_HARMLESS` damage-0 IMPACTs still resolve
through the funnel and will stamp — fine for probe legs, and real telemetry
comes from unshaped owner sessions anyway.

---

## 4. Evidence & automated acceptance (no hand-recorded numbers)

**Audit:** the two new tick hold gates emit `[LAG_COMP] auto_reach` /
`[LAG_COMP] auto_los` dual-verdict lines in the existing grammar
(`melee_gate` / `impact_recheck`, `melee.rs:3014,4288`); all four line kinds
gain a `signal=press|standing` field. Flip rate on auto checks is the money
metric; OFF legs still log would-be verdicts (S8 property, inherited).

**Analyzer:** extend `ops/analyze-s8-lag-comp.py` (same log grammar): split
all existing tables by `signal`, add the auto-check section, add a
`[DEFENSE_LATE]` section (count, rate per combat-minute, late_by_ms
distribution by kind).

**Headless probe leg (server truth):** `ops/s9-auto-rewind-probe.py`,
self-verifying PASS/FAIL, throwaway measurement-build DB
(`ARENA_NPC_HARMLESS=1 ARENA_NPC_AGGRO_RADIUS=100`), reusing the S8
arrangement — runner probe tows a chasing kobold; attacker probe parks off
the line and **arms auto-attack on the kobold**, pinging with a claimed view
time ~250 ms old:

- Rewind in control (gate): with the switch ON, `auto_reach flip=true
  enabled=true` lines prove the rewound verdict overrides the present-time
  one. Both polarities exercise the same `if use_rewound { rewound } else
  { present }` branch — *entry* (present in-reach, rewound hold → swing
  HELD, favor accuracy) and *exit* (present hold, rewound in-reach → swing
  FIRES, the S9 win). The probe gates on ≥1 used flip and reliably captures
  the entry polarity; the exit polarity needs the target's reach-exit to
  land inside the ~250 ms pre-due window (a chasing-NPC timing lottery, not
  a code question) so it is logged best-effort, never gated. With the switch
  OFF the same geometry logs would-be flips but uses none, and no
  standing-stamped swing dispatches.
- One-timeline rule (E3, gate): a fired swing's chain gate and pending
  impact both carry the standing delay — the probe requires ≥1 `melee_gate`
  AND ≥1 `impact_recheck` line with `signal=standing`, which only coexist
  when a swing actually dispatched on the standing timeline and froze that
  delay onto its impact.
- Standing signal + degradation: nonzero report writes a clamped row, zero
  report deletes it; stop pinging > 6 s → holds go present-time (no rewound
  evaluation) though the stale row lingers; config default
  `auto_swing_enabled=false` (E4) → present-time despite fresh reports.
- Rider: the runner presses parry right after each undefended warrior IMPACT
  → `[DEFENSE_LATE] kind=parry` lines with `late_by_ms` in the 0–400 ms band.

**Owner client leg (feel + wiring):** shaped +40/+40 ms per
`docs/latency-testing.md`. Arm auto-attack on a kobold, then **the player
moves** — side-strafe past it and back-pedal so the player↔kobold distance
repeatedly crosses the auto-reach boundary while swings fire; that relative
motion is what produces the rewound-vs-present flips (a stationary target
rewinds to the same pose → zero delta → no flips, so the motion is
mandatory and must be the player's). Kobolds die in ~4 autos; respawn as
needed — flips accumulate in seconds, no single target need survive the leg.
OFF/ON legs, scored by the analyzer (`auto_reach [signal=standing]` flip
rate: ~0 OFF, nonzero ON) plus the existing contact-cue ledger.

**Headless leg result (2026-07-04): PASS.** `ops/s9-auto-rewind-probe.py`
against a throwaway `ARENA_NPC_HARMLESS` DB — all gates green: config default
OFF; standing write/clamp(250 ms)/zero-delete/flow; OFF audits would-be flips
but dispatches nothing on the standing stamp; ON puts the rewound verdict in
control (32 used flips overriding present-time across 14 cycles) and satisfies
the E3 one-timeline rule (35 `melee_gate` + 35 `impact_recheck` `signal=standing`
lines — swings that dispatched on the standing timeline and froze the delay
onto impact); 6 s staleness degrades to present-time; the `[DEFENSE_LATE]` rider
logged 50/50 reactive parries in-band. The exit-direction fire is best-effort
telemetry (fixture-timing dependent), not a gate — see §4.

**Gate (S7/S8 precedent):** `auto_swing_enabled` ships **default OFF**; the
shaped owner A/B is the remaining decider. PASS → flip default ON in the
acceptance commit and close S9. FAIL → park OFF, record resolved-dropped with
the numbers.

---

## 5. Kill switch & config

`combat_lag_comp_config` gains `auto_swing_enabled: bool` (seeded `false`);
`set_lag_comp_config` gains the matching third argument. The S8 master
`enabled` still gates everything — S9 activates only when both flags are on,
so the S8 kill switch remains one command and the A/B can flip S9 alone
while S8 stays in its accepted state. History writing, standing-row writing,
and all audit/telemetry logging stay on regardless of both flags.

---

## 6. Owner decisions

| # | Decision | Recommendation |
|---|---|---|
| E1 | Standing report policy | **Report only while an auto-attack target is armed, using that target's buffer delay; else 0.** Minimal and purpose-true; widening to an always-on aggregate is a client-only change later (S11 may want it). |
| E2 | Staleness TTL | **6 s** (three missed 2 s pings). Stale → present-time, never a reject. |
| E3 | One-timeline rule for auto swings | **Both tick hold gates + dispatch chain + impact freeze all use the standing delay via the transaction-stamped press context.** Rewinding the holds but not the impact (or vice versa) recreates the mixed-timeline incoherence S8 §2.3 forbids. |
| E4 | Config shape & default | **New `auto_swing_enabled` flag, default OFF, under the S8 master switch; shaped A/B flips it (S7/S8 gate precedent).** |
| E5 | [DEFENSE_LATE] rider | **Confirm scope: logging only, 400 ms window, active in all builds.** No behavior change; produces the D4b dataset. |

## 7. Recorded gaps / future extensions (not this slice)

- **Per-victim rewind for sweeps/projectile impacts** (S10 candidate): the
  frozen-press-delay pattern applied per candidate victim at resolve;
  needs no new signal. The projectile-impact philosophical ruling
  ("visible in flight, dodging is counterplay") stands unless the owner
  reopens it there.
- **Deferred defense resolution** (S11 candidate, D4b): decide on the
  [DEFENSE_LATE] numbers this slice collects; if taken, design the hold
  around the defender's standing delay (clamped) rather than a flat 150 ms
  — most connections would pay ~70–100 ms of impact latency, directly
  answering the D4 decline reason.
- **Server-side RTT/view-delay estimation** as a cross-check on claims:
  the standing row now gives it a natural comparison point (claimed delay
  vs ping-derived floor); still post-launch anti-cheat hardening.
- Aerial items: still deferred, unchanged.

## 8. Churn notes

- Schema: `ping_clock` gains an arg; `set_lag_comp_config` gains an arg;
  two new private tables (`combat_standing_view_delay`,
  `combat_last_undefended_hit`); one new column each on
  `CombatPressViewDelay` (`signal`) and `CombatLagCompConfig`
  (`auto_swing_enabled`). Bindings regen (canonical bin-path mode); no
  public-row or subscription changes.
- Client: `NetworkManager.SendClockPingIfDue` passes the E1 report (needs
  the armed-auto-target handle; `AttackerViewTime` already computes the
  value); no other client changes.
- Ops: probes that call `ping_clock` or `set_lag_comp_config`
  (`ops/s8-lag-comp-probe.py`, any lap/input probes sending pings) add the
  new args (0 / `false` are the no-op values). Analyzer extended in place.
- Tests: deferred until the contract stabilizes (standing churn ruling).
