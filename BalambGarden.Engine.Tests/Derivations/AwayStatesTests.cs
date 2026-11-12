using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 27, at the garden board's seam (spec ruling 24): away from where the
/// player stands, a planting the plugin saw sown reads exactly one of growing, ripe, wilting or dead
/// from its three clocks, and a plant ripe by the clock makes no water claim anywhere on the tab.
/// The worked examples are the ticket's own, in its own numbers (CST).</summary>
public class AwayStatesTests
{
    private static readonly DomainTables T = DomainTables.Load();

    // Data/Crops.json: Krakka Root grow 72h, wilt 24h, wither 48h; Thavnairian Onion grow 240h,
    // wilt 24h, wither 48h. Sunflowers: a flowerpot flower, no crop row.
    private const ushort Krakka = 49;
    private const ushort Onion = 50;
    private const ushort Sunflowers = 103;

    private static readonly EstateKey Yard = new(340, 11, 57);

    private static DateTimeOffset Cst(string local) => DateTimeOffset.Parse(local + "-06:00");

    private static ClaimedBed Bed(int slot = 0) => new() { Estate = Yard, MapKey = 100, PatchOrdinal = 0, BedSlot = slot };

    private static ClaimedBed Pot(int key) => new() { Estate = Yard, MapKey = key, PatchOrdinal = key, BedSlot = 0, IsPot = true };

    private static VisitCard Card(IReadOnlyList<ClaimedBed> beds, DateTimeOffset now, Func<ClaimedBed, bool>? held = null)
        => Assert.Single(GardenBoard.Cards([new EstateRecord { Key = Yard, Nickname = "FC Estate" }], beds,
            new AccessRoster([new RosterEstate(Yard, "test")]), T, now, held: held));

    private static ContainerCell Only(VisitCard card) => Assert.Single(card.Patches.SelectMany(p => p.Cells), c => c.Bed is not null);

    private static (ContainerLook Look, bool ByClock) Read(ClaimedBed bed, DateTimeOffset now)
    {
        var cell = Only(Card([bed], now));
        return (cell.Look, cell.ByClock);
    }

    /// <summary>The ticket's Krakka Root bed: sown Mon 6:00 pm, no watering recorded.</summary>
    private static ClaimedBed KrakkaSownMonday() => Seed.Plant(Bed(), Cst("2026-11-09T18:00:00"), Krakka, stage: 0);

    [Fact]
    public void AFreshPlantingReadsGrowing()
        => Assert.Equal((ContainerLook.Growing, false), Read(KrakkaSownMonday(), Cst("2026-11-10T08:00:00")));

    [Fact] // the wilt time: Tue 6:00 pm, the sowing + 24 h
    public void WiltingShowsAtItsClockTime()
    {
        Assert.Equal((ContainerLook.WaterSoon, false), Read(KrakkaSownMonday(), Cst("2026-11-10T17:59:00")));
        Assert.Equal((ContainerLook.Wilting, true), Read(KrakkaSownMonday(), Cst("2026-11-10T18:01:00")));
        Assert.Equal(Cst("2026-11-11T06:00:00"), Only(Card([KrakkaSownMonday()], Cst("2026-11-10T18:01:00"))).Clock!.DyingAt);   // "dying from ~6:00 am"
    }

    [Fact] // the death time Wed 6:00 pm comes before the ripe time Thu 6:00 pm: it never reads ripe
    public void ThePlantThatDiesBeforeItRipensReadsDeadAtItsClockTime()
    {
        Assert.Equal((ContainerLook.Dying, true), Read(KrakkaSownMonday(), Cst("2026-11-11T17:59:00")));
        var dead = Only(Card([KrakkaSownMonday()], Cst("2026-11-11T18:01:00")));
        Assert.Equal((ContainerLook.Dead, true), (dead.Look, dead.ByClock));
        Assert.Equal(Cst("2026-11-11T18:00:00"), dead.ClockSince);
        Assert.Null(dead.WaterBy);
        Assert.Null(dead.SeenDeadAt);    // the game never showed it
        Assert.Equal((ContainerLook.Dead, true), Read(KrakkaSownMonday(), Cst("2026-11-12T19:00:00")));   // past the ripe time too
    }

    [Fact] // ripe shows an hour after the ripe time: the game updates plants about hourly
    public void RipeShowsAnHourAfterTheRipeTime()
    {
        // Sown Mon 6:00 pm, watered Wed 6:00 pm: ripe Thu 6:00 pm, wilt Thu 6:00 pm, death Fri 6:00 pm.
        ClaimedBed Watered() => Seed.Tend(KrakkaSownMonday(), Cst("2026-11-11T18:00:00"));
        Assert.Equal((ContainerLook.Wilting, true), Read(Watered(), Cst("2026-11-12T18:59:00")));
        var ripe = Only(Card([Watered()], Cst("2026-11-12T19:01:00")));
        Assert.Equal((ContainerLook.Ripe, true), (ripe.Look, ripe.ByClock));
        Assert.Equal(Cst("2026-11-12T19:00:00"), ripe.ClockSince);
        Assert.Equal((Cst("2026-11-09T18:00:00"), 72), (ripe.Clock!.SownAt, ripe.Clock.Crop.GrowHours));   // the hover's "sown ..., 72 h to grow"
        Assert.Null(ripe.SeenRipeAt);
    }

    [Fact] // the ticket's onion: last watered Sat 6:00 pm, ripe Sun 8:00 pm - wilting, then ripe, never dead
    public void APlantThatWiltedThenRipenedReadsRipeAndNeverDead()
    {
        ClaimedBed Onion1() => Seed.Tend(Seed.Plant(Pot(1), Cst("2026-10-29T20:00:00"), Onion, stage: 0),
            Cst("2026-11-07T18:00:00"));
        Assert.Equal((ContainerLook.Growing, false), Read(Onion1(), Cst("2026-11-07T20:00:00")));
        Assert.Equal((ContainerLook.Wilting, true), Read(Onion1(), Cst("2026-11-08T18:01:00")));
        Assert.Equal((ContainerLook.Wilting, true), Read(Onion1(), Cst("2026-11-08T20:59:00")));
        Assert.Equal((ContainerLook.Ripe, true), Read(Onion1(), Cst("2026-11-08T21:01:00")));
        Assert.Equal((ContainerLook.Ripe, true), Read(Onion1(), Cst("2026-11-09T18:01:00")));   // past its death time
    }

    [Fact] // scope call 2: ripe 5:30 pm, death 6:00 pm, ripe shows 6:30 pm - the plant ripened first
    public void ADeathInsideTheRipeHourReadsWiltingUntilRipeShows()
    {
        // Sown Sun 5:30 pm (ripe Wed 5:30 pm), watered Mon 6:00 pm (death Wed 6:00 pm).
        ClaimedBed Bed1() => Seed.Tend(Seed.Plant(Bed(), Cst("2026-11-08T17:30:00"), Krakka, stage: 0),
            Cst("2026-11-09T18:00:00"));
        Assert.Equal((ContainerLook.Dying, true), Read(Bed1(), Cst("2026-11-11T18:15:00")));
        Assert.Equal((ContainerLook.Ripe, true), Read(Bed1(), Cst("2026-11-11T18:31:00")));
    }

    [Fact] // a planting the plugin did not see keeps today's reading: its window is behind now, "any time now"
    public void AnUnseenPlantingIsUnchanged()
    {
        var bed = Seed.Sight(Bed(), Cst("2026-11-01T12:00:00"), Krakka, stage: 2);
        var cell = Only(Card([bed], Cst("2026-11-12T12:00:00")));
        Assert.False(cell.ByClock);
        Assert.NotEqual(ContainerLook.Ripe, cell.Look);
        Assert.NotEqual(ContainerLook.Dead, cell.Look);
        Assert.True(cell.Ripe!.Latest < Cst("2026-11-12T12:00:00"));
        Assert.Equal("any time now", WindowFormat.Coarse(cell.Ripe.Earliest, cell.Ripe.Latest, Cst("2026-11-12T12:00:00")));
    }

    [Fact] // a flowerpot flower has no wilt hours and no clock: unchanged, never ripe by the clock
    public void AFlowerpotFlowerIsUnchanged()
    {
        var pot = Seed.Plant(Pot(1), Cst("2026-11-09T12:00:00"), Sunflowers, stage: 0);
        var cell = Only(Card([pot], Cst("2026-11-12T12:00:00")));
        Assert.Equal((ContainerLook.Growing, false), (cell.Look, cell.ByClock));
        Assert.True(cell.NeverWilts);
        Assert.Null(cell.WaterBy);
    }

    [Fact] // a ripe-by-the-clock plant asks for no water anywhere: deadline, chip, worst look, water bar
    public void RipeByTheClockMakesNoWaterClaim()
    {
        // Watered Wed 6:00 pm: at Thu 8:00 pm it is past its wilt time but ripe by the clock since 7:00 pm.
        var bed = Seed.Tend(KrakkaSownMonday(), Cst("2026-11-11T18:00:00"));
        var card = Card([bed], Cst("2026-11-12T20:00:00"));

        Assert.Null(card.WaterBy);
        Assert.Equal(0, card.Water.Total);
        Assert.Equal(ContainerLook.Growing, card.Thirst);
        Assert.Equal(0, card.Worst.Total);
        Assert.Null(card.Lane.WaterFrom);
        Assert.Equal((1, 1, 1), (card.RipeNow, card.RipeByClock, card.Harvest));
        var mark = Assert.Single(card.Lane.Marks);
        Assert.Equal(LaneMarkKind.RipeByClock, mark.Kind);
    }

    [Fact] // the line above the tabs counts it ripe, by the clock, and not thirsty
    public void TheLineAboveTheTabsCountsTheClocksRipeAndNoThirst()
    {
        var bed = Seed.Tend(KrakkaSownMonday(), Cst("2026-11-11T18:00:00"));
        var verdict = Verdicts.ForGarden([new EstateRecord { Key = Yard, Nickname = "FC Estate" }], [bed], T,
            new ClockWiltSource(), Cst("2026-11-12T20:00:00"));
        Assert.Equal("1 bed ripe at FC Estate (1 by the clock)", verdict.Text);
    }

    [Fact] // a seen-ripe count and the clock's together, elsewhere included
    public void TheLineAboveTheTabsSaysHowManyAreTheClocks()
    {
        var other = new EstateKey(339, 4, 52);
        var now = Cst("2026-11-12T20:00:00");
        var beds = new List<ClaimedBed>
        {
            Seed.Tend(KrakkaSownMonday(), Cst("2026-11-11T18:00:00")),
            Seed.Ripe(Seed.Plant(Bed(1), Cst("2026-11-08T12:00:00"), Krakka, stage: 0), Cst("2026-11-11T13:00:00"), Krakka),
            Seed.Tend(Seed.Plant(new ClaimedBed { Estate = other, MapKey = 5, PatchOrdinal = 0, BedSlot = 0 },
                Cst("2026-11-09T18:00:00"), Krakka, stage: 0), Cst("2026-11-11T18:00:00")),
        };
        var verdict = Verdicts.ForGarden(
            [new EstateRecord { Key = Yard, Nickname = "FC Estate" }, new EstateRecord { Key = other, Nickname = "Home" }],
            beds, T, new ClockWiltSource(), now);
        Assert.Equal("2 beds ripe at FC Estate (1 by the clock) · 1 more ripe elsewhere (1 by the clock)", verdict.Text);
    }

    [Fact] // the clock's dead asks for no water in the line above the tabs either
    public void DeadByTheClockIsNotThirsty()
    {
        var verdict = Verdicts.ForGarden([new EstateRecord { Key = Yard, Nickname = "FC Estate" }], [KrakkaSownMonday()], T,
            new ClockWiltSource(), Cst("2026-11-11T19:00:00"));
        Assert.DoesNotContain("thirsty", verdict.Text);
    }

    // ------------------------------------------------------------------ pot 131, its real record

    /// <summary>Pot 131 from the ledger copy of 2026-11-11 19:26 (rethink 25's receipts): sown Sun
    /// 11-01 5:57:05 pm, last watering Tue 11-10 7:43:20 pm, its newest read stage 3 at the Wed
    /// 7:24:59 pm arrival, seen fine then. That read was the arrival look: held.</summary>
    private static ClaimedBed Pot131()
    {
        var pot = Seed.Plant(Pot(131), DateTimeOffset.Parse("2026-11-01T23:57:05Z"), Onion, stage: 0);
        Seed.Tend(pot, DateTimeOffset.Parse("2026-11-11T01:43:20Z"), Onion);
        Seed.Sight(pot, DateTimeOffset.Parse("2026-11-12T01:24:59Z"), Onion, stage: 3);
        pot.SightWilt(DateTimeOffset.Parse("2026-11-12T01:24:59Z"), wilting: false, stage: 3);
        return pot;
    }

    [Fact]
    public void Pot131ReadsGrowingAt656WithItsDeadline()
    {
        var card = Card([Pot131()], Cst("2026-11-11T18:56:00"), held: _ => true);
        var cell = Only(card);
        Assert.Equal((ContainerLook.WaterSoon, false), (cell.Look, cell.ByClock));   // growing, its amber shade (scope call 1)
        Assert.Equal(Cst("2026-11-11T19:43:20"), card.WaterBy);
    }

    [Theory]
    [InlineData("2026-11-11T18:57:30")]
    [InlineData("2026-11-11T19:50:00")]
    public void Pot131ReadsRipeByTheClockWithNoWaterDeadline(string local)
    {
        var card = Card([Pot131()], Cst(local), held: _ => true);
        var cell = Only(card);
        Assert.Equal((ContainerLook.Ripe, true), (cell.Look, cell.ByClock));
        Assert.Null(card.WaterBy);
        Assert.Equal(0, card.Water.Total);
        Assert.Equal(ContainerLook.Growing, card.Thirst);
        Assert.Equal(1, card.RipeByClock);
    }

    // ------------------------------------------------------------------ a look that settled, walked away from (X)

    [Fact] // X: a settled growing look past the ripe hour moves ripe to an hour after the look
    public void ASettledGrowingLookMovesRipeToAnHourAfterIt()
    {
        var pot = Seed.Plant(Pot(131), DateTimeOffset.Parse("2026-11-01T23:57:05Z"), Onion, stage: 0);
        Seed.Tend(pot, DateTimeOffset.Parse("2026-11-11T01:43:20Z"), Onion);
        Seed.Sight(pot, Cst("2026-11-11T19:25:14"), Onion, stage: 3);       // the 15 s look: growing, and it stands
        pot.SightWilt(Cst("2026-11-11T19:25:14"), wilting: false, stage: 3);

        Assert.Equal((ContainerLook.WaterSoon, false), Read(pot, Cst("2026-11-11T19:30:00")));    // growing, water soon
        Assert.Equal((ContainerLook.Wilting, true), Read(pot, Cst("2026-11-11T19:50:00")));       // past its wilt time
        Assert.Equal((ContainerLook.Ripe, true), Read(pot, Cst("2026-11-11T20:26:00")));
    }

    [Fact] // my reading: a bed seen alive after its clock said dead had a watering the plugin never heard
    public void ABedSeenAliveAfterItsDeathTimeMakesNoWaterClaim()
    {
        var bed = KrakkaSownMonday();
        Seed.Sight(bed, Cst("2026-11-11T19:00:00"), Krakka, stage: 3);   // a settled look an hour past the death time
        var card = Card([bed], Cst("2026-11-11T20:00:00"));
        var cell = Only(card);
        Assert.Equal((ContainerLook.Growing, false), (cell.Look, cell.ByClock));
        Assert.True(cell.OutlivedClock);
        Assert.Null(cell.WaterBy);
        Assert.Null(card.WaterBy);
    }

    [Fact] // a look further on than the clock simply stands as seen
    public void ASeenRipeOrDeadStandsWhateverTheClock()
    {
        var ripe = Seed.Ripe(KrakkaSownMonday(), Cst("2026-11-10T08:00:00"), Krakka);
        Assert.Equal((ContainerLook.Ripe, false), Read(ripe, Cst("2026-11-10T09:00:00")));
        Assert.Equal(Cst("2026-11-10T08:00:00"), Only(Card([ripe], Cst("2026-11-10T09:00:00"))).SeenRipeAt);
        var dead = Seed.Sight(KrakkaSownMonday(), Cst("2026-11-10T08:00:00"), Krakka, stage: 5);
        Assert.Equal((ContainerLook.Dead, false), Read(dead, Cst("2026-11-10T09:00:00")));
    }

    [Fact] // a held read never overrules the clock, ripe or dead included
    public void AHeldReadNeverOverrulesTheClock()
    {
        var bed = Seed.Sight(KrakkaSownMonday(), Cst("2026-11-10T08:00:00"), Krakka, stage: 5);
        var cell = Only(Card([bed], Cst("2026-11-10T09:00:00"), held: _ => true));
        Assert.Equal((ContainerLook.Growing, false), (cell.Look, cell.ByClock));
    }
}
