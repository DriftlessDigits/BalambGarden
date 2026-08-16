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

    [Fact] // the name it speaks under is the caller's setting, not a constant in here
    public void NudgePrefixIsTheCallersToChoose()
    {
        var rollups = Rollups.ForEstate(Chelsea, [Bed(0, 4, 1)], T, new ClockWiltSource(), Now);

        Assert.StartsWith("Balamb: ", Rollups.ArrivalNudge(Chelsea, rollups));
        Assert.StartsWith("Garden: ", Rollups.ArrivalNudge(Chelsea, rollups, "Garden"));
        Assert.StartsWith("1 ripe", Rollups.ArrivalNudge(Chelsea, rollups, ""));
    }

    [Fact] // all watered, nothing ripe: silence over filler
    public void NudgeSilentWhenAllQuiet()
    {
        var rollups = Rollups.ForEstate(Chelsea, [Bed(1, 2, 1)], T, new ClockWiltSource(), Now);
        Assert.Null(Rollups.ArrivalNudge(Chelsea, rollups));
    }

    private static ClaimedBed Pot(int mapKey, byte stage, double tendedHoursAgo)
    {
        var pot = new ClaimedBed
        {
            Estate = Chelsea, MapKey = mapKey, PatchOrdinal = mapKey, BedSlot = 0, IsPot = true,
            LastTended = Now.AddHours(-tendedHoursAgo),
        };
        pot.Observe(new Observation(Now.AddHours(-tendedHoursAgo), 0x31, stage,
            ObservationSource.TendReceipt));
        return pot;
    }

    [Fact] // a pot untouched for a month is still not thirsty - flowerpots cannot wilt
    public void AncientPotContributesNoThirst()
    {
        var rollup = Assert.Single(
            Rollups.ForEstate(Chelsea, [Pot(3, 2, 720)], T, new ClockWiltSource(), Now));
        Assert.True(rollup.IsPots);
        Assert.Equal(1, rollup.Claimed);
        Assert.Equal(0, rollup.Due);
        Assert.Equal(0, rollup.Overdue);
        Assert.Equal(0, rollup.Danger);
        Assert.Equal(0, rollup.Unknown);
    }

    [Fact] // ...so an estate of nothing but stale pots gets no nudge at all
    public void NudgeSilentWhenOnlyPotsAreStale()
    {
        var rollups = Rollups.ForEstate(
            Chelsea, [Pot(3, 2, 720), Pot(4, 1, 500)], T, new ClockWiltSource(), Now);
        Assert.Null(Rollups.ArrivalNudge(Chelsea, rollups));
    }

    [Fact] // an estate's pots are ONE group, whatever per-pot ordinals the ledger rows hold
    public void PotsRollUpAsOneGroupPerEstate()
    {
        // Two pots with different patch ordinals (real shape: pot ordinals differ per pot).
        var beds = new List<ClaimedBed>
        {
            Pot(180, stage: 2, tendedHoursAgo: 1),
            Pot(181, stage: 2, tendedHoursAgo: 1),
        };

        var rollups = Rollups.ForEstate(Chelsea, beds, T, new ClockWiltSource(), Now);

        var pots = Assert.Single(rollups, r => r.IsPots);
        Assert.Equal(2, pots.Claimed);
        Assert.Equal(Rollups.PotsOrdinal, pots.PatchOrdinal);
    }

    [Fact] // but a ripe pot still speaks: pots ripen, they just never die
    public void RipePotStillCounts()
    {
        var rollups = Rollups.ForEstate(
            Chelsea, [Pot(3, 4, 720)], T, new ClockWiltSource(), Now);
        Assert.Equal(1, rollups.Sum(r => r.Ripe));
        Assert.Contains("1 ripe", Rollups.ArrivalNudge(Chelsea, rollups));
    }
}
