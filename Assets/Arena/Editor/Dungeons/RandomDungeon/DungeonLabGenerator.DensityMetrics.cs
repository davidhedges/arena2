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

        // Reporting receives the accepted intent explicitly. The embedding
        // coordinates remain diagnostic state, but a stale diagnostic intent
        // can no longer select the topology settings used by an accepted report.
        private static bool TryResolveLatticeEnvelope(
            RouteIntent intent,
            out RectInt envelope)
        {
            return TryResolveLatticeEnvelope(intent, out envelope, out _);
        }

        private static bool TryResolveLatticeEnvelope(
            RouteIntent intent,
            out RectInt envelope,
            out RectInt[] nodeEnvelopes)
        {
            envelope = default;
            nodeEnvelopes = System.Array.Empty<RectInt>();
            if (intent == null ||
                lastNodeCenters == null ||
                lastNodeCenters.Length == 0 ||
                lastNodeCenters.Length != intent.nodes.Length)
            {
                return false;
            }

            DungeonPatternSpatialSettings spatial =
                ResolveTopologySpatialSettings(intent.topology);
            envelope = LatticeEnvelopeFor(lastNodeCenters, spatial);
            nodeEnvelopes = new RectInt[lastNodeCenters.Length];
            for (int node = 0; node < lastNodeCenters.Length; node++)
            {
                nodeEnvelopes[node] = RoomEnvelope(lastNodeCenters[node], spatial);
            }

            return true;
        }

        // Authored void survives at every density and must not be counted as a
        // hole to be filled (design §4.1):
        //
        //   * the reserved vista sight lane, exactly as the route planner
        //     reserved it;
        //   * stairwell shafts — a stairwell tower stands on void BESIDE the
        //     path, so its folded footprint is deliberately empty floor mask;
        //   * planned span footprints — a bridge over filled floor is a walkway
        //     (fork F2), so the span keeps its void;
        //   * promontory and external-connector pier cells, which are surfaces
        //     the tier stage adds after the layout floor mask is closed and
        //     which would otherwise read as holes.
        //
        // Everything here is an exact cell set the plan already carries. It is
        // deliberately NOT a dilation or a guess: the flanking void beside an
        // planned deck is still counted today, and gets revisited in phase 4/6
        // when the number actually binds.
        private static HashSet<Vector2Int> CollectAuthoredVoidCells(
            DungeonLayout layout,
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

            // The shaft window the layout kept clear beside each transition
            // corridor. §4.1 calls a stairwell shaft authored void, and it is
            // authored whether or not a tower ended up standing in it — the
            // whole point of the reservation is that the placer has somewhere to
            // go. Counting only the placed footprint (below) measured the ones
            // that were USED and called the rest a hole.
            foreach (Vector2Int cell in
                     layout.reservedShaftCells ?? System.Array.Empty<Vector2Int>())
            {
                authored.Add(cell);
            }

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

                // The gap an aerial span CROSSES, not just the piece that sits
                // on it. Fork F2 keeps that void on purpose — a bridge over
                // filled floor is a walkway — and the deck is added by the tier
                // stage, so the layout floor mask the metric reads is empty
                // underneath it by construction. Counting only footprintCells
                // measured a deliberate hole as a defect: it was the single
                // 7-cell component left in all 200 seeds at density 5, and the
                // reason §5's tolerance appeared unreachable.
                if (string.Equals(
                        transition.placementClass,
                        ExternalSpanStairPlacementClass,
                        System.StringComparison.Ordinal))
                {
                    AddSpanCrossingCells(transition, authored);
                }
            }

            // A dais reads as backed against an exterior wall only while the
            // cells behind it are empty, and TryValidateAcceptedRecipes enforces
            // that — so the backdrop is authored void in exactly the sense §4.1
            // means, and filling it fails the seed. It was the LAST hole left at
            // density 5: one 7-cell strip in all 200 seeds, which is the width
            // of the reviewed landmark's dais.
            foreach (RecipeResolution resolution in
                     plan.recipeResolutions ?? System.Array.Empty<RecipeResolution>())
            {
                // A resolution with no showpiece carries a DEFAULT reservation,
                // whose arrays are null — the constructor's null-coalescing only
                // runs when one is actually built.
                foreach (Vector2Int cell in
                         resolution.showpieceReservation.backdropVoidCells ??
                         System.Array.Empty<Vector2Int>())
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

        /// <summary>
        /// What the void IS, cell by cell — as opposed to how big its holes are.
        /// </summary>
        /// <remarks>
        /// <c>voidComponents</c> says a seed has a 600-cell hole; it cannot say
        /// whether that hole is channel around rooms or craters between them,
        /// and those are removed by different mechanisms. This splits every
        /// non-floor cell of the lattice envelope three ways:
        /// <list type="bullet">
        /// <item><b>channel</b> — inside some node's placement envelope. The
        /// ring of open air around a room, which M2 (pack) closes.</item>
        /// <item><b>vacant</b> — outside every node envelope. The ~9x9 craters
        /// at lattice cells no node occupies, which is M3's (annex) target and
        /// the half of §2's measurement that packing cannot reach.</item>
        /// <item><b>authored</b> — vista lane, stairwell shafts, aerial span
        /// footprints, promontory piers: void that survives at every density by
        /// design (§4.1), already excluded from <c>voidComponents</c>.</item>
        /// </list>
        /// The three partition the envelope's non-floor cells exactly, so
        /// channel + vacant is the void the dial is allowed to remove. Floor is
        /// split on the same boundary, because floor outside every node
        /// envelope is exactly the corridors plus whatever M3 annexed — which
        /// is how you tell annexation from a room simply growing.
        /// </remarks>
        // The span's own extent: the box its two ends and its footprint span.
        // A span is straight and cardinal, so the box is the run and nothing
        // else — this cannot quietly grow into the rooms either side.
        private static void AddSpanCrossingCells(
            ElevationEdgeModel.TransitionEdge transition,
            HashSet<Vector2Int> authored)
        {
            int minX = Mathf.Min(transition.firstCell.x, transition.secondCell.x);
            int maxX = Mathf.Max(transition.firstCell.x, transition.secondCell.x);
            int minY = Mathf.Min(transition.firstCell.y, transition.secondCell.y);
            int maxY = Mathf.Max(transition.firstCell.y, transition.secondCell.y);
            foreach (Vector2Int cell in transition.footprintCells)
            {
                minX = Mathf.Min(minX, cell.x);
                maxX = Mathf.Max(maxX, cell.x);
                minY = Mathf.Min(minY, cell.y);
                maxY = Mathf.Max(maxY, cell.y);
            }

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    authored.Add(new Vector2Int(x, y));
                }
            }
        }

        private static JObject BuildVoidDecomposition(
            HashSet<Vector2Int> floorCells,
            HashSet<Vector2Int> authoredVoidCells,
            RectInt envelope,
            IReadOnlyList<RectInt> nodeEnvelopes)
        {
            int channelVoid = 0;
            int vacantVoid = 0;
            int authoredVoid = 0;
            int envelopeFloor = 0;
            int vacantFloor = 0;
            int insideNodeEnvelopes = 0;
            for (int y = envelope.yMin; y < envelope.yMax; y++)
            {
                for (int x = envelope.xMin; x < envelope.xMax; x++)
                {
                    var cell = new Vector2Int(x, y);
                    bool insideNodeEnvelope = false;
                    foreach (RectInt nodeEnvelope in nodeEnvelopes)
                    {
                        if (nodeEnvelope.Contains(cell))
                        {
                            insideNodeEnvelope = true;
                            break;
                        }
                    }

                    if (insideNodeEnvelope)
                    {
                        insideNodeEnvelopes++;
                    }

                    if (floorCells.Contains(cell))
                    {
                        envelopeFloor++;
                        if (!insideNodeEnvelope)
                        {
                            vacantFloor++;
                        }

                        continue;
                    }

                    if (authoredVoidCells.Contains(cell))
                    {
                        authoredVoid++;
                    }
                    else if (insideNodeEnvelope)
                    {
                        channelVoid++;
                    }
                    else
                    {
                        vacantVoid++;
                    }
                }
            }

            int envelopeArea = Mathf.Max(1, envelope.width * envelope.height);
            int outsideNodeEnvelopes = envelopeArea - insideNodeEnvelopes;
            return new JObject
            {
                ["nodeEnvelopeCells"] = insideNodeEnvelopes,
                ["outsideNodeEnvelopeCells"] = outsideNodeEnvelopes,
                ["channelVoidCells"] = channelVoid,
                ["vacantVoidCells"] = vacantVoid,
                ["authoredVoidCells"] = authoredVoid,
                ["floorCellsInsideNodeEnvelopes"] = envelopeFloor - vacantFloor,
                ["floorCellsOutsideNodeEnvelopes"] = vacantFloor,
                ["channelVoidPercentOfNodeEnvelopes"] =
                    channelVoid / (float)Mathf.Max(1, insideNodeEnvelopes) * 100f,
                ["vacantVoidPercentOutsideNodeEnvelopes"] =
                    vacantVoid / (float)Mathf.Max(1, outsideNodeEnvelopes) * 100f,
                ["measurement"] =
                    "every non-floor cell of the lattice envelope attributed to channel (inside a node's " +
                    "9x9 placement envelope — M2's target), vacant (outside every node envelope, the lattice " +
                    "craters — M3's target) or authored (vista lane, stairwell shaft, aerial span, pier — " +
                    "kept at every density). Floor is split on the same boundary, so " +
                    "floorCellsOutsideNodeEnvelopes is corridor plus annexed area."
            };
        }

        private static JObject BuildVoidDensityMeasurements(
            DungeonLayout layout,
            TieredLevelPlan plan,
            RouteIntent intent)
        {
            if (!TryResolveLatticeEnvelope(
                    intent,
                    out RectInt envelope,
                    out RectInt[] nodeEnvelopes))
            {
                return new JObject
                {
                    ["available"] = false,
                    ["reason"] = "no route embedding was recorded, so the lattice envelope is unknown"
                };
            }

            HashSet<Vector2Int> authoredVoid = CollectAuthoredVoidCells(
                layout,
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
                    ["reservedShaftCells"] = layout.reservedShaftCells?.Count ?? 0,
                    ["stairwellShaftAndAerialSpanCells"] = shaftAndSpanCells,
                    ["promontoryAndPierCells"] = promontoryCells
                },
                ["voidDecomposition"] = BuildVoidDecomposition(
                    layout.floorCells,
                    authoredVoid,
                    envelope,
                    nodeEnvelopes),
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
            TieredLevelPlan plan,
            RouteIntent intent)
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
            HashSet<Vector2Int> authoredVoid = CollectAuthoredVoidCells(layout, plan, out _, out _, out _);
            var promontoryCells = new HashSet<Vector2Int>(
                CollectNamedPromontoryCells(plan.namedPromontories));
            promontoryCells.UnionWith(CollectExternalConnectorPierCells(plan.externalConnectors));

            var drawn = new HashSet<Vector2Int>(layout.floorCells);
            drawn.UnionWith(authoredVoid);
            RectInt window = GetCellRect(drawn);
            bool hasEnvelope = TryResolveLatticeEnvelope(intent, out RectInt envelope);
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
            public readonly List<int> channelVoidCellsPerSeed = new List<int>();
            public readonly List<int> vacantVoidCellsPerSeed = new List<int>();
            public readonly List<int> annexedFloorCellsPerSeed = new List<int>();
            public readonly List<int> channelVoidPercentPerSeed = new List<int>();
            public readonly List<int> vacantVoidPercentPerSeed = new List<int>();
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

            JObject decomposition = density["voidDecomposition"] as JObject ?? new JObject();
            accumulator.channelVoidCellsPerSeed.Add(
                decomposition.Value<int?>("channelVoidCells") ?? 0);
            accumulator.vacantVoidCellsPerSeed.Add(
                decomposition.Value<int?>("vacantVoidCells") ?? 0);
            accumulator.annexedFloorCellsPerSeed.Add(
                decomposition.Value<int?>("floorCellsOutsideNodeEnvelopes") ?? 0);
            accumulator.channelVoidPercentPerSeed.Add(
                Mathf.RoundToInt(decomposition.Value<float?>("channelVoidPercentOfNodeEnvelopes") ?? 0f));
            accumulator.vacantVoidPercentPerSeed.Add(
                Mathf.RoundToInt(
                    decomposition.Value<float?>("vacantVoidPercentOutsideNodeEnvelopes") ?? 0f));
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
                ["channelVoidCellsPerSeed"] =
                    BuildIntDistribution(accumulator.channelVoidCellsPerSeed),
                ["vacantVoidCellsPerSeed"] =
                    BuildIntDistribution(accumulator.vacantVoidCellsPerSeed),
                ["floorCellsOutsideNodeEnvelopesPerSeed"] =
                    BuildIntDistribution(accumulator.annexedFloorCellsPerSeed),
                ["channelVoidPercentOfNodeEnvelopesPerSeed"] =
                    BuildIntDistribution(accumulator.channelVoidPercentPerSeed),
                ["vacantVoidPercentOutsideNodeEnvelopesPerSeed"] =
                    BuildIntDistribution(accumulator.vacantVoidPercentPerSeed),
                ["measurement"] =
                    "fill percentages are rounded to whole percent so they share the integer distribution shape; " +
                    "per-seed exact values live in each seed report's measurements.density"
            };
        }
    }
}
