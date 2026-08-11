#nullable enable
using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;
using Arena.World;

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

        internal readonly struct ValidationResult
        {
            internal ValidationResult(int verified, int missing, int mismatches)
            {
                Verified = verified;
                Missing = missing;
                Mismatches = mismatches;
            }

            internal int Verified { get; }
            internal int Missing { get; }
            internal int Mismatches { get; }
            internal bool IsCompatible => Verified > 0 && Missing == 0 && Mismatches == 0;

            internal string FailureMessage
            {
                get
                {
                    if (Verified == 0 && Missing == 0 && Mismatches == 0)
                        return "No bundled shared-data contracts were available to verify.";

                    return $"Shared-data contract validation failed: {Mismatches} mismatched, "
                           + $"{Missing} missing, {Verified} verified.";
                }
            }
        }

        internal static ValidationResult Validate(RemoteTables db)
            => ValidateFiles(db, ClientSharedFiles());

        internal static ValidationResult ValidatePvpMatch(RemoteTables db)
            => ValidateFiles(db, ClientPvpSharedFiles());

        private static ValidationResult ValidateFiles(
            RemoteTables db,
            IEnumerable<(string serverKey, TextAsset? asset)> files)
        {
            int verified = 0;
            int missing = 0;
            int mismatches = 0;
            foreach ((string serverKey, TextAsset? asset) in files)
            {
                if (asset == null)
                {
                    missing++;
                    Debug.LogError(
                        $"[ContractVersion] client has no bundled shared data for '{serverKey}'.");
                    continue;
                }

                ContractVersion? row = db.ContractVersion.Key.Find(serverKey);
                if (row == null)
                {
                    missing++;
                    Debug.LogError(
                        $"[ContractVersion] server has no stamp for '{serverKey}'. "
                        + "The client cannot safely predict against this module.");
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

            var result = new ValidationResult(verified, missing, mismatches);
            if (result.IsCompatible)
                Debug.Log($"[ContractVersion] {verified} shared data stamps verified.");

            return result;
        }

        private static IEnumerable<(string serverKey, TextAsset? asset)> ClientPvpSharedFiles()
        {
            // Load these files directly. Enumerating SharedData/Worlds would
            // deserialize roughly 179 MB of open-world and dungeon assets that
            // the lean PvP server neither embeds nor uses.
            yield return (
                "map_data/arena_map_01.layout.shared.json",
                Resources.Load<TextAsset>(ArenaMapCatalog.ArenaMap01LayoutResourcePath));
            yield return (
                "map_data/arena_map_01.collision.shared.json",
                Resources.Load<TextAsset>(ArenaMapCatalog.ArenaMap01MovementCollisionResourcePath));
            yield return (
                "map_data/arena_map_01.query_collision.shared.json",
                Resources.Load<TextAsset>(ArenaMapCatalog.ArenaMap01QueryCollisionResourcePath));
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

        private static IEnumerable<(string serverKey, TextAsset? asset)> ClientSharedFiles()
        {
            // TextAsset names have the ".json" extension stripped; server
            // keys are src-relative paths ("world_data/x.shared.json").
            var seen = new HashSet<TextAsset>();
            foreach (TextAsset asset in Resources.LoadAll<TextAsset>("SharedData/Worlds"))
            {
                seen.Add(asset);
                yield return ($"world_data/{asset.name}.json", asset);
            }

            foreach (TextAsset asset in Resources.LoadAll<TextAsset>(
                         "SharedData/WorldInteractions"))
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
