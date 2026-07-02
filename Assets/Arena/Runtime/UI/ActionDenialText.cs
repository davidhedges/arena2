#nullable enable

using SpacetimeDB.Types;

namespace Arena.UI
{
    /// <summary>
    /// Player-facing display text for server action rejections (feel audit F1
    /// step 4). The server's machine-readable <see cref="ActionRejectReason"/>
    /// (netcode audit R2) is the single source of truth — the client renders
    /// it verbatim and performs no validation of its own.
    /// </summary>
    public static class ActionDenialText
    {
        public static string For(ActionRejectReason reason) => reason switch
        {
            ActionRejectReason.None => "Action failed",
            ActionRejectReason.Unspecified => "Action failed",
            ActionRejectReason.InvalidInput => "Can't do that",
            ActionRejectReason.Dead => "You are dead",
            ActionRejectReason.Disabled => "You are disabled",
            ActionRejectReason.Busy => "Busy with another action",
            ActionRejectReason.OnCooldown => "On cooldown",
            ActionRejectReason.OnGlobalCooldown => "Not ready yet",
            ActionRejectReason.InsufficientResource => "Not enough resource",
            ActionRejectReason.NoCharges => "No charges left",
            ActionRejectReason.InvalidTarget => "Invalid target",
            ActionRejectReason.OutOfRange => "Out of range",
            ActionRejectReason.NotFacingTarget => "Not facing target",
            ActionRejectReason.AerialMismatch => "Airborne restriction",
            ActionRejectReason.LineOfSightBlocked => "No line of sight",
            ActionRejectReason.MovementRestricted => "Movement restricted",
            ActionRejectReason.GapCloseBlocked => "Path blocked",
            ActionRejectReason.ComboWindow => "Combo not ready",
            ActionRejectReason.StaleSnapshot => "Action failed",
            ActionRejectReason.NotCancelable => "Can't cancel that",
            _ => "Action failed",
        };
    }
}
