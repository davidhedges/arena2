# Combat-build probe recovery evidence

Date: 2026-08-29

Result: **PASS with one explicit environment-verifier blocker and one honest
route skip.** Nine local acceptance probes deleted by `3ef2257f` were audited,
ported to the frozen combat-build contract, and rerun. This is a post-cutover
correction; it does not restore any removed production progression authority.

## Recovery decision

The scripts were not restored merely because an older commit contained them.
Each retained probe still covers a live mechanic that is not equivalently
exercised by the normal server unit suite:

| Probe | Current mechanic retained | Port decision |
|---|---|---|
| `ops/cauterize-probe.py` | Cauterize cleanse, cost, burn, and 1-HP floor | Retain; use exact canonical frozen assignments for attacker and victim. |
| `ops/knockback-probe.py` | NPC/player displacement, heavy immunity, equipment resistance, dodge, and Shockwave | Retain; remove only the obsolete arena-edge assertion after `615d3110` intentionally removed the implicit deck boundary. |
| `ops/lightning-reflexes-probe.py` | Lightning Reflexes and Blinding Light behavior | Retain; assign their current Daggers/Staff disciplines and switch explicitly. |
| `ops/npc-support-decision-probe.py` | Lich support and Wizard interrupt decisions | Retain; use the current canonical visual and Icicle action identifier. |
| `ops/s4-los-probe.py` | Press-time melee/gap-close LOS rejection | Retain; use current Staff Thrust control and Two-Handed Sword Charge. The authored far-side route remains an explicit SKIP because route collision prevents reaching the fixture; it is not reported as a pass. |
| `ops/s8-lag-comp-probe.py` | Attacker-view rewind and defense grace | Retain; use a current Staff control strike, canonical Charge discipline, and shipped default-ON expectation. |
| `ops/s9-auto-rewind-probe.py` | Standing-signal auto-reach rewind and defense-late telemetry | Retain; derive auto range from the active discipline and split the timing-sensitive OFF, ON, and rider legs into independently runnable acceptance legs. |
| `ops/s10-sweep-rewind-probe.py` | Per-victim area-sweep rewind | Retain; use the current Ice Spikes assignment and interpret sparse history sample age correctly instead of imposing an invalid 280-ms upper bound. |
| `ops/shadow-kit-probe.py` | Mortality/Shadow kit behavior | Retain; assign current Staff/Mortality abilities through the frozen build. |

No deleted reducer was resurrected. In particular, the recovered scripts do
not call `learn_spell` or either removed match-side action-bar writer.

## Canonical probe admission

`ops/combat_build_probe_support.py` reads the checked-in progression catalog,
derives discipline ownership, Staff schools, canonical weapon configurations,
and canonical slot IDs, then submits a complete `CombatBuildDraft`.

The corresponding reducer,
`configure_local_direct_probe_combat_build`, is compiled only with the existing
`projectile_load_harness` feature. It rejects non-local-direct deployments,
unconnected callers, and reservation-backed players; applies the production
`CombatBuildCatalog::validate_draft` contract; and replaces only that caller's
ordinary frozen combat-build rows. It then materializes weapons and activates
the validated starting discipline through the existing runtime projection.
This makes the probes representative of current authorization without adding
a production build writer or a second validation policy.

## Live results

All runs used fresh throwaway local databases published from the harness build:

- **Cauterize:** PASS for bleed/slow setup, cleanse, burn cost, and the 1-HP
  floor.
- **Knockback:** PASS for NPC displacement, heavy immunity, player shove,
  combat entry, equipment resistance, dodge preemption, Shockwave, and
  teardown. The removed arena-boundary check is documented above.
- **Lightning Reflexes:** PASS across the Blinding Light and Lightning
  Reflexes legs after explicit discipline switching.
- **NPC support:** PASS for Lich Bone Ward/Mend and Wizard Frostbite/Ice Lock
  interrupt behavior.
- **Shadow kit:** PASS with the canonical Mortality/Staff build.
- **S4 LOS:** PASS for the clear control, auto-attacks, and near-wall LOS
  rejection; the unreachable far-side route is explicitly SKIPPED.
- **S8:** PASS for default/config sanity, 16 history samples, 4/4 OFF rejects,
  4/4 ON accepts, rewind barrier, and the 240-second defense leg (38 parries,
  469 impacts, live 9,850-ms grace; 16 parried and 5 landed audit events).
- **S9:** focused OFF PASS (176 audits, a disabled flip, and no
  standing-stamped dispatch); focused ON PASS (five enabled flips and 55 each
  of standing `melee_gate` and `impact_recheck`); rider PASS (27 in-band
  `DEFENSE_LATE` lines from 65 presses and two undefended-hit rows). The
  combined convenience leg remains fixture-timing-sensitive, so the documented
  acceptance recipe runs OFF and ON independently rather than concealing that
  variability.
- **S10:** PASS with 10 disabled and 7 enabled excluding flips, all sourced
  from history, plus present-time degradation. History rows are sampled when
  poses change, so selected-sample age can exceed the configured 250-ms rewind
  cap; the probe now checks the configured cap and history source rather than
  treating sparse sample age as requested rewind distance.

## Static and environment validation

The shared helper has focused unit coverage for mixed-discipline ownership,
Staff-school derivation, selection-kind rejection, and exact frozen-row
observation. All nine scripts compile with Python, the harness-feature Rust
suite passes 791 tests, and the Hub suite passes 25 tests. Regenerated C#
bindings expose the feature build's probe reducer; `Assembly-CSharp`,
`Assembly-CSharp-Editor`, and `Arena.EditModeTests` all compile with zero
errors. The canonical data-preserving local setup is ready, and its eight Hub
combat-build/armor tables compare unchanged with the pre-change snapshot.

A one-sample disposable-match benchmark reached initial state in 1,900 ms and
cleaned its exact database identity. A separate ten-sample run reached initial
state in all ten samples (1,192–1,922 ms), but its 40-second aggregate cleanup
observer timed out before nine delayed cleanups were logged; those database
names were already absent when checked afterward. This timing issue is
recorded rather than promoted to a ten-sample pass.

The repository contract verifier reaches publication but currently stops on a
pre-existing contract-registration error. A freshly published probe database
verifies 36 contracts and has no hash mismatches, but its live
`contract_version` table omits the following three expected Unity-bundle keys
(the checked-in Unity and server files both exist):

- `Assets/Arena/Resources/SharedData/Maps/arena_map_01.collision.shared.json`
- `Assets/Arena/Resources/SharedData/Maps/arena_map_01.layout.shared.json`
- `Assets/Arena/Resources/SharedData/Maps/arena_map_01.query_collision.shared.json`

That failure is unrelated to combat-build admission and is not represented as
a pass. A check against the older default `arena` database also reports its
weapon-appearance hash as stale; the fresh probe database does not. Unity batch
mode was neither authorized nor used.
