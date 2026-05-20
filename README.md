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

4. Publish the local `arena` database. This clears existing local database data:

```bash
spacetime publish arena --clear-database
```

5. Generate the Unity C# bindings after server schema changes:

```bash
spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB
```

6. Return to Unity and let the editor recompile.

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

After changing server tables, reducers, or generated types:

```bash
spacetime build
spacetime publish arena --clear-database
spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB
```

Then let Unity recompile before testing.
