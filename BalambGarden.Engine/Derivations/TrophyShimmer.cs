namespace BalambGarden.Engine.Derivations;

/// <summary>The trophy line's shimmer (rethink slice 12): a soft bright band that crosses the line
/// from left to right, slowly, then rests, over and over, while the Yields tab is drawn. It runs off
/// the clock it is handed, so opening the tab never starts a sweep and nothing is kept between frames
/// (never-calls-you: nothing on arrival).</summary>
public static class TrophyShimmer
{
    /// <summary>One sweep and its rest.</summary>
    public const double PeriodSeconds = 8.0;

    /// <summary>How long the band takes to cross the line.</summary>
    public const double SweepSeconds = 3.0;

    /// <summary>Half the band's width, as a share of the line's width.</summary>
    public const float HalfWidth = 0.12f;

    /// <summary>Where the band's centre sits across the line at <paramref name="seconds"/> on the
    /// clock (0 = the line's left edge, 1 = its right edge): from just off the left edge to just off
    /// the right during a sweep, null while it rests.</summary>
    public static float? Centre(double seconds)
    {
        var t = seconds % PeriodSeconds;
        if (t < 0)
            t += PeriodSeconds;
        if (t >= SweepSeconds)
            return null;
        return -HalfWidth + (1f + 2f * HalfWidth) * (float)(t / SweepSeconds);
    }
}
