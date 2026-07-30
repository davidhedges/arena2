using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase A1 of the layered 3D topology design
    // (docs/dungeon-builder/layered-topology-design-2026-07-29.md §3.1, §8.1):
    // surface identity in the plan, and NOTHING that produces a second surface.
    //
    // The whole conceptual change is one substitution: a walkable place is
    // identified by (plan cell, level), not by plan cell. This file introduces
    // that identity and the two containers the design separates — the
    // PRE-elevation plan shadow and the POST-elevation surface field — plus the
    // shadow-agreement check that the design's H2 closure needs.
    //
    // A1 is deliberately capability-free. `SurfaceField` is backed by a
    // heightfield here, so `IsSingleLayer` is true by construction and
    // `AsHeightField()` hands back the very dictionary the pipeline already
    // builds. That is what makes the container split byte-identical: every
    // existing consumer of `Dictionary<Vector2Int,int>` keeps working unchanged.
    // The second implementation — a genuine surface list — arrives with the
    // phase that first produces two surfaces over one cell, at which point
    // `AsHeightField()` starts throwing and the remaining consumers get
    // migrated one at a time.
    internal sealed partial class DungeonLabGenerator
    {
        /// <summary>
        /// The canonical identity of a walkable place: (plan cell, level).
        /// </summary>
        /// <remarks>
        /// This is not a new identity — the floor/stair port graph has keyed its
        /// nodes on exactly this pair since it was written
        /// (<c>PortGraphNode.Floor</c>). A1 promotes it from an internal string
        /// convention to a type, so the rest of the generator can adopt it
        /// without inventing a second vocabulary. <see cref="Token"/> renders the
        /// historical string verbatim, which is why the port graph's node keys —
        /// and therefore every seed's traversal result — do not move.
        /// </remarks>
        private readonly struct SurfaceKey : IEquatable<SurfaceKey>
        {
            public readonly Vector2Int cell;
            public readonly int level;

            public SurfaceKey(Vector2Int cell, int level)
            {
                this.cell = cell;
                this.level = level;
            }

            /// <summary>
            /// The canonical rendering, `x,y,Llevel`. Stable and ordinal: it is a
            /// key, a diagnostic label and an RNG subject, so it must not depend
            /// on culture or on a struct's default ToString.
            /// </summary>
            public string Token =>
                $"{cell.x.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                $"{cell.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                $"L{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            public bool Equals(SurfaceKey other)
            {
                return cell == other.cell && level == other.level;
            }

            public override bool Equals(object obj)
            {
                return obj is SurfaceKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (cell.GetHashCode() * 397) ^ level;
                }
            }

            public override string ToString()
            {
                return Token;
            }

            /// <summary>Canonical order: x, then y, then level ascending.</summary>
            public static int Compare(SurfaceKey first, SurfaceKey second)
            {
                int byCell = CompareCells(first.cell, second.cell);
                return byCell != 0 ? byCell : first.level.CompareTo(second.level);
            }
        }

        /// <summary>
        /// A half-open vertical interval, `[minLevel, maxLevelExclusive)`.
        /// </summary>
        /// <remarks>
        /// Half-open on purpose (design review finding 3): a closed band rejects
        /// a legal clearance that ends exactly where the next one begins.
        /// </remarks>
        private readonly struct LevelBand
        {
            public readonly int minLevel;
            public readonly int maxLevelExclusive;

            public LevelBand(int minLevel, int maxLevelExclusive)
            {
                this.minLevel = minLevel;
                this.maxLevelExclusive = maxLevelExclusive;
            }

            /// <summary>
            /// A reservation that carries no elevation information at all.
            /// </summary>
            /// <remarks>
            /// This is what every reservation the generator makes today amounts
            /// to: the ledger's five cell sets constrain PLAN space and say
            /// nothing about height. Design §6 states today's semantics as
            /// exactly this special case — "every band is `[-∞, +∞)`" — and that
            /// is why Phase B is output-neutral: two bands that are both
            /// unbounded always intersect, so conflict behaves as it always has.
            /// A phase that gives a producer a real band opts that prism into
            /// the vertical rules with no further plumbing.
            /// </remarks>
            public static LevelBand Unbounded => new LevelBand(int.MinValue, int.MaxValue);

            /// <summary>A band with a known base that continues upward without limit.</summary>
            public static LevelBand From(int minLevel)
            {
                return new LevelBand(minLevel, int.MaxValue);
            }

            /// <summary>
            /// True when the band's base is a real level rather than "unknown".
            /// </summary>
            /// <remarks>
            /// The headroom rule can only judge a prism that says where it sits.
            /// An unbounded reservation is not "solid from the abyss upward" —
            /// it is a plan-space claim that has never been asked to declare a
            /// height, and treating its `int.MinValue` base as geometry would
            /// have every stair footprint violate the headroom of the very
            /// surface it carries.
            /// </remarks>
            public bool DeclaresBase => minLevel != int.MinValue;

            public bool IsEmpty => maxLevelExclusive <= minLevel;

            public bool Contains(int level)
            {
                return !IsEmpty && level >= minLevel && level < maxLevelExclusive;
            }

            public bool Intersects(LevelBand other)
            {
                if (IsEmpty || other.IsEmpty)
                {
                    return false;
                }

                return minLevel < other.maxLevelExclusive && other.minLevel < maxLevelExclusive;
            }

            /// <summary>
            /// The band a connection between two declared elevations occupies:
            /// the span of its endpoints plus the headroom a surface above it
            /// would need (design §8.1).
            /// </summary>
            public static LevelBand SpanningEndpoints(int firstLevel, int secondLevel)
            {
                return new LevelBand(
                    Mathf.Min(firstLevel, secondLevel),
                    Mathf.Max(firstLevel, secondLevel) + MinHeadroomLevels);
            }

            public override string ToString()
            {
                return $"[{minLevel},{maxLevelExclusive})";
            }
        }

        /// <summary>
        /// Where a <see cref="RoomConnection"/> came from.
        /// </summary>
        /// <remarks>
        /// The distinction is load-bearing and it is NOT "has an edge or not by
        /// accident": <c>AddLevelSafeLoopConnections</c> builds connections that
        /// carry no route intent at all, and the elevation path treats the route
        /// requirement as optional by design. So the invariant is "a RouteEdge
        /// connection resolves to exactly one edge; a SynthesizedLoop resolves to
        /// none" — never "every connection has an edge".
        /// </remarks>
        private enum ConnectionSource
        {
            RouteEdge,
            SynthesizedLoop
        }

        /// <summary>
        /// PRE-elevation. The 2-D domain the layout occupies.
        /// </summary>
        /// <remarks>
        /// This is today's <c>DungeonLayout.floorCells</c>, renamed in role
        /// rather than in storage. It is the DOMAIN the level field is computed
        /// over — <c>FillUnassignedFloorCells</c> floods within it and
        /// <c>CleanPath</c> filters against it — so it must exist before
        /// elevation and it cannot be derived from the surface field. The
        /// dependency runs shadow → surfaces, and the draft that inverted it was
        /// wrong (design review finding 1).
        /// </remarks>
        private sealed class PlanShadow
        {
            private readonly HashSet<Vector2Int> cells;

            public PlanShadow(HashSet<Vector2Int> cells)
            {
                this.cells = cells ?? new HashSet<Vector2Int>();
            }

            /// <summary>
            /// The live cell set. Handed back by reference on purpose: the
            /// layout stage builds and owns this set, and A1 changes how it is
            /// described, not who may touch it.
            /// </summary>
            public HashSet<Vector2Int> Cells => cells;

            public int Count => cells.Count;

            public bool Contains(Vector2Int cell)
            {
                return cells.Contains(cell);
            }
        }

        /// <summary>
        /// POST-elevation. The canonical walkable surfaces of a plan.
        /// </summary>
        /// <remarks>
        /// Backed by a heightfield in A1, so it carries at most one surface per
        /// cell and <see cref="AsHeightField"/> is always valid. Consumers that
        /// mean "a walkable place" should migrate to <see cref="Surfaces"/> and
        /// <see cref="LevelsAt"/>; consumers that mean "a column of plan space"
        /// keep asking for cells and stay correct forever.
        /// </remarks>
        private sealed class SurfaceField
        {
            private readonly Dictionary<Vector2Int, int> heightField;
            private SurfaceKey[] sortedSurfaces;

            public SurfaceField(Dictionary<Vector2Int, int> heightField)
            {
                this.heightField = heightField ?? new Dictionary<Vector2Int, int>();
            }

            public int Count => heightField.Count;

            /// <summary>
            /// True while every plan cell carries at most one surface. Constant
            /// in A1 because the backing store cannot express anything else;
            /// stated as a property rather than assumed so the phase that breaks
            /// it breaks it in exactly one place.
            /// </summary>
            public bool IsSingleLayer => true;

            /// <summary>
            /// The heightfield view. Valid only while <see cref="IsSingleLayer"/>.
            /// </summary>
            /// <remarks>
            /// Returns the backing dictionary itself, not a copy: this is the
            /// migration shim, and it exists so that the ~20 sites that consume
            /// <c>Dictionary&lt;Vector2Int,int&gt;</c> keep compiling and keep
            /// producing byte-identical output while the identity underneath
            /// them changes. The concrete return type is deliberate for the same
            /// reason — several consumers take the concrete dictionary.
            /// </remarks>
            public Dictionary<Vector2Int, int> AsHeightField()
            {
                if (!IsSingleLayer)
                {
                    throw new InvalidOperationException(
                        "AsHeightField() is valid only on a single-layer surface field");
                }

                return heightField;
            }

            /// <summary>The plan shadow this field actually occupies.</summary>
            public HashSet<Vector2Int> PlanCells()
            {
                return new HashSet<Vector2Int>(heightField.Keys);
            }

            /// <summary>Every surface, in canonical (x, y, level) order.</summary>
            public IReadOnlyList<SurfaceKey> Surfaces()
            {
                if (sortedSurfaces != null)
                {
                    return sortedSurfaces;
                }

                var surfaces = new List<SurfaceKey>(heightField.Count);
                foreach (KeyValuePair<Vector2Int, int> item in heightField)
                {
                    surfaces.Add(new SurfaceKey(item.Key, item.Value));
                }

                surfaces.Sort(SurfaceKey.Compare);
                sortedSurfaces = surfaces.ToArray();
                return sortedSurfaces;
            }

            /// <summary>The levels present at one plan cell, ascending.</summary>
            public IReadOnlyList<int> LevelsAt(Vector2Int cell)
            {
                return heightField.TryGetValue(cell, out int level)
                    ? new[] { level }
                    : Array.Empty<int>();
            }

            public bool TryGetSurfaceAt(Vector2Int cell, out SurfaceKey surface)
            {
                if (heightField.TryGetValue(cell, out int level))
                {
                    surface = new SurfaceKey(cell, level);
                    return true;
                }

                surface = default;
                return false;
            }
        }

        /// <summary>
        /// The two-sided difference between a plan's surfaces and its shadow.
        /// </summary>
        private readonly struct PlanShadowDisagreement
        {
            // A surface whose plan cell is missing from the shadow. This is the
            // architecture review's H2: promontory passes add cells to the level
            // field and never to floorCells, so every metric computed from the
            // shadow describes a dungeon missing its piers.
            public readonly Vector2Int[] surfacedCellsOutsideShadow;
            // A shadow cell that resolved no surface. External span gaps are the
            // known legitimate producer — the gap under a span deck stays a gap —
            // so this side is reported, not assumed to be a defect.
            public readonly Vector2Int[] shadowCellsWithoutSurface;

            public PlanShadowDisagreement(
                Vector2Int[] surfacedCellsOutsideShadow,
                Vector2Int[] shadowCellsWithoutSurface)
            {
                this.surfacedCellsOutsideShadow =
                    surfacedCellsOutsideShadow ?? Array.Empty<Vector2Int>();
                this.shadowCellsWithoutSurface =
                    shadowCellsWithoutSurface ?? Array.Empty<Vector2Int>();
            }

            /// <summary>
            /// The gated half: every surface's plan cell is in the shadow.
            /// </summary>
            /// <remarks>
            /// One-directional on purpose, settled in A2. The other side — a
            /// shadow cell that resolved no surface — is legitimate: the gap
            /// under an external span deck stays a gap, and the shadow is the
            /// DOMAIN the level field floods within, so those cells must remain.
            /// It is reported as a count so a NEW producer of unsurfaced shadow
            /// is still visible, but it is not a defect.
            /// </remarks>
            public bool Agrees => surfacedCellsOutsideShadow.Length == 0;

            public bool IsTwoSided =>
                surfacedCellsOutsideShadow.Length == 0 && shadowCellsWithoutSurface.Length == 0;
        }

        /// <summary>
        /// The A2 repair: every surface's plan cell joins the shadow.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The invariant is stated at the END of planning, and that is the only
        /// place it can be applied. The design's prose says "any pass that adds a
        /// surface must add its plan cell to the shadow in the same step", and
        /// doing that literally would change what LATER passes see:
        /// <c>BuildExternalConnectorCandidates</c> derives <c>coreExtent</c> and
        /// its outer-face test from <c>layout.floorCells</c>, so a named vista
        /// promontory added at its own producer would move the core's outer face
        /// and re-pick the connector anchors — geometry moving well beyond the
        /// shadow, which is exactly what A2's isolation exit forbids. External
        /// promontories are the final plan mutation, so calling this immediately
        /// after them satisfies the invariant where it is stated and leaves no
        /// reader of the shadow downstream of the write.
        /// </para>
        /// <para>
        /// It adds every surfaced cell rather than enumerating the two known
        /// producers (named vista promontories, external connector piers). That
        /// makes the invariant true by construction instead of true by a list
        /// somebody has to remember to extend.
        /// </para>
        /// <para>
        /// The other side of the disagreement is deliberately NOT repaired. A
        /// shadow cell with no surface is legitimate — the gap under an external
        /// span deck stays a gap — and the shadow is the DOMAIN the level field
        /// floods within, so removing those cells would change what
        /// <c>FillUnassignedFloorCells</c> and <c>CleanPath</c> operate over.
        /// Agreement is therefore one-directional: surfaces ⊆ shadow.
        /// </para>
        /// </remarks>
        /// <returns>How many cells the shadow gained.</returns>
        private static int ReconcilePlanShadowWithSurfaces(
            DungeonLayout layout,
            IReadOnlyDictionary<Vector2Int, int> cellLevels)
        {
            HashSet<Vector2Int> shadow = layout.floorCells;
            if (shadow == null || cellLevels == null)
            {
                return 0;
            }

            int added = 0;
            foreach (KeyValuePair<Vector2Int, int> surface in cellLevels)
            {
                if (shadow.Add(surface.Key))
                {
                    added++;
                }
            }

            return added;
        }

        /// <summary>
        /// Shadow agreement (design §3.1): `surfaceField.PlanCells()` must equal
        /// `planShadow.cells` at the end of planning.
        /// </summary>
        /// <remarks>
        /// DETECTED in A1 and REPAIRED in A2, and the split is not fastidiousness:
        /// <c>BuildCanonicalLayoutProjection</c> serializes the shadow into
        /// `layoutHash`, which is mixed into `canonicalHash`, so adding the
        /// missing cells necessarily moves every affected seed. A1 gates on the
        /// hash holding; it therefore cannot also repair this. The result is
        /// reported OUT OF BAND — a seed report cannot carry it either, because
        /// `resultHash` is SHA-256 over the whole seed-report array.
        /// </remarks>
        private static PlanShadowDisagreement DetectPlanShadowDisagreement(
            DungeonLayout layout,
            TieredLevelPlan plan)
        {
            HashSet<Vector2Int> shadow = layout.floorCells ?? new HashSet<Vector2Int>();
            Dictionary<Vector2Int, int> surfaces =
                plan.cellLevels ?? new Dictionary<Vector2Int, int>();

            var surfacedOutside = new List<Vector2Int>();
            foreach (KeyValuePair<Vector2Int, int> item in surfaces)
            {
                if (!shadow.Contains(item.Key))
                {
                    surfacedOutside.Add(item.Key);
                }
            }

            var shadowWithout = new List<Vector2Int>();
            foreach (Vector2Int cell in shadow)
            {
                if (!surfaces.ContainsKey(cell))
                {
                    shadowWithout.Add(cell);
                }
            }

            surfacedOutside.Sort(CompareCells);
            shadowWithout.Sort(CompareCells);
            return new PlanShadowDisagreement(surfacedOutside.ToArray(), shadowWithout.ToArray());
        }

        /// <summary>
        /// The connection-identity invariant (design §8.1, rejection code
        /// `CONNECTION_IDENTITY`), evaluated as a diagnostic.
        /// </summary>
        /// <remarks>
        /// Not a rejection in A1. A new gate can only change which seeds are
        /// accepted, and A1's whole claim is that nothing changes; so the
        /// violations are collected and reported out of band, next to the shadow
        /// disagreement, and promoting them to a rejection code belongs to the
        /// phase that is allowed to move the baseline.
        /// </remarks>
        private static List<string> FindConnectionIdentityViolations(
            DungeonLayout layout,
            RouteTierRequirements routeRequirements)
        {
            var violations = new List<string>();
            if (layout.connections == null)
            {
                return violations;
            }

            var seenConnectionIds = new HashSet<string>(StringComparer.Ordinal);
            var seenEdgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (RoomConnection connection in layout.connections)
            {
                if (string.IsNullOrEmpty(connection.connectionId))
                {
                    violations.Add(
                        $"connection {connection.fromRoom}->{connection.toRoom} carried no connectionId");
                }
                else if (!seenConnectionIds.Add(connection.connectionId))
                {
                    violations.Add($"duplicate connectionId '{connection.connectionId}'");
                }

                if (connection.source == ConnectionSource.RouteEdge)
                {
                    if (string.IsNullOrEmpty(connection.edgeId))
                    {
                        violations.Add(
                            $"RouteEdge connection '{connection.connectionId}' resolved no route edge");
                    }
                    else
                    {
                        if (!seenEdgeIds.Add(connection.edgeId))
                        {
                            violations.Add(
                                $"route edge '{connection.edgeId}' was claimed by more than one connection");
                        }

                        if (routeRequirements != null &&
                            !routeRequirements.TryGetTransition(connection.edgeId, out _))
                        {
                            violations.Add(
                                $"connection '{connection.connectionId}' named absent route edge '{connection.edgeId}'");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(connection.edgeId))
                {
                    violations.Add(
                        $"SynthesizedLoop connection '{connection.connectionId}' resolved route edge '{connection.edgeId}'");
                }
            }

            return violations;
        }
    }
}
