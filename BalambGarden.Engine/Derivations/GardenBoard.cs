using BalambGarden.Engine.Census;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sensing;

namespace BalambGarden.Engine.Derivations;

/// <summary>What one container in the garden picture is saying. <see cref="Unclaimed"/> is a bed
/// slot the ledger has nothing for; <see cref="Empty"/> is a container the plugin knows was
/// emptied (a harvest or an empty read). <see cref="WaterSoon"/> is a bed past three quarters of
/// its wilt hours, <see cref="PastDeadline"/> one past its water deadline. <see cref="Wilting"/>
/// is a pot the live map shows wilting where the player stands - observed, never clocked.</summary>
public enum ContainerLook { Unclaimed, Empty, Growing, WaterSoon, PastDeadline, Wilting, Ripe }

/// <summary>One container of a patch or of the pots, with what its hover needs. Slot is the bed
/// slot (0-7) for a bed and the map key for a pot. <see cref="WaterBy"/> is null wherever the
/// clock makes no claim; <see cref="WateringUnheard"/> marks a growing bed whose deadline (if
/// any) rests on its sowing, not on a heard watering.</summary>
public sealed record ContainerCell(
    int Slot, ContainerLook Look, ushort Species, byte Stage, bool WateringUnheard,
    DateTimeOffset? WaterBy, EtaWindow? Ripe, ClaimedBed? Bed);

/// <summary>A patch laid out by slot (always <see cref="GardenBoard.PatchSlots"/> cells), or an
/// estate's pots in map-key order (<see cref="IsPots"/>; only recorded pots, no holes).</summary>
public sealed record PatchPicture(int Ordinal, bool IsPots, IReadOnlyList<ContainerCell> Cells);

/// <summary>One crop's next ripe window on a card: the earliest of its growing containers'.</summary>
public sealed record CropRipe(ushort Species, EtaWindow Window);

/// <summary>Crops that ripen together, read as one ripe time (user story 11): the window is the
/// group's earliest crop's, provenance and all; each crop keeps its own window for the hover.
/// Beds and pots never share a group.</summary>
public sealed record RipeGroup(bool IsPots, EtaWindow Window, IReadOnlyList<CropRipe> Crops);

/// <summary>One planted estate's next visit: when it is needed by and what waits there.
/// <see cref="WaterBy"/> is the estate's water deadline (its earliest bed's), null when no bed
/// carries a clock. <see cref="WateringUnheard"/> counts the growing beds whose watering was
/// never heard. The chips: <see cref="Water"/> beds due within <see cref="GardenBoard.ChipSpan"/>
/// of the deadline (or of now, once it has passed), <see cref="Harvest"/> containers ripe now or
/// ripening within the span (<see cref="RipeNow"/> of them already ripe), <see cref="Replant"/>
/// containers the plugin knows were emptied. <see cref="PotsWilting"/> counts the pots the live
/// map shows wilting, and only on the card of the room the player stands in (ruling 4 as
/// narrowed 2026-11-08): no other card makes a pot water claim.</summary>
public sealed record VisitCard(
    EstateRecord Estate, DateTimeOffset? WaterBy, int WateringUnheard,
    int Water, int Harvest, int RipeNow, int Replant, int PotsWilting,
    IReadOnlyList<RipeGroup> Ripe, IReadOnlyList<PatchPicture> Patches);

/// <summary>
/// The garden board (dashboard rethink spec, 2026-11-08): everything the Garden tab draws,
/// worked out from the ledger, the access roster, the domain tables and the clock. Pure - the
/// window draws what this returns and holds no rule of its own.
/// </summary>
public static class GardenBoard
{
    /// <summary>Every outdoor patch is eight beds.</summary>
    public const int PatchSlots = 8;

    /// <summary>The ordinal an estate's pots picture carries: the pots are one group, whatever
    /// per-pot ordinals the ledger rows hold.</summary>
    public const int PotsOrdinal = -1;

    /// <summary>How far past the card's deadline a bed still counts toward the water chip, and how
    /// soon a plant must ripen to count toward the harvest chip: the visit you make for the first
    /// deadline is the visit that waters and harvests these too. Tunable on the bench (spec).</summary>
    public static readonly TimeSpan ChipSpan = TimeSpan.FromHours(24);

    /// <summary>Crops whose earliest ripe times fall within this of a group's first crop join that
    /// group. Tunable on the bench.</summary>
    public static readonly TimeSpan RipeGroupSpan = TimeSpan.FromHours(3);

    private static readonly ClockWiltSource Clock = new();

    /// <summary>The visit cards, most urgent first: by water deadline (the clock that can kill),
    /// then the estates with no water clock by next harvest, ripe now first (ruling 4). One card
    /// per roster-covered estate holding a known planting (ruling 5). <paramref name="here"/> and
    /// <paramref name="liveIndoor"/> are where the player stands and the live indoor map read
    /// there; together they are the only source of a pot's wilt.</summary>
    public static IReadOnlyList<VisitCard> Cards(
        IReadOnlyList<EstateRecord> estates, IReadOnlyList<ClaimedBed> beds, AccessRoster roster,
        DomainTables tables, DateTimeOffset now,
        EstateKey? here = null, IReadOnlyDictionary<int, PotReading>? liveIndoor = null)
    {
        return estates
            .Where(e => roster.Covers(e.Key))
            .Select(e => (Estate: e, Beds: beds.Where(b => b.Estate == e.Key).ToList()))
            .Where(e => e.Beds.Any(Planted))
            .Select(e => Card(e.Estate, e.Beds, tables, now, e.Estate.Key == here ? liveIndoor : null))
            .OrderBy(c => c.WaterBy is null)
            .ThenBy(c => c.WaterBy ?? NextHarvest(c, now))
            .ThenBy(c => c.Estate.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>When the card's next harvest is: now when anything is ripe, else its earliest ripe
    /// group; never, when it has no ripe window at all.</summary>
    private static DateTimeOffset NextHarvest(VisitCard card, DateTimeOffset now)
        => card.RipeNow > 0 ? now : card.Ripe.FirstOrDefault()?.Window.Earliest ?? DateTimeOffset.MaxValue;

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
            .Select(g => new PatchPicture(g.Key, IsPots: false, Enumerable.Range(0, PatchSlots)
                .Select(slot => g.FirstOrDefault(b => b.BedSlot == slot) is { } bed
                    ? Cell(slot, bed, tables, now)
                    : new ContainerCell(slot, ContainerLook.Unclaimed, 0, 0, false, null, null, null))
                .ToList()))
            .ToList();

        var pots = beds.Where(b => b.IsPot).OrderBy(b => b.MapKey).ToList();
        if (pots.Count > 0)
            patches.Add(new PatchPicture(PotsOrdinal, IsPots: true,
                pots.Select(p => Wilting(p, liveIndoor)
                        ? Cell(p.MapKey, p, tables, now) with { Look = ContainerLook.Wilting }
                        : Cell(p.MapKey, p, tables, now))
                    .ToList()));

        var cells = patches.SelectMany(p => p.Cells).Where(c => c.Bed is not null).ToList();
        var waterBy = cells.Select(c => c.WaterBy).Where(t => t is not null).Min();
        var unheard = cells.Count(c => c.WateringUnheard);

        // A deadline already behind us is no reason to leave out the beds due in the next hours:
        // the visit happens now at the earliest.
        var waterCut = (waterBy is { } w && w > now ? w : now) + ChipSpan;
        var water = cells.Count(c => c.WaterBy is { } t && t <= waterCut);
        var ripeNow = cells.Count(c => c.Look == ContainerLook.Ripe);
        var harvest = ripeNow + cells.Count(c => c.Ripe is { } r && r.Earliest <= now + ChipSpan);
        var replant = cells.Count(c => c.Look == ContainerLook.Empty);

        var ripe = RipeGroups(cells.Where(c => !c.Bed!.IsPot), isPots: false)
            .Concat(RipeGroups(cells.Where(c => c.Bed!.IsPot), isPots: true))
            .OrderBy(g => g.Window.Earliest)
            .ToList();

        var potsWilting = cells.Count(c => c.Look == ContainerLook.Wilting);

        return new VisitCard(estate, waterBy, unheard, water, harvest, ripeNow, replant, potsWilting, ripe, patches);
    }

    /// <summary>The pot wilt rule on the live read: a pot wilts only when the map standing there
    /// says so, and never at ripe (<see cref="PotWiltRule"/>).</summary>
    private static bool Wilting(ClaimedBed pot, IReadOnlyDictionary<int, PotReading>? liveIndoor)
        => liveIndoor is not null && liveIndoor.TryGetValue(pot.MapKey, out var read)
           && read.Occupied && PotWiltRule.Claims(read.Wilt, read.Stage);

    /// <summary>Each crop's earliest window, then crops grouped in ripening order: a crop joins
    /// the open group when it ripens within <see cref="RipeGroupSpan"/> of the group's first.</summary>
    private static IEnumerable<RipeGroup> RipeGroups(IEnumerable<ContainerCell> cells, bool isPots)
    {
        var crops = cells
            .Where(c => c.Ripe is not null)
            .GroupBy(c => c.Species)
            .Select(g => new CropRipe(g.Key, g.Select(c => c.Ripe!).MinBy(w => w.Earliest)!))
            .OrderBy(c => c.Window.Earliest)
            .ThenBy(c => c.Species);

        var group = new List<CropRipe>();
        foreach (var crop in crops)
        {
            if (group.Count > 0 && crop.Window.Earliest - group[0].Window.Earliest > RipeGroupSpan)
            {
                yield return new RipeGroup(isPots, group[0].Window, group);
                group = [];
            }
            group.Add(crop);
        }
        if (group.Count > 0)
            yield return new RipeGroup(isPots, group[0].Window, group);
    }

    private static ContainerCell Cell(int slot, ClaimedBed bed, DomainTables tables, DateTimeOffset now)
    {
        if (bed.Latest is not { } latest)
            return new ContainerCell(slot, ContainerLook.Empty, 0, 0, false, null, null, bed);

        var crop = tables.CropBySpeciesIndex(latest.SpeciesIndex);
        var ripe = latest.Stage >= GardenStage.Ripe;
        var water = crop is null ? WaterState.Unknown : Clock.StateFor(bed, crop, now);
        var look = ripe ? ContainerLook.Ripe
            : water == WaterState.Due ? ContainerLook.WaterSoon
            : water is WaterState.Overdue or WaterState.Danger ? ContainerLook.PastDeadline
            : ContainerLook.Growing;

        var window = !ripe && tables.GrowHours(latest.SpeciesIndex) is { } growHours
            ? StageModel.RipeWindow(bed, growHours)?.Window
            : null;

        return new ContainerCell(slot, look, latest.SpeciesIndex, latest.Stage,
            WateringUnheard: !ripe && ClockWiltSource.WateringUnheard(bed),
            WaterBy: crop is null ? null : ClockWiltSource.WaterBy(bed, crop),
            window, bed);
    }
}
