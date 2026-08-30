# Combat Build v2 Phase 6 evidence

Date: 2026-08-29
Status: PASS

## Scope

Phase 6 adds transport-neutral Unity client models for editing and presenting a
Combat Build v2 draft. It also adds an explicit developer-only adapter to the
disposable `hub-v2-rehearsal` contract and generated bindings under the
`Arena.RehearsalHubV2Db` namespace.

The canonical v1 Hub network manager, combat-build screen, runtime HUD,
generated Hub/match bindings, saved state, and gameplay authorization are not
connected to these types. The rehearsal adapter accepts only loopback URIs and
database names beginning with `arena-cbv2-p6-`, has no runtime bootstrap, and
is absent from the canonical Hub manager.

## Editor model

`CombatBuildV2EditorModel` owns a whole-build draft rather than mutating
generated transport rows. It supports:

- one to three selected Forms/Schools, including multiple Forms with the same
  parent Discipline;
- filtered Technique, Spell, and Perk pickers;
- one global 18-point selected-feature capacity with no per-bar cap;
- a separate three-point Trait capacity and initial `MASTERY` selection;
- one editable weapon configuration for each distinct derived parent;
- explicit invalid state for a selected Form/School with no selected feature;
- dormant selections that do not consume capacity or conflict with active
  order; and
- deterministic restoration where active order wins and returning active
  features append in stable catalog order.

Reducer failures remain exact strings in `CombatBuildV2SaveResult`, so stable
server validation codes/details are not translated away by the client.

## Dual-bar projection

`CombatBuildV2HudModel` creates one switch target per distinct parent
Discipline. It keeps the global Spell bar visible, shows the merged Technique
bar for the current non-Staff parent, and hides the Technique bar for Staff.
Selected Perks remain active under every equipped parent.

The server persists preferred order in its locked Spell and
Technique-parent scopes. The client deterministically merges those scopes
into the single locked set of 18 direct input identities, then filters the
result into the two displayed bars. This gives every selected active one
direct input, preserves a Spell's binding across weapon switches, and adds no
new schema authority or independent Spell/Technique cap.

## Generated-binding live rehearsal

`ops/build-combat-build-v2-phase6-bindings.sh` built the isolated Hub module
and generated 30 C# binding sources from its WASM. None was hand-authored.

`ops/run-combat-build-v2-phase6-rehearsal.sh` published
`arena-cbv2-p6-20260830014443-92702`. A real C# client then:

1. connected anonymously through the generated `DbConnection`;
2. subscribed to the caller-filtered aggregate plus all three public catalog
   tables and the contract singleton;
3. observed 18 Specializations, 208 feature definitions, one Trait, and the
   legal default draft;
4. atomically saved Bladedancer + Ruin with Dagger and Staff configurations,
   Quick Cut, Fireball, Flaming Weapon, and Mastery;
5. observed a committed reducer event and revision-2 aggregate reload; and
6. projected one Dagger Technique, a stable cross-weapon Fireball binding, no
   Staff Technique bar, and an always-active Flaming Weapon Perk.

The persisted aggregate ended with one root, two selected Specializations,
two configurations, three features, and one Trait. Canonical v1 Hub combat
row counts were identical before and after the run, and the disposable Phase
6 database was retired.

## Focused behavior rehearsal

The standalone client harness passed four executable scenarios:

- same-parent dormant restoration and conflict-free order reflow;
- mixed Dagger/Staff dual-bar transitions, distinct-parent switching, stable
  Spell input, and always-active Perk state;
- all 18 selected Spells directly reachable with no independent bar cap; and
- explicit empty-Form invalid state plus exact server error presentation.

Editor source guards verify that the bindings are generated, the adapter is
loopback/prefix gated and has no runtime bootstrap, the canonical Hub client
has no rehearsal reference, and the 18 direct action identities match the
locked Phase 0 contract.

## Verification

- `ops/dungeon-compile-gate.sh`: PASS; Assembly-CSharp,
  Assembly-CSharp-Editor, and Arena.EditModeTests each reported 0 errors.
- `dotnet run --project client-v2-rehearsal/CombatBuildV2ClientRehearsal.csproj`:
  PASS, `PHASE6_CLIENT_REHEARSAL_PASS checks=4`.
- `ops/run-combat-build-v2-phase6-rehearsal.sh`: PASS,
  `PHASE6_LIVE_CLIENT_PASS` and `PHASE6_REHEARSAL_PASS`; disposable database
  retired.
- `bash -n ops/build-combat-build-v2-phase6-bindings.sh
  ops/run-combat-build-v2-phase6-rehearsal.sh`: PASS.
- `git diff --check`: PASS.

The compile gate is an ordinary non-batch compiler workflow. No Unity Editor
or Unity batch-mode run was used.

## Exit gate

PASS. The transport-neutral editor/HUD behavior is exercised against fixtures
and a real caller-scoped save/reload, every selected active is directly
reachable across the required bars, Staff behavior is correct, and canonical
v1 client/runtime/saved state remains coherent and unchanged.
