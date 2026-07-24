# Dice authoring foundation

The d20 overlay lab is generated from first-party source rather than maintained as hand
edited mesh/prefab YAML.

After Unity imports the scripts and shader:

1. Run **Arena > Dice > Rebuild and Open D20 Overlay Lab**.
2. Resolve any errors printed by the authoring validator.
3. Enter Play Mode in `DiceOverlayLab`.
4. Use the local review panel to choose a result, motion path, and presentation
   region. Play/replay, click the moving die to skip, hold indefinitely, or
   dismiss it. `Run results 1–20` checks every final pose.

The command regenerates only the approved d20 overlay assets:

- beveled/recessed icosahedron mesh
- Cinzel numeric SDF font and ivory inlay material
- dark-red resin material
- d20 prefab and definition
- three authored d20 motion profiles
- default set catalog containing the d20 and its motion profiles
- authoring review scene

No physics, gameplay integration, network roll generation, result effects, or
other dice shapes are part of this phase. Forced review values are cosmetic
local preview data only.
