#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Entity
{
    /// <summary>
    /// Loads one NPC visual profile (and therefore one prefab dependency graph)
    /// at a time. Zero-lease profiles stay warm until the registry reaches an
    /// idle boundary, avoiding load/unload churn between nearby spawns.
    /// </summary>
    internal sealed class NpcVisualResourceCache
    {
        internal const string ProfileResourceFolder = "NpcVisualProfiles";

        private readonly Dictionary<string, Entry> _entries =
            new(StringComparer.Ordinal);

        internal sealed class Lease : IDisposable
        {
            private NpcVisualResourceCache? _owner;
            private readonly string _visualId;

            internal Lease(
                NpcVisualResourceCache owner,
                string visualId,
                NpcVisualProfile profile)
            {
                _owner = owner;
                _visualId = visualId;
                Profile = profile;
            }

            internal NpcVisualProfile Profile { get; }

            public void Dispose()
            {
                NpcVisualResourceCache? owner = _owner;
                if (owner == null)
                    return;

                _owner = null;
                owner.Release(_visualId);
            }
        }

        private sealed class Entry
        {
            internal readonly ResourceRequest Request;
            internal NpcVisualProfile? Profile;
            internal int LeaseCount;

            internal Entry(ResourceRequest request)
            {
                Request = request;
            }
        }

        internal bool TryBeginLoad(
            string visualId,
            out string normalizedVisualId,
            out ResourceRequest request,
            out string error)
        {
            normalizedVisualId = NormalizeVisualId(visualId);
            if (!IsSafeVisualId(normalizedVisualId))
            {
                request = null!;
                error = $"NPC visual ID '{visualId}' is not a safe resource key.";
                return false;
            }

            if (!_entries.TryGetValue(normalizedVisualId, out Entry? entry))
            {
                request = Resources.LoadAsync<NpcVisualProfile>(
                    ResourcePathFor(normalizedVisualId));
                entry = new Entry(request);
                _entries.Add(normalizedVisualId, entry);
            }

            request = entry.Request;
            error = string.Empty;
            return true;
        }

        internal bool TryAcquireCompleted(
            string normalizedVisualId,
            out Lease lease,
            out string error)
        {
            if (!_entries.TryGetValue(normalizedVisualId, out Entry? entry))
            {
                lease = null!;
                error = $"NPC visual '{normalizedVisualId}' was not requested.";
                return false;
            }

            if (!entry.Request.isDone)
            {
                lease = null!;
                error = $"NPC visual '{normalizedVisualId}' is still loading.";
                return false;
            }

            entry.Profile ??= entry.Request.asset as NpcVisualProfile;
            if (entry.Profile == null)
            {
                lease = null!;
                error = $"Resources/{ResourcePathFor(normalizedVisualId)} was not found.";
                return false;
            }

            if (entry.Profile.Prefab == null)
            {
                lease = null!;
                error = $"NPC visual profile '{entry.Profile.name}' has no prefab.";
                return false;
            }

            entry.LeaseCount++;
            lease = new Lease(this, normalizedVisualId, entry.Profile);
            error = string.Empty;
            return true;
        }

        internal void ReleaseUnusedProfiles()
        {
            var released = new List<string>();
            foreach ((string visualId, Entry entry) in _entries)
            {
                if (entry.LeaseCount != 0 || !entry.Request.isDone)
                    continue;

                entry.Profile ??= entry.Request.asset as NpcVisualProfile;
                if (entry.Profile != null)
                    Resources.UnloadAsset(entry.Profile);
                released.Add(visualId);
            }

            foreach (string visualId in released)
                _entries.Remove(visualId);
        }

        internal static string ResourcePathFor(string visualId)
            => $"{ProfileResourceFolder}/{NormalizeVisualId(visualId)}";

        private void Release(string visualId)
        {
            if (_entries.TryGetValue(visualId, out Entry? entry))
                entry.LeaseCount = Math.Max(0, entry.LeaseCount - 1);
        }

        private static string NormalizeVisualId(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();

        private static bool IsSafeVisualId(string visualId)
        {
            if (string.IsNullOrEmpty(visualId))
                return false;

            foreach (char character in visualId)
            {
                if ((character < 'A' || character > 'Z')
                    && (character < '0' || character > '9')
                    && character != '_')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
