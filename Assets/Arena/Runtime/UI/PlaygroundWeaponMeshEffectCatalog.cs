#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.UI
{
    [CreateAssetMenu(menuName = "Arena/Playground/Weapon Mesh Effect Catalog")]
    public sealed class PlaygroundWeaponMeshEffectCatalog : ScriptableObject
    {
        public const string DefaultResourcePath = "Playground/WeaponMeshEffectCatalog";

        [Serializable]
        public sealed class Entry
        {
            public string label = string.Empty;
            public GameObject? prefab;
        }

        [SerializeField] private List<Entry> entries = new();

        public IReadOnlyList<Entry> Entries => entries;
    }
}
