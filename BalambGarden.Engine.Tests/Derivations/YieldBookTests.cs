using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The yield book through the garden board's seam (rethink slice 7): a history and the
/// ledger go in, the Yields tab's rows come out. Nothing here reaches inside the board.</summary>
public class YieldBookTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-16T21:30:00Z");

    // Species indexes from Data/SpeciesIndex.json; seed item ids from Data/Crops.json.
    private const ushort Mirror = 17, Krakka = 49, Onion = 50, Fig = 65;
    private const uint SunLemonSeeds = 7728, CurielSeeds = 8169, KukuruSeeds = 7750;

    private static readonly EstateKey Yard = new(340, 11, 32);

    private static int seq;

    private static HistoryEntry Plant(DateTimeOffset at, ushort species, int patch, int slot, bool pot = false)
        => new($"hhbbaa:eraaaa:{++seq}", at, HistoryKind.Plant, species, Yard, pot, patch, pot ? 0 : slot,
            "hhbbaa", SyncSource.Chain, null);

    private static HistoryEntry Harvest(DateTimeOffset at, ushort species, int patch, int slot, bool pot = false,
        int? qty = null, int? seedQty = null, uint? seedItem = null)
        => new($"hhbbaa:eraaaa:{++seq}", at, HistoryKind.Harvest, species, Yard, pot, patch, pot ? 0 : slot,
            "hhbbaa", SyncSource.Chain, qty is { } q ? [new SyncYield(1, q)] : null, seedQty, seedItem);

    private static GardenHistory History(params HistoryEntry[] rows)
    {
        var history = new GardenHistory();
        foreach (var row in rows)
            history.Record(row);
        return history;
    }

    private static ClaimedBed Bed(int patch, int slot) => new()
    {
        Estate = Yard, MapKey = 100 + patch, PatchOrdinal = patch, BedSlot = slot,
    };

    private static ClaimedBed Pot(int key) => new()
    {
        Estate = Yard, MapKey = key, PatchOrdinal = key, BedSlot = 0, IsPot = true,
    };

    private static YieldBook Book(GardenHistory history, params ClaimedBed[] ledger)
        => GardenBoard.Yields(history, ledger, T);

    [Fact]
    public void BedsAndPotsComeOutAsSeparateRowsOfTheirOwnSections()
    {
        // Krakka sown and harvested in a bed and in a pot: one row in each section, never merged.
        var book = Book(History(
            Plant(T0, Krakka, 0, 0),
            Plant(T0, Krakka, 126, 0, pot: true),
            Harvest(T0.AddDays(3), Krakka, 0, 0),
            Harvest(T0.AddDays(3), Krakka, 126, 0, pot: true)));

        var bed = Assert.Single(book.Beds);
        var pot = Assert.Single(book.Pots);
        Assert.Equal((Krakka, 1, 1), (bed.Species, bed.Planted, bed.Harvested));
        Assert.Equal((Krakka, 1, 1), (pot.Species, pot.Planted, pot.Harvested));
    }

    [Fact]
    public void AHarvestPairsWithItsSowingAndOneWithNoSowingOnRecordIsNeverHarvested()
    {
        // Bed 1 was sown and harvested on record. Bed 2's harvest has no sowing on record (its
        // plant went in before the history began): it never counts as harvested, so harvested
        // never tops planted.
        var book = Book(History(
            Plant(T0, Fig, 0, 0),
            Harvest(T0.AddDays(5), Fig, 0, 0),
            Harvest(T0.AddDays(1), Fig, 0, 1)));

        var fig = Assert.Single(book.Beds);
        Assert.Equal((1, 1, 0, 0), (fig.Planted, fig.Harvested, fig.Growing, fig.Lost));
        Assert.Equal(1, fig.Unmatched);
    }

    [Fact]
    public void AHarvestNeverPairsAcrossContainers()
    {
        // A pot sown under one key and harvested under another (its key moved): no guess joins
        // them. The sowing has no record still holding it, so it reads lost.
        var book = Book(History(
            Plant(T0, Krakka, 227, 0, pot: true),
            Harvest(T0.AddDays(4), Krakka, 131, 0, pot: true)));

        var row = Assert.Single(book.Pots);
        Assert.Equal((1, 0, 0, 1), (row.Planted, row.Harvested, row.Growing, row.Lost));
        Assert.Equal(1, row.Unmatched);
    }

    [Fact]
    public void AHarvestOfAnotherCropDoesNotCloseTheSowing()
    {
        var book = Book(History(
                Plant(T0, Fig, 0, 0),
                Harvest(T0.AddDays(5), Mirror, 0, 0)),
            Seed.Sight(Bed(0, 0), T0.AddDays(6), Fig, 2));

        var fig = book.Beds.Single(r => r.Species == Fig);
        var mirror = book.Beds.Single(r => r.Species == Mirror);
        Assert.Equal((1, 0, 1, 0), (fig.Planted, fig.Harvested, fig.Growing, fig.Lost));
        Assert.Equal((0, 0, 1), (mirror.Planted, mirror.Harvested, mirror.Unmatched));
    }

    [Fact]
    public void ANewerSowingEndsTheOlderOneUnharvested()
    {
        // Sown twice with no harvest between: the first plant is gone (lost); the second is the
        // one the record still holds.
        var book = Book(History(
                Plant(T0, Fig, 0, 0),
                Plant(T0.AddDays(2), Fig, 0, 0)),
            Seed.Sight(Bed(0, 0), T0.AddDays(3), Fig, 1));

        var fig = Assert.Single(book.Beds);
        Assert.Equal((2, 0, 1, 1), (fig.Planted, fig.Harvested, fig.Growing, fig.Lost));
    }

    [Fact]
    public void ABedResownWithAnotherCropGrowsTheNewCrop()
    {
        var book = Book(History(
                Plant(T0, Fig, 0, 0),
                Plant(T0.AddDays(2), Mirror, 0, 0)),
            Seed.Sight(Bed(0, 0), T0.AddDays(3), Mirror, 1));

        var fig = book.Beds.Single(r => r.Species == Fig);
        var mirror = book.Beds.Single(r => r.Species == Mirror);
        Assert.Equal((1, 0, 1), (fig.Planted, fig.Growing, fig.Lost));
        Assert.Equal((1, 1, 0), (mirror.Planted, mirror.Growing, mirror.Lost));
    }

    [Fact]
    public void GrowingCountsOnlyThePlantingsTheLedgerStillHoldsAlive()
    {
        // Six onion pots sown, none harvested. Growing: the pot read growing, and the pot only just
        // sown (the sowing is its newest contents). Lost: the pot read dead, the pot emptied, the pot read
        // holding another crop, and the pot with no record at all.
        var sown = T0.AddDays(20);
        var book = Book(History(
                Plant(sown, Onion, 1, 0, pot: true),
                Plant(sown, Onion, 2, 0, pot: true),
                Plant(sown, Onion, 3, 0, pot: true),
                Plant(sown, Onion, 4, 0, pot: true),
                Plant(sown, Onion, 5, 0, pot: true),
                Plant(sown, Onion, 6, 0, pot: true)),
            Seed.Sight(Pot(1), sown.AddHours(5), Onion, 2),
            Seed.Plant(Pot(2), sown, Onion),
            Seed.Sight(Pot(3), sown.AddHours(30), Onion, GardenStage.Dead),
            Pot(4),
            Seed.Sight(Pot(5), sown.AddHours(5), Krakka, 1));

        var onion = Assert.Single(book.Pots);
        Assert.Equal((6, 0, 2, 4), (onion.Planted, onion.Harvested, onion.Growing, onion.Lost));
    }

    [Fact]
    public void AHarvestWithNoSowingOnRecordStillCountsItsItemsAndCrossSeed()
    {
        // Plan pick A (2026-11-11, the words await the maintainer's look): the items and the seed really came
        // out of the ground, so they count; the harvest still is not "harvested".
        var book = Book(History(
            Plant(T0, Krakka, 0, 0),
            Harvest(T0.AddDays(3), Krakka, 0, 0, qty: 9, seedQty: 1, seedItem: SunLemonSeeds),
            Harvest(T0.AddDays(1), Krakka, 0, 1, qty: 9, seedQty: 0)));

        var krakka = Assert.Single(book.Beds);
        Assert.Equal((1, 1), (krakka.Harvested, krakka.Unmatched));
        Assert.Equal((18, 2), (krakka.Gathered, krakka.GatheredFrom));
        Assert.Equal(new CrossRate(1, 2), krakka.Cross);
    }

    [Fact]
    public void GatheredCountsOnlyTheHarvestsWithAYieldRecorded()
    {
        var book = Book(History(
            Plant(T0, Mirror, 0, 0),
            Plant(T0, Mirror, 0, 1),
            Plant(T0, Mirror, 0, 2),
            Harvest(T0.AddDays(5), Mirror, 0, 0, qty: 12),
            Harvest(T0.AddDays(5), Mirror, 0, 1, qty: 12),
            Harvest(T0.AddDays(5), Mirror, 0, 2)));

        var mirror = Assert.Single(book.Beds);
        Assert.Equal((3, 24, 2), (mirror.Harvested, mirror.Gathered, mirror.GatheredFrom));
    }

    /// <summary>Harvests of <paramref name="crop"/>, each paired with its own sowing, with the
    /// cross seed as given (qty null = not heard).</summary>
    private static HistoryEntry[] Harvests(ushort crop, params (int? Qty, uint? Item)[] seeds)
        => seeds.SelectMany((s, i) => new[]
        {
            Plant(T0, crop, i / 8, i % 8),
            Harvest(T0.AddDays(3), crop, i / 8, i % 8, qty: 9, seedQty: s.Qty, seedItem: s.Item),
        }).ToArray();

    private static (int?, uint?)[] Repeat(int n, int? qty, uint? item)
        => Enumerable.Repeat<(int?, uint?)>((qty, item), n).ToArray();

    [Fact]
    public void TheCrossRateIsTheCrossingHarvestsOverTheHeardOnesAndNotHeardIsNeverAMiss()
    {
        // 31 crossed, 3 missed, 5 not heard: "91%", "31 of 34".
        var book = Book(History(Harvests(Krakka,
            [.. Repeat(17, 1, SunLemonSeeds), .. Repeat(14, 1, CurielSeeds), .. Repeat(3, 0, null), .. Repeat(5, null, null)])));

        var cross = Assert.Single(book.Beds).Cross!;
        Assert.Equal((31, 34, 91), (cross.Crossed, cross.Heard, cross.Percent));
        Assert.True(cross.Firm);
    }

    [Theory]
    [InlineData(4, 3, 75, false)]   // ruling 8's example: "75%" from 3 of 4 does not read as solid
    [InlineData(9, 9, 100, false)]
    [InlineData(10, 9, 90, true)]   // the threshold starts at ten
    public void ARateFromFewerThanTenHeardHarvestsIsNotFirm(int heard, int crossed, int percent, bool firm)
    {
        var book = Book(History(Harvests(Krakka,
            [.. Repeat(crossed, 1, SunLemonSeeds), .. Repeat(heard - crossed, 0, null)])));

        var cross = Assert.Single(book.Beds).Cross!;
        Assert.Equal((percent, firm), (cross.Percent, cross.Firm));
    }

    [Fact]
    public void ABedCropWithNoCrossHeardHasNoRate()
    {
        var book = Book(History(Harvests(Krakka, [.. Repeat(2, null, null)])));

        Assert.Null(Assert.Single(book.Beds).Cross);
    }

    [Fact]
    public void EachCrossSeedTakesItsShareAndTheSharesAddToAHundred()
    {
        // Mirror Apple at the grill: Royal Kukuru 19, Sun Lemon 11, Curiel Root 10 of 40 crossing
        // harvests - 47.5, 27.5 and 25: the left-over point goes to the first of the tied halves.
        var book = Book(History(Harvests(Mirror,
            [.. Repeat(19, 1, KukuruSeeds), .. Repeat(11, 1, SunLemonSeeds), .. Repeat(10, 1, CurielSeeds), .. Repeat(6, 0, null)])));

        Assert.Equal(
            [new SeedShare(KukuruSeeds, 19, 48), new SeedShare(SunLemonSeeds, 11, 27), new SeedShare(CurielSeeds, 10, 25)],
            Assert.Single(book.Beds).Seeds);
    }

    [Fact]
    public void KrakkaRootsTwoSeedsSplitFiftyFiveFortyFive()
    {
        var book = Book(History(Harvests(Krakka, [.. Repeat(17, 1, SunLemonSeeds), .. Repeat(14, 1, CurielSeeds)])));

        Assert.Equal(
            [new SeedShare(SunLemonSeeds, 17, 55), new SeedShare(CurielSeeds, 14, 45)],
            Assert.Single(book.Beds).Seeds);
    }

    [Fact]
    public void PotsCarryNoCrossColumns()
    {
        var book = Book(History(
            Plant(T0, Onion, 129, 0, pot: true),
            Harvest(T0.AddDays(5), Onion, 129, 0, pot: true, qty: 1, seedQty: 1, seedItem: SunLemonSeeds)));

        var onion = Assert.Single(book.Pots);
        Assert.Null(onion.Cross);
        Assert.Empty(onion.Seeds);
        Assert.Equal(1, onion.Gathered);
    }

    [Fact]
    public void RowsRunMostPlantedFirst()
    {
        var book = Book(History(
            Plant(T0, Fig, 0, 0),
            Plant(T0, Mirror, 1, 0),
            Plant(T0, Mirror, 1, 1),
            Plant(T0, Krakka, 2, 0),
            Plant(T0, Krakka, 2, 1),
            Plant(T0, Krakka, 2, 2)));

        Assert.Equal([Krakka, Mirror, Fig], book.Beds.Select(r => r.Species));
    }

    [Fact]
    public void TheBookRunsSinceTheFirstEntryOnRecord()
    {
        var book = Book(History(
            Plant(T0.AddDays(2), Fig, 0, 0),
            Harvest(T0, Fig, 0, 1)));

        Assert.Equal(T0, book.Since);
    }

    [Fact]
    public void AnEmptyHistoryHasNoStartAndNoRows()
    {
        var book = Book(new GardenHistory());

        Assert.Null(book.Since);
        Assert.Empty(book.Beds);
        Assert.Empty(book.Pots);
    }
}
