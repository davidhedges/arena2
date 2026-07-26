#nullable enable

using System;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Simulation
{
    /// <summary>
    /// Simulation-layer view of the local actor's authoritative timed world
    /// interaction. Target state remains in its concrete replicated table.
    /// </summary>
    public sealed class LocalInteractionState : ITimedActionPresentationSource
    {
        public static LocalInteractionState Instance { get; } = new();

        private Identity _localIdentity;
        private bool _bound;
        private ActiveWorldInteraction? _active;

        public ActiveWorldInteraction? Active => _active;

        public static event Action<string>? InteractionDenied;

        public void Bind(Identity localIdentity)
        {
            _localIdentity = localIdentity;
            _bound = true;
        }

        internal void ResetForNetworkReconnect()
        {
            _localIdentity = default;
            _bound = false;
            _active = null;
        }

        internal void ResetForTests() => ResetForNetworkReconnect();

        public void OnActiveWorldInteractionInsert(
            EventContext context,
            ActiveWorldInteraction row)
        {
            _ = context;
            Apply(row);
        }

        public void OnActiveWorldInteractionUpdate(
            EventContext context,
            ActiveWorldInteraction oldRow,
            ActiveWorldInteraction row)
        {
            _ = context;
            _ = oldRow;
            Apply(row);
        }

        public void OnActiveWorldInteractionDelete(
            EventContext context,
            ActiveWorldInteraction row)
        {
            _ = context;
            if (!_bound
                || row.Actor != _localIdentity
                || !string.Equals(
                    _active?.ActionInstanceId,
                    row.ActionInstanceId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _active = null;
        }

        public TimedActionPresentationSnapshot? CurrentTimedAction(long nowMs)
        {
            ActiveWorldInteraction? row = _active;
            if (row == null)
                return null;

            long startMs = row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L;
            long endMs = row.CompletesAt.MicrosecondsSinceUnixEpoch / 1000L;
            if (endMs <= startMs || nowMs >= endMs)
                return null;

            return new TimedActionPresentationSnapshot(
                row.ActionInstanceId,
                startMs,
                endMs,
                TimedActionPresentation.DisplayLabelFromKey(
                    row.ProgressLabelKey,
                    row.Verb),
                TimedActionPresentationStyle.WorldInteraction);
        }

        internal static void ReportDenial(string reason)
        {
            string message = string.IsNullOrWhiteSpace(reason)
                ? "Interaction failed"
                : reason.Trim();
            InteractionDenied?.Invoke(message);
        }

        private void Apply(ActiveWorldInteraction row)
        {
            if (!_bound || row.Actor != _localIdentity)
                return;

            ArenaServerClock.RecordObservedServerTimestampMicros(
                row.StartedAt.MicrosecondsSinceUnixEpoch);
            _active = row;
        }
    }
}
