# Source-of-truth cleanup handoff — 2026-09-05

This document records the initial six-item cleanup, the subsequent approved
five-item authoring batch, and the approved normal-Editor verification and
read-only inventory. The second batch implemented four items and retained the
Ice Spikes compatibility cue after demonstrating why its removal is unsafe
for empty-ability facts. Further migrations listed below require a separate
scope decision.

Latest result: the approved melee cleanup below corrected five selectable
timing mismatches, refreshed 34 mirrors and 12 trim fields, and added a check
that now passes for all 58 selectable melee abilities. The rebuilt local match
is `sha256-14a507e5ecb72384f8eb`; native Unity Hub-to-match-to-Hub validation
passes. Earlier batch/inventory results are historical snapshots.

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

Limitations at the end of the second batch:

- Unity-dependent animation/import tests were added and compiled but were
  not executed; the Editor was closed. The native test gap is closed by the
  verification pass below. Visual verification is still pending.
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

## Approved verification and inventory pass

The normal Unity Editor **6000.4.0f1** imported the changes and ran **40 native
EditMode tests, all passing**: event-first import, eventless import, legacy LOS
read/export compatibility, single/phased hit events, startup trim, Ice Spikes
fallback behavior, cast-hand resolution, and the cue writer. No Unity batch-mode
run was used. The verification Editor was stopped after the pass.

`Arena/Animation/Verify Combat Authoring and Export Inventory` repeats these
tests and exports a read-only report to `Logs/CombatAuthoringVerification`.
The helper rejects batch mode and Play Mode. It reads the imported animation
clips and uses the existing melee exporter, animation resolver, VFX generator,
and editor cue comparison; it does not stamp events, export the manifest,
write cues, or save authored assets. The melee inventory covers authored attack
entries, not separately synthesized autoattack aliases. VFX comparisons cover
the 151 abilities using the SPELL gameplay executor, including NPC abilities
and Techniques implemented by that executor.

Evidence for this run is in `/private/tmp/arena2-unity-verification.ysXsW2`:

- `unity/tests.xml`: 40 passed, zero failed.
- `unity/inventory.json`: 104 melee attacks across five weapon profiles and
  906 VFX comparisons (151 abilities × five profiles plus GLOBAL); zero
  inventory errors.
- `baseline-gate.log`: 822 server tests, 25 Hub tests, 19 Python tests,
  8 Ruby tests / 14 assertions, 38 mirrors, and 329 NPC references pass.
- `hub.before.json`, `hub.after.json`, and `hub-comparison.txt`: all 12 captured
  tables are exactly unchanged, including all 63 profiles, saved builds,
  equipment choices, and four Hub catalogs. These snapshots remain local.

Only the verification helper and documentation changed in this pass. No
authored assets, shared JSON, gameplay code, or generated bindings changed.
Automatic shared-data publication was disabled for the verification Editor
process; no runtime publication or additional match probe was needed. Final
canonical local status still reports the server, both artifacts, and provisioner
ready.

Direct visual inspection remains **unverified**: macOS denied UI inspection
through System Events (`-1743`). Native tests and cue-field comparisons do not
prove the appearance of a cast or attack in a running match. The unrelated
Photosynthesis assertion and Rust formatting issue above were not changed or
included in the 40-test native pass.

### Melee inventory

This inventory predates the approved melee cleanup below; its five selectable
timing mismatches, 34 stale mirrors, and 12 trim omissions are now resolved.

Five attack entries lack effective `OnStrikeHit` events. Their current export
and committed manifest agree, and all are single-clip presentations with zero
startup trim:

| Profile | Strike | Hit / recovery (ms) | Current exposure |
| --- | --- | --- | --- |
| DAGGERS | `DAGGER_COMBO_ATTACK_03_03` | 220 / 300 | Selectable `DAGGER_SEVER` |
| DAGGERS | `WARRIOR_CARVE` | 220 / 300 | Selectable `WARRIOR_CARVE`; shares the preceding row's clip |
| SWORD_AND_SHIELD | `WEAPON_THROW_ORBIT` | 833 / 250 | No progression ability has this action ID |
| SWORD_AND_SHIELD | `SHIELD_THROW_ORBIT` | 792 / 250 | No progression ability has this action ID |
| TWO_HANDED_SWORD | `COMBO_ATTACK_4_4_LUNGING_SLASH` | 256 / 350 | Selectable `WARRIOR_SUNDER` |

These five rows reference four distinct extracted clips. Lack of a progression
action match is not proof that an asset has no other consumer. Any migration
must consider every attack sharing the clip and distinguish contact from
projectile release.

The inventory also found **nine existing manifest rows with different hit
delays** from a current event-based export. Recovery matches in all nine:

| Profile | Strike | Committed → current export hit delays (ms) | Matching selectable ability in that profile |
| --- | --- | --- | --- |
| STAFF | `COMBO_ATTACK_3_1_LOW_TO_HIGH` | 935 → 762 | None; matching ability belongs to Two-Handed Sword |
| STAFF | `COMBO_ATTACK_4_4_LUNGING_SLASH` | 350 → 515 | None; matching ability belongs to Two-Handed Sword |
| SWORD_AND_SHIELD | `AIR_TO_GROUND_1` | 532 → 457 | `PALADIN_AIR_TO_GROUND_1` |
| SWORD_AND_SHIELD | `AIR_TO_GROUND_2` | 388 → 343 | None |
| SWORD_AND_SHIELD | `AIR_TO_GROUND_3` | 333 → 273 | `PALADIN_AIR_TO_GROUND_3` |
| TWO_HANDED_SWORD | `COMBO_ATTACK_3_2_LOW_TO_HIGH` | 750 → 683 | None |
| TWO_HANDED_SWORD | `CRUSHING_BLOW` | 602 → 527 | `WARRIOR_CRUSHING_BLOW` |
| TWO_HANDED_SWORD | `CATACLYSM` | 417 → 342 | `WARRIOR_CATACLYSM` |
| TWO_HANDED_SWORD | `BUZZSAW` | [341, 500] → [288, 447] | `WARRIOR_BUZZSAW` |

These values are export comparisons, not approved timing changes. Five map
directly to current selectable abilities in the affected weapon profile. The
server consumes the committed hit delays; updating those rows would change
gameplay timing and needs its own bounded migration and verification.

**Twelve additional rows differ only in startup-trim metadata among the timing
fields checked**: their committed trim is absent/default zero, while the current
export has a positive trim. Their hit delays and recovery already agree:

- ARCHER_BOW: `ARCHER_HEARTSEEKER` (125 ms).
- STAFF: `COMBO_ATTACK_1_1_HIGH_TO_LOW`, `WARRIOR_MAIM` (117 ms each).
- SWORD_AND_SHIELD: `SWORD_AND_SHIELD_ALT_LIGHT_3` (181 ms).
- TWO_HANDED_SWORD: `COMBO_ATTACK_1_1_HIGH_TO_LOW`, `WARRIOR_MAIM` (186 ms);
  `COMBO_ATTACK_1_2_LOW_TO_HIGH`, `WARRIOR_CARVE`, `COMBO_ATTACK_2_1_SPIN`
  (151 ms); `COMBO_ATTACK_1_3_GROUND_TO_AIR` (123 ms);
  `COMBO_ATTACK_2_2_HIGH_TO_LOW` (188 ms); `COMBO_ATTACK_2_4_LUNGE` (134 ms).

The server uses `startup_trim_ms` to adjust timed melee movement. None of these
12 action IDs overlaps the three current abilities authoring
`melee_timed_movement` (`WARRIOR_DISENGAGE_STRIKE`, `ARCHER_BACKSTEP`, and
`ARCHER_DISENGAGE`). Do not count these as another 12 demonstrated hit-delay
bugs. `manifestTimingMatches` in the report includes trim, recovery, hit delays,
and per-hit phase metadata; it is not a comparison of every manifest field.

**Nine STAFF attack entries have no corresponding committed manifest strike**:
`COMBO_ATTACK_1_4_AIR_TO_GROUND`, `COMBO_ATTACK_3_2_LOW_TO_HIGH`,
`COMBO_ATTACK_3_4_AIR_TO_GROUND`, `CRUSHING_BLOW`, `CATACLYSM`, `BUZZSAW`,
`WHIRLWIND`, `WARRIOR_CHARGE`, and `WARRIOR_IMPALE`. Staff currently owns no
selectable or intrinsic Technique. A full profile export would add these rows;
this inventory does not authorize that content expansion.

**34 event-backed attack entries have stale serialized normalized hit-window
mirrors**: Daggers 4, Staff 9, Sword and Shield 17, Two-Handed Sword 4. Current
export already prioritizes events, so stale mirrors are not 34 additional
demonstrated runtime timing errors. The new importer protects that authority;
this pass did not rewrite existing mirrors. These categories overlap.

### VFX inventory

The catalog remains the runtime authority. Generator comparisons reveal both
missing generation inputs and deliberate authored exceptions; they are not a
bulk-regeneration checklist. Counts below are abilities with each condition,
and conditions overlap:

| Condition | GLOBAL, Archer, Daggers, Staff, Two-Handed Sword (each) | Sword and Shield |
| --- | --- | --- |
| Existing slot fields differ | 12 | 21 |
| At least one generated-only slot | 29 | 29 |
| At least one catalog-only slot | 53 | 53 |
| Ambiguous slot identity | 1 | 1 |
| At least one authored cue cannot be assigned a slot | 11 | 11 |

For Staff, the existing comparison finds 65 matching generated slots and 14
changed slots; Sword and Shield has 50 matching and 29 changed. Only 27 Staff
cases and 19 Sword-and-Shield cases have a nonempty generated result with all
generated slots matching and none of the conditions above. This does not prove
visual equivalence or make those rows automatically generated ownership.

The 12 baseline changed abilities are:

- `NPC_DEMON_SUMMONER_SHADOW_BOLT`, `NPC_SKELETON_REAPER_SOUL_BOLT`:
  generated impact duration 700 ms versus authored 1000 ms.
- `NPC_FAB_DRAGON_BREATH`: generated Fire cast-glow ID versus authored Arcane.
- `SPELL_DEEPENING_COLD`, `SPELL_FLASH_FREEZE`, `SPELL_GLACIAL_ADVANCE`:
  generated Ice impact ID and fixed duration versus authored Glacial Spike ID
  and particle-system lifetime.
- `SPELL_DEFILED_GROUND`: fixed generated duration versus authored radial-effect
  lifetime.
- `SPELL_FLAMING_ORB`, `SPELL_GRAVEWAKE`, `SPELL_WITHERING_ORB`: anchor differences.
- `SPELL_ORBITING_BLADES`: impact ID/duration differences.
- `SPELL_PENANCE`: generic Holy impact versus authored Absolution impact.

Sword-and-Shield animation resolution adds hand-anchor differences for
`SPELL_BLIZZARD`, `SPELL_CAUTERIZE`, `SPELL_FIERY_ORBS`, `SPELL_FIREBALL`,
`SPELL_FROZEN_SPLINTERS`, `SPELL_GRIM_WHEEL`, `SPELL_INSTANT_BEAM`,
`SPELL_MAGIC_MISSILE`, and `SPELL_VAMPIRIC_ORB`; it also adds an anchor difference
to the already changed `SPELL_ORBITING_BLADES`. Generated anchors are RIGHT_HAND
where the catalog specifies LEFT_HAND. This is evidence that global cue
generation needs an explicit policy for equipment-dependent presentation before
it can safely become authoritative.

The ambiguous slot is the retained Ice Spikes ABILITY/SPELL compatibility pair.
The 11 abilities with unassigned authored cues are `SPELL_CLOUDBURST`,
`SPELL_CONTAGION`, `SPELL_EARTH_BLAST`, `SPELL_FULMINATION`, `SPELL_HOLY_SHIELD`,
`SPELL_LAVA_BLAST`, `SPELL_RECKONING`, `SPELL_TAILWIND`, `SPELL_TIDAL_BLAST`,
`SPELL_TRANSPOSE`, and `SPELL_WIND_BLAST` (Transpose has two such cues).

The 35 unresolved animation cases per equipped profile comprise **17 explicit
NoAnimation assignments and 18 NPC abilities missing from the player animation
map**. NPC presentation requires its own context; these are not 35 proven missing
player animations. GLOBAL intentionally lacks an equipped animation set and
therefore resolves fewer animations. The generator also reports 191 omitted-slot
notes across 120 abilities per profile because neither the selected school
palette nor a signature override supplies that slot's VFX ID. Missing generation
inputs do not establish that the corresponding runtime visual is absent.

### Approved melee cleanup batch

The user approved four sequential items: reconcile the five selectable timing
mismatches; refresh the 34 stale mirrors and 12 startup-trim fields; add a
catalog-derived normal-Editor drift check; and validate/rebuild the complete local
path with saved-state preservation. Eventless clip migration, extra Staff
strikes, VFX regeneration, and Ice Spikes fallback removal remain outside scope.
Additional discrepancies are to be reported, not silently migrated. If macOS
continues to block visual inspection, independent work proceeds and that gate
remains explicitly unverified.

Evidence is in `/private/tmp/arena2-melee-cleanup.k6cb8O`. Baseline source-of-truth
checks pass (822 server, 25 Hub, 19 Python, 8 Ruby tests / 14 assertions), the
canonical local stack is ready, and all 63 current profiles and v2 saved-build
tables were snapshotted before editing.

- **Item 1 complete:** a normal Unity Editor run generated the five selected
  strikes' hit windows through `BuildMeleeExport` and the existing manifest
  reader/writer. The actual event-derived delays are 457 ms, 273 ms, 527 ms,
  342 ms, and [288, 447] ms in the table's selectable-ability order. A parsed
  before/after comparison proves only those six `impact_delay_ms` values
  changed. Recovery, combo, delivery, all other manifest fields, and every
  authored asset are unchanged. All 84 server melee tests pass. Local runtime
  publication and final Unity checks belong to item 4.
- **Item 2 complete:** the normal Editor refreshed exactly the 34 inventoried
  mirrors, updating their `hitWindows` and first-hit `impactNormalized` copies.
  Complete exports of all five profiles are identical before/after the mirror
  refresh. Parsed asset comparisons prove every other field is unchanged;
  unrelated default-field serialization from two older assets was removed.
  The existing exporter/writer supplied the 12 missing positive
  `startup_trim_ms` values. No other manifest field changed, and none of those
  action IDs authors timed movement in progression. All 84 server melee tests
  pass after this metadata update. The five eventless entries retain their
  existing fallback values, and no Staff strike was added.
- **Item 3 complete:** the normal-Editor verification derives its selectable
  melee roots from the existing v2 Specialization and progression read boundary.
  All **58 selectable melee abilities** agree with their current Unity exports
  on hit delays/counts, per-hit phase metadata, recovery, startup trim, combo
  windows, and phased gap-close durations. Missing actions fail the check.
  Fifteen new native cases cover current content, catalog selection, incomplete
  references, multi-hit drift, optional phase timing, and case semantics. All
  **55 targeted native EditMode cases pass**. A fresh native reload also confirms
  zero stale mirrors and identical complete exports of all five profiles.
  The remaining four hit-delay differences, five eventless rows, and nine extra
  Staff entries are unchanged. `item3-verification/tests.xml`, `inventory.json`,
  and `item3-verification.ok` contain the evidence. The check deliberately covers
  direct selectable melee roots, not synthesized autoattack aliases or every
  combo successor.
- **Item 4 complete:** the final source-of-truth gate passes (822 server, 25 Hub,
  19 Python, 8 Ruby tests / 14 assertions; shared mirrors and NPC paths valid).
  Canonical data-preserving local setup rebuilt both artifacts and restarted the
  managed provisioner. The anonymous v2 Hub-to-match probe passed with nonempty
  saved build/equipment and completed exact-identity cleanup. A normal Unity
  Editor then compiled/imported the current code, connected to the local Hub,
  applied the saved showcase, requested a new bot match, loaded `Arena_Map_01`,
  verified the frozen build/equipment under the same identity, and returned to
  the Hub. The final canonical status is ready; all verification Editor
  processes are stopped and the temporary execution/probe scripts are removed.

Implementation commits: `e5ac7383` (five strike timings), `fa21e277` (derived
metadata), and `651e6842` (catalog-derived drift check). This handoff records the
final local validation. No remote publication or push was performed.

The Unity client evidence is `unity-client.json` and `client-unity.log` in the
batch's temporary evidence directory. It verifies the existing identity's
revision **10**, `DAGGERS_BLADEDANCER` and `DAGGERS_EXECUTIONER`, **18 active
features**, traits, `HUNTER_RD` armor, and `NEWBIE_DAGGER_PAIR_02` equipment.
The Hub showcase and match both instantiated two
`Dagger_1H_Newbie_02_Cl` prefabs and 26 avatar renderers. Unity's own rendered
captures, `hub-showcase.png` and `match-player.png`, were inspected. This closes
the showcase and general client handoff verification gaps. The five corrected
Sword-and-Shield/Two-Handed-Sword attacks were verified through native event
exports and timing tests; they were **not individually exercised in live combat**.

The first temporary client probe incorrectly compared all saved feature rows,
including dormant Specialization choices, against the active match build. The
probe was corrected to compare only active selections; production build code
and saved choices were unchanged. Its recovery Editor also became unresponsive
while destroying scene objects and required termination after the match had
already reached `CLEANED`. The corrected run passed the active-build comparison
and returned to the Hub. Both Unity test allocations and the anonymous probe
allocation reached `CLEANED`. The final lingering Editor was stopped after its
normal quit request; no authored scene or asset was saved by the client probe.

`hub.before.json`, `hub.after-setup.json`, `hub.final.json`, and
`final-hub-preservation.txt` prove that every saved row for the **63 original
profiles** survived unchanged. All four Hub catalogs are exactly unchanged.
The anonymous benchmark added one profile, bringing the total to 64; both Unity
runs reused the existing identity. Generated C# bindings and the progression
catalog are unchanged. All **906 VFX comparisons** are identical to the original
inventory. The total manifest diff remains exactly five hit-window updates and
12 startup-trim additions; no other parsed manifest field changed.

Latest verified match build: `sha256-14a507e5ecb72384f8eb`.
Source fingerprint:
`acf2f1b65ab47e83b43b236f15611b86b5e4110f1a72d97111df79501f870e2a`.
WASM SHA-256:
`14a507e5ecb72384f8ebf2c069f8ef461d4a66679cdb9e9f7cc84f6306e862da`.
Both artifact provenance manifests are copied into the evidence directory;
`final-status.log`, `match-probe.jsonl`, and `unity-cleanup.json` record readiness
and cleanup.

The remaining inventory findings are the four existing hit-delay differences
outside direct selectable roots, five eventless entries, nine unexported Staff
strikes, and the VFX/identity decisions described below. None was expanded into
this batch.

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

The reverse import path is now guarded by animation event authority. The
normal Editor inventory above identifies the five remaining eventless attack
entries and their effective timings. Any migration still needs a per-clip
contact/release decision and verification of every sharing attack. No clip
stamping or fallback removal was performed.

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
