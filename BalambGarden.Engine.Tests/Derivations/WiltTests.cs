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

    [Fact] // never tended under watch: honest Unknown, not a guess
    public void NoTendReceiptMeansUnknown()
        => Assert.Equal(WaterState.Unknown, Source.StateFor(Bed(null), Krakka, T0));

    private static ClaimedBed Pot(DateTimeOffset? tended) => new()
    {
        Estate = new EstateKey(340, 11, 32), MapKey = 3, PatchOrdinal = 3, BedSlot = 0,
        IsPot = true, LastTended = tended,
    };

    [Theory] // flowerpots cannot wilt - no age of tend clock ever makes one thirsty
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(10_000)]
    public void PotsNeverWilt(int hoursSinceTend)
        => Assert.Equal(WaterState.NotApplicable,
            Source.StateFor(Pot(T0), Krakka, T0.AddHours(hoursSinceTend)));

    [Fact] // ...and an untended pot is Not Applicable too, never a hedged Unknown
    public void UntendedPotIsNotApplicable()
        => Assert.Equal(WaterState.NotApplicable, Source.StateFor(Pot(null), Krakka, T0));

    // ------------------------------------------------------------- ripe suppression
    // 2026-08-18 (Sam's screenshot: "DANGER - water now · ripe now" on the same row).
    // A fully grown crop cannot wilt or die - the community table the wilt hours came
    // from says so - so a bed whose latest sighting is stage 4 makes no water claim,
    // however stale its tend clock. The live stage read outranks the derived clock.

    private static ClaimedBed RipeBed(DateTimeOffset tended, byte stage)
    {
        var bed = Bed(tended);
        bed.Observe(new Observation(tended, 0x41, stage, ObservationSource.MapSighting));
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
    // 2026-08-18 (Sam's Allagan Melons died): wilt is a countdown, not an end state.
    // WitherHours is hours-dry until the plant is unrecoverable; the deadline is a
    // plain derivation the surfaces can finally say out loud.

    [Fact]
    public void DiesAtIsTendPlusWitherHours()
        => Assert.Equal(T0.AddHours(48), ClockWiltSource.DiesAt(Bed(T0), Krakka));

    [Fact] // no tend receipt = no clock = no deadline claim
    public void DiesAtUnknownWithoutTend()
        => Assert.Null(ClockWiltSource.DiesAt(Bed(null), Krakka));

    [Fact] // pot wilt is observed, never clocked - a pot claims no deadline either
    public void DiesAtNotApplicableForPots()
        => Assert.Null(ClockWiltSource.DiesAt(Pot(T0), Krakka));

    [Fact] // a ripe bed cannot die - the deadline claim retires with the wilt claim
    public void DiesAtNullOnRipeBed()
        => Assert.Null(ClockWiltSource.DiesAt(RipeBed(T0, stage: 4), Krakka));
}
