#nullable enable

namespace Arena.Input
{
    /// <summary>
    /// Shared client-side movement/netcode constants for the migration away from
    /// ad-hoc timing and unbounded pending input state.
    /// </summary>
    public static class MovementNetcodeConfig
    {
        public const int FixedTickMilliseconds = 33;
        public const float FixedTickSeconds = FixedTickMilliseconds / 1000.0f;
        // Lead ticks are tuned for local combat feel, while the remaining
        // tick-count budgets preserve wall-clock safety envelopes.
        public const int DesiredServerInputLeadTicks = 2;
        public const int RemoteDesiredServerInputLeadTicks = 8;
        public const int MaxLocalPredictionTicksPerFrame = 5;
        public const int MaxTicksToSendPerFrame = 5;

        // Bounded pending-input history until full rewind/replay replaces this scaffold.
        public const int MaxPendingCommands = 96;

        // Maximum number of unacknowledged commands before triggering an emergency re-sync.
        // At 33ms/tick this gives 396ms of headroom — enough for ~200ms RTT with margin.
        // If pending exceeds this, the replay buffer is cleared and the client re-anchors
        // to the server's current tick. The predicted position will briefly snap to the
        // server position on the next reconcile, then prediction resumes normally.
        public const int MaxPredictionLeadTicks = 12;
    }
}
