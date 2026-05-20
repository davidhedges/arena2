#nullable enable
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Match
{
    /// <summary>
    /// Simulation-layer cache of arena match state: phase, winner, and mode.
    ///
    /// Populated by NetworkManager via ArenaInstance table callbacks.
    /// Read by MatchController and MatchOverlay — no table access needed there.
    ///
    /// IsArenaMode only becomes true once we've received the ArenaInstance row
    /// AND confirmed it is not flagged as a practice instance. This prevents
    /// misclassifying a practice session as an arena match during the window
    /// between joining and subscription settle.
    ///
    /// INVARIANT: Only written by table event callbacks. Only read by presentation.
    /// </summary>
    public class MatchStateCache
    {
        public static MatchStateCache Instance { get; } = new();

        private MatchPhase _serverPhase = MatchPhase.Waiting;
        private Identity? _serverWinnerId;
        private bool _clientFallbackEnded;
        private Identity? _clientFallbackWinnerId;

        public MatchPhase Phase
            => _clientFallbackEnded && _serverPhase != MatchPhase.Ended
                ? MatchPhase.Ended
                : _serverPhase;

        public Identity? WinnerId
            => _serverWinnerId ?? (_clientFallbackEnded ? _clientFallbackWinnerId : null);

        public ulong? LocalInstanceId { get; private set; }

        private bool _hasInstanceData;
        private bool _isPractice;

        public bool IsArenaMode   => _hasInstanceData && !_isPractice;
        public bool IsPracticeMode => _hasInstanceData && _isPractice;
        public bool IsCountdown   => Phase == MatchPhase.Countdown;
        public bool IsInProgress  => Phase == MatchPhase.InProgress;
        public bool IsEnded       => Phase == MatchPhase.Ended;

        public System.DateTime? CountdownStartedAt { get; private set; }

        // Called from EntityRegistry when the local player's PlayerWorld row changes.
        public void OnLocalPlayerWorldUpdate(ulong? instanceId)
        {
            if (instanceId != LocalInstanceId)
            {
                // Moved to a different instance/world — clear cached match state immediately.
                Reset();
            }
            LocalInstanceId = instanceId;
        }

        internal void ResetForNetworkReconnect()
        {
            LocalInstanceId = null;
            Reset();
        }

        // ArenaInstance callbacks — wired by NetworkManager.

        public void OnArenaInstanceInsert(EventContext ctx, ArenaInstance row)
            => ApplyInstance(row);

        public void OnArenaInstanceUpdate(EventContext ctx, ArenaInstance old, ArenaInstance row)
            => ApplyInstance(row);

        public void OnArenaInstanceDelete(EventContext ctx, ArenaInstance row)
        {
            if (row.Id == LocalInstanceId)
                Reset();
        }

        private void ApplyInstance(ArenaInstance row)
        {
            if (row.Id != LocalInstanceId) return;
            _hasInstanceData = true;
            _isPractice = row.IsPractice;
            _serverPhase = ParsePhase(row.Phase);
            _serverWinnerId = row.WinnerId;
            CountdownStartedAt = row.CountdownStartedAt.HasValue
                ? System.DateTimeOffset.FromUnixTimeMilliseconds(
                    row.CountdownStartedAt.Value.MicrosecondsSinceUnixEpoch / 1000).UtcDateTime
                : (System.DateTime?)null;

            if (_serverPhase == MatchPhase.Ended)
            {
                _clientFallbackEnded = false;
                _clientFallbackWinnerId = null;
            }
        }

        private void Reset()
        {
            _serverPhase     = MatchPhase.Waiting;
            _serverWinnerId  = null;
            _clientFallbackEnded = false;
            _clientFallbackWinnerId = null;
            _hasInstanceData = false;
            _isPractice      = false;
            CountdownStartedAt = null;
        }

        public void SetClientFallbackEnded(Identity? winnerId)
        {
            if (_serverPhase == MatchPhase.Ended)
                return;

            _clientFallbackEnded = true;
            _clientFallbackWinnerId = winnerId;
        }

        public void ClearClientFallbackEnded()
        {
            if (_serverPhase == MatchPhase.Ended)
                return;

            _clientFallbackEnded = false;
            _clientFallbackWinnerId = null;
        }

        private static MatchPhase ParsePhase(string phase)
        {
            if (string.Equals(phase, "COUNTDOWN", System.StringComparison.OrdinalIgnoreCase))
                return MatchPhase.Countdown;
            if (string.Equals(phase, "IN_PROGRESS", System.StringComparison.OrdinalIgnoreCase))
                return MatchPhase.InProgress;
            if (string.Equals(phase, "ENDED", System.StringComparison.OrdinalIgnoreCase))
                return MatchPhase.Ended;
            return MatchPhase.Waiting;
        }
    }
}
