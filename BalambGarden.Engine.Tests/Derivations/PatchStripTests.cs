using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class PatchStripTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    private static ClaimedBed Bed(int slot, byte stage, double tendedHoursAgo, bool isPot = false)
    {
        var bed = new ClaimedBed
        {
            Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = slot, IsPot = isPot,
            LastTended = Now.AddHours(-tendedHoursAgo),
        };
        // Krakka Root (0x31): 24h wilt tier - same fixture the rollup tests use.
        bed.Observe(new Observation(Now.AddHours(-tendedHoursAgo), 0x31, stage,
            ObservationSource.TendReceipt));
        return bed;
    }

    [Fact]
    public void StripIsAlwaysEightCellsInSlotOrder()
    {
        var cells = PatchStrip.ForPatch([Bed(3, 2, 1)], T, new ClockWiltSource(), Now);

        Assert.Equal(PatchStrip.Slots, cells.Count);
        Assert.Equal(Enumerable.Range(0, 8), cells.Select(c => c.Slot));
        Assert.Equal(CellFill.Growing, cells[3].Fill);
    }

    [Fact] // a slot the ledger has nothing for is a hole, and holes make no water claim
    public void UnclaimedSlotsAreHolesNotEmptyBeds()
    {
        var cells = PatchStrip.ForPatch([Bed(0, 2, 1)], T, new ClockWiltSource(), Now);

        Assert.Equal(CellFill.Unclaimed, cells[7].Fill);
        Assert.Equal(WaterState.NotApplicable, cells[7].Water);
        Assert.Equal(0, cells[7].Stage);
    }

    [Fact]
    public void StageFourIsRipeAndLowerStagesCarryTheirStage()
    {
        var cells = PatchStrip.ForPatch(
            [Bed(0, 4, 1), Bed(1, 1, 1)], T, new ClockWiltSource(), Now);

        Assert.Equal(CellFill.Ripe, cells[0].Fill);
        Assert.Equal(CellFill.Growing, cells[1].Fill);
        Assert.Equal(1, cells[1].Stage);
    }

    [Fact] // the strip counts the same census the rollup does - never a second opinion
    public void WaterMatchesTheWiltSource()
    {
        var cells = PatchStrip.ForPatch(
            [Bed(0, 2, 1), Bed(1, 2, 20), Bed(2, 2, 30)], T, new ClockWiltSource(), Now);

        Assert.Equal(WaterState.Watered, cells[0].Water);
        Assert.Equal(WaterState.Due, cells[1].Water);
        Assert.Equal(WaterState.Overdue, cells[2].Water);
    }

    [Fact] // flowerpots cannot wilt, so a pot cell never shows an attention bar
    public void PotsNeverCarryAWaterClaim()
    {
        var cells = PatchStrip.ForPatch(
            [Bed(0, 2, 400, isPot: true)], T, new ClockWiltSource(), Now);

        Assert.Equal(WaterState.NotApplicable, cells[0].Water);
        Assert.Equal(CellFill.Growing, cells[0].Fill);
    }

    private static ClaimedBed Pot(int key, byte stage)
    {
        var pot = new ClaimedBed
        {
            Estate = Chelsea, MapKey = key, PatchOrdinal = 0, BedSlot = 0, IsPot = true,
        };
        pot.Observe(new Observation(Now.AddHours(-1), 0x31, stage,
            ObservationSource.TendReceipt));
        return pot;
    }

    [Fact] // pots have no eight-slot shape: one cell per pot, in map-key order
    public void PotStripIsOneCellPerPotInKeyOrder()
    {
        var cells = PatchStrip.ForPots([Pot(181, 2), Pot(180, 4)], new HashSet<int>());

        Assert.Equal(2, cells.Count);
        Assert.Equal([180, 181], cells.Select(c => c.Slot));
        Assert.Equal(CellFill.Ripe, cells[0].Fill);
        Assert.Equal(CellFill.Growing, cells[1].Fill);
        Assert.Equal(2, cells[1].Stage);
    }

    [Fact] // pot wilt is OBSERVED (b4=1 on the live map), never predicted - the only
           // water claim a pot cell can carry is the game's own
    public void PotStripMarksObservedWiltOnly()
    {
        var cells = PatchStrip.ForPots(
            [Pot(180, 2), Pot(181, 2)], new HashSet<int> { 180 });

        Assert.Equal(WaterState.Danger, cells[0].Water);
        Assert.Equal(WaterState.NotApplicable, cells[1].Water);
    }

    [Fact] // a recorded pot with no sighting yet is Unknown, same silence as beds
    public void PotStripReadsUnknownWithNoSighting()
    {
        var bare = new ClaimedBed
        {
            Estate = Chelsea, MapKey = 5, PatchOrdinal = 0, BedSlot = 0, IsPot = true,
        };

        var cells = PatchStrip.ForPots([bare], new HashSet<int>());

        Assert.Equal(CellFill.Unknown, cells[0].Fill);
        Assert.Equal(WaterState.NotApplicable, cells[0].Water);
    }

    [Fact] // claimed but unidentified is a different silence from unclaimed
    public void ClaimedWithNoSightingReadsUnknown()
    {
        var bare = new ClaimedBed
        {
            Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = 0,
        };

        var cells = PatchStrip.ForPatch([bare], T, new ClockWiltSource(), Now);

        Assert.Equal(CellFill.Unknown, cells[0].Fill);
        Assert.Equal(WaterState.Unknown, cells[0].Water);
    }
}
