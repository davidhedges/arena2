#nullable enable

namespace Arena.UI
{
    /// <summary>
    /// Pure show/rate-limit/expiry bookkeeping for the denial toast (feel
    /// audit F1 step 4). Single-slot by construction, so a mashed rejected
    /// button can never stack toasts: an accepted re-show replaces the text
    /// and refreshes the expiry, and shows inside the rearm window are
    /// dropped. Time is injected so editor tests can drive it without Unity.
    /// </summary>
    public sealed class ActionDenialToastModel
    {
        /// <summary>Auto-dismiss window; the audit caps the cue at 1.5 s.</summary>
        public const double DisplaySeconds = 1.4;

        /// <summary>Minimum spacing between accepted shows (mash guard).</summary>
        public const double MinRearmSeconds = 0.25;

        private double _lastShowTime = double.NegativeInfinity;
        private double _expiresAt = double.NegativeInfinity;

        public string? ActiveText { get; private set; }

        public bool TryShow(string text, double now)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            if (now - _lastShowTime < MinRearmSeconds)
                return false;

            ActiveText = text;
            _lastShowTime = now;
            _expiresAt = now + DisplaySeconds;
            return true;
        }

        /// <summary>Clears the toast once expired; returns the visible text or null.</summary>
        public string? Tick(double now)
        {
            if (ActiveText != null && now >= _expiresAt)
                ActiveText = null;
            return ActiveText;
        }

        public bool IsVisible(double now) => ActiveText != null && now < _expiresAt;

        /// <summary>Remaining-lifetime fraction (1 → just shown, 0 → expired).</summary>
        public float RemainingFraction(double now)
        {
            if (!IsVisible(now))
                return 0f;
            return (float)((_expiresAt - now) / DisplaySeconds);
        }
    }
}
