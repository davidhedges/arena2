using System;
using System.Collections.Generic;
using System.Linq;
using NHance.Assets.Scripts.Enums;
using UnityEngine;

namespace NHance.Assets.Scripts
{
    [Serializable]
    public class GameObjectByItemTypeWrapper
    {
        public List<ItemCache> _cache = new List<ItemCache>();

        public GameObject this[ItemTypeEnum key]
        {
            get
            {
                return _cache.FirstOrDefault(c => c.Type == key)?.Item;
            }
            set
            {
                Remove(key);
                _cache.Add(new ItemCache(value, key));
            }
        }

        public void Remove(ItemTypeEnum type)
        {
            var prev = _cache.FirstOrDefault(c => c.Type == type);
            if (prev != null)
                _cache.Remove(prev);
        }

        public void Clear()
        {
            foreach (var itemCache in _cache)
                DestroyCachedItem(itemCache.Item);
            _cache.Clear();
        }

        private static void DestroyCachedItem(GameObject item)
        {
            if (item == null)
                return;

            if (Application.isPlaying)
                GameObject.Destroy(item);
            else
                GameObject.DestroyImmediate(item);
        }
    }
}
