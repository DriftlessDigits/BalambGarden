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

    [Fact] // the Yields tab's onion rows carry the mark, beds and pots alike; no other crop's row does
    public void TheYieldsOnionRowsAreMarkedAndNoOthers()
    {
        var book = GardenBoard.Yields(History(
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, patch: 7),
            Row(HistoryKind.Harvest, GardenBoard.GoalCrop, patch: 7, qty: 1, at: T0.AddDays(10)),
            Row(HistoryKind.Plant, Krakka, patch: 8),
            Row(HistoryKind.Plant, GardenBoard.GoalCrop, pot: false, patch: 0, slot: 1),
            Row(HistoryKind.Plant, Krakka, pot: false, patch: 0, slot: 3)), [], T);

        Assert.True(book.Pots.Single(r => r.Species == GardenBoard.GoalCrop).GoalCrop);
        Assert.False(book.Pots.Single(r => r.Species == Krakka).GoalCrop);
        Assert.True(book.Beds.Single(r => r.Species == GardenBoard.GoalCrop).GoalCrop);
        Assert.False(book.Beds.Single(r => r.Species == Krakka).GoalCrop);
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

        Assert.True(onion.GoalCrop);
        Assert.Equal(2, onion.Splits.Count);
        Assert.All(onion.Splits, split => Assert.False(split.GoalCrop));
    }
}
