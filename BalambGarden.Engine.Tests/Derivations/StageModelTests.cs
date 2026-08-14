using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

public class StageModelTests
{
    private const int Grow = 120; // 5-day crop
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-10T18:00:00Z");

    private static Observation Obs(double hoursAfterT0, byte stage,
        ObservationSource src = ObservationSource.MapSighting)
        => new(T0.AddHours(hoursAfterT0), 0x24, stage, src);

    [Fact] // plant receipt = anchored: exact ripe time, zero-width window
    public void PlantReceiptAnchors()
    {
        var w = StageModel.RipeWindow([Obs(0, 1, ObservationSource.PlantReceipt)], Grow)!;
        Assert.Equal(Provenance.Anchored, w.Provenance);
        Assert.Equal(T0.AddHours(Grow), w.Earliest);
        Assert.Equal(w.Earliest, w.Latest);
    }

    [Fact] // one sighting: estimated, window spans the whole stage band
    public void SingleSightingEstimates()
    {
        var w = StageModel.RipeWindow([Obs(50, 2)], Grow)!;
        Assert.Equal(Provenance.Estimated, w.Provenance);
        // stage 2 at t=50h: plant in [50 - 2/3*120, 50 - 1/3*120] = [-30, +10] hrs
        Assert.Equal(T0.AddHours(-30 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(10 + Grow), w.Latest);
    }

    [Fact] // two disagreeing sightings bracket the flip and tighten the window
    public void TwoSightingsBracket()
    {
        var w = StageModel.RipeWindow([Obs(30, 1), Obs(50, 2)], Grow)!;
        Assert.Equal(Provenance.Bracketed, w.Provenance);
        // stage1@30: plant in [-10, 30]; stage2@50: plant in [-30, 10] -> intersect [-10, 10]
        Assert.Equal(T0.AddHours(-10 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(10 + Grow), w.Latest);
        Assert.True(w.Latest - w.Earliest < TimeSpan.FromHours(41)); // tighter than either alone
    }

    [Fact] // ripe observed = already ripe now, not a forecast
    public void StageFourIsRipeNow()
    {
        var w = StageModel.RipeWindow([Obs(0, 1), Obs(100, 4)], Grow)!;
        Assert.True(w.Earliest <= T0.AddHours(100));
        Assert.Equal(w.Earliest, w.Latest);
    }

    [Fact]
    public void EmptyRingGivesNull()
        => Assert.Null(StageModel.RipeWindow([], Grow));

    [Fact] // contradictory sightings (impossible intersection) -> null, never a lie
    public void ContradictionGivesNull()
        => Assert.Null(StageModel.RipeWindow([Obs(0, 3), Obs(100, 1)], Grow));
}
