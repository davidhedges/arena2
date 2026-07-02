#nullable enable

namespace Arena.UI
{
    public enum ConnectionQualityLevel
    {
        Good,
        Degraded,
        Bad,
    }

    /// <summary>
    /// Pure RTT/staleness → quality classification for the always-on
    /// connection dot (feel audit F2 contract item 4). Driven only by data
    /// the client already collects: <c>ArenaServerClock</c> precise-RTT
    /// percentiles and how long ago the last SpacetimeDB row arrived
    /// (derived from <c>NetcodeReceiveCounters.TotalRows</c>). Thresholds are
    /// calibrated against docs/latency-testing.md: local dev reads Good,
    /// Profile A (~100 ms added RTT) reads Degraded, Profile B (~200 ms)
    /// reads Bad. Gameplay reads nothing from this.
    /// </summary>
    public static class ConnectionQualityModel
    {
        public const long DegradedRttP50Ms = 80;
        public const long DegradedRttP95Ms = 180;
        public const long BadRttP50Ms = 180;
        public const long BadRttP95Ms = 350;
        public const double DegradedRowStalenessSeconds = 1.5;
        public const double BadRowStalenessSeconds = 4.0;

        public static ConnectionQualityLevel Classify(
            bool hasRttStats,
            long rttP50Ms,
            long rttP95Ms,
            double rowStalenessSeconds)
        {
            if (rowStalenessSeconds >= BadRowStalenessSeconds)
                return ConnectionQualityLevel.Bad;
            if (hasRttStats && (rttP50Ms >= BadRttP50Ms || rttP95Ms >= BadRttP95Ms))
                return ConnectionQualityLevel.Bad;
            if (rowStalenessSeconds >= DegradedRowStalenessSeconds)
                return ConnectionQualityLevel.Degraded;
            if (hasRttStats && (rttP50Ms >= DegradedRttP50Ms || rttP95Ms >= DegradedRttP95Ms))
                return ConnectionQualityLevel.Degraded;
            return ConnectionQualityLevel.Good;
        }
    }
}
