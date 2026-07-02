#nullable enable
using SpacetimeDB;

namespace Arena.Network
{
    /// <summary>
    /// Canonical string form of an identity for the server's owner-key
    /// columns (inventory_container.owner_key,
    /// item_instance.current_owner_key). The server writes identity_key() =
    /// identity.to_hex(), which is lowercase hex; C# Identity.ToString()
    /// returns uppercase, and subscription SQL string equality is
    /// case-sensitive — a raw ToString() filter silently matches zero rows.
    /// Always build owner-key subscription filters through this helper.
    /// </summary>
    public static class OwnerKeys
    {
        public static string For(Identity identity)
            => identity.ToString().ToLowerInvariant();
    }
}
