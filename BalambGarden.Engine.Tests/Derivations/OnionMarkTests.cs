using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The onion mark (rethink slice 12, spec ruling 14): which History rows and which Yields
/// rows wear it. The colour itself is the window's; these are the rule.</summary>
public class OnionMarkTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-04T23:14:02Z");

    // Species indexes from Data/SpeciesIndex.json; both cross with the onion (Data/CrossbreedPairs.json).
    private const ushort Mirror = 17, SunLemon = 14, Krakka = 49;

    private static readonly EstateKey Yard = new(340, 11, 32);

    private static int seq;

    private static HistoryEntry Row(HistoryKind kind, ushort species, bool pot = true, string by = "jw9cdj", int? qty = null,
        int patch = 2, int slot = 6, SyncPatchBed[]? beside = null, DateTimeOffset? at = null)
        => new($"{by}:eraaaa:{++seq}", at ?? T0, kind, species, Yard, pot, patch, pot ? 0 : slot,
            by, SyncSource.Chain, qty is { } q ? [new SyncYield(8166, q)] : null, Patch: beside);

    private static GardenHistory History(params HistoryEntry[] rows)
    {
        var history = new GardenHistory();
        foreach (var row in rows)
            history.Record(row);
        return history;
    }

    /// <summary>A patch at sow: slot 1 holds the onion, slot 2 (the walk's first look) <paramref name="partner"/>.</summary>
    private static SyncPatchBed[] Beside(ushort partner)
        => Enumerable.Range(0, GardenBoard.PatchSlots)
            .Select(slot => slot switch
            {
                1 => new SyncPatchBed(slot, true, GardenBoard.GoalCrop, 1),
                2 => new SyncPatchBed(slot, true, partner, 4),
                _ => new SyncPatchBed(slot, false, 0, 0),
            })
            .ToArray();

    [Fact] // the goal crop is Thavnairian Onion by the crop table, not another onion
    public void TheGoalCropIsThavnairianOnion()
    {
        Assert.Equal("Thavnairian Onion", T.SpeciesName(GardenBoard.GoalCrop));
        Assert.True(GardenBoard.IsGoalCrop(T.SpeciesIndexByName("Thavnairian Onion")!.Value));
        Assert.False(GardenBoard.IsGoalCrop(T.SpeciesIndexByName("Onion Prince")!.Value));
        Assert.False(GardenBoard.IsGoalCrop(T.SpeciesIndexByName("Wild Onion Set")!.Value));
        Assert.False(GardenBoard.IsGoalCrop(T.SpeciesIndexByName("Krakka Root")!.Value));
    }

    [Fact] // a harvest of the goal crop is marked, from a pot or a bed, by any hand, its yield heard or not
    public void EveryOnionHarvestIsMarked()
    {
        Assert.True(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Harvest, GardenBoard.GoalCrop, qty: 1)));
        Assert.True(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Harvest, GardenBoard.GoalCrop)));
        Assert.True(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Harvest, GardenBoard.GoalCrop, pot: false)));
        Assert.True(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Harvest, GardenBoard.GoalCrop, by: Hand.OwnSlug, qty: 1)));
    }

    [Fact] // an onion sowing and another crop's harvest take no mark
    public void ASowingOrAnotherCropIsNotMarked()
    {
        Assert.False(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Plant, GardenBoard.GoalCrop)));
        Assert.False(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Harvest, T.SpeciesIndexByName("Onion Prince")!.Value, qty: 1)));
        Assert.False(GardenBoard.IsGoalCropHarvest(Row(HistoryKind.Harvest, Krakka, qty: 3)));
    }

    [Fact] // the Yields tab's onion rows carry the mark once they have gathered something, beds and pots alike; no other crop's row does
    public void TheYieldsOnionRowsAreMarkedOnceTheyGatherAndNoOthers()
    {
        var book = GardenBoard.Yields(History(
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, patch: 7),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, patch: 7, qty: 1, at: T0.AddDays(10)),
            Row(HistoryKind.Plant, Krakka, patch: 8),
            Row(HistoryKind.Harvest, Krakka, patch: 8, qty: 2, at: T0.AddDays(10)),
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1, qty: 1, at: T0.AddDays(10)),
            Row(HistoryKind.Plant, Krakka, pot: false, patch: 0, slot: 3)), [], T);

        Assert.True(book.Pots.Single(r => r.Species == GardenBoard.GoalCrop).OnionMark);
        Assert.False(book.Pots.Single(r => r.Species == Krakka).OnionMark);
        Assert.True(book.Beds.Single(r => r.Species == GardenBoard.GoalCrop).OnionMark);
        Assert.False(book.Beds.Single(r => r.Species == Krakka).OnionMark);
    }

    [Fact] // an onion row with nothing gathered yet (only sowings, or no yield heard) takes no mark
    public void AnOnionRowWithNothingGatheredIsNotMarked()
    {
        var book = GardenBoard.Yields(History(
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, patch: 7),
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1, at: T0.AddDays(10))), [], T);

        Assert.False(book.Pots.Single().OnionMark);
        Assert.False(book.Beds.Single().OnionMark);
    }

    [Fact] // an onion bed row split by partner keeps the mark on the crop row, not on its dim split rows
    public void AnOnionRowsSplitRowsAreNotMarked()
    {
        var onion = GardenBoard.Yields(History(
                Row(HistoryKind.Plant, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1, beside: Beside(Mirror)),
                Row(HistoryKind.Harvest, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1, qty: 1, at: T0.AddDays(10)),
                Row(HistoryKind.Plant, GardenBoard.GoalCrop, pot: false, patch: 1, slot: 1, beside: Beside(SunLemon)),
                Row(HistoryKind.Harvest, GardenBoard.GoalCrop, pot: false, patch: 1, slot: 1, qty: 1, at: T0.AddDays(10))), [], T)
            .Beds.Single(r => r.Species == GardenBoard.GoalCrop);

        Assert.True(onion.OnionMark);
        Assert.Equal(2, onion.Splits.Count);
        Assert.All(onion.Splits, split => Assert.False(split.OnionMark));
    }

    [Fact] // the trophy's onions are every onion gathered, beds and pots, sowing on record or not; another crop's never count
    public void TheTallyCountsEveryOnionGathered()
    {
        var book = GardenBoard.Yields(History(
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, patch: 7),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, patch: 7, qty: 2, at: T0.AddDays(10)),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, patch: 9, qty: 1, at: T0.AddDays(10)),  // no sowing on record
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, patch: 11, at: T0.AddDays(10)),         // yield not heard
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1, qty: 4, at: T0.AddDays(10)),
            Row(HistoryKind.Plant, Krakka, patch: 8),
            Row(HistoryKind.Harvest, Krakka, patch: 8, qty: 9, at: T0.AddDays(10))), [], T);

        Assert.Equal(7, book.Trophy.Onions);
    }

    [Fact] // the trophy's seeds are the onion seeds every cross gave, by quantity; another seed never counts
    public void TheTallyCountsOnionSeedsByQuantity()
    {
        var onionSeed = T.SeedIdBySpeciesIndex(GardenBoard.GoalCrop)!.Value;
        var otherSeed = T.SeedIdBySpeciesIndex(Krakka)!.Value;
        var book = GardenBoard.Yields(History(
            Cross(onionSeed, 2), Cross(onionSeed, 1), Cross(otherSeed, 5), Cross(null, 0)), [], T);

        Assert.Equal(new OnionTally(0, 3), book.Trophy);
    }

    /// <summary>A Royal Kukuru bed harvest (no sowing on record) whose cross gave <paramref name="qty"/> of <paramref name="seed"/>.</summary>
    private static HistoryEntry Cross(uint? seed, int qty)
        => Row(HistoryKind.Harvest, T.SpeciesIndexByName("Royal Kukuru")!.Value, pot: false, patch: ++seq % 8, slot: 1, qty: 5)
            with { SeedItem = seed, SeedQty = qty };

    [Fact] // nothing gathered and no seed: an empty tally
    public void ATallyWithNoOnionsIsEmpty()
    {
        var book = GardenBoard.Yields(History(Row(HistoryKind.Plant, GardenBoard.GoalCrop, patch: 7)), [], T);
        Assert.Equal(new OnionTally(0, 0), book.Trophy);
    }
}
