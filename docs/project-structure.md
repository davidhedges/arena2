# Project Structure

This Unity project keeps first-party game work separate from imported vendor content.

## Assets

```text
Assets/
  Arena/
    Runtime/        First-party runtime C# code.
    Editor/         First-party Unity editor tools, builders, drawers, and validators.
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

`Assets/Arena/Runtime/Generated/SpacetimeDB/` contains generated SpacetimeDB bindings. Do not hand-edit it unless a task explicitly calls out a known generated-code workaround. After server schema changes, regenerate from the repo root:

```bash
spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB
```
