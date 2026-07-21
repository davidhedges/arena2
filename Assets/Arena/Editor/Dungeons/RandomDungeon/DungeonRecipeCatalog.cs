using System;
using UnityEngine;

namespace DungeonLab.Editor
{
    [CreateAssetMenu(fileName = "dungeon_recipe_catalog", menuName = "Dungeon Lab/Recipe Catalog")]
    public sealed class DungeonRecipeCatalog : ScriptableObject
    {
        public int schemaVersion = DungeonRecipeAsset.CurrentSchemaVersion;
        public DungeonRecipeAsset[] recipes = Array.Empty<DungeonRecipeAsset>();
    }
}
