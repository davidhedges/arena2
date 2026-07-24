# Dice authoring foundation

Phase 1 is generated from first-party source rather than maintained as hand
edited mesh/prefab YAML.

After Unity imports the scripts and shader:

1. Run **Arena > Dice > Rebuild and Open D20 Foundation**.
2. Resolve any errors printed by the authoring validator.
3. Enter Play Mode in `DiceOverlayLab`.
4. Use Left/Right to inspect every final result, Space for the turntable, and
   A to auto-cycle all results.

The command regenerates only the Phase 1 d20 assets:

- beveled/recessed icosahedron mesh
- Cinzel numeric SDF font and ivory inlay material
- dark-red resin material
- d20 prefab and definition
- default set catalog containing only the d20
- authoring review scene

No physics, overlay motion, gameplay integration, network roll generation,
effects, or other dice shapes are part of this foundation.
