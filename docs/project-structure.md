# Project Structure

This Unity project keeps first-party game work separate from imported vendor content.

## Assets

```text
Assets/
  Arena/
    Runtime/        First-party runtime C# code.
    Editor/         First-party Unity editor tools and editor-only authoring inputs.
    Tests/          Unity edit-mode tests.
    Content/        First-party authored Unity content.
    Resources/      Runtime-loaded first-party assets used by Resources.Load.

  ThirdParty/
    AssetStore/     Imported Asset Store packs, grouped by domain.
    Unity/          Unity sample/package content kept in Assets.

  Recovery/         Unity recovery scenes and crash/session recovery artifacts.
  _Recovery/        Additional Unity recovery artifacts.
```

## First-Party Content

```text
Assets/Arena/Content/
  Animation/        Animator controllers, masks, and slot clips.
  Art/              First-party generated or authored art.
  Input/            Input action assets.
  Prefabs/          First-party authored prefabs.
  Scenes/           Build, open-world, and development scenes.
  Settings/         Render pipeline and project content settings.
  Shaders/          First-party shaders and shader graphs.
```

## Runtime Data

`Assets/Arena/Resources/` is intentionally narrow. Put assets here only when runtime code loads them through `Resources.Load` or `Resources.LoadAll`.

Current important resource folders:

```text
ActionProfiles/
CharacterAppearance/
CharacterAvatarBases/
CombatAnimationSets/
CombatVFX/
SharedData/
UI/
```

## Imported Content

Do not mix imported packs into `Assets/Arena`. Keep vendor packages under:

```text
Assets/ThirdParty/AssetStore/Animation/
Assets/ThirdParty/AssetStore/Audio/
Assets/ThirdParty/AssetStore/Characters/
Assets/ThirdParty/AssetStore/Environments/
Assets/ThirdParty/AssetStore/VFX/
Assets/ThirdParty/Unity/
```

If first-party gameplay needs a vendor asset, prefer making a small authored prefab/material/profile under `Assets/Arena/Content` or `Assets/Arena/Resources` that references the vendor source by GUID.

## Generated Code

`Assets/Arena/Runtime/Generated/SpacetimeDB/` contains generated SpacetimeDB bindings. Do not hand-edit it unless a task explicitly calls out a known generated-code workaround. The canonical shape includes the `projectile_load_harness` feature surface (netcode audit R5): bindings are always generated from a harness-featured wasm so the two regen paths (manual and `ops/republish-local-clear.sh`) produce identical output. The extra harness reducers are unused-but-harmless against a default-features module. After server schema changes, regenerate from the repo root:

```bash
cargo build --manifest-path server/Cargo.toml --target wasm32-unknown-unknown --release --features projectile_load_harness
spacetime generate --yes --lang csharp --bin-path server/target/wasm32-unknown-unknown/release/arena.wasm --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB
```

Do not generate with `--module-path server` — that builds default features and drops the harness surface from the generated output.
