#nullable enable

using System;
using System.Linq;

namespace Arena.Combat
{
    internal static class AbilityTagCodec
    {
        internal static bool HasTag(string? encodedTags, string expectedTag)
        {
            string normalizedExpectedTag = WireIdentifier.Normalize(expectedTag);
            if (string.IsNullOrEmpty(normalizedExpectedTag))
                return false;

            return (encodedTags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(tag => string.Equals(
                    WireIdentifier.Normalize(tag),
                    normalizedExpectedTag,
                    StringComparison.Ordinal));
        }
    }
}
