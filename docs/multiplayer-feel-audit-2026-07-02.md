# Multiplayer Feel Audit — 2026-07-02

Player-facing experience slice of the netcode: prediction, reconciliation, interpolation,
smoothing, combat feel under latency, and desync-diagnosis tooling. Companion to
`docs/netcode-sync-audit-2026-07-02.md` (architecture); that audit's findings are
referenced but not repeated.

## Implementation status (updated 2026-07-02)

- **Manual latency verification tabled (2026-07-02).** All by-hand runtime
  checks are deliberately deferred, not forgotten: the F4 live conditioner
  A/B (extrap ratio + hard snaps, old vs new timeline, overlay semicolon
  toggle), the F5 Profile A checks (gap-closer press → instant windup,
  dash ~RTT later with no pose pop; rejected press unwinds windup +
  cooldown + resource), and the F5 slice-2 contact-cue check (melee vs a
  moving target under shaping — there are no NPC patrols; the
  single-operator moving target is a Playground-spawned hostile kobold,
  which chases within its ~8 m aggro radius: kite it backward in a
  straight line so it hovers at the melee range boundary, and count
  contact-cue-but-no-damage per 100 swings via the overlay falsePos
  counter; tune the cue down or flip its flag off on that number), the F1 step-4 denial-toast check (force a
  rejection under Profile A — e.g. press at 0 resource or during a
  server-side stagger — and confirm the toast above the action bar shows
  the server's reason text, mashing never stacks it, and it dismisses
  within ~1.5 s), and the F2 item-4 indicator checks (kill the local
  server → disconnect banner appears and its Reconnect button restores
  the session; dot reads Good on local dev, Degraded under Profile A,
  Bad under Profile B). Recipe: `docs/latency-testing.md`. Run
  these before tuning anything that depends on them (e.g. F4 adaptive
  delay). Progress (2026-07-03): F2 item-4 checks verified live (banner +
  Reconnect after a server kill; dot Good → Degraded → Good across a
  Profile L apply/teardown) and the F1 toast verified end-to-end via a
  spell LOS rejection under shaping. Second round (2026-07-03, downstream-only
  shaping): gap-closer timing verified (instant windup, dash follows, no pose
  pop, no double windup — all pass); gap-close rejection feedback verified
  live (mid-air press → AerialMismatch toast + full rollback; trigger is
  timing-dependent on the grounded flag at server validation); dot Bad
  verified (downstream 180 ms, stationary); Unity Test Runner all green
  (closes the pending note for the F1/F2 HUD tests). **F4 A/B: attempted,
  inconclusive.** Logged legs (`Logs/remote-presentation-ab.csv`, see
  `RemotePresentationAbLog`): arrival timeline 74 s kiting — 4 hard snaps,
  72.3 % extrap ratio, maxErr 0.87 m; server timeline only a 32 s window with
  a mostly-idle target — 0 hard snaps, 88.9 % extrap, maxErr 0.70 m, buffer
  depth −26 → −241 ticks. Two blockers for a conclusive rerun: the legs were
  not like-for-like (motion profiles differed, ON window contaminated by
  session-start clock convergence), and the extrap-ratio/buffer-depth metrics
  are **confounded by target idleness** — a stationary NPC gets no
  `NpcPhysics` rows, so idle time counts as extrapolation and depth dives
  negative by design. Before rerunning: classify idle-vs-late samples in the
  counters (or guarantee continuous target motion for both legs).
  **Update (2026-07-03): S1 delivered** (netcode design review §7) — samples
  now classify interpolated / extrapolating / starved / settled, settled
  entities no longer dive the depth metric, and the CSV gained
  `p_starved`/`p_settled`/`n_starved`/`n_settled` columns. The F4 A/B rerun
  is **unblocked**: use the existing runbook in `docs/latency-testing.md`
  with the `RemotePresentationAbLog` CSV, and compare legs on
  (extrap + starved) / (all non-settled samples) so target idleness cannot
  confound them. F4 adaptive delay stays blocked on the clean A/B itself.
  **Rerun 1 (2026-07-03, post-S1, session 07:50:21Z): still inconclusive —
  this time the run under-delivered, not the metric.** Downstream-only 40 ms
  + 30 % 65 ms jitter, ON then OFF kiting the hostile kobold: the legs
  logged only 26 s / 24 s against the 75 s protocol, and the target was
  settled 82–84 % of samples (~a third of logged seconds had zero
  non-settled activity — the kobold hovered at rest instead of chasing).
  On the S1 metric the legs are statistically identical: late ratio ON
  26.2 % (176/672 non-settled) vs OFF 24.2 % (133/549), a 2.0 pp gap at
  z ≈ 0.8 even treating frames as independent; hard snaps 0 vs 0 (this
  shaping never elicited one); the per-second pos-error samples are almost
  all 0.000 on a settled target, so the nominal OFF edge (mean 0.042 m vs
  0.073 m) rests on ~6 nonzero readings per leg and carries no weight.
  Design note for the clean read: ON runs 100 ms presentation delay vs
  OFF's 66 ms by design, so a *tie* is a loss for ON — it pays +34 ms for
  nothing. Rerun requirements (revised 2026-07-03 — run 1's leg-killers
  were the kobold itself: chase speed equals the player's `MOVE_SPEED`
  7.0, aggro drops beyond the 8 m radius, every landed swing freezes it
  in place for its full 1800 ms cadence, and its damage was ending the
  tester early — sustained chase under stock rules is not reliably
  achievable by hand): (a) republish with both measurement flags baked
  in — `ARENA_NPC_NO_ATTACK=1 ARENA_NPC_AGGRO_RADIUS=100
  ./ops/republish-local-clear.sh` — NPC melee disabled outright (no
  swings, no damage, no cadence freezes) plus a 100 m aggro radius,
  both compile-time in `server/src/npcs.rs` like `ARENA_PROFILE_TICKS`,
  so the kobold does nothing but chase, anywhere in the playground
  (`ARENA_NPC_HARMLESS` still exists separately for checks that need
  real swings with zero damage); (b) run continuous laps at full
  speed — nothing else to manage; (c) 60 s warmup with the timeline
  toggled OFF so warmup rows form a discardable leg 0, then 75 s legs
  interleaved ON/OFF/ON/OFF (≥75 CSV rows each) so leg order can't
  confound; (d) per-leg settled share < ~40 %; a 75 s
  continuously-chasing leg yields ~9 k non-settled samples, enough to
  resolve a 1–2 pp late-ratio gap.
  Also on record: session 07:23:12Z logged a 57 s ON window at 100 %
  starved, depth −1739 ticks (session-start clock-convergence pathology) —
  start legs only after the overlay shows sane depth.
  **Rerun 2 (2026-07-03, session 13:25:52Z, measurement flags live):
  protocol met, verdict conclusive — the server-time timeline as shipped
  does NOT beat arrival.** Five legs (OFF warmup discarded, then
  ON/OFF/ON/OFF, 83–108 s each), continuous chase throughout (settled
  ≤ 1.5 % per leg, ≥ 11 k non-settled samples per leg, depth sane).
  Late ratio: ON 11.9 % / 11.4 % vs OFF 9.2 % / 8.8 % (warmup 8.3 %) —
  pooled 11.6 % vs 9.0 %, every ON leg worse than every OFF leg in the
  ABAB interleave, nominal z ≈ 10; the direction survives any plausible
  autocorrelation discount. Hard snaps 0 everywhere (this shaping never
  elicits them). Position error: ON mean slightly worse (0.357/0.359 m
  vs 0.340/0.342 m) but p95 better (0.420/0.467 m vs 0.499/0.489 m) —
  the tail-smoothing F4 was built for is real, yet ON extrapolates
  2.6 pp more while holding a 34 ms larger delay budget (100 vs 66 ms):
  the fixed server-time mapping is effectively *under*-delayed, because
  absolute delivery lateness vs the estimated server clock has a wider
  tail than inter-arrival gaps, so a fixed 100 ms server-time budget
  buys less headroom than arrival's 66 ms. F4 adaptive delay (S7) does
  not unblock as specced — its gate ("A/B proves the timeline wins")
  failed — but the failure signature (under-delay despite bigger
  nominal budget, better p95) is exactly what an adaptive delay keyed
  to measured *server-time* lateness would cure. Owner decision
  recorded in the design review S7 row. **F5 falsePos: still open** — swings were
  auto-attacks, which do not route through the predicted contact-cue system
  at all (cues hook only predicted action-bar melee presses); the one
  ability press in the log fired and matched cleanly (fired 1 / matched 1 /
  falsePos 0). Redo with an action-bar melee strike.
- **Latency-harness findings (2026-07-03, first live conditioner runs).**
  (a) The movement input lead is keyed to endpoint kind, not RTT:
  `Remote` gets 8 ticks (~264 ms), while `Local`/`Custom` get 2 ticks
  (~66 ms) (`MovementNetDriver.ResolveDesiredServerInputLeadTicks`,
  `MovementNetcodeConfig`), and the tick estimate is arrival-anchored
  (`ClientSimulationState.EstimateAuthoritativeTick`), lagging the true
  server tick by the downstream one-way delay. So shaping localhost
  above ~30–40 ms one-way starves the per-tick command buffer
  (`MOVE_FALLBACK` every tick, fallback forces `jump = false`) and
  local movement rubberbands on every input change — a harness blind
  spot, not general netcode fragility. Local-move fidelity under
  latency is therefore untestable on a shaped local endpoint until a
  dev-only lead override (or RTT-adaptive lead) exists; that is
  movement-netcode work, deliberately not started, and anything
  adaptive shares the F4-adaptive-delay gate.
  (b) ~~Gap-closers do not validate line of sight server-side~~ —
  **closed by S4 (2026-07-04, LOS unification)**: LOS is now a targeting
  rule (`requires_target_los`, default true) checked for every
  target-requiring melee action before gap-close path resolution, so a
  behind-wall gap-close press rejects `LineOfSightBlocked` ("No line of
  sight") and never dashes, while a clear-sight blocked dash path stays
  `GapCloseBlocked` ("Path blocked") — distinct reasons, verified live
  via `ops/s4-los-probe.py`. The F5 slice-1 "rejected press (out of
  range / LOS)" wording now applies to gap-closers too.
- **Design-review backlog (2026-07-03, flagged by live testing — owner has
  ruled these disputed, not endorsed). Review delivered:
  `docs/netcode-design-review-2026-07-03.md` (adversarial; target contracts
  + ordered slices S1–S8 covering every item below).**
  (1) ~~LOS validation is asymmetric~~ — **closed by S4 (2026-07-04)**:
  one authored `requires_target_los` targeting flag per action, default
  true for every hostile targeted action (melee strikes, gap-closers,
  targeted spells, auto-attacks; owner signed off zero opt-outs), checked
  at validation time; the per-delivery `requires_initial_line_of_sight`
  opt-in is superseded. See the review's §2 delivered entry.
  (2) Aerial execution gating (`GROUNDED_ONLY` authored on every strike
  since the initial import) rejects mid-air presses on a timing-dependent
  grounded flag, and the rejection presentation still plays windup + forced
  end segment — reads as "swing happened, then denied". The restriction
  itself is disputed by the owner.
  (3) Victim-side fairness: with 66–100 ms render delay plus
  present-time hit validation and no lag compensation, an approaching
  enemy visibly "hits from beyond rendered reach" (observed live with a
  chasing NPC; amplified under shaping but present at real latencies).
  **Update (2026-07-03, S3 delivered — design review §1):** every NPC
  attack now telegraphs: an authored windup (350–600 ms per kobold
  template, owner-signed) separates the CAST the victim's screen renders
  from damage resolution, with present-time re-validation at impact —
  out of authored reach at impact whiffs silently, hard CC or death
  mid-windup cancels the swing, and parry/block are now judged at impact
  time (after a visible windup) rather than at cast. This masks the
  render-delay artifact rather than rewinding it — lag compensation
  remains S8, defense-grace widening remains §1 item 2 — and the owner
  rescoped player attacks (auto-attacks included) out of the slice.
  Evidence tooling: `ops/npc-telegraph-separation.py` (per-swing
  CAST→damage separation from the live combat-event window). Verified live
  2026-07-04: 462.9–479.2 ms measured over 11 warrior swings (authored
  450 ms) and the owner confirmed the on-screen checklist at baseline and
  under downstream-only shaping.
  (4) Auto-attacks bypass the predicted contact-cue system entirely
  (cues hook predicted action-bar melee presses only).
  (5) Remote-presentation instrumentation cannot distinguish
  idle-target row silence from late delivery (confounds the F4 A/B; see
  above).
  (6) Fixed, endpoint-kind-keyed input lead (no RTT adaptation) — already
  recorded above.
- **F1 — implemented (all four steps).** `PredictedActionLedger` +
  `LocalCombatState.PredictActionStart` / `RollbackPrediction` /
  `ReleasePredictedPrimaryResource`; melee and spell press paths route their
  GCD/cooldown/resource predictions through the ledger, and
  `Rejected`/`StaleToken` results roll everything back (value-guarded so
  authoritative rows or later legitimate predictions are never clobbered).
  Editor tests: `Assets/Arena/Tests/Editor/PredictionRollbackLedgerTests.cs`
  (run via Unity Test Runner). Step 4 (denial cue) is built on the static
  `LocalCombatState.PredictionRejected` hook, which (2026-07-02, netcode
  audit R2) carries the server's machine-readable denial reason —
  `PredictionRejected(actionKind, ActionRejectReason)` fed from the new
  `reject_reason` field on `PredictedActionResult` (cooldown/GCD/resource/
  target/range/facing/LOS/etc.); rollback traces log it, and
  `NetcodeDebugOverlay` shows `lastReject=family:reason`. The HUD surface
  (2026-07-02): `Assets/Arena/Runtime/UI/ActionDenialToastHud.cs` — a small
  uGUI toast just above the action bar that renders `ActionDenialText.For`
  (every `ActionRejectReason` variant mapped to short honest text, zero
  client-side validation), single-slot with a 0.25 s rearm so a mashed
  rejected button never stacks toasts, auto-dismissed at 1.4 s with a fade
  tail; no sound, no shake. Bookkeeping is the pure, time-injected
  `ActionDenialToastModel`. Editor tests:
  `Assets/Arena/Tests/Editor/ConnectionFeedbackHudTests.cs` (full variant
  coverage of the reason→text map; rate-limit/expiry semantics). Still to
  do by hand: the Profile A rejection check in the tabled note above.
  **Update (2026-07-03, S2 — netcode design review §3):** the denial cue
  grew from toast-only to the full rejection presentation.
  `PredictionRejected` now carries `(actionKind, pressedActionId, reason)` —
  `pressedActionId` is the id the bar slot shows (combo follow-ups resolve
  to the follow-up strike id; the ledger records the pressed opener via
  `PredictedActionLedger.PressedActionId`) — and `HUDController` flashes
  every visible matching slot (ability grid, spellbook row, discipline
  bar; 0.45 s red fade over the icon, presentation-only). A `Rejected`
  result also cuts the predicted animation itself — melee (plain and
  phased), gap-close windups (no forced end segment on reject anymore;
  the end request now serves only the 5 s no-answer timeout), and instant
  spells (full-body and moving-cast overlay layers) — through the existing
  preemption/empty-state primitives, routed
  `CombatStatusReactionController.TriggerPredictionRejected` →
  `PlayerAnimator` coordination, identity-gated by the pure
  `CombatActionPlaybackController.ShouldCutRejectedActionPresentation` so
  a stale rejection never eats a later press's playback; `StaleToken`
  never cuts (a newer press owns the presentation). Cast-time holds were
  already cut by the spell presentation state machine (verified,
  unchanged). Client-only, zero schema change, both csproj builds green;
  editor tests deferred until the S2 contract stabilizes (churn ruling).
  Still to do by hand (downstream-only shaping per
  `docs/latency-testing.md`): mid-air gap-close press → AerialMismatch
  cuts the windup (no end segment) + slot flash; mid-air plain-strike
  press → same cut on the non-phased path; spell LOS rejection cuts the
  cast animation both stationary (full-body layer) and strafing
  (upper-body overlay); grounded accepted presses still play windup →
  dash → end segment normally.
- **F2 sub-slice (a) — implemented.** `NetcodeDebugOverlay` now shows remote
  hard-snap count, interp/extrap sample ratio, last/max remote position error
  (aggregated over remote players), predicted-action results by kind, and
  per-table row-receive rates. Server `MOVE_FALLBACK` count is in the
  `[TICK_PROFILE_SCAN]` window line — `ARENA_PROFILE_TICKS` is compile-time
  baked; see `docs/tick-baseline-recipe.md`.
- **F2 sub-slice (b) — implemented.** No-op `ping_clock` reducer
  (`server/src/ping.rs`, schema change — bindings regenerated) + a ~2 s
  sampler in `NetworkManager` that echoes its send time through the reducer
  arg and feeds `ArenaServerClock.RecordReducerSampleMicros` from the reducer
  event's server timestamp — activating the dormant precise midpoint
  estimator (RTT rejection, low-RTT banding, snap corroboration) and
  populating `LastRoundTripMs`. One estimator fix uncovered by wiring it: the
  sample ring now stores only precise samples — it is read exclusively for
  precise-sample statistics, and ~30 Hz observed-row timestamps were evicting
  the ~0.5 Hz pings, which would have permanently defeated the ≥2-sample snap
  corroboration. Overlay gains RTT last/p50/p95 + clock offset lines
  (precise vs observed-only tagged). Editor tests:
  `Assets/Arena/Tests/Editor/ArenaServerClockTests.cs` — corroborated precise
  samples override the monotonic-max estimate downward (through an
  observed-row flood), RTT > 1000 ms rejected, percentile stats. Gameplay
  reads nothing from RTT.
- **F2 sub-slice (c) — implemented.** `docs/latency-testing.md`: macOS
  `dnctl`+`pfctl` profiles (~100 ms/+30 ms jitter/1 % loss and
  ~200 ms/+60 ms/3 %) scoped to local port 3000, with setup/verify/teardown
  and what to expect in the overlay. Plus the optional dev-only
  `Arena.Debugging.NetworkCallbackDelay` (default off,
  `ARENA_CALLBACK_DELAY_MS`): FIFO deferral of binder-routed row callbacks by
  a configurable ms — presentation-side only; caveats in the doc.
- **F2 contract item 4 — implemented.**
  `Assets/Arena/Runtime/UI/ConnectionStatusHud.cs`: an always-on
  connection-quality dot (bottom-right) plus a disconnect banner whose
  Reconnect button promotes the environment overlay's existing
  `NetworkManager.ReconnectToSelectedEnvironment()` to production UI.
  Driven only by data the client already collects — `ArenaServerClock`
  precise-RTT p50/p95 (`TryGetRttStats`) and row-receipt staleness derived
  by watching `NetcodeReceiveCounters.TotalRows` stop changing (the
  counters expose no per-table timestamps, so the indicator derives its
  own; no new sampling, no new network traffic, no schema change).
  Disconnect detection polls `NetworkManager.IsConnected` — the state the
  existing `OnConnect`/`OnDisconnect` DbConnection callbacks maintain —
  shown only after a session was actually established, so startup isn't a
  false banner. Classification is the pure
  `ConnectionQualityModel.Classify` (Good/Degraded/Bad), thresholds
  calibrated to `docs/latency-testing.md`: local dev → Good, Profile A
  (~100 ms) → Degraded, Profile B (~200 ms) → Bad; row silence ≥1.5 s /
  ≥4 s escalates even without RTT stats. Detailed numbers stay in
  `NetcodeDebugOverlay`; gameplay reads nothing from the indicator; no new
  keybinds. Editor tests:
  `Assets/Arena/Tests/Editor/ConnectionFeedbackHudTests.cs`
  (classification boundaries incl. Profile A/B and staleness precedence).
  Still to do by hand: the banner/dot checks in the tabled note above.
- **F3 — implemented.** The remote-presentation core of
  `ClientSimulationState` (snapshot ring, render-target sampling,
  smoothing/hard-snap, the F2a counters) is extracted into
  `Assets/Arena/Runtime/Simulation/RemotePresentationBuffer.cs`; remote
  players delegate to one instance with unchanged constants (66 ms delay,
  66 ms extrapolation cap, 2.0 m / 60° snap, k=18 smoother) and unchanged
  counter semantics, so the overlay reads exactly what it read before.
  `NpcEntity.ApplyPhysics` now pushes zero-velocity snapshots into a per-NPC
  buffer instead of teleporting the transform (NPC velocity is not
  replicated, so capped extrapolation degrades to position-hold), and
  `EntityRegistry.Update` ticks NPC presentation each frame — applying the
  render pose and feeding locomotion speed from the *rendered* delta (skipped
  on hard-snap frames; idle NPCs now decay to a stop instead of holding the
  last row-derived speed). Overlay: the Remote Presentation section gains an
  NPC aggregate (hard snaps, interp/extrap ratio, last/max position error)
  beside the player aggregate. Editor tests:
  `Assets/Arena/Tests/Editor/RemotePresentationBufferTests.cs` (interpolation
  midpoint, velocity-extrapolation cap, hard-snap threshold, sub-threshold
  smoothing, NPC position-hold) — pure math via `PlayerSnapshot`'s new
  explicit-receivedTime constructor. Non-goals held: no NPC velocity
  replication, no navigation prediction, no `NpcPhysics` cadence change;
  server-time keying is F4 and lands inside `RemotePresentationBuffer`.
- **F4 — implemented (fixed 100 ms delay; adaptive delay deliberately not
  started).** `PlayerSnapshot` gains `ServerTimeMs`: the row's `UpdatedAt`
  quantized to the 33 ms fixed-tick grid
  (`RemotePresentationBuffer.QuantizeServerTimeMicros`) — chosen over a
  per-entity tick→`UpdatedAt` anchor because it needs no held state, stays
  anchored to the server epoch clock (comparable to
  `ArenaServerClock.ServerNowMs`, no drift), and works identically for
  players (`PlayerPhysics.UpdatedAt`) and NPCs (`NpcPhysics.UpdatedAt`,
  which has no tick) while still removing sub-tick write jitter.
  `EntityRegistry.SnapshotFrom` (players) and `NpcEntity.ApplyPhysics`
  (NPCs) both stamp it, so the change lives once in
  `RemotePresentationBuffer`: new `SampleServerTime` keys the ring on
  `ServerTimeMs` and renders at `ArenaServerClock.ServerNowMs − 100 ms`
  (fixed). The pre-F4 arrival-time `Sample` is byte-identical and remains
  the automatic fallback while the clock has no estimate, while any
  buffered snapshot lacks `ServerTimeMs` (e.g. the special-movement end
  seed), or while the runtime A/B toggle
  (`RemotePresentationBuffer.ServerTimeTimelineEnabled`, semicolon while
  the netcode overlay is visible — no function keys per repo standard;
  right/left bracket were taken by NetworkEnvironmentOverlay and
  LineOfSightDebugGuide) is off. Overlay additions: which timeline is
  active per aggregate (players/NPCs), effective delay, and
  buffer-depth-in-ticks lines; the F2a hard-snap/extrap-ratio/pos-error
  counters are the before/after evidence. Editor tests (`RemotePresentationBufferTests.cs`): bursty
  arrival times + uniform server times sample uniform motion on the
  server-time path but not on the arrival path; fallback selection for
  no-clock / missing-`ServerTimeMs` / toggle-off; grid quantization.
  Still to do by hand: the live A/B under `docs/latency-testing.md`
  Profile A (republish the local module first if not done since F2b's
  `ping_clock` schema change) — compare extrap ratio and hard snaps old
  vs new against a moving remote entity. Single-operator: kite a
  Playground-spawned hostile kobold (NPCs have no patrols; hostiles
  chase within ~8 m) and read the overlay's NPC aggregate — players and
  NPCs share `RemotePresentationBuffer`, so NPC evidence exercises the
  same timeline keying; add a second operator strafing a remote player
  for the player aggregate when available.
  Non-goals held: adaptive delay (only after the conditioner A/B proves
  the win), local-player path, special-movement track sampling, send
  rates.
- **F5 — slice 1 implemented (predicted gap-closer startup + no-lag-comp
  stance); slice 2 below.** On a gap-close press the
  client now plays the authored windup immediately as a predicted
  presentation: `MeleeInputHandler` routes the gap-close branch through the
  same `CombatAnimationRequest.PredictedMeleeSkill` path as ordinary
  predicted melee (new optional `drivePhasesFromSpecialMovement` flag), so
  the phased playback starts in special-movement-driven mode at press —
  Start plays, the Loop holds until the authoritative
  `SpecialMovementRuntime` row delete requests the end segment. Movement
  stays fully server-owned: when the row arrives, track sampling takes over
  position/facing exactly as before (`LocalMovementPredictionDriver`
  untouched). The press also now carries a real prediction token and routes
  GCD/cooldown/resource through `LocalCombatState.PredictActionStart`, so a
  `Rejected`/`StaleToken` result composes with F1's `PredictedActionLedger`
  rollback and additionally unwinds the held windup via the new
  `PlayerEntity.RollbackPredictedGapCloseWindup()` (no-op when a live
  authoritative special movement owns the end request); a prediction
  timeout (5 s, no result at all) unwinds it the same way so the loop can
  never hold forever. **Update (2026-07-03, S2 — netcode design review
  §3):** on `Rejected` the windup is now *cut* via
  `PlayerEntity.CutRejectedActionPresentation` (reject = interrupt, never
  completion — no more completed-looking end segment); the end-segment
  unwind above now serves only `StaleToken` and the 5 s timeout.
  Duplicate suppression: the authoritative
  `COMBAT_CAST` replay is consumed by the existing accepted-token
  bookkeeping (`_acceptedPredictedMeleeByActionInstance` / pending-replay
  hold), with a pure substrate backstop —
  `CombatActionPlaybackController.IsDuplicateAuthoritativeSpecialMovementMeleeStart`
  ignores a same-action authoritative special-movement start while the
  predicted windup is active and not yet end-requested (local player only;
  remote flows unchanged). The special-movement phase policy was extracted
  from `PlayerAnimator` into
  `CombatActionPlaybackController.TryResolveSpecialMovementDrivenPhasedTransition`
  (PlayerAnimator now delegates — no new fields/methods/responsibilities on
  it, per repo standard). No-lag-comp stance (item 3) documented in
  `docs/combat-authoring-contract.md` ("Hit Validation Timing (No Lag
  Compensation)"): server-present-time validation is by design; rewind lag
  compensation is speculative redesign, do not implement. Editor tests:
  `Assets/Arena/Tests/Editor/GapClosePredictedWindupTests.cs` — handoff
  math (windup elapsed carries into the loop offset; Start/Loop/End
  transitions incl. release-after-start and short-dash end-during-Start)
  and suppression (authoritative start after predicted start does not
  double-play; post-dash same-action start does play). Client-only, no
  schema change. Still to do by hand: `docs/latency-testing.md` Profile A —
  gap-closer press shows instant windup, dash starts ~RTT later with no
  pose pop; rejected press (out of range / LOS) unwinds windup + cooldown +
  resource together. Non-goals held: predicting dash movement,
  lag-compensated rewind, projectile flight prediction.
- **F5 — slice 2 implemented (predicted contact cues, cosmetic-only,
  flag-gated).** At the authored first hit window of a predicted local
  melee press (`CombatAnimationSet.GetStrikeFirstHitWindowSeconds`, scaled
  reference→played clip like playback's other authored thresholds),
  `PredictedMeleeContactCueController` runs an ADVISORY hit test against
  current rendered positions using the press gate's own range/facing math —
  extracted into `MeleeStrikeGeometry` and composed by both
  `MeleeInputHandler.TryTriggerAction` and the advisory test, so the two
  can never drift. On a pass it plays a light contact layer only: the
  authored `MELEE_IMPACT` cue set dispatched through `CombatVFXDispatcher`
  at rendered positions, plus a ≤50 ms animator hitstop in the new
  `MeleeContactHitstop` component (`PlayerAnimator` untouched, per
  maintenance mode). Damage numbers, health, and target hit reactions stay
  100% authoritative — predict startup, never outcomes
  (`docs/combat-authoring-contract.md`, "Hit Validation Timing").
  Gap-close presses are excluded (their contact moment depends on the
  server-owned dash; slice-1 suppression/rollback paths untouched).
  Gating: compile-time kill switch
  (`PredictedMeleeContactCueController.CompiledIn`) plus a default-ON
  debug flag, runtime-toggleable with quote while the netcode overlay is
  visible (backslash/semicolon/brackets taken; no F-keys per repo
  standard). Duplicate suppression: the authoritative `COMBAT_IMPACT` for
  the same action instance + target + hit index is consumed exactly once
  as a duplicate; `COMBAT_CONTACT`/`COMBAT_BLOCK`/`COMBAT_PARRY` confirm
  the correlation but always play — correlated through the
  `PredictedActionResult` accepted-token → action-instance map, the same
  pattern slice 1 used for the animation start. Instrumentation:
  `PredictedMeleeContactCueLedger` counts false positives (cue fired, no
  matching authoritative contact within 500 ms, or the press was rejected
  after the cue) and the overlay shows
  fired/matched/falsePos/suppressedAuth. Editor tests:
  `Assets/Arena/Tests/Editor/PredictedMeleeContactCueTests.cs` — advisory
  geometry boundaries (range + target radius + minimum range + facing
  arc), correlation (match within window vs timeout vs rejection;
  authoritative-first cancels the scheduled cue), and suppression exactly
  once. Client-only, no schema change. Still to do by hand: the Profile A
  contact-cue count in the tabled note above. Non-goals held: predicting
  damage/reactions/numbers, lag-compensated rewind, projectile flight
  prediction, gap-closer changes.

## Executive Summary

The foundation is stronger than the planning docs suggest. Verified as implemented:

- **Tick-buffered movement protocol.** The "tick vs seq mismatch" that
  `plans/movement-netcode-followup-plan.md` calls the highest-priority problem is
  fixed: the server buffers one command per input tick (`server/src/player_input.rs:13-30`),
  consumes exactly one per server tick (`pop_command_for_tick`,
  `server/src/player_input.rs:64-81`), acks `last_processed_tick`
  (`server/src/game_loop.rs:1389`), and falls back to the latest intent when a tick's
  command is missing (`server/src/game_loop.rs:1345-1367`). The plan doc is stale on
  this point and should be updated.
- **Movement modifiers reach prediction** (plan Phase 2 done): per-tick
  `MovementContextSample` carries `MovementBlocked` / `MoveSpeedMultiplier` into
  replay (`Assets/Arena/Runtime/Input/MovementPrediction.cs:129-160`,
  `LocalMovementPredictionDriver.cs:617-653`).
- **Sim/visual separation** (plan Phase 3 done): `LocalPresentationDriver`
  smooths a presentation root with 60 ms position half-life and 2.0 m hard-snap
  (`Assets/Arena/Runtime/Presentation/LocalPlayerCamera.cs:95-160`), and the camera
  follows the smoothed root. Small corrections do not visibly snap; corrections
  ≥ 0.25 m log a warning (`LocalMovementPredictionDriver.cs:21,374-389`).
- **Remote players are properly interpolated**: 12-snapshot buffer, 66 ms render
  delay, 66 ms max velocity extrapolation, 2.0 m / 60° hard-snap, k=18 smoothing
  (`Assets/Arena/Runtime/Simulation/ClientSimulationState.cs:93-100,397-442,517-564`).
- **Combat input prediction is broad**: melee and spells immediately predict
  animation, GCD, per-spell cooldown, resource spend, and cast bar
  (`MeleeInputHandler.cs:265-287`, `SpellInputHandler.cs:626-665`,
  `LocalCombatState.cs:208-282,302-330,495-503`), with responsive cast-cancel and
  remote melee/spell catch-up (V1, 200 ms clamp) implemented.

The real feel gaps, in priority order:

1. **Server rejection is silent and leaves phantom state behind** — a rejected action
   locks the button on a cooldown the server never started, spins the GCD, and holds
   the resource bar down for up to 1.25 s, with zero player-facing feedback. (F1)
2. **The project is RTT-blind and has no latency test harness** — the precise clock
   path is dormant, no ping, no artificial latency/jitter/loss tooling, and several
   remote-presentation counters that already exist are not surfaced. (F2)
3. **NPCs snap with zero interpolation** — every `NpcPhysics` row teleports the
   transform. (F3)
4. **Remote interpolation is keyed to packet arrival time**, not server time, with a
   fixed 66 ms delay — jitter and websocket batching warp remote motion directly. (F4)
5. **Combat has RTT-shaped dead zones**: gap-closers do nothing on press until the
   server answers; melee/spell hit confirmation (damage numbers, hit reactions,
   impact VFX) always waits a full round trip; there is **no lag compensation**
   (hit validation is at server-present time) and that stance is nowhere documented. (F5)

## What Happens Under Adverse Conditions Today

- **Latency (steady).** Local movement and combat startup stay responsive
  (prediction lead up to 12 ticks ≈ 396 ms of headroom,
  `MovementNetcodeConfig.cs:20-28`; remote sends target an 8-tick input lead,
  `MovementNetcodeConfig.cs:15-16`). What degrades: hit confirmation, interrupts,
  gap-closers, and projectile spawns — all full-RTT. A staggered local player keeps
  "casting" until the server's `ActiveCast` delete arrives
  (`LocalCombatState.cs:637-656` region), then the bar vanishes without explanation.
- **Jitter.** Local prediction absorbs it (tick-buffered commands + fallback intent).
  Remote players wobble: the interpolation buffer indexes on `ReceivedTime`
  (`PlayerSnapshot.cs:34`, `ClientSimulationState.cs:411,545-546`), so bursty
  delivery compresses/stretches perceived motion; gaps > 66 ms hit the extrapolation
  cap, then the k=18 smoother pulls back. Counters for this exist
  (`ClientSimulationState.cs:152-159`) but are only partially surfaced.
- **Packet loss / late input.** Server advances the tick using the last intent and
  logs `[MOVE_FALLBACK]` at debug level (`game_loop.rs:1358-1363`); the client later
  replays with real inputs → correction. There is no counter for fallback frequency,
  so loss is invisible in any overlay. Emergency resync clears the command buffer at
  > 12 pending (`MovementNetDriver.cs:80-93`).
- **Reconnect.** `NetworkManager` resets clock, entity cache, match and combat state
  (`NetworkManager.cs:193-220,374-382,404-406`) but there is **no auto-reconnect and
  no player-facing disconnect UI** — the world freezes and buttons gray out. Manual
  reconnect exists only on the dev-only environment overlay (`]`) (`NetworkEnvironmentOverlay.cs:65`).
- **Module republish.** Client experiences a disconnect (above). Server-side stale
  transient rows on reconnect are covered by the architecture audit (R1); the client
  side additionally shows nothing to the player about why they dropped.

## Prediction Coverage Matrix

| Local action | Predicted immediately | Waits for server |
|---|---|---|
| Run / strafe / jump | position, velocity, yaw, grounded (full replay) | — |
| Root / slow / speed buffs | respected in replay once context row arrives | onset of the CC itself (1 RTT) |
| Gap-closer / dash / special movement | nothing (`MeleeInputHandler.cs:258-263`) | movement + animation + VFX (1 RTT) |
| Knockback / forced movement | nothing | arrives as a correction |
| Melee strike | animation, GCD, cooldown, resource (`MeleeInputHandler.cs:265-287`) | hit result, damage numbers, target reaction (1 RTT+) |
| Instant spell | animation, release VFX, GCD, cooldown, resource (`SpellInputHandler.cs:654-665`) | impact, projectile spawn row (1 RTT) |
| Cast-time spell | cast bar, cast-hold animation, resource, cooldowns (`SpellInputHandler.cs:626-637`) | release, effects; interrupt presentation (1 RTT) |
| Cast cancel | bar suppression immediate (`LocalCombatState.cs:505-529`) | cancel-too-late verdict |
| Projectile flight | not predicted; spawned from `ProjectilePresentationEvent` rows (`CombatProjectileVisualController.cs:102-152`) | everything |
| Being hit / stagger | nothing | reaction + cast interruption (1 RTT) |

---

## 1. Top Multiplayer-Feel Improvements

### F1 — Roll back *all* predicted side effects on rejection, and tell the player

**Classification: combat feedback improvement** (highest-value fix in this audit)

**Repo evidence.**
On `Rejected`/`StaleToken`, the handlers clear only the cast bar and pending-visual
token:

- `LocalCombatState.OnPredictedActionResultInsert` clears `_predictedCastBar` and
  `_currentCastToken` only (`Assets/Arena/Runtime/Simulation/LocalCombatState.cs:559-565`).
- `MeleeInputHandler.OnPredictedActionResultInsert` only does
  `_pendingPredictedMeleeByToken.Remove(tokenKey)` (`MeleeInputHandler.cs:528-531`).
- `SpellInputHandler.OnPredictedActionResultInsert` only does
  `_pendingInstantSpellByToken.Remove(tokenKey)` (`SpellInputHandler.cs:720`).

Meanwhile the press path predicted much more:

- Per-spell cooldown written into `_spellCds` (`LocalCombatState.cs:268-282`) — the
  same dictionary authoritative rows land in. A rejected cast never produces a
  server `SpellCooldown` row, so **nothing ever removes the phantom entry**; the
  action bar shows the full cooldown.
- GCD predicted (`LocalCombatState.cs:208-219`); `ClearPredictedGlobalCooldown` is
  called only from the voluntary self-cancel path (`LocalPlayerMotor.cs:226`) —
  never on rejection.
- Resource reservation held until a 1250 ms timeout
  (`PredictedResourceSpendTimeoutMs`, `LocalCombatState.cs:114,329`) because
  reconciliation only releases it when the server's resource actually drops
  (`LocalCombatState.cs:362-368`) — which never happens for a rejected action.
- No sound, flash, toast, or reason anywhere.

**Player-facing symptom.** Rejections happen precisely in the latency races that
matter: you press a spell in the same instant the server staggers/silences/roots you,
or client/server validation drifts (the commit `115393b9` bug class). The player
experiences: button pressed → swing/cast animation starts → silently fizzles → **the
ability is now unusable for its full cooldown and mana appears spent** → "this game
eats my inputs." This converts an occasional mispredict (acceptable) into a
multi-second penalty plus confusion (not acceptable).

**Better contract.** Every predicted side effect is recorded on the action token, and
`Rejected`/`StaleToken` restores all of it atomically: remove/restore the
`_spellCds` entry (restore the pre-prediction value if one existed), clear predicted
GCD if this token set it, release the predicted resource reservation immediately, and
fire one player-facing denial cue (icon shake / brief red flash + sound). Prediction
stays optimistic; rejection becomes cheap and legible.

**Why it improves feel.** Mispredicts become a ~RTT-long blip instead of a
cooldown-long punishment. Denial feedback closes the "did my input register?" loop,
which is the single strongest subjective marker of responsive netcode.

**Bounded slice for a smaller model.**
1. Add a `PredictedActionLedger` struct captured at press time (per token):
   `{ gcdSetByThisToken, cooldownKind, priorCooldownEntry?, reservedResourceCost }`.
   Store in the existing pending dictionaries in `MeleeInputHandler` /
   `SpellInputHandler` (they already key by token).
2. Add `LocalCombatState.RollbackPrediction(in PredictedActionLedger)` that restores
   `_spellCds`, clears predicted GCD (only if the authoritative GCD row hasn't since
   arrived — compare against the last authoritative `GlobalCooldown` values), and
   zeroes the matching portion of `_predictedPrimaryResourceSpend`.
3. Call it from the three rejection sites above.
4. Add one denial cue hook (a static event the HUD subscribes to; a simple icon
   flash is enough for the slice).
   Client-only; zero schema changes; no binding regen.

**Files/surfaces.** `LocalCombatState.cs`, `MeleeInputHandler.cs`,
`SpellInputHandler.cs`, one small HUD/action-bar surface for the cue.

**Tests / runtime scenarios.** Editor tests: predict → reject → assert `_spellCds`
has no phantom entry, GCD inactive, effective resource equals server value; predict →
accept → assert ledger discarded without touching authoritative rows; reject arriving
*after* an authoritative cooldown row for the same spell (later legitimate cast) must
not clear the authoritative entry. Runtime: force rejections (cast at 0 resource with
the client pre-check bypassed; cast during a server-side stagger) and verify the
button is immediately reusable and the cue fires.

**Instrumentation.** Count `PredictedActionResult` rows by result kind per session;
show `accepted/rejected/canceled` in `NetcodeDebugOverlay`. A rising rejected count
is the early-warning signal for validation drift.

**Risks / non-goals.** Do not let rollback touch authoritative rows; only
prediction-sourced state. Do not build a second deny/sync channel — this composes
with the architecture audit's R2 (reason codes on `PredictedActionResult`), which
supplies the *message*; this slice supplies the *rollback and cue* and ships first
without any schema change.

---

### F2 — Wire real RTT sampling, surface the existing counters, and stand up a latency test harness

**Classification: instrumentation before tuning** (gates F4 and all smoothing work)

**Repo evidence.**
- `ArenaServerClock.RecordReducerSampleMs` (precise midpoint estimator, RTT
  rejection, low-RTT banding, snap corroboration —
  `Assets/Arena/Runtime/Network/ArenaServerClock.cs:46-72,133-173`) is **never called
  in production**; only the one-way monotonic-max estimator runs
  (`ArenaServerClock.cs:75-87,123-130`), which by design never decreases during a
  session. The design doc flags this as the dormant path
  (`docs/combat-animation-latency-aware-remote-playback-plan-2026-05-05.md`).
- `LastRoundTripMs` exists (`ArenaServerClock.cs:31`) but is never populated.
- No artificial latency/jitter/loss tooling anywhere in the client, and SpacetimeDB
  SDK 2.0.4 exposes none.
- Counters that exist but are not (fully) shown: remote hard-snap count,
  interpolation vs extrapolation sample counts, last/max remote position error
  (`ClientSimulationState.cs:152-159`); server `[MOVE_FALLBACK]` events have no
  counter at all (`game_loop.rs:1358-1363`).
- No player-facing connection indicator; no disconnect banner
  (`NetworkManager.cs:374-382` just resets state; UI polls `IsConnected`).

**Player-facing symptom / risk.** "It feels laggy" is currently undiagnosable: you
cannot see RTT, cannot see how often remote presentation is extrapolating or
snapping, cannot see input-loss rate, and cannot reproduce any of it locally. Every
smoothing/tuning change made in this state is guesswork — the exact failure mode the
movement plan warns about ("tuning more smoothing knobs before fixing the contract").

**Better contract.**
1. A `ping_clock` reducer (no-op server-side, returns via normal reducer result);
   client records send/receive around it every ~2 s and feeds
   `RecordReducerSampleMs`, activating the already-written low-RTT/snap logic.
2. `NetcodeDebugOverlay` gains: RTT (last/p50/p95), clock offset, remote
   extrapolation ratio, remote hard-snap count, predicted-action results by kind,
   and a server-fallback-intent counter (server: count `MOVE_FALLBACK` per profile
   window in the existing `ARENA_PROFILE_TICKS` line — log-only).
3. A documented latency recipe: macOS Network Link Conditioner / `dnctl`+`pfctl`
   profiles (100 ms/30 ms jitter/1% loss, 200 ms/60 ms/3%) checked into
   `docs/` with step-by-step usage against the local SpacetimeDB endpoint. Optional
   dev-only client hook: a delay queue wrapper that defers row-callback dispatch by a
   configurable ms (presentation-side latency simulation without touching the SDK).
4. A small always-on connection-quality dot + disconnect banner with a reconnect
   button (promote the environment overlay's reconnect action to production UI).

**Why it improves feel.** Indirectly but decisively: it converts every subsequent
feel complaint into a measurable, reproducible case, and the ping reducer improves
remote combat catch-up accuracy (its clamp math currently rests on the conservative
one-way clock).

**Bounded slice for a smaller model.** Do it in three independent sub-slices:
(a) overlay lines for existing counters (pure client, trivial);
(b) `ping_clock` reducer + client sampler (one reducer added — schema change, so
follow the regen command in `MEMORY`/checklist; server side is ~5 lines);
(c) the latency-recipe doc + optional callback-delay debug utility.

**Files/surfaces.** `ArenaServerClock.cs`, `NetcodeDebugOverlay.cs`,
`NetworkManager.cs` (sampler + banner state), `server/src/lib.rs` (or a small
`ping.rs`), regenerated bindings, one HUD element, new `docs/latency-testing.md`.

**Tests / runtime scenarios.** Clock unit tests already exist conceptually in the
plan doc's list — add: precise samples override monotonic-max estimate downward;
RTT > 1000 ms rejected. Runtime: run the conditioner profiles and confirm RTT/extrap
ratio move as expected; pull the network cable and confirm the banner appears.

**Instrumentation.** This *is* the instrumentation. Non-goal: acting on the numbers
in the same slice.

**Risks / non-goals.** Keep the ping cadence low (unmetered reducer spam is
replication churn — the architecture audit's R3 counters will show it). Do not couple
gameplay behavior to raw RTT. Do not edit generated bindings by hand.

---### F3 — Interpolate NPCs like remote players

**Classification: remote-player presentation improvement**

**Repo evidence.** `NpcEntity.ApplyPhysics` teleports the transform on every row:
`GameObject.transform.SetPositionAndRotation(nextPosition, ...)`
(`Assets/Arena/Runtime/Entity/NpcEntity.cs:116-140`). No buffer, no smoothing, no
render delay; locomotion speed is derived from raw row-to-row deltas. Remote
*players* get the full `ClientSimulationState` stack
(`ClientSimulationState.cs:397-442`).

**Player-facing symptom.** NPCs stutter-step at row-arrival cadence and freeze
between updates; under jitter or websocket batching they visibly teleport in small
hops. In melee range — where the camera is close and the player is orbiting the
target — this is the most visible motion artifact in the game, and it also makes
NPC-derived locomotion animation jumpy.

**Better contract.** NPCs use the same snapshot-buffer presentation as remote
players: push `NpcPhysics` rows into a per-NPC buffer, render at `now − delay`,
smooth toward the target, hard-snap on large error. Velocity is not replicated for
NPCs, so extrapolation is position-hold (or last-delta) rather than velocity-based.

**Why it improves feel.** Smooth, continuous enemy motion is a prerequisite for
melee combat feel: players time swings against target movement, and stuttering
targets make range/timing judgments feel random.

**Bounded slice for a smaller model.**
1. Extract the remote-presentation core of `ClientSimulationState` (snapshot ring,
   `SampleRemoteRenderTarget`, smoothing/snap in `Tick`) into a small reusable
   `RemotePresentationBuffer` class, keeping `ClientSimulationState` delegating to
   it (no behavior change for players).
2. Give `NpcEntity` one instance; `ApplyPhysics` pushes snapshots instead of setting
   the transform; a per-frame tick (from the existing NPC update path) applies the
   render pose and feeds locomotion speed from the *rendered* delta.
3. Reuse the same constants initially (66 ms delay, 2.0 m snap); expose the same
   debug counters.

**Files/surfaces.** `ClientSimulationState.cs` (extraction), new
`Assets/Arena/Runtime/Simulation/RemotePresentationBuffer.cs`, `NpcEntity.cs`,
`EntityRegistry.cs` (NPC row routing), optionally `NetcodeDebugOverlay.cs`.

**Tests / runtime scenarios.** Editor test on the extracted buffer (pure math:
interpolation between two snapshots, extrapolation cap, snap threshold — reuse
whatever covers players today, else add now). Runtime: walk an NPC patrol route with
the conditioner at 100 ms/30 ms jitter; verify no per-row hops; verify hit-reaction
and death animations still align with position (they key off state rows, not
transforms).

**Instrumentation.** Same counters as players (hard snaps, extrapolation ratio) per
NPC aggregate in the overlay.

**Risks / non-goals.** Server-side NPC hit validation uses server positions, so the
added ~66 ms render delay does not change gameplay — but melee range *pre-checks*
against NPC transforms (client advisory) will now see slightly older positions;
acceptable and consistent with remote players. Non-goals: NPC velocity replication,
navigation prediction, changing `NpcPhysics` cadence.

---

### F4 — Key remote interpolation to server time with an adaptive delay

**Classification: reconciliation/smoothing improvement — gated on F2's measurements**

**Repo evidence.** The remote timeline is client-arrival-based:
`renderTime = Time.realtimeSinceStartup - RemoteInterpolationDelaySeconds`
(`ClientSimulationState.cs:411`) sampled against `PlayerSnapshot.ReceivedTime`
(`PlayerSnapshot.cs:34`, comparisons at `ClientSimulationState.cs:541-546`), with a
fixed 66 ms delay (`ClientSimulationState.cs:96`). The rows already carry what a
server-time buffer needs — `LastProcessedTick` (33 ms grid) and `UpdatedAt` — and
`ArenaServerClock` already estimates server-now, but neither is used for
presentation timing. Under a delivery gap > 66 ms, sampling falls into
velocity-extrapolation capped at 66 ms (`ClientSimulationState.cs:554-563`), then
converges back via the k=18 smoother — i.e., jitter becomes visible speed
modulation instead of added delay.

**Player-facing symptom.** Remote players subtly speed up/slow down ("swimmy"
motion) whenever delivery cadence varies — which with SpacetimeDB transaction-batch
delivery is the normal case, not the exception. Under real WAN jitter, alternating
extrapolate/correct cycles read as micro-rubber-banding on other players, exactly
what strafing opponents in melee makes most visible.

**Better contract.** Buffer snapshots on the server-tick timeline
(`LastProcessedTick × 33 ms`, or `UpdatedAt`), render at
`ArenaServerClock.ServerNowMs − adaptiveDelay`, where `adaptiveDelay` tracks a
high percentile (e.g., p95 + half a tick) of observed arrival lateness within a
sliding window, bounded to [66 ms, 200 ms]. Jitter then costs a little more fixed
delay instead of visible motion warping.

**Why it improves feel.** Constant small delay is imperceptible; time-warped motion
is not. This is the standard snapshot-interpolation contract (source-style) and the
codebase is one field away from it — the data is already replicated.

**Bounded slice for a smaller model.**
1. In `PlayerSnapshot`, add `ServerTimeMs` (from `LastProcessedTick × 33` — prefer the
   tick: it's jitter-free) alongside `ReceivedTime`.
2. In `SampleRemoteRenderTarget`, switch comparisons to `ServerTimeMs` against a
   render time of `ArenaServerClock.ServerNowMs − delayMs`; keep the arrival-time
   path as fallback while `!ArenaServerClock.HasEstimate`.
3. Start with **fixed** 100 ms delay behind a debug toggle next to the old path;
   make the delay adaptive only after F2's overlay proves the win (extrapolation
   ratio ↓, hard snaps ↓).
   Client-only; no schema change.

**Files/surfaces.** `PlayerSnapshot.cs`, `ClientSimulationState.cs`,
`EntityRegistry.cs` (snapshot construction), `NetcodeDebugOverlay.cs` (A/B toggle +
counters). If F3 landed, the change lives once in `RemotePresentationBuffer`.

**Tests / runtime scenarios.** Editor tests: feed synthetic snapshots with bursty
arrival times but uniform server ticks → server-time path produces uniform sampled
motion, arrival-time path does not. Runtime A/B under conditioner profiles: compare
extrapolation ratio and hard-snap counts old vs new; verify remote melee catch-up
still aligns (it shares `ArenaServerClock`).

**Instrumentation.** Requires F2 first: extrapolation ratio, hard snaps, and RTT in
the overlay are the before/after evidence. Add "buffer depth in ticks" as a line.

**Risks / non-goals.** The monotonic-max clock can be off by the one-way jitter
floor — acceptable for presentation, but do this after F2's precise samples land to
avoid tuning against a biased clock. Do not change the local-player path, special
movement track sampling (`ClientSimulationState.cs:401-409`), or send rates.
Non-goal: adaptive *send*-side rates.

---

### F5 — Close the combat dead zones: predicted gap-closer startup and predicted contact cues; document the no-lag-comp stance

**Classification: combat feedback improvement** (sub-part explicitly speculative)

**Repo evidence.**
- Gap-closers deliberately skip all local presentation:
  `"melee gap close awaiting authoritative movement+animation"`
  (`MeleeInputHandler.cs:258-263`); special movement is server-sampled only
  (`LocalMovementPredictionDriver.cs:254-305`).
- Attacker hit feedback is fully authoritative: damage numbers from
  `CombatEffectEvent` (`FloatingCombatText.cs:43-66`), impact VFX/hit reactions from
  `CombatEvent` inserts (`CombatVFXDispatcher.cs:331-377`); nothing is predicted at
  the authored hit-window moment.
- No lag compensation: melee impact resolution uses present-time snapshots
  (`server/src/melee.rs:3874` onward, range check ~`4063-4072`), projectiles collide
  at present-time positions (`server/src/combat/projectiles.rs:81-299`). No doc
  states this is intentional.

**Player-facing symptom.** (a) Under 100 ms+ RTT a gap-closer button is dead for a
full round trip — the single most noticeable input-response failure in the current
design, since every other action predicts *something*. (b) Melee feels floaty: the
blade passes through the target and the thud/number arrives ~RTT+impact-delay later.
(c) Because hits validate at present time, higher-latency attackers whiff on moving
targets more than their screen suggests — an implicit design decision nobody wrote
down, so future contributors may "fix" it accidentally.

**Better contract.**
1. *Gap-closer startup prediction (implement):* on press, immediately play the
   authored windup animation and startup VFX as a predicted presentation (same
   pattern as predicted melee), while movement remains fully server-owned; when the
   authoritative `SpecialMovementRuntime` row arrives, the already-playing windup
   hands off to track sampling (suppress the duplicate authoritative start, as
   predicted melee already does via its accepted-token replay suppression).
2. *Predicted contact cues (implement, cosmetic-only):* at the authored first hit
   window of a predicted local melee, if the client-side advisory hit test passes
   against current rendered target positions, play a light contact layer — spark
   flash, weapon sound, small hitstop ≤ 50 ms. Damage numbers, health, and target
   reactions remain 100 % authoritative. On a server whiff the player sees a light
   contact cue but no number — tunable down if it reads as a lie.
3. *No-lag-comp stance (document now):* add the explicit statement to
   `docs/combat-authoring-contract.md` (or a new netcode contract doc): hit
   validation is server-present-time by design; rewind-based lag compensation is
   **speculative redesign, do not implement yet** — it requires historical position
   buffers server-side and a fairness decision, and it invalidates the
   target's-eye-view ("shot behind the wall" class tradeoffs).

**Why it improves feel.** (1) removes the only remaining dead button; (2) restores
the contact-moment feedback loop that makes melee read as connected — the
authoritative confirmation then arrives as reinforcement (number) rather than as the
only signal; (3) prevents accidental fairness regressions.

**Bounded slice for a smaller model.** Ship (1) alone first: it reuses the existing
predicted-melee request path (`CombatAnimationRequest.PredictedMeleeSkill`) and
replay-suppression bookkeeping; the only new logic is "predicted presentation without
predicted movement" plus handoff-on-row-arrival. CLAUDE.md constraint applies:
the playback change goes through `CombatAnimationSet` data / the shared playback
substrate — do not add new machinery to `PlayerAnimator`. (2) is a second slice
gated behind a debug flag and a kill-switch constant. (3) is a doc edit.

**Files/surfaces.** `MeleeInputHandler.cs` (gap-close branch),
`CombatAnimationRequest`/playback substrate, `CombatVFXDispatcher.cs` (predicted
contact cue + suppression of the duplicate authoritative cue via the existing
accepted-token maps), `docs/combat-authoring-contract.md`.

**Tests / runtime scenarios.** Editor: handoff math (predicted windup elapsed →
track sampling start offset); suppression: authoritative start after predicted start
does not double-play. Runtime under 150 ms conditioner: press gap-closer — windup is
instant, dash starts ~RTT later without a pose pop; melee vs strafing target — count
contact-cue-but-no-damage occurrences per 100 swings (tune or gate on that number).

**Instrumentation.** Log predicted-contact false-positive rate (cue fired, no
matching authoritative impact within 500 ms — the accepted-token map gives the
correlation); surface count in overlay.

**Risks / non-goals.** Contact cues can misreport during target death/immunity races
— keep them subtle and instantly killable via flag. Rejection of the gap-close must
roll back the windup (compose with F1's ledger). Non-goals: predicting dash
*movement*, lag-compensated rewind, projectile client-side flight prediction (revisit
after F2 data; classified speculative).

---

## 2. Safest First Slice

**F1, step 1-3 (rollback ledger), plus F2 sub-slice (a) (overlay lines for existing
counters).** Both are client-only, zero schema change, zero binding regen, no
gameplay-authority changes, and each is independently verifiable in the editor:

1. `PredictedActionLedger` + `RollbackPrediction` + three call sites
   (`LocalCombatState.cs`, `MeleeInputHandler.cs`, `SpellInputHandler.cs`), with the
   editor tests listed under F1. Fixes the worst live feel bug (phantom cooldown /
   held resource after rejection).
2. `NetcodeDebugOverlay` lines for: remote hard-snap count, interp/extrap sample
   ratio, last/max remote position error (already computed in
   `ClientSimulationState.cs:152-159`), and predicted-action results by kind (needed
   to observe F1 working).

Defer the denial *cue* (F1 step 4) to a follow-up if HUD surface work is considered
risky; the rollback alone is already the correctness win.

## 3. Latency/Jitter Test Plan

Environment: local SpacetimeDB (`ws://localhost:3000`) with macOS Network Link
Conditioner (or `dnctl`/`pfctl`) shaping the client. Two clients (editor + dev
build), one NPC-populated scene. Toggle overlays: `\` netcode, `]` environment.

Profiles (run every scenario at each):

| Profile | RTT | Jitter | Loss |
|---|---|---|---|
| Baseline | ~0 | 0 | 0 |
| Regional | 60 ms | 10 ms | 0 % |
| Cross-region | 120 ms | 30 ms | 1 % |
| Bad WiFi | 200 ms | 60 ms | 3 % |
| Spike | 120 ms steady + 5 s bursts of +300 ms | — | — |

Scenarios and pass criteria:

1. **Straight-line + strafe run (local).** No alternating smooth/vibrate windows;
   correction error stays < 0.25 m (no `Large correction` warnings) outside of
   loss bursts; resync count 0 at ≤ 120 ms.
2. **Combat movement (local).** Get rooted/slowed while running: correction spike at
   CC onset only (one correction ≈ RTT × speed, then stable — context rows are in
   replay, so no churn afterward).
3. **Remote observation.** Observer watches runner orbiting an NPC: no hard snaps at
   ≤ 120 ms (overlay counter), extrapolation ratio < 10 % at Regional, no visible
   speed pulsing (F4's before/after metric).
4. **Melee exchange.** Attacker swings at strafing target: measure press→animation
   (must be ~0), press→damage-number (expect ≈ RTT + authored impact delay — record
   as baseline), observer sees windup catch-up (≤ 200 ms clamp) with impact aligned.
5. **Rejection race.** Target staggers the caster exactly during cast press
   (scripted or practiced): before F1 — observe phantom cooldown + held resource;
   after F1 — button reusable immediately, cue fires, overlay rejected-count
   increments.
6. **Cast + interrupt.** Time from server-side interrupt to local cast-bar removal ≈
   one-way latency; bar never completes visually after an interrupt.
7. **Gap-closer.** Press→any visible response: currently ≈ RTT (record); after F5(1)
   ≈ 0 with dash starting ≈ RTT later, no pose pop at handoff.
8. **Loss burst.** At 3 % loss: `MOVE_FALLBACK` counter (F2) rises, movement stays
   playable, corrections stay < 2 m (no presentation hard snaps).
9. **Reconnect / republish.** Kill the server process mid-combat; republish via
   `ops/` script; client should show the disconnect state (post-F2 banner), reconnect
   cleanly, and exhibit no stale cast bar / cooldown carryover (architecture audit
   R1 verification doubles here).
10. **Spike profile.** During the +300 ms bursts: remote players add delay, not
    warp (post-F4); local prediction lead grows toward 12 ticks then recovers
    without emergency resync more than once per burst.

Record per run: RTT p50/p95, correction error last/max, replay depth max, resync
count, remote extrap ratio, hard snaps, rejected actions, `MOVE_FALLBACK` count.

## 4. Prediction/Reconciliation Review Checklist

For any PR touching prediction, presentation, or combat feedback:

- [ ] Every new predicted side effect (cooldown, GCD, resource, bar, animation, VFX)
      is recorded on the action token's ledger and restored on
      `Rejected`/`StaleToken`/`CancelTooLate` as appropriate — no phantom state can
      outlive a rejection.
- [ ] Prediction never *denies* an action the server would allow (advisory
      pre-checks only) and never mutates authoritative caches, only
      prediction-layer state alongside them (architecture audit R2 rule).
- [ ] Predicted presentation and its authoritative duplicate are correlated by
      token/action-instance id, and the duplicate is suppressed — never played twice,
      never re-anchored (the anchoring rule in the latency-aware playback plan).
- [ ] Any new server row consumed by local replay is versioned/tick-stamped and
      reachable during rewind (`GetMovementContextForTick` pattern); replay fallback
      to defaults is counted, not silent.
- [ ] Corrections route through a smoothing layer (`LocalPresentationDriver` /
      remote buffer) with an explicit hard-snap threshold; no code writes corrected
      positions directly to a visible transform.
- [ ] Remote/NPC presentation goes through the shared snapshot buffer; no direct
      `transform.SetPositionAndRotation` from a row callback.
- [ ] Presentation timing uses `ArenaServerClock` (server timeline), not
      `Time.realtimeSinceStartup` arrival times, wherever a server timestamp exists.
- [ ] Timing-sensitive constants (interp delay, catch-up clamps, snap thresholds,
      prediction TTLs) are named constants with a comment stating the tick/RTT
      assumption they encode.
- [ ] The change was exercised under at least the Cross-region conditioner profile,
      and the PR cites overlay numbers (correction error, extrap ratio, hard snaps,
      rejected count) before/after.
- [ ] Hit validation timing semantics unchanged (server-present-time; no accidental
      rewind or client-authoritative hit claims); if intentionally changed, the
      contract doc changes in the same PR.
- [ ] CLAUDE.md ownership holds: no new `PlayerAnimator` responsibilities;
      hit-reaction presentation in `CombatStatusReactionController`; combat dispatch
      through action-bar resolution.
- [ ] New per-identity transient tables added to the unified teardown (architecture
      audit R1); schema changes regenerate bindings with the canonical command in
      the same commit.

## Classification Summary

| Recommendation | Classification |
|---|---|
| F1 rejection rollback + denial cue | combat feedback improvement |
| F2 RTT/ping, counters, latency harness, disconnect banner | instrumentation before tuning |
| F3 NPC interpolation | remote-player presentation improvement |
| F4 server-time-keyed adaptive remote interpolation | reconciliation/smoothing improvement (gated on F2) |
| F5(1) predicted gap-closer startup | responsiveness improvement |
| F5(2) predicted contact cues | combat feedback improvement (flag-gated) |
| F5(3) document no-lag-comp stance | combat feedback improvement (doc-only) |
| Rewind-based lag compensation | speculative redesign, do not implement yet |
| Client-side projectile flight prediction | speculative redesign, do not implement yet |
| Local prediction of dash/special movement trajectories | speculative redesign, do not implement yet |
