Project-owned prefab variants for OpenWorld environment assets.

Use this directory for Arena prefab variants based on Toon* package prefabs from:

Assets/ThirdParty/AssetStore/Environments

Do not edit vendor prefabs in the ThirdParty tree. Create prefab variants here, add Arena-specific collider and authoring changes to the variants, and update OpenWorld scenes to reference these variants.

Bulk workflow:

Use the Unity menu item:

Arena/OpenWorld/Scene Prep/1 Generate + Replace Toon Variants

That command scans the active scene dependencies, creates missing variants here, adds a generated ArenaGameplayCollision child with a BoxCollider when the source prefab has no blocking BoxCollider, and replaces active-scene instances with the generated variants.

Package-specific collider generation settings live in:

Assets/Arena/Content/Settings/OpenWorld/toon_variant_generation_settings.json

If a prefab is excluded by package settings and the scene already references its Arena variant, rerunning the command replaces that instance with the original ThirdParty prefab.

Path convention:

Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants/<ToonPackage>/<VendorPrefabCategory>/<OriginalPrefabName>_Arena.prefab

Example:

Source:
Assets/ThirdParty/AssetStore/Environments/ToonGoldenValley/Prefabs/Rocks/Rock_01.prefab

Variant:
Assets/Arena/Content/Prefabs/OpenWorld/EnvironmentVariants/ToonGoldenValley/Rocks/Rock_01_Arena.prefab

Included packages:

ToonAdventureIsland
ToonDesertedTemples
ToonEnchantedMeadow
ToonGoldenValley

Ignored packages:

Raygeas
FantasticDungeonPack
FantasticDungeonPackPackage
