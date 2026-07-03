#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Arena.Entity;
using Arena.Input;
using Arena.Presentation;
using Arena.Simulation;
using UnityEngine;

namespace Arena.Debugging
{
    /// <summary>
    /// Feel-audit F4 A/B evidence logger: once per second, samples the same
    /// remote-presentation aggregates <see cref="NetcodeDebugOverlay"/>
    /// displays (remote players + NPCs), tagged with the active timeline
    /// (ON = server-time, OFF = arrival), and appends them to
    /// Logs/remote-presentation-ab.csv — so an A/B run needs no manual
    /// number-copying and legs can be compared as within-session counter
    /// deltas. Samples carry the S1 four-way taxonomy; the A/B comparison
    /// metric is (extrap + starved) / (interp + extrap + starved) — settled
    /// samples (entity authoritatively at rest) are excluded so target
    /// idleness cannot confound the legs.
    ///
    /// S5 extension: every row also carries the local input pipeline's
    /// closed-loop state (lead, buffer occupancy, send-covered fallback acks,
    /// inject/skip actuation, resyncs, reconcile error, correction budget,
    /// jump-delivery ledger, estimate source) — the per-leg evidence for the
    /// design review §4 acceptance criteria. Diagnostic only:
    /// editor/development builds, gameplay reads nothing from it, and any I/O
    /// failure is swallowed.
    /// </summary>
    internal sealed class RemotePresentationAbLog : MonoBehaviour
    {
        private const float SampleIntervalSeconds = 1f;

        private string _path = string.Empty;
        private float _nextSampleRealtime;
        private bool _wroteHeader;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("RemotePresentationAbLog");
            DontDestroyOnLoad(go);
            go.AddComponent<RemotePresentationAbLog>();
#endif
        }

        private void Awake()
        {
            _path = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Logs", "remote-presentation-ab.csv"));
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSampleRealtime)
                return;
            _nextSampleRealtime = now + SampleIntervalSeconds;

            var registry = EntityRegistry.Instance;
            if (registry == null)
                return;

            int remoteCount = 0;
            int hardSnaps = 0;
            long interpSamples = 0;
            long extrapSamples = 0;
            long starvedSamples = 0;
            long settledSamples = 0;
            float lastError = 0f;
            float maxError = 0f;
            foreach (var player in registry.AllPlayers)
            {
                if (player == registry.LocalPlayerEntity)
                    continue;

                remoteCount++;
                var sim = player.SimState;
                hardSnaps += sim.RemoteHardSnapCount;
                interpSamples += sim.RemoteInterpolationSampleCount;
                extrapSamples += sim.RemoteExtrapolationSampleCount;
                starvedSamples += sim.RemoteStarvedSampleCount;
                settledSamples += sim.RemoteSettledSampleCount;
                lastError = Mathf.Max(lastError, sim.LastRemotePositionError);
                maxError = Mathf.Max(maxError, sim.MaxRemotePositionErrorObserved);
            }

            int npcCount = 0;
            int npcHardSnaps = 0;
            long npcInterpSamples = 0;
            long npcExtrapSamples = 0;
            long npcStarvedSamples = 0;
            long npcSettledSamples = 0;
            float npcLastError = 0f;
            float npcMaxError = 0f;
            float npcDepthTicksSum = 0f;
            float npcDelayMsSum = 0f;
            foreach (var npc in registry.AllNpcs)
            {
                npcCount++;
                npcHardSnaps += npc.PresentationHardSnapCount;
                npcInterpSamples += npc.PresentationInterpolationSampleCount;
                npcExtrapSamples += npc.PresentationExtrapolationSampleCount;
                npcStarvedSamples += npc.PresentationStarvedSampleCount;
                npcSettledSamples += npc.PresentationSettledSampleCount;
                npcLastError = Mathf.Max(npcLastError, npc.PresentationLastPositionError);
                npcMaxError = Mathf.Max(npcMaxError, npc.PresentationMaxPositionErrorObserved);
                npcDepthTicksSum += npc.PresentationBufferAheadTicks;
                npcDelayMsSum += npc.PresentationEffectiveDelayMs;
            }

            var local = registry.LocalPlayerEntity;
            MovementNetDriver? netDriver = null;
            LocalMovementPredictionDriver? predDriver = null;
            LocalPresentationDriver? presentationDriver = null;
            if (local != null && !local.IsDestroyed)
            {
                netDriver = local.GameObject.GetComponent<MovementNetDriver>();
                predDriver = local.GameObject.GetComponent<LocalMovementPredictionDriver>();
                presentationDriver = local.GameObject.GetComponent<LocalPresentationDriver>();
            }

            if (remoteCount == 0 && npcCount == 0 && netDriver == null)
                return;

            var row = new StringBuilder();
            row.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            row.Append(',').Append(RemotePresentationBuffer.ServerTimeTimelineEnabled ? "ON" : "OFF");
            row.Append(',').Append(remoteCount);
            row.Append(',').Append(hardSnaps);
            row.Append(',').Append(interpSamples);
            row.Append(',').Append(extrapSamples);
            row.Append(',').Append(starvedSamples);
            row.Append(',').Append(settledSamples);
            row.Append(',').Append(lastError.ToString("F3", CultureInfo.InvariantCulture));
            row.Append(',').Append(maxError.ToString("F3", CultureInfo.InvariantCulture));
            row.Append(',').Append(npcCount);
            row.Append(',').Append(npcHardSnaps);
            row.Append(',').Append(npcInterpSamples);
            row.Append(',').Append(npcExtrapSamples);
            row.Append(',').Append(npcStarvedSamples);
            row.Append(',').Append(npcSettledSamples);
            row.Append(',').Append(npcLastError.ToString("F3", CultureInfo.InvariantCulture));
            row.Append(',').Append(npcMaxError.ToString("F3", CultureInfo.InvariantCulture));
            row.Append(',').Append(npcCount > 0
                ? (npcDepthTicksSum / npcCount).ToString("F2", CultureInfo.InvariantCulture)
                : "0");
            row.Append(',').Append(npcCount > 0
                ? (npcDelayMsSum / npcCount).ToString("F0", CultureInfo.InvariantCulture)
                : "0");
            row.Append(',').Append(PredictedMeleeContactCueController.CuesFired);
            row.Append(',').Append(PredictedMeleeContactCueController.MatchedCues);
            row.Append(',').Append(PredictedMeleeContactCueController.FalsePositives);
            row.Append(',').Append(PredictedMeleeContactCueController.SuppressedAuthoritativeCues);

            // S5 local input pipeline columns.
            var lead = netDriver?.LeadController;
            var localSim = local != null && !local.IsDestroyed ? local.SimState : null;
            row.Append(',').Append(lead?.LeadTicks ?? 0);
            row.Append(',').Append(lead?.LastAckBufferedCommands ?? 0);
            row.Append(',').Append(lead?.AckTicksObserved ?? 0);
            row.Append(',').Append(lead?.SendCoveredFallbackAcks ?? 0);
            row.Append(',').Append(lead?.LeadRaises ?? 0);
            row.Append(',').Append(lead?.LeadLowers ?? 0);
            row.Append(',').Append(predDriver?.InjectedCommands ?? 0);
            row.Append(',').Append(predDriver?.SkippedAuthoringSlots ?? 0);
            row.Append(',').Append(netDriver?.ResyncCount ?? 0);
            row.Append(',').Append((predDriver?.LastCorrectionPositionError ?? 0f)
                .ToString("F4", CultureInfo.InvariantCulture));
            row.Append(',').Append((predDriver?.MaxCorrectionPositionErrorObserved ?? 0f)
                .ToString("F4", CultureInfo.InvariantCulture));
            row.Append(',').Append(presentationDriver?.CorrectionSnapCount ?? 0);
            row.Append(',').Append((presentationDriver?.CorrectionAbsorbedMeters ?? 0f)
                .ToString("F3", CultureInfo.InvariantCulture));
            row.Append(',').Append(predDriver?.JumpsPredicted ?? 0);
            row.Append(',').Append(predDriver?.JumpsConfirmed ?? 0);
            row.Append(',').Append(predDriver?.JumpsLost ?? 0);
            row.Append(',').Append(localSim != null && localSim.LastTickEstimateUsedPreciseClock ? 1 : 0);
            row.Append('\n');

            try
            {
                if (!_wroteHeader)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.AppendAllText(
                        _path,
                        FormattableString.Invariant(
                            $"# session {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}\n")
                        + "unix_ms,timeline,players,p_hard_snaps,p_interp,p_extrap,p_starved,p_settled,p_last_err_m,p_max_err_m,"
                        + "npcs,n_hard_snaps,n_interp,n_extrap,n_starved,n_settled,n_last_err_m,n_max_err_m,n_depth_ticks_avg,n_delay_ms_avg,"
                        + "cue_fired,cue_matched,cue_false_pos,cue_suppressed_auth,"
                        + "s5_lead,s5_occ,s5_acks,s5_fb_acks,s5_raises,s5_lowers,s5_injected,s5_skipped,s5_resyncs,"
                        + "s5_rec_err_last,s5_rec_err_max,s5_corr_snaps,s5_corr_absorbed_m,"
                        + "s5_jump_pred,s5_jump_conf,s5_jump_lost,s5_est_precise\n");
                    _wroteHeader = true;
                }

                File.AppendAllText(_path, row.ToString());
            }
            catch (Exception)
            {
                // Diagnostics must never break play mode; drop the sample.
            }
        }
    }
}
