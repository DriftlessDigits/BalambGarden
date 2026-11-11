using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The gold onion mark (rethink slice 12, spec ruling 14): which History rows and which
/// Yields rows wear it. The colour itself is the window's; these are the rule.</summary>
public class OnionMarkTests
{
    private static readonly DomainTables T = DomainTables.Load();

    private static readonly EstateKey Yard = new(340, 11, 32);

    private static HistoryEntry Row(HistoryKind kind, ushort species, bool pot = true, string by = "jw9cdj", int? qty = null)
        => new($"{by}:eraaaa:1", DateTimeOffset.Parse("2026-11-04T23:14:02Z"), kind, species, Yard, pot, 2, pot ? 0 : 6,
            by, SyncSource.Chain, qty is { } q ? [new SyncYield(8166, q)] : null);

    [Fact] // the goal crop is Thavnairian Onion by the crop table, not another onion
    public void TheGoalCropIsThavnairianOnion()
    {
        Assert.Equal("Thavnairian Onion", T.SpeciesName(GardenBoard.Onion));
        Assert.True(GardenBoard.IsOnion(T.SpeciesIndexByName("Thavnairian Onion")!.Value));
        Assert.False(GardenBoard.IsOnion(T.SpeciesIndexByName("Onion Prince")!.Value));
        Assert.False(GardenBoard.IsOnion(T.SpeciesIndexByName("Wild Onion Set")!.Value));
        Assert.False(GardenBoard.IsOnion(T.SpeciesIndexByName("Krakka Root")!.Value));
    }

    [Fact] // a harvest of the goal crop is marked, from a pot or a bed, by any hand, its yield heard or not
    public void EveryOnionHarvestIsMarked()
    {
        Assert.True(GardenBoard.IsOnionHarvest(Row(HistoryKind.Harvest, GardenBoard.Onion, qty: 1)));
        Assert.True(GardenBoard.IsOnionHarvest(Row(HistoryKind.Harvest, GardenBoard.Onion)));
        Assert.True(GardenBoard.IsOnionHarvest(Row(HistoryKind.Harvest, GardenBoard.Onion, pot: false)));
        Assert.True(GardenBoard.IsOnionHarvest(Row(HistoryKind.Harvest, GardenBoard.Onion, by: Hand.OwnSlug, qty: 1)));
    }

    [Fact] // an onion sowing and another crop's harvest take no mark
    public void ASowingOrAnotherCropIsNotMarked()
    {
        Assert.False(GardenBoard.IsOnionHarvest(Row(HistoryKind.Plant, GardenBoard.Onion)));
        Assert.False(GardenBoard.IsOnionHarvest(Row(HistoryKind.Harvest, T.SpeciesIndexByName("Onion Prince")!.Value, qty: 1)));
        Assert.False(GardenBoard.IsOnionHarvest(Row(HistoryKind.Harvest, T.SpeciesIndexByName("Krakka Root")!.Value, qty: 3)));
    }
}
