namespace BalambGarden.Engine.Derivations;

/// <summary>The run-log countdown's pace model: an EWMA (half old, half new) over
/// completed unit durations, seeded with the chain's own shape before the first boundary.
///
/// <para>A run-lifetime average sawtoothed the display (Drift, 2026-08-29): TendChain's
/// deliberately cheap first unit dragged the average under the true cadence for the whole
/// run, so every later unit overran its estimate, drained to 0:00, and jumped back up at
/// the boundary. Unit costs also genuinely differ - pot vs bed, tend vs harvest+replant -
/// and a pot sweep decides its verb per pot only when the menu opens, so the pace cannot
/// be predicted per unit, only tracked. Half-life of one unit forgets the opener and
/// follows a verb shift within about two boundaries.</para></summary>
public readonly record struct RunPace(double MsPerUnit)
{
    /// <summary>Fold one completed unit's duration into the pace.</summary>
    public RunPace Advance(double durationMs) => new((MsPerUnit + durationMs) / 2.0);

    /// <summary>Countdown from the last unit boundary: pace times what is left, less what
    /// the current unit has already spent. Pace stays frozen between boundaries (the
    /// POC/Scrooge ruling - a live-clock pace bills the current unit's wait to every
    /// future unit); the clamp keeps a slow unit at 0:00 instead of counting negative.</summary>
    public static TimeSpan? Eta(RunPace pace, int remaining, double spentMs)
    {
        if (remaining <= 0)
            return null;
        return TimeSpan.FromMilliseconds(Math.Max(0, (pace.MsPerUnit * remaining) - spentMs));
    }
}
