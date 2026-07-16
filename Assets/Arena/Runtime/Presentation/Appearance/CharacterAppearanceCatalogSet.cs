#nullable enable
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    public sealed class CharacterAppearanceCatalogSet
    {
        private const string BaseCatalogResource = "CharacterAppearance/AvatarBaseCatalog";
        private const string PartCatalogResource = "CharacterAppearance/AvatarPartCatalog";
        private const string OutfitCatalogResource = "CharacterAppearance/OutfitCatalog";
        private const string EquipmentAppearanceCatalogResource = "CharacterAppearance/EquipmentAppearanceCatalog";
        private static CharacterAppearanceCatalogSet? _cachedDefault;

        public CharacterAppearanceCatalogSet(
            AvatarBaseCatalog baseCatalog,
            AvatarPartCatalog partCatalog,
            OutfitCatalog outfitCatalog,
            EquipmentAppearanceCatalog? equipmentAppearanceCatalog = null)
        {
            BaseCatalog = baseCatalog;
            PartCatalog = partCatalog;
            OutfitCatalog = outfitCatalog;
            EquipmentAppearanceCatalog = equipmentAppearanceCatalog;
        }

        public AvatarBaseCatalog BaseCatalog { get; }
        public AvatarPartCatalog PartCatalog { get; }
        public OutfitCatalog OutfitCatalog { get; }
        public EquipmentAppearanceCatalog? EquipmentAppearanceCatalog { get; }

        public static bool TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out string error)
        {
            if (_cachedDefault != null
                && _cachedDefault.BaseCatalog != null
                && _cachedDefault.PartCatalog != null
                && _cachedDefault.OutfitCatalog != null)
            {
                catalogs = _cachedDefault;
                error = string.Empty;
                return true;
            }

            AvatarBaseCatalog? baseCatalog = Resources.Load<AvatarBaseCatalog>(BaseCatalogResource);
            AvatarPartCatalog? partCatalog = Resources.Load<AvatarPartCatalog>(PartCatalogResource);
            OutfitCatalog? outfitCatalog = Resources.Load<OutfitCatalog>(OutfitCatalogResource);
            EquipmentAppearanceCatalog? equipmentAppearanceCatalog =
                Resources.Load<EquipmentAppearanceCatalog>(EquipmentAppearanceCatalogResource);

            if (baseCatalog == null || partCatalog == null || outfitCatalog == null)
            {
                catalogs = null!;
                error =
                    "Missing one or more CharacterAppearance catalog resources. " +
                    "Run Arena/Appearance/Rebuild Default Catalog Assets.";
                return false;
            }

            _cachedDefault = new CharacterAppearanceCatalogSet(
                baseCatalog,
                partCatalog,
                outfitCatalog,
                equipmentAppearanceCatalog);
            catalogs = _cachedDefault;
            error = string.Empty;
            return true;
        }
    }
}
