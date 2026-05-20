#nullable enable
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    public sealed class CharacterAppearanceCatalogSet
    {
        private const string BaseCatalogResource = "CharacterAppearance/AvatarBaseCatalog";
        private const string PartCatalogResource = "CharacterAppearance/AvatarPartCatalog";
        private const string OutfitCatalogResource = "CharacterAppearance/OutfitCatalog";
        private const string ClassOutfitCatalogResource = "CharacterAppearance/ClassOutfitCatalog";

        public CharacterAppearanceCatalogSet(
            AvatarBaseCatalog baseCatalog,
            AvatarPartCatalog partCatalog,
            OutfitCatalog outfitCatalog,
            ClassOutfitCatalog classOutfitCatalog)
        {
            BaseCatalog = baseCatalog;
            PartCatalog = partCatalog;
            OutfitCatalog = outfitCatalog;
            ClassOutfitCatalog = classOutfitCatalog;
        }

        public AvatarBaseCatalog BaseCatalog { get; }
        public AvatarPartCatalog PartCatalog { get; }
        public OutfitCatalog OutfitCatalog { get; }
        public ClassOutfitCatalog ClassOutfitCatalog { get; }

        public static bool TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out string error)
        {
            AvatarBaseCatalog? baseCatalog = Resources.Load<AvatarBaseCatalog>(BaseCatalogResource);
            AvatarPartCatalog? partCatalog = Resources.Load<AvatarPartCatalog>(PartCatalogResource);
            OutfitCatalog? outfitCatalog = Resources.Load<OutfitCatalog>(OutfitCatalogResource);
            ClassOutfitCatalog? classOutfitCatalog = Resources.Load<ClassOutfitCatalog>(ClassOutfitCatalogResource);

            if (baseCatalog == null || partCatalog == null || outfitCatalog == null || classOutfitCatalog == null)
            {
                catalogs = null!;
                error =
                    "Missing one or more CharacterAppearance catalog resources. " +
                    "Run Arena/Appearance/Rebuild Default Catalog Assets.";
                return false;
            }

            catalogs = new CharacterAppearanceCatalogSet(baseCatalog, partCatalog, outfitCatalog, classOutfitCatalog);
            error = string.Empty;
            return true;
        }
    }
}
