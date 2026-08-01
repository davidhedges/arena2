using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    /// <summary>
    /// What a walkable surface IS (design §3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// C1b part 1 gave <c>SurfaceField</c> a stacked backing store but left a
    /// surface a bare <c>int</c>, which makes one of the two halves of
    /// <c>IsGroundBacked</c> inexpressible. §7.1 defines it as "<c>kind ==
    /// Floor</c> AND lowest in its column", and both halves carry weight: a
    /// bridge deck over a true gap IS the lowest thing in its column and must
    /// not become a solid pillar rising from the abyss. Without a kind, the only
    /// thing that keeps that from happening is the renderer's
    /// <c>aerialDeckCellLevels</c> side table — a side table that exists
    /// precisely because the surface could not say what it was.
    /// </para>
    /// <para>
    /// Public because the renderer is a separate class and takes surfaces across
    /// that boundary. Editor-only either way.
    /// </para>
    /// </remarks>
    public enum SurfaceKind
    {
        /// <summary>Rests on fill. The only kind that contributes ground mass.</summary>
        Floor,

        /// <summary>A span deck: walkable, suspended, no mass beneath it.</summary>
        Deck,

        /// <summary>A stair landing.</summary>
        Landing,

        /// <summary>A shelf hung off a wall or a slab over a chamber.</summary>
        Ledge,

        /// <summary>Occupied by a stair body.</summary>
        Stair
    }

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
        /// <para>
        /// A1 backed this with a bare heightfield, so it could not express a
        /// second surface at all. C1b gives it a real one: the heightfield still
        /// holds the LOWEST surface in every column — which is what keeps
        /// <see cref="AsHeightField"/> byte-identical for the whole existing
        /// pipeline — and an overlay carries anything stacked above it.
        /// </para>
        /// <para>
        /// Production never touches the overlay. Nothing in the generator calls
        /// <see cref="AddSurface"/>; only the C1 fixture does. That is what makes
        /// this change output-neutral by construction rather than by measurement,
        /// and it is also why <see cref="AsHeightField"/> may keep throwing on a
        /// stacked field without stranding the ~250 consumers that still take a
        /// dictionary: while generation is single-layer, none of them can ever
        /// be handed a stacked field. Those consumers migrate in the phase that
        /// first GENERATES a second surface, not this one.
        /// </para>
        /// </remarks>
        private sealed class SurfaceField
        {
            // The column FLOOR — the lowest surface that RESTS ON FILL. Kept as
            // the original concrete dictionary so `AsHeightField()` can hand back
            // the very instance the pipeline built.
            //
            // "Floor", not "lowest surface", and that distinction is the whole
            // content of `SurfaceKind`. A span deck is walkable and suspended
            // with no mass under it, so a column whose only surface is a deck
            // has NO FLOOR. Putting one in the heightfield instead would make
            // every floor-scoped reader wrong at once: the flood fill would seed
            // at deck height, a doorway would open onto thin air beside a span,
            // the plan shadow would swallow the gap the deck crosses, and the
            // overlook stat would count stepping off a bridge as a cliff.
            private readonly Dictionary<Vector2Int, int> heightField;
            // Every SUSPENDED surface, per cell, ascending: whatever stands over
            // a column floor, plus whatever stands in a column that has none.
            // Null until one exists, so the single-layer case allocates nothing.
            private Dictionary<Vector2Int, List<StackedSurfaceEntry>> stacked;
            // Kinds that are NOT Floor, keyed by surface. Sparse on purpose:
            // every surface in the heightfield is a Floor by construction, so an
            // unstacked field allocates nothing here either.
            private Dictionary<SurfaceKey, SurfaceKind> kinds;
            private SurfaceKey[] sortedSurfaces;

            public SurfaceField(Dictionary<Vector2Int, int> heightField)
            {
                this.heightField = heightField ?? new Dictionary<Vector2Int, int>();
            }

            private readonly struct StackedSurfaceEntry
            {
                public readonly int level;
                public readonly SurfaceKind kind;

                public StackedSurfaceEntry(int level, SurfaceKind kind)
                {
                    this.level = level;
                    this.kind = kind;
                }
            }

            /// <summary>
            /// The number of FLOORED plan cells — the ground shadow's size, not
            /// the surface count.
            /// </summary>
            /// <remarks>
            /// Distinct from <see cref="Count"/> and both are wanted: a plan-space
            /// extent or a "how big is this floorplan" figure means cells, while a
            /// vertical metric means surfaces. They differ only once stacked,
            /// which is exactly when picking the wrong one stops being harmless.
            /// </remarks>
            public int FlooredCellCount => heightField.Count;

            /// <summary>The number of SURFACES, which exceeds the cell count once stacked.</summary>
            public int Count
            {
                get
                {
                    int count = heightField.Count;
                    if (stacked != null)
                    {
                        foreach (KeyValuePair<Vector2Int, List<StackedSurfaceEntry>> column in stacked)
                        {
                            count += column.Value.Count;
                        }
                    }

                    return count;
                }
            }

            /// <summary>
            /// True while every plan cell carries at most one surface.
            /// </summary>
            /// <remarks>
            /// Computed now rather than constant. A1 hard-coded it because the
            /// backing store could not express anything else; the store can, so
            /// the answer has to come from the data.
            /// </remarks>
            public bool IsSingleLayer => stacked == null || stacked.Count == 0;

            /// <summary>
            /// Add a walkable surface to a column: the heightfield takes it when
            /// it is a <see cref="SurfaceKind.Floor"/> lower than the column's
            /// current one, and the suspended overlay takes it otherwise.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The heightfield holds the column's FLOOR, so only a
            /// <see cref="SurfaceKind.Floor"/> can ever enter it. A suspended
            /// surface — a deck, a gallery slab, a ledge — goes to the overlay
            /// whether or not the column has a floor beneath it, which is what
            /// lets a span deck exist over a true gap without inventing ground
            /// under it.
            /// </para>
            /// <para>
            /// A Floor added BELOW the column's current floor takes its place and
            /// pushes the old one up into the overlay, because "the floor" means
            /// the lowest one and <c>IsGroundBacked</c> is defined against it.
            /// </para>
            /// </remarks>
            public void AddSurface(Vector2Int cell, int level, SurfaceKind kind)
            {
                sortedSurfaces = null;
                bool restsOnFill = kind == SurfaceKind.Floor;
                if (restsOnFill && !heightField.TryGetValue(cell, out _))
                {
                    heightField[cell] = level;
                    SetKind(new SurfaceKey(cell, level), kind);
                    return;
                }

                if (restsOnFill && heightField[cell] == level)
                {
                    SetKind(new SurfaceKey(cell, level), kind);
                    return;
                }

                stacked ??= new Dictionary<Vector2Int, List<StackedSurfaceEntry>>();
                if (!stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above))
                {
                    above = new List<StackedSurfaceEntry>(1);
                    stacked[cell] = above;
                }

                if (restsOnFill && level < heightField[cell])
                {
                    // The new floor is now the column's, and the old one is
                    // stacked above it. The kinds move with their levels.
                    int displacedLevel = heightField[cell];
                    SurfaceKind displacedKind = KindAt(cell, displacedLevel);
                    heightField[cell] = level;
                    SetKind(new SurfaceKey(cell, level), kind);
                    level = displacedLevel;
                    kind = displacedKind;
                }

                for (int index = 0; index < above.Count; index++)
                {
                    if (above[index].level == level)
                    {
                        above[index] = new StackedSurfaceEntry(level, kind);
                        SetKind(new SurfaceKey(cell, level), kind);
                        return;
                    }
                }

                above.Add(new StackedSurfaceEntry(level, kind));
                above.Sort((first, second) => first.level.CompareTo(second.level));
                SetKind(new SurfaceKey(cell, level), kind);
            }

            /// <summary>
            /// Add a corridor's walking surface to a column, letting the column
            /// decide what it is: the LOWEST such surface is the floor and
            /// everything over it is a suspended <see cref="SurfaceKind.Ledge"/>.
            /// </summary>
            /// <remarks>
            /// <para>
            /// D2's producer, and the reason it is not the design's literal
            /// <c>AddSurface(cell, level, SurfaceKind.Ledge)</c>: the kind is not
            /// a property of "being a corridor", it is a property of WHERE the
            /// corridor landed in the column. A layer-bound corridor's cells are
            /// mostly its own — nothing else is there, and the corridor is the
            /// ground — so a constant <c>Ledge</c> would suspend a surface in a
            /// column with no floor under it, which the renderer refuses
            /// (<c>SurfaceColumns</c> throws) and which is what a bridge is for.
            /// </para>
            /// <para>
            /// The last branch is order-independence rather than tidiness. Which
            /// of two crossing corridors resolves first is the order the topology
            /// author happened to list their edges in, and the geometry must not
            /// depend on it — so a corridor arriving BELOW the column's floor
            /// takes the floor and suspends what it displaced.
            /// </para>
            /// <para>
            /// It can only ever displace another CORRIDOR's surface, and that is
            /// a property of the claim rules rather than of this method: a
            /// corridor never writes its endpoint rooms' cells, and the
            /// third-room relaxation admits a crossing only STRICTLY ABOVE the
            /// room's floor. A room's floor is ground-backed by construction and
            /// suspending it would take its wall mass away.
            /// </para>
            /// </remarks>
            public void AddCorridorSurface(Vector2Int cell, int level)
            {
                if (!heightField.TryGetValue(cell, out int floor))
                {
                    // Nothing here: this corridor IS the ground.
                    sortedSurfaces = null;
                    heightField[cell] = level;
                    SetKind(new SurfaceKey(cell, level), SurfaceKind.Floor);
                    return;
                }

                if (floor == level)
                {
                    // Already this column's floor — the same surface, claimed by
                    // two connections that agree about its elevation.
                    return;
                }

                if (level > floor)
                {
                    AddSurface(cell, level, SurfaceKind.Ledge);
                    return;
                }

                int displaced = floor;
                sortedSurfaces = null;
                heightField[cell] = level;
                SetKind(new SurfaceKey(cell, level), SurfaceKind.Floor);
                AddSurface(cell, displaced, SurfaceKind.Ledge);
            }

            private void SetKind(SurfaceKey key, SurfaceKind kind)
            {
                if (kind == SurfaceKind.Floor)
                {
                    kinds?.Remove(key);
                    return;
                }

                kinds ??= new Dictionary<SurfaceKey, SurfaceKind>();
                kinds[key] = kind;
            }

            // ----------------------------------------------------------------
            // The layer-blind writers (C2 prerequisite, design §8.2 L6).
            //
            // The elevation stage used to build its field with twelve bare
            // `cellLevels[cell] = level` statements whose intent lived entirely
            // in the surrounding guard: three meant "insert, this column has
            // nothing", one meant "replace what is here", the rest meant "set,
            // and reject a conflict". Identical syntax for three different
            // operations is exactly how the recipe zone write came to overwrite
            // a second authored layer in silence.
            //
            // Each writer now says which it is, and all three are LAYER-BLIND:
            // they operate on the column FLOOR, which is all a heightfield
            // write could ever mean. A column carrying a stacked surface is
            // refused by all three — so when a producer starts stacking, the
            // layer-blind writers fail loudly instead of truncating the column
            // to its lowest surface. Stacking goes through AddSurface.
            // ----------------------------------------------------------------

            /// <summary>
            /// Set a column's floor, rejecting a conflicting existing one.
            /// </summary>
            /// <remarks>
            /// This is the old free function <c>TrySetCellLevel</c>. The
            /// rejection text is verbatim: it reaches the seed report, and this
            /// migration must not move a single byte of it.
            /// </remarks>
            public bool TrySetFloorLevel(Vector2Int cell, int level, out string rejectionReason)
            {
                RequireUnstackedColumn(cell, nameof(TrySetFloorLevel));
                if (heightField.TryGetValue(cell, out int existing) && existing != level)
                {
                    rejectionReason = $"cell {cell} was assigned both level {existing} and level {level}";
                    return false;
                }

                sortedSurfaces = null;
                heightField[cell] = level;
                rejectionReason = string.Empty;
                return true;
            }

            /// <summary>
            /// Give a floor to a column that has none.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The four insert-only bypasses — both <c>FillUnassignedFloorCells</c>
            /// writes, the named vista promontory and the external connector
            /// piers — all guard on absence before writing and would be wrong
            /// without that guard. Stating it here makes it a property of the
            /// operation rather than of four separate call sites, and one of the
            /// four (the connector piers) already threw for the same reason,
            /// via <c>Dictionary.Add</c>.
            /// </para>
            /// <para>
            /// Unlike its two siblings this one tolerates a column that already
            /// carries SUSPENDED surfaces, and it has to: an aerial deck may
            /// cross a corridor cell the flood fill has not reached yet, and that
            /// cell still needs its floor. Adding a floor UNDER a stack is not
            /// the layer-blind truncation the guard exists to catch — the
            /// suspended surfaces keep their levels, and the new floor simply
            /// becomes the lowest thing in the column, which it is.
            /// </para>
            /// </remarks>
            public void AddFloorLevel(Vector2Int cell, int level)
            {
                if (heightField.TryGetValue(cell, out int existing))
                {
                    throw new InvalidOperationException(
                        $"AddFloorLevel({cell}, {level}) requires an unfloored column, " +
                        $"but its floor is already at level {existing}");
                }

                sortedSurfaces = null;
                heightField[cell] = level;
            }

            /// <summary>
            /// Move the floor of a column that already has one.
            /// </summary>
            /// <remarks>
            /// THE OVERWRITE, and the reason C2 was blocked. Exactly one
            /// producer calls it: the recipe resolver raising an Elevated zone
            /// to <c>baseLevel + zone.relativeLevel</c>. Under a layer schema
            /// that same write is what an authored upper layer wants to do
            /// differently — stack a second surface rather than move the one
            /// below it — so this method is the site the layer branch lands on,
            /// and naming it is what makes that branch visible instead of an
            /// indexer assignment that reads like every other write.
            /// </remarks>
            public void RelevelFloor(Vector2Int cell, int level)
            {
                RequireUnstackedColumn(cell, nameof(RelevelFloor));
                if (!heightField.ContainsKey(cell))
                {
                    throw new InvalidOperationException(
                        $"RelevelFloor({cell}, {level}) requires a surfaced column, but it carries no floor");
                }

                sortedSurfaces = null;
                heightField[cell] = level;
            }

            private void RequireUnstackedColumn(Vector2Int cell, string operation)
            {
                if (stacked != null &&
                    stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above) &&
                    above.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"{operation} is layer-blind and cannot write column {cell}, which carries " +
                        $"{above.Count} suspended surface(s). Use AddSurface.");
                }
            }

            /// <summary>
            /// Does this column carry a FLOOR — something resting on fill?
            /// </summary>
            /// <remarks>
            /// Not "does it carry a surface": a column crossed by a span deck and
            /// nothing else is surfaced but unfloored, and every caller of this
            /// predicate wants the floor. They are asking whether the flood fill
            /// still owes this cell a level, whether a promontory pier may be
            /// planted here, whether a doorway has ground on both sides. A deck
            /// three levels overhead answers none of those.
            /// <see cref="CarriesAnySurface"/> is the other question.
            /// </remarks>
            public bool HasFloor(Vector2Int cell)
            {
                return heightField.ContainsKey(cell);
            }

            /// <summary>Does this column carry a surface of any kind?</summary>
            public bool CarriesAnySurface(Vector2Int cell)
            {
                return heightField.ContainsKey(cell) ||
                    (stacked != null && stacked.ContainsKey(cell));
            }

            /// <summary>
            /// Does this column carry a span deck?
            /// </summary>
            /// <remarks>
            /// The port graph's question. A transition's footprint normally means
            /// "a stair body fills this column", but a span's footprint IS its
            /// walkable deck — and the geometry under it is untouched.
            /// </remarks>
            public bool CarriesDeck(Vector2Int cell)
            {
                if (stacked == null || !stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above))
                {
                    return false;
                }

                foreach (StackedSurfaceEntry entry in above)
                {
                    if (entry.kind == SurfaceKind.Deck)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>The level of a column's floor — its LOWEST surface.</summary>
            public bool TryGetFloorLevel(Vector2Int cell, out int level)
            {
                return heightField.TryGetValue(cell, out level);
            }

            /// <summary>What a surface is. <see cref="SurfaceKind.Floor"/> unless told otherwise.</summary>
            public SurfaceKind KindAt(Vector2Int cell, int level)
            {
                return kinds != null && kinds.TryGetValue(new SurfaceKey(cell, level), out SurfaceKind kind)
                    ? kind
                    : SurfaceKind.Floor;
            }

            /// <summary>
            /// Is there a walkable surface at exactly this level here?
            /// </summary>
            /// <remarks>
            /// The reader-side counterpart of <see cref="AddSurface"/>, and the
            /// question most of the migrated readers were really asking. On a
            /// single-layer field it is exactly
            /// <c>TryGetFloorLevel(cell, out l) &amp;&amp; l == level</c>, which is what
            /// keeps the migration output-neutral; on a stacked one it is the
            /// difference between "the recipe's gallery is still there" and "the
            /// chamber under it is still there".
            /// </remarks>
            public bool HasSurfaceAt(Vector2Int cell, int level)
            {
                if (heightField.TryGetValue(cell, out int floor) && floor == level)
                {
                    return true;
                }

                if (stacked == null || !stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above))
                {
                    return false;
                }

                foreach (StackedSurfaceEntry entry in above)
                {
                    if (entry.level == level)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// The HIGHEST surface in a column — what stands in the way of
            /// anything passing over it.
            /// </summary>
            /// <remarks>
            /// The mirror of <see cref="TryGetFloorLevel"/>, and the one a
            /// clearance question wants. A reader that asks "is this column low
            /// enough for a deck to fly over" and gets the column FLOOR back is
            /// answering about the wrong surface the moment anything stacks: the
            /// gallery at deck height is invisible to it.
            /// </remarks>
            public bool TryGetHighestSurfaceLevel(Vector2Int cell, out int level)
            {
                bool floored = heightField.TryGetValue(cell, out level);
                if (stacked == null || !stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above))
                {
                    return floored;
                }

                // Kept ascending by AddSurface, so the last one is the top.
                for (int index = above.Count - 1; index >= 0; index--)
                {
                    if (!floored || above[index].level > level)
                    {
                        level = above[index].level;
                        break;
                    }
                }

                return floored || above.Count > 0;
            }

            /// <summary>The highest surface strictly below a level in one column.</summary>
            public bool TryGetHighestSurfaceBelow(Vector2Int cell, int ceiling, out int level)
            {
                level = int.MinValue;
                foreach (int candidate in LevelsAt(cell))
                {
                    if (candidate < ceiling && candidate > level)
                    {
                        level = candidate;
                    }
                }

                return level != int.MinValue;
            }

            /// <summary>
            /// Is this surface the lowest in its column — i.e. is the mass under
            /// it earth rather than open air? The column half of
            /// <c>IsGroundBacked</c> (design §7.1).
            /// </summary>
            public bool IsLowestInColumn(Vector2Int cell, int level)
            {
                if (heightField.TryGetValue(cell, out int floor))
                {
                    // A floor is by definition the lowest thing in its column.
                    return floor == level;
                }

                // No floor: the lowest surface is the bottom of the overlay,
                // which is where a deck over a true gap lives.
                if (stacked == null || !stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above) ||
                    above.Count == 0)
                {
                    return false;
                }

                return above[0].level == level;
            }

            /// <summary>
            /// `IsGroundBacked(s)` in full: a <see cref="SurfaceKind.Floor"/>
            /// AND the lowest in its column (design §7.1).
            /// </summary>
            /// <remarks>
            /// Both halves are load-bearing and each excludes a different thing.
            /// A span deck over a true gap is lowest but is not a Floor, so it
            /// must not become a solid pillar; a gallery slab over its own
            /// chamber is a Floor but is not lowest, so it must not wall the
            /// chamber off. Until C1b this predicate had no home that could
            /// answer it — the field stored levels with no kinds — and the
            /// renderer answered the deck half from a side table instead.
            /// </remarks>
            public bool IsGroundBacked(Vector2Int cell, int level)
            {
                return IsLowestInColumn(cell, level) && KindAt(cell, level) == SurfaceKind.Floor;
            }

            /// <summary>Every surface above the column floor, in canonical order.</summary>
            /// <remarks>
            /// This is what the renderer is handed. The heightfield half travels
            /// as it always has — as a <c>Dictionary&lt;Vector2Int,int&gt;</c> —
            /// so a single-layer field hands over an empty list and the renderer
            /// takes the path it took before this existed.
            /// </remarks>
            public IReadOnlyList<ElevationEdgeModel.StackedSurface> StackedSurfaces()
            {
                if (stacked == null || stacked.Count == 0)
                {
                    return Array.Empty<ElevationEdgeModel.StackedSurface>();
                }

                var above = new List<ElevationEdgeModel.StackedSurface>();
                foreach (KeyValuePair<Vector2Int, List<StackedSurfaceEntry>> column in stacked)
                {
                    foreach (StackedSurfaceEntry entry in column.Value)
                    {
                        above.Add(new ElevationEdgeModel.StackedSurface(
                            column.Key,
                            entry.level,
                            entry.kind));
                    }
                }

                above.Sort((first, second) => SurfaceKey.Compare(
                    new SurfaceKey(first.cell, first.level),
                    new SurfaceKey(second.cell, second.level)));
                return above;
            }

            /// <summary>
            /// Every column's FLOOR, valid however many surfaces a column carries.
            /// </summary>
            /// <remarks>
            /// Not <see cref="AsHeightField"/> renamed — the two say opposite
            /// things. <see cref="AsHeightField"/> is the migration shim and its
            /// throw means "this reader believes a cell has one surface, and it is
            /// now wrong". This is for a reader that takes BOTH halves and knows
            /// the difference: the renderer, which is handed these floors together
            /// with <see cref="StackedSurfaces"/> and reassembles the columns
            /// itself. Handing it <see cref="AsHeightField"/> would throw on
            /// exactly the plans it exists to draw.
            /// </remarks>
            public IReadOnlyDictionary<Vector2Int, int> ColumnFloors()
            {
                return heightField;
            }

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

            /// <summary>
            /// The GROUND shadow: every plan cell this field gives a floor.
            /// </summary>
            /// <remarks>
            /// Floors, not surfaces, and its one caller wants exactly that — the
            /// external-connector exterior flood asks which void the dungeon does
            /// not stand on, and a bridge deck overhead leaves the ground below it
            /// as exterior as it was.
            /// </remarks>
            public HashSet<Vector2Int> FlooredPlanCells()
            {
                return new HashSet<Vector2Int>(heightField.Keys);
            }

            /// <summary>
            /// The FLOORED plan cells, without a copy and in BACKING-STORE
            /// order.
            /// </summary>
            /// <remarks>
            /// Floors only. A column crossed by a span deck and nothing else is
            /// surfaced but unfloored, and every caller here wants the ground:
            /// the flood fill's seeds, the plan shadow reconcile, the overlook
            /// stat, the canonical level rows. <see cref="AllSurfacedCells"/> is
            /// the wider enumeration, and the port graph is the one reader that
            /// wants it.
            /// <para>
            /// The order is the caller's problem and one caller genuinely wants
            /// it — <c>ReconcilePlanShadowWithSurfaces</c> inserts into the
            /// shadow <c>HashSet</c>, whose own enumeration order is a function
            /// of its insertion order, and that shadow is read again downstream.
            /// Sorting here would be tidier and would move seeds.
            /// </para>
            /// </remarks>
            public IEnumerable<Vector2Int> FlooredCells()
            {
                return heightField.Keys;
            }

            /// <summary>
            /// Every plan cell carrying a surface of any kind, in BACKING-STORE
            /// order: floored columns first, then the columns that are only
            /// suspended.
            /// </summary>
            /// <remarks>
            /// The order is deliberate and not cosmetic. The port graph's node
            /// INSERTION order reaches its summary and its reachability message,
            /// both of which are reported, so the floored columns must keep
            /// enumerating exactly as the raw dictionary did and the unfloored
            /// ones must arrive after them rather than interleaved.
            /// </remarks>
            public IEnumerable<Vector2Int> AllSurfacedCells()
            {
                foreach (Vector2Int cell in heightField.Keys)
                {
                    yield return cell;
                }

                if (stacked == null)
                {
                    yield break;
                }

                foreach (KeyValuePair<Vector2Int, List<StackedSurfaceEntry>> column in stacked)
                {
                    if (!heightField.ContainsKey(column.Key) && column.Value.Count > 0)
                    {
                        yield return column.Key;
                    }
                }
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

                if (stacked != null)
                {
                    foreach (KeyValuePair<Vector2Int, List<StackedSurfaceEntry>> column in stacked)
                    {
                        foreach (StackedSurfaceEntry entry in column.Value)
                        {
                            surfaces.Add(new SurfaceKey(column.Key, entry.level));
                        }
                    }
                }

                surfaces.Sort(SurfaceKey.Compare);
                sortedSurfaces = surfaces.ToArray();
                return sortedSurfaces;
            }

            /// <summary>The levels present at one plan cell, ascending.</summary>
            public IReadOnlyList<int> LevelsAt(Vector2Int cell)
            {
                bool floored = heightField.TryGetValue(cell, out int level);
                if (stacked == null || !stacked.TryGetValue(cell, out List<StackedSurfaceEntry> above))
                {
                    return floored ? new[] { level } : Array.Empty<int>();
                }

                var levels = new List<int>(above.Count + 1);
                if (floored)
                {
                    levels.Add(level);
                }

                foreach (StackedSurfaceEntry entry in above)
                {
                    levels.Add(entry.level);
                }

                return levels;
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
        /// Agreement is therefore one-directional: floors ⊆ shadow.
        /// </para>
        /// <para>
        /// FLOORS, not surfaces, and the qualifier arrived with the first
        /// suspended producer. The shadow is a GROUND claim — it is what the
        /// level field floods within and what <c>CleanPath</c> filters against —
        /// so a span deck's plan cell does not belong in it. Adding one would
        /// flood a level into the gap the deck was built to cross and would let a
        /// corridor path route through thin air. A surface owes the shadow a cell
        /// exactly when it rests on fill.
        /// </para>
        /// </remarks>
        /// <returns>How many cells the shadow gained.</returns>
        private static int ReconcilePlanShadowWithSurfaces(
            DungeonLayout layout,
            SurfaceField surfaces)
        {
            HashSet<Vector2Int> shadow = layout.floorCells;
            if (shadow == null || surfaces == null)
            {
                return 0;
            }

            // Backing-store order, NOT canonical order. The shadow is a
            // HashSet that later passes enumerate, so the order these piers are
            // inserted in is observable; iterating the field's own store keeps
            // it byte-for-byte what iterating the raw dictionary gave.
            int added = 0;
            foreach (Vector2Int cell in surfaces.FlooredCells())
            {
                if (shadow.Add(cell))
                {
                    added++;
                }
            }

            return added;
        }

        /// <summary>
        /// Shadow agreement (design §3.1): `surfaceField.FlooredPlanCells()` must
        /// equal `planShadow.cells` at the end of planning.
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
            SurfaceField surfaces = plan.surfaces;

            // Plan-space on both sides, and FLOOR-scoped. Agreement is between
            // the shadow and the cells the field gives GROUND to, so a stacked
            // column is one cell here, not two — a second storey adds no new plan
            // coordinate — and a column that only carries a suspended surface
            // adds none either, because a deck makes no ground claim.
            var surfacedOutside = new List<Vector2Int>();
            foreach (Vector2Int cell in surfaces?.FlooredCells() ?? Array.Empty<Vector2Int>())
            {
                if (!shadow.Contains(cell))
                {
                    surfacedOutside.Add(cell);
                }
            }

            var shadowWithout = new List<Vector2Int>();
            foreach (Vector2Int cell in shadow)
            {
                if (surfaces == null || !surfaces.HasFloor(cell))
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
