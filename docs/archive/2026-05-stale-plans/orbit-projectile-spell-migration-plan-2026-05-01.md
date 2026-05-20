# Orbit Projectile Spell Migration Plan

Status: implemented as a melee cleanup pass on 2026-05-01.

The important result is that melee no longer owns the old contact-resolution field or targetless projectile delivery path. Current player-facing melee abilities all resolve to targeted melee strikes, and the exported melee manifest now contains targeted strikes only.

## Completed Changes

- Removed targetless projectile delivery from Rust melee schema/runtime.
- Removed the old contact-resolution field from Rust, Unity authoring code, generated bindings, exported JSON, Unity assets, docs, and tests.
- Removed projectile-only hit-count/cooldown tuning from melee ability and auto-attack catalog rows.
- Regenerated SpacetimeDB C# bindings after the Rust schema change.
- Removed old targetless projectile strike entries from combat animation set melee attack lists and from `server/src/melee_manifest.shared.json`.
- Kept the Dread Strike authored strike targeted by pointing the runtime behavior at `COMBO_ATTACK_2_4_LUNGE`; Dread Strike now consumes that authored strike through auto-attack replacement behavior rather than a selectable melee row.

## Follow-Up

If a targetless weapon-shaped projectile becomes player-facing again, add it as spell ability gameplay instead of reintroducing it to melee. The ability's `gameplay.delivery` should own projectile lifetime, hit cooldown, max hits per target, and presentation routing. The melee layer should remain animation-first targeted strike data.

## Acceptance

- `server/src/melee_manifest.shared.json` contains targeted melee strikes only.
- `docs/combat-authoring-contract.md` describes melee without projectile-delivery exceptions.
- `cargo test --manifest-path server/Cargo.toml` should pass before commit.
- Unity/editor builds should pass after generated binding changes.
