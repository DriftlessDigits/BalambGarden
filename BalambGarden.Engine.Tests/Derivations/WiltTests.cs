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
}
