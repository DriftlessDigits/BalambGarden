using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>Rethink slice 27, the look rule at its seam (spec ruling 24, ruled 2026-11-11, the
/// reading confirmed by the player): the arrival look never overrules the clock; if the 5 s look
/// agrees the clock stands, if it disagrees the 15 s look is the truth and a warning line is
/// written; every look after is the room's truth; leaving before the 15 s look leaves the clock
/// speaking. Pot 131's arrival, 2026-11-11 (CST), in the ticket's own numbers.</summary>
public class LookRuleTests
{
    private static readonly DomainTables T = DomainTables.Load();
    private const ushort Onion = 50;
    private static readonly EstateKey Fc = new(340, 11, 57);
    private static readonly RoomSide Inside = new(Fc, Inside: true);

    private static DateTimeOffset Cst(string local) => DateTimeOffset.Parse(local + "-06:00");

    private static readonly DateTimeOffset Arrival = Cst("2026-11-11T19:24:59");
    private static readonly DateTimeOffset Five = Cst("2026-11-11T19:25:04");
    private static readonly DateTimeOffset Fifteen = Cst("2026-11-11T19:25:14");

    /// <summary>Pot 131 as recorded, ripe by the clock since 6:57 pm, then read at the arrival.</summary>
    private static ClaimedBed Pot131(byte arrivalStage = 3)
    {
        var pot = Seed.Plant(new ClaimedBed { Estate = Fc, MapKey = 131, PatchOrdinal = 131, BedSlot = 0, IsPot = true },
            DateTimeOffset.Parse("2026-11-01T23:57:05Z"), Onion, stage: 0);
        Seed.Tend(pot, DateTimeOffset.Parse("2026-11-11T01:43:20Z"), Onion);
        return Look(pot, Arrival, arrivalStage);
    }

    /// <summary>A census read of the pot at <paramref name="at"/>: its stage, not wilting.</summary>
    private static ClaimedBed Look(ClaimedBed pot, DateTimeOffset at, byte stage)
    {
        Seed.Sight(pot, at, Onion, stage);
        pot.SightWilt(at, wilting: false, stage);
        return pot;
    }

    private static ContainerLook Shows(ClaimedBed pot, LookRule rule, DateTimeOffset now)
        => Assert.Single(GardenBoard.Cards([new EstateRecord { Key = Fc, Nickname = "FC Estate" }], [pot],
                new AccessRoster([new RosterEstate(Fc, "test")]), T, now, held: rule.Holds))
            .Patches.Single().Cells.Single().Look;

    [Fact] // the stale arrival look (stage 3) never overrules the clock's ripe
    public void TheArrivalLookIsHeldAndTheClockSpeaks()
    {
        var pot = Pot131();
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);

        Assert.True(rule.Holds(pot));
        Assert.Equal(ContainerLook.Ripe, Shows(pot, rule, Arrival.AddSeconds(1)));
    }

    [Fact] // the 5 s look agrees with the clock: the clock stands, and the pot is seen ripe
    public void AFiveSecondLookThatAgreesSettlesIt()
    {
        var pot = Pot131();
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);

        Assert.Empty(rule.Looked(Inside, [Look(pot, Five, stage: 4)], Five));
        Assert.False(rule.Holds(pot));
        Assert.Equal(ContainerLook.Ripe, Shows(pot, rule, Five.AddSeconds(1)));
    }

    [Fact] // the 5 s look disagrees; the 15 s look says growing: the look is the truth
    public void AFifteenSecondLookThatDisagreesIsTheTruth()
    {
        var pot = Pot131();
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);

        Assert.Empty(rule.Looked(Inside, [Look(pot, Five, stage: 3)], Five));
        Assert.True(rule.Holds(pot));                                    // still the clock's word
        Assert.Equal(ContainerLook.Ripe, Shows(pot, rule, Five.AddSeconds(1)));

        var warnings = rule.Looked(Inside, [Look(pot, Fifteen, stage: 3)], Fifteen);
        Assert.False(rule.Holds(pot));
        Assert.Equal(ContainerLook.WaterSoon, Shows(pot, rule, Fifteen.AddSeconds(1)));   // growing, water soon
        Assert.Equal(
            ["a look disagreed with the clock at Ward 12 Plot 58, pot (key 131): the clock said ripe; "
             + "the 5s look at 2026-11-12 01:25:04Z said growing; the 15s look at 2026-11-12 01:25:14Z said growing and stands"],
            warnings);
    }

    [Fact] // the 5 s look disagrees; the 15 s look says ripe: the clock's state, taken from the look
    public void AFifteenSecondLookThatAgreesWithTheClockIsTheTruthToo()
    {
        var pot = Pot131();
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);
        rule.Looked(Inside, [Look(pot, Five, stage: 3)], Five);

        var warnings = rule.Looked(Inside, [Look(pot, Fifteen, stage: 4)], Fifteen);
        Assert.False(rule.Holds(pot));
        Assert.Equal(ContainerLook.Ripe, Shows(pot, rule, Fifteen.AddSeconds(1)));
        Assert.Equal(
            ["a look disagreed with the clock at Ward 12 Plot 58, pot (key 131): the clock said ripe; "
             + "the 5s look at 2026-11-12 01:25:04Z said growing; the 15s look at 2026-11-12 01:25:14Z said ripe and stands"],
            warnings);
    }

    [Fact] // he leaves before the 15 s look: the clock keeps speaking
    public void LeavingBeforeTheFifteenSecondLookLeavesTheClockSpeaking()
    {
        var pot = Pot131();
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);
        rule.Looked(Inside, [Look(pot, Five, stage: 3)], Five);

        Assert.True(rule.Holds(pot));
        Assert.Equal(ContainerLook.Ripe, Shows(pot, rule, Cst("2026-11-11T19:50:00")));
    }

    [Fact] // a look after the 15 s one is the room's truth: nothing is held again until the next arrival
    public void ALookAfterTheFifteenSecondOneIsTheRoomsTruth()
    {
        var pot = Pot131();
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);
        rule.Looked(Inside, [Look(pot, Five, stage: 4)], Five);
        rule.Looked(Inside, [Look(pot, Fifteen, stage: 4)], Fifteen);

        var thirty = Cst("2026-11-11T19:25:29");
        Assert.Empty(rule.Looked(Inside, [Look(pot, thirty, stage: 3)], thirty));
        Assert.False(rule.Holds(pot));
        Assert.Equal(ContainerLook.WaterSoon, Shows(pot, rule, thirty.AddSeconds(1)));   // growing, water soon
    }

    [Fact] // a bed publishes no wilt, so its living read agrees with a clock that says wilting
    public void ABedsLivingReadAgreesWithTheClocksWilting()
    {
        const ushort krakka = 49;
        var bed = Seed.Plant(new ClaimedBed { Estate = Fc, MapKey = 100, PatchOrdinal = 0, BedSlot = 0 },
            Cst("2026-11-09T18:00:00"), krakka, stage: 0);
        var outside = new RoomSide(Fc, Inside: false);
        var arrival = Cst("2026-11-10T19:00:00");    // an hour past its wilt time
        Seed.Sight(bed, arrival, krakka, 2);
        var rule = new LookRule(T);
        rule.Arrived(outside, [bed], arrival);

        Seed.Sight(bed, arrival.AddSeconds(5), krakka, 2);
        rule.Looked(outside, [bed], arrival.AddSeconds(5));
        Assert.False(rule.Holds(bed));
    }

    [Fact] // a planting the plugin did not see has no clock: nothing to hold
    public void AnUnseenPlantingIsNeverHeld()
    {
        var pot = Seed.Sight(new ClaimedBed { Estate = Fc, MapKey = 7, PatchOrdinal = 7, BedSlot = 0, IsPot = true },
            Arrival, Onion, stage: 3);
        var rule = new LookRule(T);
        rule.Arrived(Inside, [pot], Arrival);
        Assert.False(rule.Holds(pot));
    }
}
