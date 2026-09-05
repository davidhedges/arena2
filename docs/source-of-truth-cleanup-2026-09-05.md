# Source-of-truth cleanup handoff — 2026-09-05

The approved six-item batch is complete. This document records its boundaries,
validation, and remaining conflicts. The proposed migrations below are not
approved implementation items; they require a separate scope decision.

## Completed batch

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

## Repeatable checks and evidence

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

## Remaining animation/VFX conflicts

### Melee event timing still has a writable compatibility path

The [animation contract](combat-animation-authoring-contract.md#hit-windows)
declares assigned `OnStrikeHit` clip events authoritative for migrated
attacks. `WeaponStrikeCombatAuthoring.hitWindows` is their compatibility
mirror, and the melee manifest is exported output. Attacks without events
still use serialized hit windows; that fallback is intentional until each
attack is migrated. Startup trim changes the effective event time, so a
raw timestamp copy is not sufficient to prove equivalence.

In [CombatAnimationSetEditor.cs](../Assets/Arena/Editor/CombatAnimationSetEditor.cs),
`CollectStrikeValidationMessages` resolves events first and warns about
missing events or stale mirrors. However, `ImportCurrentManifest` still
writes `authored.hitWindows = BuildImportedHitWindows(...)` from the exported
manifest back into the asset. That creates a reverse writer for data the
contract otherwise treats as derived.

Proposed bounded next step: guard the reverse import for event-backed
attacks and keep eventless fallback handling explicit. First record the
affected strikes and effective hit/recovery times through a normal Editor
workflow, then prove those values remain unchanged. Migrating eventless
attacks is a separate per-attack task; this batch neither counted those
attacks through Unity nor stamped or retimed clips.

### An exported melee line-of-sight setting no longer controls gameplay

`CombatAnimationSet` still exposes
`projectileRequiresInitialLineOfSight`, and its exporter writes
`requires_initial_line_of_sight`. There are **11** such fields in the
current melee manifest, all `true`. The C# manifest schema and an Editor
regression assertion still retain the field.

In [server/src/melee.rs](../server/src/melee.rs), the deserialization field
explicitly says it is superseded by ability-level `requires_target_los` and
is never consulted. The runtime projectile path repeats that distinction.
Thus the authoring switch suggests control it no longer has.

Proposed migration: retire the ineffective melee authoring switch and stop
emitting that melee-manifest field, while retaining compatibility parsing
as needed. Verify the actual ability LOS/range rules and projectile behavior
before and after. Do not remove the similarly named generated projectile
table field across all systems as part of this narrow cleanup.

### VFX generation inputs and runtime cue rows are both editable

School palettes under
`Assets/Arena/Editor/SpellPresentation/SchoolVfxSets/`, the 39 entries in
`SpellVfxOverrideCatalog.asset`, and gameplay/animation facts feed
`SpellVfxGenerator`. Runtime effects consume the authored
`combat_vfx_cues` in progression plus the prefab mappings in
`Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset`.

These layers can be a valid authoring-to-output pipeline, but ownership is
not yet uniformly enforced. In
[SpellAuthoringWindow.CueGeneration.cs](../Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs),
`DrawGeneratedCuePreview` still describes a read-only preview, while
`DrawWriteToCatalogButton` and `ConfirmAndWriteOwnerCues` provide a guarded
catalog writer. The writer blocks ambiguous slots, changed matched rows,
and catalog-only rows; it permits exact matches and added generated slots.
Those safeguards should be preserved, rather than replaced with blanket
regeneration. `server/src/vfx_generation.rs` is a validator of cue field
relationships, not a second generator.

Proposed migration: identify which existing cue slots are generated and
which are intentional manual exceptions. Preserve each effective cue's
prefab, trigger, anchor, attachment, lifetime, and ordering; make only the
agreed generated slots derived output. Update the misleading preview help
text together with that authoring workflow. Verify representative spells
visually before widening the migration.

### Ice Spikes has an active legacy cue fallback

The catalog currently has **178 `ABILITY` cues and one `SPELL` cue**.
`SPELL_ICE_SPIKES` owns an `ABILITY` / `AREA_IMPACT` cue at sort order 30;
`ICE_SPIKES` owns a `SPELL` / `AREA_IMPACT` cue at sort order 31. They specify
the same effect, anchor, attachment, and 2500 ms duration.

[CombatVfxCueResolver.cs](../Assets/Arena/Runtime/Presentation/CombatVfxCueResolver.cs)
adds ability cues first and suppresses matching spell cues by an override
key. For an Ice Spikes fact carrying `SPELL_ICE_SPIKES`, the second row is
therefore a shadowed fallback, not evidence that the effect renders twice.
Facts without that ability identity may still use the legacy row.

Proposed migration: verify every Ice Spikes event/reconstruction path
supplies the ability identity, cover the effective resolved output, then
remove that one redundant row if it is no longer needed. Retiring the
entire `SPELL` owner kind is a separate compatibility decision.

### Cast hand retains an unused authoring fallback

`ResolveCastHandAnchor` prefers the animation-owned cast origin, then the
older per-spell VFX `castHand` override, then inferred animation/default
hand. All **39 current overrides are `Auto`**; there is no explicit
left/right override in the current asset. This is a remaining parallel
authoring option, not an observed wrong-hand bug.

Proposed migration: establish animation-owned origins for the relevant
recipes, verify mirrored poses and projectile origins, then retire the
unused per-spell hand override. Preserve the existing resolution until
that evidence exists.

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
