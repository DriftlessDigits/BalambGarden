using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The yield book's partner split through the garden board's seam (rethink slice 8,
/// ruling 12): sowings with their patch at sow go in, a crop's rows by partner come out; soil splits
/// nothing (ruled 2026-11-11). The partner is worked out from the patch at sow by the crossbreed
/// walk (right, down, up, left on the patch's grid, first valid partner wins), never stored.</summary>
public class YieldSplitTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-07T16:00:00Z");

    // Species indexes from Data/SpeciesIndex.json; item ids from Data/Crops.json and Data/Soils.json.
    private const ushort Mirror = 17, Krakka = 49, Onion = 50, Fig = 65, SunLemon = 14;
    private const uint SunLemonSeeds = 7728, KukuruSeeds = 7750;
    private const uint Thanalan3 = 7766, Shroud3 = 7763, PottingSoil = 16026;

    private static readonly EstateKey Yard = new(340, 11, 32);

    private static int seq;

    /// <summary>A patch at sow: every slot empty but the ones named, each (slot, species, stage).</summary>
    private static SyncPatchBed[] Patch(params (int Slot, ushort Species, byte Stage)[] beds)
        => Enumerable.Range(0, GardenBoard.PatchSlots)
            .Select(slot => beds.FirstOrDefault(b => b.Slot == slot) is { Species: > 0 } b
                ? new SyncPatchBed(slot, true, b.Species, b.Stage)
                : new SyncPatchBed(slot, false, 0, 0))
            .ToArray();

    private static HistoryEntry Plant(DateTimeOffset at, ushort species, int patch, int slot,
        SyncPatchBed[]? beside = null, uint? soil = null, bool pot = false)
        => new($"hhbbaa:eraaaa:{++seq}", at, HistoryKind.Plant, species, Yard, pot, patch, pot ? 0 : slot,
            "hhbbaa", SyncSource.Chain, null, Soil: soil, Patch: beside);

    private static HistoryEntry Harvest(DateTimeOffset at, ushort species, int patch, int slot,
        int? seedQty = null, uint? seedItem = null, bool pot = false)
        => new($"hhbbaa:eraaaa:{++seq}", at, HistoryKind.Harvest, species, Yard, pot, patch, pot ? 0 : slot,
            "hhbbaa", SyncSource.Chain, [new SyncYield(1, 12)], seedQty, seedItem);

    /// <summary>A bed sown with its patch at sow (and a soil when given), then harvested with the cross seed as given.</summary>
    private static HistoryEntry[] Grown(ushort crop, int patch, int slot, SyncPatchBed[]? beside,
        int? seedQty = 1, uint? seedItem = KukuruSeeds, uint? soil = null)
        => [Plant(T0, crop, patch, slot, beside, soil), Harvest(T0.AddDays(3), crop, patch, slot, seedQty, seedItem)];

    private static GardenHistory History(params HistoryEntry[][] rows)
    {
        var history = new GardenHistory();
        foreach (var row in rows.SelectMany(r => r))
            history.Record(row);
        return history;
    }

    private static YieldRow Bed(GardenHistory history, ushort crop, params ClaimedBed[] ledger)
        => GardenBoard.Yields(history, ledger, T).Beds.Single(r => r.Species == crop);

    // Mirror Apple in slot 1 (top middle): the walk looks right (slot 2), then left (slot 0); slot
    // 1 has nothing below it (the patch's middle) and nothing above.
    private static readonly SyncPatchBed[] BesideKrakka = Patch((1, Mirror, 1), (2, Krakka, 4));
    private static readonly SyncPatchBed[] BesideFig = Patch((1, Mirror, 1), (0, Fig, 4));

    [Fact]
    public void ACropThatCrossedWithTwoPartnersSplitsIntoARowPerPartner()
    {
        // The spec's example: Mirror Apple beside Krakka Root at one estate, beside Old World Fig at
        // another. Each partner's row carries its own counts, rate and seeds.
        var mirror = Bed(History(
                Grown(Mirror, 0, 1, BesideKrakka, 1, SunLemonSeeds),
                Grown(Mirror, 1, 1, BesideKrakka, 0, null),
                Grown(Mirror, 2, 1, BesideFig, 1, KukuruSeeds)),
            Mirror);

        Assert.Equal((3, 3), (mirror.Planted, mirror.Harvested));
        Assert.Equal([Partner.Beside(Krakka), Partner.Beside(Fig)], mirror.Splits.Select(s => s.Partner!.Value));
        var krakka = mirror.Splits[0];
        Assert.Equal((2, 2, new CrossRate(1, 2)), (krakka.Planted, krakka.Harvested, krakka.Cross));
        Assert.Equal([new SeedShare(SunLemonSeeds, 1, 100)], krakka.Seeds);
        var fig = mirror.Splits[1];
        Assert.Equal((1, 1, new CrossRate(1, 1)), (fig.Planted, fig.Harvested, fig.Cross));
        Assert.Equal([new SeedShare(KukuruSeeds, 1, 100)], fig.Seeds);
    }

    [Fact]
    public void ACropWithOnePartnerStaysOneRowAndThePartnerRidesTheHover()
    {
        var mirror = Bed(History(
                Grown(Mirror, 0, 1, BesideKrakka),
                Grown(Mirror, 1, 1, BesideKrakka),
                Grown(Mirror, 2, 1, null)),
            Mirror);

        Assert.Empty(mirror.Splits);
        Assert.Equal([new PartnerCount(Partner.Beside(Krakka), 2)], mirror.Partners);
    }

    [Fact]
    public void AHarvestWhoseSowingHasNoPatchRecordReadsPartnerNotRecorded()
    {
        // Two partners split the crop; the sowing from before v0.4.1 (no patch at sow) and the harvest
        // with no sowing on record at all both land in the not-recorded row, never guessed.
        // The not-recorded row is the most planted and met first, and still comes last.
        var mirror = Bed(History(
                Grown(Mirror, 2, 1, null, 1, SunLemonSeeds),
                Grown(Mirror, 3, 1, null, 0, null),
                Grown(Mirror, 0, 1, BesideKrakka),
                Grown(Mirror, 1, 1, BesideFig),
                [Harvest(T0.AddDays(1), Mirror, 2, 5, 0)]),
            Mirror);

        var notRecorded = mirror.Splits.Single(s => s.Partner == Partner.NotRecorded);
        Assert.Equal((2, 2, 1), (notRecorded.Planted, notRecorded.Harvested, notRecorded.Unmatched));
        Assert.Equal(new CrossRate(1, 3), notRecorded.Cross);
        Assert.Equal(Partner.NotRecorded, mirror.Splits[^1].Partner);
    }

    [Fact]
    public void AGrowingSowingBesideASecondPartnerDoesNotSplitTheCrop()
    {
        // The split waits for harvests from both partners: until the fig-side Mirror Apple is
        // harvested there is nothing to compare.
        var mirror = Bed(History(
                Grown(Mirror, 0, 1, BesideKrakka),
                [Plant(T0, Mirror, 1, 1, BesideFig)]),
            Mirror);

        Assert.Empty(mirror.Splits);
        Assert.Equal(
            [new PartnerCount(Partner.Beside(Krakka), 1), new PartnerCount(Partner.Beside(Fig), 1)],
            mirror.Partners);
    }

    [Theory]
    // Slot 1 (top middle): right is slot 2, then left is slot 0.
    [InlineData(1, new[] { 2, Krakka, 0, Fig }, Krakka)]
    [InlineData(1, new[] { 0, Fig }, Fig)]
    // Slot 0 (top-left corner): right is slot 1, down is slot 7 (the left side's middle).
    [InlineData(0, new[] { 7, Fig, 1, Krakka }, Krakka)]
    [InlineData(0, new[] { 7, Fig }, Fig)]
    // Slot 6 (bottom-left corner): right is slot 5, then up is slot 7 (the live 11-07 fig sowing's shape).
    [InlineData(6, new[] { 7, Krakka, 5, Fig }, Fig)]
    [InlineData(6, new[] { 7, Krakka }, Krakka)]
    // Slot 3 (right side's middle): down is slot 4 before up, slot 2.
    [InlineData(3, new[] { 2, Fig, 4, Krakka }, Krakka)]
    // Slot 7 (left side's middle): down slot 6, up slot 0; slot 1 across the top is no neighbour.
    [InlineData(7, new[] { 1, Krakka, 0, Fig }, Fig)]
    public void TheWalkTakesTheFirstPartnerRightDownUpLeft(int slot, int[] beds, int partner)
    {
        var patch = Patch([.. beds.Chunk(2).Select(b => (b[0], (ushort)b[1], (byte)2)), (slot, Mirror, 1)]);
        var mirror = Bed(History([Plant(T0, Mirror, 0, slot, patch)]), Mirror);

        Assert.Equal(Partner.Beside((ushort)partner), Assert.Single(mirror.Partners).Partner);
    }

    [Fact]
    public void TheWalkSkipsEmptyBedsTheSameCropDeadPlantsAndPairsThatCannotCross()
    {
        Assert.Empty(T.CrossResults(T.SeedIdBySpeciesIndex(Mirror)!.Value, T.SeedIdBySpeciesIndex(SunLemon)!.Value));

        // Slot 0's two neighbours, each in turn not a partner; the bed beyond the walk is a fig.
        SyncPatchBed[][] none =
        [
            Patch((0, Mirror, 1)),                                    // both empty
            Patch((0, Mirror, 1), (1, Mirror, 4), (7, Mirror, 2)),    // the same crop
            Patch((0, Mirror, 1), (1, Krakka, 5), (7, Krakka, 5)),    // dead (stage 5): withered plants never cross
            Patch((0, Mirror, 1), (1, SunLemon, 4), (7, SunLemon, 4)), // no pair in the table
            Patch((0, Mirror, 1), (2, Fig, 4), (4, Fig, 4)),          // figs, but not beside slot 0
        ];
        foreach (var patch in none)
        {
            var mirror = Bed(History([Plant(T0, Mirror, 0, 0, patch)]), Mirror);
            Assert.Equal([new PartnerCount(Partner.NoPartner, 1)], mirror.Partners);
        }

        // A seedling counts: neighbours need only be there at the sow.
        var seedling = Bed(History([Plant(T0, Mirror, 0, 0, Patch((0, Mirror, 1), (1, Krakka, 0)))]), Mirror);
        Assert.Equal([new PartnerCount(Partner.Beside(Krakka), 1)], seedling.Partners);
    }

    [Fact]
    public void NoPartnerIsNotASecondPartner()
    {
        // Harvests beside Krakka Root and with nothing to cross with: one partner, so one row.
        var mirror = Bed(History(
                Grown(Mirror, 0, 1, BesideKrakka),
                Grown(Mirror, 1, 1, Patch((1, Mirror, 1)), 0, null)),
            Mirror);

        Assert.Empty(mirror.Splits);
        Assert.Equal(
            [new PartnerCount(Partner.Beside(Krakka), 1), new PartnerCount(Partner.NoPartner, 1)],
            mirror.Partners);
    }

    [Fact]
    public void NoPartnerGetsItsOwnRowOnceTheCropSplits()
    {
        // "no partner" is the most planted and met first, and still comes after the partners; the
        // partners run most planted first.
        var mirror = Bed(History(
                Grown(Mirror, 2, 1, Patch((1, Mirror, 1)), 0, null),
                Grown(Mirror, 3, 1, Patch((1, Mirror, 1)), 0, null),
                Grown(Mirror, 4, 1, Patch((1, Mirror, 1)), 0, null),
                Grown(Mirror, 0, 1, BesideKrakka),
                Grown(Mirror, 1, 1, BesideFig),
                Grown(Mirror, 5, 1, BesideFig)),
            Mirror);

        Assert.Equal(
            [Partner.Beside(Fig), Partner.Beside(Krakka), Partner.NoPartner],
            mirror.Splits.Select(s => s.Partner!.Value));
        Assert.Empty(mirror.Partners);
    }

    [Fact] // ruled 2026-11-11 over ruling 10: "Off the table. We only really use Thav grade 3 or potting soil"
    public void SoilSplitsNothing()
    {
        // Harvests from two soils and one with none recorded: still one Krakka Root row, and no hover.
        var krakka = Bed(History(
                Grown(Krakka, 0, 1, null, soil: Thanalan3),
                Grown(Krakka, 1, 1, null, soil: Shroud3),
                Grown(Krakka, 2, 1, null)),
            Krakka);

        Assert.Equal((3, 3), (krakka.Planted, krakka.Harvested));
        Assert.Empty(krakka.Splits);
        Assert.Empty(krakka.Partners);
    }

    [Fact]
    public void PotsNeverSplitAndListNoPartner()
    {
        var book = GardenBoard.Yields(History(
                [Plant(T0, Onion, 129, 0, soil: PottingSoil, pot: true), Harvest(T0.AddDays(4), Onion, 129, 0, pot: true)],
                [Plant(T0, Onion, 130, 0, soil: Thanalan3, pot: true), Harvest(T0.AddDays(4), Onion, 130, 0, pot: true)]),
            [], T);

        var onion = Assert.Single(book.Pots);
        Assert.Empty(onion.Splits);
        Assert.Empty(onion.Partners);
    }

    [Fact]
    public void SplitRowsAddUpToTheCropRow()
    {
        var mirror = Bed(History(
                Grown(Mirror, 0, 1, BesideKrakka),
                Grown(Mirror, 1, 1, BesideFig),
                [Plant(T0, Mirror, 2, 1, BesideFig)],
                [Plant(T0, Mirror, 3, 1, null), Plant(T0.AddDays(1), Mirror, 3, 1, null)],
                [Harvest(T0.AddDays(1), Mirror, 4, 5, 1, SunLemonSeeds)]),
            Mirror,
            Seed.Sight(new ClaimedBed { Estate = Yard, MapKey = 102, PatchOrdinal = 2, BedSlot = 1 }, T0.AddDays(1), Mirror, 1));

        Assert.Equal((5, 2, 1, 2, 1), (mirror.Planted, mirror.Harvested, mirror.Growing, mirror.Lost, mirror.Unmatched));
        Assert.Equal(
            (mirror.Planted, mirror.Harvested, mirror.Growing, mirror.Lost, mirror.Unmatched, mirror.Gathered),
            (mirror.Splits.Sum(s => s.Planted), mirror.Splits.Sum(s => s.Harvested), mirror.Splits.Sum(s => s.Growing),
                mirror.Splits.Sum(s => s.Lost), mirror.Splits.Sum(s => s.Unmatched), mirror.Splits.Sum(s => s.Gathered)));

        // Each row's own counts: the fig-side sowing still in the ground grows on the fig row.
        Assert.Equal(
            [
                (Partner.Beside(Fig), 2, 1, 1, 0, 0),
                (Partner.Beside(Krakka), 1, 1, 0, 0, 0),
                (Partner.NotRecorded, 2, 0, 0, 2, 1),
            ],
            mirror.Splits.Select(s => (s.Partner!.Value, s.Planted, s.Harvested, s.Growing, s.Lost, s.Unmatched)));
    }
}
