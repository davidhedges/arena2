# Source-of-truth cleanup handoff — 2026-09-05

This document records the initial six-item cleanup and the subsequent approved
five-item authoring batch. The second batch implemented four items and retained
the Ice Spikes compatibility cue after demonstrating why its removal is unsafe
for empty-ability facts. Further migrations listed below require a separate
scope decision.

## First batch: completed

| Item | Result | Local commit |
| --- | --- | --- |
| Ability ownership | Corrected six progression discipline fields to `DAGGERS`; generator and shared Rust validation now require all 220 selectable features to agree with their v2 Specialization parent. | `703499d1` |
| Shared JSON contracts | Corrected Python's `Maps/` contract prefix to `map_data/`; added offline mirror, missing-file, and ambiguous-key checks. No shared content was regenerated. | `6b81cc08` |
| Armor definitions | Moved the 89-set roster, pieces, tiers, and resolved stats into one pure Rust catalog consumed by inventory and Hub. | `2d148914` |
| Weapon read schema | Inventory, Hub, and v2 build validation now deserialize the single authored weapon JSON through one Rust schema. Existing legality checks remain in place. | `19a66e01` |
| NPC authoring paths | Family authoring resolves the current catalog and visual-ID profile filenames, preserving existing GUIDs and `.meta` contents. Added a read-only path check. | `246cf6f5` |
| Regression protection | Added `ops/check-source-of-truth.sh`, wired it into the existing PvP workflow with relevant path triggers, and updated the ownership contract and this handoff. | This handoff's commit |

The six corrected abilities were `WARRIOR_CARVE`, `PALADIN_SERRATED_BLADES`,
`DAGGER_DISARM`, `DAGGER_DARKNESS`, `DAGGER_STALK`, and `DAGGER_SHADOWREND`.
Spell schools and gameplay fields were preserved. In particular, the last
three retain Mortality school even though their owning Form belongs to
Daggers. The generated v2 catalog did not change, so existing build ownership,
availability, ordering, and capacities remain intact.

This batch follows the earlier reproducible v2 generator/classification
repair (`8536aeed`) and shared upfront-cost resolver (`3ebd6286`). Those fixes
remain in place; this batch does not migrate the remaining cost fields.

## First batch validation

Run from the repository root:

```bash
ops/check-source-of-truth.sh
```

This command checks the catalog generator and its rejection cases, shared
JSON mirrors, NPC reference paths/GUIDs, and the full server/Hub unit suites.
It needs Python 3, Ruby with Minitest, and Rust dependencies. It starts no
Unity process and uses no live database. It does not regenerate bindings or
prove animation/VFX correctness. See the
[authoring contract](combat-authoring-contract.md#validation-and-publication)
for local publication and live match checks after runtime changes.

Validation recorded for this batch:

- Final gate: **19 Python tests**, **8 Ruby tests / 14 assertions**,
  **821 server tests**, and **25 Hub tests** pass. The initial baseline was
  820 server and 24 Hub tests, with no failures.
- All **38 source/bundle pairs** match under the existing CR-normalized
  comparison. Weapon appearance has one authored copy. During item 2, all
  **39 bundled contract keys and hashes** also matched Rust's compiled hash
  output, including the corrected map prefix.
- All **329 NPC catalog references** resolve to their current profiles and
  matching GUIDs. The five-profile Fab Mimic authoring dry run passed with
  zero new profiles or catalog rows. No profile generation was written.
- Before/after resolved armor snapshots are byte-identical for all 89 sets,
  including piece mappings, names, order, and base stats. Live Hub catalog
  snapshots are identical for **220 features, 89 armor sets, 138 weapon
  definitions, and 425 weapon-color rows**.
- The final local Hub snapshot preserves every saved row belonging to all
  **58 original profiles**, including builds, active/dormant selections,
  discipline configurations, traits, and armor choices. Four anonymous test
  profiles were added by the probes; they do not replace original profiles.
- The 11 focused match build-contract tests pass. The composition probe
  passed three Schools / 18 Spells, three Dagger Forms / six Techniques,
  rejection of a nineteenth feature, and frozen-match isolation from later
  Hub edits.
- The anonymous Hub-to-match probe passed after the final runtime refactor:
  a nonempty saved build and equipment were applied under the same gameplay
  identity, and its match cleanup reached `CLEANED`.
- Final canonical local status reports Hub, PvP artifact, open-world
  artifact, and managed provisioner ready. There are no changes under
  `Assets/` across this batch, including generated C# bindings. No Unity
  Editor was running at the final check; Unity compilation, reconnect, and
  visual showcase verification remain **unverified**.

Latest verified match build: `sha256-7904880b19f0141c2efa`.
Source fingerprint:
`d26bebea4f2c38d120520cd5b74d39f3ba6c2b5a69ef67cd615baa1606fa15df`.
WASM SHA-256:
`7904880b19f0141c2efa2ec71ce7012378f603caea39c472b3e305c834ae5479`.

Local logs, baseline/final Hub snapshots, armor snapshots, and probe results
are in `/private/tmp/arena2-truth-cleanup.zDkf7R`. These are temporary local
evidence, not checked-in player data. CI wiring was validated locally; no
remote workflow was triggered and nothing was pushed.

## Second approved batch

| Item | Result | Local commit |
| --- | --- | --- |
| Protect melee timing | Manifest import rebuilds event-backed mirrors from animation events, including startup trim and phased timing. Eventless import conversion, recovery, and combo behavior are unchanged. | `19be6834` |
| Retire ignored melee LOS control | Hid the old serialized asset field, removed its exporter input and 11 ignored manifest fields, and retained old-manifest read compatibility. Ability targeting rules are unchanged. | `62be044a` |
| Resolve Ice Spikes duplicate if safe | Retained the fallback. Four behavioral cases demonstrate that removing it loses the effect for an empty-ability fact. | `99dec9c1` |
| Consolidate cast-hand ownership | Removed the unused per-spell option and 39 serialized `Auto` values. Resolved animation origin/mirroring and existing inference/default behavior remain authoritative. | `87680d19` |
| Clarify VFX authoring | Documented field ownership, corrected preview guidance, restricted generated writes to ABILITY owners, rejected stale-preview writes, and aligned explicit/generated CharacterFx variant keys. | This update's commit |

The current ownership rules are in the
[VFX authoring contract](combat-authoring-contract.md#vfx-cue-ownership) and
[animation contract](combat-animation-authoring-contract.md). This batch did
not regenerate VFX cues, change clips, retime attacks, or edit prefab wiring.
The progression catalog and generated network bindings are unchanged.

Second-batch evidence is in `/private/tmp/arena2-authoring-cleanup.7R38au`:

- Baseline: 821 server tests and 25 Hub tests passed; all 62 current profiles
  and associated v2 saved-build rows were snapshotted before editing.
- Final catalog gate: 822 server tests, 25 Hub tests, 19 Python tests, and
  8 Ruby tests / 14 assertions pass. Shared mirrors and 329 NPC references
  still validate.
- The editor and test assemblies compile with .NET against the installed
  Unity references. Twenty-six selected managed regression cases run against
  those compiled assemblies: four Ice Spikes cases, eight cast-hand cases,
  and fourteen cue-writer cases. This starts no Unity process and does not
  substitute for Editor import, native animation tests, or visual verification.
- All eight cast-hand before/after resolver snapshots are identical. The
  VFX override asset changed only by deleting 39 unused `castHand: 0` lines;
  every slot look, duration, prefab reference, and GUID was preserved.
- The melee manifest changed only by removing 11
  `requires_initial_line_of_sight: true` entries. Every other parsed value,
  including hit/recovery timings and projectile tuning, is identical.
- Data-preserving local setup passed; all 62 original saved profiles/builds
  and every Hub catalog row survived unchanged. The anonymous match probe
  applied the nonempty Hub build and equipment under the same identity and
  cleaned up successfully. It added one test profile.
- Latest verified match build: `sha256-4baa709449388f554fad`.
  Source fingerprint:
  `22b1170b6ae57b070de4273a3a1d18b850bf253d832f2344fbcf8774a2cd7247`.
  WASM SHA-256:
  `4baa709449388f554fadd7d8aee78f80bab723cf927255bb353fd930674515d3`.

Validation limitations remain explicit:

- Unity-dependent animation/import tests were added and compiled but were
  not executed; the Editor was closed. Visual verification is still pending.
- A broader managed run found
  `PhotosynthesisVfx_ScalesOnlyTheAuthoredLeafFlecksWithStacks` failing a
  source-text assertion about `main.maxParticles`. Both that test body and
  the production file are identical to the batch baseline. It was left
  unchanged because Photosynthesis behavior is outside this batch. A separate
  existing CharacterFx whitespace-normalization failure was directly within
  the cue identity work and is fixed and passing.
- `rustfmt --check` reports the same wrapping-only difference in the existing
  `effective_melee_timed_movement_start_delay_ms` assertion as the saved
  pre-edit source. That unrelated formatting was preserved.

## Remaining work

### Ice Spikes identity prerequisite

The catalog still contains 178 `ABILITY` cues and one `SPELL` cue. For
`SPELL_ICE_SPIKES`, the ABILITY row suppresses the equivalent legacy SPELL
row. Without ability identity, the resolver uses the SPELL row; deleting it
produces zero cues. The new tests execute that behavior using the real
resolver, including both retained/removed and present/missing identity cases.

`CombatVFXDispatcher.BuildFact` forwards `CombatEvent.AbilityId` without
requiring a nonempty value. Its predicted area-impact path uses
`ResolveLocalAbilityId`, which can return an empty selected-action lookup.
The server's `ability_id_for_spell` also returns an empty string when neither
player selection nor NPC lookup resolves. Normal area and delayed-area
paths propagate the supplied identity, but this batch did not prove every
producer can never supply an empty value.

Before removing the fallback, establish and test that invariant across
prediction, authoritative events, and any trusted or compatibility cast
paths. The batch deliberately preserved the cue instead of changing identity
semantics to make its removal possible.

### Remaining melee fallback migration

The reverse import path is now guarded by animation event authority.
Eventless attacks still have serialized hit windows. Any migration of those
attacks needs a normal Editor inventory and per-attack effective timing
comparison, including startup trim and phased clips. No clip stamping or
fallback removal was performed in this batch.

### VFX generation remains an explicit authoring operation

The writer now respects ABILITY owner boundaries and rejects stale catalog
previews. It preserves manual/legacy rows and existing fields, but the catalog
is not globally generated output. Its 166 explicit slot keys identify rows;
they do not declare 166 automatically generated rows.

A future migration must identify which cue slots are reproducible from
palettes, overrides, gameplay, and animation metadata, and which remain
intentional manual exceptions. Preserve effective prefab, trigger, anchor,
attachment, lifetime, and ordering for each migrated slot and verify visuals
before widening that scope. The registry's prefab/scale authority also remains
separate from palette documentation fields. No blanket regeneration or
registry migration was done.

## Other boundaries still worth tracking

- Ability JSON still has several purpose-specific Rust readers, including
  progression, spell gameplay, and build metadata views. The cost policy
  is shared, but the larger gameplay schema has not been consolidated.
  Shared typed fragments should be introduced only where consumers mean
  the same thing, with resolved-runtime snapshots preserving behavior.
- Upfront and per-second costs remain different concepts. The shared
  resolver fixes upfront metadata; periodic channel/emanation costs and
  the remaining dual authored cost fields need a separate content/UI
  decision.
- Saved Hub choices and frozen match builds are intentionally different
  revisions. Their shared validator/materializer and identity handoff
  were preserved; do not synchronize an active match back to later Hub
  edits merely to eliminate a copy.
- Generated C# types, Hub catalog projections, and validated shared JSON
  mirrors are legitimate derived outputs. They should be regenerated or
  checked at their established boundary, not promoted to independent
  authoring sources.
