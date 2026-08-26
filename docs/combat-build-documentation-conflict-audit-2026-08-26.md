# Combat Build Documentation Conflict Audit

Date: 2026-08-26

Status: **Documentation reconciliation complete. Runtime, schema, generated
binding, catalog, and UI implementation remain unchanged.**

Authority: `docs/combat-build-progression-cutover-plan-2026-08-26.md`

## Scope and method

This audit searched current, non-`docs/archive/` documentation, plans, and web
prototypes for assertions involving:

- primary/secondary disciplines or their persisted field names;
- school-as-discipline catalog rows;
- legacy selectable IDs (`SUBTLETY`, `WAR`, `ZEAL`, `PRECISION`, and Staff as
  `ARCANA`);
- spell/known-spell/spellbook action-bar authorization;
- granular damage/presentation types called spell schools; and
- ambiguous selected-ability lists and discipline-loadout terminology.

Each hit was inspected in context. A text match was not automatically treated
as a conflict: dated current-state audits, asset paths, and unrelated uses of
the English word “precision” are not competing design authority.

## Confirmed conflicts and disposition

| Document/prototype | Conflict | Disposition |
|---|---|---|
| `docs/combat-authoring-contract.md` | Normatively described weapon-family IDs and consolidated Staff schools as peer disciplines; treated `ability_tags` and the current ActionRef/loadout shape as the future model | Revised in place. The canonical five weapon disciplines and six Staff schools are now explicit. Current `discipline_id`, tag, and ActionRef behavior is labeled a temporary implementation adapter that must not seed new architecture. |
| `docs/spellbook-composer-design-2026-07-20.md` | Preserved `spellbook -> known -> action bar -> cast` as the availability chain | Archived in place with its phases revoked. Attachment/glyph research is historical only; any future composer must be a modifier/collection feature and cannot grant player combat availability. |
| `docs/reward-choice-flow-design-2026-07-25.md` | Called Air/Arcane/Cold/Fire/Holy/Lightning/Necromancy/Shadow spell schools, exposed old discipline identities, and made school/discipline unlocks mutually exclusive | Archived in place and former approval revoked. It is visual history only. |
| `docs/ui-prototypes/reward-choice/` | Executable presentation of the conflicting reward design | Archived in place with a README, source headers, an archived page title, and a visible do-not-implement banner. Kept at the path only to preserve asset/provenance links. |
| `docs/ui-prototypes/disciplines/` | Executable primary/secondary UI with schools as disciplines and 8/1 minima | Archived in place with a README, source headers, an archived page title, and a visible supersession banner. The future replacement UI remains a separate task. |
| `docs/ui-prototypes/hub/` | Current Hub example showed `SUBTLETY` as primary and `RUIN`/`ARCANA` as secondary disciplines | Corrected in place to three equal top-level weapon disciplines: Daggers, Staff, and Archer Bow. Existing legacy icon files are presentation assets, not build identities. |
| `docs/spell-presentation-dry-redesign-2026-07-07.md` and `docs/spell-vfx-migration-map.md` | Historical/current presentation `vfx_school` and palette terminology could be mistaken for player-facing Staff schools | Kept as linked presentation evidence, with explicit notes that their “schools” are legacy VFX themes/damage-presentation types, not Staff schools. |

“Archived in place” is intentional. Several live code comments, later design
documents, and prototype asset links cite these paths. Moving them would erase
useful provenance and create broken references. Their explicit archived status,
revoked approval, source headers, and visible browser notices remove current
design authority without pretending the historical artifacts never existed.

## Inspected non-conflicts

| Document | Why it remains |
|---|---|
| `docs/independent-vertical-slice-audit-2026-07-10.md` | A dated inventory notes that known-spell tables existed. It does not prescribe them as the future player authorization model. |
| `docs/netcode-sync-audit-2026-07-02.md` | `ItemSpell` appears in a dated replication/table inventory, not a loadout design rule. |
| `docs/survival-mode-design-2026-08-03.md` | `ItemSpell` appears in item aggregate/cleanup examples. Those statements do not independently grant combat actions and will be revisited only if the table is removed by implementation. |
| `docs/ui-art-direction.md` | “Arena has combat disciplines, not classes” is compatible with the five weapon-discipline model. |
| `docs/spell-cast-animation-migration-checklist.md` | `Precision` is part of a historical animation-set/profile label, not a selectable combat-build identity. |
| `docs/lag-compensation-design-2026-07-04.md` and `docs/server-event-scheduling-design-2026-07-16.md` | “Precision” is ordinary English and unrelated to the legacy discipline ID. |

## Remaining boundary

This reconciliation removes competing **documentation authority**. It does not
claim the codebase has completed the progression cutover. The old Toolkit
screen, Hub/match schemas, generated bindings, validators, spellbook/known-spell
authorization, and other runtime paths remain on the cutover plan's required
deletion ledger until their separately approved implementation phases pass.

## Verification expectations

Documentation is considered reconciled when:

- every retained conflicting artifact has an explicit archived/superseded or
  transitional status and points to the canonical plan;
- current examples do not present schools as discipline slots or old
  weapon-family IDs as the five top-level choices;
- current normative docs distinguish the six consolidated Staff schools from
  granular damage/presentation types; and
- scoped terminology searches classify every remaining hit as canonical plan
  language, a documented temporary adapter, an explicitly archived artifact,
  historical evidence, or an asset path.

Source/test execution is unnecessary for this documentation-only slice. No
Unity, SpacetimeDB, Hub, match, provisioner, or player data was touched.

## Executed checks

- `node --check` passed for both archived prototype controllers.
- `ops/uss_dialect_lint.py` passed for the still-current Hub prototype and the
  archived reward prototype stylesheets.
- The archived Disciplines stylesheet still reports its pre-existing
  browser-only/non-USS constructs when linted as a whole. No style rule was
  changed in this slice—only its archive header—and it is explicitly no longer
  a translation source.
- All concrete `docs/*.md` references introduced or inspected here resolve;
  the plan's `...evidence-YYYY-MM-DD.md` name is intentionally a future
  artifact template.
- JavaScript syntax checks, trailing-whitespace checks for new files, and
  `git diff --check` passed.
