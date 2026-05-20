#nullable enable
using System;
using UnityEngine;

namespace Arena.Network
{
    /// <summary>
    /// Presentation-oriented estimate of server time.
    /// V1 production samples come from observed replicated server timestamps.
    /// This uses a monotonic-max offset estimate: the largest observed
    /// server-minus-client offset is treated as the lowest one-way-latency sample.
    /// Precise reducer round-trip samples are supported by API for a future SDK
    /// callback or ping reducer, but are not wired in production yet.
    /// </summary>
    public static class ArenaServerClock
    {
        private const int MaxSamples = 32;
        private const long SnapThresholdMs = 80L;
        private const long LowRttBandMs = 12L;
        private const long MaxAcceptedRttMs = 1000L;
        private static readonly Sample[] Samples = new Sample[MaxSamples];
        private static int _sampleCount;
        private static int _nextSampleIndex;
        private static bool _hasEstimate;
        private static bool _hasPreciseSample;
        private static double _serverMinusClientMs;
        private static long _latestObservedServerMs;

        public static bool HasEstimate => _hasEstimate;
        public static long ServerNowMs => ClientNowMs + (long)Math.Round(_serverMinusClientMs);
        public static long LastRoundTripMs { get; private set; }

        private static long ClientNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static void Reset()
        {
            _sampleCount = 0;
            _nextSampleIndex = 0;
            _hasEstimate = false;
            _hasPreciseSample = false;
            _serverMinusClientMs = 0.0;
            _latestObservedServerMs = 0L;
            LastRoundTripMs = 0L;
        }

        public static void RecordReducerSampleMicros(
            long clientSendMs,
            long serverTimestampMicros,
            long clientReceiveMs)
        {
            RecordReducerSampleMs(
                clientSendMs,
                MicrosToMillis(serverTimestampMicros),
                clientReceiveMs);
        }

        public static void RecordReducerSampleMs(
            long clientSendMs,
            long serverTimestampMs,
            long clientReceiveMs)
        {
            if (clientReceiveMs < clientSendMs || serverTimestampMs <= 0L)
                return;

            long rttMs = clientReceiveMs - clientSendMs;
            if (rttMs > MaxAcceptedRttMs)
                return;

            double midpointMs = (clientSendMs + clientReceiveMs) * 0.5;
            double offsetMs = serverTimestampMs - midpointMs;
            LastRoundTripMs = rttMs;
            AddSample(offsetMs, rttMs, precise: true);
        }

        public static void RecordObservedServerTimestampMicros(long serverTimestampMicros)
        {
            RecordObservedServerTimestampMs(MicrosToMillis(serverTimestampMicros), ClientNowMs);
        }

        public static void RecordObservedServerTimestampMs(long serverTimestampMs, long clientReceiveMs)
        {
            if (serverTimestampMs <= 0L || serverTimestampMs < _latestObservedServerMs)
                return;

            _latestObservedServerMs = serverTimestampMs;
            AddSample(serverTimestampMs - clientReceiveMs, rttMs: long.MaxValue, precise: false);
        }

        private static long MicrosToMillis(long micros) => micros / 1000L;

        private static void AddSample(double offsetMs, long rttMs, bool precise)
        {
            Samples[_nextSampleIndex] = new Sample(offsetMs, rttMs, precise);
            _nextSampleIndex = (_nextSampleIndex + 1) % MaxSamples;
            _sampleCount = Mathf.Min(_sampleCount + 1, MaxSamples);
            if (precise)
                _hasPreciseSample = true;

            double nextEstimate = precise
                ? LowestRttBiasedEstimate()
                : (_hasEstimate && _hasPreciseSample ? _serverMinusClientMs : offsetMs);
            if (!_hasEstimate)
            {
                _serverMinusClientMs = nextEstimate;
                _hasEstimate = true;
                return;
            }

            double delta = nextEstimate - _serverMinusClientMs;
            if (Math.Abs(delta) >= SnapThresholdMs && HasCorroboratingPreciseSamples(nextEstimate))
            {
                _serverMinusClientMs = nextEstimate;
                return;
            }

            if (precise)
            {
                if (Math.Abs(delta) >= SnapThresholdMs)
                    return;

                _serverMinusClientMs = nextEstimate;
            }
            else if (!_hasPreciseSample && nextEstimate > _serverMinusClientMs)
            {
                // Observed timestamps only tell us "server time at row creation
                // was at least this far ahead of client receive time". Keeping
                // the largest observed offset approximates the lowest one-way
                // latency sample and intentionally does not decrease in V1.
                _serverMinusClientMs = nextEstimate;
            }
        }

        private static double LowestRttBiasedEstimate()
        {
            long bestRtt = long.MaxValue;
            for (int i = 0; i < _sampleCount; i++)
            {
                Sample sample = Samples[i];
                if (sample.Precise && sample.RttMs < bestRtt)
                    bestRtt = sample.RttMs;
            }

            if (bestRtt == long.MaxValue)
                return _serverMinusClientMs;

            double total = 0.0;
            int count = 0;
            long cutoff = bestRtt + LowRttBandMs;
            for (int i = 0; i < _sampleCount; i++)
            {
                Sample sample = Samples[i];
                if (!sample.Precise || sample.RttMs > cutoff)
                    continue;

                total += sample.OffsetMs;
                count++;
            }

            return count > 0 ? total / count : _serverMinusClientMs;
        }

        private static bool HasCorroboratingPreciseSamples(double targetOffsetMs)
        {
            int count = 0;
            for (int i = 0; i < _sampleCount; i++)
            {
                Sample sample = Samples[i];
                if (sample.Precise && Math.Abs(sample.OffsetMs - targetOffsetMs) <= LowRttBandMs)
                    count++;
            }

            return count >= 2;
        }

        private readonly struct Sample
        {
            public Sample(double offsetMs, long rttMs, bool precise)
            {
                OffsetMs = offsetMs;
                RttMs = rttMs;
                Precise = precise;
            }

            public readonly double OffsetMs;
            public readonly long RttMs;
            public readonly bool Precise;
        }
    }
}
