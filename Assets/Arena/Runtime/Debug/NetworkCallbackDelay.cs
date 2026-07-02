#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Debugging
{
    /// <summary>
    /// Dev-only receive-path latency simulation (feel audit F2c). When
    /// <see cref="DelayMs"/> is non-zero, replicated row callbacks routed
    /// through <c>NetworkCallbackBinder</c> are held in a FIFO queue and
    /// dispatched that many milliseconds later, on the main thread, in
    /// arrival order. Disabled (the default), Dispatch invokes inline with no
    /// queueing, so production behavior is untouched.
    ///
    /// This defers only the presentation-side callbacks — the SDK client
    /// cache still applies rows immediately, so a deferred handler that reads
    /// <c>conn.Db</c> sees state newer than its row arguments, and outbound
    /// reducer calls are not delayed at all. Use it for quick in-editor
    /// checks of interpolation/prediction presentation under receive delay;
    /// for faithful end-to-end latency, jitter, and loss use the OS-level
    /// profiles in docs/latency-testing.md.
    ///
    /// Enable via the ARENA_CALLBACK_DELAY_MS environment variable or by
    /// setting <see cref="DelayMs"/> from debug code. No gameplay system may
    /// read or write this.
    /// </summary>
    public static class NetworkCallbackDelay
    {
        private readonly struct Deferred
        {
            public Deferred(Action action, float dueRealtime)
            {
                Action = action;
                DueRealtime = dueRealtime;
            }

            public readonly Action Action;
            public readonly float DueRealtime;
        }

        private static readonly Queue<Deferred> Pending = new();
        private static int _delayMs = ReadInitialDelayMs();

        public static int DelayMs
        {
            get => _delayMs;
            set => _delayMs = Mathf.Max(0, value);
        }

        public static bool IsActive => _delayMs > 0;
        public static int PendingCount => Pending.Count;

        public static void Dispatch(Action callback)
        {
            if (_delayMs <= 0 && Pending.Count == 0)
            {
                callback();
                return;
            }

            // Keep enqueuing while the queue drains so a just-disabled delay
            // cannot reorder callbacks that are still in flight.
            Pending.Enqueue(new Deferred(callback, Time.realtimeSinceStartup + _delayMs / 1000.0f));
        }

        /// <summary>
        /// Runs due callbacks in arrival order. Called by NetworkManager after
        /// FrameTick each frame; a later-due head blocks newer entries so
        /// ordering survives mid-flight DelayMs changes.
        /// </summary>
        public static void Pump()
        {
            float now = Time.realtimeSinceStartup;
            while (Pending.Count > 0 && Pending.Peek().DueRealtime <= now)
                Pending.Dequeue().Action();
        }

        /// <summary>
        /// Drops queued callbacks. Called on connect/disconnect so stale rows
        /// from a dead connection never replay into freshly reset caches.
        /// </summary>
        public static void ResetForNetworkReconnect()
        {
            Pending.Clear();
        }

        private static int ReadInitialDelayMs()
        {
            string? raw = Environment.GetEnvironmentVariable("ARENA_CALLBACK_DELAY_MS");
            return int.TryParse(raw, out int parsed) ? Mathf.Max(0, parsed) : 0;
        }
    }
}
