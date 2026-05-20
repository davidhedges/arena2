# Terrain-Conforming Combat VFX Plan

Status: Proposal for review on 2026-05-02.

Goal: support ground-impact VFX that read as painted, scorched, cracked, or projected onto uneven terrain instead of clipping through it as flat 2D planes.

This plan extends the existing combat VFX cue system. It should not make Earthshatter special-case code, and it should not move terrain deformation or gameplay radius into VFX authoring.

## Problem

Imported ground VFX such as `VFX_Fire_Area_Burst_01` often contain flat particle planes for cracks, rings, glows, or scorch marks. They look good on flat ground, but on uneven terrain they:

- clip into slopes and bumps,
- float above nearby lower areas,
- visually disagree with the actual combat surface,
- become harder to tune because raising the whole prefab also raises sparks, domes, smoke, and burst particles.

The current `CombatVFXDispatcher` spawns the whole prefab at one world position. That is correct for one-shot particles, but it is not enough for "ground graffiti" elements.

## Design Direction

Split combat VFX into two presentation layers:

- **Volumetric or particle layer**: sparks, smoke, fire bursts, domes, rocks, embers, light flashes. These remain normal prefab children.
- **Surface layer**: cracks, scorch marks, glowing circles, runes, floor shockwaves. These need a terrain-aware placement mode.

The cue still says only "spawn `vfx_id` at this combat event." The Unity-side VFX template decides whether any child elements need terrain conforming.

Recommended mental model:

```text
server combat cue
  -> VFX_FIRE_AREA_BURST_01_ARENA
  -> Unity registry resolves prefab + surface presentation profile
  -> dispatcher spawns normal prefab
  -> optional surface presenter projects or conforms selected ground visuals
```

## Non-Goals

- Do not encode terrain mesh details in `progression_catalog.shared.json`.
- Do not use VFX visuals to define damage radius, hit validation, or gameplay shape.
- Do not rebuild large meshes every frame.
- Do not require every VFX to use terrain conforming.
- Do not edit vendor package prefabs directly.

## Recommended V1

Start with a **project-owned prefab plus optional conforming surface profile**.

Keep tuned VFX assets under:

```text
Assets/Arena/Content/VFX/Combat/
  Impact/
    Ground/
      VFX_Fire_Area_Burst_01.prefab
```

Add a Unity-side profile concept to the existing registry:

```text
CombatVFXRegistry
  VFX_FIRE_AREA_BURST_01_ARENA
    prefab: VFX_Fire_Area_Burst_01
    surfaceProfile: optional ScriptableObject
```

The server cue continues to reference only:

```json
{
  "vfx_id": "VFX_FIRE_AREA_BURST_01_ARENA"
}
```

This keeps the authored gameplay catalog stable while Unity artists can tune the actual projection implementation.

`surfaceProfile` is the single source of truth for surface behavior. If it is `null`, the entry has no surface layer. Do not put a second `surfaceMode` field on the registry entry, because that creates drift such as "registry says DecalProjector, profile says ConformingMesh."

## Surface Modes

### None

Default behavior. Instantiate the prefab normally.

Use for:

- airborne effects,
- sparks and bursts,
- weapon trails,
- VFX that already look fine on uneven terrain.

### Raised Plane

The simplest tuning mode. Spawn or offset selected ground-plane children upward by a small amount.

Use for:

- quick fixes,
- mostly flat arenas,
- effects where slight floating is acceptable.

This can be implemented inside the prefab without runtime support, or as a registry profile field:

```text
groundYOffset: 0.08
```

This is cheap but not truly terrain conforming.

The global spawn offset in `CombatVFXDispatcher` should not also own surface offset. For V1, treat dispatcher Y offset as a prefab spawn safety offset only. Any decal or conforming mesh offset belongs to `CombatVFXSurfaceProfile.heightOffset`, and the dispatcher should pass the raw impact point into the surface presenter.

### URP Decal Projector

Project a decal texture/material down onto the surface. This is the best first "real" solution for scorch marks, cracks, and rings.

Use for:

- lava cracks,
- scorch marks,
- runes,
- circular telegraphs,
- persistent ground stains.

Pros:

- Conforms visually to uneven surfaces.
- Does not require custom mesh generation.
- Easy to author as material/texture.
- Good match for "graffiti on the ground."

Risks:

- Requires URP decal support to be enabled and configured before any decal prototype can work.
- Needs compatible materials.
- Very vertical or overhanging surfaces may project in ways that need culling/size tuning.
- Existing particle-plane textures may need extraction or recreation as decal textures.
- Uses Unity decal layer filtering, not physics raycast filtering. This needs a separate `decalLayerMask` or equivalent layer policy from conforming mesh `groundLayerMask`.

### Conforming Mesh

Generate a one-shot mesh at VFX spawn time by sampling the ground across a disc or ring. Assign a material that looks like cracks/scorch/lava.

Use for:

- terrain where decals are insufficient,
- stylized cracks that need exact surface placement,
- arenas with non-standard ground renderers.

Pros:

- Full control over shape, UVs, radius, falloff, and height offset.
- Can work without URP decal features.
- Can be tuned per VFX.

Risks:

- More code.
- More tuning.
- One-time raycast/height sampling cost.
- Mesh can look faceted if sample density is too low.

V1 should not update the mesh every frame. Generate once, keep it for the cue lifetime, then destroy it.

## Runtime Architecture

Add these Unity-side pieces:

```text
Assets/Arena/Runtime/Presentation/VFX/
  CombatVFXRegistry.cs
  CombatVFXSurfaceProfile.cs
  CombatVFXSurfacePresenter.cs
  TerrainConformingDiscBuilder.cs
```

### CombatVFXRegistry

Extend each registry entry with optional surface metadata:

```csharp
public sealed class Entry
{
    public string vfxId;
    public UnityEngine.Object prefab;
    public CombatVFXSurfaceProfile surfaceProfile;
}
```

The dispatcher still resolves by `vfx_id`.

Existing registry entries deserialize with `surfaceProfile = null` and continue to behave as prefab-only VFX.

### CombatVFXSurfaceProfile

ScriptableObject describing how to present the surface layer.

Initial fields:

```text
mode
material
radius
innerRadius
segmentsRadial
segmentsAngular
heightOffset
raycastStartHeight
raycastDistance
groundLayerMask
decalLayerMask
durationSeconds
fadeOutSeconds
rotationMode
```

Recommended defaults:

- `radius`: 4.0
- `segmentsRadial`: 4
- `segmentsAngular`: 32
- `heightOffset`: 0.03
- `raycastStartHeight`: 5.0
- `raycastDistance`: 20.0
- `durationSeconds`: use cue `duration_ms` when present

`groundLayerMask` is for physics height sampling in conforming mesh mode. `decalLayerMask` is for Decal Projector visibility/filtering and should exclude characters/props unless intentionally supported.

### CombatVFXSurfacePresenter

Small runtime component created by `CombatVFXDispatcher` after spawning a cue.

Responsibilities:

- create decal projector or conforming mesh,
- position it at the resolved impact point,
- apply deterministic rotation if desired,
- handle fade/lifetime,
- destroy generated objects cleanly.

Lifecycle should be explicit. The dispatcher should create a parent `CombatVFXInstance` root per cue, parent the normal prefab instance and any surface presenter objects under it, and destroy that root after the max of cue duration and surface profile duration. If a surface element needs to outlive the burst particles, it can do so without being detached from lifecycle ownership.

### TerrainConformingDiscBuilder

Builds a mesh once:

```text
for each radial ring
  for each angular segment
    compute x/z offset
    raycast downward against ground layer
    place vertex at hit point + normal * heightOffset
    write UV based on normalized local disc coordinates
build triangles
recalculate bounds
```

Use 64-160 vertices for V1. That is cheap for occasional impact abilities.

Start with simple synchronous raycasts for the prototype only if implementation speed matters. Prefer `RaycastCommand` batching for the production version so 128-256 samples do not create avoidable main-thread or GC pressure when several impacts spawn close together.

## Authoring Workflow

For Earthshatter:

1. Keep `VFX_FIRE_AREA_BURST_01_ARENA` as the combat cue `vfx_id`.
2. Duplicate or tune the prefab so it contains only non-surface particles for the explosion.
3. Extract or recreate the cracked lava floor look as either:
   - a decal material, or
   - a conforming disc material.
4. Add a `CombatVFXSurfaceProfile` asset:

```text
Assets/Arena/Content/VFX/Combat/Impact/Ground/Profiles/FireAreaBurstSurface.asset
```

5. Set the registry entry:

```text
VFX_FIRE_AREA_BURST_01_ARENA
  prefab: VFX_Fire_Area_Burst_01
  surfaceProfile: FireAreaBurstSurface
```

6. Test on:

- flat training ground,
- desert uneven ground,
- slope,
- near rocks or props,
- overlapping multiple casts.

## Performance Budget

Target budget for V1 conforming mesh:

- generated once at spawn,
- no per-frame mesh rebuild,
- 4 radial rings x 32 angular samples = 128 sample points,
- one material instance only if fade/color needs per-instance mutation,
- lifetime under 3 seconds for impact effects.

Expected cost is acceptable for occasional abilities such as Earthshatter.

Avoid:

- generating every frame,
- sampling more than 256 points without profiling,
- using conforming meshes for every minor hit spark,
- enabling this for many persistent AoEs at once.

## Server And Catalog Impact

No server schema change is required for V1.

Existing cue:

```json
{
  "owner_kind": "MELEE_STRIKE",
  "owner_id": "COMBO_ATTACK_3_4_AIR_TO_GROUND",
  "trigger": "MELEE_IMPACT",
  "anchor": "GROUND_UNDER_TARGET",
  "vfx_id": "VFX_FIRE_AREA_BURST_01_ARENA",
  "attach_mode": "SPAWN_WORLD",
  "scale": 1.0,
  "duration_ms": 2500,
  "sort_order": 10
}
```

The terrain-conforming behavior belongs to the Unity registry/template for `VFX_FIRE_AREA_BURST_01_ARENA`.

Possible later server fields, only if needed:

- `surface_radius`
- `surface_mode`
- `surface_profile_id`

Do not add these until multiple abilities need server-authored variants of the same VFX template.

## Implementation Phases

### Phase 0: Decal Renderer Decision

Decals are not just an implementation detail; URP decal support must be enabled before Phase 3 is viable.

This project currently appears to use:

```text
ProjectSettings/GraphicsSettings.asset
  -> Assets/Arena/Content/Settings/Rendering/PC_RPAsset.asset
  -> Assets/Arena/Content/Settings/Rendering/PC_Renderer.asset
```

`Assets/Arena/Content/Settings/Rendering/PC_Renderer.asset` currently has renderer features, but no Decal Renderer Feature. Before building decal mode, choose one:

- enable URP Decal Renderer Feature on `PC_Renderer.asset`, and on `Mobile_Renderer.asset` if mobile builds should support decals;
- or explicitly skip decal mode for V1 and go straight to conforming mesh mode.

Manual Unity editor path:

```text
Project window
  Assets/Arena/Content/Settings/Rendering/PC_Renderer.asset
Inspector
  Add Renderer Feature
  Decal
```

Then configure decal layers/materials and test that a simple Decal Projector appears on the terrain before wiring combat VFX to it.

### Phase 1: Art-Side Baseline

- Tune `VFX_Fire_Area_Burst_01` so explosion particles read well without relying on a flat floor plane.
- Identify which child systems are surface-only.
- Disable or separate those children for the runtime combat prefab.
- Verify the current raised-plane workaround is not sufficient.

### Phase 2: Registry Metadata

- Extend `CombatVFXRegistry.Entry` with optional `surfaceProfile`.
- Keep existing prefab-only entries working with defaults.
- Add editor-friendly enums and tooltips.

### Phase 3: Decal Prototype

- Add `CombatVFXSurfaceProfile`.
- Add `CombatVFXSurfacePresenter`.
- Implement `DecalProjector` mode if URP decals are configured.
- Create one fire cracks decal material/profile.
- Test on desert terrain.

### Phase 4: Conforming Mesh Prototype

- Add `TerrainConformingDiscBuilder`.
- Implement one-shot disc mesh generation.
- Use raycast-based ground sampling first.
- Add profile controls for radius, rings, segments, layer mask, and height offset.
- Add optional fade-out.

### Phase 5: Earthshatter Integration

- Attach the chosen surface profile to `VFX_FIRE_AREA_BURST_01_ARENA` in `CombatVFXRegistry.asset`.
- Keep the cue row unchanged.
- Test Earthshatter on flat and curved ground.
- Capture before/after screenshots.
- Keep `VFX_FIRE_AREA_01` as-is unless it is separately selected for terrain-conforming treatment. Phase 5 scope is only the burst variant used by Earthshatter.

### Phase 6: Hardening

- Add debug visualization for sample points and failed raycasts.
- Add fallback behavior when sampling fails:
  - skip vertex,
  - use center height,
  - or disable surface layer.
- Add per-profile max sample count.
- Add pooling only if profiling shows allocation pressure.

## Validation Checklist

- Earthshatter impact surface no longer clips visibly through desert terrain.
- Explosion particles still spawn at the correct impact point.
- Surface effect does not float obviously on flat ground.
- Surface effect does not project onto characters.
- Surface effect does not project onto nearby vertical props unless intended.
- Multiple casts do not cause frame spikes.
- Existing prefab-only VFX still work with no registry changes.
- `CombatVFXDispatcher` still routes these cues through the existing `SPAWN_WORLD` path before the optional surface layer is applied.
- Server catalog tests still pass.
- Unity build passes.

## Recommendation

Try URP decals first if the cracked fire floor can be represented as a material/texture. If decals are awkward with the current terrain or art style, implement the one-shot conforming mesh builder.

Do not start by making every ground VFX conforming. Use this only for large, high-value ground impact visuals where clipping is obvious, such as Earthshatter.
