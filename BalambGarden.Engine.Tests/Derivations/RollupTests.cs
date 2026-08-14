using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class RollupTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private static readonly EstateKey Chelsea = new(340, 11, 32);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T18:00:00Z");

    private static ClaimedBed Bed(int slot, byte stage, double tendedHoursAgo)
    {
        var bed = new ClaimedBed
        {
            Estate = Chelsea, MapKey = 110, PatchOrdinal = 0, BedSlot = slot,
            LastTended = Now.AddHours(-tendedHoursAgo),
        };
        // Krakka Root (0x31): 24h wilt tier - drives the wilt states below
        bed.Observe(new Observation(Now.AddHours(-tendedHoursAgo), 0x31, stage,
            ObservationSource.TendReceipt));
        return bed;
    }

    [Fact]
    public void RollupCountsStates()
    {
        var beds = new List<ClaimedBed>
        {
            Bed(0, stage: 4, tendedHoursAgo: 1),    // ripe, watered
            Bed(1, stage: 2, tendedHoursAgo: 1),    // watered
            Bed(2, stage: 2, tendedHoursAgo: 20),   // due (>= 18h)
            Bed(3, stage: 2, tendedHoursAgo: 30),   // overdue (>= 24h)
        };

        var rollup = Assert.Single(Rollups.ForEstate(Chelsea, beds, T, new ClockWiltSource(), Now));
        Assert.Equal(4, rollup.Claimed);
        Assert.Equal(1, rollup.Ripe);
        Assert.Equal(1, rollup.Due);
        Assert.Equal(1, rollup.Overdue);
        Assert.NotNull(rollup.NextRipe);   // the stage-2 beds project a window
    }

    [Fact]
    public void NudgeSpeaksWhenAttentionNeeded()
    {
        var rollups = Rollups.ForEstate(Chelsea,
            [Bed(0, 4, 1), Bed(2, 2, 20)], T, new ClockWiltSource(), Now);
        var line = Rollups.ArrivalNudge(Chelsea, rollups);
        Assert.NotNull(line);
        Assert.Contains("1 ripe", line);
        Assert.Contains("1", line);   // one thirsty
    }

    [Fact] // all watered, nothing ripe: silence over filler
    public void NudgeSilentWhenAllQuiet()
    {
        var rollups = Rollups.ForEstate(Chelsea, [Bed(1, 2, 1)], T, new ClockWiltSource(), Now);
        Assert.Null(Rollups.ArrivalNudge(Chelsea, rollups));
    }
}
