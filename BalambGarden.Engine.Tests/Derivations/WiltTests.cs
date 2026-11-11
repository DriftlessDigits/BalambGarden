using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Domain;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class WiltTests
{
    private static readonly Crop Krakka = new("Krakka Root", GrowHours: 72, WiltHours: 24,
        WitherHours: 48, ItemId: 4842, SeedId: 7745, SeedName: "Krakka Root Seeds", Crossable: true);
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-13T18:00:00Z");

    private static ClaimedBed Bed(DateTimeOffset? tended) => new()
    {
        Estate = new EstateKey(340, 11, 32), MapKey = 110, PatchOrdinal = 0, BedSlot = 0,
        LastTended = tended,
    };

    private static readonly ClockWiltSource Source = new();

    [Theory]
    [InlineData(0, WaterState.Watered)]
    [InlineData(17, WaterState.Watered)]  // < 18h (75% of 24)
    [InlineData(19, WaterState.Due)]      // 75% crossed
    [InlineData(25, WaterState.Overdue)]  // past 24h wilt window
    [InlineData(37, WaterState.Danger)]   // past 24 + (48-24)/2 = 36h
    public void KrakkaClockStates(int hoursSinceTend, WaterState expected)
        => Assert.Equal(expected, Source.StateFor(Bed(T0), Krakka, T0.AddHours(hoursSinceTend)));

    [Fact] // no watering and no sowing heard: honest Unknown, not a guess
    public void NoTendReceiptMeansUnknown()
        => Assert.Equal(WaterState.Unknown, Source.StateFor(Bed(null), Krakka, T0));

    // ------------------------------------------------------------- sowing start
    // Dashboard rethink ruling 3 (2026-11-08): a sowing starts the wilt clock, so a
    // freshly sown bed whose watering was never heard counts from its sowing.

    private static ClaimedBed Sown(DateTimeOffset sownAt) => Seed.Plant(Bed(null), sownAt, 0x31, stage: 1);

    [Theory]
    [InlineData(17, WaterState.Watered)]  // under 18h since the sowing
    [InlineData(19, WaterState.Due)]
    [InlineData(25, WaterState.Overdue)]
    [InlineData(37, WaterState.Danger)]
    public void SowingStartsTheClockWhenNoWateringWasHeard(int hoursSinceSowing, WaterState expected)
        => Assert.Equal(expected, Source.StateFor(Sown(T0), Krakka, T0.AddHours(hoursSinceSowing)));

    [Fact] // a watering heard after the sowing restarts the clock from the watering
    public void AWateringAfterTheSowingWins()
    {
        var bed = Seed.Tend(Sown(T0), T0.AddHours(20), 0x31);
        Assert.Equal(WaterState.Watered, Source.StateFor(bed, Krakka, T0.AddHours(30)));
    }

    [Fact]
    public void DiesAtCountsFromTheSowingWhenNoWateringWasHeard()
        => Assert.Equal(T0.AddHours(48), ClockWiltSource.DiesAt(Sown(T0), Krakka));

    [Fact] // the water deadline: start of the clock + the crop's wilt hours
    public void WaterByIsTheClockStartPlusWiltHours()
    {
        Assert.Equal(T0.AddHours(24), ClockWiltSource.WaterBy(Sown(T0), Krakka));
        Assert.Equal(T0.AddHours(44), ClockWiltSource.WaterBy(Seed.Tend(Sown(T0), T0.AddHours(20), 0x31), Krakka));
        Assert.Null(ClockWiltSource.WaterBy(Bed(null), Krakka));
        Assert.Equal(T0.AddHours(24), ClockWiltSource.WaterBy(Pot(T0), Krakka));
    }

    [Fact] // the sowing start is a deadline resting on no recorded watering - say so, beds and pots alike
    public void TendUnheardOnlyWithoutATend()
    {
        Assert.True(ClockWiltSource.TendUnheard(Sown(T0)));
        Assert.True(ClockWiltSource.TendUnheard(Bed(null)));
        Assert.False(ClockWiltSource.TendUnheard(Bed(T0)));
        Assert.True(ClockWiltSource.TendUnheard(Pot(null)));
        Assert.False(ClockWiltSource.TendUnheard(Pot(T0)));
    }

    private static ClaimedBed Pot(DateTimeOffset? tended) => new()
    {
        Estate = new EstateKey(340, 11, 32), MapKey = 3, PatchOrdinal = 3, BedSlot = 0,
        IsPot = true, LastTended = tended,
    };

    [Theory] // spec ruling 23 (2026-11-10): a crop in a pot runs the same clock as one in a bed
    [InlineData(0, WaterState.Watered)]
    [InlineData(19, WaterState.Due)]
    [InlineData(25, WaterState.Overdue)]
    [InlineData(37, WaterState.Danger)]
    public void PotsRunTheSameClock(int hoursSinceTend, WaterState expected)
        => Assert.Equal(expected, Source.StateFor(Pot(T0), Krakka, T0.AddHours(hoursSinceTend)));

    [Fact] // a pot with neither a watering nor a sowing recorded has no clock, as a bed
    public void APotWithNoClockStartIsUnknown()
        => Assert.Equal(WaterState.Unknown, Source.StateFor(Pot(null), Krakka, T0));

    // ------------------------------------------------------------- ripe suppression
    // 2026-08-18 (Drift's screenshot: "DANGER - water now · ripe now" on the same row).
    // A fully grown crop cannot wilt or die - the community table the wilt hours came
    // from says so - so a bed whose latest sighting is stage 4 makes no water claim,
    // however stale its tend clock. The live stage read outranks the derived clock.

    private static ClaimedBed RipeBed(DateTimeOffset tended, byte stage)
    {
        var bed = Bed(tended);
        Seed.Sight(bed, tended, 0x41, stage);
        return bed;
    }

    [Theory]
    [InlineData(25)]     // clock says Overdue
    [InlineData(37)]     // clock says Danger
    [InlineData(500)]    // clock says long dead - and the plant is still fine
    public void RipeBedMakesNoWaterClaim(int hoursSinceTend)
        => Assert.Equal(WaterState.NotApplicable,
            Source.StateFor(RipeBed(T0, stage: 4), Krakka, T0.AddHours(hoursSinceTend)));

    [Fact] // stage 3 is still on the clock - suppression is stage 4 only
    public void GrowingBedStillRunsTheClock()
        => Assert.Equal(WaterState.Danger,
            Source.StateFor(RipeBed(T0, stage: 3), Krakka, T0.AddHours(37)));

    // ------------------------------------------------------------------ death clock
    // 2026-08-18 (Drift's Allagan Melons died): wilt is a countdown, not an end state.
    // WitherHours is hours-dry until the plant is unrecoverable; the deadline is a
    // plain derivation the surfaces can finally say out loud.

    [Fact]
    public void DiesAtIsTendPlusWitherHours()
        => Assert.Equal(T0.AddHours(48), ClockWiltSource.DiesAt(Bed(T0), Krakka));

    [Fact] // no tend receipt = no clock = no deadline claim
    public void DiesAtUnknownWithoutTend()
        => Assert.Null(ClockWiltSource.DiesAt(Bed(null), Krakka));

    [Fact] // spec ruling 23: a pot's death time counts like a bed's
    public void DiesAtCountsForPots()
        => Assert.Equal(T0.AddHours(48), ClockWiltSource.DiesAt(Pot(T0), Krakka));

    [Fact] // a ripe bed cannot die - the deadline claim retires with the wilt claim
    public void DiesAtNullOnRipeBed()
        => Assert.Null(ClockWiltSource.DiesAt(RipeBed(T0, stage: 4), Krakka));
}
