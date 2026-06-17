# Arena

Arena is a Unity project backed by a SpacetimeDB module. First-party Unity code and content live under `Assets/Arena`, while the server module lives in `server`.

## Prerequisites

- Unity Editor for this project version.
- SpacetimeDB CLI available as `spacetime`.
- Rust toolchain for the SpacetimeDB module.

## First Run

1. Open the repository root in Unity.
2. Build the SpacetimeDB module:

```bash
spacetime build
```

3. Start the local SpacetimeDB server:

```bash
spacetime start
```

4. Publish the local `arena` database. This clears existing local database data
   and regenerates matching Unity bindings:

```bash
ops/republish-local-clear.sh
```

5. Return to Unity and let the editor recompile.

## Project Layout

- `Assets/Arena/Runtime`: first-party runtime C# code.
- `Assets/Arena/Editor`: first-party Unity editor tools.
- `Assets/Arena/Content`: first-party authored content.
- `Assets/Arena/Resources`: runtime-loaded first-party assets.
- `Assets/Arena/Runtime/Generated/SpacetimeDB`: generated SpacetimeDB C# bindings. Do not hand-edit.
- `Assets/ThirdParty`: imported vendor and package content.
- `server`: SpacetimeDB Rust module.
- `docs`: architecture notes, plans, and project conventions.

See `docs/project-structure.md` for the full folder map.

## Common Workflow

After changing server tables, reducers, generated types, or when you want a
fresh local database:

```bash
ops/republish-local-clear.sh
```

The script builds the server module, publishes `arena` with cleared data,
regenerates Unity bindings, and runs `dotnet build Assembly-CSharp.csproj` when
that project file is present. It defaults to the local projectile-load-harness
feature so regenerated bindings keep matching the checked-in Unity debug overlay.
Set `ARENA_PROJECTILE_LOAD_HARNESS=0` only when intentionally publishing the
plain server shape.

Projectile load harness reducers are feature-gated and are not included by the
plain workflow above. Use the harness build/publish workflow in
`docs/combat-projectile-load-harness-plan-2026-05-15.md` when running that
overlay.
