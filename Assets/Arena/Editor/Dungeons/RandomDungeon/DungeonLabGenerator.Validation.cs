using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // One dungeon-plan validation gate, called from exactly one place in the
    // generation path and reused verbatim by the batch report.
    //
    // Before 2026-07-25 these checks existed only inside the batch reporting
    // path, so the strongest guarantee the project owned gated nothing: a plan
    // that failed them was still rendered, saved, and exported. The checks now
    // run inside the tier-attempt loop, where a failure is an ordinary retry
    // reason, and the report projects the same result instead of recomputing it.
    internal sealed partial class DungeonLabGenerator
    {
        private const int MinimumProductionLayeredRecipeCount = 3;
        private const int MinimumProductionStackedSurfaceCount = 48;

        // Named, independently derived random streams for the tier planner.
        //
        // Before 2026-07-25 one System.Random was threaded sequentially through
        // both retry loops, so how many attempts had already failed decided which
        // rooms were enclosed, which loops appeared, and which stair prefabs were
        // picked. That is why unrelated edits reshuffled every seed and why stored
        // hashes rotted. Every draw now comes from a stream keyed by
        // (seed, layout attempt, tier attempt, purpose, subject), so a change to
        // one decision provably cannot perturb another.
        //
        // The route planner already worked this way; this extends the same pattern
        // across the tier seam rather than inventing a second mechanism.
        private readonly struct DungeonRandomScope
        {
            private readonly int dungeonSeed;
            private readonly int layoutAttempt;
            private readonly int tierAttempt;

            public DungeonRandomScope(int dungeonSeed, int layoutAttempt, int tierAttempt)
            {
                this.dungeonSeed = dungeonSeed;
                this.layoutAttempt = layoutAttempt;
                this.tierAttempt = tierAttempt;
            }

            public DungeonRandomScope ForTierAttempt(int attempt)
            {
                return new DungeonRandomScope(dungeonSeed, layoutAttempt, attempt);
            }

            /// <summary>One independent stream for a whole-plan decision.</summary>
            public System.Random Stream(string purpose)
            {
                return DerivedRandom(
                    dungeonSeed,
                    layoutAttempt,
                    purpose,
                    $"tier-{tierAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            /// <summary>One independent stream per subject, so per-item decisions cannot influence each other.</summary>
            public System.Random Stream(string purpose, string subject)
            {
                return DerivedRandom(
                    dungeonSeed,
                    layoutAttempt,
                    $"{purpose}:{subject}",
                    $"tier-{tierAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            /// <summary>
            /// One independent stream per SURFACE, keyed on the canonical
            /// (cell, level) identity rather than on a plan coordinate.
            /// </summary>
            /// <remarks>
            /// The design's Phase A invariant reads "RNG subjects that were cell
            /// tokens become surface tokens". Measured against this commit, no
            /// such subject exists: every keyed stream in the generator is
            /// subjected on a room pair, a node id, a recipe id or a topology id,
            /// and not one is a `Vector2Int`. So the migration is a no-op and
            /// this is the entry point that keeps it one — a per-place decision
            /// added later takes a surface here and cannot silently reintroduce
            /// a plan coordinate as an identity.
            /// </remarks>
            public System.Random Stream(string purpose, SurfaceKey surface)
            {
                return Stream(purpose, surface.Token);
            }
        }

        internal readonly struct DungeonPlanCheck
        {
            public readonly string id;
            public readonly string failureCode;
            public readonly bool passed;
            public readonly string message;

            public DungeonPlanCheck(string id, string failureCode, bool passed, string message)
            {
                this.id = id;
                this.failureCode = failureCode;
                this.passed = passed;
                this.message = message ?? string.Empty;
            }
        }

        internal sealed class DungeonPlanValidation
        {
            public readonly bool passed;
            public readonly DungeonPlanCheck[] checks;

            public DungeonPlanValidation(DungeonPlanCheck[] checks)
            {
                this.checks = checks ?? Array.Empty<DungeonPlanCheck>();
                passed = true;
                foreach (DungeonPlanCheck check in this.checks)
                {
                    if (!check.passed)
                    {
                        passed = false;
                        break;
                    }
                }
            }

            public IEnumerable<string> FailureCodes()
            {
                foreach (DungeonPlanCheck check in checks)
                {
                    if (!check.passed)
                    {
                        yield return check.failureCode;
                    }
                }
            }

            public string FirstFailure()
            {
                foreach (DungeonPlanCheck check in checks)
                {
                    if (!check.passed)
                    {
                        return $"[{check.failureCode}] {check.message}";
                    }
                }

                return string.Empty;
            }
        }

        // The boundary context is built here rather than after acceptance because
        // it is both a validation input and a renderer input, and building it
        // twice would consume the shared draw stream twice.
        private static DungeonPlanValidation ValidateDungeonPlan(
            int seed,
            DungeonLayout layout,
            TieredLevelPlan plan,
            RouteIntent intent,
            bool boundaryValid,
            string boundaryMessage)
        {
            bool layoutConnected = IsConnected(layout.floorCells);
            bool roomGraphConnected = TryValidateRoomGraphConnectivity(layout, out string roomGraphMessage);
            bool transitionContractsValid = TryValidateTransitionLevelDeltas(
                plan.transitions,
                plan.topologyCeilingLevels,
                layout,
                out string transitionMessage);

            bool portGraphBuilt = TryBuildFloorStairPortGraph(
                plan.surfaces,
                plan.transitions,
                out FloorStairPortGraph portGraph,
                out string portGraphBuildMessage);
            bool portGraphConnected = false;
            string portGraphConnectedMessage = portGraphBuildMessage;
            if (portGraphBuilt)
            {
                portGraphConnected = portGraph.IsFallFreeConnected(out portGraphConnectedMessage);
            }

            bool bottomToTop = portGraphConnected && plan.minLevel < plan.maxLevel;
            bool routeRequirementsValid = TryValidateAcceptedRouteRequirements(
                intent,
                layout,
                plan,
                out string routeRequirementsMessage);
            bool recipesValid = TryValidateAcceptedRecipes(intent, plan, out string recipesMessage);
            bool richLayeringValid = TryValidateAcceptedRichLayering(intent, plan, out string richLayeringMessage);
            bool namedPromontoriesValid = TryValidateAcceptedNamedPromontories(intent, plan, out string namedPromontoryMessage);
            bool externalConnectorsValid = TryValidateAcceptedExternalConnectors(seed, plan, out string externalConnectorMessage);
            bool headroomValid = TryValidateAcceptedPlanHeadroom(plan, out string headroomMessage);
            // D5: the reserved-void gate design §13 names in Phase D's exit
            // criteria. Vacuously true until a recipe declares a volume, and
            // that is exactly its value — it is the check that would catch the
            // atrium being quietly packed, whichever pass did it.
            bool openVolumesValid = plan.prisms.TryValidateOpenVolumes(
                plan.surfaces,
                out string openVolumeMessage);
            bool rendererInputsValid = TryValidateRendererInputs(plan, out string rendererInputMessage);

            return new DungeonPlanValidation(new[]
            {
                new DungeonPlanCheck(
                    "layoutConnectivity",
                    "FLOOR_CONNECTIVITY",
                    layoutConnected,
                    layoutConnected ? "floor mask connected" : "floor mask disconnected"),
                new DungeonPlanCheck(
                    "roomGraphConnectivity",
                    "ROOM_GRAPH_CONNECTIVITY",
                    roomGraphConnected,
                    roomGraphMessage),
                new DungeonPlanCheck(
                    "transitionContracts",
                    "TRANSITION_CONTRACT",
                    transitionContractsValid,
                    transitionMessage),
                new DungeonPlanCheck(
                    "verticalTraversal",
                    "VERTICAL_TRAVERSAL",
                    portGraphConnected,
                    portGraphConnectedMessage),
                new DungeonPlanCheck(
                    "bottomToTopTraversal",
                    "BOTTOM_TO_TOP_TRAVERSAL",
                    bottomToTop,
                    bottomToTop
                        ? $"connected traversal spans levels {plan.minLevel}..{plan.maxLevel}"
                        : $"traversal did not span distinct bottom/top levels ({plan.minLevel}..{plan.maxLevel})"),
                new DungeonPlanCheck(
                    "routeRequirements",
                    "ROUTE_REQUIREMENTS",
                    routeRequirementsValid,
                    routeRequirementsMessage),
                new DungeonPlanCheck("recipes", "RECIPES", recipesValid, recipesMessage),
                new DungeonPlanCheck(
                    "richLayering",
                    "RICH_LAYERING_REQUIRED",
                    richLayeringValid,
                    richLayeringMessage),
                new DungeonPlanCheck(
                    "namedPromontories",
                    "NAMED_PROMONTORY",
                    namedPromontoriesValid,
                    namedPromontoryMessage),
                new DungeonPlanCheck(
                    "externalConnectors",
                    "EXTERNAL_CONNECTOR_PROMONTORY",
                    externalConnectorsValid,
                    externalConnectorMessage),
                new DungeonPlanCheck(
                    "headroom",
                    "POST_PLAN_HEADROOM_CLEARANCE",
                    headroomValid,
                    headroomMessage),
                new DungeonPlanCheck(
                    "openVolumes",
                    "OPEN_VOLUME_VIOLATION",
                    openVolumesValid,
                    openVolumeMessage),
                new DungeonPlanCheck("boundary", "BOUNDARY_CONTEXT", boundaryValid, boundaryMessage),
                new DungeonPlanCheck(
                    "rendererInputs",
                    "RENDERER_INPUT",
                    rendererInputsValid,
                    rendererInputMessage)
            });
        }
    }
}
