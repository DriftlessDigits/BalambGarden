using BalambGarden.Engine.Derivations;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The run-log countdown's pace model. The old run-lifetime average sawtoothed:
/// TendChain's deliberately cheap first unit (TendPace, not PostTendDelay) dragged the
/// average under the true cadence for the whole run, so every unit overran its estimate,
/// drained to 0:00, and jumped at the boundary. The EWMA forgets the opener on its own
/// and re-learns when a pot sweep shifts from tending into harvest-and-replant.</summary>
public class RunPaceTests
{
    [Fact]
    public void SeedIsTheFirstEstimate()
    {
        var pace = new RunPace(8000);
        Assert.Equal(8000, pace.MsPerUnit);
    }

    [Fact]
    public void AdvanceBlendsHalfOldHalfNew()
    {
        var pace = new RunPace(8000).Advance(2000);
        Assert.Equal(5000, pace.MsPerUnit);
    }

    [Fact]
    public void CheapOpenerIsForgottenWithinTwoUnits()
    {
        // Bed-shaped seed 8s, opener 1s (TendChain's first-unit discount), then true 8s units.
        var pace = new RunPace(8000).Advance(1000).Advance(8000).Advance(8000);
        // Old run-average after these three: (1+8+8)/3 ≈ 5.7s - 30% under forever.
        Assert.True(pace.MsPerUnit > 7000, $"pace {pace.MsPerUnit} should have recovered past 7s");
    }

    [Fact]
    public void VerbShiftIsLearnedWithinTwoUnits()
    {
        // A pot sweep: two quick tends, then the sweep hits ripe pots (harvest+replant ~12s).
        var pace = new RunPace(4000).Advance(3000).Advance(3000).Advance(12000).Advance(12000);
        Assert.True(pace.MsPerUnit > 9000, $"pace {pace.MsPerUnit} should track the harvest cadence");
    }

    [Fact]
    public void EtaIsPaceTimesRemainingLessSpent()
    {
        var eta = RunPace.Eta(new RunPace(8000), remaining: 3, spentMs: 2000);
        Assert.Equal(TimeSpan.FromMilliseconds(22000), eta);
    }

    [Fact]
    public void EtaClampsAtZeroNotNegative()
    {
        var eta = RunPace.Eta(new RunPace(1000), remaining: 1, spentMs: 5000);
        Assert.Equal(TimeSpan.Zero, eta);
    }

    [Fact]
    public void EtaIsNullWhenNothingRemains()
    {
        Assert.Null(RunPace.Eta(new RunPace(1000), remaining: 0, spentMs: 0));
    }
}
