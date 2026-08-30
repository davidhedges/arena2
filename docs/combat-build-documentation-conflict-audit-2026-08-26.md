# Combat Build Documentation Conflict Audit

Date: 2026-08-26

Final reconciliation: 2026-08-27

Status: **SUPERSEDED 2026-08-29.** This records the v1 cutover audit. The
current Forms/Schools/Combat Features contract and authoring rules live in
`docs/combat-build-forms-schools-traits-plan-2026-08-29.md` and
`docs/combat-authoring-contract.md`.

Authority: `docs/combat-build-progression-cutover-plan-2026-08-26.md`

## Scope and method

The final Phase 7 audit searched current documentation and prototypes for:

- primary/secondary discipline persistence or validation;
- schools represented as discipline rows;
- legacy selectable discipline IDs or Staff-to-Arcana fallback mappings;
- learned-spell, spellbook-content, or inventory-based cast authorization;
- ambiguous selected-ability lists;
- legacy Hub, match, generated-binding, bootstrap, or action-bar names; and
- granular damage/presentation types described as the six Staff schools.

Every retained hit was inspected in context. A dated migration record or an
explicitly archived design is not current authority, but it remains allowlisted
only when its status and purpose are unambiguous.

## Current documents reconciled

| Document | Final disposition |
|---|---|
| `docs/combat-authoring-contract.md` | Rewritten as the post-cutover contract: five weapon disciplines, six Staff-only schools, exact active assignments, explicit passives, and one current Hub build. |
| `docs/ability-icon-style.md` | Uses canonical actor scope, selection kind, gameplay kind, discipline, and Staff-school filters. |
| `docs/npc-system-design-2026-07-11.md` | Player authorization is the exact frozen build assignment; NPC authoring remains separate. |
| `docs/open-world-disposable-instances-2026-08-18.md` | Names the durable canonical build and `freeze_player_combat_build_for_ticket`; no legacy Hub row is presented as current. |
| `docs/survival-mode-design-2026-08-03.md` | Item snapshot examples use only current inventory children and do not claim a removed spell-list table exists. |
| Netcode, latency, rewind, and projectile design documents touched by Phase 7 | Local-direct probes use feature-gated canonical frozen-build setup; historical PASS statements retain their dates and link to the current runnable paths. |

## Explicitly archived conflicts

| Document/prototype | Reason it remains |
|---|---|
| `docs/spellbook-composer-design-2026-07-20.md` | Archived and superseded in place. Its former availability chain is historical; any future composer must be a non-authorizing modifier/collection feature. |
| `docs/reward-choice-flow-design-2026-07-25.md` | Archived and superseded in place because it treated damage types and legacy identities as selectable schools/disciplines. |
| `docs/ui-prototypes/reward-choice/` | Visible archived banner and source headers preserve presentation provenance only. |
| `docs/ui-prototypes/disciplines/` | Visible archived banner and README preserve the former primary/secondary mockup only. |
| `docs/spell-presentation-dry-redesign-2026-07-07.md` and `docs/spell-vfx-migration-map.md` | Their legacy VFX-theme terminology is explicitly presentation-only and not a Staff-school selection contract. |

Moving these files would break provenance links. Their archive headers revoke
implementation authority without erasing historical context.

## Final allowlist

The final terminology search permits legacy strings only in these classes:

1. the cutover plan, machine-readable ledger, and dated Phase 1–7 evidence;
2. the explicitly archived/superseded documents and prototypes above;
3. dated audits such as `docs/netcode-sync-audit-2026-07-02.md` and
   `docs/independent-vertical-slice-audit-2026-07-10.md`, where old names record
   what existed at that time rather than prescribe current behavior;
4. immutable authored ability IDs that retain words such as `SUBTLETY` or
   `PRECISION`; these are ability identifiers, not selectable discipline IDs;
5. generic spellbook item/equipment or art-direction terminology that does not
   grant a cast, action-bar slot, school, or discipline; and
6. private runtime/presentation helpers containing “profile” when the value is
   derived from one canonical discipline and owns no public catalog,
   persistence, selection, reducer, or action bar.

No current normative document or runnable prototype remains allowlisted for a
legacy persistence, selection, bootstrap, cast-authorization, or UI-writing
path.

## Verification result

- Scoped production searches returned no removed schema, reducer, field,
  helper, generated DTO, or authorization path.
- Current documentation searches found no unclassified legacy assertion.
- Archived documents retain explicit archive/supersession status.
- The complete commands and results are recorded in
  `docs/combat-build-progression-cutover-evidence-2026-08-27.md`.
