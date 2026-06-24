#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    public readonly struct EquippedWeaponVisual
    {
        public EquippedWeaponVisual(string roleId, string itemDefId, GameObject prefab)
        {
            RoleId = roleId;
            ItemDefId = itemDefId;
            Prefab = prefab;
        }

        public string RoleId { get; }
        public string ItemDefId { get; }
        public GameObject Prefab { get; }
    }
}
