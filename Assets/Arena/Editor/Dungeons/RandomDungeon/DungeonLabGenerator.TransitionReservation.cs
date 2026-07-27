using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // How much corridor a declared route transition needs, measured from the
    // static stair contract set.
    //
    // This replaces `BaselineRoomSizeRangeForRole` (density-scale design §3, M1).
    // That rule capped a room to a fixed 4-5 cells on any axis carrying a
    // Stair/Stairwell/Bridge edge, to leave room for a stair whose prefab is
    // chosen later. It was the single biggest brake on density: the reviewed
    // contracts it was protecting need a 1-cell run at rise 4 and a 2-cell run at
    // rise 8, so it gave up four or five cells to reserve two, on nine of
    // processional-spine's thirteen edges.
    //
    // What is loaded early here is CONTRACT DATA, not a stair choice. Selection
    // still happens where it always did, in TryResolveConnectionTransition,
    // against the concrete corridor. All this answers is "what is the shortest
    // corridor any compatible contract could possibly fit in", which is a
    // property of the shipped contract files and of nothing else.
    internal sealed partial class DungeonLabGenerator
    {
        // A transition occupies `runLength` corridor cells and needs one landing
        // cell at each end — the same `path.Count >= runLength + 2` requirement
        // AddReviewedActiveStairTransitionCandidates enforces when it places one.
        private const int TransitionLandingCells = 2;

        // A stairwell tower anchors on two adjacent path cells rather than
        // running along the corridor, so its corridor cost is one cell plus the
        // landings. Its real requirement is LATERAL — void beside the path for
        // the folded footprint — which is the shaft reservation, not this.
        private const int StairwellTransitionRunCells = 1;

        // Every generic room stays at least this wide on a reserved axis.
        // DungeonRoomSizeRange.Validated() floors every bound at 3, so going
        // below it would produce a room the rest of the generator does not model.
        private const int MinimumReservedRoomExtentCells = 3;

        private static Dictionary<int, int> transitionReservationByKindAndRise;
        private static string transitionReservationContractStamp = string.Empty;

        /// <summary>
        /// The fewest corridor cells a declared transition of this kind and rise
        /// could occupy, over every compatible contract in the shipped set.
        /// </summary>
        private static int RequiredTransitionCorridorCells(RouteTransitionKind kind, int riseLevels)
        {
            if (kind == RouteTransitionKind.LevelCorridor)
            {
                return 0;
            }

            EnsureTransitionReservationTable();
            int key = TransitionReservationKey(kind, Mathf.Abs(riseLevels));
            if (transitionReservationByKindAndRise.TryGetValue(key, out int required))
            {
                return required;
            }

            // No contract can realize this edge at all, so the layout is doomed
            // whatever this returns — TryResolveConnectionTransition rejects it
            // by name later. Reserve the widest measured requirement rather than
            // the narrowest, so a doomed seed fails on the transition it cannot
            // build instead of on a room collision it was pushed into.
            int widest = 0;
            foreach (int value in transitionReservationByKindAndRise.Values)
            {
                widest = Mathf.Max(widest, value);
            }

            return widest;
        }

        private static int TransitionReservationKey(RouteTransitionKind kind, int riseLevels)
        {
            return ((int)kind * 1024) + riseLevels;
        }

        // Cached per domain load, but keyed on the contract files' write times so
        // editing a contract in the editor is picked up without a reload.
        private static void EnsureTransitionReservationTable()
        {
            string stamp = ContractFileStamp(StairProofContractsPath) + "|" +
                ContractFileStamp(ForgedStairContractsPath);
            if (transitionReservationByKindAndRise != null &&
                string.Equals(transitionReservationContractStamp, stamp, StringComparison.Ordinal))
            {
                return;
            }

            var table = new Dictionary<int, int>();
            foreach (ReviewedActiveStairOption option in LoadReviewedActiveStairOptions())
            {
                if (option.rise <= 0 ||
                    option.runLength <= 0 ||
                    option.laneCount <= 0 ||
                    option.laneCount > MaxActiveStairLaneCount)
                {
                    continue;
                }

                // A contract's kind is what the placer will accept it for: a
                // bridgeAllowed contract is the external-span pool, everything
                // else is the embedded-stair pool.
                RouteTransitionKind kind = option.isBridge
                    ? RouteTransitionKind.Bridge
                    : RouteTransitionKind.Stair;
                AccumulateMinimum(table, kind, option.rise, option.runLength + TransitionLandingCells);
            }

            // The stairwell tower is synthesized rather than drawn from the
            // contract pool, so its corridor cost is stated here at the one rise
            // grammar the route can declare. Keeping it in the same table means
            // the room packer has exactly one question to ask per edge.
            foreach (int rise in new[] { MajorRiseLevels, DoubleMajorRiseLevels })
            {
                AccumulateMinimum(
                    table,
                    RouteTransitionKind.Stairwell,
                    rise,
                    StairwellTransitionRunCells + TransitionLandingCells);
            }

            transitionReservationByKindAndRise = table;
            transitionReservationContractStamp = stamp;
        }

        private static void AccumulateMinimum(
            Dictionary<int, int> table,
            RouteTransitionKind kind,
            int riseLevels,
            int corridorCells)
        {
            int key = TransitionReservationKey(kind, riseLevels);
            if (!table.TryGetValue(key, out int existing) || corridorCells < existing)
            {
                table[key] = corridorCells;
            }
        }

        private static string ContractFileStamp(string path)
        {
            return File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "<missing>";
        }

        /// <summary>
        /// The widest a room may grow on one axis and still leave every
        /// transition on that axis the corridor it needs.
        /// </summary>
        /// <remarks>
        /// Both endpoint rooms are centred on their node and both apply this
        /// rule, so the reservation is split evenly between them and the result
        /// is independent of the order rooms are inflated in — which matters,
        /// because inflation places recipe rooms first and then the rest in node
        /// order. <c>CenteredRect</c> reaches <c>floor(size / 2)</c> cells from
        /// the centre in the negative direction and one fewer in the positive,
        /// so bounding the larger of the two reaches bounds both.
        /// </remarks>
        private static int MaxRoomExtentForTransition(int axisDistanceCells, int requiredCorridorCells)
        {
            int reach = (axisDistanceCells - requiredCorridorCells - 1) / 2;
            return Mathf.Max(MinimumReservedRoomExtentCells, reach * 2 + 1);
        }
    }
}
