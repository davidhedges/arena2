# Combat Build v2 Phase 7 evidence

Date: 2026-08-29
Status: PASS

## Scope

Phase 7 makes Combat Build v2 the coherent local authority across the Hub,
ticket snapshot, provisioner, disposable PvP/open-world modules, generated C#
bindings, Unity network/editor models, runtime authorization, switching, and
HUD projection. It also performs the approved combat-build-only local Hub
reset and replaces the v1-aware live release probes with v2 assertions.

The four removed Staff melee abilities are not v2 selectable features. Staff
retains its ordinary intrinsic autoattack presentation, but no Staff Technique
row, Technique bar, or feature authorization is derived from that data.

## Canonical publication and guarded reset

`ops/setup-local-multiplayer.sh setup` published the Hub with
`delete-data=never`, regenerated Hub and PvP match bindings, rebuilt both
disposable artifacts, and started the managed provisioner. No Unity Editor or
Unity batch-mode process was used.

Immediately before the reset:

- the recoverable snapshot
  `Library/ArenaLocalMultiplayer/combat-build-v2.before.json` still had SHA-256
  `9c9b6864142859c5b305c96e8270f72341508dc85a9c6cc63bc88a57ceaa3af5`;
- the six v1 row counts were exactly `8/12/12/4/14/2`;
- `hub_player` and `hub_player_armor_selection` were exactly `8/8`; and
- no cutover audit row existed.

The module-owner-only `execute_combat_build_v_2_cutover_reset` reducer accepted
that locked hash. Its immutable audit records eight v1 roots and 44 v1 child
rows removed, eight player rows and eight armor rows preserved, and eight legal
v2 defaults created through the normal defaulting path.

After the reset and all anonymous live probes:

| State | Rows |
| --- | ---: |
| each of the six v1 combat-build tables | 0 |
| `hub_player` | 14 |
| `hub_player_armor_selection` | 14 |
| `combat_build_v_2` | 14 |
| `combat_build_v_2_cutover_audit` | 1 |

The six additional player/armor/build roots belong to the anonymous Phase 7
release probes. The original eight preserved rows remain accounted for by the
audit, and every current player has exactly one armor selection and v2 root.

## Generated-client Hub proof

The standalone C# client now compiles the canonical generated
`Arena.HubDb` bindings rather than the retired Phase 6 rehearsal namespace. It
connected to `arena-hub-local`, subscribed to the caller-filtered v2 aggregate
and catalog, atomically saved Bladedancer + Ruin, reloaded revision 2, and
projected:

- one Dagger Technique bar;
- one stable global Spell binding under Daggers and Staff;
- no Technique bar under Staff; and
- the selected Ruin Perk under both equipped parents.

Result: `PHASE7_LIVE_CLIENT_PASS revision=2 specializations=2 features=3`.

## Hub-to-match handoff and runtime proof

The updated one-sample anonymous benchmark compared the Hub v2 aggregate with
all seven normalized match tables, materialized weapon instances, starting
equipment, armor, schema version, revision, and Mastery predicate. It reached
initial state in 2,377.332 ms and ended `1/1 CLEANED`.

`ops/test-combat-build-runtime.py` saved two Dagger Forms plus Ruin and proved
in a provisioned match:

- the repeated Forms share one Dagger configuration and one switch target;
- one Dagger Technique succeeds under Daggers and is denied under Staff with
  stable `WRONG_WEAPON` authorization telemetry;
- a Ruin School Meteor begins while Daggers are equipped;
- movement authoritatively clears that active cast;
- a Dagger-Form Darkness Spell commits while Staff is equipped;
- no Staff Technique materializes;
- the Ruin Perk remains present across Dagger/Staff/Dagger switches;
- mixed-parent Mastery is inactive; and
- the ticket and exact disposable database reach `CLEANED`.

`ops/test-combat-build-v2-compositions.py` then proved two more real matches:

1. Ruin + Arcana + Blight derived only Staff, materialized exactly 18 ordered
   Spells and zero Techniques, and activated Mastery. A nineteenth feature was
   rejected with `COMBAT_BUILD_V2_FEATURE_CAPACITY` without advancing state.
2. Bladedancer + Executioner + Shadow derived one Dagger configuration and one
   active switch target, materialized two Techniques plus one Form-owned Spell,
   and activated Mastery.

The Hub was changed from the three-School build to the three-Dagger build only
after the first ticket froze. The running School match remained unchanged,
proving snapshot isolation. Both disposable databases reached `CLEANED`.

## Client and presentation proof

The canonical Unity source now consumes the atomic v2 draft, derives one
switch entry per distinct parent, resolves the always-present global Spell bar
and current non-Staff Technique bar, hides the Technique bar under Staff, and
maps all selected actives to the reviewed 18 direct input identities. The
normal Assembly-CSharp and EditMode test projects compile this source and the
regenerated bindings with zero errors. The standalone generated-client probe
provides explicit Hub/HUD diagnostics in place of launching Unity.

The existing global spell-animation lookup and equipped-Discipline override
architecture was retained; no Form-level animator topology was added.

## Artifacts

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| optimized PvP match WASM | 3,418,486 | `06a422f04c23834e7f9d8610219f68ab2d21b606af8864473253fab7533772ea` |
| PvP provenance manifest | — | `c761bb28da2ed2164377a81555037f02ec52a104846bc52a4e3e73b4933b0724` |
| optimized open-world WASM | 126,069,557 | `84d9ed3fb04b726e721b4d2402863ec4a2740a77a8d8854e849e9759179bd705` |
| open-world provenance manifest | — | `044b6b5d0a1adc2a1e4010fa82494676ae6f2c691a83af16079384553ef6be94` |

The PvP artifact remains below its 3,500,000-byte guard.

## Verification

- `cargo test --manifest-path server/Cargo.toml --lib`: PASS, 794 tests.
- `cargo test --manifest-path hub-server/Cargo.toml --lib`: PASS, 31 tests.
- `python3 -m unittest match_provisioner.test_worker
  match_provisioner.test_artifact_provenance
  ops.test_benchmark_local_match_start`: PASS, 35 tests.
- `dotnet build Assembly-CSharp.csproj --no-restore`: PASS, 0 errors.
- `dotnet build Arena.EditModeTests.csproj --no-restore`: PASS, 0 errors.
- `dotnet build client-v2-rehearsal/CombatBuildV2ClientRehearsal.csproj
  --no-restore`: PASS, 0 errors.
- canonical generated C# live client: PASS.
- v2-aware anonymous match-start benchmark: PASS, exact handoff and cleanup.
- mixed runtime authorization/switch/interrupt probe: PASS.
- three-School/18-feature and three-Dagger-Form composition probe: PASS.
- generated Hub/match C# meta synchronization: PASS, `created=0 removed=0`.
- `ops/setup-local-multiplayer.sh status`: PASS; server, both artifacts, and
  managed provisioner ready.
- `git diff --check`: PASS.

## Exit gate

PASS. Hub save/reload, exact frozen snapshot handoff, v2 runtime authorization,
normal client compilation, dual-bar diagnostics, guarded persistent-data
cutover, required real-match compositions, cross-weapon Spells, wrong-weapon
Techniques, Mastery, interruption, freeze isolation, and cleanup all pass on
the coherent canonical local stack.
