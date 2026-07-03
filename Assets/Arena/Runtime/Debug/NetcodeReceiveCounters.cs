#nullable enable
using System.Collections.Generic;
using SpacetimeDB.Types;

namespace Arena.Debugging
{
    /// <summary>
    /// Overlay-only counters for replicated row callbacks (netcode audit R3,
    /// client half). NetworkCallbackBinder records one hit per table row
    /// callback; NetcodeDebugOverlay renders rows/sec over a rolling window.
    ///
    /// No gameplay system may read these — measurement only. Sanctioned
    /// readers: ConnectionStatusHud (staleness dot) and RowDeliveryFresh
    /// below (RemotePresentationBuffer's settled-vs-starved sample
    /// classification, design review S1); neither feeds back into
    /// presentation or simulation behavior.
    /// </summary>
    public static class NetcodeReceiveCounters
    {
        public const float WindowSeconds = 5.0f;

        /// <summary>
        /// Freshness window for RowDeliveryFresh — the same ~250 ms scale
        /// ConnectionStatusHud derives its staleness classification from.
        /// </summary>
        public const float RowFlowFreshWindowSeconds = 0.25f;

        private static readonly Dictionary<string, long> _totalRowsByTable = new(System.StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _windowRowsByTable = new(System.StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _lastWindowRatesByTable = new(System.StringComparer.Ordinal);
        private static readonly Dictionary<ActionResultKind, long> _predictedResultsByKind = new();
        private static float _windowStartedAtRealtime = -1.0f;
        private static long _totalRows;
        private static long _flowObservedTotalRows = -1L;
        private static float _flowLastChangeRealtime = float.NegativeInfinity;

        public static long TotalRows => _totalRows;
        public static IReadOnlyDictionary<string, long> TotalRowsByTable => _totalRowsByTable;
        public static IReadOnlyDictionary<ActionResultKind, long> PredictedResultsByKind => _predictedResultsByKind;
        public static string LastRejection { get; private set; } = string.Empty;

        public static void Record(string table)
        {
            _totalRows++;
            _totalRowsByTable.TryGetValue(table, out long total);
            _totalRowsByTable[table] = total + 1;
            _windowRowsByTable.TryGetValue(table, out long window);
            _windowRowsByTable[table] = window + 1;
        }

        public static void RecordPredictedActionResult(PredictedActionResult row)
        {
            _predictedResultsByKind.TryGetValue(row.Result, out long count);
            _predictedResultsByKind[row.Result] = count + 1;
            if (row.Result == ActionResultKind.Rejected)
                LastRejection = $"{row.Family}:{row.RejectReason}";
        }

        public static void ResetForNetworkReconnect()
        {
            _totalRowsByTable.Clear();
            _windowRowsByTable.Clear();
            _lastWindowRatesByTable.Clear();
            _predictedResultsByKind.Clear();
            LastRejection = string.Empty;
            _windowStartedAtRealtime = -1.0f;
            _totalRows = 0;
            _flowObservedTotalRows = -1L;
            _flowLastChangeRealtime = float.NegativeInfinity;
        }

        /// <summary>
        /// True while any replicated row arrived within the last
        /// RowFlowFreshWindowSeconds — the global-delivery-health input to
        /// RemotePresentationBuffer.ClassifySample (an idle NPC past the
        /// extrapolation cap is settled while this holds, starved once it
        /// stalls). Observer state advances on read; call from the main
        /// thread with Time.realtimeSinceStartup.
        /// </summary>
        public static bool RowDeliveryFresh(float nowRealtime)
        {
            if (_totalRows != _flowObservedTotalRows)
            {
                _flowObservedTotalRows = _totalRows;
                _flowLastChangeRealtime = nowRealtime;
            }

            return _totalRows > 0
                && nowRealtime - _flowLastChangeRealtime <= RowFlowFreshWindowSeconds;
        }

        /// <summary>
        /// Rows/sec by table for the most recently completed window. Flushes
        /// the current window when it has elapsed; call from the main thread.
        /// </summary>
        public static IReadOnlyDictionary<string, float> WindowRates(float nowRealtime)
        {
            if (_windowStartedAtRealtime < 0.0f)
                _windowStartedAtRealtime = nowRealtime;

            float elapsed = nowRealtime - _windowStartedAtRealtime;
            if (elapsed >= WindowSeconds)
            {
                _lastWindowRatesByTable.Clear();
                foreach (var pair in _windowRowsByTable)
                    _lastWindowRatesByTable[pair.Key] = pair.Value / elapsed;
                _windowRowsByTable.Clear();
                _windowStartedAtRealtime = nowRealtime;
            }

            return _lastWindowRatesByTable;
        }
    }
}
