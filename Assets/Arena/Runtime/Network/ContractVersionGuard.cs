#nullable enable
using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Network
{
    /// <summary>
    /// Netcode audit R5: validates the client's bundled shared-JSON copies
    /// against the content stamps the server writes to ContractVersion at
    /// publish. The shared collision/heightfield data must match exactly for
    /// movement parity; a mismatch means shared data was edited without
    /// running Arena &gt; OpenWorld &gt; Sync Shared Movement Data, or the
    /// client is running against a stale module.
    /// </summary>
    internal static class ContractVersionGuard
    {
        private const ulong FnvOffset = 0xcbf29ce484222325UL;
        private const ulong FnvPrime = 0x100000001b3UL;

        internal static void Validate(RemoteTables db)
        {
            int verified = 0;
            int mismatches = 0;
            foreach ((string serverKey, TextAsset asset) in ClientSharedFiles())
            {
                ContractVersion? row = db.ContractVersion.Key.Find(serverKey);
                if (row == null)
                {
                    Debug.LogWarning(
                        $"[ContractVersion] server has no stamp for '{serverKey}' — the module predates this client's shared data.");
                    continue;
                }

                ulong clientHash = SharedContentHash(asset.bytes);
                if (clientHash != row.ContentHash)
                {
                    mismatches++;
                    Debug.LogError(
                        $"[ContractVersion] shared data drift for '{serverKey}': client {clientHash:x16} != server {row.ContentHash:x16}. "
                        + "Run Arena > OpenWorld > Sync Shared Movement Data and republish the module.");
                }
                else
                {
                    verified++;
                }
            }

            if (mismatches == 0)
                Debug.Log($"[ContractVersion] {verified} shared data stamps verified.");
        }

        /// <summary>
        /// FNV-1a over the file bytes, skipping CR so checkouts with
        /// different line endings hash identically. Mirrors
        /// shared_content_hash in server/src/contract_version.rs — keep the
        /// two implementations in sync.
        /// </summary>
        private static ulong SharedContentHash(byte[] bytes)
        {
            ulong hash = FnvOffset;
            foreach (byte value in bytes)
            {
                if (value == (byte)'\r')
                    continue;
                hash ^= value;
                hash *= FnvPrime;
            }

            return hash;
        }

        private static IEnumerable<(string serverKey, TextAsset asset)> ClientSharedFiles()
        {
            // TextAsset names have the ".json" extension stripped; server
            // keys are src-relative paths ("world_data/x.shared.json").
            var seen = new HashSet<TextAsset>();
            foreach (TextAsset asset in Resources.LoadAll<TextAsset>("SharedData/Worlds"))
            {
                seen.Add(asset);
                yield return ($"world_data/{asset.name}.json", asset);
            }

            foreach (TextAsset asset in Resources.LoadAll<TextAsset>("SharedData"))
            {
                if (seen.Add(asset))
                    yield return ($"{asset.name}.json", asset);
            }
        }
    }
}
