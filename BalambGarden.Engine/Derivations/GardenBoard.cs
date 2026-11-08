using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Derivations;

/// <summary>What one container in the garden picture is saying. <see cref="Unclaimed"/> is a bed
/// slot the ledger has nothing for; <see cref="Unread"/> a record whose contents were never read
/// (a watering heard before any read); <see cref="Empty"/> a container the plugin knows was
/// emptied (a harvest witnessed or an empty read). <see cref="WaterSoon"/> is a bed past three
/// quarters of its wilt hours, <see cref="PastDeadline"/> one past its water deadline.
/// <see cref="Wilting"/> is a pot the live map shows wilting where the player stands - observed,
/// never clocked.</summary>
public enum ContainerLook { Unclaimed, Unread, Empty, Growing, WaterSoon, PastDeadline, Wilting, Ripe }

/// <summary>One container of the garden picture, with what its hover needs. <see cref="Position"/>
/// is the bed's slot (0-7) in a patch, or the pot's map key among the pots. <see cref="WaterBy"/>
/// is null wherever the wilt clock makes no claim; <see cref="TendUnheard"/> marks a growing bed
/// whose deadline (if any) rests on its sowing, not on a heard tend.</summary>
public sealed record ContainerCell(
    int Position, ContainerLook Look, ushort Species, byte Stage, bool TendUnheard,
    DateTimeOffset? WaterBy, EtaWindow? Ripe, ClaimedBed? Bed)
{
    public static ContainerCell Unclaimed(int slot) => new(slot, ContainerLook.Unclaimed,
        Species: 0, Stage: 0, TendUnheard: false, WaterBy: null, Ripe: null, Bed: null);
}

/// <summary>The garden picture of one patch (<see cref="Ordinal"/> set; always
/// <see cref="GardenBoard.PatchSlots"/> containers in slot order, drawn on
/// <see cref="GardenBoard.RingPosition"/>), or of an estate's pots (no ordinal; only recorded
/// pots, in map-key order).</summary>
public sealed record PatchPicture(int? Ordinal, IReadOnlyList<ContainerCell> Cells)
{
    public bool IsPots => Ordinal is null;
}

/// <summary>One crop's next ripe window on a card: the earliest of its growing containers'.</summary>
public sealed record CropRipe(ushort Species, EtaWindow Window);

/// <summary>Crops that ripen together, read as one ripe time (user story 11): the window is the
/// group's earliest crop's, provenance and all; each crop keeps its own window for the hover.
/// The pots are one group of their own.</summary>
public sealed record RipeGroup(bool IsPots, EtaWindow Window, IReadOnlyList<CropRipe> Crops);

/// <summary>Where the player stands: the estate, whether inside its house or room, and the live
/// indoor map read. Only inside is that read about the pots in front of the player.</summary>
public sealed record Standing(EstateKey Estate, bool Inside, IReadOnlyDictionary<int, PotReading> LiveIndoor);

/// <summary>One planted estate's next visit: when it is needed by and what waits there.
/// <see cref="WaterBy"/> is the estate's water deadline (its earliest bed's), null when no bed
/// carries a clock, and <see cref="Thirst"/> the worst bed's look (past its deadline, water soon,
/// or growing). <see cref="TendUnheard"/> counts the growing beds whose tend was never heard. The
/// chips: <see cref="Water"/> beds due within <see cref="GardenBoard.ChipSpan"/> of the deadline
/// (or of now, once it has passed), <see cref="Harvest"/> containers ripe now or whose ripe
/// window opens within the span (<see cref="RipeNow"/> of them seen ripe), <see cref="Replant"/>
/// containers the plugin knows were emptied. <see cref="PotsWilting"/> counts the pots the live
/// map shows wilting, only on the card of the room the player stands in (ruling 4 as narrowed
/// 2026-11-08): no other card makes a pot water claim.</summary>
public sealed record VisitCard(
    EstateRecord Estate, DateTimeOffset? WaterBy, ContainerLook Thirst, int TendUnheard,
    int Water, int Harvest, int RipeNow, int Replant, int PotsWilting,
    IReadOnlyList<RipeGroup> Ripe, IReadOnlyList<PatchPicture> Patches, TimelineLane Lane);

/// <summary>A card's lane on the timeline under the cards (ruling 17). <see cref="WaterFrom"/> is
/// where the water bar starts: the clock start of the bed that sets the card's water deadline, so
/// the bar is one real clock, from its watering (or sowing) to the deadline drop; null when no bed
/// carries a clock.</summary>
public sealed record TimelineLane(DateTimeOffset? WaterFrom, IReadOnlyList<LaneMark> Marks);

/// <summary>The stretch of time the lanes draw.</summary>
public sealed record TimelineSpan(DateTimeOffset From, DateTimeOffset To);

/// <summary>Which row of a lane a mark sits on: the beds' ripe times, or the pots' below them.</summary>
public enum LaneRow { Beds, Pots }

/// <summary>How a ripe mark is drawn. <see cref="Tick"/> is an exact ripe time (a witnessed
/// sowing, a zero-width window); <see cref="Band"/> a bracketed window, as wide as it is;
/// <see cref="EstimateBand"/> an estimated one, striped. <see cref="RipeNow"/> marks containers
/// seen ripe, at the now line: they carry no window, and the lane would otherwise be blank where
/// the most urgent harvest is.</summary>
public enum LaneMarkKind { Tick, Band, EstimateBand, RipeNow }

/// <summary>One mark on a lane: a ripe group (or what is seen ripe) and its crops for the label.
/// <see cref="Group"/> is the card's ripe group it draws, for the hover; null for ripe now.</summary>
public sealed record LaneMark(
    LaneRow Row, LaneMarkKind Kind, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ushort> Crops,
    RipeGroup? Group = null);

/// <summary>
/// The garden board (dashboard rethink spec, 2026-11-08): everything the Garden tab draws,
/// worked out from the ledger, the access roster, the domain tables and the clock. Pure - the
/// window draws what this returns and holds no rule of its own.
/// </summary>
public static class GardenBoard
{
    /// <summary>Every outdoor patch is eight beds.</summary>
    public const int PatchSlots = 8;

    /// <summary>How far past the card's deadline a bed still counts toward the water chip, and how
    /// soon a ripe window must open to count toward the harvest chip: the visit you make for the
    /// first deadline is the visit that waters and harvests these too. Tunable on the bench.</summary>
    public static readonly TimeSpan ChipSpan = TimeSpan.FromHours(24);

    /// <summary>Crops whose earliest ripe times fall within this of a group's first crop join that
    /// group. Tunable on the bench.</summary>
    public static readonly TimeSpan RipeGroupSpan = TimeSpan.FromHours(3);

    /// <summary>Where each bed slot sits in the patch, as the game lays it out: the eight beds ring
    /// the patch clockwise from the top-left corner, 1 2 3 across the top, 4 and 5 down the right,
    /// 6 and 7 back along the bottom, 8 on the left (GLOSSARY "Bed", checked in the field
    /// 2026-11-07). Column and row on a three-by-three grid whose middle is empty.</summary>
    public static (int Column, int Row) RingPosition(int slot) => Ring[slot];

    private static readonly (int Column, int Row)[] Ring =
        [(0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (1, 2), (0, 2), (0, 1)];

    /// <summary>The lanes' span: from <see cref="SpanBefore"/> ago to <see cref="SpanAhead"/> ahead.</summary>
    public static TimelineSpan Span(DateTimeOffset now) => new(now - SpanBefore, now + SpanAhead);

    /// <summary>How far back the lanes reach: enough to show a water bar's start and a window just
    /// opened. Tunable on the bench.</summary>
    public static readonly TimeSpan SpanBefore = TimeSpan.FromHours(6);

    /// <summary>How far ahead the lanes reach: the next six days (ruling 17). Tunable on the bench.</summary>
    public static readonly TimeSpan SpanAhead = TimeSpan.FromDays(6);

    private static readonly ClockWiltSource Clock = new();

    /// <summary>The visit cards, most urgent first: by water deadline (the clock that can kill);
    /// then the estates with no water clock, those with a plant seen ripe first, then by next ripe
    /// window (ruling 4). One card per roster-covered estate holding a known planting (ruling 5).
    /// <paramref name="standing"/> is the only source of a pot's wilt.</summary>
    public static IReadOnlyList<VisitCard> Cards(
        IReadOnlyList<EstateRecord> estates, IReadOnlyList<ClaimedBed> beds, AccessRoster roster,
        DomainTables tables, DateTimeOffset now, Standing? standing = null)
    {
        return estates
            .Where(e => roster.Covers(e.Key))
            .Select(e => (Estate: e, Beds: beds.Where(b => b.Estate == e.Key).ToList()))
            .Where(e => e.Beds.Any(Planted))
            .Select(e => Card(e.Estate, e.Beds, tables, now,
                standing is { Inside: true } s && s.Estate == e.Estate.Key ? s.LiveIndoor : null))
            .OrderBy(c => c.WaterBy is null)
            .ThenBy(c => c.WaterBy is null && c.RipeNow == 0)
            .ThenBy(c => c.WaterBy ?? c.Ripe.FirstOrDefault()?.Window.Earliest ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.Estate.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Ruling 5: an estate is planted when at least one container holds a known planting
    /// (contents read, or a sowing witnessed). An emptied container holds none.</summary>
    private static bool Planted(ClaimedBed bed) => bed.Latest is not null || bed.PlantedAt is not null;

    private static VisitCard Card(
        EstateRecord estate, List<ClaimedBed> beds, DomainTables tables, DateTimeOffset now,
        IReadOnlyDictionary<int, PotReading>? liveIndoor)
    {
        var patches = beds
            .Where(b => !b.IsPot)
            .GroupBy(b => b.PatchOrdinal)
            .OrderBy(g => g.Key)
            .Select(g => new PatchPicture(g.Key, Enumerable.Range(0, PatchSlots)
                .Select(slot => g.FirstOrDefault(b => b.BedSlot == slot) is { } bed
                    ? Cell(slot, bed, tables, now)
                    : ContainerCell.Unclaimed(slot))
                .ToList()))
            .ToList();

        var pots = beds.Where(b => b.IsPot).OrderBy(b => b.MapKey).ToList();
        if (pots.Count > 0)
            patches.Add(new PatchPicture(null, pots
                .Select(p => Cell(p.MapKey, p, tables, now) is var cell && Wilting(p, liveIndoor)
                    ? cell with { Look = ContainerLook.Wilting }
                    : cell)
                .ToList()));

        var cells = patches.SelectMany(p => p.Cells).Where(c => c.Bed is not null).ToList();
        var bedCells = cells.Where(c => !c.Bed!.IsPot).ToList();
        var waterBy = bedCells.Select(c => c.WaterBy).Where(t => t is not null).Min();
        var thirst = bedCells.Any(c => c.Look == ContainerLook.PastDeadline) ? ContainerLook.PastDeadline
            : bedCells.Any(c => c.Look == ContainerLook.WaterSoon) ? ContainerLook.WaterSoon
            : ContainerLook.Growing;

        // A deadline already behind us is no reason to leave out the beds due in the next hours:
        // the visit happens now at the earliest.
        var waterCut = (waterBy is { } w && w > now ? w : now) + ChipSpan;
        var water = bedCells.Count(c => c.WaterBy is { } t && t <= waterCut);
        var ripeNow = cells.Count(c => c.Look == ContainerLook.Ripe);
        var harvest = ripeNow + cells.Count(c => c.Ripe is { } r && r.Earliest <= now + ChipSpan);

        var ripe = BedRipeGroups(bedCells)
            .Concat(PotsRipeGroup(cells.Where(c => c.Bed!.IsPot)))
            .OrderBy(g => g.Window.Earliest)
            .ToList();

        return new VisitCard(estate, waterBy, thirst,
            TendUnheard: bedCells.Count(c => c.TendUnheard),
            water, harvest, ripeNow,
            Replant: cells.Count(c => c.Look == ContainerLook.Empty),
            PotsWilting: cells.Count(c => c.Look == ContainerLook.Wilting),
            ripe, patches, new TimelineLane(WaterFrom(bedCells, waterBy),
                RipeNowMarks(cells, now).Concat(ripe.Select(Mark)).Where(m => Touches(m, Span(now))).ToList()));
    }

    /// <summary>Where the water bar starts: the clock start of the bed whose deadline is the card's.</summary>
    private static DateTimeOffset? WaterFrom(List<ContainerCell> bedCells, DateTimeOffset? waterBy) => bedCells
        .Where(c => waterBy is not null && c.WaterBy == waterBy)
        .Select(c => ClockWiltSource.ClockStart(c.Bed!))
        .Min();

    /// <summary>A card's ripe group as a lane mark: its provenance decides the drawing.</summary>
    private static LaneMark Mark(RipeGroup group) => new(
        group.IsPots ? LaneRow.Pots : LaneRow.Beds,
        group.Window.Provenance switch
        {
            Provenance.Anchored => LaneMarkKind.Tick,
            Provenance.Bracketed => LaneMarkKind.Band,
            _ => LaneMarkKind.EstimateBand,
        },
        group.Window.Earliest, group.Window.Latest,
        group.Crops.Select(c => c.Species).ToList(), group);

    private static bool Touches(LaneMark mark, TimelineSpan span) => mark.To >= span.From && mark.From <= span.To;

    /// <summary>One "ripe now" mark per row holding a container seen ripe, at the now line.</summary>
    private static IEnumerable<LaneMark> RipeNowMarks(IEnumerable<ContainerCell> cells, DateTimeOffset now) => cells
        .Where(c => c.Look == ContainerLook.Ripe)
        .GroupBy(c => c.Bed!.IsPot ? LaneRow.Pots : LaneRow.Beds)
        .OrderBy(g => g.Key)
        .Select(g => new LaneMark(g.Key, LaneMarkKind.RipeNow, now, now,
            g.Select(c => c.Species).Distinct().Order().ToList()));

    /// <summary>The pot wilt rule on the live read: a pot wilts only when the map in front of the
    /// player says so, and never at ripe (<see cref="PotWiltRule"/>).</summary>
    private static bool Wilting(ClaimedBed pot, IReadOnlyDictionary<int, PotReading>? liveIndoor)
        => liveIndoor is not null && liveIndoor.TryGetValue(pot.MapKey, out var read)
           && read.Occupied && PotWiltRule.Claims(read.Wilt, read.Stage);

    private static IEnumerable<CropRipe> EarliestPerCrop(IEnumerable<ContainerCell> cells) => cells
        .Where(c => c.Ripe is not null)
        .GroupBy(c => c.Species)
        .Select(g => new CropRipe(g.Key, g.Select(c => c.Ripe!).MinBy(w => w.Earliest)!))
        .OrderBy(c => c.Window.Earliest)
        .ThenBy(c => c.Species);

    /// <summary>Bed crops grouped in ripening order: a crop joins the open group when it ripens
    /// within <see cref="RipeGroupSpan"/> of the group's first.</summary>
    private static IEnumerable<RipeGroup> BedRipeGroups(IEnumerable<ContainerCell> cells)
    {
        var group = new List<CropRipe>();
        foreach (var crop in EarliestPerCrop(cells))
        {
            if (group.Count > 0 && crop.Window.Earliest - group[0].Window.Earliest > RipeGroupSpan)
            {
                yield return new RipeGroup(IsPots: false, group[0].Window, group);
                group = [];
            }
            group.Add(crop);
        }
        if (group.Count > 0)
            yield return new RipeGroup(IsPots: false, group[0].Window, group);
    }

    /// <summary>The pots as one group: their earliest window leads, every pot crop rides along.</summary>
    private static IEnumerable<RipeGroup> PotsRipeGroup(IEnumerable<ContainerCell> cells)
    {
        var crops = EarliestPerCrop(cells).ToList();
        if (crops.Count > 0)
            yield return new RipeGroup(IsPots: true, crops[0].Window, crops);
    }

    private static ContainerCell Cell(int position, ClaimedBed bed, DomainTables tables, DateTimeOffset now)
    {
        if (bed.Latest is not { } latest)
        {
            // Known empty only when something emptied it (a fence); a record that exists only
            // because a tend was heard before any read has simply never been read.
            var look = bed.PlantingResetAt is null ? ContainerLook.Unread : ContainerLook.Empty;
            return new ContainerCell(position, look, Species: 0, Stage: 0, TendUnheard: false,
                WaterBy: null, Ripe: null, bed);
        }

        var crop = tables.CropBySpeciesIndex(latest.SpeciesIndex);
        var ripe = latest.Stage >= GardenStage.Ripe;
        var water = crop is null ? WaterState.Unknown : Clock.StateFor(bed, crop, now);
        var grown = ripe ? ContainerLook.Ripe
            : water == WaterState.Due ? ContainerLook.WaterSoon
            : water is WaterState.Overdue or WaterState.Danger ? ContainerLook.PastDeadline
            : ContainerLook.Growing;

        var window = !ripe && tables.GrowHours(latest.SpeciesIndex) is { } growHours
            ? StageModel.RipeWindow(bed, growHours)?.Window
            : null;

        return new ContainerCell(position, grown, latest.SpeciesIndex, latest.Stage,
            TendUnheard: !ripe && ClockWiltSource.TendUnheard(bed),
            WaterBy: crop is null ? null : ClockWiltSource.WaterBy(bed, crop),
            window, bed);
    }
}
