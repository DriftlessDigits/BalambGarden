using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Derivations;

/// <summary>What one container in the garden picture is saying. <see cref="Unclaimed"/> is a bed
/// slot the ledger has nothing for; <see cref="Unread"/> a record whose contents were never read
/// (a watering heard before any read); <see cref="Empty"/> a container the plugin knows was
/// emptied (a harvest witnessed or an empty read). The water looks follow the colour map (ruling
/// 22), beds and pots alike (ruling 23): <see cref="WaterSoon"/> is past three quarters of its wilt
/// hours (the clock's Due); <see cref="Wilting"/> past its wilt hours (Overdue), or a pot the game
/// shows wilting - the live map where the player stands, the last sighting elsewhere;
/// <see cref="Dying"/> past halfway from its wilt hours to withering (Danger). <see cref="Dead"/> is
/// a plant the game reads as lost (stage 5, "beyond hope"): never ripe, never growing, no water
/// claim, one more container to clear and sow (rethink slice 23).</summary>
public enum ContainerLook { Unclaimed, Unread, Empty, Growing, WaterSoon, Wilting, Dying, Ripe, Dead }

/// <summary>One container of the garden picture, with what its hover needs. <see cref="Position"/>
/// is the bed's slot (0-7) in a patch, or the pot's map key among the pots. <see cref="WaterBy"/>
/// and <see cref="DiesAt"/> (when the plant withers if nobody waters) are null wherever the wilt
/// clock makes no claim; <see cref="TendUnheard"/> marks a growing bed or pot whose deadline (if
/// any) rests on its sowing, no watering recorded. <see cref="WiltShown"/> marks a pot whose wilt
/// the game itself showed: the live read in its room, or (with <see cref="WiltSeenAt"/>, when the
/// game last showed it) the sighting its record keeps. <see cref="NeverWilts"/> marks a plant with
/// no wilt hours in the crop table (the flowerpot flowers): no clock, by its nature.
/// <see cref="SeenDeadAt"/> is when the read that showed a <see cref="ContainerLook.Dead"/> plant
/// was taken (the record keeps only the newest read, so it is the last time it was seen dead).
/// <see cref="SeenFineAt"/> is set on a pot whose clock rests on the seen-fine floor alone (rethink
/// slice 21): when the room last showed it fine, the "?" over its cell, and its hover's words.</summary>
public sealed record ContainerCell(
    int Position, ContainerLook Look, ushort Species, byte Stage, bool TendUnheard,
    DateTimeOffset? WaterBy, EtaWindow? Ripe, ClaimedBed? Bed, DateTimeOffset? DiesAt = null,
    DateTimeOffset? WiltSeenAt = null, bool WiltShown = false, bool NeverWilts = false,
    DateTimeOffset? SeenDeadAt = null, DateTimeOffset? SeenFineAt = null)
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

/// <summary>How many beds and pots a card line is about, and the patch of the bed when there is
/// exactly one (spec ruling 23: the card names the kind and the place, never a number).</summary>
public sealed record ContainerCount(int Beds, int Pots, int? Patch)
{
    public static readonly ContainerCount None = new(0, 0, null);

    public int Total => Beds + Pots;

    public static ContainerCount Of(IEnumerable<ContainerCell> cells)
    {
        var held = cells.Where(c => c.Bed is not null).ToList();
        var beds = held.Where(c => !c.Bed!.IsPot).ToList();
        return new(beds.Count, held.Count - beds.Count, beds.Count == 1 ? beds[0].Bed!.PatchOrdinal : null);
    }
}

/// <summary>Crops that ripen together, read as one ripe time (user story 11): the window is the
/// group's earliest crop's, provenance and all; each crop keeps its own window for the hover.
/// The pots are one group of their own.</summary>
public sealed record RipeGroup(bool IsPots, EtaWindow Window, IReadOnlyList<CropRipe> Crops);

/// <summary>Where the player stands: the estate, whether inside its house or room, and the live
/// indoor map read. Only inside is that read about the pots in front of the player.</summary>
public sealed record Standing(EstateKey Estate, bool Inside, IReadOnlyDictionary<int, PotReading> LiveIndoor);

/// <summary>One planted estate's next visit: when it is needed by and what waits there.
/// <see cref="WaterBy"/> is the estate's water deadline (its earliest bed's or pot's), null when
/// nothing carries a clock, with <see cref="FirstToWilt"/> the beds and pots whose deadline it is;
/// <see cref="Thirst"/> is the worst look among them all (dying, wilting, water soon, or growing),
/// with <see cref="Worst"/> the beds and pots that wear it (none while growing).
/// <see cref="TendUnheard"/> counts the growing beds and pots with no watering recorded. The
/// chips: <see cref="Water"/> beds and pots due within <see cref="GardenBoard.ChipSpan"/> of the deadline
/// (or of now, once it has passed), <see cref="Harvest"/> containers ripe now or whose ripe
/// window opens within the span (<see cref="RipeNow"/> of them seen ripe), <see cref="Replant"/>
/// containers to sow: the ones the plugin knows were emptied and the <see cref="Dead"/> ones, which
/// need clearing first (rethink slice 23). <see cref="PotsWilting"/> counts the pots the game
/// showed wilting (ruling 4, rethink slice 19): in the room the player stands in, the live map's;
/// anywhere else, the pots this install last saw wilting and has not heard watered, harvested or
/// replanted since, with <see cref="PotsWiltingSeenAt"/> the newest of those sightings (null on the
/// live read). A pot wilting by its clock alone is not among them: it speaks as a wilting bed does,
/// in its cell and the deadline (ruling 23). <see cref="DeadlineSeenFine"/> is set when every pot
/// that sets the deadline rests on the seen-fine floor alone (rethink slice 21): the deadline is
/// then the look itself, so the headline says what to do rather than "water now ... over".</summary>
public sealed record VisitCard(
    EstateRecord Estate, DateTimeOffset? WaterBy, ContainerCount FirstToWilt, ContainerLook Thirst,
    ContainerCount Worst, ContainerCount TendUnheard,
    ContainerCount Water, int Harvest, int RipeNow, int Replant, ContainerCount Dead,
    int PotsWilting, DateTimeOffset? PotsWiltingSeenAt,
    IReadOnlyList<RipeGroup> Ripe, IReadOnlyList<PatchPicture> Patches, TimelineLane Lane,
    SeenFineDeadline? DeadlineSeenFine = null);

/// <summary>A card deadline that rests on the seen-fine floor alone (rethink slice 21):
/// <see cref="SeenAt"/> is the newest look at the pots that set it; <see cref="MayBeDying"/> once
/// any of them has passed its floor's dying point (the earliest it could be dying).</summary>
public sealed record SeenFineDeadline(DateTimeOffset SeenAt, bool MayBeDying);

/// <summary>A card's lane on the timeline under the cards (ruling 17). <see cref="WaterFrom"/> is
/// where the water bar starts: the clock start of the bed or pot that sets the card's water
/// deadline, so the bar is one real clock, from its watering (or sowing) to the deadline drop; null
/// when nothing carries a clock. <see cref="FromSowing"/>: that start is a sowing, no watering
/// recorded. <see cref="SeenFine"/>: the card's deadline rests on the seen-fine floor alone
/// (<see cref="VisitCard.DeadlineSeenFine"/>), so the bar starts at the look, the last thing the
/// plugin knows, never at the floor a day before it: the deadline is that look, so only the drop
/// shows.</summary>
public sealed record TimelineLane(DateTimeOffset? WaterFrom, IReadOnlyList<LaneMark> Marks, bool FromSowing = false,
    bool SeenFine = false);

/// <summary>The stretch of time the lanes draw.</summary>
public sealed record TimelineSpan(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>Every midnight inside the span in <paramref name="zone"/> (the reader's own, which
    /// the window passes in), each with that day's own offset, so a clock change moves the line
    /// with it.</summary>
    public IReadOnlyList<DateTimeOffset> Midnights(TimeZoneInfo zone)
    {
        var days = new List<DateTimeOffset>();
        var day = TimeZoneInfo.ConvertTime(From, zone).Date.AddDays(1);
        for (var midnight = AtMidnight(day, zone); midnight < To; midnight = AtMidnight(day = day.AddDays(1), zone))
            days.Add(midnight);
        return days;
    }

    private static DateTimeOffset AtMidnight(DateTime date, TimeZoneInfo zone) => new(date, zone.GetUtcOffset(date));
}

/// <summary>Which row of a lane a mark sits on: the beds' ripe times, or the pots' below them.</summary>
public enum LaneRow { Beds, Pots }

/// <summary>How a ripe mark is drawn. <see cref="Tick"/> is an anchored window (a witnessed
/// sowing, zero width); <see cref="Band"/> a bracketed window, as wide as it is;
/// <see cref="EstimatedBand"/> an estimated one, striped. <see cref="RipeNow"/> marks containers
/// seen ripe, at the now line: they carry no window, and the lane would otherwise be blank where
/// the most urgent harvest is.</summary>
public enum LaneMarkKind { Tick, Band, EstimatedBand, RipeNow }

/// <summary>One mark on a lane: a ripe group (or what is seen ripe) and its crops for the label.
/// <see cref="Group"/> is the card's own ripe group it draws, so the lane and the card cannot
/// disagree (ruled at rethink slice 4: the lane says what the card says); null only for
/// <see cref="LaneMarkKind.RipeNow"/>, which has no window and so needs its own extent.</summary>
public sealed record LaneMark(
    LaneRow Row, LaneMarkKind Kind, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ushort> Crops,
    RipeGroup? Group = null);

/// <summary>
/// The garden board (dashboard rethink spec, 2026-11-08): everything the Garden tab draws,
/// worked out from the ledger, the access roster, the domain tables and the clock. Pure - the
/// window draws what this returns and holds no rule of its own.
/// </summary>
public static partial class GardenBoard
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

    /// <summary>The lanes' span: from <see cref="SpanBefore"/> ago to <see cref="SpanAhead"/> ahead.
    /// A lane holds every mark that starts before the span's end: one whose window already closed
    /// is still on the card, likely ripe and unpicked, and stays on the lane pinned at its start.</summary>
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
    /// A pot's wilt comes from <paramref name="standing"/>'s live read in its room, else from the
    /// sighting its record keeps.</summary>
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
                .Select(p => PotCell(p, Cell(p.MapKey, p, tables, now), liveIndoor))
                .ToList()));

        // Beds and pots alike carry the clock (ruling 23): the deadline is the earliest of them all.
        var cells = patches.SelectMany(p => p.Cells).Where(c => c.Bed is not null).ToList();
        var bedCells = cells.Where(c => !c.Bed!.IsPot).ToList();
        var waterBy = cells.Select(c => c.WaterBy).Where(t => t is not null).Min();
        var firstToWilt = waterBy is null ? [] : cells.Where(c => c.WaterBy == waterBy).ToList();
        // The worst water look here, the deadline's colour.
        var thirst = Thirsts.FirstOrDefault(look => cells.Any(c => c.Look == look), ContainerLook.Growing);

        // A deadline already behind us is no reason to leave out the ones due in the next hours:
        // the visit happens now at the earliest.
        var waterCut = (waterBy is { } w && w > now ? w : now) + ChipSpan;
        var water = cells.Where(c => c.WaterBy is { } t && t <= waterCut);
        var ripeNow = cells.Count(c => c.Look == ContainerLook.Ripe);
        var harvest = ripeNow + cells.Count(c => c.Ripe is { } r && r.Earliest <= now + ChipSpan);

        var ripe = BedRipeGroups(bedCells)
            .Concat(PotsRipeGroup(cells.Where(c => c.Bed!.IsPot)))
            .OrderBy(g => g.Window.Earliest)
            .ToList();

        // The note counts what the game showed; a pot wilting by its clock alone speaks in its cell.
        var potsWilting = cells.Where(c => c.WiltShown).ToList();

        // The deadline rests on the seen-fine floor when every bed or pot setting it does (slice 21).
        var seenFine = firstToWilt.Count > 0 && firstToWilt.All(c => c.SeenFineAt is not null)
            ? new SeenFineDeadline(firstToWilt.Max(c => c.SeenFineAt!.Value), firstToWilt.Any(c => c.Look == ContainerLook.Dying))
            : null;

        // The water bar is the clock of the one that sets the deadline: on the floor it starts at the
        // look; otherwise at the earliest recorded clock among the setters not on the floor (theirs
        // is the recorded start, the floor being no later).
        var barSetter = seenFine is null
            ? firstToWilt.Where(c => c.SeenFineAt is null).MinBy(c => ClockWiltSource.RecordedStart(c.Bed!))
            : null;
        var barFrom = seenFine?.SeenAt ?? (barSetter is null ? null : ClockWiltSource.RecordedStart(barSetter.Bed!));

        return new VisitCard(estate, waterBy,
            FirstToWilt: ContainerCount.Of(firstToWilt),
            Thirst: thirst,
            Worst: thirst == ContainerLook.Growing ? ContainerCount.None : ContainerCount.Of(cells.Where(c => c.Look == thirst)),
            TendUnheard: ContainerCount.Of(cells.Where(c => c.TendUnheard)),
            Water: ContainerCount.Of(water), harvest, ripeNow,
            Replant: cells.Count(c => c.Look is ContainerLook.Empty or ContainerLook.Dead),
            Dead: ContainerCount.Of(cells.Where(c => c.Look == ContainerLook.Dead)),
            PotsWilting: potsWilting.Count,
            PotsWiltingSeenAt: potsWilting.Select(c => c.WiltSeenAt).Max(),
            ripe, patches, new TimelineLane(
                barFrom,
                RipeNowMarks(LaneRow.Beds, bedCells, now)
                    .Concat(RipeNowMarks(LaneRow.Pots, cells.Where(c => c.Bed!.IsPot), now))
                    .Concat(ripe.Select(Mark))
                    .Where(m => m.From <= Span(now).To)
                    .ToList(),
                FromSowing: barSetter is not null && barSetter.Bed!.LastTended is null,
                SeenFine: seenFine is not null),
            seenFine);
    }

    /// <summary>The water looks a card's thirst can take, worst first.</summary>
    private static readonly ContainerLook[] Thirsts = [ContainerLook.Dying, ContainerLook.Wilting, ContainerLook.WaterSoon];

    /// <summary>A card's ripe group as a lane mark: its provenance decides the drawing.</summary>
    private static LaneMark Mark(RipeGroup group) => new(
        group.IsPots ? LaneRow.Pots : LaneRow.Beds,
        group.Window.Provenance switch
        {
            Provenance.Anchored => LaneMarkKind.Tick,
            Provenance.Bracketed => LaneMarkKind.Band,
            _ => LaneMarkKind.EstimatedBand,
        },
        group.Window.Earliest, group.Window.Latest,
        group.Crops.Select(c => c.Species).ToList(), group);

    /// <summary>One "ripe now" mark for a row, when anything on it is seen ripe.</summary>
    private static IEnumerable<LaneMark> RipeNowMarks(LaneRow row, IEnumerable<ContainerCell> cells, DateTimeOffset now)
    {
        var crops = cells.Where(c => c.Look == ContainerLook.Ripe).Select(c => c.Species).Distinct().Order().ToList();
        if (crops.Count > 0)
            yield return new LaneMark(row, LaneMarkKind.RipeNow, now, now, crops);
    }

    /// <summary>A pot's wilt. The cell comes in wearing its clock's look (ruling 23); where the game
    /// speaks, the game wins. In the room the live read decides: the pot wilts when the map in front
    /// of the player says so (<see cref="PotWiltRule"/>), and a pot the map shows not wilting is not
    /// wilting or dying, whatever its clock says (a watering went unrecorded; the census's read of
    /// the same room floors the clock, slice 21, so a pot on the floor reads "water soon" here as
    /// away). Away, it wilts on the sighting the ledger keeps, never once its contents read ripe (a
    /// ripe pot never claims) or are gone. A clock already past dying stays dying: the game's wilt
    /// agrees, and the clock says how far it has gone. Where the game shows the wilt, its word
    /// stands alone: no "?" for the floor.</summary>
    private static ContainerCell PotCell(
        ClaimedBed pot, ContainerCell cell, IReadOnlyDictionary<int, PotReading>? liveIndoor)
    {
        var shownLook = cell.Look == ContainerLook.Dying ? ContainerLook.Dying : ContainerLook.Wilting;

        if (liveIndoor is not null)
        {
            if (!liveIndoor.TryGetValue(pot.MapKey, out var read) || !read.Occupied)
                return cell;
            if (PotWiltRule.Claims(read.Wilt, read.Stage))
                return cell with { Look = shownLook, WiltShown = true, SeenFineAt = null };
            return cell.Look is ContainerLook.Wilting or ContainerLook.Dying
                ? cell with { Look = ContainerLook.Growing }
                : cell;
        }

        return pot.WiltSeenAt is { } seen && pot.Latest is { Stage: < PotWiltRule.RipeStage }
            ? cell with { Look = shownLook, WiltSeenAt = seen, WiltShown = true, SeenFineAt = null }
            : cell;
    }

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

        // The game publishes death as stage 5 (rethink slice 23): dead, never ripe and never
        // growing, no ripe window and no water claim, until a later read replaces it.
        if (bed.ReadsDead)
            return new ContainerCell(position, ContainerLook.Dead, latest.SpeciesIndex, latest.Stage,
                TendUnheard: false, WaterBy: null, Ripe: null, bed, SeenDeadAt: latest.At);

        var crop = tables.CropBySpeciesIndex(latest.SpeciesIndex);
        var ripe = latest.Stage >= GardenStage.Ripe;
        var water = crop is null ? WaterState.Unknown : Clock.StateFor(bed, crop, now);
        // A pot whose clock rests on the seen-fine floor alone was fine at the look, so past its
        // floor's wilt hours it may wilt any time, not "is wilting" (ruled 2026-11-10 at slice 21's
        // plan call): water soon until the floor's dying point.
        var onSeenFineFloor = !ripe && crop is not null && ClockWiltSource.RestsOnSeenFineFloor(bed, crop);
        var grown = ripe ? ContainerLook.Ripe : water switch
        {
            WaterState.Due => ContainerLook.WaterSoon,
            WaterState.Overdue when onSeenFineFloor => ContainerLook.WaterSoon,
            WaterState.Overdue => ContainerLook.Wilting,
            WaterState.Danger => ContainerLook.Dying,
            _ => ContainerLook.Growing,
        };

        var window = !ripe && tables.GrowHours(latest.SpeciesIndex) is { } growHours
            ? StageModel.RipeWindow(bed, growHours)?.Window
            : null;

        // No crop row, no wilt hours: the flowerpot flowers, which never wilt (ruling 23).
        return new ContainerCell(position, grown, latest.SpeciesIndex, latest.Stage,
            TendUnheard: !ripe && crop is not null && ClockWiltSource.TendUnheard(bed),
            WaterBy: crop is null ? null : ClockWiltSource.WaterBy(bed, crop),
            window, bed,
            DiesAt: crop is null ? null : ClockWiltSource.DiesAt(bed, crop),
            NeverWilts: crop is null && tables.GrowHours(latest.SpeciesIndex) is not null,
            SeenFineAt: onSeenFineFloor ? bed.SeenFineAt : null);
    }
}
