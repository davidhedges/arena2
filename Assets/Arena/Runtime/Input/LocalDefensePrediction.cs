#nullable enable

using Arena.Entity;
using Arena.Simulation;
using SpacetimeDB.Types;

namespace Arena.Input
{
    internal static class LocalDefensePrediction
    {
        private const long PredictionReconcileMs = 150;
        private const long AcceptedPredictionStateHoldMs = 5000;
        private const string BlockKind = "BLOCK";
        private const string BlockCooldownKind = "BLOCK_COOLDOWN";
        private const string ParryKind = "PARRY";
        private const string ParryCooldownKind = "PARRY_COOLDOWN";

        private static bool _predictedParryActive;
        private static long _predictedParryStartedMs;
        private static ActionPredictionToken _predictedParryToken;
        private static bool _predictedParryAccepted;
        private static bool _parryStopRequested;

        public static void Reset()
        {
            _predictedParryActive = false;
            _predictedParryStartedMs = 0L;
            _predictedParryToken = ActionPredictionToken.None(ParryKind);
            _predictedParryAccepted = false;
            _parryStopRequested = false;
        }

        public static void PredictParry(long nowMs, ActionPredictionToken token)
        {
            _predictedParryActive = true;
            _predictedParryStartedMs = nowMs;
            _predictedParryToken = token;
            _predictedParryAccepted = false;
            _parryStopRequested = false;
        }

        public static void OnPredictedActionResult(PredictedActionResult row, PlayerEntity entity)
        {
            if (row.Family != PredictedActionFamily.Defense)
                return;
            if (!_predictedParryActive || !TokenMatches(row, _predictedParryToken))
                return;
            if (row.Result == ActionResultKind.Accepted)
            {
                _predictedParryAccepted = true;
                return;
            }

            if (ShouldClearPredictedParryForResult(row, _predictedParryToken, _predictedParryActive))
            {
                _predictedParryActive = false;
                _predictedParryStartedMs = 0L;
                _predictedParryToken = ActionPredictionToken.None(ParryKind);
                _predictedParryAccepted = false;
                entity.SetParryArmed(false);
            }
        }

        public static bool ShouldRequestParryStop(DbConnection conn, PlayerEntity entity, long nowMs)
        {
            if (_parryStopRequested)
                return false;

            DefenseState? auth = conn.Db.DefenseState.Owner.Find(entity.Identity);
            return _predictedParryActive || IsArmedParry(auth, nowMs);
        }

        public static void RequestParryStop(PlayerEntity entity)
        {
            _predictedParryActive = false;
            _predictedParryAccepted = false;
            _parryStopRequested = true;
            entity.SetParryArmed(false);
        }

        public static bool ConsumePredictedParryPresentation()
        {
            if (!_predictedParryActive)
                return false;

            _predictedParryActive = false;
            _predictedParryToken = ActionPredictionToken.None(ParryKind);
            _predictedParryAccepted = false;
            return true;
        }

        public static bool IsBlocking(DbConnection conn, PlayerEntity entity)
        {
            DefenseState? auth = conn.Db.DefenseState.Owner.Find(entity.Identity);
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return auth != null
                && ((string.Equals(auth.Kind, BlockKind, System.StringComparison.OrdinalIgnoreCase)
                        && nowMs < auth.ActiveUntil.MicrosecondsSinceUnixEpoch / 1000L)
                    || (string.Equals(auth.Kind, BlockCooldownKind, System.StringComparison.OrdinalIgnoreCase)
                        && nowMs < auth.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L));
        }

        public static bool IsParrying(DbConnection conn, PlayerEntity entity)
        {
            DefenseState? auth = conn.Db.DefenseState.Owner.Find(entity.Identity);
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _predictedParryActive
                || IsArmedParry(auth, nowMs)
                || IsSuccessfulParryCooldown(auth, nowMs);
        }

        public static bool TryGetSuccessfulParryCooldown(
            DbConnection conn,
            PlayerEntity entity,
            long nowMs,
            out long remainingMs,
            out long durationMs)
        {
            remainingMs = 0L;
            durationMs = 0L;

            DefenseState? auth = conn.Db.DefenseState.Owner.Find(entity.Identity);
            if (auth == null
                || !string.Equals(auth.Kind, ParryCooldownKind, System.StringComparison.OrdinalIgnoreCase))
                return false;

            long activeUntilMs = auth.ActiveUntil.MicrosecondsSinceUnixEpoch / 1000L;
            long recoveryUntilMs = auth.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L;
            if (recoveryUntilMs <= activeUntilMs || nowMs >= recoveryUntilMs)
                return false;

            remainingMs = recoveryUntilMs - nowMs;
            durationMs = recoveryUntilMs - activeUntilMs;
            return remainingMs > 0L && durationMs > 0L;
        }

        public static void Reconcile(DbConnection conn, PlayerEntity entity, long nowMs)
        {
            DefenseState? auth = conn.Db.DefenseState.Owner.Find(entity.Identity);
            if (auth == null
                || (!string.Equals(auth.Kind, ParryKind, System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(auth.Kind, ParryCooldownKind, System.StringComparison.OrdinalIgnoreCase)))
                _parryStopRequested = false;

            if (_predictedParryActive)
            {
                if (auth != null && string.Equals(auth.Kind, ParryKind, System.StringComparison.OrdinalIgnoreCase))
                {
                    _predictedParryActive = false;
                    _predictedParryAccepted = false;
                }
                else if (ShouldTimeoutPredictedParry(nowMs, _predictedParryStartedMs, _predictedParryAccepted))
                {
                    _predictedParryActive = false;
                    _predictedParryToken = ActionPredictionToken.None(ParryKind);
                    _predictedParryAccepted = false;
                    entity.SetParryArmed(false);
                }
            }
        }

        internal static bool ShouldClearPredictedParryForResult(
            PredictedActionResult row,
            ActionPredictionToken token,
            bool predictedParryActive)
        {
            return predictedParryActive
                && row.Family == PredictedActionFamily.Defense
                && TokenMatches(row, token)
                && row.Result != ActionResultKind.Accepted;
        }

        internal static bool ShouldTimeoutPredictedParry(long nowMs, long startedMs, bool accepted)
        {
            long timeoutMs = accepted ? AcceptedPredictionStateHoldMs : PredictionReconcileMs;
            return nowMs >= startedMs + timeoutMs;
        }

        private static bool TokenMatches(PredictedActionResult row, ActionPredictionToken token)
        {
            return token.IsPredicted
                && string.Equals(row.PredictedActionId, token.PredictedActionId, System.StringComparison.Ordinal)
                && row.ClientActionSeq == token.ClientActionSeq;
        }

        private static bool IsArmedParry(DefenseState? auth, long nowMs)
        {
            if (auth == null || !string.Equals(auth.Kind, ParryKind, System.StringComparison.OrdinalIgnoreCase))
                return false;

            long activeUntilMs = auth.ActiveUntil.MicrosecondsSinceUnixEpoch / 1000L;
            long recoveryUntilMs = auth.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L;
            return nowMs < activeUntilMs && recoveryUntilMs <= activeUntilMs;
        }

        private static bool IsSuccessfulParryCooldown(DefenseState? auth, long nowMs)
        {
            if (auth == null || !string.Equals(auth.Kind, ParryCooldownKind, System.StringComparison.OrdinalIgnoreCase))
                return false;

            long recoveryUntilMs = auth.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L;
            return nowMs < recoveryUntilMs;
        }
    }
}
