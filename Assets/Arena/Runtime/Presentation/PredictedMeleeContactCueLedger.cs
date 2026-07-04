#nullable enable
using System;
using System.Collections.Generic;

namespace Arena.Presentation
{
    internal enum AuthoritativeContactCueDecision
    {
        PlayCue = 0,
        SuppressCue = 1,
    }

    /// <summary>
    /// Pure bookkeeping for predicted melee contact cues (feel audit F5
    /// slice 2). Tracks, per prediction token: the scheduled advisory hit
    /// test at the authored first hit window, the fired cue's correlation
    /// window against the authoritative COMBAT_IMPACT/COMBAT_CONTACT that
    /// should confirm it, duplicate suppression of the authoritative impact
    /// cue, and the false-positive count (cue fired, no matching
    /// authoritative contact within the window). Accepted
    /// PredictedActionResult rows map action instance ids back to tokens —
    /// the same correlation slice 1 used for the animation start.
    /// Cosmetic-only: nothing here reads or writes gameplay state.
    /// </summary>
    internal sealed class PredictedMeleeContactCueLedger
    {
        public const long AuthoritativeMatchWindowMs = 500L;
        public const long ScheduledEntryGraceMs = 2000L;

        private enum EntryState
        {
            Scheduled = 0,
            Fired = 1,
        }

        private sealed class Entry
        {
            public string TargetKey = string.Empty;
            public EntryState State;
            public int PredictedHitIndex;
            public long FireAtMs;
            public long MatchWindowEndsAtMs;
            public bool AuthoritativeContactSeenWhileScheduled;
            public bool IsAutoAttack;
        }

        private readonly Dictionary<string, Entry> _entriesByToken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tokenByActionInstance = new(StringComparer.Ordinal);

        public int CuesFired { get; private set; }
        public int MatchedCues { get; private set; }
        public int FalsePositives { get; private set; }
        public int SuppressedAuthoritativeCues { get; private set; }
        // Auto-attack share of the counters above (netcode design review S6);
        // ability-melee values are the difference.
        public int AutoCuesFired { get; private set; }
        public int AutoMatchedCues { get; private set; }
        public int AutoFalsePositives { get; private set; }
        public int AutoSuppressedAuthoritativeCues { get; private set; }
        public int PendingCount => _entriesByToken.Count;

        public void Schedule(
            string tokenKey,
            string targetKey,
            long fireAtMs,
            int predictedHitIndex,
            bool isAutoAttack = false)
        {
            if (string.IsNullOrWhiteSpace(tokenKey))
                return;

            _entriesByToken[tokenKey] = new Entry
            {
                TargetKey = targetKey ?? string.Empty,
                State = EntryState.Scheduled,
                PredictedHitIndex = predictedHitIndex,
                FireAtMs = fireAtMs,
                IsAutoAttack = isAutoAttack,
            };
        }

        public void MapAcceptedActionInstance(string actionInstanceId, string tokenKey)
        {
            if (string.IsNullOrWhiteSpace(actionInstanceId) || string.IsNullOrWhiteSpace(tokenKey))
                return;
            if (!_entriesByToken.ContainsKey(tokenKey))
                return;

            _tokenByActionInstance[actionInstanceId] = tokenKey;
        }

        /// <summary>
        /// A rejected/stale press: a cue that already fired can never be
        /// confirmed (no authoritative action exists), so it is an immediate
        /// false positive; a still-scheduled advisory is silently canceled.
        /// </summary>
        public void OnPredictionRejected(string tokenKey)
        {
            if (!_entriesByToken.TryGetValue(tokenKey, out Entry? entry))
                return;

            if (entry.State == EntryState.Fired)
                RecordFalsePositive(entry);
            _entriesByToken.Remove(tokenKey);
        }

        /// <summary>
        /// Gate for the controller right before it runs the advisory hit
        /// test. False when the press was rejected, the authoritative contact
        /// already arrived (its cue already played — firing now would
        /// double-play), or the entry is unknown.
        /// </summary>
        public bool TryBeginFiring(string tokenKey)
        {
            if (!_entriesByToken.TryGetValue(tokenKey, out Entry? entry)
                || entry.State != EntryState.Scheduled)
            {
                return false;
            }

            if (entry.AuthoritativeContactSeenWhileScheduled)
            {
                _entriesByToken.Remove(tokenKey);
                return false;
            }

            return true;
        }

        public void RecordCueFired(string tokenKey, long nowMs)
        {
            if (!_entriesByToken.TryGetValue(tokenKey, out Entry? entry))
                return;

            entry.State = EntryState.Fired;
            entry.MatchWindowEndsAtMs = nowMs + AuthoritativeMatchWindowMs;
            CuesFired++;
            if (entry.IsAutoAttack)
                AutoCuesFired++;
        }

        /// <summary>Advisory hit test failed — no cue, nothing to correlate.</summary>
        public void Drop(string tokenKey)
        {
            _entriesByToken.Remove(tokenKey);
        }

        /// <summary>
        /// An authoritative contact-family CombatEvent
        /// (impact/contact/block/parry) for the local caster. Any same-target
        /// row inside the window confirms the fired cue (not a false
        /// positive); only the impact row that duplicates the predicted
        /// presentation — impact trigger at the predicted hit index — is
        /// suppressed. Block/parry cues are different presentation and always
        /// play.
        /// </summary>
        public AuthoritativeContactCueDecision OnAuthoritativeContact(
            string actionInstanceId,
            string targetKey,
            bool isImpactCueTrigger,
            int hitIndex,
            long nowMs)
        {
            if (string.IsNullOrWhiteSpace(actionInstanceId)
                || !_tokenByActionInstance.TryGetValue(actionInstanceId, out string tokenKey)
                || !_entriesByToken.TryGetValue(tokenKey, out Entry? entry))
            {
                return AuthoritativeContactCueDecision.PlayCue;
            }

            if (entry.State == EntryState.Scheduled)
            {
                // The server's contact beat the advisory window; the
                // authoritative cue plays and the scheduled advisory is
                // canceled at its fire time.
                entry.AuthoritativeContactSeenWhileScheduled = true;
                return AuthoritativeContactCueDecision.PlayCue;
            }

            if (!string.Equals(entry.TargetKey, targetKey, StringComparison.Ordinal))
                return AuthoritativeContactCueDecision.PlayCue;

            if (nowMs > entry.MatchWindowEndsAtMs)
            {
                RecordFalsePositive(entry);
                _entriesByToken.Remove(tokenKey);
                return AuthoritativeContactCueDecision.PlayCue;
            }

            MatchedCues++;
            if (entry.IsAutoAttack)
                AutoMatchedCues++;
            _entriesByToken.Remove(tokenKey);
            if (isImpactCueTrigger && hitIndex == entry.PredictedHitIndex)
            {
                SuppressedAuthoritativeCues++;
                if (entry.IsAutoAttack)
                    AutoSuppressedAuthoritativeCues++;
                return AuthoritativeContactCueDecision.SuppressCue;
            }

            return AuthoritativeContactCueDecision.PlayCue;
        }

        private void RecordFalsePositive(Entry entry)
        {
            FalsePositives++;
            if (entry.IsAutoAttack)
                AutoFalsePositives++;
        }

        /// <summary>
        /// Expires fired cues whose match window lapsed with no authoritative
        /// contact (the false-positive definition), abandoned scheduled
        /// entries, and instance mappings whose token is gone.
        /// </summary>
        public void Tick(long nowMs)
        {
            List<string>? staleTokens = null;
            foreach (KeyValuePair<string, Entry> pair in _entriesByToken)
            {
                Entry entry = pair.Value;
                bool firedWindowLapsed = entry.State == EntryState.Fired
                    && nowMs > entry.MatchWindowEndsAtMs;
                bool scheduledAbandoned = entry.State == EntryState.Scheduled
                    && nowMs > entry.FireAtMs + ScheduledEntryGraceMs;
                if (!firedWindowLapsed && !scheduledAbandoned)
                    continue;

                if (firedWindowLapsed)
                    RecordFalsePositive(entry);
                (staleTokens ??= new List<string>()).Add(pair.Key);
            }

            if (staleTokens != null)
            {
                foreach (string tokenKey in staleTokens)
                    _entriesByToken.Remove(tokenKey);
            }

            List<string>? staleInstances = null;
            foreach (KeyValuePair<string, string> pair in _tokenByActionInstance)
            {
                if (!_entriesByToken.ContainsKey(pair.Value))
                    (staleInstances ??= new List<string>()).Add(pair.Key);
            }

            if (staleInstances != null)
            {
                foreach (string actionInstanceId in staleInstances)
                    _tokenByActionInstance.Remove(actionInstanceId);
            }
        }
    }
}
