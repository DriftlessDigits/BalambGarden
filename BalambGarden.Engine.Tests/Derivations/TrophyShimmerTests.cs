using BalambGarden.Engine.Derivations;
using Xunit;

namespace BalambGarden.Engine.Tests.Derivations;

/// <summary>The trophy line's shimmer (rethink slice 12): where its band sits on the clock it is
/// handed. A sweep of 3 seconds every 8; the band starts just off the left edge and leaves just off
/// the right.</summary>
public class TrophyShimmerTests
{
    private const float Half = TrophyShimmer.HalfWidth;

    [Fact] // a sweep starts with the band just off the line's left edge, is mid-line halfway through, and ends off the right
    public void TheBandCrossesTheLineLeftToRightInASweep()
    {
        Assert.Equal(-Half, TrophyShimmer.Centre(0.0)!.Value, 4);
        Assert.Equal(0.5f, TrophyShimmer.Centre(1.5)!.Value, 4);
        Assert.Equal(1f + Half, TrophyShimmer.Centre(2.9999999)!.Value, 4);
        Assert.True(TrophyShimmer.Centre(1.0) < TrophyShimmer.Centre(2.0));
    }

    [Fact] // after a sweep the band rests, for the rest of the period
    public void TheBandRestsBetweenSweeps()
    {
        Assert.Null(TrophyShimmer.Centre(3.0));
        Assert.Null(TrophyShimmer.Centre(5.5));
        Assert.Null(TrophyShimmer.Centre(7.99));
    }

    [Fact] // the sweep comes round again every period, on any clock, negative included
    public void TheSweepRepeatsEveryPeriod()
    {
        Assert.Equal(0.5f, TrophyShimmer.Centre(8.0 + 1.5)!.Value, 4);
        Assert.Equal(0.5f, TrophyShimmer.Centre(8000.0 + 1.5)!.Value, 4);
        Assert.Equal(0.5f, TrophyShimmer.Centre(-8.0 + 1.5)!.Value, 4);
        Assert.Null(TrophyShimmer.Centre(-1.0));
    }
}
