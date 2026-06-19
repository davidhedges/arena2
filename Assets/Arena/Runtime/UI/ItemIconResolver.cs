#nullable enable

using System;
using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.UI
{
    public static class ItemIconResolver
    {
        private const string ResourceRoot = "UI/ItemIcons";
        private static readonly Dictionary<string, Sprite?> Cache = new(StringComparer.Ordinal);

        public static Sprite? Resolve(ItemDefinition? definition)
        {
            return definition == null ? null : Resolve(definition.IconId);
        }

        public static Sprite? Resolve(string iconId)
        {
            string normalizedId = iconId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
                return null;

            if (Cache.TryGetValue(normalizedId, out Sprite? cached))
                return cached;

            Sprite? loaded = Resources.Load<Sprite>($"{ResourceRoot}/{normalizedId}");
            Cache[normalizedId] = loaded;
            return loaded;
        }
    }
}
