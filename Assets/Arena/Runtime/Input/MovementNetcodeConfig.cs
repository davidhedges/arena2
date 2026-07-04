#nullable enable

namespace Arena.Input
{
    /// <summary>
    /// Shared client-side movement/netcode constants.
    /// S5 (design review §4): the input lead is no longer a static constant —
    /// <see cref="InputLeadController"/> runs a closed loop against the
    /// server's per-tick consume feedback. The values here bound and pace
    /// that loop; none of them is "the lead".
    /// </summary>
    public static class MovementNetcodeConfig
    {
        public const int FixedTickMilliseconds = 33;
        public const float FixedTickSeconds = FixedTickMilliseconds / 1000.0f;
        public const int MaxLocalPredictionTicksPerFrame = 5;
        public const int MaxTicksToSendPerFrame = 5;

        // Bounded pending-input history until full rewind/replay replaces this scaffold.
        public const int MaxPendingCommands = 96;

        // Hard bound on prediction ahead of the last authoritative ack. The
        // S5 degradation ladder responds to overrun by throttling input
        // production (authoring already stops at this bound); a hard resync
        // is the LAST rung, reached only when the overrun is sustained
        // (acks stopped entirely) — not the first response.
        public const int MaxPredictionLeadTicks = 12;

        // --- S5 closed-loop input lead ---
        // Where the loop starts before feedback arrives (loopback converges
        // near here), and the bounds it may steer within. Max stays under
        // MaxPredictionLeadTicks so the loop never rides the resync bound.
        public const int InitialInputLeadTicks = 2;
        public const int MinInputLeadTicks = 1;
        public const int MaxInputLeadTicks = 10;

        // Server buffer occupancy setpoint (buffered commands remaining after
        // each consume). Below Low → raise lead; above High sustained → lower.
        public const int BufferOccupancySetpointLow = 1;
        public const int BufferOccupancySetpointHigh = 2;

        // Asymmetric pacing: starvation raises immediately (rate-limited so a
        // burst of stale acks after a stall counts once), surplus lowers only
        // after it has persisted.
        public const float LeadRaiseHoldoffSeconds = 0.25f;
        public const float LeadLowerAfterSurplusSeconds = 5.0f;

        // Actuation is the target-chasing authoring clock itself (author tick
        // N when estimate + lead crosses N; pause automatically while ahead),
        // bounded by MaxLocalPredictionTicksPerFrame — no separate inject or
        // skip rate constants. The first S5 cut rate-capped skips at 2/s,
        // which could not drain the ~3-tick/s surplus a 36.6 ms real server
        // cadence produced against 33 ms wall-clock authoring (occupancy
        // pinned at the prediction bound; acceptance session 2026-07-04).

        // Ladder rung 3: hard resync fires only when pending commands exceed
        // MaxPredictionLeadTicks continuously for this long (acks stalled).
        public const float HardResyncSustainedOverrunSeconds = 3.0f;

        // Mirror of the server's post-special-movement input discard window
        // (SPECIAL_MOVEMENT_INPUT_DISCARD_LEAD_TICKS in game_loop.rs): the
        // re-anchor after special movement must clear it.
        public const int SpecialMovementInputDiscardLeadTicks = 4;
    }
}
