using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // The density metric and its readable projection.
    //
    // `floorFillPercent` — floor over the FLOOR bounding box — is the metric
    // that let the previous density attempt believe it had shipped: it moved
    // when exterior corridor length moved, while the void did not. Worse, the
    // floor bounding box GROWS when an external promontory reaches outward, so
    // that metric penalises a feature the design wants.
    //
    // What replaces it, per the density-scale design of 2026-07-27 §5:
    //
    //   latticeEnvelopeFillPercent  floor over the LATTICE envelope — the box
    //                               the embedder itself measures against — which
    //                               does not move when a promontory reaches out.
    //   voidComponents              connected components of non-floor cells
    //                               inside that envelope, excluding authored
    //                               void, as a size histogram with a max. This
    //                               is the number the eye actually reads, and
    //                               density 5 is accepted on it directly.
    //
    // Alongside them, an ASCII floorplan per seed, because the previous failure
    // mode was optimising a scalar nobody could see. This file records evidence
    // and never participates in generation.
    internal sealed partial class DungeonLabGenerator
    {
        private const string DensityMeasurementVersion = "density-void-v1";

        // Rooms get a glyph each (index in base 36); everything else is fixed.
        private const char FloorplanOutsideGlyph = ' ';
        private const char FloorplanVoidGlyph = '.';
        private const char FloorplanVistaVoidGlyph = 'v';
        private const char FloorplanSpanVoidGlyph = 's';
        private const char FloorplanCorridorGlyph = '+';
        private const char FloorplanPromontoryGlyph = 'p';
        private const char FloorplanOverflowRoomGlyph = '?';

        private const string FloorplanLegend =
            "' '=outside the lattice envelope; '.'=counted void; 'v'=authored void (reserved vista lane); " +
            "'s'=authored void (stairwell shaft / aerial span footprint); 'p'=promontory or external connector pier; " +
            "'+'=corridor floor; 0-9a-z=room floor by room index. First row is the highest y, matching the topology maps.";

        // The lattice envelope: the bounding box of the route's node centres
        // grown by the room envelope radius on every side. This is exactly the
        // box TryTransformCoarseEmbedding measures against mapWidth/DepthMaxCells,
        // so it is the generator's own idea of "the space this dungeon was given"
        // rather than a bounding box derived from what came out.
        internal static RectInt LatticeEnvelopeFor(
            IReadOnlyList<Vector2Int> nodeCenters,
            DungeonPatternSpatialSettings spatial)
        {
            int radius = spatial.roomEnvelopeRadiusCells;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;
            foreach (Vector2Int center in nodeCenters)
            {
                minX = Mathf.Min(minX, center.x);
                minY = Mathf.Min(minY, center.y);
                maxX = Mathf.Max(maxX, center.x);
                maxY = Mathf.Max(maxY, center.y);
            }

            return new RectInt(
                minX - radius,
                minY - radius,
                maxX - minX + radius * 2 + 1,
                maxY - minY + radius * 2 + 1);
        }

        /// <summary>
        /// Floor inside the lattice envelope, over that envelope's area.
        /// </summary>
        /// <remarks>
        /// This is the acceptance metric that replaced
        /// <see cref="CalculateFloorFillPercent"/>, which measured floor over the
        /// FLOOR bounding box and therefore fell whenever a promontory reached
        /// outward or a room grew — penalising two things the density work wants
        /// (design §3, §5). The denominator here is fixed by the embedding, so
        /// the number only moves when the amount of floor does.
        /// </remarks>
        internal static float LatticeEnvelopeFillPercent(
            HashSet<Vector2Int> floorCells,
            RectInt envelope)
        {
            int area = Mathf.Max(1, envelope.width * envelope.height);
            int inside = 0;
            foreach (Vector2Int cell in floorCells)
            {
                if (envelope.Contains(cell))
                {
                    inside++;
                }
            }

            return inside / (float)area;
        }

        // The diagnostic entry point, which reads the ephemeral route embedding
        // the report path already depends on. Generation gates use the pure
        // overload above with the embedding they hold.
        private static bool TryResolveLatticeEnvelope(out RectInt envelope)
        {
            envelope = default;
            if (lastRouteIntent == null ||
                lastNodeCenters == null ||
                lastNodeCenters.Length == 0 ||
                lastNodeCenters.Length != lastRouteIntent.nodes.Length)
            {
                return false;
            }

            envelope = LatticeEnvelopeFor(
                lastNodeCenters,
                ResolveTopologySpatialSettings(lastRouteIntent.topology));
            return true;
        }

        // Authored void survives at every density and must not be counted as a
        // hole to be filled (design §4.1):
        //
        //   * the reserved vista sight lane, exactly as the route planner
        //     reserved it;
        //   * stairwell shafts — a stairwell tower stands on void BESIDE the
        //     path, so its folded footprint is deliberately empty floor mask;
        //   * aerial span footprints — a bridge over filled floor is a walkway
        //     (fork F2), so the span keeps its void;
        //   * promontory and external-connector pier cells, which are surfaces
        //     the tier stage adds after the layout floor mask is closed and
        //     which would otherwise read as holes.
        //
        // Everything here is an exact cell set the plan already carries. It is
        // deliberately NOT a dilation or a guess: the flanking void beside an
        // aerial deck is still counted today, and gets revisited in phase 4/6
        // when the number actually binds.
        private static HashSet<Vector2Int> CollectAuthoredVoidCells(
            TieredLevelPlan plan,
            out int vistaLaneCells,
            out int shaftAndSpanCells,
            out int promontoryCells)
        {
            var authored = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in plan.routeRequirementResolution.reservedVistaCells)
            {
                authored.Add(cell);
            }

            vistaLaneCells = authored.Count;

            foreach (ElevationEdgeModel.TransitionEdge transition in
                     plan.transitions ?? new List<ElevationEdgeModel.TransitionEdge>())
            {
                if (!string.Equals(
                        transition.placementClass,
                        StairwellStairPlacementClass,
                        System.StringComparison.Ordinal) &&
                    !string.Equals(
                        transition.placementClass,
                        ExternalSpanStairPlacementClass,
                        System.StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Vector2Int cell in transition.footprintCells)
                {
                    authored.Add(cell);
                }
            }

            shaftAndSpanCells = authored.Count - vistaLaneCells;

            foreach (Vector2Int cell in CollectNamedPromontoryCells(plan.namedPromontories))
            {
                authored.Add(cell);
            }

            foreach (Vector2Int cell in CollectExternalConnectorPierCells(plan.externalConnectors))
            {
                authored.Add(cell);
            }

            promontoryCells = authored.Count - vistaLaneCells - shaftAndSpanCells;
            return authored;
        }

        private static JObject BuildVoidDensityMeasurements(
            DungeonLayout layout,
            TieredLevelPlan plan)
        {
            if (!TryResolveLatticeEnvelope(out RectInt envelope))
            {
                return new JObject
                {
                    ["available"] = false,
                    ["reason"] = "no route embedding was recorded, so the lattice envelope is unknown"
                };
            }

            HashSet<Vector2Int> authoredVoid = CollectAuthoredVoidCells(
                plan,
                out int vistaLaneCells,
                out int shaftAndSpanCells,
                out int promontoryCells);

            int floorInside = 0;
            foreach (Vector2Int cell in layout.floorCells)
            {
                if (envelope.Contains(cell))
                {
                    floorInside++;
                }
            }

            int envelopeArea = Mathf.Max(1, envelope.width * envelope.height);
            CollectVoidComponents(
                layout.floorCells,
                authoredVoid,
                envelope,
                out List<int> componentSizes,
                out int maxComponentCells,
                out int componentsLargerThanOneCell,
                out int totalVoidCells,
                out int envelopeEdgeComponentCells,
                out int largestEnclosedComponentCells);

            return new JObject
            {
                ["available"] = true,
                ["measurementVersion"] = DensityMeasurementVersion,
                ["latticeEnvelope"] = RectToken(envelope),
                ["latticeEnvelopeAreaCells"] = envelopeArea,
                ["floorCellsInsideEnvelope"] = floorInside,
                ["floorCellsOutsideEnvelope"] = layout.floorCells.Count - floorInside,
                ["latticeEnvelopeFillPercent"] = floorInside / (float)envelopeArea * 100f,
                ["authoredVoid"] = new JObject
                {
                    ["cellCount"] = authoredVoid.Count,
                    ["vistaLaneCells"] = vistaLaneCells,
                    ["stairwellShaftAndAerialSpanCells"] = shaftAndSpanCells,
                    ["promontoryAndPierCells"] = promontoryCells
                },
                ["voidComponents"] = new JObject
                {
                    ["componentCount"] = componentSizes.Count,
                    ["maxComponentCells"] = maxComponentCells,
                    ["componentsLargerThanOneCell"] = componentsLargerThanOneCell,
                    ["totalVoidCells"] = totalVoidCells,
                    ["envelopeEdgeComponentCells"] = envelopeEdgeComponentCells,
                    ["largestEnclosedComponentCells"] = largestEnclosedComponentCells,
                    ["sizeDistribution"] = BuildIntDistribution(componentSizes)
                },
                ["measurement"] =
                    "latticeEnvelopeFillPercent is floor inside the lattice envelope over that envelope's area; " +
                    "unlike floorFillPercent it does not move when a promontory reaches outward. A void cell is a " +
                    "cell inside the envelope that is neither floor nor authored void; components are 4-connected " +
                    "and the envelope border bounds them (it is not an escape), so at a sparse setting the open " +
                    "field around the dungeon mass is itself one large component — envelopeEdgeComponentCells says " +
                    "how much of the void that is. Authored void is subtracted from the mask, so it separates " +
                    "components rather than joining them."
            };
        }

        // 4-connected flood fill over the non-floor, non-authored cells of the
        // lattice envelope. The envelope border is a wall, not an escape: the
        // metric is "how much of the space this dungeon was given is empty",
        // and at density 0 the answer is correctly "most of it, in one piece".
        private static void CollectVoidComponents(
            HashSet<Vector2Int> floorCells,
            HashSet<Vector2Int> authoredVoidCells,
            RectInt envelope,
            out List<int> componentSizes,
            out int maxComponentCells,
            out int componentsLargerThanOneCell,
            out int totalVoidCells,
            out int envelopeEdgeComponentCells,
            out int largestEnclosedComponentCells)
        {
            componentSizes = new List<int>();
            maxComponentCells = 0;
            componentsLargerThanOneCell = 0;
            totalVoidCells = 0;
            envelopeEdgeComponentCells = 0;
            largestEnclosedComponentCells = 0;

            var visited = new HashSet<Vector2Int>();
            var queue = new Queue<Vector2Int>();
            var component = new List<Vector2Int>();
            for (int y = envelope.yMin; y < envelope.yMax; y++)
            {
                for (int x = envelope.xMin; x < envelope.xMax; x++)
                {
                    var start = new Vector2Int(x, y);
                    if (floorCells.Contains(start) ||
                        authoredVoidCells.Contains(start) ||
                        !visited.Add(start))
                    {
                        continue;
                    }

                    component.Clear();
                    queue.Enqueue(start);
                    bool touchesEnvelopeEdge = false;
                    while (queue.Count > 0)
                    {
                        Vector2Int current = queue.Dequeue();
                        component.Add(current);
                        touchesEnvelopeEdge |=
                            current.x == envelope.xMin || current.x == envelope.xMax - 1 ||
                            current.y == envelope.yMin || current.y == envelope.yMax - 1;
                        foreach (Vector2Int direction in CardinalCellOffsets)
                        {
                            Vector2Int neighbor = current + direction;
                            if (!envelope.Contains(neighbor) ||
                                floorCells.Contains(neighbor) ||
                                authoredVoidCells.Contains(neighbor) ||
                                !visited.Add(neighbor))
                            {
                                continue;
                            }

                            queue.Enqueue(neighbor);
                        }
                    }

                    componentSizes.Add(component.Count);
                    totalVoidCells += component.Count;
                    maxComponentCells = Mathf.Max(maxComponentCells, component.Count);
                    if (component.Count > 1)
                    {
                        componentsLargerThanOneCell++;
                    }

                    if (touchesEnvelopeEdge)
                    {
                        envelopeEdgeComponentCells += component.Count;
                    }
                    else
                    {
                        largestEnclosedComponentCells =
                            Mathf.Max(largestEnclosedComponentCells, component.Count);
                    }
                }
            }

            componentSizes.Sort();
        }

        private static readonly Vector2Int[] CardinalCellOffsets =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1)
        };

        // The floorplan, readable without opening Unity. The window is the union
        // of the lattice envelope, the floor bounding box and the authored-void
        // extent, so an external connector reaching eight cells past the
        // envelope is drawn rather than clipped at the frame.
        private static JObject BuildFloorplanProjection(
            DungeonLayout layout,
            TieredLevelPlan plan)
        {
            var roomIdByCell = new Dictionary<Vector2Int, int>();
            for (int room = 0; room < layout.rooms.Count; room++)
            {
                foreach (Vector2Int cell in layout.rooms[room].cells)
                {
                    roomIdByCell[cell] = room;
                }
            }

            var vistaCells = new HashSet<Vector2Int>(
                plan.routeRequirementResolution.reservedVistaCells);
            HashSet<Vector2Int> authoredVoid = CollectAuthoredVoidCells(plan, out _, out _, out _);
            var promontoryCells = new HashSet<Vector2Int>(
                CollectNamedPromontoryCells(plan.namedPromontories));
            promontoryCells.UnionWith(CollectExternalConnectorPierCells(plan.externalConnectors));

            var drawn = new HashSet<Vector2Int>(layout.floorCells);
            drawn.UnionWith(authoredVoid);
            RectInt window = GetCellRect(drawn);
            bool hasEnvelope = TryResolveLatticeEnvelope(out RectInt envelope);
            if (hasEnvelope)
            {
                int minX = Mathf.Min(window.xMin, envelope.xMin);
                int minY = Mathf.Min(window.yMin, envelope.yMin);
                int maxX = Mathf.Max(window.xMax, envelope.xMax);
                int maxY = Mathf.Max(window.yMax, envelope.yMax);
                window = new RectInt(minX, minY, maxX - minX, maxY - minY);
            }

            var rows = new JArray();
            var line = new StringBuilder(window.width);
            for (int y = window.yMax - 1; y >= window.yMin; y--)
            {
                line.Length = 0;
                for (int x = window.xMin; x < window.xMax; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (layout.floorCells.Contains(cell))
                    {
                        line.Append(roomIdByCell.TryGetValue(cell, out int room)
                            ? RoomGlyph(room)
                            : FloorplanCorridorGlyph);
                        continue;
                    }

                    if (promontoryCells.Contains(cell))
                    {
                        line.Append(FloorplanPromontoryGlyph);
                        continue;
                    }

                    if (vistaCells.Contains(cell))
                    {
                        line.Append(FloorplanVistaVoidGlyph);
                        continue;
                    }

                    if (authoredVoid.Contains(cell))
                    {
                        line.Append(FloorplanSpanVoidGlyph);
                        continue;
                    }

                    line.Append(hasEnvelope && envelope.Contains(cell)
                        ? FloorplanVoidGlyph
                        : FloorplanOutsideGlyph);
                }

                rows.Add(line.ToString());
            }

            return new JObject
            {
                ["legend"] = FloorplanLegend,
                ["window"] = RectToken(window),
                ["latticeEnvelope"] = hasEnvelope ? RectToken(envelope) : (JToken)JValue.CreateNull(),
                ["topLeftCell"] = CellToken(new Vector2Int(window.xMin, window.yMax - 1)),
                ["rows"] = rows
            };
        }

        private static char RoomGlyph(int roomIndex)
        {
            if (roomIndex < 0)
            {
                return FloorplanOverflowRoomGlyph;
            }

            if (roomIndex < 10)
            {
                return (char)('0' + roomIndex);
            }

            if (roomIndex < 36)
            {
                return (char)('a' + (roomIndex - 10));
            }

            return FloorplanOverflowRoomGlyph;
        }

        // Corpus roll-up. §6 wants achieved fill and max void component per
        // topology, which is what tells a phase-4 run WHICH topology cannot
        // reach a density rather than only that something cannot.
        private sealed class VoidDensityAccumulator
        {
            public readonly List<int> fillPercentPerSeed = new List<int>();
            public readonly List<int> maxVoidComponentPerSeed = new List<int>();
            public readonly List<int> componentsLargerThanOneCellPerSeed = new List<int>();
            public readonly List<int> totalVoidCellsPerSeed = new List<int>();
            public readonly List<int> largestEnclosedComponentPerSeed = new List<int>();
        }

        private static void AccumulateVoidDensity(JObject density, VoidDensityAccumulator accumulator)
        {
            if (density?.Value<bool?>("available") != true)
            {
                return;
            }

            JObject components = density["voidComponents"] as JObject ?? new JObject();
            accumulator.fillPercentPerSeed.Add(
                Mathf.RoundToInt(density.Value<float?>("latticeEnvelopeFillPercent") ?? 0f));
            accumulator.maxVoidComponentPerSeed.Add(components.Value<int?>("maxComponentCells") ?? 0);
            accumulator.componentsLargerThanOneCellPerSeed.Add(
                components.Value<int?>("componentsLargerThanOneCell") ?? 0);
            accumulator.totalVoidCellsPerSeed.Add(components.Value<int?>("totalVoidCells") ?? 0);
            accumulator.largestEnclosedComponentPerSeed.Add(
                components.Value<int?>("largestEnclosedComponentCells") ?? 0);
        }

        private static JObject BuildVoidDensitySummary(VoidDensityAccumulator accumulator)
        {
            return new JObject
            {
                ["measurementVersion"] = DensityMeasurementVersion,
                ["seeds"] = accumulator.fillPercentPerSeed.Count,
                ["latticeEnvelopeFillPercentPerSeed"] =
                    BuildIntDistribution(accumulator.fillPercentPerSeed),
                ["maxVoidComponentCellsPerSeed"] =
                    BuildIntDistribution(accumulator.maxVoidComponentPerSeed),
                ["voidComponentsLargerThanOneCellPerSeed"] =
                    BuildIntDistribution(accumulator.componentsLargerThanOneCellPerSeed),
                ["totalVoidCellsPerSeed"] =
                    BuildIntDistribution(accumulator.totalVoidCellsPerSeed),
                ["largestEnclosedVoidComponentCellsPerSeed"] =
                    BuildIntDistribution(accumulator.largestEnclosedComponentPerSeed),
                ["measurement"] =
                    "fill percentages are rounded to whole percent so they share the integer distribution shape; " +
                    "per-seed exact values live in each seed report's measurements.density"
            };
        }
    }
}
