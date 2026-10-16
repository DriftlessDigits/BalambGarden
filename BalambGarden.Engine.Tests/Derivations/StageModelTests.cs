using BalambGarden.Engine.Census;
using BalambGarden.Engine.Derivations;
using BalambGarden.Engine.Ledger;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The ripe estimate reads the tenancy record (2026-10-14): a witnessed sowing anchors;
/// otherwise the per-stage brackets intersect. Never null while any stage evidence exists - a
/// contradiction is an honest estimate with a flag, not a shrug.</summary>
public class StageModelTests
{
    private const int Grow = 120; // 5-day crop
    private const ushort Species = 0x24;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-10T18:00:00Z");

    private static ClaimedBed Bed() => new()
    {
        Estate = new EstateKey(340, 11, 32), MapKey = 110, PatchOrdinal = 0, BedSlot = 3,
    };

    private static ClaimedBed Seen(params (double HoursAfterT0, byte Stage)[] sightings)
    {
        var bed = Bed();
        foreach (var (h, stage) in sightings)
            Seed.Sight(bed, T0.AddHours(h), Species, stage);
        return bed;
    }

    [Fact] // plant receipt = anchored: exact ripe time, zero-width window
    public void PlantReceiptAnchors()
    {
        var w = StageModel.RipeWindow(Seed.Plant(Bed(), T0, Species, stage: 1), Grow)!;
        Assert.Equal(Provenance.Anchored, w.Window.Provenance);
        Assert.Equal(T0.AddHours(Grow), w.Window.Earliest);
        Assert.Equal(w.Window.Earliest, w.Window.Latest);
        Assert.False(w.Contradiction);
    }

    [Fact] // an anchor outranks any bracket the sightings would give
    public void AnchorOutranksBrackets()
    {
        var bed = Seed.Plant(Bed(), T0, Species);
        Seed.Sight(bed, T0.AddHours(50), Species, 2);
        Assert.Equal(Provenance.Anchored, StageModel.RipeWindow(bed, Grow)!.Window.Provenance);
    }

    [Fact] // one sighting: estimated, window spans the whole stage band
    public void SingleSightingEstimates()
    {
        var w = StageModel.RipeWindow(Seen((50, 2)), Grow)!.Window;
        Assert.Equal(Provenance.Estimated, w.Provenance);
        // stage 2 at t=50h: plant in [50 - 2/3*120, 50 - 1/3*120] = [-30, +10] hrs
        Assert.Equal(T0.AddHours(-30 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(10 + Grow), w.Latest);
    }

    [Fact] // the same stage read twice is still one piece of evidence, but the bracket uses both ends
    public void SameStageTwiceStaysEstimatedAndUsesBothEnds()
    {
        var w = StageModel.RipeWindow(Seen((10, 2), (20, 2)), Grow)!.Window;
        Assert.Equal(Provenance.Estimated, w.Provenance);
        // earliest plant = LastSeen(20) - 80 = -60; latest plant = FirstSeen(10) - 40 = -30
        Assert.Equal(T0.AddHours(-60 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(-30 + Grow), w.Latest);
    }

    [Fact] // two stages bracket the flip and tighten the window
    public void TwoStagesBracket()
    {
        var w = StageModel.RipeWindow(Seen((30, 1), (50, 2)), Grow)!.Window;
        Assert.Equal(Provenance.Bracketed, w.Provenance);
        // stage1@30: plant in [-10, 30]; stage2@50: plant in [-30, 10] -> intersect [-10, 10]
        Assert.Equal(T0.AddHours(-10 + Grow), w.Earliest);
        Assert.Equal(T0.AddHours(10 + Grow), w.Latest);
    }

    [Fact] // ripe seen: it ripened between the last stage-3 look and the first stage-4 one
    public void RipeBracketsBetweenLastStageThreeAndFirstStageFour()
    {
        var e = StageModel.RipeWindow(Seen((60, 3), (100, 4), (110, 4)), Grow)!;
        Assert.Equal(T0.AddHours(60), e.Window.Earliest);
        Assert.Equal(T0.AddHours(100), e.Window.Latest);
        Assert.Equal(Provenance.Bracketed, e.Window.Provenance);
        Assert.False(e.Contradiction);
    }

    [Fact] // ripe seen with no stage 3 on record: one band wide, estimated
    public void RipeWithoutStageThreeIsAOneBandEstimate()
    {
        var e = StageModel.RipeWindow(Seen((100, 4)), Grow)!;
        Assert.Equal(T0.AddHours(60), e.Window.Earliest);
        Assert.Equal(T0.AddHours(100), e.Window.Latest);
        Assert.Equal(Provenance.Estimated, e.Window.Provenance);
    }

    [Fact] // pass-3 nit: an inverted stage-3/stage-4 pair is not a bracket
    public void RipeWithAnInvertedStageThreeIsEstimated()
    {
        var e = StageModel.RipeWindow(Seen((100, 4), (110, 3)), Grow)!;
        Assert.Equal(Provenance.Estimated, e.Window.Provenance);
        Assert.Equal(T0.AddHours(100), e.Window.Latest);
    }

    [Fact]
    public void EmptyRecordGivesNull()
        => Assert.Null(StageModel.RipeWindow(Bed(), Grow));

    [Fact] // contradictory brackets: an honest estimate off the newest stage, flagged - never null
    public void ContradictionGivesAnEstimateAndAFlag()
    {
        var e = StageModel.RipeWindow(Seen((0, 3), (100, 1)), Grow)!;
        Assert.True(e.Contradiction);
        Assert.Equal(Provenance.Estimated, e.Window.Provenance);
        // newest = stage 1 at 100h: ripe in [100 + (1 - 1/3)*120, 100 + 120] = [180, 220]
        Assert.Equal(T0.AddHours(180), e.Window.Earliest);
        Assert.Equal(T0.AddHours(220), e.Window.Latest);
    }
}
