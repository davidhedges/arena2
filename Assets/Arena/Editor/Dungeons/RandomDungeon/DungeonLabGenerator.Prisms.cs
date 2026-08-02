using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonLab.Editor
{
    // Phase B of the layered 3D topology design
    // (docs/dungeon-builder/layered-topology-design-2026-07-29.md §6, §13):
    // reservations and clearance become VOLUMES.
    //
    // The five `HashSet<Vector2Int>` that were `StairPlacementLedger` become one
    // ledger of prisms, each carrying a half-open level band and a typed owner.
    // Flat reservations still use `LevelBand.Unbounded`; structural decks and
    // reserved voids add their resolved bands through this same ledger. The
    // original flat behavior remains the unbounded special case.
    //
    // Three things this file is careful about, because three review rounds got
    // each of them wrong (design §6, "three corrections to the draft"):
    //
    //   (a) bands are HALF-OPEN, so a clearance of exactly MinHeadroomLevels
    //       passes — matching the `clearance < MinHeadroomLevels` gate it
    //       replaces;
    //   (b) prisms carry an OWNER, without which a transition's own footprint
    //       violates its own clearance;
    //   (c) conflict is an ASYMMETRIC per-kind policy, not a symmetric matrix.
    //       Landing-landing, landing-clearance and mouth-clearance are all legal
    //       today and a symmetric table gets all three wrong.
    internal sealed partial class DungeonLabGenerator
    {
        /// <summary>
        /// What a prism reserves. The five that exist today, plus three the
        /// layered model adds (design §6).
        /// </summary>
        private enum PrismKind
        {
            // --- the five the flat ledger kept ---
            Footprint,
            Landing,
            Mouth,
            FootprintClearance,             // was `clearanceCells` — tests against footprints
            TransitionClearance,            // was `transitionClearanceCells` — tests against mouths

            // --- added by the layered design ---
            Support,                        // piers, columns, buttresses, stairwell shafts (§7.1)
            Wall,                           // partitions and enclosure walls (§7.1)
            OpenVolume                      // reserved vertical void (§6, §5)
        }

        /// <summary>
        /// The family half of an owner key. Distinct families keep ids from
        /// colliding across producers that number their own things from zero.
        /// </summary>
        private enum OwnerFamily
        {
            Transition,
            Recipe,
            Room,
            Opening,
            Corridor,
            Vista,
            Promontory
        }

        /// <summary>
        /// Whose a prism is: stable, authored-nameable, unique across families.
        /// </summary>
        /// <remarks>
        /// Deliberately not a runtime integer. An <see cref="PrismKind.OpenVolume"/>'s
        /// penetration allow-list is authored content sitting beside a room, and
        /// it has to be able to name its members — `Transition:atrium-stair-a`,
        /// `Room:great-atrium#gallery`. A key that is an allocation counter
        /// cannot be authored against and renumbers whenever placement order
        /// moves.
        /// </remarks>
        private readonly struct OwnerKey : IEquatable<OwnerKey>
        {
            public readonly OwnerFamily family;
            public readonly string id;

            public OwnerKey(OwnerFamily family, string id)
            {
                this.family = family;
                this.id = id ?? string.Empty;
            }

            /// <summary>
            /// The owner of a plan surface that no reservation claims — plain
            /// floor. Every ledger prism belongs to something else, so this
            /// never accidentally exempts a reservation from the headroom rule
            /// under the same-owner rule.
            /// </summary>
            public static OwnerKey PlanFloor => new OwnerKey(OwnerFamily.Room, "plan-floor");

            /// <summary>
            /// The owner a candidate is tested under before it has one. The id
            /// is reserved: no producer may register under it, so a probe is
            /// foreign to everything in the ledger and the same-owner rule
            /// cannot silently wave it through.
            /// </summary>
            public static OwnerKey CandidateProbe => new OwnerKey(OwnerFamily.Transition, "#candidate-probe");

            public string Token => $"{family}:{id}";

            public bool Equals(OwnerKey other)
            {
                return family == other.family && string.Equals(id, other.id, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is OwnerKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)family * 397) ^ (id?.GetHashCode() ?? 0);
                }
            }

            public override string ToString()
            {
                return Token;
            }
        }

        private static OwnerKey FinalViewAnchorsOwner =>
            new OwnerKey(OwnerFamily.Vista, "final-view-anchors");

        /// <summary>
        /// One reservation: a plan cell, a half-open level band, a kind and an owner.
        /// </summary>
        private readonly struct Prism
        {
            public readonly Vector2Int cell;
            public readonly LevelBand band;
            public readonly PrismKind kind;
            public readonly OwnerKey owner;

            public Prism(Vector2Int cell, LevelBand band, PrismKind kind, OwnerKey owner)
            {
                this.cell = cell;
                this.band = band;
                this.kind = kind;
                this.owner = owner;
            }

            public override string ToString()
            {
                return $"{kind}{band}@({cell.x},{cell.y}) [{owner.Token}]";
            }
        }

        /// <summary>
        /// The one named headroom predicate (design §6).
        /// </summary>
        /// <remarks>
        /// It exists because three passages of an earlier revision defined three
        /// different blocker sets for the same rule. Every site that talks about
        /// headroom refers to this by name rather than restating a set.
        /// <para>
        /// <see cref="PrismKind.Landing"/> is deliberately EXCLUDED. A landing is
        /// itself a walkable surface, and surface-to-surface vertical separation
        /// is `SURFACE_STACK_CLEARANCE` — a different rule. Counting it here
        /// would double-report the same conflict.
        /// </para>
        /// </remarks>
        private static bool BlocksHeadroom(PrismKind kind)
        {
            return kind == PrismKind.Footprint ||
                kind == PrismKind.Support ||
                kind == PrismKind.Wall;
        }

        /// <summary>
        /// Which registered kinds an incoming kind conflicts with — the
        /// asymmetric `blocksKinds` policy, seeded verbatim from the flat
        /// ledger's `ConflictsWithReservation` (design §6, correction (c)).
        /// </summary>
        /// <remarks>
        /// The relation is DIRECTIONAL and uses five sets, not four. Reading the
        /// table the other way round is what makes landing-landing,
        /// landing-clearance and mouth-clearance legal — all three of which a
        /// symmetric matrix rejects, and all three of which the generator relies
        /// on today. The two clearance concepts stay distinct because one guards
        /// footprints and the other guards transition mouths; merging them into
        /// one `Clearance` kind loses behaviour.
        /// <para>
        /// <see cref="PrismKind.Support"/> and <see cref="PrismKind.Wall"/> block
        /// like <see cref="PrismKind.Footprint"/>, so they appear wherever
        /// `Footprint` does. Neither has a producer yet, so adding them here is
        /// inert until one exists.
        /// </para>
        /// </remarks>
        private static bool BlocksKind(PrismKind incoming, PrismKind registered)
        {
            switch (incoming)
            {
                case PrismKind.Footprint:
                case PrismKind.Support:
                case PrismKind.Wall:
                    // footprint ∪ landing ∪ footprintClearance — the old `BlocksFootprint`
                    return IsSolidStructure(registered) ||
                        registered == PrismKind.Landing ||
                        registered == PrismKind.FootprintClearance;

                case PrismKind.Landing:
                    // footprint ONLY — so landing-landing and landing-clearance are legal
                    return IsSolidStructure(registered);

                case PrismKind.FootprintClearance:
                    // footprint ONLY — so clearance-landing is legal
                    return IsSolidStructure(registered);

                case PrismKind.Mouth:
                    return registered == PrismKind.TransitionClearance;

                case PrismKind.TransitionClearance:
                    return registered == PrismKind.Mouth;

                case PrismKind.OpenVolume:
                    // A reserved void forbids every solid kind, landings included:
                    // a balcony floor inside an atrium's void is exactly what the
                    // reservation exists to stop.
                    return IsSolidStructure(registered) || registered == PrismKind.Landing;

                default:
                    return false;
            }
        }

        private static bool IsSolidStructure(PrismKind kind)
        {
            return kind == PrismKind.Footprint ||
                kind == PrismKind.Support ||
                kind == PrismKind.Wall;
        }

        // Canonical occupancy for every planned elevation transition. Footprints,
        // landings and transition mouths retain their existing sharing rules; recipe
        // features can additionally reserve cells that must remain clear of a body or
        // transition mouth. All producers consult this ledger before acceptance.
        //
        // Phase B: every reservation is a PRISM — a cell, a half-open band and a
        // typed owner — and the five cell sets are gone. The density fill passes
        // query it too, so a reserved volume cannot be quietly filled in.
        private sealed class PrismLedger
        {
            private readonly Dictionary<Vector2Int, List<Prism>> byCell =
                new Dictionary<Vector2Int, List<Prism>>();

            // Cells carrying at least one BlocksHeadroom prism that declares a
            // base. Maintained on registration so the headroom rule does not
            // sweep the whole ledger, and so the rule's iteration order is the
            // same sorted cell order the flat `spanDeckLevels` gate used.
            private readonly HashSet<Vector2Int> headroomBearingCells = new HashSet<Vector2Int>();

            // OpenVolume owner -> the owners its authored allow-list admits.
            private readonly Dictionary<OwnerKey, HashSet<OwnerKey>> penetrationAllowLists =
                new Dictionary<OwnerKey, HashSet<OwnerKey>>();

            // The surface a span deck CARRIES, and who owns it. Recorded at
            // registration, not inferred from "a prism whose base is this level":
            // a promontory pier raised to a deck's exact level would satisfy that
            // guess and would then excuse the 0u clearance the gate exists to
            // reject.
            private readonly Dictionary<SurfaceKey, OwnerKey> carriedSurfaceOwners =
                new Dictionary<SurfaceKey, OwnerKey>();

            /// <summary>
            /// How many cells carry structure that declares where it sits. This
            /// is the count the headroom gate reports, and today every one of
            /// them is an external-span deck cell.
            /// </summary>
            public int HeadroomBearingCellCount => headroomBearingCells.Count;

            public void Add(Prism prism)
            {
                if (!byCell.TryGetValue(prism.cell, out List<Prism> prisms))
                {
                    prisms = new List<Prism>(2);
                    byCell[prism.cell] = prisms;
                }

                prisms.Add(prism);
                if (BlocksHeadroom(prism.kind) && prism.band.DeclaresBase)
                {
                    headroomBearingCells.Add(prism.cell);
                }
            }

            public void Add(
                IEnumerable<Vector2Int> cells,
                LevelBand band,
                PrismKind kind,
                OwnerKey owner)
            {
                if (cells == null)
                {
                    return;
                }

                foreach (Vector2Int cell in cells)
                {
                    Add(new Prism(cell, band, kind, owner));
                }
            }

            /// <summary>
            /// Register a footprint, its landings, and optionally its mouths and
            /// the two clearance kinds — the flat ledger's `Register`, with the
            /// owner made explicit and every band still unbounded.
            /// </summary>
            public void Register(
                OwnerKey owner,
                IReadOnlyList<Vector2Int> footprint,
                IReadOnlyList<Vector2Int> lowerLandings,
                IReadOnlyList<Vector2Int> upperLandings)
            {
                Register(
                    owner,
                    footprint,
                    lowerLandings,
                    upperLandings,
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>(),
                    Array.Empty<Vector2Int>());
            }

            public void Register(
                OwnerKey owner,
                IReadOnlyList<Vector2Int> footprint,
                IReadOnlyList<Vector2Int> lowerLandings,
                IReadOnlyList<Vector2Int> upperLandings,
                IReadOnlyList<Vector2Int> transitionMouths,
                IReadOnlyList<Vector2Int> requiredClearance,
                IReadOnlyList<Vector2Int> requiredTransitionClearance)
            {
                Add(footprint, LevelBand.Unbounded, PrismKind.Footprint, owner);
                Add(lowerLandings, LevelBand.Unbounded, PrismKind.Landing, owner);
                Add(upperLandings, LevelBand.Unbounded, PrismKind.Landing, owner);
                Add(transitionMouths, LevelBand.Unbounded, PrismKind.Mouth, owner);
                Add(requiredClearance, LevelBand.Unbounded, PrismKind.FootprintClearance, owner);
                Add(
                    requiredTransitionClearance,
                    LevelBand.Unbounded,
                    PrismKind.TransitionClearance,
                    owner);
            }

            /// <summary>
            /// A deck: solid structure whose base level is KNOWN, sitting over
            /// whatever the plan puts underneath it.
            /// </summary>
            /// <remarks>
            /// This is the one producer in the generator that can say where its
            /// geometry sits, and therefore the one that the headroom rule can
            /// judge. The band runs upward without limit, which reproduces the
            /// flat gate exactly — including its rejection of a deck buried at or
            /// below the floor it spans, which a bounded band would let through.
            /// The old `spanDeckLevels` side table kept the LOWEST deck level per
            /// cell; several prisms on one cell reproduce that, because the rule
            /// reports against the lowest base it finds.
            /// </remarks>
            public void RegisterSpanDeck(IEnumerable<Vector2Int> deckCells, int deckLevel, OwnerKey owner)
            {
                foreach (Vector2Int cell in deckCells ?? Array.Empty<Vector2Int>())
                {
                    Add(new Prism(cell, LevelBand.From(deckLevel), PrismKind.Footprint, owner));
                    // The deck's walk surface stands ON this prism. Without that
                    // recorded, the headroom rule reads the deck's own surface,
                    // finds the deck's own structure in its headroom band, and
                    // rejects every plan that places a bridge.
                    carriedSurfaceOwners[new SurfaceKey(cell, deckLevel)] = owner;
                }
            }

            /// <summary>
            /// Register one generated room storey: its closed slab, bracket
            /// supports, and the clear air between it and the structural level
            /// below. The walk surface itself remains in <see cref="SurfaceField"/>.
            /// </summary>
            /// <remarks>
            /// Integer prism bands cannot spell the renderer's measured 0.5u
            /// slab, so the structural reservation conservatively occupies the
            /// final 1u below the surface. On the 4u structural lattice that
            /// leaves exactly the existing 3u minimum headroom; half-open bands
            /// make the boundary legal. Supports use the same bracket band and
            /// therefore do not turn the traversable chamber below into piers.
            /// </remarks>
            public bool TryRegisterStructuralSurface(
                IReadOnlyList<Vector2Int> surfaceCells,
                IReadOnlyList<Vector2Int> supportCells,
                int lowerLevel,
                int surfaceLevel,
                OwnerKey owner,
                out Prism blocker)
            {
                var slabBand = new LevelBand(surfaceLevel - 1, surfaceLevel);
                var clearanceBand = new LevelBand(lowerLevel, surfaceLevel - 1);
                foreach (Vector2Int cell in surfaceCells ?? Array.Empty<Vector2Int>())
                {
                    if (Blocks(cell, slabBand, PrismKind.Footprint, owner, out blocker) ||
                        !clearanceBand.IsEmpty &&
                        Blocks(cell, clearanceBand, PrismKind.FootprintClearance, owner, out blocker))
                    {
                        return false;
                    }
                }

                foreach (Vector2Int cell in supportCells ?? Array.Empty<Vector2Int>())
                {
                    if (Blocks(cell, slabBand, PrismKind.Support, owner, out blocker))
                    {
                        return false;
                    }
                }

                Add(surfaceCells, slabBand, PrismKind.Footprint, owner);
                Add(supportCells, slabBand, PrismKind.Support, owner);
                if (!clearanceBand.IsEmpty)
                {
                    Add(surfaceCells, clearanceBand, PrismKind.FootprintClearance, owner);
                }

                foreach (Vector2Int cell in surfaceCells ?? Array.Empty<Vector2Int>())
                {
                    carriedSurfaceOwners[new SurfaceKey(cell, surfaceLevel)] = owner;
                }

                blocker = default;
                return true;
            }

            /// <summary>
            /// Who carries this surface: the structure it stands on, or
            /// <see cref="OwnerKey.PlanFloor"/> when it rests on fill.
            /// </summary>
            public OwnerKey SurfaceOwnerAt(SurfaceKey surface)
            {
                return carriedSurfaceOwners.TryGetValue(surface, out OwnerKey owner)
                    ? owner
                    : OwnerKey.PlanFloor;
            }

            /// <summary>
            /// Reserve a vertical void, with the authored list of owners allowed
            /// to penetrate it (design §6).
            /// </summary>
            /// <remarks>
            /// Recipe atria and generated vista volumes both publish here. The
            /// allow-list is what keeps the reservation usable: an atrium that
            /// forbade everything would forbid its own balconies, stairs and
            /// bridges.
            /// </remarks>
            public void RegisterOpenVolume(
                IEnumerable<Vector2Int> cells,
                LevelBand band,
                OwnerKey owner,
                IEnumerable<OwnerKey> penetrationAllowList)
            {
                AdmitOpenVolumePenetrations(owner, penetrationAllowList);
                Add(cells, band, PrismKind.OpenVolume, owner);
            }

            /// <summary>
            /// Register a generated void only when its declared band does not
            /// intersect an existing, non-admitted structural prism.
            /// </summary>
            public bool TryRegisterOpenVolume(
                IEnumerable<Vector2Int> cells,
                LevelBand band,
                OwnerKey owner,
                IEnumerable<OwnerKey> penetrationAllowList,
                out Prism blocker)
            {
                AdmitOpenVolumePenetrations(owner, penetrationAllowList);
                foreach (Vector2Int cell in cells ?? Array.Empty<Vector2Int>())
                {
                    if (Blocks(cell, band, PrismKind.OpenVolume, owner, out blocker))
                    {
                        return false;
                    }
                }

                Add(cells, band, PrismKind.OpenVolume, owner);
                blocker = default;
                return true;
            }

            private void AdmitOpenVolumePenetrations(
                OwnerKey owner,
                IEnumerable<OwnerKey> penetrationAllowList)
            {
                if (!penetrationAllowLists.TryGetValue(owner, out HashSet<OwnerKey> allowed))
                {
                    allowed = new HashSet<OwnerKey>();
                    penetrationAllowLists[owner] = allowed;
                }

                foreach (OwnerKey admitted in penetrationAllowList ?? Array.Empty<OwnerKey>())
                {
                    allowed.Add(admitted);
                }
            }

            /// <summary>
            /// Open-volume footprints grouped by their plan owner. Consumers
            /// reason about reserved voids through this owner-neutral ledger
            /// view rather than walking recipe zones.
            /// </summary>
            public IReadOnlyList<HashSet<Vector2Int>> OpenVolumeCellGroups()
            {
                var cellsByOwner = new Dictionary<OwnerKey, HashSet<Vector2Int>>();
                foreach (KeyValuePair<Vector2Int, List<Prism>> item in SortedByCell(byCell))
                {
                    foreach (Prism prism in item.Value)
                    {
                        if (prism.kind != PrismKind.OpenVolume)
                        {
                            continue;
                        }

                        if (!cellsByOwner.TryGetValue(prism.owner, out HashSet<Vector2Int> cells))
                        {
                            cells = new HashSet<Vector2Int>();
                            cellsByOwner[prism.owner] = cells;
                        }

                        cells.Add(item.Key);
                    }
                }

                var owners = new List<OwnerKey>(cellsByOwner.Keys);
                owners.Sort((a, b) => string.CompareOrdinal(a.Token, b.Token));
                var result = new List<HashSet<Vector2Int>>(owners.Count);
                foreach (OwnerKey owner in owners)
                {
                    result.Add(cellsByOwner[owner]);
                }

                return result;
            }

            /// <summary>
            /// The core relation: does an incoming prism conflict with anything
            /// already registered?
            /// </summary>
            /// <remarks>
            /// Three rules, in the order they are applied:
            /// <list type="number">
            /// <item>bands must intersect — half-open, so touching endpoints do
            /// not;</item>
            /// <item>an <see cref="PrismKind.OpenVolume"/> on either side is
            /// settled by its penetration allow-list, and the same-owner rule
            /// does NOT apply — a room's own floor must appear on its own
            /// allow-list, or an atrium fills its own void;</item>
            /// <item>otherwise the same owner never conflicts, which is what
            /// makes clearance expressible at all, and the asymmetric
            /// <see cref="BlocksKind"/> policy decides the rest.</item>
            /// </list>
            /// </remarks>
            private bool Conflicts(Prism incoming, Prism registered)
            {
                if (!incoming.band.Intersects(registered.band))
                {
                    return false;
                }

                if (registered.kind == PrismKind.OpenVolume)
                {
                    return BlocksKind(PrismKind.OpenVolume, incoming.kind) &&
                        !IsPenetrationAllowed(registered.owner, incoming.owner);
                }

                if (incoming.kind == PrismKind.OpenVolume)
                {
                    return BlocksKind(PrismKind.OpenVolume, registered.kind) &&
                        !IsPenetrationAllowed(incoming.owner, registered.owner);
                }

                if (incoming.owner.Equals(registered.owner))
                {
                    return false;
                }

                return BlocksKind(incoming.kind, registered.kind);
            }

            private bool IsPenetrationAllowed(OwnerKey volumeOwner, OwnerKey penetrating)
            {
                return penetrationAllowLists.TryGetValue(volumeOwner, out HashSet<OwnerKey> allowed) &&
                    allowed.Contains(penetrating);
            }

            /// <summary>
            /// Would a prism of this kind and owner conflict at this cell?
            /// </summary>
            public bool Blocks(
                Vector2Int cell,
                LevelBand band,
                PrismKind kind,
                OwnerKey owner,
                out Prism blocker)
            {
                if (byCell.TryGetValue(cell, out List<Prism> prisms))
                {
                    var incoming = new Prism(cell, band, kind, owner);
                    foreach (Prism registered in prisms)
                    {
                        if (Conflicts(incoming, registered))
                        {
                            blocker = registered;
                            return true;
                        }
                    }
                }

                blocker = default;
                return false;
            }

            public bool Blocks(Vector2Int cell, LevelBand band, PrismKind kind, OwnerKey owner)
            {
                return Blocks(cell, band, kind, owner, out _);
            }

            /// <summary>
            /// May a fill pass claim this plan cell? The density dial's annex and
            /// mop-up sweeps ask this instead of testing a bare cell set.
            /// </summary>
            /// <remarks>
            /// Design §6 makes this an invariant, and §11 names the failure mode
            /// it prevents: "the density passes will claim any plan cell they
            /// can. If they do not read the prism ledger, density ≥3 packs an
            /// atrium and no check fires." A fill claim is an incoming
            /// <see cref="PrismKind.Footprint"/> from nobody in particular, so it
            /// is foreign to every reservation and to every reserved volume.
            /// </remarks>
            public bool BlocksFill(Vector2Int cell)
            {
                return Blocks(cell, LevelBand.Unbounded, PrismKind.Footprint, OwnerKey.CandidateProbe);
            }

            /// <summary>
            /// The lowest declared base among the structural prisms at a cell —
            /// the height the flat `spanDeckLevels` side table used to hold.
            /// </summary>
            public bool TryGetLowestStructureBase(Vector2Int cell, out int structureBase)
            {
                structureBase = int.MaxValue;
                bool found = false;
                if (byCell.TryGetValue(cell, out List<Prism> prisms))
                {
                    foreach (Prism prism in prisms)
                    {
                        if (BlocksHeadroom(prism.kind) &&
                            prism.band.DeclaresBase &&
                            prism.band.minLevel < structureBase)
                        {
                            structureBase = prism.band.minLevel;
                            found = true;
                        }
                    }
                }

                return found;
            }

            public bool BlocksFootprint(Vector2Int cell)
            {
                return Blocks(cell, LevelBand.Unbounded, PrismKind.Footprint, OwnerKey.CandidateProbe);
            }

            public bool BlocksTransitionMouth(Vector2Int cell)
            {
                return Blocks(cell, LevelBand.Unbounded, PrismKind.Mouth, OwnerKey.CandidateProbe);
            }

            /// <summary>
            /// Did every reserved void survive the plan? (design §13 Phase D's
            /// "`OpenVolume` survives every density level".)
            /// </summary>
            /// <remarks>
            /// The registration-time rule can only refuse the claims it is
            /// SHOWN. This asks the finished article instead: for every
            /// <see cref="PrismKind.OpenVolume"/> prism, is there a walkable
            /// surface standing inside its band? That catches the case §11 names
            /// — a fill pass, a late corrective pass or a producer that never
            /// consulted the ledger putting floor in the atrium — regardless of
            /// which pass did it or whether it asked permission first.
            /// <para>
            /// A surface at a level BELOW the band is the point of the thing: an
            /// aperture's catch floor is what makes it an aperture rather than a
            /// shaft into the abyss. Half-open, so a surface exactly at the
            /// band's exclusive top is a lid, not a violation.
            /// </para>
            /// </remarks>
            public bool TryValidateOpenVolumes(SurfaceField surfaces, out string rejectionReason)
            {
                int volumeCells = 0;
                foreach (KeyValuePair<Vector2Int, List<Prism>> item in
                         SortedByCell(byCell))
                {
                    foreach (Prism prism in item.Value)
                    {
                        if (prism.kind != PrismKind.OpenVolume)
                        {
                            continue;
                        }

                        volumeCells++;
                        foreach (int level in surfaces.LevelsAt(item.Key))
                        {
                            if (prism.band.Contains(level))
                            {
                                rejectionReason =
                                    $"[OPEN_VOLUME_VIOLATION] reserved volume '{prism.owner}' was filled at " +
                                    $"{item.Key} level {level}, inside {prism.band}";
                                return false;
                            }
                        }
                    }
                }

                rejectionReason = $"open-volume gate passed for {volumeCells} reserved cell(s)";
                return true;
            }

            // Canonical cell order, so the FIRST violation a seed reports is a
            // property of the geometry rather than of dictionary iteration.
            private static IEnumerable<KeyValuePair<Vector2Int, List<Prism>>> SortedByCell(
                Dictionary<Vector2Int, List<Prism>> source)
            {
                var cells = new List<Vector2Int>(source.Keys);
                cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                foreach (Vector2Int cell in cells)
                {
                    yield return new KeyValuePair<Vector2Int, List<Prism>>(cell, source[cell]);
                }
            }

            /// <summary>Every cell carrying a prism of one kind.</summary>
            public IEnumerable<Vector2Int> CellsOfKind(PrismKind kind)
            {
                foreach (KeyValuePair<Vector2Int, List<Prism>> item in byCell)
                {
                    foreach (Prism prism in item.Value)
                    {
                        if (prism.kind == kind)
                        {
                            yield return item.Key;
                            break;
                        }
                    }
                }
            }

            public bool ConflictsWithReservation(
                OwnerKey owner,
                IEnumerable<Vector2Int> footprint,
                IEnumerable<Vector2Int> landings,
                IEnumerable<Vector2Int> transitionMouths,
                IEnumerable<Vector2Int> requiredClearance,
                IEnumerable<Vector2Int> requiredTransitionClearance,
                out Vector2Int conflictCell)
            {
                // The kind order and the per-kind cell order both matter: the
                // caller names the FIRST conflicting cell in its rejection
                // message, so reordering these loops rewrites diagnostics that
                // are projected into the seed report.
                if (FirstBlocked(footprint, PrismKind.Footprint, owner, out conflictCell) ||
                    FirstBlocked(landings, PrismKind.Landing, owner, out conflictCell) ||
                    FirstBlocked(transitionMouths, PrismKind.Mouth, owner, out conflictCell) ||
                    FirstBlocked(requiredClearance, PrismKind.FootprintClearance, owner, out conflictCell) ||
                    FirstBlocked(
                        requiredTransitionClearance,
                        PrismKind.TransitionClearance,
                        owner,
                        out conflictCell))
                {
                    return true;
                }

                conflictCell = default;
                return false;
            }

            private bool FirstBlocked(
                IEnumerable<Vector2Int> cells,
                PrismKind kind,
                OwnerKey owner,
                out Vector2Int conflictCell)
            {
                foreach (Vector2Int cell in cells ?? Array.Empty<Vector2Int>())
                {
                    if (Blocks(cell, LevelBand.Unbounded, kind, owner))
                    {
                        conflictCell = cell;
                        return true;
                    }
                }

                conflictCell = default;
                return false;
            }

            public bool ConflictsWith(
                StairTransitionCandidate candidate,
                int lowerLevel,
                int upperLevel)
            {
                OwnerKey owner = OwnerKey.CandidateProbe;
                if (FirstBlocked(candidate.footprintCells, PrismKind.Footprint, owner, out _) ||
                    FirstBlocked(
                        candidate.lowerLandingCells,
                        new LevelBand(lowerLevel, lowerLevel + 1),
                        PrismKind.Landing,
                        owner,
                        out _) ||
                    FirstBlocked(
                        candidate.upperLandingCells,
                        new LevelBand(upperLevel, upperLevel + 1),
                        PrismKind.Landing,
                        owner,
                        out _) ||
                    FirstBlocked(
                        new[] { candidate.transitionFirstCell, candidate.transitionSecondCell },
                        PrismKind.Mouth,
                        owner,
                        out _))
                {
                    return true;
                }

                return false;
            }

            private bool FirstBlocked(
                IEnumerable<Vector2Int> cells,
                LevelBand band,
                PrismKind kind,
                OwnerKey owner,
                out Vector2Int conflictCell)
            {
                foreach (Vector2Int cell in cells ?? Array.Empty<Vector2Int>())
                {
                    if (Blocks(cell, band, kind, owner))
                    {
                        conflictCell = cell;
                        return true;
                    }
                }

                conflictCell = default;
                return false;
            }

            /// <summary>
            /// The headroom rule (design §6, payoff 3): for every surface S, the
            /// half-open prism `(S.cell, S.level, S.level + MinHeadroomLevels)`
            /// must not intersect any prism satisfying
            /// <see cref="BlocksHeadroom"/> owned by anything other than S.
            /// </summary>
            /// <remarks>
            /// <para>
            /// One rule, one predicate, one call site — replacing
            /// `TryValidateSpanHeadroom`, the `spanDeckLevels` side table it read
            /// and the duplicated deck formula in `.Batch.cs`. Half-open is what
            /// makes a clearance of exactly `MinHeadroomLevels` pass, which is
            /// what the arithmetic gate (`clearance &lt; MinHeadroomLevels`) did.
            /// </para>
            /// <para>
            /// Only prisms that <see cref="LevelBand.DeclaresBase"/> take part.
            /// That is not a special case for decks: it is what "S.level" means.
            /// A reservation with an unbounded band has never been asked where it
            /// sits, so there is no level at which it could clear or fail to
            /// clear anything — and reading its `int.MinValue` base as geometry
            /// would make every stair footprint violate the headroom of the
            /// surface it carries.
            /// </para>
            /// </remarks>
            public bool TryValidateSurfaceHeadroom(
                SurfaceField surfaces,
                out string rejectionReason)
            {
                rejectionReason = string.Empty;
                if (headroomBearingCells.Count == 0 || surfaces == null)
                {
                    return true;
                }

                var cells = new List<Vector2Int>(headroomBearingCells);
                cells.Sort(CompareCells);
                foreach (Vector2Int cell in cells)
                {
                    // EVERY surface in the column, ascending — the rule is stated
                    // "for every surface S", and a column that carries two of them
                    // owes clearance to both. Reading one level per cell answered
                    // for the floor and let a gallery slab sit 1u under a deck.
                    // Identical on a single-layer field, where LevelsAt returns
                    // the one value TryGetValue used to.
                    foreach (int surfaceLevel in surfaces.LevelsAt(cell))
                    {
                        var headroom = new LevelBand(surfaceLevel, surfaceLevel + MinHeadroomLevels);
                        // The surface's own carrier, not a blanket `PlanFloor`.
                        // A span deck is a surface standing on the very prism that
                        // declares the deck's base, and "owned by anything other
                        // than S" is what stops that from reading as zero
                        // clearance. Every other surface rests on fill and answers
                        // `PlanFloor`, which is what the constant meant while the
                        // deck was not a surface at all.
                        //
                        // A FLOOR always answers `PlanFloor`, even at a deck's
                        // exact level, and that clause is load-bearing rather than
                        // defensive: the flood fill can reach a gap cell from the
                        // deck's own landing and floor it at deck height, which is
                        // precisely the zero-clearance case this gate exists to
                        // reject. Resolving the carrier by (cell, level) alone
                        // would hand that floor the deck's exemption and pass a
                        // deck lying on the ground.
                        bool restsOnFill =
                            surfaces.TryGetFloorLevel(cell, out int columnFloor) &&
                            columnFloor == surfaceLevel;
                        OwnerKey surfaceOwner = restsOnFill
                            ? OwnerKey.PlanFloor
                            : SurfaceOwnerAt(new SurfaceKey(cell, surfaceLevel));
                        if (!TryFindLowestObstruction(cell, headroom, surfaceOwner, out int structureBase))
                        {
                            continue;
                        }

                        int clearance = structureBase - surfaceLevel;
                        rejectionReason =
                            $"bridge span over cell ({cell.x}, {cell.y}) left only {clearance}u headroom above the walkable floor (minimum {MinHeadroomLevels}u)";
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// The lowest-based structural prism intruding on a surface's
            /// headroom band and owned by someone else.
            /// </summary>
            /// <remarks>
            /// Lowest, because that is the one the clearance figure should name —
            /// and because the side table this replaces kept the MINIMUM deck
            /// level per cell and reported against it. With half-open bands,
            /// "intrudes" is plain interval intersection, so a structure whose
            /// base is exactly `MinHeadroomLevels` above the surface does not
            /// intrude.
            /// </remarks>
            private bool TryFindLowestObstruction(
                Vector2Int cell,
                LevelBand headroom,
                OwnerKey surfaceOwner,
                out int structureBase)
            {
                structureBase = int.MaxValue;
                bool found = false;
                if (!byCell.TryGetValue(cell, out List<Prism> prisms))
                {
                    return false;
                }

                foreach (Prism prism in prisms)
                {
                    if (!BlocksHeadroom(prism.kind) ||
                        !prism.band.DeclaresBase ||
                        prism.owner.Equals(surfaceOwner) ||
                        !prism.band.Intersects(headroom))
                    {
                        continue;
                    }

                    if (prism.band.minLevel < structureBase)
                    {
                        structureBase = prism.band.minLevel;
                        found = true;
                    }
                }

                return found;
            }
        }

        private static void RegisterPlannedOpenVolume(
            PrismLedger ledger,
            IEnumerable<Vector2Int> cells,
            LevelBand band,
            OwnerKey owner,
            IEnumerable<OwnerKey> penetrationAllowList)
        {
            ledger.RegisterOpenVolume(
                cells,
                band,
                owner,
                penetrationAllowList);
        }

        private static void RegisterReservedVistaOpenVolume(
            PrismLedger ledger,
            IEnumerable<Vector2Int> cells)
        {
            RegisterPlannedOpenVolume(
                ledger,
                cells,
                LevelBand.Unbounded,
                new OwnerKey(OwnerFamily.Vista, "reserved-lane"),
                Array.Empty<OwnerKey>());
        }
    }
}
